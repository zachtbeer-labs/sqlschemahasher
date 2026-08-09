using SqlSchemaHasher.Benchmarks.Corpus;
using zachtbeer.SqlSchemaHasher;
using Shouldly;

namespace SqlSchemaHash.UnitTests.Corpus;

/// <summary>
/// The benchmark corpus underpins every committed performance number. If it drifts between runs,
/// historical results become incomparable; if it silently builds an empty schema, the calculator
/// hashes it in nanoseconds and the result reads as a spectacular improvement. These tests pin
/// both properties.
/// </summary>
[TestClass]
public class MetadataCorpusTests
{
    [TestMethod]
    public void Build_IsDeterministic_AcrossIndependentBuilds()
    {
        var first = MetadataCorpus.Build(SchemaProfile.Small);
        var second = MetadataCorpus.Build(SchemaProfile.Small);

        var firstHash = zachtbeer.SqlSchemaHasher.SqlSchemaHash.ComputeHash(first);
        var secondHash = zachtbeer.SqlSchemaHasher.SqlSchemaHash.ComputeHash(second);

        firstHash.ShouldBe(secondHash, "Two independent builds of the same profile must produce an identical hash, or committed benchmark history is meaningless");
    }

    [TestMethod]
    public void Build_ProducesObjectCountsMatchingTheProfile()
    {
        var profile = SchemaProfile.Small;

        var schema = MetadataCorpus.Build(profile);

        schema.Tables.Count.ShouldBe(profile.Tables);
        schema.StoredProcedures.Count.ShouldBe(profile.StoredProcedures);
        schema.Views.Count.ShouldBe(profile.Views);
        schema.Functions.Count.ShouldBe(profile.Functions);
        schema.Triggers.Count.ShouldBe(profile.Triggers);
        schema.Sequences.Count.ShouldBe(profile.Sequences);
        schema.Synonyms.Count.ShouldBe(profile.Synonyms);
        schema.UserDefinedTableTypes.Count.ShouldBe(profile.TableTypes);
        schema.ExtendedProperties.Count.ShouldBe(profile.ExtendedProperties);
    }

    [TestMethod]
    public void Build_PopulatesTableInternals()
    {
        var profile = SchemaProfile.Small;

        var schema = MetadataCorpus.Build(profile);
        var table = schema.Tables[0];

        table.Columns.Count.ShouldBe(profile.ColumnsPerTable);
        table.Indexes.Count.ShouldBe(profile.IndexesPerTable);
        table.KeyConstraints.ShouldNotBeEmpty();
        table.CheckConstraints.ShouldNotBeEmpty();
        table.DefaultConstraints.ShouldNotBeEmpty();
    }

    [TestMethod]
    public void Build_ProducesDistinctHashesPerProfile()
    {
        var small = zachtbeer.SqlSchemaHasher.SqlSchemaHash.ComputeHash(MetadataCorpus.Build(SchemaProfile.Small));
        var medium = zachtbeer.SqlSchemaHasher.SqlSchemaHash.ComputeHash(MetadataCorpus.Build(SchemaProfile.Medium));

        small.ShouldNotBe(medium, "Different profiles must produce genuinely different schemas");
    }

    [TestMethod]
    public void Validate_ThrowsWhenCountsDoNotMatchTheProfile()
    {
        var mismatched = MetadataCorpus.Build(SchemaProfile.Small);

        Should.Throw<InvalidOperationException>(() => CorpusValidation.Validate(mismatched, SchemaProfile.Medium));
    }

    [TestMethod]
    public void Validate_AcceptsAMatchingCorpus()
    {
        var schema = MetadataCorpus.Build(SchemaProfile.Small);

        Should.NotThrow(() => CorpusValidation.Validate(schema, SchemaProfile.Small));
    }

    [TestMethod]
    public void OnlyTables_RetainsTablesAndDropsEverythingElse()
    {
        var full = MetadataCorpus.Build(SchemaProfile.Small);

        var tablesOnly = MetadataCorpus.OnlyTables(full);

        tablesOnly.Tables.Count.ShouldBe(full.Tables.Count);
        tablesOnly.StoredProcedures.ShouldBeEmpty();
        tablesOnly.Views.ShouldBeEmpty();
        tablesOnly.Functions.ShouldBeEmpty();
        tablesOnly.Triggers.ShouldBeEmpty();
        tablesOnly.ExtendedProperties.ShouldBeEmpty();
    }

    [TestMethod]
    public void OnlyModules_RetainsProceduresViewsFunctionsAndTriggers()
    {
        var full = MetadataCorpus.Build(SchemaProfile.Small);

        var modulesOnly = MetadataCorpus.OnlyModules(full);

        modulesOnly.StoredProcedures.Count.ShouldBe(full.StoredProcedures.Count);
        modulesOnly.Views.Count.ShouldBe(full.Views.Count);
        modulesOnly.Functions.Count.ShouldBe(full.Functions.Count);
        modulesOnly.Triggers.Count.ShouldBe(full.Triggers.Count);
        modulesOnly.Tables.ShouldBeEmpty();
    }
}
