using zachtbeer.SqlSchemaHasher;
using Shouldly;

namespace SqlSchemaHash.IntegrationTests.Fidelity;

/// <summary>
/// Table-level schema fidelity: a genuine difference in a captured table attribute — column ordinal
/// order, identity seed/increment/NFR (including detection on dotted/spaced table names), and system-
/// versioned temporal type / history retention — must change the hash, with metadata pinned where
/// practical. Bit-normalization contracts for these facets live in the Settings suite.
/// </summary>
[TestClass]
public class TableFidelityTests : IntegrationTestBase
{
    [TestMethod]
    public async Task TableColumns_OrderSwapped_ChangesHash()
    {
        var db1Name = await CreateTestDatabaseAsync("TblColOrder1");
        var db2Name = await CreateTestDatabaseAsync("TblColOrder2");

        await ExecuteSqlAsync(db1Name, "CREATE TABLE T (Id INT NOT NULL CONSTRAINT PK_T PRIMARY KEY, Alpha INT NOT NULL, Beta INT NOT NULL)");
        await ExecuteSqlAsync(db2Name, "CREATE TABLE T (Id INT NOT NULL CONSTRAINT PK_T PRIMARY KEY, Beta INT NOT NULL, Alpha INT NOT NULL)");

        (await ExtractAndHashAsync(db1Name)).ShouldNotBe(await ExtractAndHashAsync(db2Name), "Reordering table columns must change the hash now that ordinal position is preserved");
    }

    [TestMethod]
    public async Task Identity_DifferentSeedAndIncrement_ChangesHash()
    {
        var db1Name = await CreateTestDatabaseAsync("IdentitySeed1");
        var db2Name = await CreateTestDatabaseAsync("IdentitySeed2");

        await ExecuteSqlAsync(db1Name, "CREATE TABLE Invoice (Id INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Invoice PRIMARY KEY)");
        await ExecuteSqlAsync(db2Name, "CREATE TABLE Invoice (Id INT IDENTITY(1000,5) NOT NULL CONSTRAINT PK_Invoice PRIMARY KEY)");

        (await ExtractAndHashAsync(db1Name)).ShouldNotBe(await ExtractAndHashAsync(db2Name), "IDENTITY(1,1) and IDENTITY(1000,5) are different definitions and must change the hash");
    }

    [TestMethod]
    public async Task Identity_NotForReplication_ChangesHash()
    {
        var db1Name = await CreateTestDatabaseAsync("IdentityNfr1");
        var db2Name = await CreateTestDatabaseAsync("IdentityNfr2");

        await ExecuteSqlAsync(db1Name, "CREATE TABLE Invoice (Id INT IDENTITY(1,1) NOT FOR REPLICATION NOT NULL CONSTRAINT PK_Invoice PRIMARY KEY)");
        await ExecuteSqlAsync(db2Name, "CREATE TABLE Invoice (Id INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Invoice PRIMARY KEY)");

        (await ExtractAndHashAsync(db1Name)).ShouldNotBe(await ExtractAndHashAsync(db2Name), "The identity NOT FOR REPLICATION flag is part of the schema and must change the hash");
    }

    [TestMethod]
    public async Task Identity_SeedAndIncrement_AreExtracted()
    {
        var dbName = await CreateTestDatabaseAsync("IdentityMeta");

        await ExecuteSqlAsync(dbName, "CREATE TABLE Invoice (Id INT IDENTITY(1000,5) NOT NULL CONSTRAINT PK_Invoice PRIMARY KEY)");

        var table = (await ExtractSchemaAsync(dbName)).Tables.Single(t => t.Name == "Invoice");
        table.IdentityColumn.ShouldBe("Id");
        table.IdentitySeed.ShouldBe("1000", "The identity seed must be captured");
        table.IdentityIncrement.ShouldBe("5", "The identity increment must be captured");
    }

    [TestMethod]
    public async Task Identity_SameSeedAndIncrement_SameHash()
    {
        // Guards against over-sensitivity: identical identity definitions must still compare equal.
        var db1Name = await CreateTestDatabaseAsync("IdentitySame1");
        var db2Name = await CreateTestDatabaseAsync("IdentitySame2");

        const string sql = "CREATE TABLE Invoice (Id INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Invoice PRIMARY KEY)";
        await ExecuteSqlAsync(db1Name, sql);
        await ExecuteSqlAsync(db2Name, sql);

        (await ExtractAndHashAsync(db1Name)).ShouldBe(await ExtractAndHashAsync(db2Name), "Identical identity definitions must produce the same hash");
    }

    [TestMethod]
    public async Task IdentityColumn_OnTableNameWithSpace_IsDetected()
    {
        var dbName = await CreateTestDatabaseAsync("IdentitySpace");

        await ExecuteSqlAsync(dbName, @"
            CREATE TABLE [Order Details] (
                OrderDetailId INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_OrderDetails PRIMARY KEY,
                Quantity INT NOT NULL
            )");

        var schema = await ExtractSchemaAsync(dbName);
        var table = schema.Tables.Single(t => t.Name == "Order Details");
        table.IdentityColumn.ShouldBe("OrderDetailId", "Identity columns must be detected when the table name contains a space");
    }

    [TestMethod]
    public async Task IdentityColumn_OnTableNameWithDot_IsDetected()
    {
        // A dot in the table name breaks unquoted OBJECT_ID resolution
        // ('dbo.Order.Details' parses as database.schema.object) unless QUOTENAME is used.
        var db1Name = await CreateTestDatabaseAsync("IdentityDot1");
        var db2Name = await CreateTestDatabaseAsync("IdentityDot2");

        await ExecuteSqlAsync(db1Name, @"
            CREATE TABLE [Order.Details] (
                OrderDetailId INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_OrderDetails PRIMARY KEY,
                Quantity INT NOT NULL
            )");

        await ExecuteSqlAsync(db2Name, @"
            CREATE TABLE [Order.Details] (
                OrderDetailId INT NOT NULL CONSTRAINT PK_OrderDetails PRIMARY KEY,
                Quantity INT NOT NULL
            )");

        var schema1 = await ExtractSchemaAsync(db1Name);
        var table1 = schema1.Tables.Single(t => t.Name == "Order.Details");
        table1.IdentityColumn.ShouldBe("OrderDetailId", "Identity columns must be detected even when the table name contains a dot");

        var hash1 = await ExtractAndHashAsync(db1Name);
        var hash2 = await ExtractAndHashAsync(db2Name);
        hash1.ShouldNotBe(hash2, "Presence of an identity column is a schema difference and must change the hash");
    }

    [TestMethod]
    public async Task TemporalTable_VsPlain_ChangesHash_AndTypeIsExtracted()
    {
        var db1Name = await CreateTestDatabaseAsync("Temporal1");
        var db2Name = await CreateTestDatabaseAsync("Plain2");

        await ExecuteSqlAsync(db1Name, @"
            CREATE TABLE T (
                Id INT NOT NULL CONSTRAINT PK_T PRIMARY KEY,
                ValidFrom DATETIME2 GENERATED ALWAYS AS ROW START NOT NULL,
                ValidTo DATETIME2 GENERATED ALWAYS AS ROW END NOT NULL,
                PERIOD FOR SYSTEM_TIME (ValidFrom, ValidTo)
            ) WITH (SYSTEM_VERSIONING = ON)");
        await ExecuteSqlAsync(db2Name, @"
            CREATE TABLE T (
                Id INT NOT NULL CONSTRAINT PK_T PRIMARY KEY,
                ValidFrom DATETIME2 NOT NULL,
                ValidTo DATETIME2 NOT NULL
            )");

        (await ExtractAndHashAsync(db1Name)).ShouldNotBe(await ExtractAndHashAsync(db2Name), "A system-versioned temporal table must not collide with an otherwise-identical plain table");

        var t = (await ExtractSchemaAsync(db1Name)).Tables.Single(x => x.Name == "T");
        t.TemporalType.ShouldBe("SYSTEM_VERSIONED_TEMPORAL_TABLE", "A temporal table's type must be captured");
        t.Columns.Single(c => c.Name == "ValidFrom").GeneratedAlwaysType.ShouldBe("AS_ROW_START", "A period column's generated-always kind must be captured");
    }

    [TestMethod]
    public async Task TemporalTable_HistoryRetentionPeriod_ChangesHash_AndIsExtracted()
    {
        var db1Name = await CreateTestDatabaseAsync("Retention1");
        var db2Name = await CreateTestDatabaseAsync("Retention2");

        // Explicit history table names keep the whole schema deterministic; only the retention differs.
        await ExecuteSqlAsync(db1Name, TemporalTableSql("dbo.T_History", "3 MONTHS"));
        await ExecuteSqlAsync(db2Name, TemporalTableSql("dbo.T_History", "1 YEAR"));

        (await ExtractAndHashAsync(db1Name)).ShouldNotBe(await ExtractAndHashAsync(db2Name), "A different history-retention policy is part of the temporal table's definition and must change the hash");

        var t = (await ExtractSchemaAsync(db1Name)).Tables.Single(x => x.Name == "T");
        t.HistoryRetentionPeriod.ShouldBe(3, "A finite retention period must be captured");
        t.HistoryRetentionPeriodUnit.ShouldBe("MONTH", "The retention period unit must be captured");
        t.HistoryTableName.ShouldBe("dbo.T_History", "An explicitly-named history table must be captured as its schema-qualified name");
    }

    [TestMethod]
    public async Task TemporalTable_InfiniteRetention_HasNoRetentionPeriod()
    {
        var dbName = await CreateTestDatabaseAsync("InfRetention");

        await ExecuteSqlAsync(dbName, TemporalTableSql("dbo.T_History", retention: null));

        var t = (await ExtractSchemaAsync(dbName)).Tables.Single(x => x.Name == "T");
        t.HistoryRetentionPeriod.ShouldBeNull("INFINITE (default) retention must normalize to null so it matches a server without retention support");
        t.HistoryRetentionPeriodUnit.ShouldBeNull("INFINITE retention has no finite unit");
    }

    private static string TemporalTableSql(string historyTable, string? retention)
    {
        var versioning = retention is null
            ? $"HISTORY_TABLE = {historyTable}"
            : $"HISTORY_TABLE = {historyTable}, HISTORY_RETENTION_PERIOD = {retention}";
        return $@"
            CREATE TABLE T (
                Id INT NOT NULL CONSTRAINT PK_T PRIMARY KEY,
                ValidFrom DATETIME2 GENERATED ALWAYS AS ROW START NOT NULL,
                ValidTo DATETIME2 GENERATED ALWAYS AS ROW END NOT NULL,
                PERIOD FOR SYSTEM_TIME (ValidFrom, ValidTo)
            ) WITH (SYSTEM_VERSIONING = ON ({versioning}))";
    }
}
