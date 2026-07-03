using Dapper;
using Microsoft.Data.SqlClient;

namespace zachtbeer.SqlSchemaHasher;

/// <summary>
/// Extracts schema metadata from a SQL Server database using efficient batch queries.
/// Uses sys.* catalog views instead of sp_help for better performance on large schemas.
/// All object names are schema-qualified to distinguish objects in different schemas.
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
		// ObjectNamesToIgnore doesn't silently drop them.
		IReadOnlySet<string> objectNamesToIgnore = resolvedOptions.IgnoreSysDiagramObjects
			? resolvedOptions.ObjectNamesToIgnore.Concat(SchemaHashOptions.SysDiagramObjectNames).ToHashSet(StringComparer.OrdinalIgnoreCase)
			: resolvedOptions.ObjectNamesToIgnore;
		var schemaFilter = resolvedOptions.SchemaFilter;

		var tables = await ExtractTablesAsync(connection, objectNamesToIgnore);
		var storedProcedures = await ExtractStoredProceduresAsync(connection, objectNamesToIgnore);
		var userDefinedTableTypes = await ExtractUserDefinedTableTypesAsync(connection, objectNamesToIgnore);

		// Apply schema filter if specified
		if (!string.IsNullOrWhiteSpace(schemaFilter))
		{
			tables = tables.Where(t => t.SchemaName.Equals(schemaFilter, StringComparison.OrdinalIgnoreCase)).ToList();
			storedProcedures = storedProcedures.Where(p => p.SchemaName.Equals(schemaFilter, StringComparison.OrdinalIgnoreCase)).ToList();
			userDefinedTableTypes = userDefinedTableTypes.Where(u => u.SchemaName.Equals(schemaFilter, StringComparison.OrdinalIgnoreCase)).ToList();
		}

		return new SchemaMetadata(tables, storedProcedures, userDefinedTableTypes);
	}

	private async Task<List<TableSchema>> ExtractTablesAsync(SqlConnection connection, IReadOnlySet<string> objectNamesToIgnore)
	{
		// Query 1: Get all tables with schema names and their identity column (a table has at most one).
		// Joins sys.identity_columns by object_id rather than resolving names through OBJECT_ID,
		// which breaks for table names containing dots.
		const string tablesQuery = @"
			SELECT
				SCHEMA_NAME(t.schema_id) AS SchemaName,
				t.name AS TableName,
				idc.name AS IdentityColumn,
				CONVERT(NVARCHAR(64), idc.seed_value) AS IdentitySeed,
				CONVERT(NVARCHAR(64), idc.increment_value) AS IdentityIncrement,
				CAST(ISNULL(idc.is_not_for_replication, 0) AS bit) AS IdentityNotForReplication
			FROM sys.tables t
			LEFT JOIN sys.identity_columns idc ON idc.object_id = t.object_id
			WHERE t.type = 'U'
			ORDER BY SchemaName, t.name";

		var tableInfos = (await connection.QueryAsync<(string SchemaName, string TableName, string? IdentityColumn, string? IdentitySeed, string? IdentityIncrement, bool IdentityNotForReplication)>(tablesQuery))
			.Where(t => !objectNamesToIgnore.Contains(t.TableName))
			.ToList();

		if (tableInfos.Count == 0)
			return new List<TableSchema>();

		// Query 2: Get all columns for all tables in one query (with schema).
		// Computed columns are joined from sys.computed_columns so a change to the formula (or to
		// PERSISTED) is reflected in the hash; the formula alone is invisible in sys.columns.
		const string columnsQuery = @"
			SELECT
				SCHEMA_NAME(t.schema_id) AS SchemaName,
				t.name AS TableName,
				c.name AS ColumnName,
				CASE WHEN ty.is_user_defined = 1 THEN SCHEMA_NAME(ty.schema_id) + '.' + ty.name ELSE ty.name END AS DataType,
				c.max_length AS MaxLength,
				c.precision AS Precision,
				c.scale AS Scale,
				c.is_nullable AS IsNullable,
				c.is_computed AS IsComputed,
				cc.definition AS ComputedDefinition,
				CAST(ISNULL(cc.is_persisted, 0) AS bit) AS IsPersisted,
				c.collation_name AS Collation,
				c.column_id AS Ordinal,
				c.is_sparse AS IsSparse,
				c.is_rowguidcol AS IsRowGuidCol
			FROM sys.tables t
			INNER JOIN sys.columns c ON t.object_id = c.object_id
			INNER JOIN sys.types ty ON c.user_type_id = ty.user_type_id
			LEFT JOIN sys.computed_columns cc ON c.object_id = cc.object_id AND c.column_id = cc.column_id
			WHERE t.type = 'U'
			ORDER BY SchemaName, t.name, c.column_id";

		var allColumns = (await connection.QueryAsync<(string SchemaName, string TableName, string ColumnName, string DataType, int MaxLength, int Precision, int Scale, bool IsNullable, bool IsComputed, string? ComputedDefinition, bool IsPersisted, string? Collation, int Ordinal, bool IsSparse, bool IsRowGuidCol)>(columnsQuery))
			.Where(c => !objectNamesToIgnore.Contains(c.TableName))
			.GroupBy(c => (c.SchemaName, c.TableName))
			.ToDictionary(
				g => g.Key,
				g => g.Select(c => new ColumnSchema(c.ColumnName, c.DataType, c.MaxLength, c.Precision, c.Scale, c.IsNullable, c.IsComputed, c.ComputedDefinition, c.IsPersisted, c.Collation, c.Ordinal, c.IsSparse, c.IsRowGuidCol)).OrderBy(c => c.Ordinal).ToList());

		// Query 3: Get all index columns for all tables (with schema)
		// Returns one row per index column; column aggregation is done in C# for SQL Server 2012 compatibility.
		const string indexesQuery = @"
			SELECT
				SCHEMA_NAME(t.schema_id) AS SchemaName,
				t.name AS TableName,
				i.name AS IndexName,
				i.type_desc AS IndexType,
				i.is_unique AS IsUnique,
				i.is_primary_key AS IsPrimaryKey,
				c.name AS ColumnName,
				ic.key_ordinal AS KeyOrdinal,
				ic.is_included_column AS IsIncluded,
				CAST(ISNULL(ic.is_descending_key, 0) AS bit) AS IsDescending,
				i.filter_definition AS FilterDefinition
			FROM sys.tables t
			INNER JOIN sys.indexes i ON t.object_id = i.object_id
			INNER JOIN sys.index_columns ic ON i.object_id = ic.object_id AND i.index_id = ic.index_id
			INNER JOIN sys.columns c ON ic.object_id = c.object_id AND ic.column_id = c.column_id
			WHERE t.type = 'U' AND i.name IS NOT NULL
			ORDER BY SchemaName, t.name, i.name, ic.key_ordinal";

		var allIndexes = (await connection.QueryAsync<(string SchemaName, string TableName, string IndexName, string IndexType, bool IsUnique, bool IsPrimaryKey, string ColumnName, int KeyOrdinal, bool IsIncluded, bool IsDescending, string? FilterDefinition)>(indexesQuery))
			.Where(i => !objectNamesToIgnore.Contains(i.TableName))
			.GroupBy(i => (i.SchemaName, i.TableName))
			.ToDictionary(
				g => g.Key,
				// FilterDefinition is constant per index, so it joins the per-index grouping key.
				g => g.GroupBy(i => (i.IndexName, i.IndexType, i.IsUnique, i.IsPrimaryKey, i.FilterDefinition))
					.Select(ig =>
					{
						string description = BuildIndexDescription(ig.Key.IndexType, ig.Key.IsUnique, ig.Key.IsPrimaryKey);
						// Key columns are ordered by key ordinal; columnstore columns all have key_ordinal 0,
						// so ties are broken by name for determinism. Included columns are an unordered set
						// and are kept separate from key columns, sorted by name. Descending key columns are
						// suffixed with " DESC"; KeysWithoutDirection is only materialized when it differs
						// so the calculator can ignore sort order without string parsing.
						var orderedKeyColumns = ig.Where(c => !c.IsIncluded).OrderBy(c => c.KeyOrdinal).ThenBy(c => c.ColumnName, StringComparer.Ordinal).ToList();
						string keyColumns = string.Join(", ", orderedKeyColumns.Select(c => c.ColumnName + (c.IsDescending ? " DESC" : "")));
						string? keysWithoutDirection = orderedKeyColumns.Any(c => c.IsDescending) ? string.Join(", ", orderedKeyColumns.Select(c => c.ColumnName)) : null;
						var includedColumns = ig.Where(c => c.IsIncluded).Select(c => c.ColumnName).OrderBy(c => c, StringComparer.Ordinal).ToList();
						string? included = includedColumns.Count > 0 ? string.Join(", ", includedColumns) : null;
						return new IndexSchema(ig.Key.IndexName, description, keyColumns, included, keysWithoutDirection, ig.Key.FilterDefinition);
					}).OrderBy(i => i.Keys, StringComparer.Ordinal).ThenBy(i => i.Name, StringComparer.Ordinal).ToList());

		// Query 4: Get all constraints (with schema)
		// Returns one row per constraint column via UNION ALL; column aggregation is done in C# for SQL Server 2012 compatibility.
		const string constraintsQuery = @"
			SELECT SCHEMA_NAME(t.schema_id) AS SchemaName, t.name AS TableName,
				CASE WHEN kc.type = 'PK' THEN 'PRIMARY KEY' ELSE 'UNIQUE' END AS ConstraintType,
				kc.name AS ConstraintName, c.name AS ColumnOrDefinition, ic.key_ordinal AS SortOrder,
				CAST(0 AS bit) AS IsDisabled, CAST(0 AS bit) AS IsNotTrusted
			FROM sys.tables t
			INNER JOIN sys.key_constraints kc ON t.object_id = kc.parent_object_id
			INNER JOIN sys.index_columns ic ON kc.parent_object_id = ic.object_id AND kc.unique_index_id = ic.index_id
			INNER JOIN sys.columns c ON ic.object_id = c.object_id AND ic.column_id = c.column_id
			WHERE t.type = 'U'
			UNION ALL
			SELECT SCHEMA_NAME(t.schema_id), t.name, 'FOREIGN KEY', fk.name,
				COL_NAME(fkc.parent_object_id, fkc.parent_column_id) + ' -> ' + SCHEMA_NAME(rt.schema_id) + '.' + rt.name + '.' + COL_NAME(fkc.referenced_object_id, fkc.referenced_column_id)
						+ ' [ON DELETE ' + fk.delete_referential_action_desc COLLATE DATABASE_DEFAULT + ', ON UPDATE ' + fk.update_referential_action_desc COLLATE DATABASE_DEFAULT + ']',
				fkc.constraint_column_id, fk.is_disabled, fk.is_not_trusted
			FROM sys.tables t
			INNER JOIN sys.foreign_keys fk ON t.object_id = fk.parent_object_id
			INNER JOIN sys.foreign_key_columns fkc ON fk.object_id = fkc.constraint_object_id
			INNER JOIN sys.tables rt ON fk.referenced_object_id = rt.object_id
			WHERE t.type = 'U'
			UNION ALL
			SELECT SCHEMA_NAME(t.schema_id), t.name, 'DEFAULT', dc.name,
				COL_NAME(dc.parent_object_id, dc.parent_column_id) + ' = ' + dc.definition, 0,
				CAST(0 AS bit), CAST(0 AS bit)
			FROM sys.tables t
			INNER JOIN sys.default_constraints dc ON t.object_id = dc.parent_object_id
			WHERE t.type = 'U'
			UNION ALL
			SELECT SCHEMA_NAME(t.schema_id), t.name, 'CHECK', cc.name, cc.definition, 0,
				cc.is_disabled, cc.is_not_trusted
			FROM sys.tables t
			INNER JOIN sys.check_constraints cc ON t.object_id = cc.parent_object_id
			WHERE t.type = 'U'";

		var allConstraints = (await connection.QueryAsync<(string SchemaName, string TableName, string ConstraintType, string ConstraintName, string? ColumnOrDefinition, int SortOrder, bool IsDisabled, bool IsNotTrusted)>(constraintsQuery))
			.Where(c => !objectNamesToIgnore.Contains(c.TableName))
			.GroupBy(c => (c.SchemaName, c.TableName))
			.ToDictionary(
				g => g.Key,
				g => g.GroupBy(c => (c.ConstraintType, c.ConstraintName, c.IsDisabled, c.IsNotTrusted))
					.Select(cg =>
					{
						string keyColumns = string.Join(", ", cg.OrderBy(c => c.SortOrder).ThenBy(c => c.ColumnOrDefinition, StringComparer.Ordinal).Select(c => c.ColumnOrDefinition));
						return new ConstraintSchema(cg.Key.ConstraintType, keyColumns, cg.Key.ConstraintName, cg.Key.IsDisabled, cg.Key.IsNotTrusted);
					}).OrderBy(c => c.Keys, StringComparer.Ordinal).ThenBy(c => c.Type, StringComparer.Ordinal).ThenBy(c => c.Name, StringComparer.Ordinal).ToList());

		// Build table schemas
		var tables = new List<TableSchema>(tableInfos.Count);
		foreach (var (schemaName, tableName, identityColumn, identitySeed, identityIncrement, identityNotForReplication) in tableInfos)
		{
			var key = (schemaName, tableName);
			var columns = allColumns.GetValueOrDefault(key, new List<ColumnSchema>());
			var indexes = allIndexes.GetValueOrDefault(key, new List<IndexSchema>());
			var constraints = allConstraints.GetValueOrDefault(key, new List<ConstraintSchema>());

			tables.Add(new TableSchema(schemaName, tableName, columns, indexes, constraints, identityColumn, identitySeed, identityIncrement, identityNotForReplication));
		}

		return tables.OrderBy(t => t.SchemaName, StringComparer.Ordinal).ThenBy(t => t.Name, StringComparer.Ordinal).ToList();
	}

	private async Task<List<StoredProcedureSchema>> ExtractStoredProceduresAsync(SqlConnection connection, IReadOnlySet<string> objectNamesToIgnore)
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
				CAST(CASE WHEN m.object_id IS NOT NULL AND m.definition IS NULL THEN 1 ELSE 0 END AS bit) AS IsEncrypted
			FROM sys.procedures p
			LEFT JOIN sys.sql_modules m ON p.object_id = m.object_id
			WHERE p.type = 'P'
			ORDER BY SchemaName, p.name";

		var allProcs = (await connection.QueryAsync<(string SchemaName, string ProcName, string? DefinitionHash, bool IsEncrypted)>(allProcsQuery))
			.Where(p => !objectNamesToIgnore.Contains(p.ProcName))
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
				pa.is_readonly AS IsReadonly
			FROM sys.procedures p
			INNER JOIN sys.parameters pa ON p.object_id = pa.object_id
			INNER JOIN sys.types t ON pa.user_type_id = t.user_type_id
			WHERE p.type = 'P'
			ORDER BY SchemaName, p.name, pa.parameter_id";

		var procsAndParams = await connection.QueryAsync<(string SchemaName, string ProcName, string ParamName, string TypeName, int MaxLength, int Precision, int Scale, bool IsNullable, bool IsOutput, bool IsReadonly)>(paramsQuery);

		var paramsByProc = procsAndParams
			.Where(p => !objectNamesToIgnore.Contains(p.ProcName))
			.GroupBy(p => (p.SchemaName, p.ProcName))
			.ToDictionary(
				g => g.Key,
				g => g.Select(p => new ParameterSchema(p.ParamName.TrimStart('@'), p.TypeName, p.MaxLength, p.Precision, p.Scale, p.IsNullable, p.IsOutput, p.IsReadonly)).ToList()
			);

		var procedures = allProcs
			.Select(proc => new StoredProcedureSchema(
				proc.SchemaName,
				proc.ProcName,
				paramsByProc.GetValueOrDefault((proc.SchemaName, proc.ProcName), new List<ParameterSchema>()),
				proc.IsEncrypted ? EncryptedDefinitionSentinel : (proc.DefinitionHash ?? ComputeEmptyDefinitionHash())
			))
			.OrderBy(p => p.SchemaName, StringComparer.Ordinal)
			.ThenBy(p => p.Name, StringComparer.Ordinal)
			.ToList();

		return procedures;
	}

	private async Task<List<UserDefinedTableTypeSchema>> ExtractUserDefinedTableTypesAsync(SqlConnection connection, IReadOnlySet<string> objectNamesToIgnore)
	{
		// Get UDTs with schema names. Computed columns are joined the same way as table columns so
		// the formula participates in the hash.
		const string udtQuery = @"
			SELECT
				SCHEMA_NAME(tt.schema_id) AS SchemaName,
				tt.name AS TableTypeName,
				c.name AS ColumnName,
				CASE WHEN t.is_user_defined = 1 THEN SCHEMA_NAME(t.schema_id) + '.' + t.name ELSE t.name END AS DataType,
				c.max_length AS MaxLength,
				c.precision AS Precision,
				c.scale AS Scale,
				c.is_nullable AS IsNullable,
				c.is_computed AS IsComputed,
				cc.definition AS ComputedDefinition,
				CAST(ISNULL(cc.is_persisted, 0) AS bit) AS IsPersisted,
				c.collation_name AS Collation,
				c.column_id AS Ordinal,
				c.is_sparse AS IsSparse,
				c.is_rowguidcol AS IsRowGuidCol
			FROM sys.table_types tt
			INNER JOIN sys.columns c ON tt.type_table_object_id = c.object_id
			INNER JOIN sys.types t ON c.user_type_id = t.user_type_id
			LEFT JOIN sys.computed_columns cc ON c.object_id = cc.object_id AND c.column_id = cc.column_id
			ORDER BY SchemaName, tt.name, c.column_id";

		var udtData = await connection.QueryAsync<(string SchemaName, string TableTypeName, string ColumnName, string DataType, int MaxLength, int Precision, int Scale, bool IsNullable, bool IsComputed, string? ComputedDefinition, bool IsPersisted, string? Collation, int Ordinal, bool IsSparse, bool IsRowGuidCol)>(udtQuery);

		var udts = udtData
			.Where(u => !objectNamesToIgnore.Contains(u.TableTypeName))
			.GroupBy(u => (u.SchemaName, u.TableTypeName))
			.Select(g => new UserDefinedTableTypeSchema(
				g.First().SchemaName,
				g.Key.TableTypeName,
				g.Select(c => new ColumnSchema(c.ColumnName, c.DataType, c.MaxLength, c.Precision, c.Scale, c.IsNullable, c.IsComputed, c.ComputedDefinition, c.IsPersisted, c.Collation, c.Ordinal, c.IsSparse, c.IsRowGuidCol)).OrderBy(c => c.Ordinal).ToList()
			))
			.OrderBy(u => u.SchemaName, StringComparer.Ordinal)
			.ThenBy(u => u.Name, StringComparer.Ordinal)
			.ToList();

		return udts;
	}

	private static string BuildIndexDescription(string indexType, bool isUnique, bool isPrimaryKey)
	{
		var parts = new List<string>();

		if (isPrimaryKey)
			parts.Add("primary key");

		if (isUnique && !isPrimaryKey)
			parts.Add("unique");

		parts.Add(indexType.ToLowerInvariant());

		return string.Join(", ", parts);
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
}
