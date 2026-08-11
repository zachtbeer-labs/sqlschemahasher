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
    public static string ResultsDirectory()
    {
        var version = typeof(zachtbeer.SqlSchemaHasher.SqlSchemaHash).Assembly.GetName().Version;
        var folder = version is null ? "unknown" : $"{version.Major}.{version.Minor}.{version.Build}";
        return Path.Combine(RepoRoot(), "benchmarks", "results", folder);
    }
}
