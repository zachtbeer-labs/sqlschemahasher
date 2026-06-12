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
/// Represents a column's schema definition. For computed columns, IsComputed is true and
/// ComputedDefinition carries the formula (e.g. "([Price]*[Qty])"); IsPersisted reflects whether
/// the computed value is physically stored. Both are null/false for ordinary columns.
/// Collation carries the column's collation for string columns (e.g. "SQL_Latin1_General_CP1_CI_AS"),
/// or null for non-string columns that have no collation.
/// </summary>
public sealed record ColumnSchema(string Name, string DataType, int MaxLength, int Precision, int Scale, bool IsNullable, bool IsComputed = false, string? ComputedDefinition = null, bool IsPersisted = false, string? Collation = null);

/// <summary>
/// Represents an index definition. Keys contains the key columns in key order, with descending
/// key columns suffixed by " DESC" (ascending is implicit, e.g. "Name, Created DESC").
/// IncludedColumns contains non-key (INCLUDE) columns sorted by name, or null if there are none.
/// KeysWithoutDirection contains the key columns without direction suffixes; it is null when no
/// key column is descending (i.e. when it would be identical to Keys). A column literally named
/// "Foo DESC" is ambiguous with a descending "Foo" in this representation — the same ambiguity
/// class as column names containing ", ".
/// FilterDefinition carries the predicate of a filtered index (e.g. "([IsActive]=(1))"), or null
/// for an unfiltered index.
/// </summary>
public sealed record IndexSchema(string Name, string Description, string? Keys, string? IncludedColumns = null, string? KeysWithoutDirection = null, string? FilterDefinition = null);

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
/// Represents a stored procedure or function parameter. IsOutput is true for OUTPUT parameters;
/// IsReadonly is true for READONLY parameters (required for table-valued parameters).
/// </summary>
public sealed record ParameterSchema(string Name, string Type, int MaxLength, int Precision, int Scale, bool IsNullable, bool IsOutput = false, bool IsReadonly = false);

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
