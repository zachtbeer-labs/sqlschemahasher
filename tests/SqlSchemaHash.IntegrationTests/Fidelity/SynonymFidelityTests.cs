using Shouldly;

namespace SqlSchemaHash.IntegrationTests.Fidelity;

/// <summary>
/// Synonym schema fidelity: presence and target text must be reflected in the hash. Synonym targets
/// are not validated or resolved at CREATE time, so a synonym pointing at a nonexistent object still
/// extracts and hashes deterministically.
/// </summary>
[TestClass]
public class SynonymFidelityTests : IntegrationTestBase
{
    [TestMethod]
    public async Task AddingSynonym_ChangesHash()
    {
        var dbName = await CreateTestDatabaseAsync("SynAdd");
        await ExecuteSqlAsync(dbName, "CREATE TABLE dbo.T (Id INT NOT NULL)");
        var hashBefore = await ExtractAndHashAsync(dbName);

        await ExecuteSqlAsync(dbName, "CREATE SYNONYM dbo.SynT FOR dbo.T");
        var hashAfter = await ExtractAndHashAsync(dbName);

        hashBefore.ShouldNotBe(hashAfter, "Adding a synonym must change the hash");
    }

    [TestMethod]
    public async Task IdenticalSynonym_AcrossDatabases_ProducesSameHash()
    {
        var db1 = await CreateTestDatabaseAsync("SynSame1");
        var db2 = await CreateTestDatabaseAsync("SynSame2");

        var ddl = new[] { "CREATE TABLE dbo.T (Id INT NOT NULL)", "CREATE SYNONYM dbo.SynT FOR dbo.T" };
        foreach (var sql in ddl) await ExecuteSqlAsync(db1, sql);
        foreach (var sql in ddl) await ExecuteSqlAsync(db2, sql);

        (await ExtractAndHashAsync(db1)).ShouldBe(await ExtractAndHashAsync(db2), "Identical synonyms must produce the same hash");
    }

    [TestMethod]
    public async Task ChangingTarget_ChangesHash()
    {
        var dbName = await CreateTestDatabaseAsync("SynRetarget");
        await ExecuteSqlAsync(dbName, "CREATE TABLE dbo.T1 (Id INT NOT NULL)");
        await ExecuteSqlAsync(dbName, "CREATE TABLE dbo.T2 (Id INT NOT NULL)");
        await ExecuteSqlAsync(dbName, "CREATE SYNONYM dbo.SynT FOR dbo.T1");

        var hashBefore = await ExtractAndHashAsync(dbName);

        await ExecuteSqlAsync(dbName, "DROP SYNONYM dbo.SynT");
        await ExecuteSqlAsync(dbName, "CREATE SYNONYM dbo.SynT FOR dbo.T2");

        var hashAfter = await ExtractAndHashAsync(dbName);
        hashBefore.ShouldNotBe(hashAfter, "Re-pointing a synonym at a different target must change the hash");
    }

    [TestMethod]
    public async Task SynonymTargetingNonexistentObject_StillExtractsDeterministically()
    {
        var db1 = await CreateTestDatabaseAsync("SynDangling1");
        var db2 = await CreateTestDatabaseAsync("SynDangling2");

        const string ddl = "CREATE SYNONYM dbo.SynGhost FOR dbo.DoesNotExist";
        await ExecuteSqlAsync(db1, ddl);
        await ExecuteSqlAsync(db2, ddl);

        var synonym = (await ExtractSchemaAsync(db1)).Synonyms.Single();
        synonym.BaseObjectName.ShouldBe("[dbo].[DoesNotExist]", "A synonym's target is captured verbatim (as SQL Server itself normalizes it) without validation");

        (await ExtractAndHashAsync(db1)).ShouldBe(await ExtractAndHashAsync(db2), "A synonym targeting a nonexistent object must still hash deterministically");
    }
}
