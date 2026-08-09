namespace SqlSchemaHasher.Benchmarks.Corpus;

/// <summary>
/// The schema names and index-to-schema mapping shared by <see cref="MetadataCorpus"/> and
/// <see cref="DdlCorpus"/>, so both renderers distribute objects across schemas identically for
/// the same profile.
/// </summary>
internal static class CorpusSchemas
{
    public static readonly string[] Names = ["dbo", "sales", "reporting", "staging"];

    public static string For(int index) => Names[index % Names.Length];
}
