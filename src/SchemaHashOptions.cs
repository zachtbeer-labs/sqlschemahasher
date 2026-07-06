namespace zachtbeer.SqlSchemaHasher;

/// <summary>
/// Configuration options for schema hash calculation.
/// A bare <c>new SchemaHashOptions()</c> is the neutral baseline: every domain at
/// <c>…Normalization.Strict</c> (everything compared exactly), nothing excluded.
///
/// Comparison behavior is grouped into five domain <c>[Flags]</c> enums — <see cref="Tables"/>,
/// <see cref="Columns"/>, <see cref="Indexes"/>, <see cref="Constraints"/>, <see cref="Modules"/> —
/// each carrying fine-grained loosening bits plus named combos (<c>Strict</c>, <c>Structural</c>).
/// Set a bit to remove a distinction from the hash, e.g.
/// <c>new SchemaHashOptions { Indexes = IndexNormalization.IgnoreFillFactor | IndexNormalization.IgnoreLockOptions }</c>,
/// or take a whole-domain combo, e.g. <c>Indexes = IndexNormalization.Structural</c>. Use the static
/// presets (<see cref="V1"/>, <see cref="V2"/>, <see cref="Structural"/>) for common configurations.
///
/// Object <em>scoping</em> (which objects are compared at all) is separate from the normalization
/// enums: see <see cref="SchemaFilter"/>, <see cref="ObjectNamesToIgnore"/>, <see cref="IgnoreSysDiagramObjects"/>,
/// <see cref="IgnoreExtendedProperties"/>.
/// </summary>
public sealed class SchemaHashOptions
{
	/// <summary>
	/// Object names belonging to SSMS database diagrams (sysdiagrams table and related helper procs),
	/// excluded from extraction when <see cref="IgnoreSysDiagramObjects"/> is set.
	/// </summary>
	internal static readonly IReadOnlySet<string> SysDiagramObjectNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
	{
		"sysdiagrams",
		"fn_diagramobjects",
		"sp_alterdiagram",
		"sp_creatediagram",
		"sp_dropdiagram",
		"sp_helpdiagramdefinition",
		"sp_helpdiagrams",
		"sp_renamediagram"
	};

	/// <summary>
	/// Recommended defaults. Alias for <see cref="V2"/>.
	/// </summary>
	public static SchemaHashOptions Default => V2;

	/// <summary>
	/// Comparison semantics matching v1 of this library: exact index/constraint names, exact clustering
	/// type, index key sort order not compared (v1 never captured it), stored procedure text included,
	/// SSMS diagram objects ignored. Note: hashes still differ from package v1.x output because
	/// v2 extraction fidelity fixes apply unconditionally.
	/// </summary>
	public static SchemaHashOptions V1 => new() { IgnoreSysDiagramObjects = true, Indexes = IndexNormalization.IgnoreSortOrder };

	/// <summary>
	/// Recommended v2 defaults: everything compared exactly (including index key sort order),
	/// SSMS diagram objects ignored.
	/// </summary>
	public static SchemaHashOptions V2 => new() { IgnoreSysDiagramObjects = true };

	/// <summary>
	/// Opinionated structural comparison: answers "is the schema logically the same?" by ignoring
	/// naming and physical layout noise — clustered vs nonclustered, index key sort order, index and
	/// constraint names entirely, table column order, and SSMS diagram objects. Columns, key column
	/// sets, included columns, uniqueness/primary-key-ness, constraint definitions, identity columns,
	/// and stored procedures are still compared.
	/// </summary>
	public static SchemaHashOptions Structural => new()
	{
		IgnoreSysDiagramObjects = true,
		Tables = TableNormalization.Structural,
		Indexes = IndexNormalization.Structural,
		Constraints = ConstraintNormalization.Structural,
	};

	/// <summary>
	/// Table-level normalization (column order, identity seed/NFR, temporal retention).
	/// Default: <see cref="TableNormalization.Strict"/> (everything compared exactly).
	/// </summary>
	public TableNormalization Tables { get; set; } = TableNormalization.Strict;

	/// <summary>
	/// Column-level normalization (collation, ANSI padding, dynamic data masking).
	/// Default: <see cref="ColumnNormalization.Strict"/> (everything compared exactly).
	/// </summary>
	public ColumnNormalization Columns { get; set; } = ColumnNormalization.Strict;

	/// <summary>
	/// Index normalization (names, clustering, key sort order, fill factor, pad index, lock options, disabled state).
	/// Default: <see cref="IndexNormalization.Strict"/> (everything compared exactly).
	/// </summary>
	public IndexNormalization Indexes { get; set; } = IndexNormalization.Strict;

	/// <summary>
	/// Constraint normalization (names, FK/CHECK disabled/trust/NOT-FOR-REPLICATION enforcement state).
	/// Default: <see cref="ConstraintNormalization.Strict"/> (everything compared exactly).
	/// </summary>
	public ConstraintNormalization Constraints { get; set; } = ConstraintNormalization.Strict;

	/// <summary>
	/// Programmable-module normalization (stored procedure body text, CREATE-time SET options).
	/// Default: <see cref="ModuleNormalization.Strict"/> (body text and SET options compared exactly).
	/// </summary>
	public ModuleNormalization Modules { get; set; } = ModuleNormalization.Strict;

	/// <summary>
	/// Object names to exclude from schema extraction. An entry may be a <em>bare</em> name (e.g.
	/// <c>Orders</c>), which matches an object of that name in <em>any</em> schema, or
	/// <em>schema-qualified</em> (e.g. <c>sales.Orders</c>), which matches only that schema's object —
	/// prefer the qualified form to avoid unintentionally excluding a same-named object in another schema.
	/// Matching uses <see cref="ObjectNameComparer"/> (case-insensitive by default), independent of the
	/// comparer of the set assigned here. Default: empty (no objects excluded). Composes additively with
	/// <see cref="IgnoreSysDiagramObjects"/>.
	/// </summary>
	public IReadOnlySet<string> ObjectNamesToIgnore { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

	/// <summary>
	/// Equality comparer used to match <see cref="ObjectNamesToIgnore"/> entries against object names
	/// (bare and schema-qualified). Default: <see cref="StringComparer.OrdinalIgnoreCase"/>. Assign
	/// <see cref="StringComparer.Ordinal"/> for case-sensitive matching. The library applies this
	/// comparer itself, so matching no longer depends on the comparer of the assigned
	/// <see cref="ObjectNamesToIgnore"/> set.
	/// </summary>
	public StringComparer ObjectNameComparer { get; set; } = StringComparer.OrdinalIgnoreCase;

	/// <summary>
	/// When true, excludes SSMS database diagram objects (sysdiagrams table and related helper procs)
	/// from schema extraction, in addition to any names in <see cref="ObjectNamesToIgnore"/>.
	/// Default: false. Enabled by all presets (<see cref="V1"/>, <see cref="V2"/>, <see cref="Structural"/>).
	/// </summary>
	public bool IgnoreSysDiagramObjects { get; set; } = false;

	/// <summary>
	/// When true, extended properties (<c>sys.extended_properties</c>, e.g. <c>MS_Description</c>) are
	/// not extracted and do not affect the hash. Default: false — they are compared exactly, matching
	/// the everything-exact baseline; two databases differing only in an extended property (name, value,
	/// or value type) hash differently. No preset sets this: opt in if you consider extended properties
	/// documentation noise rather than schema.
	/// </summary>
	public bool IgnoreExtendedProperties { get; set; } = false;

	/// <summary>
	/// Filter to include only objects from specific schema(s).
	/// When null (default), all schemas are included.
	/// Examples: "dbo", "sales", "reporting"
	/// </summary>
	public string? SchemaFilter { get; set; } = null;
}
