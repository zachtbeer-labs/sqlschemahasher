using BenchmarkDotNet.Running;

namespace SqlSchemaHasher.Benchmarks;

/// <summary>
/// Entry point. Integration-tier benchmarks require Docker and are excluded unless the caller asks
/// for them explicitly, e.g. <c>-- --anyCategories Integration</c>.
/// </summary>
public static class Program
{
    public static void Main(string[] args)
    {
        var includeIntegration = args.Any(arg => arg.Contains(BenchmarkConfig.IntegrationCategory, StringComparison.OrdinalIgnoreCase));
        var config = BenchmarkConfig.Create(includeIntegration);
        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args, config);
    }
}
