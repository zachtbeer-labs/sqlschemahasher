using SqlSchemaHash.IntegrationTests.TestHelpers;
using zachtbeer.SqlSchemaHasher;
using Shouldly;

namespace SqlSchemaHash.IntegrationTests.Extraction;

/// <summary>
/// Extractor plumbing: the <see cref="SchemaExtractor"/> must surface every supported object type,
/// populate procedure definition hashes, filter system objects (sysdiagrams), and handle the empty
/// and procedures-only edge cases without throwing. (Object-scoping options are covered in Settings.)
/// </summary>
[TestClass]
public class SchemaExtractorTests : IntegrationTestBase
{
    [TestMethod]
    public async Task SchemaExtractor_ExtractsAllObjectTypes()
    {
        // Arrange
        var dbName = await CreateTestDatabaseAsync("HashTest_AllObjects");
        await DatabaseTestHelpers.CreateComprehensiveSchemaAsync(dbName);

        // Act
        var schema = await ExtractSchemaAsync(dbName);

        // Assert - tables
        schema.Tables.Count.ShouldBeGreaterThan(0, "Should extract tables");
        schema.Tables.Any(t => t.Columns.Count > 0).ShouldBeTrue("Tables should have columns");
        schema.Tables.Any(t => t.Indexes.Count > 0).ShouldBeTrue("Tables should have indexes");

        // Assert - stored procedures with definition hash
        schema.StoredProcedures.Count.ShouldBeGreaterThan(0, "Should extract stored procedures");
        var proc = schema.StoredProcedures.First();
        proc.DefinitionHash.ShouldNotBeNullOrEmpty("DefinitionHash should be populated");
        proc.DefinitionHash.Length.ShouldBe(64, "SHA256 hash should be 64 hex chars");

        // Assert - user-defined table types
        schema.UserDefinedTableTypes.Count.ShouldBeGreaterThan(0, "Should extract user-defined table types");
        var udt = schema.UserDefinedTableTypes.First();
        udt.Columns.Count.ShouldBeGreaterThan(0, "UDT should have columns");
    }

    [TestMethod]
    public async Task SchemaExtractor_HandlesEmptyDatabase()
    {
        // Arrange - Create two empty databases with no user objects
        var db1Name = await CreateTestDatabaseAsync("HashTest_Empty1");
        var db2Name = await CreateTestDatabaseAsync("HashTest_Empty2");

        // Act
        var schema = await ExtractSchemaAsync(db1Name);
        var hash1 = await ExtractAndHashAsync(db1Name);
        var hash2 = await ExtractAndHashAsync(db2Name);

        // Assert - Should not throw and should produce valid output
        schema.ShouldNotBeNull();
        schema.Tables.Count.ShouldBe(0, "Empty database should have no tables");
        schema.StoredProcedures.Count.ShouldBe(0, "Empty database should have no stored procedures");
        hash1.ShouldNotBeNullOrEmpty("Should produce a valid hash even for empty database");
        hash1.Length.ShouldBe(64, "SHA256 hash should be 64 hex characters");

        // Two empty databases should have the same hash (absorbed from ProducesConsistentHashForEmptyDatabases)
        hash1.ShouldBe(hash2, "Two empty databases should have identical hashes");
    }

    [TestMethod]
    public async Task SchemaExtractor_FiltersSystemObjects()
    {
        // Arrange - Create database with sysdiagrams (should be filtered)
        var dbName = await CreateTestDatabaseAsync("HashTest_SysObjects");

        await ExecuteSqlAsync(dbName, @"
            CREATE TABLE sysdiagrams (
                name sysname NOT NULL,
                principal_id int NOT NULL,
                diagram_id int IDENTITY(1,1) PRIMARY KEY,
                version int,
                definition varbinary(max)
            )");

        // Act
        var schema = await ExtractSchemaAsync(dbName);

        // Assert - sysdiagrams should be filtered out
        schema.Tables.Any(t => t.Name.Equals("sysdiagrams", StringComparison.OrdinalIgnoreCase))
            .ShouldBeFalse("sysdiagrams table should be filtered out of schema extraction");
    }

    [TestMethod]
    public async Task SchemaExtractor_ExtractsConstraints()
    {
        // Arrange
        var dbName = await CreateTestDatabaseAsync("HashTest_Constraints");

        await ExecuteSqlAsync(dbName, @"
            CREATE TABLE Departments (
                DepartmentId INT IDENTITY(1,1) PRIMARY KEY,
                Name NVARCHAR(50) NOT NULL UNIQUE
            )");

        await ExecuteSqlAsync(dbName, @"
            CREATE TABLE Employees (
                EmployeeId INT IDENTITY(1,1) PRIMARY KEY,
                DepartmentId INT NOT NULL,
                Name NVARCHAR(100) NOT NULL,
                Salary DECIMAL(10,2) NOT NULL,
                CONSTRAINT CK_Employees_Salary CHECK (Salary >= 0),
                CONSTRAINT FK_Employees_Departments FOREIGN KEY (DepartmentId) REFERENCES Departments(DepartmentId)
            )");

        // Act
        var schema = await ExtractSchemaAsync(dbName);

        // Assert
        var employeesTable = schema.Tables.FirstOrDefault(t => t.Name == "Employees");
        employeesTable.ShouldNotBeNull();

        var totalConstraints = employeesTable.KeyConstraints.Count + employeesTable.ForeignKeys.Count + employeesTable.CheckConstraints.Count + employeesTable.DefaultConstraints.Count;
        totalConstraints.ShouldBeGreaterThan(0, "Should extract constraints");
        employeesTable.ForeignKeys.Count.ShouldBeGreaterThan(0, "Should have foreign key constraint");
    }

    [TestMethod]
    public async Task SchemaExtractor_HandlesDatabaseWithOnlyStoredProcedures()
    {
        // Arrange
        var dbName = await CreateTestDatabaseAsync("HashTest_OnlyProcs");

        await ExecuteSqlAsync(dbName, @"
            CREATE PROCEDURE GetServerVersion
            AS
            BEGIN
                SELECT @@VERSION
            END");

        await ExecuteSqlAsync(dbName, @"
            CREATE PROCEDURE GetServerName
            AS
            BEGIN
                SELECT @@SERVERNAME
            END");

        // Act
        var schema = await ExtractSchemaAsync(dbName);
        var hash = await ExtractAndHashAsync(dbName);

        // Assert
        schema.Tables.Count.ShouldBe(0, "Should have no tables");
        schema.StoredProcedures.Count.ShouldBe(2, "Should have 2 stored procedures");
        hash.ShouldNotBeNullOrEmpty("Should produce valid hash for database with only stored procedures");
    }
}
