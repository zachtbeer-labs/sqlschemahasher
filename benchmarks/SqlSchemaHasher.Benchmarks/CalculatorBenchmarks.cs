using BenchmarkDotNet.Attributes;
using SqlSchemaHasher.Benchmarks.Corpus;
using zachtbeer.SqlSchemaHasher;

namespace SqlSchemaHasher.Benchmarks;

/// <summary>
/// The regression tier: pure CPU hashing of an in-memory schema, no I/O. Varies schema size against
/// each normalization preset, since preset gating is where option-dependent work happens.
/// </summary>
public class CalculatorBenchmarks
{
    public static IEnumerable<SchemaProfile> Profiles => SchemaProfile.All;

    [ParamsSource(nameof(Profiles))]
    public SchemaProfile Profile { get; set; } = SchemaProfile.Small;

    [Params(Presets.Strict, Presets.V1, Presets.V2, Presets.Structural)]
    public string Preset { get; set; } = Presets.V2;

    private SchemaMetadata _schema = MetadataCorpus.Empty;
    private SchemaHashOptions _options = new();

    [GlobalSetup]
    public void Setup()
    {
        _schema = MetadataCorpus.Build(Profile);
        CorpusValidation.Validate(_schema, Profile);
        _options = Presets.Resolve(Preset);
    }

    [Benchmark]
    public string ComputeHash() => SqlSchemaHash.ComputeHash(_schema, _options);
}
