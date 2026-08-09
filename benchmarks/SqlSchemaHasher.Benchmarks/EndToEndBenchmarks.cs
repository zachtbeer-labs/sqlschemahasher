using BenchmarkDotNet.Attributes;
using SqlSchemaHasher.Benchmarks.Corpus;
using zachtbeer.SqlSchemaHasher;

namespace SqlSchemaHasher.Benchmarks;

/// <summary>
/// The characterization tier: what a consumer actually experiences. Extraction and full hashing are
/// measured separately so the published table shows the split — the point being that the cost is
/// round-trips, not SHA256. Requires a SQL Server instance (LocalDB by default) and is excluded from
/// the default run.
/// </summary>
[BenchmarkCategory(BenchmarkConfig.IntegrationCategory)]
public class EndToEndBenchmarks
{
    public static IEnumerable<SchemaProfile> Profiles => SchemaProfile.All;

    [ParamsSource(nameof(Profiles))]
    public SchemaProfile Profile { get; set; } = SchemaProfile.Small;

    private string _connectionString = string.Empty;
    private readonly SchemaHashOptions _options = Presets.Resolve(Presets.V2);

    [GlobalSetup]
    public async Task Setup()
    {
        _connectionString = await BenchmarkSqlServer.EnsureSeededDatabaseAsync(Profile);
    }

    // No [GlobalCleanup]: the seeded Bench_* databases are left on the LocalDB instance between runs
    // so a failed run can be inspected. Each run drops and recreates them, so nothing goes stale.

    [Benchmark(Baseline = true)]
    public async Task<SchemaMetadata> ExtractSchema() => await SqlSchemaHash.ExtractSchemaAsync(_connectionString, _options);

    [Benchmark]
    public async Task<string> GetHash() => await SqlSchemaHash.GetHashAsync(_connectionString, _options);
}
