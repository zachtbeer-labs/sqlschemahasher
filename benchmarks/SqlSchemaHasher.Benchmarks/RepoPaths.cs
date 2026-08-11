using System.Reflection;

namespace SqlSchemaHasher.Benchmarks;

/// <summary>
/// Locates repository-relative output paths. The benchmark executable runs from its build output
/// directory, so committed artifacts have to be routed back to the repo root explicitly.
/// </summary>
internal static class RepoPaths
{
    /// <summary>
    /// Walks up from the executable location to the directory containing <c>SqlSchemaHasher.slnx</c>.
    /// </summary>
    public static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SqlSchemaHasher.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Could not locate SqlSchemaHasher.slnx above the benchmark output directory. Run the benchmarks from inside the repository.");
    }

    /// <summary>
    /// Results directory for the library version under test, e.g. <c>benchmarks/results/2.0.0</c>.
    /// Version-scoping keeps each release's numbers as a distinct committed artifact.
    /// </summary>
    /// <remarks>
    /// Reads the informational version, not <c>AssemblyName.Version</c>: MinVer sets AssemblyVersion
    /// to <c>{Major}.0.0.0</c> for every build in a major, which would route 2.1.0's results straight
    /// over the committed 2.0.0 baseline. The informational version carries the full MinVer version,
    /// so an untagged build lands in an obviously-scratch folder (<c>2.0.1-alpha.0.7</c>) instead of
    /// overwriting a release's numbers. Build metadata is stripped — it is not part of the version.
    /// </remarks>
    public static string ResultsDirectory()
    {
        var informationalVersion = typeof(zachtbeer.SqlSchemaHasher.SqlSchemaHash).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var folder = string.IsNullOrEmpty(informationalVersion) ? "unknown" : informationalVersion.Split('+')[0];
        return Path.Combine(RepoRoot(), "benchmarks", "results", folder);
    }
}
