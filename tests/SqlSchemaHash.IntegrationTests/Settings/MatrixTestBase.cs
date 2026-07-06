using Dapper;
using Microsoft.Data.SqlClient;
using Shouldly;
using zachtbeer.SqlSchemaHasher;

namespace SqlSchemaHash.IntegrationTests.Settings;

/// <summary>
/// Base class for the TESTPLAN settings-matrix suite (see TESTPLAN.md).
///
/// Adds two things on top of <see cref="IntegrationTestBase"/>:
/// <list type="bullet">
/// <item><see cref="BuildSchemaAsync"/> — create a database, run a list of DDL statements on a
/// single shared connection (so session-scoped SET options persist across statements, which the
/// module SET-option facets depend on), and extract its metadata once. Extraction is
/// normalization-independent, so one extract serves every option variant.</item>
/// <item><see cref="AssertBitContractAsync"/> — the canonical 4-part bit contract from TESTPLAN §B:
/// T1 sensitivity under Strict, T2 collapse under the loosening bit, T3 specificity (the bit must
/// not over-collapse a sibling facet).</item>
/// </list>
/// </summary>
public abstract class MatrixTestBase : IntegrationTestBase
{
    /// <summary>Neutral, everything-exact options: all domains Strict, nothing scoped out.</summary>
    protected static SchemaHashOptions Strict => new();

    /// <summary>
    /// Creates a fresh database and runs each DDL statement in order on one shared connection (so
    /// session SET state such as <c>SET QUOTED_IDENTIFIER OFF</c> stays in effect for subsequent
    /// <c>CREATE</c> batches), returning the database name. Use this when the test needs to extract
    /// with specific scoping options rather than the neutral extraction.
    /// </summary>
    protected async Task<string> CreateDatabaseWithAsync(string prefix, params string[] statements)
    {
        var dbName = await CreateTestDatabaseAsync(prefix);
        await using var connection = new SqlConnection(GetConnectionString(dbName));
        await connection.OpenAsync();
        foreach (var sql in statements)
            await connection.ExecuteAsync(sql);
        return dbName;
    }

    /// <summary>
    /// Creates a fresh database, runs each DDL statement, and extracts its metadata once with neutral
    /// options. Extraction is normalization-independent, so one extract serves every option variant.
    /// </summary>
    protected async Task<SchemaMetadata> BuildSchemaAsync(string prefix, params string[] statements)
    {
        var dbName = await CreateDatabaseWithAsync(prefix, statements);
        return await ExtractSchemaAsync(dbName);
    }

    /// <summary>Hashes already-extracted metadata under the given options (enveloped string).</summary>
    protected static string Hash(SchemaMetadata schema, SchemaHashOptions options) =>
        zachtbeer.SqlSchemaHasher.SqlSchemaHash.ComputeHash(schema, options);

    /// <summary>
    /// Asserts the full normalization-bit contract for a single loosening option (TESTPLAN §B).
    /// <paramref name="baseline"/> and <paramref name="variant"/> must differ ONLY in the facet the
    /// bit governs. <paramref name="sibling"/> (optional) differs from the baseline in a neighboring
    /// facet that the bit must NOT collapse — the anti-over-collapse guard.
    /// </summary>
    protected async Task AssertBitContractAsync(string bit, SchemaHashOptions loosened, string[] baseline, string[] variant, string[]? sibling = null)
    {
        var b = await BuildSchemaAsync("base", baseline);
        var v = await BuildSchemaAsync("var", variant);

        Hash(b, Strict).ShouldNotBe(Hash(v, Strict), $"T1 ({bit}): the governed facet must change the hash under the Strict baseline");
        Hash(b, loosened).ShouldBe(Hash(v, loosened), $"T2 ({bit}): the bit must collapse the governed-facet difference");

        if (sibling is not null)
        {
            var s = await BuildSchemaAsync("sib", sibling);
            Hash(b, loosened).ShouldNotBe(Hash(s, loosened), $"T3 ({bit}): the bit must not over-collapse a sibling-facet difference");
        }
    }
}
