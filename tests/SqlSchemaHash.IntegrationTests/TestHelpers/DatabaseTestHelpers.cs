using Dapper;
using Microsoft.Data.SqlClient;

namespace SqlSchemaHash.IntegrationTests.TestHelpers;

/// <summary>
/// Shared helper methods for database integration tests.
/// Provides common database creation, cleanup, and schema creation utilities.
/// </summary>
public static class DatabaseTestHelpers
{
    #region Database Lifecycle

    /// <summary>
    /// Creates a new test database with a unique name.
    /// </summary>
    /// <param name="prefix">Prefix for the database name (default: "TestDb")</param>
    /// <returns>The name of the created database</returns>
    public static async Task<string> CreateTestDatabaseAsync(string prefix = "TestDb")
    {
        var dbName = $"{prefix}_{Guid.NewGuid():N}";
        await using var connection = new SqlConnection(SqlServerFixture.ConnectionString);
        await connection.OpenAsync();
        await connection.ExecuteAsync($"CREATE DATABASE [{dbName}]");
        return dbName;
    }

    /// <summary>
    /// Creates a test database with a specific name.
    /// </summary>
    public static async Task CreateTestDatabaseAsync(string connectionString, string dbName)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await connection.ExecuteAsync($"CREATE DATABASE [{dbName}]");
    }

    /// <summary>
    /// Safely drops a test database, ignoring any errors.
    /// </summary>
    public static async Task DropTestDatabaseSafeAsync(string dbName)
    {
        try
        {
            await using var connection = new SqlConnection(SqlServerFixture.ConnectionString);
            await connection.OpenAsync();
            await connection.ExecuteAsync(
                $"IF EXISTS (SELECT 1 FROM sys.databases WHERE name = '{dbName}') " +
                $"BEGIN " +
                $"  ALTER DATABASE [{dbName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; " +
                $"  DROP DATABASE [{dbName}]; " +
                $"END");
        }
        catch
        {
            // Ignore cleanup errors
        }
    }

    /// <summary>
    /// Gets a connection string for a specific database.
    /// </summary>
    public static string GetConnectionString(string dbName)
    {
        var builder = new SqlConnectionStringBuilder(SqlServerFixture.ConnectionString)
        {
            InitialCatalog = dbName
        };
        return builder.ToString();
    }

    /// <summary>
    /// Adds a MEMORY_OPTIMIZED_DATA filegroup and file to a test database so it can host memory-optimized
    /// tables. Must be called once per database before creating any memory-optimized table. The file path
    /// is inside the Linux container's default data directory; dropping the database (test cleanup) also
    /// removes the filegroup file.
    /// </summary>
    public static async Task EnableMemoryOptimizedAsync(string dbName)
    {
        await using var connection = new SqlConnection(GetConnectionString(dbName));
        await connection.OpenAsync();
        await connection.ExecuteAsync($@"
            ALTER DATABASE [{dbName}] ADD FILEGROUP [{dbName}_mod] CONTAINS MEMORY_OPTIMIZED_DATA;
            ALTER DATABASE [{dbName}] ADD FILE (NAME='{dbName}_mod1', FILENAME='/var/opt/mssql/data/{dbName}_mod') TO FILEGROUP [{dbName}_mod];");
    }

    #endregion

    #region Schema Creation Helpers

    /// <summary>
    /// Creates a simple Employees schema for hash testing.
    /// </summary>
    public static async Task CreateEmployeesSchemaAsync(string dbName)
    {
        await using var connection = new SqlConnection(GetConnectionString(dbName));
        await connection.OpenAsync();

        await connection.ExecuteAsync(@"
            CREATE TABLE Employees (
                EmployeeId INT IDENTITY(1,1),
                Name NVARCHAR(100) NOT NULL,
                Salary DECIMAL(10,2) NOT NULL,
                HireDate DATETIME2 NOT NULL CONSTRAINT DF_Employees_HireDate DEFAULT GETUTCDATE(),
                CONSTRAINT PK_Employees PRIMARY KEY (EmployeeId)
            )");

        await connection.ExecuteAsync(@"
            CREATE INDEX IX_Employees_Name ON Employees(Name)");

        await connection.ExecuteAsync(@"
            CREATE PROCEDURE GetEmployeeById
                @EmployeeId INT
            AS
            BEGIN
                SELECT * FROM Employees WHERE EmployeeId = @EmployeeId
            END");
    }

    /// <summary>
    /// Creates a comprehensive schema with multiple object types for testing extraction.
    /// </summary>
    public static async Task CreateComprehensiveSchemaAsync(string dbName)
    {
        await using var connection = new SqlConnection(GetConnectionString(dbName));
        await connection.OpenAsync();

        // Create tables with various features
        await connection.ExecuteAsync(@"
            CREATE TABLE Departments (
                DepartmentId INT IDENTITY(1,1),
                Name NVARCHAR(50) NOT NULL,
                CONSTRAINT PK_Departments PRIMARY KEY (DepartmentId),
                CONSTRAINT UQ_Departments_Name UNIQUE (Name)
            )");

        await connection.ExecuteAsync(@"
            CREATE TABLE Employees (
                EmployeeId INT IDENTITY(1,1),
                DepartmentId INT NOT NULL,
                Name NVARCHAR(100) NOT NULL,
                Salary DECIMAL(10,2) NOT NULL,
                IsActive BIT NOT NULL CONSTRAINT DF_Employees_IsActive DEFAULT 1,
                CONSTRAINT PK_Employees PRIMARY KEY (EmployeeId),
                CONSTRAINT CK_Employees_Salary CHECK (Salary >= 0),
                CONSTRAINT FK_Employees_Departments FOREIGN KEY (DepartmentId) REFERENCES Departments(DepartmentId)
            )");

        await connection.ExecuteAsync(@"
            CREATE INDEX IX_Employees_DepartmentId ON Employees(DepartmentId)");

        await connection.ExecuteAsync(@"
            CREATE INDEX IX_Employees_Name ON Employees(Name)");

        // Stored procedure with parameters
        await connection.ExecuteAsync(@"
            CREATE PROCEDURE GetEmployeesByDepartment
                @DepartmentId INT,
                @MinSalary DECIMAL(10,2) = NULL
            AS
            BEGIN
                SELECT e.*, d.Name AS DepartmentName
                FROM Employees e
                INNER JOIN Departments d ON e.DepartmentId = d.DepartmentId
                WHERE e.DepartmentId = @DepartmentId
                    AND (@MinSalary IS NULL OR e.Salary >= @MinSalary)
            END");

        // Stored procedure without parameters
        await connection.ExecuteAsync(@"
            CREATE PROCEDURE GetAllDepartments
            AS
            BEGIN
                SELECT * FROM Departments ORDER BY Name
            END");

        // User-defined table type
        await connection.ExecuteAsync(@"
            CREATE TYPE dbo.ProjectAssignmentTableType AS TABLE (
                EmployeeId INT NOT NULL,
                HoursAllocated INT NOT NULL,
                Rate DECIMAL(10,2) NOT NULL
            )");
    }

    #endregion
}
