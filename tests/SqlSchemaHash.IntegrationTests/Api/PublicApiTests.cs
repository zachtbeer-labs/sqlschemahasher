using SqlSchemaHash.IntegrationTests.TestHelpers;
using zachtbeer.SqlSchemaHasher;
using Shouldly;

namespace SqlSchemaHash.IntegrationTests.Api;

/// <summary>
/// Public facade behavior (<see cref="SqlSchemaHash"/> + <see cref="SchemaHashResult"/>): the entry
/// points return a versioned envelope, apply the supplied options, and compare as expected —
/// same-options hashes are fully Equal, while the same schema under different presets shares a version
/// but compares as Different (the envelope carries no options fingerprint).
/// </summary>
[TestClass]
public class PublicApiTests : IntegrationTestBase
{
    [TestMethod]
    public async Task PublicApi_GetHashAsync_ReturnsVersionedEnvelope()
    {
        // Arrange
        var dbName = await CreateTestDatabaseAsync("PublicApi_Base64");
        await DatabaseTestHelpers.CreateEmployeesSchemaAsync(dbName);

        var connectionString = GetConnectionString(dbName);

        // Act
        var hash1 = await zachtbeer.SqlSchemaHasher.SqlSchemaHash.GetHashAsync(connectionString);
        var hash2 = await zachtbeer.SqlSchemaHasher.SqlSchemaHash.GetHashAsync(connectionString);

        // Assert - "<version>:<base64hash>" envelope
        System.Text.RegularExpressions.Regex.IsMatch(hash1, @"^\d+:").ShouldBeTrue($"Expected a versioned envelope, got '{hash1}'");
        SchemaHashResult.TryParse(hash1, out var parsed).ShouldBeTrue("The envelope should parse");
        parsed.Version.ShouldBe(2, "Default hash-format version is the assembly major (2.x)");
        parsed.Hash.Length.ShouldBe(44, "Base64-encoded SHA256 (32 bytes) is 44 characters with padding");
        hash1.ShouldBe(hash2, "Public API should return consistent results");
    }

    [TestMethod]
    public async Task PublicApi_GetHashAsync_WithOptions_AppliesOptions()
    {
        // Arrange
        var dbName = await CreateTestDatabaseAsync("PublicApi_Options");
        await DatabaseTestHelpers.CreateEmployeesSchemaAsync(dbName);

        var connectionString = GetConnectionString(dbName);

        // Act - default includes stored procedure text
        var hashDefault = await zachtbeer.SqlSchemaHasher.SqlSchemaHash.GetHashAsync(connectionString);
        var hashNoProc = await zachtbeer.SqlSchemaHasher.SqlSchemaHash.GetHashAsync(connectionString, new SchemaHashOptions { Modules = ModuleNormalization.IgnoreBodyText });

        // Assert - different options should produce different hashes
        hashDefault.ShouldNotBe(hashNoProc, "IncludeStoredProcedureText=false should produce a different hash");
    }

    [TestMethod]
    public async Task Envelope_SameSchemaDifferentPresets_ShareVersion_ButCompareAsDifferent()
    {
        // The envelope does not encode the comparison options: two hashes of the same database under
        // different presets share the version but produce different hashes, so Compare reports Different
        // (NOT Incomparable). This documents the deliberate design — callers must use matching options;
        // an options mismatch is indistinguishable from a genuine schema difference.
        var dbName = await CreateTestDatabaseAsync("EnvelopeDifferentPresets");

        await ExecuteSqlAsync(dbName, @"
            CREATE TABLE Assets (
                Id INT NOT NULL CONSTRAINT PK_Assets PRIMARY KEY,
                Created DATETIME2 NOT NULL
            )");
        await ExecuteSqlAsync(dbName, "CREATE INDEX IX_Assets_Created ON Assets(Created DESC)");

        var schema = await ExtractSchemaAsync(dbName);

        var v2 = SchemaHashResult.Parse(zachtbeer.SqlSchemaHasher.SqlSchemaHash.ComputeHash(schema, SchemaHashOptions.V2));
        var structural = SchemaHashResult.Parse(zachtbeer.SqlSchemaHasher.SqlSchemaHash.ComputeHash(schema, SchemaHashOptions.Structural));

        v2.Version.ShouldBe(structural.Version, "The hash-format version does not depend on the comparison options");
        v2.Hash.ShouldNotBe(structural.Hash, "Structural normalizes away the DESC key and constraint name, changing the hash");

        SchemaHashResult.Compare(v2, structural).ShouldBe(SchemaHashComparison.Different, "Different options are not detected — a hash mismatch under the same version reads as Different");
    }

    [TestMethod]
    public async Task PublicApi_GetHashAsync_CancelledToken_ThrowsOperationCanceledException()
    {
        var dbName = await CreateTestDatabaseAsync("PublicApi_Cancellation");
        await DatabaseTestHelpers.CreateEmployeesSchemaAsync(dbName);

        var connectionString = GetConnectionString(dbName);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(() => zachtbeer.SqlSchemaHasher.SqlSchemaHash.GetHashAsync(connectionString, cts.Token));
    }

    [TestMethod]
    public async Task Envelope_SameSchemaSameOptions_IsFullyEqual()
    {
        var dbName = await CreateTestDatabaseAsync("EnvelopeEqual");

        await ExecuteSqlAsync(dbName, @"
            CREATE TABLE Assets (
                Id INT NOT NULL CONSTRAINT PK_Assets PRIMARY KEY,
                Created DATETIME2 NOT NULL
            )");

        var schema = await ExtractSchemaAsync(dbName);

        var a = zachtbeer.SqlSchemaHasher.SqlSchemaHash.ComputeHash(schema, SchemaHashOptions.V2);
        var b = zachtbeer.SqlSchemaHasher.SqlSchemaHash.ComputeHash(schema, SchemaHashOptions.V2);

        a.ShouldBe(b, "The whole envelope is deterministic for the same schema and options");
        SchemaHashResult.Compare(a, b).ShouldBe(SchemaHashComparison.Equal);
    }
}
