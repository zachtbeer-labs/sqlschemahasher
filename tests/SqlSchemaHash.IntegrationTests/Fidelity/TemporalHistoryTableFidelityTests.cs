using Dapper;
using Microsoft.Data.SqlClient;
using zachtbeer.SqlSchemaHasher;
using Shouldly;

namespace SqlSchemaHash.IntegrationTests.Fidelity;

/// <summary>
/// A system-versioned temporal table created without an explicit HISTORY_TABLE gets an anonymous
/// history table named MSSQL_TemporalHistoryFor_&lt;object_id&gt;, with an auto-created clustered index
/// ix_MSSQL_TemporalHistoryFor_&lt;object_id&gt;. Both names embed the parent's catalog object_id, which
/// is not stable across databases. The calculator substitutes an effective name derived from the
/// versioned parent (see SchemaHashCalculator.EffectiveTableName / HistoryIndexNameRewrite) so two
/// databases built from identical DDL hash identically despite their raw object_ids diverging.
///
/// Two freshly created databases tend to allocate the same object_ids, which would mask this bug, so
/// the determinism tests below diverge db2's allocation counter first (create + drop a throwaway
/// table) before running the shared script.
/// </summary>
[TestClass]
public class TemporalHistoryTableFidelityTests : IntegrationTestBase
{
    private const string AnonymousTemporalScript = @"
        CREATE TABLE dbo.Account (
            Id INT NOT NULL CONSTRAINT PK_Account PRIMARY KEY,
            ValidFrom DATETIME2 GENERATED ALWAYS AS ROW START NOT NULL,
            ValidTo DATETIME2 GENERATED ALWAYS AS ROW END NOT NULL,
            PERIOD FOR SYSTEM_TIME (ValidFrom, ValidTo)
        ) WITH (SYSTEM_VERSIONING = ON)";

    [TestMethod]
    public async Task AnonymousHistoryTable_CrossDatabase_HashesIdentically_UnderEveryPreset()
    {
        var db1Name = await CreateTestDatabaseAsync("AnonHist1");
        var db2Name = await CreateTestDatabaseAsync("AnonHist2");

        // Diverge db2's object_id allocation counter before running the identical script, so the two
        // anonymous history tables do NOT coincidentally land on the same object_id (which would mask
        // the bug this test guards against).
        await ExecuteSqlAsync(db2Name, "CREATE TABLE dbo.Throwaway (Id INT); DROP TABLE dbo.Throwaway;");

        await ExecuteSqlAsync(db1Name, AnonymousTemporalScript);
        await ExecuteSqlAsync(db2Name, AnonymousTemporalScript);

        var schema1 = await ExtractSchemaAsync(db1Name);
        var schema2 = await ExtractSchemaAsync(db2Name);

        // Guard the precondition: the two databases really did diverge their history tables' raw names
        // (otherwise this test would pass even without the fix).
        var history1 = schema1.Tables.Single(t => t.TemporalType == "HISTORY_TABLE");
        var history2 = schema2.Tables.Single(t => t.TemporalType == "HISTORY_TABLE");
        history1.Name.ShouldNotBe(history2.Name, "The two databases must have diverged raw history-table names for this test to be meaningful");

        zachtbeer.SqlSchemaHasher.SqlSchemaHash.ComputeHash(schema1, new SchemaHashOptions()).ShouldBe(zachtbeer.SqlSchemaHasher.SqlSchemaHash.ComputeHash(schema2, new SchemaHashOptions()), "Identical DDL must hash identically under the all-Strict baseline despite diverged object_ids");
        zachtbeer.SqlSchemaHasher.SqlSchemaHash.ComputeHash(schema1, SchemaHashOptions.Default).ShouldBe(zachtbeer.SqlSchemaHasher.SqlSchemaHash.ComputeHash(schema2, SchemaHashOptions.Default), "Identical DDL must hash identically under Default");
        zachtbeer.SqlSchemaHasher.SqlSchemaHash.ComputeHash(schema1, SchemaHashOptions.Structural).ShouldBe(zachtbeer.SqlSchemaHasher.SqlSchemaHash.ComputeHash(schema2, SchemaHashOptions.Structural), "Identical DDL must hash identically under Structural");
    }

    [TestMethod]
    public async Task AnonymousHistoryTable_VsExplicitlyNamed_ChangesHash()
    {
        var db1Name = await CreateTestDatabaseAsync("AnonVsExplicit1");
        var db2Name = await CreateTestDatabaseAsync("AnonVsExplicit2");

        await ExecuteSqlAsync(db1Name, AnonymousTemporalScript);
        await ExecuteSqlAsync(db2Name, @"
            CREATE TABLE dbo.Account (
                Id INT NOT NULL CONSTRAINT PK_Account PRIMARY KEY,
                ValidFrom DATETIME2 GENERATED ALWAYS AS ROW START NOT NULL,
                ValidTo DATETIME2 GENERATED ALWAYS AS ROW END NOT NULL,
                PERIOD FOR SYSTEM_TIME (ValidFrom, ValidTo)
            ) WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = dbo.Account_History))");

        (await ExtractAndHashAsync(db1Name)).ShouldNotBe(await ExtractAndHashAsync(db2Name), "An explicitly-chosen history table name is real schema and must not collapse to the same effective identity as an anonymous one");
    }

    [TestMethod]
    public async Task AnonymousHistoryTable_ExtraIndex_ChangesHash()
    {
        var db1Name = await CreateTestDatabaseAsync("HistIdx1");
        var db2Name = await CreateTestDatabaseAsync("HistIdx2");

        await ExecuteSqlAsync(db1Name, AnonymousTemporalScript);
        await ExecuteSqlAsync(db2Name, AnonymousTemporalScript);

        await using (var connection = new SqlConnection(GetConnectionString(db2Name)))
        {
            await connection.OpenAsync();
            var historyTableName = await connection.QuerySingleAsync<string>("SELECT name FROM sys.tables WHERE temporal_type = 1");
            await connection.ExecuteAsync($"CREATE INDEX IX_History_ValidFrom ON dbo.[{historyTableName}](ValidFrom)");
        }

        (await ExtractAndHashAsync(db1Name)).ShouldNotBe(await ExtractAndHashAsync(db2Name), "An extra index on the history table is real structure and must still change the hash after the history table's own identity is normalized");
    }

    [TestMethod]
    public async Task TwoAnonymousHistoryTables_CrossDatabase_HashIdentically()
    {
        // Exercises the reverse join and sort order with two anonymous history tables mapped in one schema.
        var db1Name = await CreateTestDatabaseAsync("MultiAnon1");
        var db2Name = await CreateTestDatabaseAsync("MultiAnon2");

        await ExecuteSqlAsync(db2Name, "CREATE TABLE dbo.Throwaway (Id INT); DROP TABLE dbo.Throwaway;");

        const string script = @"
            CREATE TABLE dbo.Account (
                Id INT NOT NULL CONSTRAINT PK_Account PRIMARY KEY,
                ValidFrom DATETIME2 GENERATED ALWAYS AS ROW START NOT NULL,
                ValidTo DATETIME2 GENERATED ALWAYS AS ROW END NOT NULL,
                PERIOD FOR SYSTEM_TIME (ValidFrom, ValidTo)
            ) WITH (SYSTEM_VERSIONING = ON);
            CREATE TABLE dbo.Widget (
                Id INT NOT NULL CONSTRAINT PK_Widget PRIMARY KEY,
                ValidFrom DATETIME2 GENERATED ALWAYS AS ROW START NOT NULL,
                ValidTo DATETIME2 GENERATED ALWAYS AS ROW END NOT NULL,
                PERIOD FOR SYSTEM_TIME (ValidFrom, ValidTo)
            ) WITH (SYSTEM_VERSIONING = ON);";

        await ExecuteSqlAsync(db1Name, script);
        await ExecuteSqlAsync(db2Name, script);

        var schema1 = await ExtractSchemaAsync(db1Name);
        var schema2 = await ExtractSchemaAsync(db2Name);

        schema1.Tables.Count(t => t.TemporalType == "HISTORY_TABLE").ShouldBe(2, "Both anonymous history tables must be extracted");

        zachtbeer.SqlSchemaHasher.SqlSchemaHash.ComputeHash(schema1, new SchemaHashOptions()).ShouldBe(zachtbeer.SqlSchemaHasher.SqlSchemaHash.ComputeHash(schema2, new SchemaHashOptions()), "Two anonymous history tables in the same schema must still hash identically across databases");
    }

    [TestMethod]
    public async Task AnonymousHistoryTable_ExtractionFidelity_RawNameAndParentLinkage()
    {
        var dbName = await CreateTestDatabaseAsync("HistFidelity");

        await ExecuteSqlAsync(dbName, AnonymousTemporalScript);

        var schema = await ExtractSchemaAsync(dbName);

        var parent = schema.Tables.Single(t => t.Name == "Account");
        parent.VersionedParentSchema.ShouldBeNull("An ordinary versioned table must not report a versioned parent of its own");
        parent.VersionedParentName.ShouldBeNull("An ordinary versioned table must not report a versioned parent of its own");

        var history = schema.Tables.Single(t => t.TemporalType == "HISTORY_TABLE");
        history.Name.StartsWith("MSSQL_TemporalHistoryFor_", StringComparison.Ordinal).ShouldBeTrue("Extraction must stay a faithful mirror of the raw catalog name");
        history.VersionedParentSchema.ShouldBe("dbo", "The history table must be mapped back to its versioned parent's schema");
        history.VersionedParentName.ShouldBe("Account", "The history table must be mapped back to its versioned parent's name");
    }

    [TestMethod]
    public async Task ExplicitHistoryTable_AlsoReportsVersionedParentLinkage()
    {
        var dbName = await CreateTestDatabaseAsync("ExplicitHistFidelity");

        await ExecuteSqlAsync(dbName, @"
            CREATE TABLE dbo.Account (
                Id INT NOT NULL CONSTRAINT PK_Account PRIMARY KEY,
                ValidFrom DATETIME2 GENERATED ALWAYS AS ROW START NOT NULL,
                ValidTo DATETIME2 GENERATED ALWAYS AS ROW END NOT NULL,
                PERIOD FOR SYSTEM_TIME (ValidFrom, ValidTo)
            ) WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = dbo.Account_History))");

        var schema = await ExtractSchemaAsync(dbName);

        var history = schema.Tables.Single(t => t.Name == "Account_History");
        history.VersionedParentSchema.ShouldBe("dbo", "An explicitly-named history table must also report its versioned parent");
        history.VersionedParentName.ShouldBe("Account", "An explicitly-named history table must also report its versioned parent");
    }
}
