namespace SqlSchemaHasher.Benchmarks.Corpus;

/// <summary>
/// Describes the shape of a synthetic database. One profile drives both renderers:
/// <see cref="MetadataCorpus"/> (in-memory, for the calculator tier) and <c>DdlCorpus</c>
/// (T-SQL, for the integration tier), so both tiers measure the same shape.
/// </summary>
public sealed record SchemaProfile(string Name, int Tables, int ColumnsPerTable, int IndexesPerTable, int ForeignKeysPerTable, int StoredProcedures, int ParametersPerModule, int Views, int Functions, int Triggers, int Sequences, int Synonyms, int TableTypes, int ExtendedProperties)
{
    /// <summary>A typical small application database.</summary>
    public static SchemaProfile Small { get; } = new("Small", Tables: 25, ColumnsPerTable: 8, IndexesPerTable: 2, ForeignKeysPerTable: 1, StoredProcedures: 50, ParametersPerModule: 3, Views: 10, Functions: 5, Triggers: 5, Sequences: 2, Synonyms: 2, TableTypes: 3, ExtendedProperties: 25);

    /// <summary>The headline profile: roughly 200 tables and 500 stored procedures.</summary>
    public static SchemaProfile Medium { get; } = new("Medium", Tables: 200, ColumnsPerTable: 12, IndexesPerTable: 3, ForeignKeysPerTable: 2, StoredProcedures: 500, ParametersPerModule: 5, Views: 75, Functions: 40, Triggers: 40, Sequences: 10, Synonyms: 10, TableTypes: 15, ExtendedProperties: 200);

    /// <summary>An enterprise upper bound.</summary>
    public static SchemaProfile Large { get; } = new("Large", Tables: 1000, ColumnsPerTable: 16, IndexesPerTable: 4, ForeignKeysPerTable: 2, StoredProcedures: 2000, ParametersPerModule: 6, Views: 300, Functions: 150, Triggers: 150, Sequences: 25, Synonyms: 25, TableTypes: 50, ExtendedProperties: 1000);

    public static IReadOnlyList<SchemaProfile> All { get; } = [Small, Medium, Large];

    /// <summary>
    /// BenchmarkDotNet renders parameters via <c>ToString()</c> in every results row; the
    /// record-generated version would print all fourteen properties. Return the name only.
    /// </summary>
    public override string ToString() => Name;
}
