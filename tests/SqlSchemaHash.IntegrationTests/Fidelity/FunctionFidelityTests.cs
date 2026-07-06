using zachtbeer.SqlSchemaHasher;
using Shouldly;

namespace SqlSchemaHash.IntegrationTests.Fidelity;

/// <summary>
/// Function schema fidelity: scalar functions, inline table-valued functions, and multi-statement
/// table-valued functions must all be captured with their TypeDesc distinguishing shape even when
/// body text is ignored, and their parameters (including a scalar function's return-type row)
/// participate in the hash the same way stored procedure parameters do.
/// </summary>
[TestClass]
public class FunctionFidelityTests : IntegrationTestBase
{
    [TestMethod]
    public async Task ScalarFunctionBody_DifferentBodies_ProduceDifferentHashes()
    {
        var db1 = await CreateTestDatabaseAsync("FuncBody1");
        var db2 = await CreateTestDatabaseAsync("FuncBody2");

        await ExecuteSqlAsync(db1, "CREATE FUNCTION dbo.DoubleValue(@x INT) RETURNS INT AS BEGIN RETURN @x * 2 END");
        await ExecuteSqlAsync(db2, "CREATE FUNCTION dbo.DoubleValue(@x INT) RETURNS INT AS BEGIN RETURN @x * 3 END");

        (await ExtractAndHashAsync(db1)).ShouldNotBe(await ExtractAndHashAsync(db2), "Functions with different bodies must produce different hashes");
    }

    [TestMethod]
    public async Task ScalarFunction_IdenticalAcrossDatabases_ProducesSameHash()
    {
        var db1 = await CreateTestDatabaseAsync("FuncSame1");
        var db2 = await CreateTestDatabaseAsync("FuncSame2");

        const string ddl = "CREATE FUNCTION dbo.DoubleValue(@x INT) RETURNS INT AS BEGIN RETURN @x * 2 END";
        await ExecuteSqlAsync(db1, ddl);
        await ExecuteSqlAsync(db2, ddl);

        (await ExtractAndHashAsync(db1)).ShouldBe(await ExtractAndHashAsync(db2), "Identical scalar functions must produce the same hash");
    }

    [TestMethod]
    public async Task ScalarFunction_ReturnTypeChange_ChangesHash_EvenUnderIgnoreBodyText()
    {
        var db1 = await CreateTestDatabaseAsync("FuncRet1");
        var db2 = await CreateTestDatabaseAsync("FuncRet2");

        await ExecuteSqlAsync(db1, "CREATE FUNCTION dbo.GetVal() RETURNS INT AS BEGIN RETURN 1 END");
        await ExecuteSqlAsync(db2, "CREATE FUNCTION dbo.GetVal() RETURNS BIGINT AS BEGIN RETURN 1 END");

        var ignoreBody = new SchemaHashOptions { Modules = ModuleNormalization.IgnoreBodyText };
        (await ExtractAndHashAsync(db1, ignoreBody)).ShouldNotBe(await ExtractAndHashAsync(db2, ignoreBody), "A return-type change must change the hash even when body text is ignored, since the return type is carried on the parameter_id = 0 row");
    }

    [TestMethod]
    public async Task ScalarFunction_ParameterAddOrTypeChange_ChangesHash()
    {
        var db1 = await CreateTestDatabaseAsync("FuncParam1");
        var db2 = await CreateTestDatabaseAsync("FuncParam2");
        var db3 = await CreateTestDatabaseAsync("FuncParam3");

        await ExecuteSqlAsync(db1, "CREATE FUNCTION dbo.Calc(@x INT) RETURNS INT AS BEGIN RETURN @x END");
        await ExecuteSqlAsync(db2, "CREATE FUNCTION dbo.Calc(@x INT, @y INT) RETURNS INT AS BEGIN RETURN @x + @y END");
        await ExecuteSqlAsync(db3, "CREATE FUNCTION dbo.Calc(@x BIGINT) RETURNS INT AS BEGIN RETURN @x END");

        (await ExtractAndHashAsync(db1)).ShouldNotBe(await ExtractAndHashAsync(db2), "Adding a parameter must change the hash");
        (await ExtractAndHashAsync(db1)).ShouldNotBe(await ExtractAndHashAsync(db3), "Changing a parameter's type must change the hash");
    }

    [TestMethod]
    public async Task TypeDesc_DistinguishesInlineFromMultiStatementTvf_EvenUnderIgnoreBodyText()
    {
        var db1 = await CreateTestDatabaseAsync("FuncTvf1");
        var db2 = await CreateTestDatabaseAsync("FuncTvf2");

        await ExecuteSqlAsync(db1, "CREATE FUNCTION dbo.GetItems() RETURNS TABLE AS RETURN (SELECT 1 AS Id)");
        await ExecuteSqlAsync(db2, @"
            CREATE FUNCTION dbo.GetItems() RETURNS @Result TABLE (Id INT) AS
            BEGIN
                INSERT INTO @Result VALUES (1)
                RETURN
            END");

        var ignoreBody = new SchemaHashOptions { Modules = ModuleNormalization.IgnoreBodyText };
        (await ExtractAndHashAsync(db1, ignoreBody)).ShouldNotBe(await ExtractAndHashAsync(db2, ignoreBody), "An inline TVF and a multi-statement TVF must not collide, even with body text ignored");

        (await ExtractSchemaAsync(db1)).Functions.Single(f => f.Name == "GetItems").TypeDesc.ShouldBe("SQL_INLINE_TABLE_VALUED_FUNCTION");
        (await ExtractSchemaAsync(db2)).Functions.Single(f => f.Name == "GetItems").TypeDesc.ShouldBe("SQL_TABLE_VALUED_FUNCTION");
    }

    [TestMethod]
    public async Task AliasTypedParameter_GetsBaseTypeEnrichment()
    {
        var dbName = await CreateTestDatabaseAsync("FuncAliasParam");

        await ExecuteSqlAsync(dbName, "CREATE TYPE dbo.Money9 FROM DECIMAL(9,2) NOT NULL");
        await ExecuteSqlAsync(dbName, "CREATE FUNCTION dbo.Tax(@amount dbo.Money9) RETURNS DECIMAL(9,2) AS BEGIN RETURN @amount * 0.1 END");

        var function = (await ExtractSchemaAsync(dbName)).Functions.Single(f => f.Name == "Tax");
        var param = function.Parameters.Single(p => p.Name == "amount");
        param.Type.ShouldBe("dbo.Money9{decimal(9,2) NOT NULL}", "An alias-typed parameter must carry its underlying base type enrichment");
    }

    [TestMethod]
    public async Task ScalarFunction_ReturnRow_IsCapturedAsParameterIdZero()
    {
        var dbName = await CreateTestDatabaseAsync("FuncReturnRow");
        await ExecuteSqlAsync(dbName, "CREATE FUNCTION dbo.GetVal() RETURNS INT AS BEGIN RETURN 1 END");

        var function = (await ExtractSchemaAsync(dbName)).Functions.Single(f => f.Name == "GetVal");
        var returnParam = function.Parameters.ShouldHaveSingleItem();
        returnParam.Name.ShouldBe(string.Empty, "A scalar function's return row carries an empty parameter name");
        returnParam.IsOutput.ShouldBeTrue("A scalar function's return row is reported as an output parameter");
        returnParam.Type.ShouldBe("int");
    }
}
