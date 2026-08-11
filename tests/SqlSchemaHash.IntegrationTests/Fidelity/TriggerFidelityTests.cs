using zachtbeer.SqlSchemaHasher;
using Shouldly;

namespace SqlSchemaHash.IntegrationTests.Fidelity;

/// <summary>
/// DML trigger schema fidelity: presence, disabled state, INSTEAD OF vs AFTER, body-text changes,
/// FIRST/LAST ordering (set out-of-band via sp_settriggerorder), and the "trigger follows its
/// excluded parent" scoping rule.
/// </summary>
[TestClass]
public class TriggerFidelityTests : IntegrationTestBase
{
    [TestMethod]
    public async Task AddingTrigger_ChangesHash()
    {
        var dbName = await CreateTestDatabaseAsync("TrigAdd");
        await ExecuteSqlAsync(dbName, "CREATE TABLE T (Id INT NOT NULL)");
        var hashBefore = await ExtractAndHashAsync(dbName);

        await ExecuteSqlAsync(dbName, "CREATE TRIGGER trg_T ON T AFTER INSERT AS BEGIN SET NOCOUNT ON END");
        var hashAfter = await ExtractAndHashAsync(dbName);

        hashBefore.ShouldNotBe(hashAfter, "Adding a trigger must change the hash");
    }

    [TestMethod]
    public async Task IdenticalTrigger_AcrossDatabases_ProducesSameHash()
    {
        var db1 = await CreateTestDatabaseAsync("TrigSame1");
        var db2 = await CreateTestDatabaseAsync("TrigSame2");

        var ddl = new[] { "CREATE TABLE T (Id INT NOT NULL)", "CREATE TRIGGER trg_T ON T AFTER INSERT AS BEGIN SET NOCOUNT ON END" };
        foreach (var sql in ddl) await ExecuteSqlAsync(db1, sql);
        foreach (var sql in ddl) await ExecuteSqlAsync(db2, sql);

        (await ExtractAndHashAsync(db1)).ShouldBe(await ExtractAndHashAsync(db2), "Identical triggers must produce the same hash");
    }

    [TestMethod]
    public async Task DisablingTrigger_ChangesHash()
    {
        var dbName = await CreateTestDatabaseAsync("TrigDisable");
        await ExecuteSqlAsync(dbName, "CREATE TABLE T (Id INT NOT NULL)");
        await ExecuteSqlAsync(dbName, "CREATE TRIGGER trg_T ON T AFTER INSERT AS BEGIN SET NOCOUNT ON END");
        var hashBefore = await ExtractAndHashAsync(dbName);

        await ExecuteSqlAsync(dbName, "DISABLE TRIGGER trg_T ON T");
        var hashAfter = await ExtractAndHashAsync(dbName);

        hashBefore.ShouldNotBe(hashAfter, "Disabling a trigger must change the hash under Strict");

        (await ExtractSchemaAsync(dbName)).Triggers.Single(t => t.Name == "trg_T").IsDisabled.ShouldBeTrue();
    }

    [TestMethod]
    public async Task InsteadOfTrigger_OnView_IsCaptured()
    {
        var dbName = await CreateTestDatabaseAsync("TrigInsteadOf");
        await ExecuteSqlAsync(dbName, "CREATE TABLE T (Id INT NOT NULL)");
        await ExecuteSqlAsync(dbName, "CREATE VIEW VT AS SELECT Id FROM T");
        await ExecuteSqlAsync(dbName, "CREATE TRIGGER trg_VT ON VT INSTEAD OF INSERT AS BEGIN SET NOCOUNT ON END");

        var trigger = (await ExtractSchemaAsync(dbName)).Triggers.Single(t => t.Name == "trg_VT");
        trigger.IsInsteadOfTrigger.ShouldBeTrue("An INSTEAD OF trigger must be reported as such");
        trigger.ParentName.ShouldBe("VT", "An INSTEAD OF trigger's parent may be a view");
    }

    [TestMethod]
    public async Task TriggerBody_DifferentBodies_ProduceDifferentHashes_ButEqualUnderIgnoreBodyText()
    {
        var db1 = await CreateTestDatabaseAsync("TrigBody1");
        var db2 = await CreateTestDatabaseAsync("TrigBody2");

        await ExecuteSqlAsync(db1, "CREATE TABLE T (Id INT NOT NULL)");
        await ExecuteSqlAsync(db2, "CREATE TABLE T (Id INT NOT NULL)");
        await ExecuteSqlAsync(db1, "CREATE TRIGGER trg_T ON T AFTER INSERT AS BEGIN SET NOCOUNT ON END");
        await ExecuteSqlAsync(db2, "CREATE TRIGGER trg_T ON T AFTER INSERT AS BEGIN SET NOCOUNT OFF END");

        (await ExtractAndHashAsync(db1)).ShouldNotBe(await ExtractAndHashAsync(db2), "Different trigger bodies must change the hash under Strict");

        var ignoreBody = new SchemaHashOptions { Modules = ModuleNormalization.IgnoreBodyText };
        (await ExtractAndHashAsync(db1, ignoreBody)).ShouldBe(await ExtractAndHashAsync(db2, ignoreBody), "IgnoreBodyText must collapse a body-only difference (flags/events still hashed)");
    }

    [TestMethod]
    public async Task TriggerOrder_First_ChangesHash_AndIsCaptured()
    {
        var dbName = await CreateTestDatabaseAsync("TrigOrder");
        await ExecuteSqlAsync(dbName, "CREATE TABLE T (Id INT NOT NULL)");
        await ExecuteSqlAsync(dbName, "CREATE TRIGGER trg_A ON T AFTER INSERT AS BEGIN SET NOCOUNT ON END");
        await ExecuteSqlAsync(dbName, "CREATE TRIGGER trg_B ON T AFTER INSERT AS BEGIN SET NOCOUNT ON END");

        var hashBefore = await ExtractAndHashAsync(dbName);

        await ExecuteSqlAsync(dbName, "EXEC sp_settriggerorder @triggername = 'trg_A', @order = 'First', @stmttype = 'INSERT'");

        var hashAfter = await ExtractAndHashAsync(dbName);
        hashBefore.ShouldNotBe(hashAfter, "Setting FIRST ordering must change the hash");

        var trigger = (await ExtractSchemaAsync(dbName)).Triggers.Single(t => t.Name == "trg_A");
        trigger.Events.Single(e => e.Type == "INSERT").IsFirst.ShouldBeTrue("The FIRST-ordered trigger's INSERT event must be reported as IsFirst");
    }

    [TestMethod]
    public async Task ExcludingParentTable_AlsoExcludesItsTriggers()
    {
        var dbName = await CreateTestDatabaseAsync("TrigParentIgnore");
        await ExecuteSqlAsync(dbName, "CREATE TABLE dbo.T (Id INT NOT NULL)");
        await ExecuteSqlAsync(dbName, "CREATE TRIGGER trg_T ON dbo.T AFTER INSERT AS BEGIN SET NOCOUNT ON END");

        var options = new SchemaHashOptions { ObjectNamesToIgnore = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "T" } };
        var schema = await ExtractSchemaAsync(dbName, options);

        schema.Tables.Any(t => t.Name == "T").ShouldBeFalse("The excluded table must not appear");
        schema.Triggers.Any(t => t.Name == "trg_T").ShouldBeFalse("A trigger whose parent table is excluded must be excluded too, not leaked back into the hash");
    }
}
