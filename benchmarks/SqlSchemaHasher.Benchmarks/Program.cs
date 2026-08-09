using BenchmarkDotNet.Running;

namespace SqlSchemaHasher.Benchmarks;

/// <summary>
/// Entry point. Integration-tier benchmarks need a SQL Server (LocalDB by default) and are excluded
/// unless the caller asks for them explicitly, e.g. <c>-- --anyCategories Integration</c>.
/// </summary>
public static class Program
{
    public static void Main(string[] args)
    {
        var includeIntegration = args.Any(arg => arg.Contains(BenchmarkConfig.IntegrationCategory, StringComparison.OrdinalIgnoreCase));
        var config = BenchmarkConfig.Create(includeIntegration);

        // With no arguments BenchmarkSwitcher prompts for a selection on stdin, which strands a
        // plain `dotnet run` — the command CONTRIBUTING.md documents for the calculator tier. Select
        // everything instead; the config's category filter still excludes the integration tier.
        var effectiveArgs = args.Length == 0 ? ["--filter", "*"] : args;

        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(effectiveArgs, config);
    }
}
