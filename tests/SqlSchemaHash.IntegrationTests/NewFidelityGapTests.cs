using zachtbeer.SqlSchemaHasher;
using Shouldly;

namespace SqlSchemaHash.IntegrationTests;

/// <summary>
/// Regression tests for the fidelity gaps closed alongside the typed-model refactor: each asserts
/// that a genuine schema difference the old string-packed model collapsed now changes the hash, and
/// pins the newly-captured metadata where practical.
/// </summary>
[TestClass]
public class NewFidelityGapTests : IntegrationTestBase
{
    #region Disabled indexes

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

    #endregion

    #region IGNORE_DUP_KEY

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

    #endregion

    #region Columnstore clustering distinction

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

    #endregion

    #region Typed XML columns

    [TestMethod]
    public async Task XmlColumn_TypedVsUntyped_ChangesHash_AndCollectionIsExtracted()
    {
        var db1Name = await CreateTestDatabaseAsync("XmlTyped1");
        var db2Name = await CreateTestDatabaseAsync("XmlUntyped2");

        const string schemaCollection = @"
            CREATE XML SCHEMA COLLECTION dbo.DocSchema AS
            '<xsd:schema xmlns:xsd=""http://www.w3.org/2001/XMLSchema""><xsd:element name=""root"" type=""xsd:string""/></xsd:schema>';";

        await ExecuteSqlAsync(db1Name, schemaCollection);
        await ExecuteSqlAsync(db1Name, "CREATE TABLE T (Id INT NOT NULL CONSTRAINT PK_T PRIMARY KEY, Doc XML(dbo.DocSchema) NULL)");
        await ExecuteSqlAsync(db2Name, "CREATE TABLE T (Id INT NOT NULL CONSTRAINT PK_T PRIMARY KEY, Doc XML NULL)");

        (await ExtractAndHashAsync(db1Name)).ShouldNotBe(await ExtractAndHashAsync(db2Name), "A typed xml column enforces schema-collection validation and must hash differently from untyped xml");

        var typed = (await ExtractSchemaAsync(db1Name)).Tables.Single(t => t.Name == "T").Columns.Single(c => c.Name == "Doc");
        typed.XmlSchemaCollectionName.ShouldBe("dbo.DocSchema", "A typed xml column must carry its schema-qualified collection name");

        var untyped = (await ExtractSchemaAsync(db2Name)).Tables.Single(t => t.Name == "T").Columns.Single(c => c.Name == "Doc");
        untyped.XmlSchemaCollectionName.ShouldBeNull("An untyped xml column has no schema collection");
    }

    #endregion

    #region Dynamic Data Masking

    [TestMethod]
    public async Task Column_Masked_ChangesHash_AndFunctionIsExtracted()
    {
        var db1Name = await CreateTestDatabaseAsync("Masked1");
        var db2Name = await CreateTestDatabaseAsync("Masked2");

        await ExecuteSqlAsync(db1Name, "CREATE TABLE T (Id INT NOT NULL CONSTRAINT PK_T PRIMARY KEY, Ssn VARCHAR(11) MASKED WITH (FUNCTION = 'default()') NULL)");
        await ExecuteSqlAsync(db2Name, "CREATE TABLE T (Id INT NOT NULL CONSTRAINT PK_T PRIMARY KEY, Ssn VARCHAR(11) NULL)");

        (await ExtractAndHashAsync(db1Name)).ShouldNotBe(await ExtractAndHashAsync(db2Name), "A masked column exposes data differently and must change the hash");

        var masked = (await ExtractSchemaAsync(db1Name)).Tables.Single(t => t.Name == "T").Columns.Single(c => c.Name == "Ssn");
        masked.IsMasked.ShouldBeTrue("A masked column must be reported as masked");
        masked.MaskingFunction.ShouldNotBeNull("A masked column must carry its masking function");
    }

    #endregion

    #region User-defined table type constraints and identity

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

    #endregion
}
