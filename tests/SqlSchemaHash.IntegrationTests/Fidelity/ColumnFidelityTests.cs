using zachtbeer.SqlSchemaHasher;
using Shouldly;

namespace SqlSchemaHash.IntegrationTests.Fidelity;

/// <summary>
/// Column-level schema fidelity: a genuine difference in a captured column attribute — ordinal
/// position, sparse/rowguid markers, user-type schema-qualification, dynamic data masking, typed
/// XML, collation, computed formulas, ANSI padding — must change the hash, and the newly-captured
/// metadata is pinned where practical.
/// </summary>
[TestClass]
public class ColumnFidelityTests : IntegrationTestBase
{
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

        columns.Single(c => c.Name == "Name").CollationName.ShouldBe("SQL_Latin1_General_CP1_CS_AS", "A string column must carry its collation");
        columns.Single(c => c.Name == "Id").CollationName.ShouldBeNull("A non-string column has no collation");
    }

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

    [TestMethod]
    public async Task Column_AnsiPaddingOff_ChangesHash_AndIsExtracted()
    {
        var db1Name = await CreateTestDatabaseAsync("Ansi1");
        var db2Name = await CreateTestDatabaseAsync("Ansi2");

        // ANSI_PADDING must be set in the same batch as the CREATE for it to bind to the column.
        await ExecuteSqlAsync(db1Name, "SET ANSI_PADDING ON; CREATE TABLE T (Id INT NOT NULL, A VARCHAR(10) NULL)");
        await ExecuteSqlAsync(db2Name, "SET ANSI_PADDING OFF; CREATE TABLE T (Id INT NOT NULL, A VARCHAR(10) NULL)");

        (await ExtractAndHashAsync(db1Name)).ShouldNotBe(await ExtractAndHashAsync(db2Name), "A varchar column created under ANSI_PADDING OFF stores trailing spaces differently and must change the hash");

        var padded = (await ExtractSchemaAsync(db1Name)).Tables.Single(t => t.Name == "T").Columns.Single(c => c.Name == "A");
        padded.IsAnsiPadded.ShouldBeTrue("A column created under ANSI_PADDING ON must report is_ansi_padded = true");

        var unpadded = (await ExtractSchemaAsync(db2Name)).Tables.Single(t => t.Name == "T").Columns.Single(c => c.Name == "A");
        unpadded.IsAnsiPadded.ShouldBeFalse("A column created under ANSI_PADDING OFF must report is_ansi_padded = false");
    }
}
