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
internal sealed class SchemaExtractor
{
	/// <summary>
	/// Extracts complete schema metadata from the database using <see cref="SchemaHashOptions.Default"/>.
	/// </summary>
	/// <param name="connectionString">Connection string to the target database.</param>
	/// <param name="cancellationToken">Token to cancel the extraction.</param>
	/// <returns>Schema metadata containing tables, stored procedures, UDTs, views, functions, triggers, sequences, and synonyms.</returns>
	public async Task<SchemaMetadata> ExtractSchemaAsync(string connectionString, CancellationToken cancellationToken = default)
	{
		return await ExtractSchemaAsync(connectionString, SchemaHashOptions.Default, cancellationToken);
	}

	/// <summary>
	/// Extracts complete schema metadata from the database with optional filtering.
	/// </summary>
	/// <param name="connectionString">Connection string to the target database.</param>
	/// <param name="options">Options for schema extraction (e.g., schema filter, object exclusions). Pass null for <see cref="SchemaHashOptions.Default"/>.</param>
	/// <param name="cancellationToken">Token to cancel the extraction.</param>
	/// <returns>Schema metadata containing tables, stored procedures, UDTs, views, functions, triggers, sequences, and synonyms.</returns>
	public async Task<SchemaMetadata> ExtractSchemaAsync(string connectionString, SchemaHashOptions? options, CancellationToken cancellationToken = default)
	{
		var resolvedOptions = options ?? SchemaHashOptions.Default;

		await using var connection = new SqlConnection(connectionString);
		await connection.OpenAsync(cancellationToken);

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

		// Server capability probes, hoisted here so they run once regardless of how many module kinds
		// (procs, views, functions, triggers) need HASHBYTES routing or temporal-retention support.
		var majorVersion = int.Parse(connection.ServerVersion.Split('.')[0]);
		var engineEdition = await connection.ExecuteScalarAsync<int>(new CommandDefinition("SELECT CAST(SERVERPROPERTY('EngineEdition') AS INT)", cancellationToken: cancellationToken));
		var definitionHashExpr = SupportsUnlimitedHashBytes(majorVersion, engineEdition)
			? "CONVERT(VARCHAR(64), HASHBYTES('SHA2_256', ISNULL(m.definition, '')), 2)"
			: "CONVERT(VARCHAR(40), CHECKSUM(ISNULL(m.definition, '')))";

		// Shared, object_id-keyed extraction covering both tables and table types.
		var columnsByObjectId = await ExtractColumnsAsync(connection, cancellationToken);
		var indexesByObjectId = await ExtractIndexesAsync(connection, cancellationToken);
		var keyConstraintsByObjectId = await ExtractKeyConstraintsAsync(connection, cancellationToken);
		var foreignKeysByObjectId = await ExtractForeignKeysAsync(connection, cancellationToken);
		var checkConstraintsByObjectId = await ExtractCheckConstraintsAsync(connection, cancellationToken);
		var defaultConstraintsByObjectId = await ExtractDefaultConstraintsAsync(connection, cancellationToken);
		// Procs and functions share the schema-scoped object namespace, so a single (SchemaName, Name)
		// keyed dictionary covers both without collision risk.
		var paramsByOwner = await ExtractParametersAsync(connection, cancellationToken);

		var tables = await ExtractTablesAsync(connection, IsIgnored, majorVersion, engineEdition, columnsByObjectId, indexesByObjectId, keyConstraintsByObjectId, foreignKeysByObjectId, checkConstraintsByObjectId, defaultConstraintsByObjectId, cancellationToken);
		var storedProcedures = await ExtractStoredProceduresAsync(connection, IsIgnored, definitionHashExpr, paramsByOwner, cancellationToken);
		var userDefinedTableTypes = await ExtractUserDefinedTableTypesAsync(connection, IsIgnored, columnsByObjectId, keyConstraintsByObjectId, checkConstraintsByObjectId, defaultConstraintsByObjectId, cancellationToken);
		var views = await ExtractViewsAsync(connection, IsIgnored, definitionHashExpr, indexesByObjectId, cancellationToken);
		var functions = await ExtractFunctionsAsync(connection, IsIgnored, definitionHashExpr, paramsByOwner, cancellationToken);
		var triggers = await ExtractTriggersAsync(connection, IsIgnored, definitionHashExpr, cancellationToken);
		var sequences = await ExtractSequencesAsync(connection, IsIgnored, cancellationToken);
		var synonyms = await ExtractSynonymsAsync(connection, IsIgnored, cancellationToken);
		var extendedProperties = resolvedOptions.IgnoreExtendedProperties
			? new List<ExtendedPropertySchema>()
			: await ExtractExtendedPropertiesAsync(connection, IsIgnored, cancellationToken);

		// Apply schema filter if specified
		if (!string.IsNullOrWhiteSpace(schemaFilter))
		{
			tables = tables.Where(t => t.SchemaName.Equals(schemaFilter, StringComparison.OrdinalIgnoreCase)).ToList();
			storedProcedures = storedProcedures.Where(p => p.SchemaName.Equals(schemaFilter, StringComparison.OrdinalIgnoreCase)).ToList();
			userDefinedTableTypes = userDefinedTableTypes.Where(u => u.SchemaName.Equals(schemaFilter, StringComparison.OrdinalIgnoreCase)).ToList();
			views = views.Where(v => v.SchemaName.Equals(schemaFilter, StringComparison.OrdinalIgnoreCase)).ToList();
			functions = functions.Where(f => f.SchemaName.Equals(schemaFilter, StringComparison.OrdinalIgnoreCase)).ToList();
			triggers = triggers.Where(t => t.SchemaName.Equals(schemaFilter, StringComparison.OrdinalIgnoreCase)).ToList();
			sequences = sequences.Where(s => s.SchemaName.Equals(schemaFilter, StringComparison.OrdinalIgnoreCase)).ToList();
			synonyms = synonyms.Where(s => s.SchemaName.Equals(schemaFilter, StringComparison.OrdinalIgnoreCase)).ToList();
			// A database-scoped extended property has no schema, so it falls outside any schema filter.
			extendedProperties = extendedProperties.Where(p => p.SchemaName is not null && p.SchemaName.Equals(schemaFilter, StringComparison.OrdinalIgnoreCase)).ToList();
		}

		return new SchemaMetadata(tables, storedProcedures, userDefinedTableTypes, views, functions, triggers, sequences, synonyms, extendedProperties);
	}

	// Shared column projection + joins, reused by the table and table-type column queries. Only the
	// driving object (sys.tables vs sys.table_types) differs. Computed-column formula, masking
	// function, and bound XML schema collection are joined so those attributes participate in the hash.
	// bt resolves an alias scalar type's underlying system base type (sys.types rows for system types have
	// user_type_id = system_type_id), so BaseTypeName/AliasMaxLength/AliasPrecision/AliasScale/AliasIsNullable
	// are non-null only for alias-typed columns (bug #8: the alias's underlying definition was previously
	// unobservable, so dropping and recreating the alias with a different base type left the hash unchanged).
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
			c.is_ansi_padded AS IsAnsiPadded,
			bt.name AS BaseTypeName,
			ty.max_length AS AliasMaxLength,
			ty.precision AS AliasPrecision,
			ty.scale AS AliasScale,
			ty.is_nullable AS AliasIsNullable";

	private const string ColumnJoins = @"
			INNER JOIN sys.types ty ON c.user_type_id = ty.user_type_id
			LEFT JOIN sys.types bt ON ty.is_user_defined = 1 AND ty.is_table_type = 0 AND ty.system_type_id = bt.user_type_id
			LEFT JOIN sys.computed_columns cc ON c.object_id = cc.object_id AND c.column_id = cc.column_id
			LEFT JOIN sys.masked_columns mc ON c.object_id = mc.object_id AND c.column_id = mc.column_id
			LEFT JOIN sys.xml_schema_collections xsc ON c.xml_collection_id = xsc.xml_collection_id";

	private async Task<Dictionary<int, List<ColumnSchema>>> ExtractColumnsAsync(SqlConnection connection, CancellationToken cancellationToken)
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

		var tableColumns = await connection.QueryAsync<(int ObjectId, string ColumnName, string DataType, int MaxLength, int Precision, int Scale, bool IsNullable, bool IsComputed, string? ComputedDefinition, bool IsPersisted, string? CollationName, int ColumnId, bool IsSparse, bool IsRowGuidCol, bool IsFilestream, bool IsMasked, string? MaskingFunction, string? EncryptionTypeDesc, string? XmlSchemaCollectionName, bool IsXmlDocument, string? GeneratedAlwaysType, bool IsHidden, bool IsAnsiPadded, string? BaseTypeName, int AliasMaxLength, byte AliasPrecision, byte AliasScale, bool AliasIsNullable)>(new CommandDefinition(tableColumnsQuery, cancellationToken: cancellationToken));
		var typeColumns = await connection.QueryAsync<(int ObjectId, string ColumnName, string DataType, int MaxLength, int Precision, int Scale, bool IsNullable, bool IsComputed, string? ComputedDefinition, bool IsPersisted, string? CollationName, int ColumnId, bool IsSparse, bool IsRowGuidCol, bool IsFilestream, bool IsMasked, string? MaskingFunction, string? EncryptionTypeDesc, string? XmlSchemaCollectionName, bool IsXmlDocument, string? GeneratedAlwaysType, bool IsHidden, bool IsAnsiPadded, string? BaseTypeName, int AliasMaxLength, byte AliasPrecision, byte AliasScale, bool AliasIsNullable)>(new CommandDefinition(typeColumnsQuery, cancellationToken: cancellationToken));

		return tableColumns.Concat(typeColumns)
			.GroupBy(c => c.ObjectId)
			.ToDictionary(
				g => g.Key,
				g => g.Select(c => new ColumnSchema(c.ColumnName, EffectiveDataType(c.DataType, c.BaseTypeName, c.AliasMaxLength, c.AliasPrecision, c.AliasScale, c.AliasIsNullable), c.MaxLength, c.Precision, c.Scale, c.IsNullable, c.IsComputed, c.ComputedDefinition, c.IsPersisted, c.CollationName, c.ColumnId, c.IsSparse, c.IsRowGuidCol, c.IsFilestream, c.IsMasked, c.MaskingFunction, c.EncryptionTypeDesc, c.XmlSchemaCollectionName, c.IsXmlDocument, c.GeneratedAlwaysType, c.IsHidden, c.IsAnsiPadded)).OrderBy(c => c.ColumnId).ToList());
	}

	private async Task<Dictionary<int, List<IndexSchema>>> ExtractIndexesAsync(SqlConnection connection, CancellationToken cancellationToken)
	{
		// One row per index column; column aggregation is done in C# for SQL Server 2012 compatibility.
		// type_desc is kept raw (e.g. "CLUSTERED COLUMNSTORE") so the calculator can normalize placement
		// while preserving the rowstore/columnstore distinction. is_disabled and ignore_dup_key are
		// behavioral attributes of the index. fill_factor / is_padded / allow_row_locks / allow_page_locks
		// are the physical storage and locking options (WITH (FILLFACTOR=…, PAD_INDEX=…, ALLOW_*_LOCKS=…)).
		// Driven from sys.objects with type IN ('U', 'V') so the same result dictionary (keyed by
		// object_id) distributes indexes to both tables and indexed views, mirroring how table-type
		// columns distribute to table types.
		const string indexesQuery = @"
			SELECT
				o.object_id AS ObjectId,
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
			FROM sys.objects o
			INNER JOIN sys.indexes i ON o.object_id = i.object_id
			INNER JOIN sys.index_columns ic ON i.object_id = ic.object_id AND i.index_id = ic.index_id
			INNER JOIN sys.columns c ON ic.object_id = c.object_id AND ic.column_id = c.column_id
			WHERE o.type IN ('U', 'V') AND i.name IS NOT NULL AND i.is_hypothetical = 0
			ORDER BY o.object_id, i.name, ic.key_ordinal";

		var rows = await connection.QueryAsync<(int ObjectId, string IndexName, string TypeDesc, bool IsUnique, bool IsUniqueConstraint, bool IsPrimaryKey, bool IsDisabled, bool IgnoreDupKey, byte FillFactor, bool IsPadded, bool AllowRowLocks, bool AllowPageLocks, string ColumnName, int KeyOrdinal, bool IsIncluded, bool IsDescendingKey, string? FilterDefinition)>(new CommandDefinition(indexesQuery, cancellationToken: cancellationToken));

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

	private async Task<Dictionary<int, List<KeyConstraintSchema>>> ExtractKeyConstraintsAsync(SqlConnection connection, CancellationToken cancellationToken)
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

		var rows = await connection.QueryAsync<(int ObjectId, string ConstraintType, string ConstraintName, bool IsSystemNamed, string ColumnName, int KeyOrdinal, bool IsDescendingKey)>(new CommandDefinition(query, cancellationToken: cancellationToken));

		return rows
			.GroupBy(r => r.ObjectId)
			.ToDictionary(
				g => g.Key,
				g => g.GroupBy(r => (r.ConstraintType, r.ConstraintName, r.IsSystemNamed))
					.Select(cg => new KeyConstraintSchema(cg.Key.ConstraintType, cg.Key.ConstraintName, cg.Key.IsSystemNamed, cg.OrderBy(c => c.KeyOrdinal).ThenBy(c => c.ColumnName, StringComparer.Ordinal).Select(c => new IndexKeyColumn(c.ColumnName, c.IsDescendingKey)).ToList()))
					.ToList());
	}

	private async Task<Dictionary<int, List<ForeignKeyConstraintSchema>>> ExtractForeignKeysAsync(SqlConnection connection, CancellationToken cancellationToken)
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

		var rows = await connection.QueryAsync<(int ObjectId, string ConstraintName, string ParentColumn, string ReferencedSchema, string ReferencedTable, string ReferencedColumn, int ColumnId, string DeleteAction, string UpdateAction, bool IsDisabled, bool IsNotTrusted, bool IsNotForReplication, bool IsSystemNamed)>(new CommandDefinition(query, cancellationToken: cancellationToken));

		return rows
			.GroupBy(r => r.ObjectId)
			.ToDictionary(
				g => g.Key,
				g => g.GroupBy(r => (r.ConstraintName, r.DeleteAction, r.UpdateAction, r.IsDisabled, r.IsNotTrusted, r.IsNotForReplication, r.IsSystemNamed))
					.Select(fg => new ForeignKeyConstraintSchema(fg.Key.ConstraintName, fg.First().ReferencedSchema, fg.First().ReferencedTable, fg.OrderBy(x => x.ColumnId).Select(x => new ForeignKeyColumnPair(x.ParentColumn, x.ReferencedColumn)).ToList(), fg.Key.DeleteAction, fg.Key.UpdateAction, fg.Key.IsDisabled, fg.Key.IsNotTrusted, fg.Key.IsNotForReplication, fg.Key.IsSystemNamed))
					.ToList());
	}

	private async Task<Dictionary<int, List<CheckConstraintSchema>>> ExtractCheckConstraintsAsync(SqlConnection connection, CancellationToken cancellationToken)
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

		var rows = await connection.QueryAsync<(int ObjectId, string ConstraintName, string Definition, bool IsDisabled, bool IsNotTrusted, bool IsNotForReplication, bool IsSystemNamed)>(new CommandDefinition(query, cancellationToken: cancellationToken));

		return rows
			.GroupBy(r => r.ObjectId)
			.ToDictionary(
				g => g.Key,
				g => g.Select(r => new CheckConstraintSchema(r.ConstraintName, r.Definition, r.IsDisabled, r.IsNotTrusted, r.IsNotForReplication, r.IsSystemNamed)).ToList());
	}

	private async Task<Dictionary<int, List<DefaultConstraintSchema>>> ExtractDefaultConstraintsAsync(SqlConnection connection, CancellationToken cancellationToken)
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

		var rows = await connection.QueryAsync<(int ObjectId, string ConstraintName, string ColumnName, string Definition, bool IsSystemNamed)>(new CommandDefinition(query, cancellationToken: cancellationToken));

		return rows
			.GroupBy(r => r.ObjectId)
			.ToDictionary(
				g => g.Key,
				g => g.Select(r => new DefaultConstraintSchema(r.ConstraintName, r.ColumnName, r.Definition, r.IsSystemNamed)).ToList());
	}

	private async Task<List<TableSchema>> ExtractTablesAsync(SqlConnection connection, Func<string, string, bool> isIgnored, int majorVersion, int engineEdition, Dictionary<int, List<ColumnSchema>> columnsByObjectId, Dictionary<int, List<IndexSchema>> indexesByObjectId, Dictionary<int, List<KeyConstraintSchema>> keyConstraintsByObjectId, Dictionary<int, List<ForeignKeyConstraintSchema>> foreignKeysByObjectId, Dictionary<int, List<CheckConstraintSchema>> checkConstraintsByObjectId, Dictionary<int, List<DefaultConstraintSchema>> defaultConstraintsByObjectId, CancellationToken cancellationToken)
	{
		// history_retention_period / history_retention_period_unit_desc were added in SQL Server 2017
		// (major 14+); Azure SQL (EngineEdition >= 5) has them regardless of the major version it reports.
		// On older servers the columns don't exist, so select NULL literals instead. INFINITE retention
		// (the default) is normalized to NULL so a 2016 server and a 2017 server hash a default temporal
		// table identically.
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
		// The reverse join (pt.history_table_id = t.object_id) identifies, for a history table itself,
		// the versioned parent table it belongs to; the calculator uses that linkage to derive an
		// effective name for an auto-named history table (and its auto-created index) instead of the
		// raw object_id-suffixed name. The join cannot fan out: at most one table references a given
		// history table.
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
				SCHEMA_NAME(pt.schema_id) AS VersionedParentSchema,
				pt.name AS VersionedParentName,
				{retentionSelect}
			FROM sys.tables t
			LEFT JOIN sys.identity_columns idc ON idc.object_id = t.object_id
			LEFT JOIN sys.tables ht ON t.history_table_id = ht.object_id
			LEFT JOIN sys.tables pt ON pt.history_table_id = t.object_id
			WHERE t.type = 'U'
			ORDER BY SchemaName, t.name";

		var tableInfos = (await connection.QueryAsync<(int ObjectId, string SchemaName, string TableName, string? IdentityColumn, string? IdentitySeed, string? IdentityIncrement, bool IdentityNotForReplication, string? TemporalType, bool IsMemoryOptimized, string? DurabilityDesc, string? HistoryTableName, string? VersionedParentSchema, string? VersionedParentName, int? HistoryRetentionPeriod, string? HistoryRetentionPeriodUnit)>(new CommandDefinition(tablesQuery, cancellationToken: cancellationToken)))
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
				info.HistoryRetentionPeriodUnit,
				info.VersionedParentSchema,
				info.VersionedParentName));
		}

		return tables.OrderBy(t => t.SchemaName, StringComparer.Ordinal).ThenBy(t => t.Name, StringComparer.Ordinal).ToList();
	}

	private async Task<List<StoredProcedureSchema>> ExtractStoredProceduresAsync(SqlConnection connection, Func<string, string, bool> isIgnored, string definitionHashExpr, Dictionary<(string SchemaName, string Name), List<ParameterSchema>> paramsByOwner, CancellationToken cancellationToken)
	{
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

		var allProcs = (await connection.QueryAsync<(string SchemaName, string ProcName, string? DefinitionHash, bool IsEncrypted, bool UsesAnsiNulls, bool UsesQuotedIdentifier)>(new CommandDefinition(allProcsQuery, cancellationToken: cancellationToken)))
			.Where(p => !isIgnored(p.SchemaName, p.ProcName))
			.ToList();

		var procedures = allProcs
			.Select(proc => new StoredProcedureSchema(
				proc.SchemaName,
				proc.ProcName,
				paramsByOwner.GetValueOrDefault((proc.SchemaName, proc.ProcName), new List<ParameterSchema>()),
				proc.IsEncrypted ? EncryptedDefinitionSentinel : (proc.DefinitionHash ?? ComputeEmptyDefinitionHash()),
					proc.UsesAnsiNulls,
					proc.UsesQuotedIdentifier
			))
			.OrderBy(p => p.SchemaName, StringComparer.Ordinal)
			.ThenBy(p => p.Name, StringComparer.Ordinal)
			.ToList();

		return procedures;
	}

	private async Task<Dictionary<(string SchemaName, string Name), List<ParameterSchema>>> ExtractParametersAsync(SqlConnection connection, CancellationToken cancellationToken)
	{
		// Parameters for stored procedures and functions share one query and one (SchemaName, Name)-keyed
		// dictionary: procs and functions share the schema-scoped object namespace, so the key cannot
		// collide between them. bt resolves an alias scalar type's underlying base type, same as the
		// column extraction above; it stays null for built-in types and table-typed (readonly TVP)
		// parameters. A scalar function's return type arrives as the parameter_id = 0 row (empty name).
		const string paramsQuery = @"
			SELECT
				SCHEMA_NAME(p.schema_id) AS SchemaName,
				p.name AS OwnerName,
				pa.name AS ParamName,
				CASE WHEN t.is_user_defined = 1 THEN SCHEMA_NAME(t.schema_id) + '.' + t.name ELSE t.name END AS TypeName,
				pa.max_length AS MaxLength,
				pa.precision AS Precision,
				pa.scale AS Scale,
				pa.is_nullable AS IsNullable,
				pa.is_output AS IsOutput,
				pa.is_readonly AS IsReadonly,
				CASE WHEN xsc.xml_collection_id IS NOT NULL THEN SCHEMA_NAME(xsc.schema_id) + '.' + xsc.name ELSE NULL END AS XmlSchemaCollectionName,
				pa.is_xml_document AS IsXmlDocument,
				bt.name AS BaseTypeName,
				t.max_length AS AliasMaxLength,
				t.precision AS AliasPrecision,
				t.scale AS AliasScale,
				t.is_nullable AS AliasIsNullable
			FROM sys.objects p
			INNER JOIN sys.parameters pa ON p.object_id = pa.object_id
			INNER JOIN sys.types t ON pa.user_type_id = t.user_type_id
			LEFT JOIN sys.types bt ON t.is_user_defined = 1 AND t.is_table_type = 0 AND t.system_type_id = bt.user_type_id
			LEFT JOIN sys.xml_schema_collections xsc ON pa.xml_collection_id = xsc.xml_collection_id
			WHERE p.type IN ('P', 'FN', 'IF', 'TF')
			ORDER BY SchemaName, p.name, pa.parameter_id";

		var rows = await connection.QueryAsync<(string SchemaName, string OwnerName, string ParamName, string TypeName, int MaxLength, int Precision, int Scale, bool IsNullable, bool IsOutput, bool IsReadonly, string? XmlSchemaCollectionName, bool IsXmlDocument, string? BaseTypeName, int AliasMaxLength, byte AliasPrecision, byte AliasScale, bool AliasIsNullable)>(new CommandDefinition(paramsQuery, cancellationToken: cancellationToken));

		return rows
			.GroupBy(p => (p.SchemaName, p.OwnerName))
			.ToDictionary(
				g => g.Key,
				g => g.Select(p => new ParameterSchema(p.ParamName.TrimStart('@'), EffectiveDataType(p.TypeName, p.BaseTypeName, p.AliasMaxLength, p.AliasPrecision, p.AliasScale, p.AliasIsNullable), p.MaxLength, p.Precision, p.Scale, p.IsNullable, p.IsOutput, p.IsReadonly, p.XmlSchemaCollectionName, p.IsXmlDocument)).ToList());
	}

	private async Task<List<UserDefinedTableTypeSchema>> ExtractUserDefinedTableTypesAsync(SqlConnection connection, Func<string, string, bool> isIgnored, Dictionary<int, List<ColumnSchema>> columnsByObjectId, Dictionary<int, List<KeyConstraintSchema>> keyConstraintsByObjectId, Dictionary<int, List<CheckConstraintSchema>> checkConstraintsByObjectId, Dictionary<int, List<DefaultConstraintSchema>> defaultConstraintsByObjectId, CancellationToken cancellationToken)
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

		var typeInfos = (await connection.QueryAsync<(int ObjectId, string SchemaName, string TableTypeName, string? IdentityColumn, string? IdentitySeed, string? IdentityIncrement, bool IdentityNotForReplication, bool IsMemoryOptimized)>(new CommandDefinition(typesQuery, cancellationToken: cancellationToken)))
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

	private async Task<List<ViewSchema>> ExtractViewsAsync(SqlConnection connection, Func<string, string, bool> isIgnored, string definitionHashExpr, Dictionary<int, List<IndexSchema>> indexesByObjectId, CancellationToken cancellationToken)
	{
		// A WITH ENCRYPTION view has a row in sys.sql_modules but a NULL definition, handled with the
		// same IsEncrypted sentinel as stored procedures.
		string viewsQuery = $@"
			SELECT v.object_id AS ObjectId, SCHEMA_NAME(v.schema_id) AS SchemaName, v.name AS ViewName,
				{definitionHashExpr} AS DefinitionHash,
				CAST(CASE WHEN m.object_id IS NOT NULL AND m.definition IS NULL THEN 1 ELSE 0 END AS bit) AS IsEncrypted,
				CAST(ISNULL(m.uses_ansi_nulls, 1) AS bit) AS UsesAnsiNulls,
				CAST(ISNULL(m.uses_quoted_identifier, 1) AS bit) AS UsesQuotedIdentifier
			FROM sys.views v
			LEFT JOIN sys.sql_modules m ON v.object_id = m.object_id
			ORDER BY SchemaName, v.name";

		var views = (await connection.QueryAsync<(int ObjectId, string SchemaName, string ViewName, string? DefinitionHash, bool IsEncrypted, bool UsesAnsiNulls, bool UsesQuotedIdentifier)>(new CommandDefinition(viewsQuery, cancellationToken: cancellationToken)))
			.Where(v => !isIgnored(v.SchemaName, v.ViewName))
			.ToList();

		return views
			.Select(v => new ViewSchema(
				v.SchemaName,
				v.ViewName,
				indexesByObjectId.GetValueOrDefault(v.ObjectId, new List<IndexSchema>()),
				v.IsEncrypted ? EncryptedDefinitionSentinel : (v.DefinitionHash ?? ComputeEmptyDefinitionHash()),
				v.UsesAnsiNulls,
				v.UsesQuotedIdentifier))
			.OrderBy(v => v.SchemaName, StringComparer.Ordinal)
			.ThenBy(v => v.Name, StringComparer.Ordinal)
			.ToList();
	}

	private async Task<List<FunctionSchema>> ExtractFunctionsAsync(SqlConnection connection, Func<string, string, bool> isIgnored, string definitionHashExpr, Dictionary<(string SchemaName, string Name), List<ParameterSchema>> paramsByOwner, CancellationToken cancellationToken)
	{
		// No is_ms_shipped filter, mirroring stored procedure extraction: SSMS's fn_diagramobjects is
		// handled via IgnoreSysDiagramObjects/ObjectNamesToIgnore instead of a blanket exclusion here.
		string functionsQuery = $@"
			SELECT o.object_id AS ObjectId, SCHEMA_NAME(o.schema_id) AS SchemaName, o.name AS FunctionName, o.type_desc AS TypeDesc,
				{definitionHashExpr} AS DefinitionHash,
				CAST(CASE WHEN m.object_id IS NOT NULL AND m.definition IS NULL THEN 1 ELSE 0 END AS bit) AS IsEncrypted,
				CAST(ISNULL(m.uses_ansi_nulls, 1) AS bit) AS UsesAnsiNulls,
				CAST(ISNULL(m.uses_quoted_identifier, 1) AS bit) AS UsesQuotedIdentifier
			FROM sys.objects o
			LEFT JOIN sys.sql_modules m ON o.object_id = m.object_id
			WHERE o.type IN ('FN', 'IF', 'TF')
			ORDER BY SchemaName, o.name";

		var functions = (await connection.QueryAsync<(int ObjectId, string SchemaName, string FunctionName, string TypeDesc, string? DefinitionHash, bool IsEncrypted, bool UsesAnsiNulls, bool UsesQuotedIdentifier)>(new CommandDefinition(functionsQuery, cancellationToken: cancellationToken)))
			.Where(f => !isIgnored(f.SchemaName, f.FunctionName))
			.ToList();

		return functions
			.Select(f => new FunctionSchema(
				f.SchemaName,
				f.FunctionName,
				f.TypeDesc,
				paramsByOwner.GetValueOrDefault((f.SchemaName, f.FunctionName), new List<ParameterSchema>()),
				f.IsEncrypted ? EncryptedDefinitionSentinel : (f.DefinitionHash ?? ComputeEmptyDefinitionHash()),
				f.UsesAnsiNulls,
				f.UsesQuotedIdentifier))
			.OrderBy(f => f.SchemaName, StringComparer.Ordinal)
			.ThenBy(f => f.Name, StringComparer.Ordinal)
			.ToList();
	}

	private async Task<List<TriggerSchema>> ExtractTriggersAsync(SqlConnection connection, Func<string, string, bool> isIgnored, string definitionHashExpr, CancellationToken cancellationToken)
	{
		// parent_class = 1 restricts to object (DML) triggers — database-scoped DDL triggers are out of
		// scope; type = 'TR' excludes CLR triggers ('TA'). sys.triggers itself has no schema_id, so the
		// trigger's own schema is taken from its sys.objects row (a DML trigger always lives in its
		// parent's schema, so SchemaName == ParentSchemaName — captured separately anyway since the
		// catalog exposes them as distinct columns).
		string triggersQuery = $@"
			SELECT SCHEMA_NAME(o.schema_id) AS SchemaName, tr.name AS TriggerName, tr.object_id AS ObjectId,
				SCHEMA_NAME(po.schema_id) AS ParentSchemaName, po.name AS ParentName,
				tr.is_disabled AS IsDisabled, tr.is_instead_of_trigger AS IsInsteadOfTrigger,
				tr.is_not_for_replication AS IsNotForReplication,
				{definitionHashExpr} AS DefinitionHash,
				CAST(CASE WHEN m.object_id IS NOT NULL AND m.definition IS NULL THEN 1 ELSE 0 END AS bit) AS IsEncrypted,
				CAST(ISNULL(m.uses_ansi_nulls, 1) AS bit) AS UsesAnsiNulls,
				CAST(ISNULL(m.uses_quoted_identifier, 1) AS bit) AS UsesQuotedIdentifier
			FROM sys.triggers tr
			INNER JOIN sys.objects o ON tr.object_id = o.object_id
			INNER JOIN sys.objects po ON tr.parent_id = po.object_id
			LEFT JOIN sys.sql_modules m ON tr.object_id = m.object_id
			WHERE tr.parent_class = 1 AND tr.type = 'TR'
			ORDER BY SchemaName, tr.name";

		const string eventsQuery = @"
			SELECT te.object_id AS ObjectId, te.type_desc AS Type, te.is_first AS IsFirst, te.is_last AS IsLast
			FROM sys.trigger_events te";

		var triggerRows = (await connection.QueryAsync<(string SchemaName, string TriggerName, int ObjectId, string ParentSchemaName, string ParentName, bool IsDisabled, bool IsInsteadOfTrigger, bool IsNotForReplication, string? DefinitionHash, bool IsEncrypted, bool UsesAnsiNulls, bool UsesQuotedIdentifier)>(new CommandDefinition(triggersQuery, cancellationToken: cancellationToken)))
			// A trigger is excluded when either it or its parent object is excluded, so excluding a table
			// takes its triggers with it rather than leaking them back into the hash.
			.Where(t => !isIgnored(t.SchemaName, t.TriggerName) && !isIgnored(t.ParentSchemaName, t.ParentName))
			.ToList();

		var eventsByObjectId = (await connection.QueryAsync<(int ObjectId, string Type, bool IsFirst, bool IsLast)>(new CommandDefinition(eventsQuery, cancellationToken: cancellationToken)))
			.GroupBy(e => e.ObjectId)
			.ToDictionary(g => g.Key, g => g.OrderBy(e => e.Type, StringComparer.Ordinal).Select(e => new TriggerEventSchema(e.Type, e.IsFirst, e.IsLast)).ToList());

		return triggerRows
			.Select(t => new TriggerSchema(
				t.SchemaName,
				t.TriggerName,
				t.ParentSchemaName,
				t.ParentName,
				t.IsDisabled,
				t.IsInsteadOfTrigger,
				t.IsNotForReplication,
				eventsByObjectId.GetValueOrDefault(t.ObjectId, new List<TriggerEventSchema>()),
				t.IsEncrypted ? EncryptedDefinitionSentinel : (t.DefinitionHash ?? ComputeEmptyDefinitionHash()),
				t.UsesAnsiNulls,
				t.UsesQuotedIdentifier))
			.OrderBy(t => t.SchemaName, StringComparer.Ordinal)
			.ThenBy(t => t.Name, StringComparer.Ordinal)
			.ToList();
	}

	private async Task<List<SequenceSchema>> ExtractSequencesAsync(SqlConnection connection, Func<string, string, bool> isIgnored, CancellationToken cancellationToken)
	{
		// Pure catalog read, no sys.sql_modules involvement. bt resolves an alias scalar type's underlying
		// base type, same technique as columns/parameters — a sequence may be declared AS a user-defined
		// alias type. current_value is deliberately not selected: it is runtime state, not schema.
		const string sequencesQuery = @"
			SELECT SCHEMA_NAME(s.schema_id) AS SchemaName, s.name AS SequenceName,
				CASE WHEN ty.is_user_defined = 1 THEN SCHEMA_NAME(ty.schema_id) + '.' + ty.name ELSE ty.name END AS DataType,
				bt.name AS BaseTypeName, ty.max_length AS AliasMaxLength, ty.precision AS AliasPrecision, ty.scale AS AliasScale, ty.is_nullable AS AliasIsNullable,
				s.precision AS Precision,
				CONVERT(NVARCHAR(64), s.start_value) AS StartValue,
				CONVERT(NVARCHAR(64), s.increment) AS Increment,
				CONVERT(NVARCHAR(64), s.minimum_value) AS MinimumValue,
				CONVERT(NVARCHAR(64), s.maximum_value) AS MaximumValue,
				s.is_cycling AS IsCycling, s.is_cached AS IsCached, s.cache_size AS CacheSize
			FROM sys.sequences s
			INNER JOIN sys.types ty ON s.user_type_id = ty.user_type_id
			LEFT JOIN sys.types bt ON ty.is_user_defined = 1 AND ty.is_table_type = 0 AND ty.system_type_id = bt.user_type_id
			ORDER BY SchemaName, s.name";

		var sequences = (await connection.QueryAsync<(string SchemaName, string SequenceName, string DataType, string? BaseTypeName, int AliasMaxLength, byte AliasPrecision, byte AliasScale, bool AliasIsNullable, byte Precision, string StartValue, string Increment, string MinimumValue, string MaximumValue, bool IsCycling, bool IsCached, int? CacheSize)>(new CommandDefinition(sequencesQuery, cancellationToken: cancellationToken)))
			.Where(s => !isIgnored(s.SchemaName, s.SequenceName))
			.ToList();

		return sequences
			.Select(s => new SequenceSchema(
				s.SchemaName,
				s.SequenceName,
				EffectiveDataType(s.DataType, s.BaseTypeName, s.AliasMaxLength, s.AliasPrecision, s.AliasScale, s.AliasIsNullable),
				s.Precision,
				s.StartValue,
				s.Increment,
				s.MinimumValue,
				s.MaximumValue,
				s.IsCycling,
				s.IsCached,
				s.CacheSize))
			.OrderBy(s => s.SchemaName, StringComparer.Ordinal)
			.ThenBy(s => s.Name, StringComparer.Ordinal)
			.ToList();
	}

	private async Task<List<SynonymSchema>> ExtractSynonymsAsync(SqlConnection connection, Func<string, string, bool> isIgnored, CancellationToken cancellationToken)
	{
		const string synonymsQuery = @"
			SELECT SCHEMA_NAME(sn.schema_id) AS SchemaName, sn.name AS SynonymName, sn.base_object_name AS BaseObjectName
			FROM sys.synonyms sn
			ORDER BY SchemaName, sn.name";

		var synonyms = (await connection.QueryAsync<(string SchemaName, string SynonymName, string BaseObjectName)>(new CommandDefinition(synonymsQuery, cancellationToken: cancellationToken)))
			.Where(s => !isIgnored(s.SchemaName, s.SynonymName))
			.ToList();

		return synonyms
			.Select(s => new SynonymSchema(s.SchemaName, s.SynonymName, s.BaseObjectName))
			.OrderBy(s => s.SchemaName, StringComparer.Ordinal)
			.ThenBy(s => s.Name, StringComparer.Ordinal)
			.ToList();
	}

	private async Task<List<ExtendedPropertySchema>> ExtractExtendedPropertiesAsync(SqlConnection connection, Func<string, string, bool> isIgnored, CancellationToken cancellationToken)
	{
		// One pass over sys.extended_properties covering the property classes whose targets are in scope:
		// 0 database, 1 object-or-column, 2 parameter, 3 schema, 6 type (table types only — alias/CLR
		// scalar types are out of scope), 7 index. The (class, major_id, minor_id) target is resolved to
		// names here because catalog ids are not deterministic across databases. Class-1 targets are
		// restricted to the extracted object kinds; that deliberately drops properties on constraint
		// objects, whose system-generated names are not deterministic across databases. po resolves a
		// trigger's parent so a property follows its trigger when the parent table/view is excluded.
		// The value is a sql_variant: SQL_VARIANT_PROPERTY preserves its base type and the NVARCHAR
		// rendering preserves its content; both are NULL for a NULL value, keeping it distinct from ''.
		const string propertiesQuery = @"
			SELECT ep.class_desc AS ClassDesc, ep.name AS Name,
				CONVERT(NVARCHAR(128), SQL_VARIANT_PROPERTY(ep.value, 'BaseType')) AS ValueType,
				CONVERT(NVARCHAR(MAX), ep.value) AS Value,
				CASE ep.class WHEN 3 THEN s.name WHEN 6 THEN SCHEMA_NAME(ty.schema_id) ELSE SCHEMA_NAME(o.schema_id) END AS SchemaName,
				CASE ep.class WHEN 6 THEN ty.name ELSE o.name END AS ObjectName,
				o.type AS ObjectType,
				CASE ep.class WHEN 1 THEN c.name WHEN 2 THEN pa.name WHEN 7 THEN ix.name END AS SubObjectName,
				SCHEMA_NAME(po.schema_id) AS ParentSchemaName, po.name AS ParentName
			FROM sys.extended_properties ep
			LEFT JOIN sys.objects o ON ep.class IN (1, 2, 7) AND ep.major_id = o.object_id
			LEFT JOIN sys.objects po ON o.parent_object_id <> 0 AND o.parent_object_id = po.object_id
			LEFT JOIN sys.columns c ON ep.class = 1 AND ep.minor_id > 0 AND c.object_id = ep.major_id AND c.column_id = ep.minor_id
			LEFT JOIN sys.parameters pa ON ep.class = 2 AND pa.object_id = ep.major_id AND pa.parameter_id = ep.minor_id
			LEFT JOIN sys.indexes ix ON ep.class = 7 AND ix.object_id = ep.major_id AND ix.index_id = ep.minor_id
			LEFT JOIN sys.schemas s ON ep.class = 3 AND ep.major_id = s.schema_id
			LEFT JOIN sys.types ty ON ep.class = 6 AND ep.major_id = ty.user_type_id
			WHERE ep.class IN (0, 1, 2, 3, 6, 7)
				AND (ep.class <> 1 OR o.type IN ('U', 'V', 'P', 'FN', 'IF', 'TF', 'TR', 'SO', 'SN'))
				AND (ep.class <> 2 OR o.type IN ('P', 'FN', 'IF', 'TF'))
				AND (ep.class <> 7 OR o.type IN ('U', 'V'))
				AND (ep.class <> 6 OR ty.is_table_type = 1)";

		var properties = (await connection.QueryAsync<(string ClassDesc, string Name, string? ValueType, string? Value, string? SchemaName, string? ObjectName, string? ObjectType, string? SubObjectName, string? ParentSchemaName, string? ParentName)>(new CommandDefinition(propertiesQuery, cancellationToken: cancellationToken)))
			// A property follows its excluded owner: dropping an object from the hash via
			// ObjectNamesToIgnore drops the properties on it (and on its columns/parameters/indexes),
			// and — like the trigger rule — excluding a table takes its triggers' properties with it.
			.Where(p => p.ObjectName is null || !isIgnored(p.SchemaName!, p.ObjectName))
			.Where(p => p.ParentName is null || !isIgnored(p.ParentSchemaName!, p.ParentName))
			.ToList();

		return properties
			.Select(p => new ExtendedPropertySchema(p.ClassDesc, p.SchemaName, p.ObjectName, p.SubObjectName, p.Name, p.ValueType, p.Value))
			.OrderBy(p => p.ClassDesc, StringComparer.Ordinal)
			.ThenBy(p => p.SchemaName ?? string.Empty, StringComparer.Ordinal)
			.ThenBy(p => p.ObjectName ?? string.Empty, StringComparer.Ordinal)
			.ThenBy(p => p.SubObjectName ?? string.Empty, StringComparer.Ordinal)
			.ThenBy(p => p.Name, StringComparer.Ordinal)
			.ToList();
	}

	/// <summary>
	/// Appends an alias scalar type's underlying base type definition to its schema-qualified name (e.g.
	/// <c>[dbo].[OrderTotal]{decimal(9,2) NOT NULL}</c>) so recreating the alias with a different base type
	/// changes the hash. <paramref name="baseTypeName"/> is null for built-in types and table types, in which
	/// case <paramref name="dataType"/> is returned unchanged.
	/// </summary>
	private static string EffectiveDataType(string dataType, string? baseTypeName, int aliasMaxLength, byte aliasPrecision, byte aliasScale, bool aliasIsNullable)
	{
		return baseTypeName is null ? dataType : $"{dataType}{{{FormatAliasBaseType(baseTypeName, aliasMaxLength, aliasPrecision, aliasScale, aliasIsNullable)}}}";
	}

	/// <summary>
	/// Deterministically renders an alias scalar type's underlying system type using the alias's own
	/// max_length/precision/scale/is_nullable (its CREATE TYPE definition), not the referencing column's.
	/// </summary>
	private static string FormatAliasBaseType(string baseTypeName, int maxLength, byte precision, byte scale, bool isNullable)
	{
		var name = baseTypeName.ToLowerInvariant();
		var suffix = name switch
		{
			"decimal" or "numeric" => $"({precision},{scale})",
			"char" or "varchar" or "binary" or "varbinary" => maxLength == -1 ? "(max)" : $"({maxLength})",
			"nchar" or "nvarchar" => maxLength == -1 ? "(max)" : $"({maxLength / 2})",
			"datetime2" or "datetimeoffset" or "time" => $"({scale})",
			_ => string.Empty,
		};
		return $"{name}{suffix} {(isNullable ? "NULL" : "NOT NULL")}";
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
