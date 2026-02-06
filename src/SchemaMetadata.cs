namespace zachtbeer.SqlSchemaHasher;

/// <summary>
/// Lightweight schema metadata representation for hash-based comparison.
/// Contains all schema elements needed to determine if two databases have identical schemas.
/// </summary>
public sealed record SchemaMetadata(List<TableSchema> Tables, List<StoredProcedureSchema> StoredProcedures, List<UserDefinedTableTypeSchema> UserDefinedTableTypes);

/// <summary>
/// Represents a table's schema including columns, indexes, and constraints.
/// SchemaName + Name together form the fully-qualified table identifier.
/// </summary>
public sealed record TableSchema(string SchemaName, string Name, List<ColumnSchema> Columns, List<IndexSchema> Indexes, List<ConstraintSchema> Constraints, string? IdentityColumn)
{
	/// <summary>
	/// Returns the fully-qualified name in [schema].[name] format.
	/// </summary>
	public string FullName => $"[{SchemaName}].[{Name}]";
}

/// <summary>
/// Represents a column's schema definition.
/// </summary>
public sealed record ColumnSchema(string Name, string DataType, int MaxLength, int Precision, int Scale, bool IsNullable);

/// <summary>
/// Represents an index definition.
/// </summary>
public sealed record IndexSchema(string Name, string Description, string? Keys);

/// <summary>
/// Represents a constraint definition (PRIMARY KEY, FOREIGN KEY, UNIQUE, CHECK, DEFAULT).
/// </summary>
public sealed record ConstraintSchema(string Type, string? Keys);

/// <summary>
/// Represents a stored procedure's schema including its parameters and definition hash.
/// SchemaName + Name together form the fully-qualified procedure identifier.
/// DefinitionHash captures changes to the procedure body that parameter list alone would miss.
/// </summary>
public sealed record StoredProcedureSchema(string SchemaName, string Name, List<ParameterSchema> Parameters, string DefinitionHash)
{
	/// <summary>
	/// Returns the fully-qualified name in [schema].[name] format.
	/// </summary>
	public string FullName => $"[{SchemaName}].[{Name}]";
}

/// <summary>
/// Represents a stored procedure or function parameter.
/// </summary>
public sealed record ParameterSchema(string Name, string Type, int MaxLength, int Precision, int Scale, bool IsNullable);

/// <summary>
/// Represents a user-defined table type and its columns.
/// SchemaName + Name together form the fully-qualified type identifier.
/// </summary>
public sealed record UserDefinedTableTypeSchema(string SchemaName, string Name, List<ColumnSchema> Columns)
{
	/// <summary>
	/// Returns the fully-qualified name in [schema].[name] format.
	/// </summary>
	public string FullName => $"[{SchemaName}].[{Name}]";
}
