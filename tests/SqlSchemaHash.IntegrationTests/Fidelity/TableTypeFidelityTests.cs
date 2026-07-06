using zachtbeer.SqlSchemaHasher;
using Shouldly;

namespace SqlSchemaHash.IntegrationTests.Fidelity;

/// <summary>
/// User-defined table type (TVP) schema fidelity: a table type marshals positionally and carries its
/// own constraints, so a genuine difference in column order, a PRIMARY KEY, a CHECK constraint, or an
/// identity column must change the hash, with the captured metadata pinned.
/// </summary>
[TestClass]
public class TableTypeFidelityTests : IntegrationTestBase
{
    [TestMethod]
    public async Task UserDefinedTableType_ColumnOrderSwapped_ChangesHash()
    {
        // A TVP marshals its columns positionally, so (Sku, Qty) and (Qty, Sku) are genuinely
        // different types. Sorting columns by name discarded that difference.
        var db1Name = await CreateTestDatabaseAsync("UdtColOrder1");
        var db2Name = await CreateTestDatabaseAsync("UdtColOrder2");

        await ExecuteSqlAsync(db1Name, "CREATE TYPE dbo.OrderLine AS TABLE (Sku NVARCHAR(20) NOT NULL, Qty INT NOT NULL)");
        await ExecuteSqlAsync(db2Name, "CREATE TYPE dbo.OrderLine AS TABLE (Qty INT NOT NULL, Sku NVARCHAR(20) NOT NULL)");

        (await ExtractAndHashAsync(db1Name)).ShouldNotBe(await ExtractAndHashAsync(db2Name), "A table type whose columns are defined in a different order is a different type and must change the hash");
    }

    [TestMethod]
    public async Task UserDefinedTableType_WithPrimaryKey_ChangesHash_AndIsExtracted()
    {
        var db1Name = await CreateTestDatabaseAsync("UdttPk1");
        var db2Name = await CreateTestDatabaseAsync("UdttPk2");

        await ExecuteSqlAsync(db1Name, "CREATE TYPE dbo.IdList AS TABLE (Id INT NOT NULL PRIMARY KEY)");
        await ExecuteSqlAsync(db2Name, "CREATE TYPE dbo.IdList AS TABLE (Id INT NOT NULL)");

        (await ExtractAndHashAsync(db1Name)).ShouldNotBe(await ExtractAndHashAsync(db2Name), "A primary key on a table type changes its validation/marshalling semantics and must change the hash");

        var udt = (await ExtractSchemaAsync(db1Name)).UserDefinedTableTypes.Single(u => u.Name == "IdList");
        udt.KeyConstraints.ShouldContain(c => c.Type == "PRIMARY KEY", "A table type's PRIMARY KEY must be captured");
    }

    [TestMethod]
    public async Task UserDefinedTableType_WithCheckConstraint_ChangesHash_AndIsExtracted()
    {
        var db1Name = await CreateTestDatabaseAsync("UdttCk1");
        var db2Name = await CreateTestDatabaseAsync("UdttCk2");

        await ExecuteSqlAsync(db1Name, "CREATE TYPE dbo.Qtys AS TABLE (Qty INT NOT NULL CHECK (Qty > 0))");
        await ExecuteSqlAsync(db2Name, "CREATE TYPE dbo.Qtys AS TABLE (Qty INT NOT NULL)");

        (await ExtractAndHashAsync(db1Name)).ShouldNotBe(await ExtractAndHashAsync(db2Name), "A CHECK constraint on a table type is part of its definition and must change the hash");

        var udt = (await ExtractSchemaAsync(db1Name)).UserDefinedTableTypes.Single(u => u.Name == "Qtys");
        udt.CheckConstraints.ShouldNotBeEmpty("A table type's CHECK constraint must be captured");
    }

    [TestMethod]
    public async Task UserDefinedTableType_WithIdentity_ChangesHash_AndIsExtracted()
    {
        var db1Name = await CreateTestDatabaseAsync("UdttId1");
        var db2Name = await CreateTestDatabaseAsync("UdttId2");

        await ExecuteSqlAsync(db1Name, "CREATE TYPE dbo.Rows AS TABLE (Id INT IDENTITY(1,1) NOT NULL, Val INT NOT NULL)");
        await ExecuteSqlAsync(db2Name, "CREATE TYPE dbo.Rows AS TABLE (Id INT NOT NULL, Val INT NOT NULL)");

        (await ExtractAndHashAsync(db1Name)).ShouldNotBe(await ExtractAndHashAsync(db2Name), "An identity column on a table type is part of its definition and must change the hash");

        var udt = (await ExtractSchemaAsync(db1Name)).UserDefinedTableTypes.Single(u => u.Name == "Rows");
        udt.IdentityColumn.ShouldBe("Id", "A table type's identity column must be captured");
    }
}
