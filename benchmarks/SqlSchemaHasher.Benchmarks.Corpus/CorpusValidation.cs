using zachtbeer.SqlSchemaHasher;

namespace SqlSchemaHasher.Benchmarks.Corpus;

/// <summary>
/// Verifies a built corpus matches the profile it claims to represent. An empty or under-built
/// <see cref="SchemaMetadata"/> hashes in nanoseconds and reads as a spectacular improvement, so
/// benchmarks call this in <c>[GlobalSetup]</c> to fail loudly instead of reporting a fast number.
/// </summary>
public static class CorpusValidation
{
    public static void Validate(SchemaMetadata schema, SchemaProfile profile)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(profile);

        Require("tables", schema.Tables.Count, profile.Tables, profile);
        Require("stored procedures", schema.StoredProcedures.Count, profile.StoredProcedures, profile);
        Require("table types", schema.UserDefinedTableTypes.Count, profile.TableTypes, profile);
        Require("views", schema.Views.Count, profile.Views, profile);
        Require("functions", schema.Functions.Count, profile.Functions, profile);
        Require("triggers", schema.Triggers.Count, profile.Triggers, profile);
        Require("sequences", schema.Sequences.Count, profile.Sequences, profile);
        Require("synonyms", schema.Synonyms.Count, profile.Synonyms, profile);
        Require("extended properties", schema.ExtendedProperties.Count, profile.ExtendedProperties, profile);
    }

    private static void Require(string kind, int actual, int expected, SchemaProfile profile)
    {
        if (actual != expected)
        {
            throw new InvalidOperationException($"Corpus for profile '{profile.Name}' has {actual} {kind}, expected {expected}. Benchmarking an under-built corpus produces meaningless results.");
        }
    }
}
