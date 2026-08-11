using Dapper;
using Microsoft.Data.SqlClient;
using zachtbeer.SqlSchemaHasher;
using Shouldly;

namespace SqlSchemaHash.IntegrationTests.Fidelity;

/// <summary>
/// View schema fidelity: presence, body-text changes, encryption, indexed-view indexes, and
/// CREATE-time SET state must all be reflected in the hash, with normalization bits able to
/// neutralize body text and SET options exactly as they do for stored procedures.
/// </summary>
[TestClass]
public class ViewFidelityTests : IntegrationTestBase
{
    [TestMethod]
    public async Task AddingView_ChangesHash()
    {
        var dbName = await CreateTestDatabaseAsync("ViewAdd");
        await ExecuteSqlAsync(dbName, "CREATE TABLE T (Id INT NOT NULL)");
        var hashBefore = await ExtractAndHashAsync(dbName);

        await ExecuteSqlAsync(dbName, "CREATE VIEW dbo.VT AS SELECT Id FROM T");
        var hashAfter = await ExtractAndHashAsync(dbName);

        hashBefore.ShouldNotBe(hashAfter, "Adding a view must change the hash");
    }

    [TestMethod]
    public async Task IdenticalView_AcrossDatabases_ProducesSameHash()
    {
        var db1 = await CreateTestDatabaseAsync("ViewSame1");
        var db2 = await CreateTestDatabaseAsync("ViewSame2");

        const string ddl = "CREATE VIEW dbo.VT AS SELECT 1 AS V";
        await ExecuteSqlAsync(db1, ddl);
        await ExecuteSqlAsync(db2, ddl);

        (await ExtractAndHashAsync(db1)).ShouldBe(await ExtractAndHashAsync(db2), "Identical views must produce the same hash");
    }

    [TestMethod]
    public async Task ViewBody_DifferentText_ChangesHash_ButEqualUnderIgnoreBodyText()
    {
        var db1 = await CreateTestDatabaseAsync("ViewBody1");
        var db2 = await CreateTestDatabaseAsync("ViewBody2");

        await ExecuteSqlAsync(db1, "CREATE VIEW dbo.VT AS SELECT 1 AS V");
        await ExecuteSqlAsync(db2, "CREATE VIEW dbo.VT AS SELECT 2 AS V");

        (await ExtractAndHashAsync(db1)).ShouldNotBe(await ExtractAndHashAsync(db2), "Different view bodies must change the hash under Strict");

        var ignoreBody = new SchemaHashOptions { Modules = ModuleNormalization.IgnoreBodyText };
        (await ExtractAndHashAsync(db1, ignoreBody)).ShouldBe(await ExtractAndHashAsync(db2, ignoreBody), "IgnoreBodyText must collapse a body-only difference");
    }

    [TestMethod]
    public async Task EncryptedView_UsesDistinctDefinitionSentinel_AndDiffersFromUnencrypted()
    {
        var db1 = await CreateTestDatabaseAsync("ViewEnc1");
        var db2 = await CreateTestDatabaseAsync("ViewEnc2");

        await ExecuteSqlAsync(db1, "CREATE VIEW dbo.VT WITH ENCRYPTION AS SELECT 1 AS V");
        await ExecuteSqlAsync(db2, "CREATE VIEW dbo.VT AS SELECT 1 AS V");

        var views = (await ExtractSchemaAsync(db1)).Views;
        views.Single(v => v.Name == "VT").DefinitionHash.ShouldBe("<encrypted>", "An encrypted view must carry a distinct sentinel");

        (await ExtractAndHashAsync(db1)).ShouldNotBe(await ExtractAndHashAsync(db2), "An encrypted view must hash differently from an otherwise-identical unencrypted one");
    }

    [TestMethod]
    public async Task EncryptedView_IdenticalAcrossDatabases_ProducesSameHash()
    {
        var db1 = await CreateTestDatabaseAsync("ViewEncSame1");
        var db2 = await CreateTestDatabaseAsync("ViewEncSame2");

        const string ddl = "CREATE VIEW dbo.VT WITH ENCRYPTION AS SELECT 1 AS V";
        await ExecuteSqlAsync(db1, ddl);
        await ExecuteSqlAsync(db2, ddl);

        (await ExtractAndHashAsync(db1)).ShouldBe(await ExtractAndHashAsync(db2), "Two databases with the identical encrypted view must hash the same (inherent sentinel collision)");
    }

    [TestMethod]
    public async Task IndexedView_AddingOrDroppingIndex_ChangesHash()
    {
        var dbName = await CreateTestDatabaseAsync("IndexedView");

        await using (var connection = new SqlConnection(GetConnectionString(dbName)))
        {
            await connection.OpenAsync();
            await connection.ExecuteAsync("SET ARITHABORT ON");
            await connection.ExecuteAsync("CREATE TABLE dbo.T (Id INT NOT NULL, Val INT NOT NULL)");
            await connection.ExecuteAsync("CREATE VIEW dbo.VT WITH SCHEMABINDING AS SELECT Id, Val FROM dbo.T");
        }

        var hashBeforeIndex = await ExtractAndHashAsync(dbName);

        await using (var connection = new SqlConnection(GetConnectionString(dbName)))
        {
            await connection.OpenAsync();
            await connection.ExecuteAsync("SET ARITHABORT ON");
            await connection.ExecuteAsync("CREATE UNIQUE CLUSTERED INDEX IX_VT ON dbo.VT(Id)");
        }

        var hashAfterIndex = await ExtractAndHashAsync(dbName);
        hashBeforeIndex.ShouldNotBe(hashAfterIndex, "Adding a unique clustered index to an indexed view must change the hash");

        var views = (await ExtractSchemaAsync(dbName)).Views;
        var view = views.Single(v => v.Name == "VT");
        view.Indexes.ShouldHaveSingleItem().Name.ShouldBe("IX_VT");

        await ExecuteSqlAsync(dbName, "DROP INDEX IX_VT ON dbo.VT");
        var hashAfterDrop = await ExtractAndHashAsync(dbName);
        hashAfterDrop.ShouldBe(hashBeforeIndex, "Dropping the index must restore the pre-index hash");
    }

    [TestMethod]
    public async Task View_AnsiNullsSetting_ChangesHash_AndIsExtracted()
    {
        var db1 = await CreateTestDatabaseAsync("ViewAnsi1");
        var db2 = await CreateTestDatabaseAsync("ViewAnsi2");

        const string viewSql = "CREATE VIEW dbo.VT AS SELECT 1 AS V";
        await ExecuteSqlAsync(db1, viewSql); // ANSI_NULLS ON (client default)

        await using (var connection = new SqlConnection(GetConnectionString(db2)))
        {
            await connection.OpenAsync();
            await connection.ExecuteAsync("SET ANSI_NULLS OFF");
            await connection.ExecuteAsync(viewSql);
        }

        (await ExtractAndHashAsync(db1)).ShouldNotBe(await ExtractAndHashAsync(db2), "A view's ANSI_NULLS SET state baked at create time must change the hash under Strict");

        var ignoreSetOptions = new SchemaHashOptions { Modules = ModuleNormalization.IgnoreSetOptions };
        (await ExtractAndHashAsync(db1, ignoreSetOptions)).ShouldBe(await ExtractAndHashAsync(db2, ignoreSetOptions), "IgnoreSetOptions must collapse a SET-option-only difference");

        var view = (await ExtractSchemaAsync(db2)).Views.Single(v => v.Name == "VT");
        view.UsesAnsiNulls.ShouldBeFalse("A view created under SET ANSI_NULLS OFF must be reported as such");
    }
}
