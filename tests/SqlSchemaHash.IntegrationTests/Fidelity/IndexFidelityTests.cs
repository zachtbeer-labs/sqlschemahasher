using zachtbeer.SqlSchemaHasher;
using Shouldly;

namespace SqlSchemaHash.IntegrationTests.Fidelity;

/// <summary>
/// Index-level schema fidelity: a genuine difference in a captured index attribute — disabled state,
/// IGNORE_DUP_KEY, columnstore clustering, filter predicate, physical storage (fill factor / locking),
/// and key-vs-included column membership — must change the hash, and the metadata is pinned where
/// practical. Bit-normalization contracts for these facets live in the Settings suite.
/// </summary>
[TestClass]
public class IndexFidelityTests : IntegrationTestBase
{
    [TestMethod]
    public async Task Index_Disabled_ChangesHash_AndIsExtracted()
    {
        var db1Name = await CreateTestDatabaseAsync("IdxDisabled1");
        var db2Name = await CreateTestDatabaseAsync("IdxDisabled2");

        const string setup = @"
            CREATE TABLE T (Id INT NOT NULL CONSTRAINT PK_T PRIMARY KEY, A INT NOT NULL);
            CREATE INDEX IX_T_A ON T(A);";
        await ExecuteSqlAsync(db1Name, setup);
        await ExecuteSqlAsync(db2Name, setup);

        await ExecuteSqlAsync(db2Name, "ALTER INDEX IX_T_A ON T DISABLE");

        (await ExtractAndHashAsync(db1Name)).ShouldNotBe(await ExtractAndHashAsync(db2Name), "A disabled index is not maintained or used by the optimizer and must change the hash");

        var index = (await ExtractSchemaAsync(db2Name)).Tables.Single(t => t.Name == "T").Indexes.Single(i => i.Name == "IX_T_A");
        index.IsDisabled.ShouldBeTrue("A disabled index must be reported as disabled");
    }

    [TestMethod]
    public async Task UniqueIndex_IgnoreDupKey_ChangesHash_AndIsExtracted()
    {
        var db1Name = await CreateTestDatabaseAsync("IgnoreDup1");
        var db2Name = await CreateTestDatabaseAsync("IgnoreDup2");

        const string setup = "CREATE TABLE T (Id INT NOT NULL CONSTRAINT PK_T PRIMARY KEY, A INT NOT NULL)";
        await ExecuteSqlAsync(db1Name, setup);
        await ExecuteSqlAsync(db2Name, setup);

        await ExecuteSqlAsync(db1Name, "CREATE UNIQUE INDEX UX_T_A ON T(A)");
        await ExecuteSqlAsync(db2Name, "CREATE UNIQUE INDEX UX_T_A ON T(A) WITH (IGNORE_DUP_KEY = ON)");

        (await ExtractAndHashAsync(db1Name)).ShouldNotBe(await ExtractAndHashAsync(db2Name), "IGNORE_DUP_KEY changes INSERT behavior and must change the hash");

        var index = (await ExtractSchemaAsync(db2Name)).Tables.Single(t => t.Name == "T").Indexes.Single(i => i.Name == "UX_T_A");
        index.IgnoreDupKey.ShouldBeTrue("An IGNORE_DUP_KEY unique index must be reported as such");
    }

    [TestMethod]
    public async Task ColumnstoreClustering_ClusteredVsNonclustered_ChangesHash_EvenUnderStructural()
    {
        // The old regex collapsed "CLUSTERED COLUMNSTORE" and "NONCLUSTERED COLUMNSTORE" to the same
        // token under NormalizeClusteringType. They are fundamentally different storage strategies and
        // must stay distinct even when clustered/nonclustered *rowstore* placement is normalized away.
        var db1Name = await CreateTestDatabaseAsync("Cci1");
        var db2Name = await CreateTestDatabaseAsync("Ncci2");

        await ExecuteSqlAsync(db1Name, "CREATE TABLE T (Id INT NOT NULL, A INT NOT NULL)");
        await ExecuteSqlAsync(db1Name, "CREATE CLUSTERED COLUMNSTORE INDEX CS ON T");

        await ExecuteSqlAsync(db2Name, "CREATE TABLE T (Id INT NOT NULL, A INT NOT NULL)");
        await ExecuteSqlAsync(db2Name, "CREATE NONCLUSTERED COLUMNSTORE INDEX CS ON T(Id, A)");

        (await ExtractAndHashAsync(db1Name, SchemaHashOptions.Structural)).ShouldNotBe(await ExtractAndHashAsync(db2Name, SchemaHashOptions.Structural), "A clustered columnstore index must not collide with a nonclustered columnstore index, even under Structural");
    }

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

    [TestMethod]
    public async Task FilteredIndex_DifferentPredicate_ChangesHash()
    {
        // The predicate lives in sys.indexes.filter_definition; two indexes that differ only in
        // their WHERE clause are different indexes and must hash differently.
        var db1Name = await CreateTestDatabaseAsync("Filter1");
        var db2Name = await CreateTestDatabaseAsync("Filter2");

        const string tableSql = @"
            CREATE TABLE Assets (
                Id INT NOT NULL CONSTRAINT PK_Assets PRIMARY KEY,
                Status INT NOT NULL
            )";
        await ExecuteSqlAsync(db1Name, tableSql);
        await ExecuteSqlAsync(db2Name, tableSql);

        await ExecuteSqlAsync(db1Name, "CREATE INDEX IX_Assets_Status ON Assets(Status) WHERE Status > 0");
        await ExecuteSqlAsync(db2Name, "CREATE INDEX IX_Assets_Status ON Assets(Status) WHERE Status > 5");

        var hash1 = await ExtractAndHashAsync(db1Name);
        var hash2 = await ExtractAndHashAsync(db2Name);

        hash1.ShouldNotBe(hash2, "A different filtered-index predicate is a schema difference and must change the hash");
    }

    [TestMethod]
    public async Task FilteredIndex_VsUnfiltered_ChangesHash_AndPredicateIsExtracted()
    {
        var db1Name = await CreateTestDatabaseAsync("FilterVsPlain1");
        var db2Name = await CreateTestDatabaseAsync("FilterVsPlain2");

        const string tableSql = @"
            CREATE TABLE Assets (
                Id INT NOT NULL CONSTRAINT PK_Assets PRIMARY KEY,
                Status INT NOT NULL
            )";
        await ExecuteSqlAsync(db1Name, tableSql);
        await ExecuteSqlAsync(db2Name, tableSql);

        await ExecuteSqlAsync(db1Name, "CREATE INDEX IX_Assets_Status ON Assets(Status) WHERE Status > 0");
        await ExecuteSqlAsync(db2Name, "CREATE INDEX IX_Assets_Status ON Assets(Status)");

        (await ExtractAndHashAsync(db1Name)).ShouldNotBe(await ExtractAndHashAsync(db2Name), "A filtered index must hash differently from an otherwise-identical unfiltered index");

        var filtered = (await ExtractSchemaAsync(db1Name)).Tables.Single(t => t.Name == "Assets").Indexes.Single(i => i.Name == "IX_Assets_Status");
        filtered.FilterDefinition.ShouldNotBeNull("The filter predicate must be captured for a filtered index");
        filtered.FilterDefinition.ShouldContain("Status", customMessage: "The captured filter predicate must reference the filtered column");

        var unfiltered = (await ExtractSchemaAsync(db2Name)).Tables.Single(t => t.Name == "Assets").Indexes.Single(i => i.Name == "IX_Assets_Status");
        unfiltered.FilterDefinition.ShouldBeNull("An unfiltered index must have a null filter predicate");
    }

    [TestMethod]
    public async Task IndexIncludedColumns_AreNotListedBeforeKeyColumns()
    {
        // Included columns have key_ordinal = 0 in sys.index_columns. They must not be
        // sorted in front of the actual key columns in the extracted key list.
        var dbName = await CreateTestDatabaseAsync("IncludePosition");

        await ExecuteSqlAsync(dbName, @"
            CREATE TABLE Assets (
                Id INT NOT NULL CONSTRAINT PK_Assets PRIMARY KEY,
                Name NVARCHAR(100) NOT NULL,
                Created DATETIME2 NOT NULL
            )");
        await ExecuteSqlAsync(dbName, "CREATE INDEX IX_Assets_Name ON Assets(Name) INCLUDE (Created)");

        var schema = await ExtractSchemaAsync(dbName);
        var index = schema.Tables.Single(t => t.Name == "Assets").Indexes.Single(i => i.Name == "IX_Assets_Name");

        index.KeyColumns.Select(k => k.Name).ShouldBe(new[] { "Name" }, customMessage: "Only the key column belongs in KeyColumns; included columns must not appear there");
        index.IncludedColumns.ShouldContain("Created", customMessage: "The included column must be captured in IncludedColumns, not the key list");
    }

    [TestMethod]
    public async Task IndexKeyColumn_SwappedWithIncludedColumn_ChangesHash()
    {
        // An index keyed on A including B is a different index than one keyed on B including A.
        var db1Name = await CreateTestDatabaseAsync("KeyVsInclude1");
        var db2Name = await CreateTestDatabaseAsync("KeyVsInclude2");

        const string tableSql = @"
            CREATE TABLE Assets (
                Id INT NOT NULL CONSTRAINT PK_Assets PRIMARY KEY,
                ColA NVARCHAR(100) NOT NULL,
                ColB NVARCHAR(100) NOT NULL
            )";

        await ExecuteSqlAsync(db1Name, tableSql);
        await ExecuteSqlAsync(db2Name, tableSql);

        await ExecuteSqlAsync(db1Name, "CREATE INDEX IX_Assets ON Assets(ColA) INCLUDE (ColB)");
        await ExecuteSqlAsync(db2Name, "CREATE INDEX IX_Assets ON Assets(ColB) INCLUDE (ColA)");

        var hash1 = await ExtractAndHashAsync(db1Name);
        var hash2 = await ExtractAndHashAsync(db2Name);

        hash1.ShouldNotBe(hash2, "Swapping an index key column with an included column changes the index definition and must change the hash");
    }
}
