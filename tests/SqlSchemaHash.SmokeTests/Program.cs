using System.Runtime.InteropServices;
using Microsoft.Data.SqlClient;
using zachtbeer.SqlSchemaHasher;

namespace SmokeTests;

/// <summary>
/// Runs on every TFM the library ships, which the MSTest suite cannot: Microsoft.NET.Test.Sdk and
/// MSTest.TestAdapter publish no assets below net8.0, so a test project cannot target the full set.
///
/// This exists because that gap hid a shipped defect. The library used to advertise net6.0/net7.0;
/// both were compiled but never executed, and both were broken — those TFMs bound
/// Microsoft.Data.SqlClient's netstandard2.0 asset, whose only runtime implementation is
/// win/net462, so the first SqlConnection.OpenAsync threw NullReferenceException on Linux and
/// macOS. The first run of this project caught it, and 2.0.0 dropped both targets.
///
/// So this is deliberately NOT a fidelity suite. Hash *semantics* are TFM-independent and stay
/// covered by the net10.0 integration tests. What differs per TFM is dependency resolution and
/// runtime binding, so this asserts only the coarse end-to-end contract: the library loads, queries
/// a real server, and produces a stable, well-formed, change-sensitive hash. Anything subtler
/// belongs in the integration suite.
///
/// Keep the language level conservative — this project's whole job is to run on the oldest TFM the
/// library supports, and that floor moves down as easily as up.
/// </summary>
internal static class Program
{
    /// <summary>The version the envelope is expected to carry; mirrors the library's private HashFormatVersion.</summary>
    private const int ExpectedHashFormatVersion = 2;

    private static int _failures;

    private static async Task<int> Main()
    {
        Console.WriteLine($"SqlSchemaHasher smoke run on {RuntimeInformation.FrameworkDescription}");

        var connectionString = Environment.GetEnvironmentVariable("SMOKE_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Console.Error.WriteLine("FATAL: SMOKE_CONNECTION_STRING is not set. Point it at a SQL Server the runner may create and drop databases on.");
            return 2;
        }

        // Two databases built from identical DDL: one is mutated to prove change detection, the
        // other stays pristine to prove two independently-created schemas hash identically.
        var suffix = Guid.NewGuid().ToString("N").Substring(0, 12);
        var primaryDb = $"smoke_{suffix}_a";
        var replicaDb = $"smoke_{suffix}_b";

        try
        {
            await CreateDatabaseAsync(connectionString, primaryDb);
            await CreateDatabaseAsync(connectionString, replicaDb);

            var primary = ConnectionTo(connectionString, primaryDb);
            var replica = ConnectionTo(connectionString, replicaDb);

            await BuildReferenceSchemaAsync(primary);
            await BuildReferenceSchemaAsync(replica);

            await CheckExtractionAsync(primary);
            var baseline = await CheckEnvelopeAsync(primary);
            await CheckDeterminismAsync(primary, baseline);
            await CheckCrossDatabaseEqualityAsync(replica, baseline);
            await CheckChangeDetectedAsync(primary, baseline);
        }
        catch (Exception ex)
        {
            // An unhandled exception here is itself the finding — on an untested TFM the likely
            // failure is a TypeLoadException/FileNotFoundException from dependency resolution,
            // long before any assertion runs. Report it as a failure rather than a crash.
            Console.Error.WriteLine($"FAIL  unhandled exception: {ex}");
            _failures++;
        }
        finally
        {
            await TryDropDatabaseAsync(connectionString, primaryDb);
            await TryDropDatabaseAsync(connectionString, replicaDb);
        }

        Console.WriteLine(_failures == 0 ? "RESULT: all smoke checks passed" : $"RESULT: {_failures} smoke check(s) failed");
        return _failures == 0 ? 0 : 1;
    }

    private static async Task CheckExtractionAsync(string connectionString)
    {
        var schema = await SqlSchemaHash.ExtractSchemaAsync(connectionString);

        var table = schema.Tables.SingleOrDefault(t => t.Name == "SmokePerson");
        Check(table is not null, "extraction finds the table");
        if (table is not null)
        {
            Check(table.Columns.Any(c => c.Name == "Email"), "extraction finds the table's columns");
            Check(table.Indexes.Any(i => i.Name == "IX_SmokePerson_Email"), "extraction finds the table's indexes");
            Check(table.KeyConstraints.Any(k => k.Name == "PK_SmokePerson"), "extraction finds the table's key constraints");
        }

        Check(schema.StoredProcedures.Any(p => p.Name == "SmokeGetPerson"), "extraction finds the stored procedure");
        Check(schema.Views.Any(v => v.Name == "SmokePersonView"), "extraction finds the view");
    }

    private static async Task<string> CheckEnvelopeAsync(string connectionString)
    {
        var envelope = await SqlSchemaHash.GetHashAsync(connectionString);

        var parsed = SchemaHashResult.TryParse(envelope, out var result);
        Check(parsed, "the returned envelope parses");
        if (parsed)
        {
            Check(result.Version == ExpectedHashFormatVersion, $"the envelope carries hash-format version {ExpectedHashFormatVersion} (got {result.Version})");
            Check(!string.IsNullOrEmpty(result.Hash), "the envelope carries a non-empty hash");
        }

        return envelope;
    }

    private static async Task CheckDeterminismAsync(string connectionString, string baseline)
    {
        var again = await SqlSchemaHash.GetHashAsync(connectionString);
        Check(SchemaHashResult.Compare(baseline, again) == SchemaHashComparison.Equal, "an unchanged database hashes identically on a second read");
    }

    private static async Task CheckCrossDatabaseEqualityAsync(string replicaConnectionString, string baseline)
    {
        var replicaHash = await SqlSchemaHash.GetHashAsync(replicaConnectionString);
        Check(SchemaHashResult.Compare(baseline, replicaHash) == SchemaHashComparison.Equal, "two databases built from identical DDL hash identically");
    }

    private static async Task CheckChangeDetectedAsync(string connectionString, string baseline)
    {
        await ExecuteAsync(connectionString, "ALTER TABLE dbo.SmokePerson ADD Nickname NVARCHAR(50) NULL");

        var changed = await SqlSchemaHash.GetHashAsync(connectionString);
        Check(SchemaHashResult.Compare(baseline, changed) == SchemaHashComparison.Different, "adding a column changes the hash");
    }

    /// <summary>Reference schema: enough object kinds to exercise the extractor's main query paths, no more.</summary>
    private static async Task BuildReferenceSchemaAsync(string connectionString)
    {
        // CREATE VIEW and CREATE PROCEDURE must each be the first statement in their batch, so
        // every statement is sent separately rather than as one script.
        var statements = new[]
        {
            @"CREATE TABLE dbo.SmokePerson (
                PersonId INT NOT NULL CONSTRAINT PK_SmokePerson PRIMARY KEY,
                Email NVARCHAR(256) NOT NULL,
                CreatedUtc DATETIME2 NOT NULL CONSTRAINT DF_SmokePerson_CreatedUtc DEFAULT SYSUTCDATETIME()
            )",
            "CREATE INDEX IX_SmokePerson_Email ON dbo.SmokePerson(Email)",
            "CREATE VIEW dbo.SmokePersonView AS SELECT PersonId, Email FROM dbo.SmokePerson",
            "CREATE PROCEDURE dbo.SmokeGetPerson @PersonId INT AS SELECT PersonId, Email FROM dbo.SmokePerson WHERE PersonId = @PersonId",
        };

        foreach (var statement in statements)
        {
            await ExecuteAsync(connectionString, statement);
        }
    }

    private static string ConnectionTo(string connectionString, string database)
    {
        var builder = new SqlConnectionStringBuilder(connectionString) { InitialCatalog = database };
        return builder.ConnectionString;
    }

    private static async Task CreateDatabaseAsync(string connectionString, string database)
    {
        // The database name is a locally-generated GUID suffix, never user input, so bracket-quoting
        // it is sufficient — CREATE DATABASE takes no parameters.
        await ExecuteAsync(ConnectionTo(connectionString, "master"), $"CREATE DATABASE [{database}]");
    }

    private static async Task TryDropDatabaseAsync(string connectionString, string database)
    {
        try
        {
            var drop = $"IF DB_ID('{database}') IS NOT NULL BEGIN ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}]; END";
            await ExecuteAsync(ConnectionTo(connectionString, "master"), drop);
        }
        catch (Exception ex)
        {
            // Cleanup is best-effort: a leaked scratch database must not turn a passing run red.
            Console.Error.WriteLine($"WARN  could not drop {database}: {ex.Message}");
        }
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static void Check(bool condition, string description)
    {
        if (condition)
        {
            Console.WriteLine($"PASS  {description}");
            return;
        }

        Console.Error.WriteLine($"FAIL  {description}");
        _failures++;
    }
}
