using Microsoft.Data.SqlClient;
using SqlSchemaHasher.Benchmarks.Corpus;

namespace SqlSchemaHasher.Benchmarks;

/// <summary>
/// Provides the SQL Server instance backing the integration tier. Defaults to LocalDB, which needs
/// no container and no startup wait; each profile is seeded into its own database. BenchmarkDotNet
/// runs <c>[GlobalSetup]</c> once per parameter combination, so seeding is guarded to happen once
/// per profile per process.
///
/// Set <c>SQLSCHEMAHASHER_BENCHMARK_CONNECTIONSTRING</c> to benchmark against a different server —
/// LocalDB measures the library without network latency, which is the right baseline for detecting
/// regressions but understates what a networked deployment sees.
/// </summary>
internal static class BenchmarkSqlServer
{
    private const string ConnectionStringVariable = "SQLSCHEMAHASHER_BENCHMARK_CONNECTIONSTRING";
    private const string LocalDbConnectionString = @"Server=(localdb)\MSSQLLocalDB;Integrated Security=True;TrustServerCertificate=True;";

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly HashSet<string> SeededProfiles = [];

    private static string? _serverConnectionString;

    public static async Task<string> EnsureSeededDatabaseAsync(SchemaProfile profile)
    {
        await Gate.WaitAsync();
        try
        {
            var serverConnectionString = EnsureServer();
            var databaseName = $"Bench_{profile.Name}";

            if (SeededProfiles.Add(profile.Name))
            {
                await CreateDatabaseAsync(serverConnectionString, databaseName);
                await SeedAsync(DatabaseConnectionString(serverConnectionString, databaseName), profile);
            }

            return DatabaseConnectionString(serverConnectionString, databaseName);
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>
    /// Resolves the server to benchmark against: the configured connection string if one is set,
    /// otherwise LocalDB. Synchronous — resolving and caching an environment variable needs no
    /// asynchrony, unlike the database creation/seeding work its caller also does.
    /// </summary>
    private static string EnsureServer()
    {
        if (_serverConnectionString is not null)
        {
            return _serverConnectionString;
        }

        var configured = Environment.GetEnvironmentVariable(ConnectionStringVariable);
        _serverConnectionString = string.IsNullOrWhiteSpace(configured) ? LocalDbConnectionString : configured;
        return _serverConnectionString;
    }

    private static string DatabaseConnectionString(string serverConnectionString, string databaseName)
    {
        var builder = new SqlConnectionStringBuilder(serverConnectionString) { InitialCatalog = databaseName };
        return builder.ConnectionString;
    }

    private static async Task CreateDatabaseAsync(string serverConnectionString, string databaseName)
    {
        await using var connection = new SqlConnection(serverConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"IF DB_ID('{databaseName}') IS NOT NULL BEGIN ALTER DATABASE [{databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{databaseName}]; END; CREATE DATABASE [{databaseName}];";
        await command.ExecuteNonQueryAsync();
    }

    private static async Task SeedAsync(string databaseConnectionString, SchemaProfile profile)
    {
        var batches = DdlCorpus.Script(profile);

        await using var connection = new SqlConnection(databaseConnectionString);
        await connection.OpenAsync();

        foreach (var batch in batches)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = batch;
            command.CommandTimeout = 300;
            await command.ExecuteNonQueryAsync();
        }
    }
}
