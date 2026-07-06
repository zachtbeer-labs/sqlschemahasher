using System.Globalization;
using SqlSchemaHash.IntegrationTests.TestHelpers;
using zachtbeer.SqlSchemaHasher;
using Shouldly;

namespace SqlSchemaHash.IntegrationTests.Determinism;

/// <summary>
/// Hash determinism: the same schema must always produce the same hash regardless of machine culture,
/// host byte order, or the order columns/statements are supplied in — and two genuinely different
/// schemas must not collide. Golden wire-format vectors pin the integer serialization layout. (Object-
/// creation-order determinism against a rich reference DB is anchored in the Settings regression suite.)
/// </summary>
[TestClass]
public class DeterminismTests : IntegrationTestBase
{
    [TestMethod]
    public async Task IdenticalSchemas_ProduceSameHash()
    {
        // Arrange - Create two databases with identical schemas
        var db1Name = await CreateTestDatabaseAsync("HashTest_Identical1");
        var db2Name = await CreateTestDatabaseAsync("HashTest_Identical2");

        await DatabaseTestHelpers.CreateEmployeesSchemaAsync(db1Name);
        await DatabaseTestHelpers.CreateEmployeesSchemaAsync(db2Name);

        // Act
        var hash1 = await ExtractAndHashAsync(db1Name);
        var hash2 = await ExtractAndHashAsync(db2Name);

        // Assert
        hash1.ShouldBe(hash2, "Identical schemas should produce the same hash");
        hash1.Length.ShouldBe(64, "SHA256 hash should be 64 hex characters");
    }

    [TestMethod]
    public async Task DifferentSchemas_ProduceDifferentHashes()
    {
        // Arrange - Create two databases with different schemas
        var db1Name = await CreateTestDatabaseAsync("HashTest_Different1");
        var db2Name = await CreateTestDatabaseAsync("HashTest_Different2");

        await DatabaseTestHelpers.CreateEmployeesSchemaAsync(db1Name);
        await CreateDifferentSchema(db2Name);

        // Act
        var hash1 = await ExtractAndHashAsync(db1Name);
        var hash2 = await ExtractAndHashAsync(db2Name);

        // Assert
        hash1.ShouldNotBe(hash2, "Different schemas should produce different hashes");
    }

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
            new("dbo", "Åland", columns, new List<IndexSchema>(), new List<KeyConstraintSchema>(), new List<ForeignKeyConstraintSchema>(), new List<CheckConstraintSchema>(), new List<DefaultConstraintSchema>(), null),
            new("dbo", "Borders", columns, new List<IndexSchema>(), new List<KeyConstraintSchema>(), new List<ForeignKeyConstraintSchema>(), new List<CheckConstraintSchema>(), new List<DefaultConstraintSchema>(), null)
        };
        var schema = new SchemaMetadata(tables, new List<StoredProcedureSchema>(), new List<UserDefinedTableTypeSchema>());

        var hashEnUs = ComputeHashWithCulture(schema, enUs);
        var hashDaDk = ComputeHashWithCulture(schema, daDk);

        hashEnUs.ShouldBe(hashDaDk, "The hash of identical schema metadata must not depend on the current culture");
    }

    [TestMethod]
    public void SchemaHash_IntegerSerialization_IsLittleEndianAndStable()
    {
        // AppendInt and AppendString's length prefix write little-endian explicitly so the hash
        // does not depend on the host's byte order. This golden vector pins that layout: a change
        // to the constant means the hash wire format changed and previously-stored hashes are invalid.
        var columns = new List<ColumnSchema> { new("Id", "int", 4, 10, 0, false) };
        var tables = new List<TableSchema>
        {
            new("dbo", "T", columns, new List<IndexSchema>(), new List<KeyConstraintSchema>(), new List<ForeignKeyConstraintSchema>(), new List<CheckConstraintSchema>(), new List<DefaultConstraintSchema>(), null)
        };
        var schema = new SchemaMetadata(tables, new List<StoredProcedureSchema>(), new List<UserDefinedTableTypeSchema>());

        new SchemaHashCalculator().ComputeHash(schema)
            .ShouldBe("82d011e1ddb7ab5b7a89b5099181873768948d51f5eb595e87c6bbb76185f84c", "The integer byte layout of the hash must remain stable and independent of host endianness");
    }

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

    private async Task CreateDifferentSchema(string dbName)
    {
        await ExecuteSqlAsync(dbName, @"
            CREATE TABLE Projects (
                ProjectId INT IDENTITY(1,1) PRIMARY KEY,
                Code NVARCHAR(255) NOT NULL,
                Name NVARCHAR(50),
                Description NVARCHAR(50)
            )");

        await ExecuteSqlAsync(dbName, @"
            CREATE INDEX IX_Projects_Code ON Projects(Code)");
    }
}
