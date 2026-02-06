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

		var objectNamesToIgnore = resolvedOptions.ObjectNamesToIgnore;
		var schemaFilter = resolvedOptions.SchemaFilter;

		var tables = await ExtractTablesAsync(connection, objectNamesToIgnore);
		var storedProcedures = await ExtractStoredProceduresAsync(connection, objectNamesToIgnore);
		var userDefinedTableTypes = await ExtractUserDefinedTableTypesAsync(connection);

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
		// Query 1: Get all tables with schema names
		const string tablesQuery = @"
			SELECT
				SCHEMA_NAME(t.schema_id) AS SchemaName,
				t.name AS TableName,
				ic.COLUMN_NAME AS IdentityColumn
			FROM sys.tables t
			LEFT JOIN INFORMATION_SCHEMA.COLUMNS ic ON ic.TABLE_SCHEMA = SCHEMA_NAME(t.schema_id)
				AND ic.TABLE_NAME = t.name
				AND COLUMNPROPERTY(OBJECT_ID(ic.TABLE_SCHEMA + '.' + ic.TABLE_NAME), ic.COLUMN_NAME, 'IsIdentity') = 1
			WHERE t.type = 'U'
			ORDER BY SchemaName, t.name";

		var tableInfos = (await connection.QueryAsync<(string SchemaName, string TableName, string? IdentityColumn)>(tablesQuery))
			.Where(t => !objectNamesToIgnore.Contains(t.TableName))
			.ToList();

		if (tableInfos.Count == 0)
			return new List<TableSchema>();

		// Query 2: Get all columns for all tables in one query (with schema)
		const string columnsQuery = @"
			SELECT
				SCHEMA_NAME(t.schema_id) AS SchemaName,
				t.name AS TableName,
				c.name AS ColumnName,
				ty.name AS DataType,
				c.max_length AS MaxLength,
				c.precision AS Precision,
				c.scale AS Scale,
				c.is_nullable AS IsNullable
			FROM sys.tables t
			INNER JOIN sys.columns c ON t.object_id = c.object_id
			INNER JOIN sys.types ty ON c.user_type_id = ty.user_type_id
			WHERE t.type = 'U'
			ORDER BY SchemaName, t.name, c.column_id";

		var allColumns = (await connection.QueryAsync<(string SchemaName, string TableName, string ColumnName, string DataType, int MaxLength, int Precision, int Scale, bool IsNullable)>(columnsQuery))
			.Where(c => !objectNamesToIgnore.Contains(c.TableName))
			.GroupBy(c => (c.SchemaName, c.TableName))
			.ToDictionary(
				g => g.Key,
				g => g.Select(c => new ColumnSchema(c.ColumnName, c.DataType, c.MaxLength, c.Precision, c.Scale, c.IsNullable)).OrderBy(c => c.Name).ToList());

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
				ic.key_ordinal AS KeyOrdinal
			FROM sys.tables t
			INNER JOIN sys.indexes i ON t.object_id = i.object_id
			INNER JOIN sys.index_columns ic ON i.object_id = ic.object_id AND i.index_id = ic.index_id
			INNER JOIN sys.columns c ON ic.object_id = c.object_id AND ic.column_id = c.column_id
			WHERE t.type = 'U' AND i.name IS NOT NULL
			ORDER BY SchemaName, t.name, i.name, ic.key_ordinal";

		var allIndexes = (await connection.QueryAsync<(string SchemaName, string TableName, string IndexName, string IndexType, bool IsUnique, bool IsPrimaryKey, string ColumnName, int KeyOrdinal)>(indexesQuery))
			.Where(i => !objectNamesToIgnore.Contains(i.TableName))
			.GroupBy(i => (i.SchemaName, i.TableName))
			.ToDictionary(
				g => g.Key,
				g => g.GroupBy(i => (i.IndexName, i.IndexType, i.IsUnique, i.IsPrimaryKey))
					.Select(ig =>
					{
						string description = BuildIndexDescription(ig.Key.IndexType, ig.Key.IsUnique, ig.Key.IsPrimaryKey);
						string keyColumns = string.Join(", ", ig.OrderBy(c => c.KeyOrdinal).Select(c => c.ColumnName));
						return new IndexSchema(ig.Key.IndexName, description, keyColumns);
					}).OrderBy(i => i.Keys).ThenBy(i => i.Name).ToList());

		// Query 4: Get all constraints (with schema)
		// Returns one row per constraint column via UNION ALL; column aggregation is done in C# for SQL Server 2012 compatibility.
		const string constraintsQuery = @"
			SELECT SCHEMA_NAME(t.schema_id) AS SchemaName, t.name AS TableName,
				CASE WHEN kc.type = 'PK' THEN 'PRIMARY KEY' ELSE 'UNIQUE' END AS ConstraintType,
				kc.name AS ConstraintName, c.name AS ColumnOrDefinition, ic.key_ordinal AS SortOrder
			FROM sys.tables t
			INNER JOIN sys.key_constraints kc ON t.object_id = kc.parent_object_id
			INNER JOIN sys.index_columns ic ON kc.parent_object_id = ic.object_id AND kc.unique_index_id = ic.index_id
			INNER JOIN sys.columns c ON ic.object_id = c.object_id AND ic.column_id = c.column_id
			WHERE t.type = 'U'
			UNION ALL
			SELECT SCHEMA_NAME(t.schema_id), t.name, 'FOREIGN KEY', fk.name,
				COL_NAME(fkc.parent_object_id, fkc.parent_column_id), fkc.constraint_column_id
			FROM sys.tables t
			INNER JOIN sys.foreign_keys fk ON t.object_id = fk.parent_object_id
			INNER JOIN sys.foreign_key_columns fkc ON fk.object_id = fkc.constraint_object_id
			WHERE t.type = 'U'
			UNION ALL
			SELECT SCHEMA_NAME(t.schema_id), t.name, 'DEFAULT', dc.name, dc.definition, 0
			FROM sys.tables t
			INNER JOIN sys.default_constraints dc ON t.object_id = dc.parent_object_id
			WHERE t.type = 'U'
			UNION ALL
			SELECT SCHEMA_NAME(t.schema_id), t.name, 'CHECK', cc.name, cc.definition, 0
			FROM sys.tables t
			INNER JOIN sys.check_constraints cc ON t.object_id = cc.parent_object_id
			WHERE t.type = 'U'";

		var allConstraints = (await connection.QueryAsync<(string SchemaName, string TableName, string ConstraintType, string ConstraintName, string? ColumnOrDefinition, int SortOrder)>(constraintsQuery))
			.Where(c => !objectNamesToIgnore.Contains(c.TableName))
			.GroupBy(c => (c.SchemaName, c.TableName))
			.ToDictionary(
				g => g.Key,
				g => g.GroupBy(c => (c.ConstraintType, c.ConstraintName))
					.Select(cg =>
					{
						string keyColumns = string.Join(", ", cg.OrderBy(c => c.SortOrder).Select(c => c.ColumnOrDefinition));
						return new ConstraintSchema(cg.Key.ConstraintType, keyColumns);
					}).OrderBy(c => c.Keys).ThenBy(c => c.Type).ToList());

		// Build table schemas
		var tables = new List<TableSchema>(tableInfos.Count);
		foreach (var (schemaName, tableName, identityColumn) in tableInfos)
		{
			var key = (schemaName, tableName);
			var columns = allColumns.GetValueOrDefault(key, new List<ColumnSchema>());
			var indexes = allIndexes.GetValueOrDefault(key, new List<IndexSchema>());
			var constraints = allConstraints.GetValueOrDefault(key, new List<ConstraintSchema>());

			tables.Add(new TableSchema(schemaName, tableName, columns, indexes, constraints, identityColumn));
		}

		return tables.OrderBy(t => t.SchemaName).ThenBy(t => t.Name).ToList();
	}

	private async Task<List<StoredProcedureSchema>> ExtractStoredProceduresAsync(SqlConnection connection, IReadOnlySet<string> objectNamesToIgnore)
	{
		// SQL Server 2016+ (major version 13+) removed the 8000-byte input limit on HASHBYTES.
		// On older versions, fall back to CHECKSUM which has no size limit but weaker collision resistance.
		// Collision resistance isn't critical here since this value is mixed into the overall SHA256 hash.
		var majorVersion = int.Parse(connection.ServerVersion.Split('.')[0]);
		var definitionHashExpr = majorVersion >= 13
			? "CONVERT(VARCHAR(64), HASHBYTES('SHA2_256', ISNULL(m.definition, '')), 2)"
			: "CONVERT(VARCHAR(40), CHECKSUM(ISNULL(m.definition, '')))";

		string allProcsQuery = $@"
			SELECT
				SCHEMA_NAME(p.schema_id) AS SchemaName,
				p.name AS ProcName,
				{definitionHashExpr} AS DefinitionHash
			FROM sys.procedures p
			LEFT JOIN sys.sql_modules m ON p.object_id = m.object_id
			WHERE p.type = 'P'
			ORDER BY SchemaName, p.name";

		var allProcs = (await connection.QueryAsync<(string SchemaName, string ProcName, string DefinitionHash)>(allProcsQuery))
			.Where(p => !objectNamesToIgnore.Contains(p.ProcName))
			.ToList();

		// Get parameters for procedures that have them (with schema)
		const string paramsQuery = @"
			SELECT
				SCHEMA_NAME(p.schema_id) AS SchemaName,
				p.name AS ProcName,
				pa.name AS ParamName,
				t.name AS TypeName,
				pa.max_length AS MaxLength,
				pa.precision AS Precision,
				pa.scale AS Scale,
				pa.is_nullable AS IsNullable
			FROM sys.procedures p
			INNER JOIN sys.parameters pa ON p.object_id = pa.object_id
			INNER JOIN sys.types t ON pa.user_type_id = t.user_type_id
			WHERE p.type = 'P'
			ORDER BY SchemaName, p.name, pa.parameter_id";

		var procsAndParams = await connection.QueryAsync<(string SchemaName, string ProcName, string ParamName, string TypeName, int MaxLength, int Precision, int Scale, bool IsNullable)>(paramsQuery);

		var paramsByProc = procsAndParams
			.Where(p => !objectNamesToIgnore.Contains(p.ProcName))
			.GroupBy(p => (p.SchemaName, p.ProcName))
			.ToDictionary(
				g => g.Key,
				g => g.Select(p => new ParameterSchema(
					p.ParamName.TrimStart('@'),
					p.TypeName,
					p.MaxLength,
					p.Precision,
					p.Scale,
					p.IsNullable
				)).ToList()
			);

		var procedures = allProcs
			.Select(proc => new StoredProcedureSchema(
				proc.SchemaName,
				proc.ProcName,
				paramsByProc.GetValueOrDefault((proc.SchemaName, proc.ProcName), new List<ParameterSchema>()),
				proc.DefinitionHash ?? ComputeEmptyDefinitionHash()
			))
			.OrderBy(p => p.SchemaName)
			.ThenBy(p => p.Name)
			.ToList();

		return procedures;
	}

	private async Task<List<UserDefinedTableTypeSchema>> ExtractUserDefinedTableTypesAsync(SqlConnection connection)
	{
		// Get UDTs with schema names
		const string udtQuery = @"
			SELECT
				SCHEMA_NAME(tt.schema_id) AS SchemaName,
				tt.name AS TableTypeName,
				c.name AS ColumnName,
				t.name AS DataType,
				c.max_length AS MaxLength,
				c.precision AS Precision,
				c.scale AS Scale,
				c.is_nullable AS IsNullable
			FROM sys.table_types tt
			INNER JOIN sys.columns c ON tt.type_table_object_id = c.object_id
			INNER JOIN sys.types t ON c.user_type_id = t.user_type_id
			ORDER BY SchemaName, tt.name, c.column_id";

		var udtData = await connection.QueryAsync<(string SchemaName, string TableTypeName, string ColumnName, string DataType, int MaxLength, int Precision, int Scale, bool IsNullable)>(udtQuery);

		var udts = udtData
			.GroupBy(u => (u.SchemaName, u.TableTypeName))
			.Select(g => new UserDefinedTableTypeSchema(
				g.First().SchemaName,
				g.Key.TableTypeName,
				g.Select(c => new ColumnSchema(c.ColumnName, c.DataType, c.MaxLength, c.Precision, c.Scale, c.IsNullable)).OrderBy(c => c.Name).ToList()
			))
			.OrderBy(u => u.SchemaName)
			.ThenBy(u => u.Name)
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
}
