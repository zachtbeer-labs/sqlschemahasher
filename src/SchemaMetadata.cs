namespace zachtbeer.SqlSchemaHasher;

/// <summary>
/// Lightweight schema metadata representation for hash-based comparison.
/// Contains all schema elements needed to determine if two databases have identical schemas.
/// The records are a faithful, typed mirror of the SQL Server <c>sys.*</c> catalog: each field
/// corresponds to a catalog column rather than a pre-concatenated string, so the extractor is a
/// near-dumb column-to-field mapper and the calculator hashes structured values directly.
/// </summary>
public sealed record SchemaMetadata(List<TableSchema> Tables, List<StoredProcedureSchema> StoredProcedures, List<UserDefinedTableTypeSchema> UserDefinedTableTypes, List<ViewSchema> Views, List<FunctionSchema> Functions, List<TriggerSchema> Triggers, List<SequenceSchema> Sequences, List<SynonymSchema> Synonyms, List<ExtendedPropertySchema> ExtendedProperties);

/// <summary>
/// Represents a table's schema including columns, indexes, and constraints.
/// SchemaName + Name together form the fully-qualified table identifier.
/// Constraints are split into typed lists mirroring the distinct catalog views
/// (<c>sys.key_constraints</c>, <c>sys.foreign_keys</c>, <c>sys.check_constraints</c>,
/// <c>sys.default_constraints</c>).
/// When the table has an identity column, IdentitySeed and IdentityIncrement carry its
/// seed/increment (as decimal strings, e.g. "1000"/"5") and IdentityNotForReplication reflects
/// the NOT FOR REPLICATION flag. All three are null/false when there is no identity column.
/// For a system-versioned temporal table, HistoryTableName is the schema-qualified name of its
/// history table (the calculator normalizes SQL Server's auto-generated MSSQL_TemporalHistoryFor_&lt;id&gt;
/// names, whose object-id suffix is not deterministic across databases, into a name derived from the
/// versioned parent instead); HistoryRetentionPeriod and HistoryRetentionPeriodUnit carry a finite
/// retention policy (e.g. 6 / "MONTH"), both null for the INFINITE default or on servers without
/// retention support. All are null for non-temporal tables.
/// VersionedParentSchema/VersionedParentName are populated only on a history table itself (any
/// TemporalType of "HISTORY_TABLE", whether the history table was named explicitly or left anonymous):
/// they identify the versioned table this one stores history for (the reverse of HistoryTableName's
/// linkage). Both null for every other table, including the versioned table itself. The raw catalog
/// Name is always preserved here — the calculator, not the extractor, substitutes an effective name
/// for an auto-named history table and its auto-created index.
/// </summary>
public sealed record TableSchema(string SchemaName, string Name, List<ColumnSchema> Columns, List<IndexSchema> Indexes, List<KeyConstraintSchema> KeyConstraints, List<ForeignKeyConstraintSchema> ForeignKeys, List<CheckConstraintSchema> CheckConstraints, List<DefaultConstraintSchema> DefaultConstraints, string? IdentityColumn, string? IdentitySeed = null, string? IdentityIncrement = null, bool IdentityNotForReplication = false, string? TemporalType = null, bool IsMemoryOptimized = false, string? DurabilityDesc = null, string? HistoryTableName = null, int? HistoryRetentionPeriod = null, string? HistoryRetentionPeriodUnit = null, string? VersionedParentSchema = null, string? VersionedParentName = null)
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
/// CollationName carries the column's collation for string columns (e.g. "SQL_Latin1_General_CP1_CI_AS"),
/// or null for non-string columns that have no collation.
/// ColumnId is the column's 1-based position (sys.columns.column_id); it drives ordering so that
/// two tables/types differing only in column order hash differently, but the ordinal number itself
/// is not hashed (so an unrelated column drop that leaves a gap in column_id does not spuriously
/// change the hash). IsSparse and IsRowGuidCol carry the SPARSE and ROWGUIDCOL storage markers.
/// IsFilestream carries the FILESTREAM storage marker. IsMasked/MaskingFunction carry Dynamic Data
/// Masking state (masking_function is null when the column is not masked). EncryptionTypeDesc carries the
/// Always Encrypted encryption_type_desc (e.g. "DETERMINISTIC"/"RANDOMIZED"), or null when the column
/// is not encrypted. XmlSchemaCollectionName carries the schema-qualified name of the bound XML schema
/// collection for a typed xml column (null for untyped xml and non-xml columns); IsXmlDocument is the
/// DOCUMENT (true) vs CONTENT (false) facet of a typed xml column.
/// DataType is schema-qualified for user-defined types (e.g. "dbo.IntList") and a bare name for
/// built-in types (e.g. "int").
/// GeneratedAlwaysType is the temporal/ledger period marker (sys.columns.generated_always_type_desc,
/// e.g. "NOT_APPLICABLE", "AS_ROW_START", "AS_ROW_END"); IsHidden reflects a HIDDEN period column.
/// IsAnsiPadded is sys.columns.is_ansi_padded: for varchar/binary columns it records whether the
/// column was created under SET ANSI_PADDING ON, which changes trailing-space/zero storage semantics.
/// </summary>
public sealed record ColumnSchema(string Name, string DataType, int MaxLength, int Precision, int Scale, bool IsNullable, bool IsComputed = false, string? ComputedDefinition = null, bool IsPersisted = false, string? CollationName = null, int ColumnId = 0, bool IsSparse = false, bool IsRowGuidCol = false, bool IsFilestream = false, bool IsMasked = false, string? MaskingFunction = null, string? EncryptionTypeDesc = null, string? XmlSchemaCollectionName = null, bool IsXmlDocument = false, string? GeneratedAlwaysType = null, bool IsHidden = false, bool IsAnsiPadded = true);

/// <summary>
/// Represents a single index key column and its sort direction. Included (non-key) columns are not
/// represented here — they carry no direction and live in <see cref="IndexSchema.IncludedColumns"/>.
/// </summary>
public sealed record IndexKeyColumn(string Name, bool IsDescendingKey);

/// <summary>
/// Represents an index definition mirroring <c>sys.indexes</c> + <c>sys.index_columns</c>.
/// TypeDesc is the raw <c>type_desc</c> (e.g. "CLUSTERED", "NONCLUSTERED", "CLUSTERED COLUMNSTORE",
/// "NONCLUSTERED COLUMNSTORE"); the calculator normalizes it only when IndexNormalization.NormalizeClustering is set,
/// and even then preserves the COLUMNSTORE distinction. KeyColumns are the key columns in key order,
/// each carrying its ASC/DESC direction. IncludedColumns are the non-key INCLUDE columns (an unordered
/// set, sorted by name). IsDisabled reflects a disabled index; IgnoreDupKey reflects IGNORE_DUP_KEY on
/// a unique index. FilterDefinition carries the predicate of a filtered index (e.g. "([IsActive]=(1))"),
/// or null for an unfiltered index. FillFactor / IsPadded / AllowRowLocks / AllowPageLocks are the
/// physical storage and locking options (WITH (FILLFACTOR=…, PAD_INDEX=…, ALLOW_ROW_LOCKS=…,
/// ALLOW_PAGE_LOCKS=…)); FillFactor 0 means the server default. Defaults mirror a plain index created
/// without explicit options (fill factor 0, unpadded, both lock granularities allowed).
/// </summary>
public sealed record IndexSchema(string Name, string TypeDesc, bool IsUnique, bool IsUniqueConstraint, bool IsPrimaryKey, bool IsDisabled, bool IgnoreDupKey, List<IndexKeyColumn> KeyColumns, List<string> IncludedColumns, string? FilterDefinition = null, byte FillFactor = 0, bool IsPadded = false, bool AllowRowLocks = true, bool AllowPageLocks = true);

/// <summary>
/// Represents a PRIMARY KEY or UNIQUE constraint (mirrors <c>sys.key_constraints</c>). Type is
/// "PRIMARY KEY" or "UNIQUE". KeyColumns are the constraint's key columns in key order with direction.
/// Name participates in the hash unless <see cref="ConstraintNormalization.IgnoreNames"/> is set.
/// The clustering of the backing index is captured on the corresponding <see cref="IndexSchema"/>
/// (a PK/UNIQUE index has a name and so appears in the table's index list as well).
/// </summary>
public sealed record KeyConstraintSchema(string Type, string Name, bool IsSystemNamed, List<IndexKeyColumn> KeyColumns);

/// <summary>
/// Represents one referencing→referenced column pair in a foreign key (mirrors a
/// <c>sys.foreign_key_columns</c> row). The referenced table is constant across a foreign key and so
/// lives on <see cref="ForeignKeyConstraintSchema"/>, not here.
/// </summary>
public sealed record ForeignKeyColumnPair(string ParentColumn, string ReferencedColumn);

/// <summary>
/// Represents a FOREIGN KEY constraint (mirrors <c>sys.foreign_keys</c> + <c>sys.foreign_key_columns</c>).
/// ReferencedSchema + ReferencedTable are the schema-qualified target table (constant per key).
/// ColumnPairs are the referencing→referenced column pairs in constraint column order. DeleteAction and
/// UpdateAction are the referential action descriptions (e.g. "NO_ACTION", "CASCADE"). IsDisabled and
/// IsNotTrusted reflect the enforcement state (NOCHECK CONSTRAINT / WITH NOCHECK).
/// </summary>
public sealed record ForeignKeyConstraintSchema(string Name, string ReferencedSchema, string ReferencedTable, List<ForeignKeyColumnPair> ColumnPairs, string DeleteAction, string UpdateAction, bool IsDisabled, bool IsNotTrusted, bool IsNotForReplication = false, bool IsSystemNamed = false);

/// <summary>
/// Represents a CHECK constraint (mirrors <c>sys.check_constraints</c>). Definition is the predicate
/// text. IsDisabled and IsNotTrusted reflect the enforcement state (NOCHECK CONSTRAINT / WITH NOCHECK).
/// </summary>
public sealed record CheckConstraintSchema(string Name, string Definition, bool IsDisabled, bool IsNotTrusted, bool IsNotForReplication = false, bool IsSystemNamed = false);

/// <summary>
/// Represents a DEFAULT constraint (mirrors <c>sys.default_constraints</c>). ColumnName is the column
/// the default is bound to; Definition is the default expression text.
/// </summary>
public sealed record DefaultConstraintSchema(string Name, string ColumnName, string Definition, bool IsSystemNamed = false);

/// <summary>
/// Represents a stored procedure's schema including its parameters and definition hash.
/// SchemaName + Name together form the fully-qualified procedure identifier.
/// DefinitionHash captures changes to the procedure body that parameter list alone would miss.
/// </summary>
public sealed record StoredProcedureSchema(string SchemaName, string Name, List<ParameterSchema> Parameters, string DefinitionHash, bool UsesAnsiNulls = true, bool UsesQuotedIdentifier = true)
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
public sealed record ParameterSchema(string Name, string Type, int MaxLength, int Precision, int Scale, bool IsNullable, bool IsOutput = false, bool IsReadonly = false, string? XmlSchemaCollectionName = null, bool IsXmlDocument = false);

/// <summary>
/// Represents a user-defined table type: its columns plus the constraints and identity it may carry.
/// SchemaName + Name together form the fully-qualified type identifier. A table type can declare
/// PRIMARY KEY/UNIQUE, CHECK and DEFAULT constraints and an identity column (but not foreign keys),
/// all of which affect the type's validation and marshalling semantics and so participate in the hash.
/// </summary>
public sealed record UserDefinedTableTypeSchema(string SchemaName, string Name, List<ColumnSchema> Columns, List<KeyConstraintSchema> KeyConstraints, List<CheckConstraintSchema> CheckConstraints, List<DefaultConstraintSchema> DefaultConstraints, string? IdentityColumn = null, string? IdentitySeed = null, string? IdentityIncrement = null, bool IdentityNotForReplication = false, bool IsMemoryOptimized = false)
{
	/// <summary>
	/// Returns the fully-qualified name in [schema].[name] format.
	/// </summary>
	public string FullName => $"[{SchemaName}].[{Name}]";
}

/// <summary>
/// Represents a view's schema (mirrors <c>sys.views</c> + <c>sys.sql_modules</c>). Columns are
/// deliberately not extracted: the view's definition hash is its identity, and a <c>SELECT *</c>
/// view's column metadata is stale until <c>sp_refreshview</c> runs, which would otherwise inject
/// spurious diffs unrelated to any DDL change. Indexes carries indexed-view indexes (reusing
/// <see cref="IndexSchema"/> from tables) and is empty for an ordinary view. DefinitionHash is
/// server-computed (HASHBYTES/CHECKSUM) the same way as a stored procedure's; UsesAnsiNulls and
/// UsesQuotedIdentifier carry the SET options captured at CREATE time.
/// </summary>
public sealed record ViewSchema(string SchemaName, string Name, List<IndexSchema> Indexes, string DefinitionHash, bool UsesAnsiNulls = true, bool UsesQuotedIdentifier = true)
{
	/// <summary>
	/// Returns the fully-qualified name in [schema].[name] format.
	/// </summary>
	public string FullName => $"[{SchemaName}].[{Name}]";
}

/// <summary>
/// Represents a T-SQL function's schema (mirrors <c>sys.objects</c> + <c>sys.sql_modules</c>).
/// TypeDesc is the raw <c>sys.objects.type_desc</c> ("SQL_SCALAR_FUNCTION",
/// "SQL_INLINE_TABLE_VALUED_FUNCTION", "SQL_TABLE_VALUED_FUNCTION"), so scalar-vs-TVF identity
/// survives even under <see cref="ModuleNormalization.IgnoreBodyText"/>. For a scalar function, its
/// return type appears as the parameter_id = 0 row in Parameters (empty name, IsOutput true); a
/// TVF's return-table shape is covered only by DefinitionHash, not by a typed record here.
/// </summary>
public sealed record FunctionSchema(string SchemaName, string Name, string TypeDesc, List<ParameterSchema> Parameters, string DefinitionHash, bool UsesAnsiNulls = true, bool UsesQuotedIdentifier = true)
{
	/// <summary>
	/// Returns the fully-qualified name in [schema].[name] format.
	/// </summary>
	public string FullName => $"[{SchemaName}].[{Name}]";
}

/// <summary>
/// Represents one <c>sys.trigger_events</c> row: the DML event type (e.g. "INSERT", "UPDATE",
/// "DELETE") and its FIRST/LAST ordering. That ordering is set out-of-band via
/// <c>sp_settriggerorder</c> and exists only in the catalog, never in the trigger's body text.
/// </summary>
public sealed record TriggerEventSchema(string Type, bool IsFirst, bool IsLast);

/// <summary>
/// Represents a DML trigger's schema (mirrors <c>sys.triggers</c> + <c>sys.sql_modules</c>). The
/// parent may be a table or a view (for an INSTEAD OF trigger). ParentSchemaName/ParentName always
/// equal SchemaName/Name for a DML trigger (a trigger lives in its parent's schema), but are captured
/// separately since <c>sys.triggers</c> itself carries no <c>schema_id</c> — the record is honest
/// about what the catalog actually exposes. Events carries the trigger's DML event set together with
/// its FIRST/LAST ordering.
/// </summary>
public sealed record TriggerSchema(string SchemaName, string Name, string ParentSchemaName, string ParentName, bool IsDisabled, bool IsInsteadOfTrigger, bool IsNotForReplication, List<TriggerEventSchema> Events, string DefinitionHash, bool UsesAnsiNulls = true, bool UsesQuotedIdentifier = true)
{
	/// <summary>
	/// Returns the fully-qualified name in [schema].[name] format.
	/// </summary>
	public string FullName => $"[{SchemaName}].[{Name}]";
}

/// <summary>
/// Represents a sequence object's schema (mirrors <c>sys.sequences</c>). StartValue, Increment,
/// MinimumValue, and MaximumValue are decimal strings (the catalog exposes them as <c>sql_variant</c>,
/// the same technique used for identity seed/increment). CacheSize is null both for NO CACHE and for
/// the server-default cache size; IsCached disambiguates the two — together they encode NO CACHE /
/// default CACHE / CACHE n unambiguously. <c>current_value</c> is deliberately not captured here: it
/// is runtime state that advances on every <c>NEXT VALUE FOR</c>, not schema, and capturing it would
/// make the hash unstable across otherwise-identical databases.
/// </summary>
public sealed record SequenceSchema(string SchemaName, string Name, string DataType, byte Precision, string StartValue, string Increment, string MinimumValue, string MaximumValue, bool IsCycling, bool IsCached, int? CacheSize)
{
	/// <summary>
	/// Returns the fully-qualified name in [schema].[name] format.
	/// </summary>
	public string FullName => $"[{SchemaName}].[{Name}]";
}

/// <summary>
/// Represents a synonym (mirrors <c>sys.synonyms</c>). BaseObjectName is the target exactly as the
/// catalog stores it (<c>sys.synonyms.base_object_name</c>, possibly multi-part, possibly quoted);
/// synonym targets are not validated or resolved at CREATE time, so the stored text is deterministic
/// across databases and hashes verbatim.
/// </summary>
public sealed record SynonymSchema(string SchemaName, string Name, string BaseObjectName)
{
	/// <summary>
	/// Returns the fully-qualified name in [schema].[name] format.
	/// </summary>
	public string FullName => $"[{SchemaName}].[{Name}]";
}

/// <summary>
/// Represents one extended property (mirrors <c>sys.extended_properties</c>, e.g. <c>MS_Description</c>).
/// The catalog's (class, major_id, minor_id) target is resolved to names at extraction (ids are not
/// deterministic across databases): ClassDesc is the raw <c>class_desc</c> (DATABASE, SCHEMA,
/// OBJECT_OR_COLUMN, PARAMETER, INDEX, or TYPE for a user-defined table type); SchemaName/ObjectName
/// locate the owning object (both null for a database-scoped property, ObjectName null for a
/// schema-scoped one); SubObjectName is the column, parameter, or index name when the property targets
/// one (null for the object itself — ClassDesc disambiguates which kind it names). The <c>sql_variant</c>
/// value is captured as its base type name (ValueType, via <c>SQL_VARIANT_PROPERTY</c>) plus its
/// <c>NVARCHAR</c> rendering (Value); both are null when the stored value is NULL, which keeps a NULL
/// value distinct from an empty string. Properties on constraint objects are deliberately not captured:
/// system-generated constraint names are not deterministic across databases.
/// </summary>
public sealed record ExtendedPropertySchema(string ClassDesc, string? SchemaName, string? ObjectName, string? SubObjectName, string Name, string? ValueType, string? Value);
