using SqlSchemaHash.IntegrationTests.TestHelpers;
using Shouldly;

namespace SqlSchemaHash.IntegrationTests.Determinism;

/// <summary>
/// Hash determinism against real databases: the same schema must always produce the same hash, two
/// genuinely different schemas must not collide, and unordered DDL clauses (INCLUDE column order) must
/// not affect the hash. (DB-free determinism — culture independence, the wire-format golden vector, and
/// HASHBYTES-support version/engine-edition logic — lives in the unit test project's Determinism suite.
/// Object-creation-order determinism against a rich reference DB is anchored in the Settings regression suite.)
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
