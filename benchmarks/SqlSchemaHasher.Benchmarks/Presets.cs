using zachtbeer.SqlSchemaHasher;

namespace SqlSchemaHasher.Benchmarks;

/// <summary>
/// Maps a benchmark parameter string to a <see cref="SchemaHashOptions"/> preset. BenchmarkDotNet
/// <c>[Params]</c> values must be compile-time constants, so presets are selected by name.
/// </summary>
internal static class Presets
{
    public const string Strict = "Strict";
    public const string V1 = "V1";
    public const string V2 = "V2";
    public const string Structural = "Structural";

    public static SchemaHashOptions Resolve(string name) => name switch
    {
        Strict => new SchemaHashOptions(),
        V1 => SchemaHashOptions.V1,
        V2 => SchemaHashOptions.V2,
        Structural => SchemaHashOptions.Structural,
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Unknown preset name.")
    };
}
