using Dapper;
using Microsoft.Data.SqlClient;

namespace zachtbeer.SqlSchemaHasher;

/// <summary>
/// Extracts schema metadata from a SQL Server database using efficient batch queries.
/// Uses sys.* catalog views instead of sp_help for better performance on large schemas.
/// All object names are schema-qualified to distinguish objects in different schemas.
///
/// Columns and constraints are keyed by catalog object_id throughout: a table's object_id and a
/// table type's type_table_object_id both appear as parent_object_id in the constraint views, so a
/// single pass over sys.key_constraints / sys.check_constraints / sys.default_constraints covers
/// both containers, and each row is distributed to whichever container owns that object_id.
/// </summary>
public sealed class SchemaExtractor
{
	/// <summary>
	/// Extracts complete schema metadata from the database using <see cref="SchemaHashOptions.Default"/>.
	/// </summary>
	/// <param name="connectionString">Connection string to the target database.</param>
	/// <returns>Schema metadata containing tables, stored procedures, and UDTs.</returns>
	public async Task<SchemaMetadata> ExtractSchemaAsync(string connectionString)
	{
		return await ExtractSchemaAsync(connectionString, SchemaHashOptions.Default);
	}

	/// <summary>
	/// Extracts complete schema metadata from the database with optional filtering.
	/// </summary>
	/// <param name="connectionString">Connection string to the target database.</param>
	/// <param name="options">Options for schema extraction (e.g., schema filter, object exclusions). Pass null for <see cref="SchemaHashOptions.Default"/>.</param>
	/// <returns>Schema metadata containing tables, stored procedures, and UDTs.</returns>
	public async Task<SchemaMetadata> ExtractSchemaAsync(string connectionString, SchemaHashOptions? options)
	{
		var resolvedOptions = options ?? SchemaHashOptions.Default;

		await using var connection = new SqlConnection(connectionString);
		await connection.OpenAsync();

		// Diagram exclusions compose additively with the user's ignore set, so a custom
		// ObjectNamesToIgnore doesn't silently drop them. The match set is (re)built with the configured
		// ObjectNameComparer so case behavior is owned here, not inherited from the caller's set.
		var comparer = resolvedOptions.ObjectNameComparer ?? StringComparer.OrdinalIgnoreCase;
		var ignoreEntries = resolvedOptions.IgnoreSysDiagramObjects
			? resolvedOptions.ObjectNamesToIgnore.Concat(SchemaHashOptions.SysDiagramObjectNames)
			: resolvedOptions.ObjectNamesToIgnore;
		var ignoreSet = new HashSet<string>(ignoreEntries, comparer);
		// An entry matches either the bare object name (any schema) or the schema-qualified name.
		bool IsIgnored(string schema, string name) => ignoreSet.Contains(name) || ignoreSet.Contains($"{schema}.{name}");
		var schemaFilter = resolvedOptions.SchemaFilter;

		// Shared, object_id-keyed extraction covering both tables and table types.
		var columnsByObjectId = await ExtractColumnsAsync(connection);
		var indexesByObjectId = await ExtractIndexesAsync(connection);
		var keyConstraintsByObjectId = await ExtractKeyConstraintsAsync(connection);
		var foreignKeysByObjectId = await ExtractForeignKeysAsync(connection);
		var checkConstraintsByObjectId = await ExtractCheckConstraintsAsync(connection);
		var defaultConstraintsByObjectId = await ExtractDefaultConstraintsAsync(connection);

		var tables = await ExtractTablesAsync(connection, IsIgnored, columnsByObjectId, indexesByObjectId, keyConstraintsByObjectId, foreignKeysByObjectId, checkConstraintsByObjectId, defaultConstraintsByObjectId);
		var storedProcedures = await ExtractStoredProceduresAsync(connection, IsIgnored);
		var userDefinedTableTypes = await ExtractUserDefinedTableTypesAsync(connection, IsIgnored, columnsByObjectId, keyConstraintsByObjectId, checkConstraintsByObjectId, defaultConstraintsByObjectId);

		// Apply schema filter if specified
		if (!string.IsNullOrWhiteSpace(schemaFilter))
		{
			tables = tables.Where(t => t.SchemaName.Equals(schemaFilter, StringComparison.OrdinalIgnoreCase)).ToList();
			storedProcedures = storedProcedures.Where(p => p.SchemaName.Equals(schemaFilter, StringComparison.OrdinalIgnoreCase)).ToList();
			userDefinedTableTypes = userDefinedTableTypes.Where(u => u.SchemaName.Equals(schemaFilter, StringComparison.OrdinalIgnoreCase)).ToList();
		}

		return new SchemaMetadata(tables, storedProcedures, userDefinedTableTypes);
	}

	// Shared column projection + joins, reused by the table and table-type column queries. Only the
	// driving object (sys.tables vs sys.table_types) differs. Computed-column formula, masking
	// function, and bound XML schema collection are joined so those attributes participate in the hash.
	private const string ColumnSelectList = @"
			c.object_id AS ObjectId,
			c.name AS ColumnName,
			CASE WHEN ty.is_user_defined = 1 THEN SCHEMA_NAME(ty.schema_id) + '.' + ty.name ELSE ty.name END AS DataType,
			c.max_length AS MaxLength,
			c.precision AS Precision,
			c.scale AS Scale,
			c.is_nullable AS IsNullable,
			c.is_computed AS IsComputed,
			cc.definition AS ComputedDefinition,
			CAST(ISNULL(cc.is_persisted, 0) AS bit) AS IsPersisted,
			c.collation_name AS CollationName,
			c.column_id AS ColumnId,
			c.is_sparse AS IsSparse,
			c.is_rowguidcol AS IsRowGuidCol,
			c.is_filestream AS IsFilestream,
			CAST(CASE WHEN mc.masking_function IS NOT NULL THEN 1 ELSE 0 END AS bit) AS IsMasked,
			mc.masking_function AS MaskingFunction,
			c.encryption_type_desc AS EncryptionTypeDesc,
			CASE WHEN xsc.xml_collection_id IS NOT NULL THEN SCHEMA_NAME(xsc.schema_id) + '.' + xsc.name ELSE NULL END AS XmlSchemaCollectionName,
			c.is_xml_document AS IsXmlDocument,
			c.generated_always_type_desc AS GeneratedAlwaysType,
			c.is_hidden AS IsHidden,
			c.is_ansi_padded AS IsAnsiPadded";

	private const string ColumnJoins = @"
			INNER JOIN sys.types ty ON c.user_type_id = ty.user_type_id
			LEFT JOIN sys.computed_columns cc ON c.object_id = cc.object_id AND c.column_id = cc.column_id
			LEFT JOIN sys.masked_columns mc ON c.object_id = mc.object_id AND c.column_id = mc.column_id
			LEFT JOIN sys.xml_schema_collections xsc ON c.xml_collection_id = xsc.xml_collection_id";

	private async Task<Dictionary<int, List<ColumnSchema>>> ExtractColumnsAsync(SqlConnection connection)
	{
		// Columns for tables and for table types share the same projection; the two queries differ only
		// in the driving catalog view and the object_id the columns hang off.
		string tableColumnsQuery = $@"
			SELECT {ColumnSelectList}
			FROM sys.tables t
			INNER JOIN sys.columns c ON c.object_id = t.object_id
			{ColumnJoins}
			WHERE t.type = 'U'";

		string typeColumnsQuery = $@"
			SELECT {ColumnSelectList}
			FROM sys.table_types tt
			INNER JOIN sys.columns c ON c.object_id = tt.type_table_object_id
			{ColumnJoins}";

		var tableColumns = await connection.QueryAsync<(int ObjectId, string ColumnName, string DataType, int MaxLength, int Precision, int Scale, bool IsNullable, bool IsComputed, string? ComputedDefinition, bool IsPersisted, string? CollationName, int ColumnId, bool IsSparse, bool IsRowGuidCol, bool IsFilestream, bool IsMasked, string? MaskingFunction, string? EncryptionTypeDesc, string? XmlSchemaCollectionName, bool IsXmlDocument, string? GeneratedAlwaysType, bool IsHidden, bool IsAnsiPadded)>(tableColumnsQuery);
		var typeColumns = await connection.QueryAsync<(int ObjectId, string ColumnName, string DataType, int MaxLength, int Precision, int Scale, bool IsNullable, bool IsComputed, string? ComputedDefinition, bool IsPersisted, string? CollationName, int ColumnId, bool IsSparse, bool IsRowGuidCol, bool IsFilestream, bool IsMasked, string? MaskingFunction, string? EncryptionTypeDesc, string? XmlSchemaCollectionName, bool IsXmlDocument, string? GeneratedAlwaysType, bool IsHidden, bool IsAnsiPadded)>(typeColumnsQuery);

		return tableColumns.Concat(typeColumns)
			.GroupBy(c => c.ObjectId)
			.ToDictionary(
				g => g.Key,
				g => g.Select(c => new ColumnSchema(c.ColumnName, c.DataType, c.MaxLength, c.Precision, c.Scale, c.IsNullable, c.IsComputed, c.ComputedDefinition, c.IsPersisted, c.CollationName, c.ColumnId, c.IsSparse, c.IsRowGuidCol, c.IsFilestream, c.IsMasked, c.MaskingFunction, c.EncryptionTypeDesc, c.XmlSchemaCollectionName, c.IsXmlDocument, c.GeneratedAlwaysType, c.IsHidden, c.IsAnsiPadded)).OrderBy(c => c.ColumnId).ToList());
	}

	private async Task<Dictionary<int, List<IndexSchema>>> ExtractIndexesAsync(SqlConnection connection)
	{
		// One row per index column; column aggregation is done in C# for SQL Server 2012 compatibility.
		// type_desc is kept raw (e.g. "CLUSTERED COLUMNSTORE") so the calculator can normalize placement
		// while preserving the rowstore/columnstore distinction. is_disabled and ignore_dup_key are
		// behavioral attributes of the index. fill_factor / is_padded / allow_row_locks / allow_page_locks
		// are the physical storage and locking options (WITH (FILLFACTOR=…, PAD_INDEX=…, ALLOW_*_LOCKS=…)).
		const string indexesQuery = @"
			SELECT
				t.object_id AS ObjectId,
				i.name AS IndexName,
				i.type_desc AS TypeDesc,
				i.is_unique AS IsUnique,
				i.is_unique_constraint AS IsUniqueConstraint,
				i.is_primary_key AS IsPrimaryKey,
				i.is_disabled AS IsDisabled,
				i.ignore_dup_key AS IgnoreDupKey,
				i.fill_factor AS [FillFactor],
				i.is_padded AS IsPadded,
				i.allow_row_locks AS AllowRowLocks,
				i.allow_page_locks AS AllowPageLocks,
				c.name AS ColumnName,
				ic.key_ordinal AS KeyOrdinal,
				ic.is_included_column AS IsIncluded,
				CAST(ISNULL(ic.is_descending_key, 0) AS bit) AS IsDescendingKey,
				i.filter_definition AS FilterDefinition
			FROM sys.tables t
			INNER JOIN sys.indexes i ON t.object_id = i.object_id
			INNER JOIN sys.index_columns ic ON i.object_id = ic.object_id AND i.index_id = ic.index_id
			INNER JOIN sys.columns c ON ic.object_id = c.object_id AND ic.column_id = c.column_id
			WHERE t.type = 'U' AND i.name IS NOT NULL AND i.is_hypothetical = 0
			ORDER BY t.object_id, i.name, ic.key_ordinal";

		var rows = await connection.QueryAsync<(int ObjectId, string IndexName, string TypeDesc, bool IsUnique, bool IsUniqueConstraint, bool IsPrimaryKey, bool IsDisabled, bool IgnoreDupKey, byte FillFactor, bool IsPadded, bool AllowRowLocks, bool AllowPageLocks, string ColumnName, int KeyOrdinal, bool IsIncluded, bool IsDescendingKey, string? FilterDefinition)>(indexesQuery);

		return rows
			.GroupBy(i => i.ObjectId)
			.ToDictionary(
				g => g.Key,
				// FilterDefinition, the physical options, and the index-level flags are constant per index, so they join the grouping key.
				g => g.GroupBy(i => (i.IndexName, i.TypeDesc, i.IsUnique, i.IsUniqueConstraint, i.IsPrimaryKey, i.IsDisabled, i.IgnoreDupKey, i.FillFactor, i.IsPadded, i.AllowRowLocks, i.AllowPageLocks, i.FilterDefinition))
					.Select(ig =>
					{
						// Key columns are ordered by key ordinal; columnstore key columns all have key_ordinal 0,
						// so ties are broken by name for determinism. Included columns are an unordered set kept
						// separate from key columns and sorted by name.
						var keyColumns = ig.Where(c => !c.IsIncluded)
							.OrderBy(c => c.KeyOrdinal).ThenBy(c => c.ColumnName, StringComparer.Ordinal)
							.Select(c => new IndexKeyColumn(c.ColumnName, c.IsDescendingKey)).ToList();
						var includedColumns = ig.Where(c => c.IsIncluded).Select(c => c.ColumnName).OrderBy(c => c, StringComparer.Ordinal).ToList();
						return new IndexSchema(ig.Key.IndexName, ig.Key.TypeDesc, ig.Key.IsUnique, ig.Key.IsUniqueConstraint, ig.Key.IsPrimaryKey, ig.Key.IsDisabled, ig.Key.IgnoreDupKey, keyColumns, includedColumns, ig.Key.FilterDefinition, ig.Key.FillFactor, ig.Key.IsPadded, ig.Key.AllowRowLocks, ig.Key.AllowPageLocks);
					}).ToList());
	}

	private async Task<Dictionary<int, List<KeyConstraintSchema>>> ExtractKeyConstraintsAsync(SqlConnection connection)
	{
		// PRIMARY KEY / UNIQUE constraints for both tables and table types (parent_object_id resolves to
		// a table's object_id or a table type's type_table_object_id). One row per key column.
		const string query = @"
			SELECT
				kc.parent_object_id AS ObjectId,
				CASE WHEN kc.type = 'PK' THEN 'PRIMARY KEY' ELSE 'UNIQUE' END AS ConstraintType,
				kc.name AS ConstraintName,
				kc.is_system_named AS IsSystemNamed,
				c.name AS ColumnName,
				ic.key_ordinal AS KeyOrdinal,
				CAST(ISNULL(ic.is_descending_key, 0) AS bit) AS IsDescendingKey
			FROM sys.key_constraints kc
			INNER JOIN sys.index_columns ic ON kc.parent_object_id = ic.object_id AND kc.unique_index_id = ic.index_id
			INNER JOIN sys.columns c ON ic.object_id = c.object_id AND ic.column_id = c.column_id
			WHERE kc.type IN ('PK', 'UQ')";

		var rows = await connection.QueryAsync<(int ObjectId, string ConstraintType, string ConstraintName, bool IsSystemNamed, string ColumnName, int KeyOrdinal, bool IsDescendingKey)>(query);

		return rows
			.GroupBy(r => r.ObjectId)
			.ToDictionary(
				g => g.Key,
				g => g.GroupBy(r => (r.ConstraintType, r.ConstraintName, r.IsSystemNamed))
					.Select(cg => new KeyConstraintSchema(cg.Key.ConstraintType, cg.Key.ConstraintName, cg.Key.IsSystemNamed, cg.OrderBy(c => c.KeyOrdinal).ThenBy(c => c.ColumnName, StringComparer.Ordinal).Select(c => new IndexKeyColumn(c.ColumnName, c.IsDescendingKey)).ToList()))
					.ToList());
	}

	private async Task<Dictionary<int, List<ForeignKeyConstraintSchema>>> ExtractForeignKeysAsync(SqlConnection connection)
	{
		// FOREIGN KEY constraints (tables only — table types cannot declare foreign keys). One row per
		// referencing→referenced column pair. Referential actions come from the *_desc columns directly.
		const string query = @"
			SELECT
				fk.parent_object_id AS ObjectId,
				fk.name AS ConstraintName,
				COL_NAME(fkc.parent_object_id, fkc.parent_column_id) AS ParentColumn,
				SCHEMA_NAME(rt.schema_id) AS ReferencedSchema,
				rt.name AS ReferencedTable,
				COL_NAME(fkc.referenced_object_id, fkc.referenced_column_id) AS ReferencedColumn,
				fkc.constraint_column_id AS ColumnId,
				fk.delete_referential_action_desc AS DeleteAction,
				fk.update_referential_action_desc AS UpdateAction,
				fk.is_disabled AS IsDisabled,
				fk.is_not_trusted AS IsNotTrusted,
				fk.is_not_for_replication AS IsNotForReplication,
				fk.is_system_named AS IsSystemNamed
			FROM sys.foreign_keys fk
			INNER JOIN sys.foreign_key_columns fkc ON fk.object_id = fkc.constraint_object_id
			INNER JOIN sys.tables rt ON fk.referenced_object_id = rt.object_id";

		var rows = await connection.QueryAsync<(int ObjectId, string ConstraintName, string ParentColumn, string ReferencedSchema, string ReferencedTable, string ReferencedColumn, int ColumnId, string DeleteAction, string UpdateAction, bool IsDisabled, bool IsNotTrusted, bool IsNotForReplication, bool IsSystemNamed)>(query);

		return rows
			.GroupBy(r => r.ObjectId)
			.ToDictionary(
				g => g.Key,
				g => g.GroupBy(r => (r.ConstraintName, r.DeleteAction, r.UpdateAction, r.IsDisabled, r.IsNotTrusted, r.IsNotForReplication, r.IsSystemNamed))
					.Select(fg => new ForeignKeyConstraintSchema(fg.Key.ConstraintName, fg.First().ReferencedSchema, fg.First().ReferencedTable, fg.OrderBy(x => x.ColumnId).Select(x => new ForeignKeyColumnPair(x.ParentColumn, x.ReferencedColumn)).ToList(), fg.Key.DeleteAction, fg.Key.UpdateAction, fg.Key.IsDisabled, fg.Key.IsNotTrusted, fg.Key.IsNotForReplication, fg.Key.IsSystemNamed))
					.ToList());
	}

	private async Task<Dictionary<int, List<CheckConstraintSchema>>> ExtractCheckConstraintsAsync(SqlConnection connection)
	{
		// CHECK constraints for both tables and table types. One row per constraint.
		const string query = @"
			SELECT
				cc.parent_object_id AS ObjectId,
				cc.name AS ConstraintName,
				cc.definition AS Definition,
				cc.is_disabled AS IsDisabled,
				cc.is_not_trusted AS IsNotTrusted,
				cc.is_not_for_replication AS IsNotForReplication,
				cc.is_system_named AS IsSystemNamed
			FROM sys.check_constraints cc";

		var rows = await connection.QueryAsync<(int ObjectId, string ConstraintName, string Definition, bool IsDisabled, bool IsNotTrusted, bool IsNotForReplication, bool IsSystemNamed)>(query);

		return rows
			.GroupBy(r => r.ObjectId)
			.ToDictionary(
				g => g.Key,
				g => g.Select(r => new CheckConstraintSchema(r.ConstraintName, r.Definition, r.IsDisabled, r.IsNotTrusted, r.IsNotForReplication, r.IsSystemNamed)).ToList());
	}

	private async Task<Dictionary<int, List<DefaultConstraintSchema>>> ExtractDefaultConstraintsAsync(SqlConnection connection)
	{
		// DEFAULT constraints for both tables and table types. One row per constraint.
		const string query = @"
			SELECT
				dc.parent_object_id AS ObjectId,
				dc.name AS ConstraintName,
				COL_NAME(dc.parent_object_id, dc.parent_column_id) AS ColumnName,
				dc.definition AS Definition,
				dc.is_system_named AS IsSystemNamed
			FROM sys.default_constraints dc";

		var rows = await connection.QueryAsync<(int ObjectId, string ConstraintName, string ColumnName, string Definition, bool IsSystemNamed)>(query);

		return rows
			.GroupBy(r => r.ObjectId)
			.ToDictionary(
				g => g.Key,
				g => g.Select(r => new DefaultConstraintSchema(r.ConstraintName, r.ColumnName, r.Definition, r.IsSystemNamed)).ToList());
	}

	private async Task<List<TableSchema>> ExtractTablesAsync(SqlConnection connection, Func<string, string, bool> isIgnored, Dictionary<int, List<ColumnSchema>> columnsByObjectId, Dictionary<int, List<IndexSchema>> indexesByObjectId, Dictionary<int, List<KeyConstraintSchema>> keyConstraintsByObjectId, Dictionary<int, List<ForeignKeyConstraintSchema>> foreignKeysByObjectId, Dictionary<int, List<CheckConstraintSchema>> checkConstraintsByObjectId, Dictionary<int, List<DefaultConstraintSchema>> defaultConstraintsByObjectId)
	{
		// history_retention_period / history_retention_period_unit_desc were added in SQL Server 2017
		// (major 14+); Azure SQL (EngineEdition >= 5) has them regardless of the major version it reports.
		// On older servers the columns don't exist, so select NULL literals instead. INFINITE retention
		// (the default) is normalized to NULL so a 2016 server and a 2017 server hash a default temporal
		// table identically.
		var majorVersion = int.Parse(connection.ServerVersion.Split('.')[0]);
		var engineEdition = await connection.ExecuteScalarAsync<int>("SELECT CAST(SERVERPROPERTY('EngineEdition') AS INT)");
		var retentionSelect = SupportsTemporalRetention(majorVersion, engineEdition)
			? @"NULLIF(t.history_retention_period, -1) AS HistoryRetentionPeriod,
				CASE WHEN t.history_retention_period = -1 THEN NULL ELSE t.history_retention_period_unit_desc END AS HistoryRetentionPeriodUnit"
			: @"CAST(NULL AS INT) AS HistoryRetentionPeriod,
				CAST(NULL AS NVARCHAR(60)) AS HistoryRetentionPeriodUnit";

		// Tables with schema names and their identity column (a table has at most one). Joins
		// sys.identity_columns by object_id rather than resolving names through OBJECT_ID, which breaks
		// for table names containing dots. For a system-versioned table, history_table_id links to its
		// history table; it is resolved to a schema-qualified name here and normalized by the calculator
		// (an unnamed history table gets an object_id-derived name that is not deterministic across DBs).
		string tablesQuery = $@"
			SELECT
				t.object_id AS ObjectId,
				SCHEMA_NAME(t.schema_id) AS SchemaName,
				t.name AS TableName,
				idc.name AS IdentityColumn,
				CONVERT(NVARCHAR(64), idc.seed_value) AS IdentitySeed,
				CONVERT(NVARCHAR(64), idc.increment_value) AS IdentityIncrement,
				CAST(ISNULL(idc.is_not_for_replication, 0) AS bit) AS IdentityNotForReplication,
				t.temporal_type_desc AS TemporalType,
				t.is_memory_optimized AS IsMemoryOptimized,
				t.durability_desc AS DurabilityDesc,
				CASE WHEN t.history_table_id IS NOT NULL THEN SCHEMA_NAME(ht.schema_id) + '.' + ht.name ELSE NULL END AS HistoryTableName,
				{retentionSelect}
			FROM sys.tables t
			LEFT JOIN sys.identity_columns idc ON idc.object_id = t.object_id
			LEFT JOIN sys.tables ht ON t.history_table_id = ht.object_id
			WHERE t.type = 'U'
			ORDER BY SchemaName, t.name";

		var tableInfos = (await connection.QueryAsync<(int ObjectId, string SchemaName, string TableName, string? IdentityColumn, string? IdentitySeed, string? IdentityIncrement, bool IdentityNotForReplication, string? TemporalType, bool IsMemoryOptimized, string? DurabilityDesc, string? HistoryTableName, int? HistoryRetentionPeriod, string? HistoryRetentionPeriodUnit)>(tablesQuery))
			.Where(t => !isIgnored(t.SchemaName, t.TableName))
			.ToList();

		var tables = new List<TableSchema>(tableInfos.Count);
		foreach (var info in tableInfos)
		{
			tables.Add(new TableSchema(
				info.SchemaName,
				info.TableName,
				columnsByObjectId.GetValueOrDefault(info.ObjectId, new List<ColumnSchema>()),
				indexesByObjectId.GetValueOrDefault(info.ObjectId, new List<IndexSchema>()),
				keyConstraintsByObjectId.GetValueOrDefault(info.ObjectId, new List<KeyConstraintSchema>()),
				foreignKeysByObjectId.GetValueOrDefault(info.ObjectId, new List<ForeignKeyConstraintSchema>()),
				checkConstraintsByObjectId.GetValueOrDefault(info.ObjectId, new List<CheckConstraintSchema>()),
				defaultConstraintsByObjectId.GetValueOrDefault(info.ObjectId, new List<DefaultConstraintSchema>()),
				info.IdentityColumn,
				info.IdentitySeed,
				info.IdentityIncrement,
				info.IdentityNotForReplication,
				info.TemporalType,
				info.IsMemoryOptimized,
				info.DurabilityDesc,
				info.HistoryTableName,
				info.HistoryRetentionPeriod,
				info.HistoryRetentionPeriodUnit));
		}

		return tables.OrderBy(t => t.SchemaName, StringComparer.Ordinal).ThenBy(t => t.Name, StringComparer.Ordinal).ToList();
	}

	private async Task<List<StoredProcedureSchema>> ExtractStoredProceduresAsync(SqlConnection connection, Func<string, string, bool> isIgnored)
	{
		// SQL Server 2016+ (major version 13+) removed the 8000-byte input limit on HASHBYTES,
		// as did all Azure SQL offerings (EngineEdition >= 5) — Azure SQL Database reports major
		// version 12, so the version number alone would wrongly route it to the fallback.
		// On older on-prem versions, fall back to CHECKSUM which has no size limit but weaker collision
		// resistance. That isn't critical here since this value is mixed into the overall SHA256 hash.
		var majorVersion = int.Parse(connection.ServerVersion.Split('.')[0]);
		var engineEdition = await connection.ExecuteScalarAsync<int>("SELECT CAST(SERVERPROPERTY('EngineEdition') AS INT)");
		var definitionHashExpr = SupportsUnlimitedHashBytes(majorVersion, engineEdition)
			? "CONVERT(VARCHAR(64), HASHBYTES('SHA2_256', ISNULL(m.definition, '')), 2)"
			: "CONVERT(VARCHAR(40), CHECKSUM(ISNULL(m.definition, '')))";

		// A WITH ENCRYPTION procedure has a row in sys.sql_modules but a NULL definition, so its body
		// is unavailable to hash. IsEncrypted lets the calculator give it a distinct sentinel instead
		// of collapsing to the hash of the empty string (which would also collide with an empty body).
		// Two different encrypted procedures with the same signature still collide — that is inherent,
		// since the server exposes nothing that reflects an encrypted body.
		string allProcsQuery = $@"
			SELECT
				SCHEMA_NAME(p.schema_id) AS SchemaName,
				p.name AS ProcName,
				{definitionHashExpr} AS DefinitionHash,
				CAST(CASE WHEN m.object_id IS NOT NULL AND m.definition IS NULL THEN 1 ELSE 0 END AS bit) AS IsEncrypted,
					CAST(ISNULL(m.uses_ansi_nulls, 1) AS bit) AS UsesAnsiNulls,
					CAST(ISNULL(m.uses_quoted_identifier, 1) AS bit) AS UsesQuotedIdentifier
			FROM sys.procedures p
			LEFT JOIN sys.sql_modules m ON p.object_id = m.object_id
			WHERE p.type = 'P'
			ORDER BY SchemaName, p.name";

		var allProcs = (await connection.QueryAsync<(string SchemaName, string ProcName, string? DefinitionHash, bool IsEncrypted, bool UsesAnsiNulls, bool UsesQuotedIdentifier)>(allProcsQuery))
			.Where(p => !isIgnored(p.SchemaName, p.ProcName))
			.ToList();

		// Get parameters for procedures that have them (with schema)
		const string paramsQuery = @"
			SELECT
				SCHEMA_NAME(p.schema_id) AS SchemaName,
				p.name AS ProcName,
				pa.name AS ParamName,
				CASE WHEN t.is_user_defined = 1 THEN SCHEMA_NAME(t.schema_id) + '.' + t.name ELSE t.name END AS TypeName,
				pa.max_length AS MaxLength,
				pa.precision AS Precision,
				pa.scale AS Scale,
				pa.is_nullable AS IsNullable,
				pa.is_output AS IsOutput,
				pa.is_readonly AS IsReadonly,
				CASE WHEN xsc.xml_collection_id IS NOT NULL THEN SCHEMA_NAME(xsc.schema_id) + '.' + xsc.name ELSE NULL END AS XmlSchemaCollectionName,
				pa.is_xml_document AS IsXmlDocument
			FROM sys.procedures p
			INNER JOIN sys.parameters pa ON p.object_id = pa.object_id
			INNER JOIN sys.types t ON pa.user_type_id = t.user_type_id
			LEFT JOIN sys.xml_schema_collections xsc ON pa.xml_collection_id = xsc.xml_collection_id
			WHERE p.type = 'P'
			ORDER BY SchemaName, p.name, pa.parameter_id";

		var procsAndParams = await connection.QueryAsync<(string SchemaName, string ProcName, string ParamName, string TypeName, int MaxLength, int Precision, int Scale, bool IsNullable, bool IsOutput, bool IsReadonly, string? XmlSchemaCollectionName, bool IsXmlDocument)>(paramsQuery);

		var paramsByProc = procsAndParams
			.Where(p => !isIgnored(p.SchemaName, p.ProcName))
			.GroupBy(p => (p.SchemaName, p.ProcName))
			.ToDictionary(
				g => g.Key,
				g => g.Select(p => new ParameterSchema(p.ParamName.TrimStart('@'), p.TypeName, p.MaxLength, p.Precision, p.Scale, p.IsNullable, p.IsOutput, p.IsReadonly, p.XmlSchemaCollectionName, p.IsXmlDocument)).ToList()
			);

		var procedures = allProcs
			.Select(proc => new StoredProcedureSchema(
				proc.SchemaName,
				proc.ProcName,
				paramsByProc.GetValueOrDefault((proc.SchemaName, proc.ProcName), new List<ParameterSchema>()),
				proc.IsEncrypted ? EncryptedDefinitionSentinel : (proc.DefinitionHash ?? ComputeEmptyDefinitionHash()),
					proc.UsesAnsiNulls,
					proc.UsesQuotedIdentifier
			))
			.OrderBy(p => p.SchemaName, StringComparer.Ordinal)
			.ThenBy(p => p.Name, StringComparer.Ordinal)
			.ToList();

		return procedures;
	}

	private async Task<List<UserDefinedTableTypeSchema>> ExtractUserDefinedTableTypesAsync(SqlConnection connection, Func<string, string, bool> isIgnored, Dictionary<int, List<ColumnSchema>> columnsByObjectId, Dictionary<int, List<KeyConstraintSchema>> keyConstraintsByObjectId, Dictionary<int, List<CheckConstraintSchema>> checkConstraintsByObjectId, Dictionary<int, List<DefaultConstraintSchema>> defaultConstraintsByObjectId)
	{
		// Table types, keyed by their type_table_object_id so columns and constraints (extracted in the
		// shared object_id-keyed pass) attach to the right type. Identity joins by that same object_id.
		const string typesQuery = @"
			SELECT
				tt.type_table_object_id AS ObjectId,
				SCHEMA_NAME(tt.schema_id) AS SchemaName,
				tt.name AS TableTypeName,
				idc.name AS IdentityColumn,
				CONVERT(NVARCHAR(64), idc.seed_value) AS IdentitySeed,
				CONVERT(NVARCHAR(64), idc.increment_value) AS IdentityIncrement,
				CAST(ISNULL(idc.is_not_for_replication, 0) AS bit) AS IdentityNotForReplication,
				tt.is_memory_optimized AS IsMemoryOptimized
			FROM sys.table_types tt
			LEFT JOIN sys.identity_columns idc ON idc.object_id = tt.type_table_object_id
			ORDER BY SchemaName, tt.name";

		var typeInfos = (await connection.QueryAsync<(int ObjectId, string SchemaName, string TableTypeName, string? IdentityColumn, string? IdentitySeed, string? IdentityIncrement, bool IdentityNotForReplication, bool IsMemoryOptimized)>(typesQuery))
			.Where(u => !isIgnored(u.SchemaName, u.TableTypeName))
			.ToList();

		var udts = new List<UserDefinedTableTypeSchema>(typeInfos.Count);
		foreach (var info in typeInfos)
		{
			udts.Add(new UserDefinedTableTypeSchema(
				info.SchemaName,
				info.TableTypeName,
				columnsByObjectId.GetValueOrDefault(info.ObjectId, new List<ColumnSchema>()),
				keyConstraintsByObjectId.GetValueOrDefault(info.ObjectId, new List<KeyConstraintSchema>()),
				checkConstraintsByObjectId.GetValueOrDefault(info.ObjectId, new List<CheckConstraintSchema>()),
				defaultConstraintsByObjectId.GetValueOrDefault(info.ObjectId, new List<DefaultConstraintSchema>()),
				info.IdentityColumn,
				info.IdentitySeed,
				info.IdentityIncrement,
				info.IdentityNotForReplication,
				info.IsMemoryOptimized));
		}

		return udts.OrderBy(u => u.SchemaName, StringComparer.Ordinal).ThenBy(u => u.Name, StringComparer.Ordinal).ToList();
	}

	/// <summary>
	/// Returns a fallback hash for an empty/null procedure definition, consistent with SQL Server's CHECKSUM('').
	/// </summary>
	private static string ComputeEmptyDefinitionHash() => "0";

	/// <summary>
	/// Distinct sentinel used as the definition hash of a WITH ENCRYPTION procedure, whose body is
	/// unavailable. Keeps encrypted procedures from colliding with an ordinary empty-body procedure.
	/// </summary>
	private const string EncryptedDefinitionSentinel = "<encrypted>";

	/// <summary>
	/// Determines whether the server supports HASHBYTES on inputs over 8000 bytes.
	/// True for SQL Server 2016+ (major version 13+) and for all Azure SQL offerings
	/// (EngineEdition 5 = Azure SQL Database, 8 = Managed Instance, etc.), which support
	/// it regardless of the major version they report.
	/// </summary>
	internal static bool SupportsUnlimitedHashBytes(int majorVersion, int engineEdition) => majorVersion >= 13 || engineEdition >= 5;

	/// <summary>
	/// Determines whether the server exposes temporal history retention columns
	/// (<c>sys.tables.history_retention_period</c> / <c>history_retention_period_unit_desc</c>),
	/// introduced in SQL Server 2017 (major version 14+) and present in all Azure SQL offerings
	/// (EngineEdition >= 5) regardless of the major version they report.
	/// </summary>
	internal static bool SupportsTemporalRetention(int majorVersion, int engineEdition) => majorVersion >= 14 || engineEdition >= 5;
}
