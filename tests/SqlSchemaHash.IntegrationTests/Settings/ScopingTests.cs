using zachtbeer.SqlSchemaHasher;
using Shouldly;

namespace SqlSchemaHash.IntegrationTests.Settings;

/// <summary>
/// TESTPLAN §C P4 — object scoping (which objects are compared at all), which is applied at
/// extraction and is orthogonal to the normalization enums: <see cref="SchemaHashOptions.SchemaFilter"/>,
/// <see cref="SchemaHashOptions.ObjectNamesToIgnore"/>, <see cref="SchemaHashOptions.IgnoreSysDiagramObjects"/>.
/// </summary>
[TestClass]
public class ScopingTests : MatrixTestBase
{
    private static bool HasTable(SchemaMetadata s, string schema, string name) =>
        s.Tables.Any(t => t.SchemaName == schema && t.Name == name);
    private static bool HasProc(SchemaMetadata s, string schema, string name) =>
        s.StoredProcedures.Any(p => p.SchemaName == schema && p.Name == name);
    private static bool HasUdt(SchemaMetadata s, string schema, string name) =>
        s.UserDefinedTableTypes.Any(u => u.SchemaName == schema && u.Name == name);

    // Two-schema fixture with a table, a procedure and a table type in each of dbo and sales.
    private static readonly string[] TwoSchemaObjects =
    {
        "CREATE SCHEMA sales",
        "CREATE TABLE dbo.Customers (Id INT NOT NULL CONSTRAINT PK_Customers PRIMARY KEY)",
        "CREATE TABLE sales.Orders (Id INT NOT NULL CONSTRAINT PK_Orders PRIMARY KEY)",
        "CREATE PROCEDURE dbo.GetCustomer AS BEGIN SELECT 1 AS V END",
        "CREATE PROCEDURE sales.GetOrder AS BEGIN SELECT 1 AS V END",
        "CREATE TYPE dbo.CustType AS TABLE (Id INT NOT NULL)",
        "CREATE TYPE sales.OrderType AS TABLE (Id INT NOT NULL)",
    };

    // ── SchemaFilter ─────────────────────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task SchemaFilter_ControlsSchemaInclusion_AcrossTablesProcsUdts()
    {
        var dbName = await CreateDatabaseWithAsync("filterAll", TwoSchemaObjects);

        var all = await ExtractSchemaAsync(dbName, Strict);
        HasTable(all, "dbo", "Customers").ShouldBeTrue("null filter includes dbo tables");
        HasTable(all, "sales", "Orders").ShouldBeTrue("null filter includes sales tables");
        HasProc(all, "dbo", "GetCustomer").ShouldBeTrue();
        HasProc(all, "sales", "GetOrder").ShouldBeTrue();
        HasUdt(all, "dbo", "CustType").ShouldBeTrue();
        HasUdt(all, "sales", "OrderType").ShouldBeTrue();

        var salesOnly = await ExtractSchemaAsync(dbName, new SchemaHashOptions { SchemaFilter = "sales" });
        HasTable(salesOnly, "sales", "Orders").ShouldBeTrue("the filtered schema's table is kept");
        HasProc(salesOnly, "sales", "GetOrder").ShouldBeTrue("the filtered schema's proc is kept");
        HasUdt(salesOnly, "sales", "OrderType").ShouldBeTrue("the filtered schema's UDT is kept");
        HasTable(salesOnly, "dbo", "Customers").ShouldBeFalse("dbo tables are excluded");
        HasProc(salesOnly, "dbo", "GetCustomer").ShouldBeFalse("dbo procs are excluded");
        HasUdt(salesOnly, "dbo", "CustType").ShouldBeFalse("dbo UDTs are excluded");
    }

    [TestMethod]
    public async Task SchemaFilter_DifferenceOutsideFilteredSchema_DoesNotAffectHash()
    {
        var db1 = await CreateDatabaseWithAsync("filterHash1",
            "CREATE SCHEMA sales",
            "CREATE TABLE dbo.Customers (Id INT NOT NULL CONSTRAINT PK_Customers PRIMARY KEY)",
            "CREATE TABLE sales.Orders (Id INT NOT NULL CONSTRAINT PK_Orders PRIMARY KEY)");
        var db2 = await CreateDatabaseWithAsync("filterHash2",
            "CREATE SCHEMA sales",
            "CREATE TABLE dbo.Customers (Id INT NOT NULL CONSTRAINT PK_Customers PRIMARY KEY)",
            "CREATE TABLE sales.Orders (Id INT NOT NULL CONSTRAINT PK_Orders PRIMARY KEY, Extra NVARCHAR(10) NULL)");

        var dboOnly = new SchemaHashOptions { SchemaFilter = "dbo" };
        (await ExtractAndHashAsync(db1, dboOnly)).ShouldBe(await ExtractAndHashAsync(db2, dboOnly),
            "A difference confined to the sales schema must not affect the hash when filtering to dbo");
        (await ExtractAndHashAsync(db1, Strict)).ShouldNotBe(await ExtractAndHashAsync(db2, Strict),
            "Without a filter the sales-schema difference must change the hash");
    }

    [TestMethod]
    public async Task SchemaFilter_CrossSchemaForeignKey_RetainedOnIncludedTable()
    {
        // An FK's referenced schema/table are plain strings on the child table's record, so filtering
        // out the referenced table does NOT strip the FK from the (included) child table.
        var dbName = await CreateDatabaseWithAsync("filterFk",
            "CREATE SCHEMA sales",
            "CREATE TABLE dbo.Customers (Id INT NOT NULL CONSTRAINT PK_Customers PRIMARY KEY)",
            "CREATE TABLE sales.Orders (Id INT NOT NULL CONSTRAINT PK_Orders PRIMARY KEY, CustomerId INT NOT NULL CONSTRAINT FK_Orders_Customers REFERENCES dbo.Customers(Id))");

        var salesOnly = await ExtractSchemaAsync(dbName, new SchemaHashOptions { SchemaFilter = "sales" });

        HasTable(salesOnly, "dbo", "Customers").ShouldBeFalse("The referenced dbo table is filtered out");
        var orders = salesOnly.Tables.Single(t => t.SchemaName == "sales" && t.Name == "Orders");
        var fk = orders.ForeignKeys.ShouldHaveSingleItem();
        fk.ReferencedSchema.ShouldBe("dbo", "The FK still references the filtered-out schema");
        fk.ReferencedTable.ShouldBe("Customers");
    }

    // ── ObjectNamesToIgnore ──────────────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task ObjectNamesToIgnore_ExcludesStoredProcedure()
    {
        var dbName = await CreateDatabaseWithAsync("ignoreProc",
            "CREATE PROCEDURE dbo.KeepProc AS BEGIN SELECT 1 AS V END",
            "CREATE PROCEDURE dbo.DropProc AS BEGIN SELECT 1 AS V END");

        var options = new SchemaHashOptions { ObjectNamesToIgnore = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "DropProc" } };
        var schema = await ExtractSchemaAsync(dbName, options);

        HasProc(schema, "dbo", "KeepProc").ShouldBeTrue("Unrelated procedures are retained");
        HasProc(schema, "dbo", "DropProc").ShouldBeFalse("A procedure named in ObjectNamesToIgnore is excluded");
    }

    [TestMethod]
    public async Task ObjectNamesToIgnore_IsCaseInsensitiveByDefault_RegardlessOfSetComparer()
    {
        var dbName = await CreateDatabaseWithAsync("ignoreCase",
            "CREATE PROCEDURE dbo.GetCustomer AS BEGIN SELECT 1 AS V END");

        // A plain (ordinal-comparer) set is used deliberately: the library applies ObjectNameComparer
        // (OrdinalIgnoreCase by default), so case-insensitive matching no longer depends on the set.
        var options = new SchemaHashOptions { ObjectNamesToIgnore = new HashSet<string> { "getcustomer" } };
        var schema = await ExtractSchemaAsync(dbName, options);

        HasProc(schema, "dbo", "GetCustomer").ShouldBeFalse("Default matching is case-insensitive even when the assigned set uses an ordinal comparer");
    }

    [TestMethod]
    public async Task ObjectNamesToIgnore_CaseSensitive_WhenComparerIsOrdinal()
    {
        var dbName = await CreateDatabaseWithAsync("ignoreCaseSensitive",
            "CREATE PROCEDURE dbo.GetCustomer AS BEGIN SELECT 1 AS V END");

        var wrongCase = new SchemaHashOptions { ObjectNameComparer = StringComparer.Ordinal, ObjectNamesToIgnore = new HashSet<string> { "getcustomer" } };
        var exactCase = new SchemaHashOptions { ObjectNameComparer = StringComparer.Ordinal, ObjectNamesToIgnore = new HashSet<string> { "GetCustomer" } };

        HasProc(await ExtractSchemaAsync(dbName, wrongCase), "dbo", "GetCustomer").ShouldBeTrue("With an ordinal comparer a differently-cased entry does NOT match");
        HasProc(await ExtractSchemaAsync(dbName, exactCase), "dbo", "GetCustomer").ShouldBeFalse("With an ordinal comparer an exact-case entry matches");
    }

    [TestMethod]
    public async Task ObjectNamesToIgnore_BareName_MatchesAcrossSchemas()
    {
        // A bare (non-qualified) entry is an all-schemas shorthand: it drops the object from every
        // schema that has one. Use a schema-qualified entry to target a single schema.
        var dbName = await CreateDatabaseWithAsync("ignoreBare",
            "CREATE SCHEMA sales",
            "CREATE TABLE dbo.Thing (Id INT NOT NULL CONSTRAINT PK_dboThing PRIMARY KEY)",
            "CREATE TABLE sales.Thing (Id INT NOT NULL CONSTRAINT PK_salesThing PRIMARY KEY)");

        var options = new SchemaHashOptions { ObjectNamesToIgnore = new HashSet<string> { "Thing" } };
        var schema = await ExtractSchemaAsync(dbName, options);

        HasTable(schema, "dbo", "Thing").ShouldBeFalse("A bare entry excludes dbo.Thing");
        HasTable(schema, "sales", "Thing").ShouldBeFalse("A bare entry also excludes sales.Thing (all-schemas shorthand)");
    }

    [TestMethod]
    public async Task ObjectNamesToIgnore_SchemaQualified_TargetsSingleSchema()
    {
        // A schema-qualified entry excludes only that schema's object, leaving a same-named object in
        // another schema intact — the precise-targeting fix for the bare-name footgun.
        var dbName = await CreateDatabaseWithAsync("ignoreQualified",
            "CREATE SCHEMA sales",
            "CREATE TABLE dbo.Thing (Id INT NOT NULL CONSTRAINT PK_dboThing PRIMARY KEY)",
            "CREATE TABLE sales.Thing (Id INT NOT NULL CONSTRAINT PK_salesThing PRIMARY KEY)");

        var options = new SchemaHashOptions { ObjectNamesToIgnore = new HashSet<string> { "sales.Thing" } };
        var schema = await ExtractSchemaAsync(dbName, options);

        HasTable(schema, "dbo", "Thing").ShouldBeTrue("A schema-qualified entry must not affect another schema's same-named object");
        HasTable(schema, "sales", "Thing").ShouldBeFalse("A schema-qualified entry excludes only that schema's object");
    }

    // ── IgnoreSysDiagramObjects ──────────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task IgnoreSysDiagramObjects_ExcludesHelperProceduresAndIsOffByDefault()
    {
        var dbName = await CreateDatabaseWithAsync("diagramProcs",
            "CREATE PROCEDURE dbo.sp_creatediagram AS BEGIN SELECT 1 AS V END",
            "CREATE PROCEDURE dbo.sp_helpdiagrams AS BEGIN SELECT 1 AS V END",
            "CREATE PROCEDURE dbo.RealProc AS BEGIN SELECT 1 AS V END");

        var withToggle = await ExtractSchemaAsync(dbName, new SchemaHashOptions { IgnoreSysDiagramObjects = true });
        HasProc(withToggle, "dbo", "RealProc").ShouldBeTrue("A normal procedure is retained");
        HasProc(withToggle, "dbo", "sp_creatediagram").ShouldBeFalse("A diagram helper procedure is excluded when the toggle is set");
        HasProc(withToggle, "dbo", "sp_helpdiagrams").ShouldBeFalse("All diagram helper procedures are excluded, not just the sysdiagrams table");

        var defaultOptions = await ExtractSchemaAsync(dbName, Strict);
        HasProc(defaultOptions, "dbo", "sp_creatediagram").ShouldBeTrue("The toggle is off by default, so diagram helper procs are NOT excluded");
    }
}
