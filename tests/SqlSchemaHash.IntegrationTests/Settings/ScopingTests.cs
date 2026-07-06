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
    private static bool HasView(SchemaMetadata s, string schema, string name) =>
        s.Views.Any(v => v.SchemaName == schema && v.Name == name);
    private static bool HasFunction(SchemaMetadata s, string schema, string name) =>
        s.Functions.Any(f => f.SchemaName == schema && f.Name == name);
    private static bool HasTrigger(SchemaMetadata s, string schema, string name) =>
        s.Triggers.Any(t => t.SchemaName == schema && t.Name == name);
    private static bool HasSequence(SchemaMetadata s, string schema, string name) =>
        s.Sequences.Any(sq => sq.SchemaName == schema && sq.Name == name);
    private static bool HasSynonym(SchemaMetadata s, string schema, string name) =>
        s.Synonyms.Any(sy => sy.SchemaName == schema && sy.Name == name);

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

    // ── sysdiagrams table + additive composition with the user ignore set ─────────────────────────

    [TestMethod]
    public async Task SysDiagrams_Table_IgnoredOnlyWhenToggleSet()
    {
        var db1Name = await CreateTestDatabaseAsync("Diagrams1");
        var db2Name = await CreateTestDatabaseAsync("Diagrams2");

        const string tableSql = "CREATE TABLE Assets (Id INT NOT NULL CONSTRAINT PK_Assets PRIMARY KEY)";
        await ExecuteSqlAsync(db1Name, tableSql);
        await ExecuteSqlAsync(db2Name, tableSql);

        await ExecuteSqlAsync(db1Name, @"
            CREATE TABLE sysdiagrams (
                name sysname NOT NULL,
                principal_id int NOT NULL,
                diagram_id int IDENTITY(1,1) PRIMARY KEY,
                version int,
                definition varbinary(max)
            )");

        var ignoreOptions = new SchemaHashOptions { IgnoreSysDiagramObjects = true };
        var neutralOptions = new SchemaHashOptions();

        (await ExtractAndHashAsync(db1Name, ignoreOptions)).ShouldBe(await ExtractAndHashAsync(db2Name, ignoreOptions), "IgnoreSysDiagramObjects must exclude the sysdiagrams table from the hash");
        (await ExtractAndHashAsync(db1Name, neutralOptions)).ShouldNotBe(await ExtractAndHashAsync(db2Name, neutralOptions), "The neutral baseline excludes nothing, so the sysdiagrams table must affect the hash");

        var schema = await ExtractSchemaAsync(db1Name, ignoreOptions);
        schema.Tables.Any(t => t.Name.Equals("sysdiagrams", StringComparison.OrdinalIgnoreCase)).ShouldBeFalse("sysdiagrams must be omitted from extraction when the toggle is set");
    }

    [TestMethod]
    public async Task IgnoreSysDiagramObjects_ComposesWithUserIgnoreSet()
    {
        var dbName = await CreateTestDatabaseAsync("DiagramsCompose");

        await ExecuteSqlAsync(dbName, "CREATE TABLE Assets (Id INT NOT NULL CONSTRAINT PK_Assets PRIMARY KEY)");
        await ExecuteSqlAsync(dbName, "CREATE TABLE MyTable (Id INT NOT NULL CONSTRAINT PK_MyTable PRIMARY KEY)");
        await ExecuteSqlAsync(dbName, @"
            CREATE TABLE sysdiagrams (
                name sysname NOT NULL,
                principal_id int NOT NULL,
                diagram_id int IDENTITY(1,1) PRIMARY KEY,
                version int,
                definition varbinary(max)
            )");

        var options = new SchemaHashOptions { ObjectNamesToIgnore = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "MyTable" }, IgnoreSysDiagramObjects = true };
        var schema = await ExtractSchemaAsync(dbName, options);

        schema.Tables.Any(t => t.Name.Equals("MyTable", StringComparison.OrdinalIgnoreCase)).ShouldBeFalse("A user-supplied ignore set must still apply alongside the diagram toggle");
        schema.Tables.Any(t => t.Name.Equals("sysdiagrams", StringComparison.OrdinalIgnoreCase)).ShouldBeFalse("Diagram exclusions must compose additively with the user-supplied ignore set");
        schema.Tables.Any(t => t.Name.Equals("Assets", StringComparison.OrdinalIgnoreCase)).ShouldBeTrue("Unrelated tables must still be extracted");
    }

    [TestMethod]
    public async Task UserDefinedTableType_IsExcluded_WhenInObjectNamesToIgnore()
    {
        // UDT extraction must honor ObjectNamesToIgnore, consistent with tables and procedures.
        var dbName = await CreateTestDatabaseAsync("UdtIgnore");

        await ExecuteSqlAsync(dbName, "CREATE TYPE dbo.IgnoredType AS TABLE (Id INT NOT NULL)");
        await ExecuteSqlAsync(dbName, "CREATE TYPE dbo.KeptType AS TABLE (Id INT NOT NULL)");

        var options = new SchemaHashOptions { ObjectNamesToIgnore = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "IgnoredType" } };
        var schema = await ExtractSchemaAsync(dbName, options);

        schema.UserDefinedTableTypes.Any(u => u.Name.Equals("IgnoredType", StringComparison.OrdinalIgnoreCase)).ShouldBeFalse("A UDT named in ObjectNamesToIgnore must be excluded, consistent with tables and procedures");
        schema.UserDefinedTableTypes.Any(u => u.Name.Equals("KeptType", StringComparison.OrdinalIgnoreCase)).ShouldBeTrue("Unrelated UDTs must still be extracted");
    }

    // ── New object kinds (views/functions/triggers/sequences/synonyms) ─────────────────────────────

    [TestMethod]
    public async Task ObjectNamesToIgnore_ExcludesEachNewObjectKind_BareName()
    {
        var dbName = await CreateDatabaseWithAsync("ignoreNewKinds",
            "CREATE TABLE dbo.T (Id INT NOT NULL)",
            "CREATE VIEW dbo.KeepView AS SELECT Id FROM dbo.T",
            "CREATE VIEW dbo.DropView AS SELECT Id FROM dbo.T",
            "CREATE FUNCTION dbo.KeepFunc() RETURNS INT AS BEGIN RETURN 1 END",
            "CREATE FUNCTION dbo.DropFunc() RETURNS INT AS BEGIN RETURN 1 END",
            "CREATE TRIGGER KeepTrigger ON dbo.T AFTER INSERT AS BEGIN SET NOCOUNT ON END",
            "CREATE TRIGGER DropTrigger ON dbo.T AFTER UPDATE AS BEGIN SET NOCOUNT ON END",
            "CREATE SEQUENCE dbo.KeepSeq AS INT START WITH 1",
            "CREATE SEQUENCE dbo.DropSeq AS INT START WITH 1",
            "CREATE SYNONYM dbo.KeepSyn FOR dbo.T",
            "CREATE SYNONYM dbo.DropSyn FOR dbo.T");

        var options = new SchemaHashOptions { ObjectNamesToIgnore = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "DropView", "DropFunc", "DropTrigger", "DropSeq", "DropSyn" } };
        var schema = await ExtractSchemaAsync(dbName, options);

        HasView(schema, "dbo", "KeepView").ShouldBeTrue();
        HasView(schema, "dbo", "DropView").ShouldBeFalse("A view named in ObjectNamesToIgnore is excluded");
        HasFunction(schema, "dbo", "KeepFunc").ShouldBeTrue();
        HasFunction(schema, "dbo", "DropFunc").ShouldBeFalse("A function named in ObjectNamesToIgnore is excluded");
        HasTrigger(schema, "dbo", "KeepTrigger").ShouldBeTrue();
        HasTrigger(schema, "dbo", "DropTrigger").ShouldBeFalse("A trigger named in ObjectNamesToIgnore is excluded");
        HasSequence(schema, "dbo", "KeepSeq").ShouldBeTrue();
        HasSequence(schema, "dbo", "DropSeq").ShouldBeFalse("A sequence named in ObjectNamesToIgnore is excluded");
        HasSynonym(schema, "dbo", "KeepSyn").ShouldBeTrue();
        HasSynonym(schema, "dbo", "DropSyn").ShouldBeFalse("A synonym named in ObjectNamesToIgnore is excluded");
    }

    [TestMethod]
    public async Task ObjectNamesToIgnore_SchemaQualified_TargetsSingleSchema_ForNewObjectKinds()
    {
        var dbName = await CreateDatabaseWithAsync("ignoreQualifiedNewKinds",
            "CREATE SCHEMA sales",
            "CREATE TABLE dbo.T (Id INT NOT NULL)",
            "CREATE TABLE sales.T (Id INT NOT NULL)",
            "CREATE VIEW dbo.SameView AS SELECT Id FROM dbo.T",
            "CREATE VIEW sales.SameView AS SELECT Id FROM sales.T");

        var options = new SchemaHashOptions { ObjectNamesToIgnore = new HashSet<string> { "sales.SameView" } };
        var schema = await ExtractSchemaAsync(dbName, options);

        HasView(schema, "dbo", "SameView").ShouldBeTrue("A schema-qualified entry must not affect another schema's same-named view");
        HasView(schema, "sales", "SameView").ShouldBeFalse("A schema-qualified entry excludes only that schema's view");
    }

    [TestMethod]
    public async Task SchemaFilter_ControlsInclusion_AcrossNewObjectKinds()
    {
        var dbName = await CreateDatabaseWithAsync("filterNewKinds",
            "CREATE SCHEMA sales",
            "CREATE TABLE dbo.T (Id INT NOT NULL)",
            "CREATE TABLE sales.T (Id INT NOT NULL)",
            "CREATE VIEW dbo.V AS SELECT Id FROM dbo.T",
            "CREATE VIEW sales.V AS SELECT Id FROM sales.T",
            "CREATE FUNCTION dbo.F() RETURNS INT AS BEGIN RETURN 1 END",
            "CREATE FUNCTION sales.F() RETURNS INT AS BEGIN RETURN 1 END",
            "CREATE TRIGGER TrigDbo ON dbo.T AFTER INSERT AS BEGIN SET NOCOUNT ON END",
            "CREATE TRIGGER TrigSales ON sales.T AFTER INSERT AS BEGIN SET NOCOUNT ON END",
            "CREATE SEQUENCE dbo.Seq AS INT START WITH 1",
            "CREATE SEQUENCE sales.Seq AS INT START WITH 1",
            "CREATE SYNONYM dbo.Syn FOR dbo.T",
            "CREATE SYNONYM sales.Syn FOR sales.T");

        var salesOnly = await ExtractSchemaAsync(dbName, new SchemaHashOptions { SchemaFilter = "sales" });

        HasView(salesOnly, "sales", "V").ShouldBeTrue();
        HasView(salesOnly, "dbo", "V").ShouldBeFalse();
        HasFunction(salesOnly, "sales", "F").ShouldBeTrue();
        HasFunction(salesOnly, "dbo", "F").ShouldBeFalse();
        HasTrigger(salesOnly, "sales", "TrigSales").ShouldBeTrue();
        HasTrigger(salesOnly, "dbo", "TrigDbo").ShouldBeFalse();
        HasSequence(salesOnly, "sales", "Seq").ShouldBeTrue();
        HasSequence(salesOnly, "dbo", "Seq").ShouldBeFalse();
        HasSynonym(salesOnly, "sales", "Syn").ShouldBeTrue();
        HasSynonym(salesOnly, "dbo", "Syn").ShouldBeFalse();
    }

    [TestMethod]
    public async Task SchemaFilter_ControlsInclusion_ForExtendedProperties()
    {
        var dbName = await CreateDatabaseWithAsync("filterExtProps",
            "CREATE SCHEMA sales",
            "CREATE TABLE dbo.T (Id INT NOT NULL)",
            "CREATE TABLE sales.T (Id INT NOT NULL)",
            "EXEC sys.sp_addextendedproperty @name = N'MS_Description', @value = N'dbo table', @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'T'",
            "EXEC sys.sp_addextendedproperty @name = N'MS_Description', @value = N'sales table', @level0type = N'SCHEMA', @level0name = N'sales', @level1type = N'TABLE', @level1name = N'T'",
            "EXEC sys.sp_addextendedproperty @name = N'AppVersion', @value = N'1.0'");

        var salesOnly = await ExtractSchemaAsync(dbName, new SchemaHashOptions { SchemaFilter = "sales" });

        salesOnly.ExtendedProperties.ShouldHaveSingleItem("Only the filtered schema's property survives; a database-scoped property has no schema and falls outside any schema filter");
        salesOnly.ExtendedProperties.Single().Value.ShouldBe("sales table");
    }

    [TestMethod]
    public async Task IgnoreSysDiagramObjects_ExcludesFnDiagramObjectsFunction()
    {
        var dbName = await CreateDatabaseWithAsync("diagramFunc",
            "CREATE FUNCTION dbo.fn_diagramobjects() RETURNS INT AS BEGIN RETURN 1 END",
            "CREATE FUNCTION dbo.RealFunc() RETURNS INT AS BEGIN RETURN 1 END");

        var withToggle = await ExtractSchemaAsync(dbName, new SchemaHashOptions { IgnoreSysDiagramObjects = true });
        HasFunction(withToggle, "dbo", "RealFunc").ShouldBeTrue("A normal function is retained");
        HasFunction(withToggle, "dbo", "fn_diagramobjects").ShouldBeFalse("fn_diagramobjects is now actually excluded now that functions are extracted");

        var defaultOptions = await ExtractSchemaAsync(dbName, Strict);
        HasFunction(defaultOptions, "dbo", "fn_diagramobjects").ShouldBeTrue("The toggle is off by default, so fn_diagramobjects is NOT excluded");
    }
}
