using BenchmarkDotNet.Attributes;
using SqlSchemaHasher.Benchmarks.Corpus;
using zachtbeer.SqlSchemaHasher;

namespace SqlSchemaHasher.Benchmarks;

/// <summary>
/// The attribution tier: hashes one object kind at a time so a regression points at a subsystem
/// rather than at <c>ComputeHash</c> in general. Fixed at the Medium profile and V2 preset so the
/// object-kind axis varies alone.
/// </summary>
public class ObjectKindBenchmarks
{
    private static readonly SchemaProfile Profile = SchemaProfile.Medium;

    private readonly SchemaHashOptions _options = Presets.Resolve(Presets.V2);

    private SchemaMetadata _tables = MetadataCorpus.Empty;
    private SchemaMetadata _modules = MetadataCorpus.Empty;
    private SchemaMetadata _tableTypes = MetadataCorpus.Empty;
    private SchemaMetadata _extendedProperties = MetadataCorpus.Empty;

    [GlobalSetup]
    public void Setup()
    {
        var full = MetadataCorpus.Build(Profile);
        CorpusValidation.Validate(full, Profile);

        _tables = MetadataCorpus.OnlyTables(full);
        _modules = MetadataCorpus.OnlyModules(full);
        _tableTypes = MetadataCorpus.OnlyTableTypes(full);
        _extendedProperties = MetadataCorpus.OnlyExtendedProperties(full);
    }

    [Benchmark(Baseline = true)]
    public string Tables() => SqlSchemaHash.ComputeHash(_tables, _options);

    [Benchmark]
    public string Modules() => SqlSchemaHash.ComputeHash(_modules, _options);

    [Benchmark]
    public string TableTypes() => SqlSchemaHash.ComputeHash(_tableTypes, _options);

    [Benchmark]
    public string ExtendedProperties() => SqlSchemaHash.ComputeHash(_extendedProperties, _options);
}
