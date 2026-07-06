using zachtbeer.SqlSchemaHasher;
using Shouldly;

namespace SqlSchemaHash.IntegrationTests.Fidelity;

/// <summary>
/// Extended property fidelity: presence, name, value, value base type, and resolved target of every
/// captured property class (database, schema, object/column, parameter, index, table type) must be
/// reflected in the hash. Also covers the two scoping levers specific to this element: the
/// IgnoreExtendedProperties option, and property-follows-excluded-owner semantics.
/// </summary>
[TestClass]
public class ExtendedPropertyFidelityTests : IntegrationTestBase
{
    [TestMethod]
    public async Task AddingExtendedProperty_ChangesHash()
    {
        var dbName = await CreateTestDatabaseAsync("XpAdd");
        await ExecuteSqlAsync(dbName, "CREATE TABLE dbo.T (Id INT NOT NULL)");
        var hashBefore = await ExtractAndHashAsync(dbName);

        await ExecuteSqlAsync(dbName, "EXEC sys.sp_addextendedproperty @name = N'MS_Description', @value = N'Core table', @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'T'");
        var hashAfter = await ExtractAndHashAsync(dbName);

        hashBefore.ShouldNotBe(hashAfter, "Adding an extended property must change the hash");
    }

    [TestMethod]
    public async Task IdenticalExtendedProperties_AcrossDatabases_ProduceSameHash()
    {
        var db1 = await CreateTestDatabaseAsync("XpSame1");
        var db2 = await CreateTestDatabaseAsync("XpSame2");

        var ddl = new[]
        {
            "CREATE TABLE dbo.T (Id INT NOT NULL)",
            "EXEC sys.sp_addextendedproperty @name = N'MS_Description', @value = N'Core table', @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'T'",
            "EXEC sys.sp_addextendedproperty @name = N'MS_Description', @value = N'Primary key', @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'T', @level2type = N'COLUMN', @level2name = N'Id'",
        };
        foreach (var sql in ddl) await ExecuteSqlAsync(db1, sql);
        foreach (var sql in ddl) await ExecuteSqlAsync(db2, sql);

        (await ExtractAndHashAsync(db1)).ShouldBe(await ExtractAndHashAsync(db2), "Identical extended properties must produce the same hash");
    }

    [TestMethod]
    public async Task UpdatingValue_ChangesHash()
    {
        var dbName = await CreateTestDatabaseAsync("XpUpdate");
        await ExecuteSqlAsync(dbName, "CREATE TABLE dbo.T (Id INT NOT NULL)");
        await ExecuteSqlAsync(dbName, "EXEC sys.sp_addextendedproperty @name = N'MS_Description', @value = N'Core table', @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'T'");
        var hashBefore = await ExtractAndHashAsync(dbName);

        await ExecuteSqlAsync(dbName, "EXEC sys.sp_updateextendedproperty @name = N'MS_Description', @value = N'Archived table', @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'T'");
        var hashAfter = await ExtractAndHashAsync(dbName);

        hashBefore.ShouldNotBe(hashAfter, "Updating an extended property's value must change the hash");
    }

    [TestMethod]
    public async Task ColumnParameterIndexAndTableTypeTargets_AreCapturedDistinctly()
    {
        var dbName = await CreateTestDatabaseAsync("XpTargets");
        await ExecuteSqlAsync(dbName, "CREATE TABLE dbo.T (Id INT NOT NULL, Name NVARCHAR(50) NOT NULL)");
        await ExecuteSqlAsync(dbName, "CREATE INDEX IX_T_Name ON dbo.T(Name)");
        await ExecuteSqlAsync(dbName, "CREATE PROCEDURE dbo.P @x INT AS BEGIN SELECT @x END");
        await ExecuteSqlAsync(dbName, "CREATE TYPE dbo.TT AS TABLE (Id INT NOT NULL)");
        await ExecuteSqlAsync(dbName, "EXEC sys.sp_addextendedproperty @name = N'MS_Description', @value = N'Key column', @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'T', @level2type = N'COLUMN', @level2name = N'Id'");
        await ExecuteSqlAsync(dbName, "EXEC sys.sp_addextendedproperty @name = N'MS_Description', @value = N'Name index', @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'T', @level2type = N'INDEX', @level2name = N'IX_T_Name'");
        await ExecuteSqlAsync(dbName, "EXEC sys.sp_addextendedproperty @name = N'MS_Description', @value = N'Input value', @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'PROCEDURE', @level1name = N'P', @level2type = N'PARAMETER', @level2name = N'@x'");
        await ExecuteSqlAsync(dbName, "EXEC sys.sp_addextendedproperty @name = N'MS_Description', @value = N'Line items', @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TYPE', @level1name = N'TT'");

        var properties = (await ExtractSchemaAsync(dbName)).ExtendedProperties;

        var column = properties.Single(p => p.ClassDesc == "OBJECT_OR_COLUMN");
        (column.SchemaName, column.ObjectName, column.SubObjectName, column.Value).ShouldBe(("dbo", "T", "Id", "Key column"));
        var index = properties.Single(p => p.ClassDesc == "INDEX");
        (index.ObjectName, index.SubObjectName).ShouldBe(("T", "IX_T_Name"));
        var parameter = properties.Single(p => p.ClassDesc == "PARAMETER");
        (parameter.ObjectName, parameter.SubObjectName).ShouldBe(("P", "@x"));
        var tableType = properties.Single(p => p.ClassDesc == "TYPE");
        (tableType.SchemaName, tableType.ObjectName, tableType.SubObjectName).ShouldBe(("dbo", "TT", null));
    }

    [TestMethod]
    public async Task DatabaseAndSchemaScopedProperties_AreCapturedAndChangeHash()
    {
        var dbName = await CreateTestDatabaseAsync("XpScopes");
        await ExecuteSqlAsync(dbName, "CREATE TABLE dbo.T (Id INT NOT NULL)");
        var hashBefore = await ExtractAndHashAsync(dbName);

        await ExecuteSqlAsync(dbName, "EXEC sys.sp_addextendedproperty @name = N'AppVersion', @value = N'1.0'");
        await ExecuteSqlAsync(dbName, "EXEC sys.sp_addextendedproperty @name = N'MS_Description', @value = N'Default schema', @level0type = N'SCHEMA', @level0name = N'dbo'");
        var hashAfter = await ExtractAndHashAsync(dbName);
        hashBefore.ShouldNotBe(hashAfter, "Database- and schema-scoped extended properties must change the hash");

        var properties = (await ExtractSchemaAsync(dbName)).ExtendedProperties;
        var database = properties.Single(p => p.ClassDesc == "DATABASE");
        (database.SchemaName, database.ObjectName, database.Name, database.Value).ShouldBe((null, null, "AppVersion", "1.0"));
        var schema = properties.Single(p => p.ClassDesc == "SCHEMA");
        (schema.SchemaName, schema.ObjectName, schema.Value).ShouldBe(("dbo", null, "Default schema"));
    }

    [TestMethod]
    public async Task ValueBaseType_IsCapturedAndChangesHash()
    {
        // sp_addextendedproperty takes a sql_variant, so the same rendered text can arrive as
        // different underlying types; the captured base type must keep them distinct.
        var db1 = await CreateTestDatabaseAsync("XpVarInt");
        var db2 = await CreateTestDatabaseAsync("XpVarStr");

        await ExecuteSqlAsync(db1, "EXEC sys.sp_addextendedproperty @name = N'SchemaRevision', @value = 5");
        await ExecuteSqlAsync(db2, "EXEC sys.sp_addextendedproperty @name = N'SchemaRevision', @value = N'5'");

        var intProperty = (await ExtractSchemaAsync(db1)).ExtendedProperties.Single();
        (intProperty.ValueType, intProperty.Value).ShouldBe(("int", "5"));
        var stringProperty = (await ExtractSchemaAsync(db2)).ExtendedProperties.Single();
        (stringProperty.ValueType, stringProperty.Value).ShouldBe(("nvarchar", "5"));

        (await ExtractAndHashAsync(db1)).ShouldNotBe(await ExtractAndHashAsync(db2), "An int-typed and an nvarchar-typed extended property with the same rendered value must hash differently");
    }

    [TestMethod]
    public async Task IgnoreExtendedProperties_RemovesThemFromMetadataAndHash()
    {
        var db1 = await CreateTestDatabaseAsync("XpIgnore1");
        var db2 = await CreateTestDatabaseAsync("XpIgnore2");
        await ExecuteSqlAsync(db1, "CREATE TABLE dbo.T (Id INT NOT NULL)");
        await ExecuteSqlAsync(db2, "CREATE TABLE dbo.T (Id INT NOT NULL)");
        await ExecuteSqlAsync(db1, "EXEC sys.sp_addextendedproperty @name = N'MS_Description', @value = N'Core table', @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'T'");

        var ignoring = new SchemaHashOptions { IgnoreExtendedProperties = true };
        (await ExtractSchemaAsync(db1, ignoring)).ExtendedProperties.ShouldBeEmpty("IgnoreExtendedProperties must skip extraction entirely");
        (await ExtractAndHashAsync(db1, ignoring)).ShouldBe(await ExtractAndHashAsync(db2, ignoring), "Under IgnoreExtendedProperties, databases differing only in extended properties must hash equal");

        (await ExtractAndHashAsync(db1)).ShouldNotBe(await ExtractAndHashAsync(db2), "By default the extended property must still be compared");
    }

    [TestMethod]
    public async Task ExcludingObject_AlsoExcludesItsProperties()
    {
        var db1 = await CreateTestDatabaseAsync("XpExcl1");
        var db2 = await CreateTestDatabaseAsync("XpExcl2");

        // db1 carries a table with properties on the table, a column, and a trigger on the table;
        // db2 has nothing. Excluding the table must take every one of those properties with it,
        // including the trigger's (the trigger itself follows its excluded parent).
        await ExecuteSqlAsync(db1, "CREATE TABLE dbo.T (Id INT NOT NULL)");
        await ExecuteSqlAsync(db1, "CREATE TRIGGER dbo.trg_T ON dbo.T AFTER INSERT AS BEGIN SET NOCOUNT ON END");
        await ExecuteSqlAsync(db1, "EXEC sys.sp_addextendedproperty @name = N'MS_Description', @value = N'Core table', @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'T'");
        await ExecuteSqlAsync(db1, "EXEC sys.sp_addextendedproperty @name = N'MS_Description', @value = N'Key column', @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'T', @level2type = N'COLUMN', @level2name = N'Id'");
        await ExecuteSqlAsync(db1, "EXEC sys.sp_addextendedproperty @name = N'MS_Description', @value = N'Audit trigger', @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'T', @level2type = N'TRIGGER', @level2name = N'trg_T'");

        var excludingT = new SchemaHashOptions { ObjectNamesToIgnore = new HashSet<string> { "dbo.T" } };
        (await ExtractSchemaAsync(db1, excludingT)).ExtendedProperties.ShouldBeEmpty("Every property owned by the excluded table (directly, via a column, or via its trigger) must be excluded");
        (await ExtractAndHashAsync(db1, excludingT)).ShouldBe(await ExtractAndHashAsync(db2, excludingT), "An excluded table must take its extended properties with it");
    }
}
