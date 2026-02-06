using Dapper;
using zachtbeer.SqlSchemaHasher;
using Microsoft.Data.SqlClient;
using SqlSchemaHash.IntegrationTests.TestHelpers;

[assembly: Parallelize(Scope = ExecutionScope.ClassLevel)]

namespace SqlSchemaHash.IntegrationTests;

/// <summary>
/// Base class for integration tests that provides automatic database cleanup
/// and shared test infrastructure.
/// </summary>
[TestClass]
public abstract class IntegrationTestBase
{
    /// <summary>
    /// List of databases created during the test that will be cleaned up automatically.
    /// </summary>
    protected List<string> CreatedDatabases { get; } = new();

    /// <summary>
    /// The shared SQL Server connection string from the test container.
    /// </summary>
    protected static string ConnectionString => SqlServerFixture.ConnectionString;

    /// <summary>
    /// Automatically cleans up all databases created during the test.
    /// </summary>
    [TestCleanup]
    public async Task CleanupDatabases()
    {
        foreach (var dbName in CreatedDatabases)
        {
            await DatabaseTestHelpers.DropTestDatabaseSafeAsync(dbName);
        }
        CreatedDatabases.Clear();
    }

    /// <summary>
    /// Creates a test database with a unique name and tracks it for cleanup.
    /// </summary>
    /// <param name="prefix">Prefix for the database name (default: "TestDb")</param>
    /// <returns>The name of the created database</returns>
    protected async Task<string> CreateTestDatabaseAsync(string prefix = "TestDb")
    {
        var dbName = await DatabaseTestHelpers.CreateTestDatabaseAsync(prefix);
        CreatedDatabases.Add(dbName);
        return dbName;
    }

    /// <summary>
    /// Gets a connection string for a specific database.
    /// </summary>
    protected static string GetConnectionString(string dbName)
    {
        return DatabaseTestHelpers.GetConnectionString(dbName);
    }

    /// <summary>
    /// Extracts schema and computes a hex hash for the given database.
    /// </summary>
    protected static async Task<string> ExtractAndHashAsync(string dbName, SchemaHashOptions? options = null)
    {
        var extractor = new SchemaExtractor();
        var schema = await extractor.ExtractSchemaAsync(GetConnectionString(dbName), options);
        var calculator = new SchemaHashCalculator(options ?? SchemaHashOptions.Default);
        return calculator.ComputeHash(schema);
    }

    /// <summary>
    /// Executes SQL against a test database.
    /// </summary>
    protected static async Task ExecuteSqlAsync(string dbName, string sql)
    {
        await using var connection = new SqlConnection(GetConnectionString(dbName));
        await connection.OpenAsync();
        await connection.ExecuteAsync(sql);
    }

    /// <summary>
    /// Extracts schema metadata for inspection.
    /// </summary>
    protected static async Task<SchemaMetadata> ExtractSchemaAsync(string dbName, SchemaHashOptions? options = null)
    {
        var extractor = new SchemaExtractor();
        return await extractor.ExtractSchemaAsync(GetConnectionString(dbName), options);
    }
}
