using zachtbeer.SqlSchemaHasher;
using Shouldly;

namespace SqlSchemaHash.IntegrationTests;

/// <summary>
/// Regression tests for the second hardening pass: constraint-ordering determinism under
/// name-ignoring options, index physical/locking options, ANSI padding, and temporal history
/// retention/linkage. Each asserts either that a genuine difference now changes the hash, or that
/// two option-equivalent schemas hash identically.
/// </summary>
[TestClass]
public class HardeningGapTests : IntegrationTestBase
{
    #region Constraint ordering determinism (#1)

    [TestMethod]
    public async Task CheckConstraints_DifferingOnlyInEnforcement_HashSame_RegardlessOfCreationOrder_UnderStructural()
    {
        // Two CHECK constraints share a predicate and differ only in trust state (WITH NOCHECK). Under
        // Structural, IgnoreConstraintNames collapses their names, so the old sort key (Definition, Name)
        // tied and fell back to catalog scan order — which follows object_id / creation order. Creating
        // the two constraints in opposite orders in the two databases must still produce the same hash.
        var db1Name = await CreateTestDatabaseAsync("CkOrder1");
        var db2Name = await CreateTestDatabaseAsync("CkOrder2");

        await ExecuteSqlAsync(db1Name, "CREATE TABLE T (X INT NOT NULL)");
        await ExecuteSqlAsync(db1Name, "ALTER TABLE T ADD CONSTRAINT Ca CHECK (X > 0)");
        await ExecuteSqlAsync(db1Name, "ALTER TABLE T WITH NOCHECK ADD CONSTRAINT Cb CHECK (X > 0)");

        await ExecuteSqlAsync(db2Name, "CREATE TABLE T (X INT NOT NULL)");
        await ExecuteSqlAsync(db2Name, "ALTER TABLE T WITH NOCHECK ADD CONSTRAINT Cb CHECK (X > 0)");
        await ExecuteSqlAsync(db2Name, "ALTER TABLE T ADD CONSTRAINT Ca CHECK (X > 0)");

        (await ExtractAndHashAsync(db1Name, SchemaHashOptions.Structural)).ShouldBe(await ExtractAndHashAsync(db2Name, SchemaHashOptions.Structural), "Two CHECK constraints differing only in trust state must hash deterministically regardless of the order the catalog returns them");
    }

    #endregion

    #region Index physical options (#3)

    [TestMethod]
    public async Task Index_FillFactor_ChangesHash_AndIsExtracted()
    {
        var db1Name = await CreateTestDatabaseAsync("Fill1");
        var db2Name = await CreateTestDatabaseAsync("Fill2");

        const string setup = "CREATE TABLE T (Id INT NOT NULL CONSTRAINT PK_T PRIMARY KEY, A INT NOT NULL)";
        await ExecuteSqlAsync(db1Name, setup);
        await ExecuteSqlAsync(db2Name, setup);

        await ExecuteSqlAsync(db1Name, "CREATE INDEX IX_T_A ON T(A)");
        await ExecuteSqlAsync(db2Name, "CREATE INDEX IX_T_A ON T(A) WITH (FILLFACTOR = 70, PAD_INDEX = ON)");

        (await ExtractAndHashAsync(db1Name)).ShouldNotBe(await ExtractAndHashAsync(db2Name), "A non-default fill factor / PAD_INDEX changes page density and must change the hash under the exact baseline");

        var index = (await ExtractSchemaAsync(db2Name)).Tables.Single(t => t.Name == "T").Indexes.Single(i => i.Name == "IX_T_A");
        index.FillFactor.ShouldBe((byte)70, "A non-default fill factor must be captured");
        index.IsPadded.ShouldBeTrue("PAD_INDEX = ON must be captured");
    }

    [TestMethod]
    public async Task Index_AllowPageLocksOff_ChangesHash_AndIsExtracted()
    {
        var db1Name = await CreateTestDatabaseAsync("Lock1");
        var db2Name = await CreateTestDatabaseAsync("Lock2");

        const string setup = "CREATE TABLE T (Id INT NOT NULL CONSTRAINT PK_T PRIMARY KEY, A INT NOT NULL)";
        await ExecuteSqlAsync(db1Name, setup);
        await ExecuteSqlAsync(db2Name, setup);

        await ExecuteSqlAsync(db1Name, "CREATE INDEX IX_T_A ON T(A)");
        await ExecuteSqlAsync(db2Name, "CREATE INDEX IX_T_A ON T(A) WITH (ALLOW_PAGE_LOCKS = OFF)");

        (await ExtractAndHashAsync(db1Name)).ShouldNotBe(await ExtractAndHashAsync(db2Name), "ALLOW_PAGE_LOCKS = OFF changes the index's locking behavior and must change the hash");

        var index = (await ExtractSchemaAsync(db2Name)).Tables.Single(t => t.Name == "T").Indexes.Single(i => i.Name == "IX_T_A");
        index.AllowPageLocks.ShouldBeFalse("ALLOW_PAGE_LOCKS = OFF must be captured");
    }

    #endregion

    #region ANSI padding (#4)

    [TestMethod]
    public async Task Column_AnsiPaddingOff_ChangesHash_AndIsExtracted()
    {
        var db1Name = await CreateTestDatabaseAsync("Ansi1");
        var db2Name = await CreateTestDatabaseAsync("Ansi2");

        // ANSI_PADDING must be set in the same batch as the CREATE for it to bind to the column.
        await ExecuteSqlAsync(db1Name, "SET ANSI_PADDING ON; CREATE TABLE T (Id INT NOT NULL, A VARCHAR(10) NULL)");
        await ExecuteSqlAsync(db2Name, "SET ANSI_PADDING OFF; CREATE TABLE T (Id INT NOT NULL, A VARCHAR(10) NULL)");

        (await ExtractAndHashAsync(db1Name)).ShouldNotBe(await ExtractAndHashAsync(db2Name), "A varchar column created under ANSI_PADDING OFF stores trailing spaces differently and must change the hash");

        var padded = (await ExtractSchemaAsync(db1Name)).Tables.Single(t => t.Name == "T").Columns.Single(c => c.Name == "A");
        padded.IsAnsiPadded.ShouldBeTrue("A column created under ANSI_PADDING ON must report is_ansi_padded = true");

        var unpadded = (await ExtractSchemaAsync(db2Name)).Tables.Single(t => t.Name == "T").Columns.Single(c => c.Name == "A");
        unpadded.IsAnsiPadded.ShouldBeFalse("A column created under ANSI_PADDING OFF must report is_ansi_padded = false");
    }

    #endregion

    #region Temporal history retention & linkage (#6)

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

    #endregion
}
