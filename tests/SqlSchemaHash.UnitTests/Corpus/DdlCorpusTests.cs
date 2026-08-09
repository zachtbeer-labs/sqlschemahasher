using SqlSchemaHasher.Benchmarks.Corpus;
using Shouldly;

namespace SqlSchemaHash.UnitTests.Corpus;

/// <summary>
/// The DDL renderer seeds the integration-tier database. These tests cover generation only —
/// that the script actually executes against SQL Server is proven by running the integration
/// benchmarks, which is a manual, Docker-dependent step.
/// </summary>
[TestClass]
public class DdlCorpusTests
{
    [TestMethod]
    public void Script_IsDeterministic()
    {
        var first = DdlCorpus.Script(SchemaProfile.Small);
        var second = DdlCorpus.Script(SchemaProfile.Small);

        first.ShouldBe(second, "Script generation must be deterministic so the seeded database is identical across runs");
    }

    [TestMethod]
    public void Script_CreatesEveryObjectKindInTheProfile()
    {
        var profile = SchemaProfile.Small;

        var batches = DdlCorpus.Script(profile);
        var script = string.Join("\n", batches);

        CountOccurrences(script, "CREATE TABLE ").ShouldBe(profile.Tables);
        CountOccurrences(script, "CREATE PROCEDURE ").ShouldBe(profile.StoredProcedures);
        CountOccurrences(script, "CREATE VIEW ").ShouldBe(profile.Views);
        CountOccurrences(script, "CREATE TRIGGER ").ShouldBe(profile.Triggers);
        CountOccurrences(script, "CREATE SEQUENCE ").ShouldBe(profile.Sequences);
        CountOccurrences(script, "CREATE SYNONYM ").ShouldBe(profile.Synonyms);
        CountOccurrences(script, "CREATE TYPE ").ShouldBe(profile.TableTypes);
    }

    [TestMethod]
    public void Script_PutsEachModuleCreationInItsOwnBatch()
    {
        // CREATE PROCEDURE/VIEW/FUNCTION/TRIGGER must each be the first statement in its batch.
        var batches = DdlCorpus.Script(SchemaProfile.Small);
        var moduleBatches = batches.Where(b => b.Contains("CREATE PROCEDURE ", StringComparison.Ordinal) || b.Contains("CREATE VIEW ", StringComparison.Ordinal) || b.Contains("CREATE TRIGGER ", StringComparison.Ordinal) || b.Contains("CREATE FUNCTION ", StringComparison.Ordinal)).ToList();

        moduleBatches.ShouldNotBeEmpty();
        foreach (var batch in moduleBatches)
        {
            batch.TrimStart().ShouldStartWith("CREATE ");
        }
    }

    [TestMethod]
    public void Script_ContainsNoBatchSeparatorKeyword()
    {
        // GO is an SSMS client directive; SqlCommand rejects it.
        var batches = DdlCorpus.Script(SchemaProfile.Small);

        foreach (var batch in batches)
        {
            batch.ShouldNotContain("\nGO", Case.Sensitive);
        }
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = haystack.IndexOf(needle, StringComparison.Ordinal);
        while (index >= 0)
        {
            count++;
            index = haystack.IndexOf(needle, index + needle.Length, StringComparison.Ordinal);
        }

        return count;
    }
}
