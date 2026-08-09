using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Exporters;
using BenchmarkDotNet.Exporters.Csv;
using BenchmarkDotNet.Exporters.Json;
using BenchmarkDotNet.Filters;

namespace SqlSchemaHasher.Benchmarks;

/// <summary>
/// Shared BenchmarkDotNet configuration: memory diagnostics, the three committed export formats,
/// and results routed into the repository's version-scoped results directory.
/// </summary>
internal static class BenchmarkConfig
{
    /// <summary>Category for benchmarks that need a live SQL Server. Excluded from the default run.</summary>
    public const string IntegrationCategory = "Integration";

    public static IConfig Create(bool includeIntegration)
    {
        var config = DefaultConfig.Instance
            .AddDiagnoser(MemoryDiagnoser.Default)
            .AddExporter(MarkdownExporter.GitHub)
            .AddExporter(CsvExporter.Default)
            .AddExporter(JsonExporter.Full)
            .WithArtifactsPath(RepoPaths.ResultsDirectory());

        if (!includeIntegration)
        {
            config = config.AddFilter(new SimpleFilter(benchmarkCase => !benchmarkCase.Descriptor.Categories.Contains(IntegrationCategory)));
        }

        return config;
    }
}
