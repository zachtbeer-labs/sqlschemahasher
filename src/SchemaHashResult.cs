namespace zachtbeer.SqlSchemaHasher;

/// <summary>
/// The outcome of comparing two schema-hash envelopes.
/// </summary>
public enum SchemaHashComparison
{
    /// <summary>Same hash-format version and same hash — the schemas are equal.</summary>
    Equal,

    /// <summary>
    /// Same version but the hashes differ. Usually a genuine schema difference — but note the hash
    /// also differs when the two hashes were computed with different comparison options, which this
    /// library does not detect. Compare only hashes produced with the same options.
    /// </summary>
    Different,

    /// <summary>
    /// The two hashes were produced by different library versions (or one could not be parsed). They
    /// cannot be meaningfully compared; recompute both with a single library version first.
    /// </summary>
    Incomparable
}

/// <summary>
/// A parsed schema-hash envelope: the versioned wrapper around a base64 SHA256 hash. The canonical
/// string form is <c>&lt;version&gt;:&lt;hash&gt;</c> (e.g. <c>2:dGhpcyBpc...</c>). The base64 alphabet
/// contains no <c>:</c>, so the form parses unambiguously.
/// </summary>
/// <param name="Version">Hash-format version — schemas hashed by different versions are not directly comparable.</param>
/// <param name="Hash">The bare base64-encoded SHA256 hash (the pre-envelope value).</param>
public sealed record SchemaHashResult(int Version, string Hash)
{
    /// <summary>Renders the canonical envelope string <c>&lt;version&gt;:&lt;hash&gt;</c>.</summary>
    public override string ToString() => $"{Version}:{Hash}";

    /// <summary>
    /// Parses a canonical envelope string. Throws <see cref="FormatException"/> for anything that is not a
    /// well-formed envelope (including a legacy bare base64 hash with no version prefix).
    /// </summary>
    public static SchemaHashResult Parse(string value)
    {
        return TryParse(value, out var result)
            ? result
            : throw new FormatException($"'{value}' is not a valid schema-hash envelope (expected '<version>:<hash>').");
    }

    /// <summary>
    /// Attempts to parse a canonical envelope string. Returns <c>false</c> (without throwing) for a legacy
    /// bare base64 hash or any other malformed input, so callers can treat unversioned stored hashes as
    /// <see cref="SchemaHashComparison.Incomparable"/>.
    /// </summary>
    public static bool TryParse(string? value, out SchemaHashResult result)
    {
        result = default!;
        if (string.IsNullOrEmpty(value))
            return false;

        var colon = value.IndexOf(':');
        if (colon <= 0 || colon == value.Length - 1)
            return false;

        if (!int.TryParse(value.AsSpan(0, colon), out var version))
            return false;

        result = new SchemaHashResult(version, value[(colon + 1)..]);
        return true;
    }

    /// <summary>
    /// Compares two parsed envelopes: a differing version yields <see cref="SchemaHashComparison.Incomparable"/>;
    /// otherwise equal hashes yield <see cref="SchemaHashComparison.Equal"/> and differing hashes
    /// <see cref="SchemaHashComparison.Different"/>.
    /// </summary>
    public static SchemaHashComparison Compare(SchemaHashResult a, SchemaHashResult b)
    {
        if (a.Version != b.Version)
            return SchemaHashComparison.Incomparable;

        return string.Equals(a.Hash, b.Hash, StringComparison.Ordinal)
            ? SchemaHashComparison.Equal
            : SchemaHashComparison.Different;
    }

    /// <summary>
    /// Compares two envelope strings. Either string failing to parse (e.g. a legacy bare base64 hash)
    /// yields <see cref="SchemaHashComparison.Incomparable"/>.
    /// </summary>
    public static SchemaHashComparison Compare(string a, string b)
    {
        return TryParse(a, out var pa) && TryParse(b, out var pb)
            ? Compare(pa, pb)
            : SchemaHashComparison.Incomparable;
    }
}
