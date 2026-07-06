using zachtbeer.SqlSchemaHasher;
using Shouldly;

namespace SqlSchemaHash.IntegrationTests;

/// <summary>
/// Regression tests for the fidelity/determinism bugs catalogued in KNOWN_BUGS.md. Each region
/// corresponds to a numbered finding: a "false negative" fix asserts that two genuinely different
/// schemas now hash differently, and extraction-level assertions pin the newly-captured metadata.
/// </summary>
[TestClass]
public class KnownBugFixTests : IntegrationTestBase
{
    #region Bug 1 — Column order is preserved

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
    public async Task TableColumns_OrderSwapped_ChangesHash()
    {
        var db1Name = await CreateTestDatabaseAsync("TblColOrder1");
        var db2Name = await CreateTestDatabaseAsync("TblColOrder2");

        await ExecuteSqlAsync(db1Name, "CREATE TABLE T (Id INT NOT NULL CONSTRAINT PK_T PRIMARY KEY, Alpha INT NOT NULL, Beta INT NOT NULL)");
        await ExecuteSqlAsync(db2Name, "CREATE TABLE T (Id INT NOT NULL CONSTRAINT PK_T PRIMARY KEY, Beta INT NOT NULL, Alpha INT NOT NULL)");

        (await ExtractAndHashAsync(db1Name)).ShouldNotBe(await ExtractAndHashAsync(db2Name), "Reordering table columns must change the hash now that ordinal position is preserved");
    }

    [TestMethod]
    public async Task Column_Ordinal_IsCapturedInDefinitionOrder()
    {
        var dbName = await CreateTestDatabaseAsync("ColOrdinal");

        await ExecuteSqlAsync(dbName, "CREATE TABLE T (Id INT NOT NULL CONSTRAINT PK_T PRIMARY KEY, Alpha INT NOT NULL, Beta INT NOT NULL)");

        var columns = (await ExtractSchemaAsync(dbName)).Tables.Single(t => t.Name == "T").Columns;

        columns.Single(c => c.Name == "Id").ColumnId.ShouldBe(1, "column_id must be captured as the ordinal");
        columns.Single(c => c.Name == "Alpha").ColumnId.ShouldBe(2);
        columns.Single(c => c.Name == "Beta").ColumnId.ShouldBe(3);
    }

    #endregion

    #region Bug 2 — Identity seed / increment / NOT FOR REPLICATION

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

    #endregion

    #region Bug 3 — Disabled / untrusted CHECK and FOREIGN KEY constraints

    [TestMethod]
    public async Task ForeignKey_DisabledAndUntrusted_ChangesHash()
    {
        var db1Name = await CreateTestDatabaseAsync("FkTrust1");
        var db2Name = await CreateTestDatabaseAsync("FkTrust2");

        const string baseSql = @"
            CREATE TABLE Parent (Id INT NOT NULL CONSTRAINT PK_Parent PRIMARY KEY);
            CREATE TABLE Child (Id INT NOT NULL CONSTRAINT PK_Child PRIMARY KEY, PId INT NULL);";
        await ExecuteSqlAsync(db1Name, baseSql);
        await ExecuteSqlAsync(db2Name, baseSql);

        // db1: enforced and trusted. db2: added WITH NOCHECK (untrusted) then disabled.
        await ExecuteSqlAsync(db1Name, "ALTER TABLE Child WITH CHECK ADD CONSTRAINT FK_Child_Parent FOREIGN KEY (PId) REFERENCES Parent(Id)");
        await ExecuteSqlAsync(db2Name, "ALTER TABLE Child WITH NOCHECK ADD CONSTRAINT FK_Child_Parent FOREIGN KEY (PId) REFERENCES Parent(Id)");
        await ExecuteSqlAsync(db2Name, "ALTER TABLE Child NOCHECK CONSTRAINT FK_Child_Parent");

        (await ExtractAndHashAsync(db1Name)).ShouldNotBe(await ExtractAndHashAsync(db2Name), "A disabled/untrusted foreign key has different enforcement semantics and must change the hash");
    }

    [TestMethod]
    public async Task CheckConstraint_Disabled_ChangesHash()
    {
        var db1Name = await CreateTestDatabaseAsync("CkDisabled1");
        var db2Name = await CreateTestDatabaseAsync("CkDisabled2");

        const string tableSql = "CREATE TABLE T (Id INT NOT NULL CONSTRAINT PK_T PRIMARY KEY, Age INT NOT NULL, CONSTRAINT CK_Age CHECK (Age >= 0))";
        await ExecuteSqlAsync(db1Name, tableSql);
        await ExecuteSqlAsync(db2Name, tableSql);

        await ExecuteSqlAsync(db2Name, "ALTER TABLE T NOCHECK CONSTRAINT CK_Age");

        (await ExtractAndHashAsync(db1Name)).ShouldNotBe(await ExtractAndHashAsync(db2Name), "A disabled CHECK constraint no longer enforces data integrity and must change the hash");
    }

    [TestMethod]
    public async Task ForeignKey_EnforcementState_IsExtracted()
    {
        var dbName = await CreateTestDatabaseAsync("FkStateMeta");

        await ExecuteSqlAsync(dbName, @"
            CREATE TABLE Parent (Id INT NOT NULL CONSTRAINT PK_Parent PRIMARY KEY);
            CREATE TABLE Child (Id INT NOT NULL CONSTRAINT PK_Child PRIMARY KEY, PId INT NULL);");
        await ExecuteSqlAsync(dbName, "ALTER TABLE Child WITH NOCHECK ADD CONSTRAINT FK_Child_Parent FOREIGN KEY (PId) REFERENCES Parent(Id)");
        await ExecuteSqlAsync(dbName, "ALTER TABLE Child NOCHECK CONSTRAINT FK_Child_Parent");

        var fk = (await ExtractSchemaAsync(dbName)).Tables.Single(t => t.Name == "Child").ForeignKeys.Single();
        fk.IsDisabled.ShouldBeTrue("A NOCHECK CONSTRAINT foreign key must be reported as disabled");
        fk.IsNotTrusted.ShouldBeTrue("A WITH NOCHECK foreign key must be reported as untrusted");
    }

    #endregion

    #region Bug 4 — Constraint names are configurable

    [TestMethod]
    public async Task CheckConstraint_Renamed_ChangesHash_UnderDefault_ButNotWhenIgnored()
    {
        // Same expression, different constraint name. PK is explicitly named identically in both
        // databases so the only difference is the CHECK constraint's name.
        var db1Name = await CreateTestDatabaseAsync("CkName1");
        var db2Name = await CreateTestDatabaseAsync("CkName2");

        await ExecuteSqlAsync(db1Name, "CREATE TABLE T (Id INT NOT NULL CONSTRAINT PK_T PRIMARY KEY, Age INT NOT NULL, CONSTRAINT CK_Age_Positive CHECK (Age >= 0))");
        await ExecuteSqlAsync(db2Name, "CREATE TABLE T (Id INT NOT NULL CONSTRAINT PK_T PRIMARY KEY, Age INT NOT NULL, CONSTRAINT CK_ValidAge CHECK (Age >= 0))");

        (await ExtractAndHashAsync(db1Name)).ShouldNotBe(await ExtractAndHashAsync(db2Name), "By default a renamed constraint must change the hash, mirroring exact index-name comparison");

        var ignoreNames = new SchemaHashOptions { Constraints = ConstraintNormalization.IgnoreNames };
        (await ExtractAndHashAsync(db1Name, ignoreNames)).ShouldBe(await ExtractAndHashAsync(db2Name, ignoreNames), "ConstraintNormalization.IgnoreNames must make a pure rename compare equal");
        (await ExtractAndHashAsync(db1Name, SchemaHashOptions.Structural)).ShouldBe(await ExtractAndHashAsync(db2Name, SchemaHashOptions.Structural), "The Structural preset ignores constraint names");
    }

    [TestMethod]
    public async Task ConstraintName_IsExtracted()
    {
        var dbName = await CreateTestDatabaseAsync("CkNameMeta");

        await ExecuteSqlAsync(dbName, "CREATE TABLE T (Id INT NOT NULL CONSTRAINT PK_T PRIMARY KEY, Age INT NOT NULL, CONSTRAINT CK_Age_Positive CHECK (Age >= 0))");

        var table = (await ExtractSchemaAsync(dbName)).Tables.Single(t => t.Name == "T");
        table.CheckConstraints.Single().Name.ShouldBe("CK_Age_Positive", "The constraint name must be captured");
        table.KeyConstraints.Single(c => c.Type == "PRIMARY KEY").Name.ShouldBe("PK_T");
    }

    #endregion

    #region Bug 6 — Encrypted stored procedures

    [TestMethod]
    public async Task EncryptedProcedure_UsesDistinctDefinitionSentinel()
    {
        // A WITH ENCRYPTION procedure has a NULL definition, so its body cannot be hashed. It must
        // still be distinguishable from the empty-definition fallback. Two different encrypted
        // procedures with the same signature remain indistinguishable — that limitation is inherent
        // because the server exposes nothing that reflects an encrypted body.
        var dbName = await CreateTestDatabaseAsync("EncProc");

        await ExecuteSqlAsync(dbName, "CREATE PROCEDURE Secret WITH ENCRYPTION AS BEGIN SELECT 42 END");
        await ExecuteSqlAsync(dbName, "CREATE PROCEDURE Plain AS BEGIN SELECT 42 END");

        var procs = (await ExtractSchemaAsync(dbName)).StoredProcedures;
        var encrypted = procs.Single(p => p.Name == "Secret");
        var plain = procs.Single(p => p.Name == "Plain");

        encrypted.DefinitionHash.ShouldBe("<encrypted>", "An encrypted procedure must carry a distinct sentinel rather than the empty-definition fallback");
        encrypted.DefinitionHash.ShouldNotBe(plain.DefinitionHash, "An encrypted procedure must not collide with an ordinary procedure's body hash");
    }

    [TestMethod]
    public async Task EncryptedProcedure_VsUnencrypted_ChangesHash()
    {
        var db1Name = await CreateTestDatabaseAsync("EncVsPlain1");
        var db2Name = await CreateTestDatabaseAsync("EncVsPlain2");

        await ExecuteSqlAsync(db1Name, "CREATE PROCEDURE Secret WITH ENCRYPTION AS BEGIN SELECT 42 END");
        await ExecuteSqlAsync(db2Name, "CREATE PROCEDURE Secret AS BEGIN SELECT 42 END");

        (await ExtractAndHashAsync(db1Name)).ShouldNotBe(await ExtractAndHashAsync(db2Name), "An encrypted procedure must hash differently from an otherwise-identical unencrypted one");
    }

    #endregion

    #region Bug 7 — SPARSE and ROWGUIDCOL column markers

    [TestMethod]
    public async Task Column_Sparse_ChangesHash_AndIsExtracted()
    {
        var db1Name = await CreateTestDatabaseAsync("Sparse1");
        var db2Name = await CreateTestDatabaseAsync("Sparse2");

        await ExecuteSqlAsync(db1Name, "CREATE TABLE T (Id INT NOT NULL CONSTRAINT PK_T PRIMARY KEY, Val INT SPARSE NULL)");
        await ExecuteSqlAsync(db2Name, "CREATE TABLE T (Id INT NOT NULL CONSTRAINT PK_T PRIMARY KEY, Val INT NULL)");

        (await ExtractAndHashAsync(db1Name)).ShouldNotBe(await ExtractAndHashAsync(db2Name), "A SPARSE column changes storage and NULL semantics and must change the hash");

        var val = (await ExtractSchemaAsync(db1Name)).Tables.Single(t => t.Name == "T").Columns.Single(c => c.Name == "Val");
        val.IsSparse.ShouldBeTrue("A SPARSE column must be reported as sparse");
    }

    [TestMethod]
    public async Task Column_RowGuidCol_ChangesHash_AndIsExtracted()
    {
        var db1Name = await CreateTestDatabaseAsync("RowGuid1");
        var db2Name = await CreateTestDatabaseAsync("RowGuid2");

        await ExecuteSqlAsync(db1Name, "CREATE TABLE T (Id INT NOT NULL CONSTRAINT PK_T PRIMARY KEY, G UNIQUEIDENTIFIER ROWGUIDCOL NOT NULL)");
        await ExecuteSqlAsync(db2Name, "CREATE TABLE T (Id INT NOT NULL CONSTRAINT PK_T PRIMARY KEY, G UNIQUEIDENTIFIER NOT NULL)");

        (await ExtractAndHashAsync(db1Name)).ShouldNotBe(await ExtractAndHashAsync(db2Name), "The ROWGUIDCOL marker is a real column attribute and must change the hash");

        var g = (await ExtractSchemaAsync(db1Name)).Tables.Single(t => t.Name == "T").Columns.Single(c => c.Name == "G");
        g.IsRowGuidCol.ShouldBeTrue("A ROWGUIDCOL column must be reported as such");
    }

    #endregion

    #region Bug 8 — Data type / parameter type schema-qualification

    [TestMethod]
    public async Task Column_UserDefinedTypeInDifferentSchema_ChangesHash()
    {
        // dbo.Money2 and staging.Money2 are distinct alias types over the same base type. Capturing
        // only the bare type name ("Money2") collided them.
        var db1Name = await CreateTestDatabaseAsync("AliasType1");
        var db2Name = await CreateTestDatabaseAsync("AliasType2");

        await ExecuteSqlAsync(db1Name, "CREATE TYPE dbo.Money2 FROM DECIMAL(9,2)");
        await ExecuteSqlAsync(db1Name, "CREATE TABLE T (Id INT NOT NULL CONSTRAINT PK_T PRIMARY KEY, Amt dbo.Money2 NULL)");

        await ExecuteSqlAsync(db2Name, "CREATE SCHEMA staging");
        await ExecuteSqlAsync(db2Name, "CREATE TYPE staging.Money2 FROM DECIMAL(9,2)");
        await ExecuteSqlAsync(db2Name, "CREATE TABLE T (Id INT NOT NULL CONSTRAINT PK_T PRIMARY KEY, Amt staging.Money2 NULL)");

        (await ExtractAndHashAsync(db1Name)).ShouldNotBe(await ExtractAndHashAsync(db2Name), "A column typed as a UDT in a different schema must change the hash");
    }

    [TestMethod]
    public async Task Column_DataType_IsSchemaQualifiedForUserTypes_AndBareForBuiltIns()
    {
        var dbName = await CreateTestDatabaseAsync("TypeQualify");

        await ExecuteSqlAsync(dbName, "CREATE TYPE dbo.Money2 FROM DECIMAL(9,2)");
        await ExecuteSqlAsync(dbName, "CREATE TABLE T (Id INT NOT NULL CONSTRAINT PK_T PRIMARY KEY, Amt dbo.Money2 NULL)");

        var columns = (await ExtractSchemaAsync(dbName)).Tables.Single(t => t.Name == "T").Columns;
        columns.Single(c => c.Name == "Amt").DataType.ShouldBe("dbo.Money2", "A user-defined type must be schema-qualified");
        columns.Single(c => c.Name == "Id").DataType.ShouldBe("int", "A built-in type must remain a bare name");
    }

    [TestMethod]
    public async Task Parameter_TableValuedType_IsSchemaQualified()
    {
        var dbName = await CreateTestDatabaseAsync("ParamTypeQualify");

        await ExecuteSqlAsync(dbName, "CREATE TYPE dbo.IdList AS TABLE (Id INT NOT NULL)");
        await ExecuteSqlAsync(dbName, "CREATE PROCEDURE P @ids dbo.IdList READONLY, @n INT AS BEGIN SELECT 1 END");

        var proc = (await ExtractSchemaAsync(dbName)).StoredProcedures.Single(p => p.Name == "P");
        proc.Parameters.Single(p => p.Name == "ids").Type.ShouldBe("dbo.IdList", "A table-valued parameter's type must be schema-qualified");
        proc.Parameters.Single(p => p.Name == "n").Type.ShouldBe("int", "A built-in parameter type must remain a bare name");
    }

    #endregion
}
