using System.Globalization;
using zachtbeer.SqlSchemaHasher;
using Shouldly;

namespace SqlSchemaHash.IntegrationTests;

/// <summary>
/// Tests covering hash determinism (same schema must always produce the same hash,
/// regardless of machine culture or DDL statement ordering) and schema fidelity
/// (real schema differences must produce different hashes).
/// </summary>
[TestClass]
public class SchemaDeterminismAndFidelityTests : IntegrationTestBase
{
    #region Index Included Columns

    [TestMethod]
    public async Task IndexIncludedColumns_DdlIncludeOrder_DoesNotAffectHash()
    {
        // INCLUDE columns are an unordered set: INCLUDE (A, B) and INCLUDE (B, A)
        // define the same index and must produce the same hash.
        var db1Name = await CreateTestDatabaseAsync("IncludeOrder1");
        var db2Name = await CreateTestDatabaseAsync("IncludeOrder2");

        const string tableSql = @"
            CREATE TABLE Assets (
                Id INT NOT NULL CONSTRAINT PK_Assets PRIMARY KEY,
                Name NVARCHAR(100) NOT NULL,
                Created DATETIME2 NOT NULL,
                Modified DATETIME2 NOT NULL
            )";

        await ExecuteSqlAsync(db1Name, tableSql);
        await ExecuteSqlAsync(db2Name, tableSql);

        await ExecuteSqlAsync(db1Name, "CREATE INDEX IX_Assets_Name ON Assets(Name) INCLUDE (Created, Modified)");
        await ExecuteSqlAsync(db2Name, "CREATE INDEX IX_Assets_Name ON Assets(Name) INCLUDE (Modified, Created)");

        var hash1 = await ExtractAndHashAsync(db1Name);
        var hash2 = await ExtractAndHashAsync(db2Name);

        hash1.ShouldBe(hash2, "The order of columns in an INCLUDE clause is not semantically meaningful and must not affect the hash");
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

        index.Keys.ShouldNotBeNull();
        index.Keys.ShouldStartWith("Name", customMessage: "The key column must come first; included columns must not be sorted in front of it");
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

    #endregion

    #region Culture-Independent Sorting

    [TestMethod]
    public void SchemaHash_IsIndependentOfCurrentCulture()
    {
        // Sorting must be ordinal: under en-US "Åland" sorts before "Borders" (Å ~ A),
        // under da-DK it sorts after (Å is the last letter of the Danish alphabet).
        // The hash of the same metadata must not depend on the machine's culture.
        var enUs = new CultureInfo("en-US");
        var daDk = new CultureInfo("da-DK");

        if (Math.Sign(string.Compare("Åland", "Borders", enUs, CompareOptions.None)) == Math.Sign(string.Compare("Åland", "Borders", daDk, CompareOptions.None)))
            Assert.Inconclusive("This environment's ICU data does not order the probe strings differently between en-US and da-DK, so the test cannot detect culture-sensitive sorting.");

        var columns = new List<ColumnSchema> { new("Id", "int", 4, 10, 0, false) };
        var tables = new List<TableSchema>
        {
            new("dbo", "Åland", columns, new List<IndexSchema>(), new List<ConstraintSchema>(), null),
            new("dbo", "Borders", columns, new List<IndexSchema>(), new List<ConstraintSchema>(), null)
        };
        var schema = new SchemaMetadata(tables, new List<StoredProcedureSchema>(), new List<UserDefinedTableTypeSchema>());

        var hashEnUs = ComputeHashWithCulture(schema, enUs);
        var hashDaDk = ComputeHashWithCulture(schema, daDk);

        hashEnUs.ShouldBe(hashDaDk, "The hash of identical schema metadata must not depend on the current culture");
    }

    private static string ComputeHashWithCulture(SchemaMetadata schema, CultureInfo culture)
    {
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = culture;
            return new SchemaHashCalculator().ComputeHash(schema);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    #endregion

    #region Constraint Fidelity

    [TestMethod]
    public async Task ForeignKey_ReferencingDifferentTable_ChangesHash()
    {
        var db1Name = await CreateTestDatabaseAsync("FkTarget1");
        var db2Name = await CreateTestDatabaseAsync("FkTarget2");

        const string baseSql = @"
            CREATE TABLE ParentA (Id INT NOT NULL CONSTRAINT PK_ParentA PRIMARY KEY);
            CREATE TABLE ParentB (Id INT NOT NULL CONSTRAINT PK_ParentB PRIMARY KEY);
            CREATE TABLE Child (Id INT NOT NULL CONSTRAINT PK_Child PRIMARY KEY, ParentId INT NOT NULL);";

        await ExecuteSqlAsync(db1Name, baseSql);
        await ExecuteSqlAsync(db2Name, baseSql);

        await ExecuteSqlAsync(db1Name, "ALTER TABLE Child ADD CONSTRAINT FK_Child_Parent FOREIGN KEY (ParentId) REFERENCES ParentA(Id)");
        await ExecuteSqlAsync(db2Name, "ALTER TABLE Child ADD CONSTRAINT FK_Child_Parent FOREIGN KEY (ParentId) REFERENCES ParentB(Id)");

        var hash1 = await ExtractAndHashAsync(db1Name);
        var hash2 = await ExtractAndHashAsync(db2Name);

        hash1.ShouldNotBe(hash2, "A foreign key pointing at a different table is a schema difference and must change the hash");
    }

    [TestMethod]
    public async Task ForeignKey_ReferencingDifferentColumn_ChangesHash()
    {
        var db1Name = await CreateTestDatabaseAsync("FkColumn1");
        var db2Name = await CreateTestDatabaseAsync("FkColumn2");

        const string baseSql = @"
            CREATE TABLE Parent (
                Id INT NOT NULL CONSTRAINT PK_Parent PRIMARY KEY,
                AltId INT NOT NULL CONSTRAINT UQ_Parent_AltId UNIQUE
            );
            CREATE TABLE Child (Id INT NOT NULL CONSTRAINT PK_Child PRIMARY KEY, ParentRef INT NOT NULL);";

        await ExecuteSqlAsync(db1Name, baseSql);
        await ExecuteSqlAsync(db2Name, baseSql);

        await ExecuteSqlAsync(db1Name, "ALTER TABLE Child ADD CONSTRAINT FK_Child_Parent FOREIGN KEY (ParentRef) REFERENCES Parent(Id)");
        await ExecuteSqlAsync(db2Name, "ALTER TABLE Child ADD CONSTRAINT FK_Child_Parent FOREIGN KEY (ParentRef) REFERENCES Parent(AltId)");

        var hash1 = await ExtractAndHashAsync(db1Name);
        var hash2 = await ExtractAndHashAsync(db2Name);

        hash1.ShouldNotBe(hash2, "A foreign key referencing a different column of the parent table is a schema difference and must change the hash");
    }

    [TestMethod]
    public async Task DefaultConstraint_MovedToDifferentColumn_ChangesHash()
    {
        var db1Name = await CreateTestDatabaseAsync("DefaultCol1");
        var db2Name = await CreateTestDatabaseAsync("DefaultCol2");

        await ExecuteSqlAsync(db1Name, @"
            CREATE TABLE Settings (
                Id INT NOT NULL CONSTRAINT PK_Settings PRIMARY KEY,
                ColA INT NOT NULL CONSTRAINT DF_Settings_Value DEFAULT 0,
                ColB INT NOT NULL
            )");

        await ExecuteSqlAsync(db2Name, @"
            CREATE TABLE Settings (
                Id INT NOT NULL CONSTRAINT PK_Settings PRIMARY KEY,
                ColA INT NOT NULL,
                ColB INT NOT NULL CONSTRAINT DF_Settings_Value DEFAULT 0
            )");

        var hash1 = await ExtractAndHashAsync(db1Name);
        var hash2 = await ExtractAndHashAsync(db2Name);

        hash1.ShouldNotBe(hash2, "A default constraint applied to a different column is a schema difference and must change the hash");
    }

    #endregion

    #region Stored Procedure Hashing Strategy

    [TestMethod]
    public void HashBytesSupport_IsDeterminedByVersionAndEngineEdition()
    {
        // Azure SQL Database reports major version 12 but supports unlimited HASHBYTES;
        // the decision must consider the engine edition, not just the version number.
        SchemaExtractor.SupportsUnlimitedHashBytes(12, 5).ShouldBeTrue("Azure SQL Database (EngineEdition 5) supports unlimited HASHBYTES despite reporting version 12");
        SchemaExtractor.SupportsUnlimitedHashBytes(12, 8).ShouldBeTrue("Azure SQL Managed Instance (EngineEdition 8) supports unlimited HASHBYTES");
        SchemaExtractor.SupportsUnlimitedHashBytes(13, 3).ShouldBeTrue("On-prem SQL Server 2016+ supports unlimited HASHBYTES");
        SchemaExtractor.SupportsUnlimitedHashBytes(11, 3).ShouldBeFalse("On-prem SQL Server 2012 has the 8000-byte HASHBYTES limit");
    }

    #endregion

    #region Identity Detection on Quoted Names

    [TestMethod]
    public async Task IdentityColumn_OnTableNameWithSpace_IsDetected()
    {
        var dbName = await CreateTestDatabaseAsync("IdentitySpace");

        await ExecuteSqlAsync(dbName, @"
            CREATE TABLE [Order Details] (
                OrderDetailId INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_OrderDetails PRIMARY KEY,
                Quantity INT NOT NULL
            )");

        var schema = await ExtractSchemaAsync(dbName);
        var table = schema.Tables.Single(t => t.Name == "Order Details");
        table.IdentityColumn.ShouldBe("OrderDetailId", "Identity columns must be detected when the table name contains a space");
    }

    [TestMethod]
    public async Task IdentityColumn_OnTableNameWithDot_IsDetected()
    {
        // A dot in the table name breaks unquoted OBJECT_ID resolution
        // ('dbo.Order.Details' parses as database.schema.object) unless QUOTENAME is used.
        var db1Name = await CreateTestDatabaseAsync("IdentityDot1");
        var db2Name = await CreateTestDatabaseAsync("IdentityDot2");

        await ExecuteSqlAsync(db1Name, @"
            CREATE TABLE [Order.Details] (
                OrderDetailId INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_OrderDetails PRIMARY KEY,
                Quantity INT NOT NULL
            )");

        await ExecuteSqlAsync(db2Name, @"
            CREATE TABLE [Order.Details] (
                OrderDetailId INT NOT NULL CONSTRAINT PK_OrderDetails PRIMARY KEY,
                Quantity INT NOT NULL
            )");

        var schema1 = await ExtractSchemaAsync(db1Name);
        var table1 = schema1.Tables.Single(t => t.Name == "Order.Details");
        table1.IdentityColumn.ShouldBe("OrderDetailId", "Identity columns must be detected even when the table name contains a dot");

        var hash1 = await ExtractAndHashAsync(db1Name);
        var hash2 = await ExtractAndHashAsync(db2Name);
        hash1.ShouldNotBe(hash2, "Presence of an identity column is a schema difference and must change the hash");
    }

    #endregion

    #region Computed Columns

    [TestMethod]
    public async Task ComputedColumn_DifferentFormula_ChangesHash()
    {
        // The formula lives in sys.computed_columns, not sys.columns; a formula change is invisible
        // unless it is captured and hashed.
        var db1Name = await CreateTestDatabaseAsync("ComputedFormula1");
        var db2Name = await CreateTestDatabaseAsync("ComputedFormula2");

        await ExecuteSqlAsync(db1Name, @"
            CREATE TABLE LineItems (
                Id INT NOT NULL CONSTRAINT PK_LineItems PRIMARY KEY,
                Price INT NOT NULL,
                Qty INT NOT NULL,
                Total AS (Price * Qty)
            )");
        await ExecuteSqlAsync(db2Name, @"
            CREATE TABLE LineItems (
                Id INT NOT NULL CONSTRAINT PK_LineItems PRIMARY KEY,
                Price INT NOT NULL,
                Qty INT NOT NULL,
                Total AS (Price * Qty * 2)
            )");

        var hash1 = await ExtractAndHashAsync(db1Name);
        var hash2 = await ExtractAndHashAsync(db2Name);

        hash1.ShouldNotBe(hash2, "Changing a computed column's formula is a schema difference and must change the hash");
    }

    [TestMethod]
    public async Task ComputedColumn_PersistedVsNonPersisted_ChangesHash()
    {
        var db1Name = await CreateTestDatabaseAsync("ComputedPersist1");
        var db2Name = await CreateTestDatabaseAsync("ComputedPersist2");

        await ExecuteSqlAsync(db1Name, @"
            CREATE TABLE LineItems (
                Id INT NOT NULL CONSTRAINT PK_LineItems PRIMARY KEY,
                Price INT NOT NULL,
                Qty INT NOT NULL,
                Total AS (Price * Qty)
            )");
        await ExecuteSqlAsync(db2Name, @"
            CREATE TABLE LineItems (
                Id INT NOT NULL CONSTRAINT PK_LineItems PRIMARY KEY,
                Price INT NOT NULL,
                Qty INT NOT NULL,
                Total AS (Price * Qty) PERSISTED
            )");

        var hash1 = await ExtractAndHashAsync(db1Name);
        var hash2 = await ExtractAndHashAsync(db2Name);

        hash1.ShouldNotBe(hash2, "Whether a computed column is PERSISTED is a physical schema difference and must change the hash");
    }

    [TestMethod]
    public async Task ComputedColumn_IsExtractedWithDefinition_PlainColumnIsNot()
    {
        var dbName = await CreateTestDatabaseAsync("ComputedExtract");

        await ExecuteSqlAsync(dbName, @"
            CREATE TABLE LineItems (
                Id INT NOT NULL CONSTRAINT PK_LineItems PRIMARY KEY,
                Price INT NOT NULL,
                Qty INT NOT NULL,
                Total AS (Price * Qty) PERSISTED
            )");

        var columns = (await ExtractSchemaAsync(dbName)).Tables.Single(t => t.Name == "LineItems").Columns;

        var computed = columns.Single(c => c.Name == "Total");
        computed.IsComputed.ShouldBeTrue("A computed column must be flagged as computed");
        computed.ComputedDefinition.ShouldNotBeNull("A computed column must carry its formula");
        computed.ComputedDefinition.ShouldContain("Price", customMessage: "The captured formula must reference its source columns");
        computed.IsPersisted.ShouldBeTrue("A PERSISTED computed column must be reported as persisted");

        var plain = columns.Single(c => c.Name == "Price");
        plain.IsComputed.ShouldBeFalse("An ordinary column must not be flagged as computed");
        plain.ComputedDefinition.ShouldBeNull("An ordinary column must not carry a formula");
    }

    #endregion

    #region Integer Serialization

    [TestMethod]
    public void SchemaHash_IntegerSerialization_IsLittleEndianAndStable()
    {
        // AppendInt and AppendString's length prefix write little-endian explicitly so the hash
        // does not depend on the host's byte order. This golden vector pins that layout: a change
        // to the constant means the hash wire format changed and previously-stored hashes are invalid.
        var columns = new List<ColumnSchema> { new("Id", "int", 4, 10, 0, false) };
        var tables = new List<TableSchema>
        {
            new("dbo", "T", columns, new List<IndexSchema>(), new List<ConstraintSchema>(), null)
        };
        var schema = new SchemaMetadata(tables, new List<StoredProcedureSchema>(), new List<UserDefinedTableTypeSchema>());

        new SchemaHashCalculator().ComputeHash(schema)
            .ShouldBe("b8f379e55fcbcac59abb75dea5ac5ddc73675b1d8b986e0bc1ff13ff3f0ada7d", "The integer byte layout of the hash must remain stable and independent of host endianness");
    }

    #endregion

    #region Foreign Key Referential Actions

    [TestMethod]
    public async Task ForeignKey_DifferentDeleteAction_ChangesHash()
    {
        // ON DELETE CASCADE vs NO ACTION is a behavioral schema difference held in
        // sys.foreign_keys.delete_referential_action_desc, not in the FK's column list.
        var db1Name = await CreateTestDatabaseAsync("FkAction1");
        var db2Name = await CreateTestDatabaseAsync("FkAction2");

        const string baseSql = @"
            CREATE TABLE Parent (Id INT NOT NULL CONSTRAINT PK_Parent PRIMARY KEY);
            CREATE TABLE Child (Id INT NOT NULL CONSTRAINT PK_Child PRIMARY KEY, ParentId INT NULL);";

        await ExecuteSqlAsync(db1Name, baseSql);
        await ExecuteSqlAsync(db2Name, baseSql);

        await ExecuteSqlAsync(db1Name, "ALTER TABLE Child ADD CONSTRAINT FK_Child_Parent FOREIGN KEY (ParentId) REFERENCES Parent(Id) ON DELETE CASCADE");
        await ExecuteSqlAsync(db2Name, "ALTER TABLE Child ADD CONSTRAINT FK_Child_Parent FOREIGN KEY (ParentId) REFERENCES Parent(Id) ON DELETE NO ACTION");

        var hash1 = await ExtractAndHashAsync(db1Name);
        var hash2 = await ExtractAndHashAsync(db2Name);

        hash1.ShouldNotBe(hash2, "A foreign key's ON DELETE action is part of the schema and must change the hash");
    }

    [TestMethod]
    public async Task ForeignKey_DifferentUpdateAction_ChangesHash()
    {
        var db1Name = await CreateTestDatabaseAsync("FkUpdate1");
        var db2Name = await CreateTestDatabaseAsync("FkUpdate2");

        const string baseSql = @"
            CREATE TABLE Parent (Id INT NOT NULL CONSTRAINT PK_Parent PRIMARY KEY);
            CREATE TABLE Child (Id INT NOT NULL CONSTRAINT PK_Child PRIMARY KEY, ParentId INT NULL);";

        await ExecuteSqlAsync(db1Name, baseSql);
        await ExecuteSqlAsync(db2Name, baseSql);

        await ExecuteSqlAsync(db1Name, "ALTER TABLE Child ADD CONSTRAINT FK_Child_Parent FOREIGN KEY (ParentId) REFERENCES Parent(Id) ON UPDATE CASCADE");
        await ExecuteSqlAsync(db2Name, "ALTER TABLE Child ADD CONSTRAINT FK_Child_Parent FOREIGN KEY (ParentId) REFERENCES Parent(Id) ON UPDATE NO ACTION");

        var hash1 = await ExtractAndHashAsync(db1Name);
        var hash2 = await ExtractAndHashAsync(db2Name);

        hash1.ShouldNotBe(hash2, "A foreign key's ON UPDATE action is part of the schema and must change the hash");
    }

    #endregion

    #region Column Collation

    [TestMethod]
    public async Task Column_DifferentCollation_ChangesHash()
    {
        // A column's collation (case-insensitive vs case-sensitive) changes comparison/sort
        // semantics and lives in sys.columns.collation_name; it must be captured and hashed.
        var db1Name = await CreateTestDatabaseAsync("Collation1");
        var db2Name = await CreateTestDatabaseAsync("Collation2");

        await ExecuteSqlAsync(db1Name, @"
            CREATE TABLE People (
                Id INT NOT NULL CONSTRAINT PK_People PRIMARY KEY,
                Name NVARCHAR(100) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL
            )");
        await ExecuteSqlAsync(db2Name, @"
            CREATE TABLE People (
                Id INT NOT NULL CONSTRAINT PK_People PRIMARY KEY,
                Name NVARCHAR(100) COLLATE SQL_Latin1_General_CP1_CS_AS NOT NULL
            )");

        var hash1 = await ExtractAndHashAsync(db1Name);
        var hash2 = await ExtractAndHashAsync(db2Name);

        hash1.ShouldNotBe(hash2, "Changing a string column's collation is a schema difference and must change the hash");
    }

    [TestMethod]
    public async Task Column_SameCollation_SameHash()
    {
        // Guards against over-sensitivity: identical explicit collations must still compare equal.
        var db1Name = await CreateTestDatabaseAsync("CollationSame1");
        var db2Name = await CreateTestDatabaseAsync("CollationSame2");

        const string tableSql = @"
            CREATE TABLE People (
                Id INT NOT NULL CONSTRAINT PK_People PRIMARY KEY,
                Name NVARCHAR(100) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL
            )";
        await ExecuteSqlAsync(db1Name, tableSql);
        await ExecuteSqlAsync(db2Name, tableSql);

        (await ExtractAndHashAsync(db1Name)).ShouldBe(await ExtractAndHashAsync(db2Name), "Identical collations must produce the same hash");
    }

    [TestMethod]
    public async Task Column_Collation_IsExtracted_AndNullForNonStringColumns()
    {
        var dbName = await CreateTestDatabaseAsync("CollationExtract");

        await ExecuteSqlAsync(dbName, @"
            CREATE TABLE People (
                Id INT NOT NULL CONSTRAINT PK_People PRIMARY KEY,
                Name NVARCHAR(100) COLLATE SQL_Latin1_General_CP1_CS_AS NOT NULL
            )");

        var columns = (await ExtractSchemaAsync(dbName)).Tables.Single(t => t.Name == "People").Columns;

        columns.Single(c => c.Name == "Name").Collation.ShouldBe("SQL_Latin1_General_CP1_CS_AS", "A string column must carry its collation");
        columns.Single(c => c.Name == "Id").Collation.ShouldBeNull("A non-string column has no collation");
    }

    #endregion

    #region Stored Procedure Parameter Direction

    [TestMethod]
    public async Task Parameter_OutputVsInput_ChangesHash_EvenWithoutProcedureText()
    {
        // @x INT OUTPUT vs @x INT is a contract difference in sys.parameters.is_output. With
        // procedure text excluded, only the captured parameter metadata can surface it. Bodies are
        // identical so the difference is attributable solely to the OUTPUT flag.
        var db1Name = await CreateTestDatabaseAsync("ParamOut1");
        var db2Name = await CreateTestDatabaseAsync("ParamOut2");

        await ExecuteSqlAsync(db1Name, "CREATE PROCEDURE SetValue @x INT OUTPUT AS BEGIN SET @x = @x END");
        await ExecuteSqlAsync(db2Name, "CREATE PROCEDURE SetValue @x INT AS BEGIN SET @x = @x END");

        var noText = new SchemaHashOptions { IncludeStoredProcedureText = false };

        (await ExtractAndHashAsync(db1Name, noText)).ShouldNotBe(await ExtractAndHashAsync(db2Name, noText), "An OUTPUT parameter must change the hash even when stored procedure text is excluded");
    }

    [TestMethod]
    public async Task Parameter_OutputAndReadonly_AreExtracted()
    {
        var dbName = await CreateTestDatabaseAsync("ParamDirMeta");

        await ExecuteSqlAsync(dbName, "CREATE TYPE dbo.IntList AS TABLE (Value INT NOT NULL)");
        await ExecuteSqlAsync(dbName, "CREATE PROCEDURE Process @items dbo.IntList READONLY, @count INT OUTPUT AS BEGIN SET @count = 0 END");

        var proc = (await ExtractSchemaAsync(dbName)).StoredProcedures.Single(p => p.Name == "Process");

        var items = proc.Parameters.Single(p => p.Name == "items");
        items.IsReadonly.ShouldBeTrue("A table-valued parameter is READONLY and must be reported as such");
        items.IsOutput.ShouldBeFalse("A READONLY parameter is not an output parameter");

        var count = proc.Parameters.Single(p => p.Name == "count");
        count.IsOutput.ShouldBeTrue("An OUTPUT parameter must be reported as output");
        count.IsReadonly.ShouldBeFalse("A scalar OUTPUT parameter is not readonly");
    }

    #endregion
}
