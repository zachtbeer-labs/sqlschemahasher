using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace zachtbeer.SqlSchemaHasher;

/// <summary>
/// Computes deterministic SHA256 hashes from schema metadata for comparison.
/// Uses IncrementalHash for memory-efficient streaming.
/// All object names are schema-qualified to correctly distinguish objects in different schemas.
/// </summary>
public sealed class SchemaHashCalculator
{
    private readonly SchemaHashOptions _options;

    /// <summary>
    /// Creates a new hash calculator with <see cref="SchemaHashOptions.Default"/>.
    /// </summary>
    public SchemaHashCalculator() : this(SchemaHashOptions.Default)
    {
    }

    /// <summary>
    /// Creates a new hash calculator with the specified options.
    /// </summary>
    /// <param name="options">Options controlling normalization behavior. Pass null for <see cref="SchemaHashOptions.Default"/>.</param>
    public SchemaHashCalculator(SchemaHashOptions options)
    {
        _options = options ?? SchemaHashOptions.Default;
    }

    /// <summary>
    /// Computes a deterministic SHA256 hash from the schema metadata.
    /// All elements are sorted by their fully-qualified names (schema.name) before hashing to ensure determinism.
    /// </summary>
    /// <param name="schema">The schema metadata to hash.</param>
    /// <returns>64-character lowercase hex string representing the SHA256 hash.</returns>
    public string ComputeHash(SchemaMetadata schema)
    {
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        // Hash tables in sorted order by fully-qualified name.
        // All sorts use ordinal comparison: culture-sensitive sorting would make the
        // hash depend on the machine's culture and ICU version.
        foreach (var table in schema.Tables.OrderBy(t => t.SchemaName, StringComparer.Ordinal).ThenBy(t => t.Name, StringComparer.Ordinal))
        {
            HashTable(hasher, table);
        }

        // Hash stored procedures in sorted order by fully-qualified name
        foreach (var proc in schema.StoredProcedures.OrderBy(p => p.SchemaName, StringComparer.Ordinal).ThenBy(p => p.Name, StringComparer.Ordinal))
        {
            HashStoredProcedure(hasher, proc);
        }

        // Hash user-defined table types in sorted order by fully-qualified name
        foreach (var udt in schema.UserDefinedTableTypes.OrderBy(u => u.SchemaName, StringComparer.Ordinal).ThenBy(u => u.Name, StringComparer.Ordinal))
        {
            HashUserDefinedTableType(hasher, udt);
        }

        var hash = hasher.GetHashAndReset();
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private void HashTable(IncrementalHash hasher, TableSchema table)
    {
        // Marker to separate tables
        AppendString(hasher, "TABLE:");
        // Include schema name to distinguish dbo.Employees from reporting.Employees
        AppendString(hasher, table.SchemaName);
        AppendString(hasher, table.Name);

        // Identity column
        if (!string.IsNullOrEmpty(table.IdentityColumn))
        {
            AppendString(hasher, "IDENTITY:");
            AppendString(hasher, table.IdentityColumn);
        }

        // Columns in sorted order
        foreach (var column in table.Columns.OrderBy(c => c.Name, StringComparer.Ordinal))
        {
            HashColumn(hasher, column);
        }

        // Project indexes to their effective (post-option) values before sorting and hashing.
        // Sorting by raw values would break equality when two indexes on a table differ only in a
        // normalized dimension (e.g. direction-only or name-only differences across databases).
        var effectiveIndexes = table.Indexes.Select(GetEffectiveIndex)
            .OrderBy(i => i.Keys, StringComparer.Ordinal)
            .ThenBy(i => i.Description, StringComparer.Ordinal)
            .ThenBy(i => i.Name, StringComparer.Ordinal)
            .ThenBy(i => i.IncludedColumns, StringComparer.Ordinal)
            .ThenBy(i => i.FilterDefinition, StringComparer.Ordinal);
        foreach (var index in effectiveIndexes)
        {
            HashIndex(hasher, index);
        }

        // Constraints in sorted order (by keys then type)
        foreach (var constraint in table.Constraints.OrderBy(c => c.Keys, StringComparer.Ordinal).ThenBy(c => c.Type, StringComparer.Ordinal))
        {
            HashConstraint(hasher, constraint);
        }
    }

    private void HashColumn(IncrementalHash hasher, ColumnSchema column)
    {
        AppendString(hasher, "COL:");
        AppendString(hasher, column.Name);
        AppendString(hasher, column.DataType);
        AppendInt(hasher, column.MaxLength);
        AppendInt(hasher, column.Precision);
        AppendInt(hasher, column.Scale);
        AppendBool(hasher, column.IsNullable);

        // Computed columns carry a trailing block so they stay distinct from an ordinary column of
        // the same resulting type, and so formula/PERSISTED changes are reflected in the hash.
        if (column.IsComputed)
        {
            AppendString(hasher, "COMPUTED:");
            AppendString(hasher, column.ComputedDefinition ?? string.Empty);
            AppendBool(hasher, column.IsPersisted);
        }
    }

    /// <summary>
    /// Applies the configured normalization options to an index, producing the values that
    /// actually participate in the hash.
    /// </summary>
    private IndexSchema GetEffectiveIndex(IndexSchema index)
    {
        // IgnoreIndexNames wins over NormalizeAutoGeneratedIndexNames. Auto-generated names hash
        // as the empty-string sentinel (length-prefixed hashing keeps "" unambiguous).
        string name = _options.IgnoreIndexNames
            ? string.Empty
            : _options.NormalizeAutoGeneratedIndexNames && IsAutoGeneratedName(index.Name) ? string.Empty : index.Name;

        string description = _options.NormalizeClusteringType
            ? NormalizeClusteringDescription(index.Description)
            : index.Description;

        string? keys = _options.IgnoreIndexSortOrder ? (index.KeysWithoutDirection ?? index.Keys) : index.Keys;

        return new IndexSchema(name, description, keys, index.IncludedColumns, FilterDefinition: index.FilterDefinition);
    }

    private static void HashIndex(IncrementalHash hasher, IndexSchema index)
    {
        AppendString(hasher, "IDX:");
        AppendString(hasher, index.Name);
        AppendString(hasher, index.Description);
        AppendString(hasher, index.Keys ?? string.Empty);
        AppendString(hasher, index.IncludedColumns ?? string.Empty);

        // Filtered indexes carry a trailing block so the predicate participates in the hash.
        if (index.FilterDefinition is not null)
        {
            AppendString(hasher, "FILTER:");
            AppendString(hasher, index.FilterDefinition);
        }
    }

    private void HashConstraint(IncrementalHash hasher, ConstraintSchema constraint)
    {
        AppendString(hasher, "CONST:");
        AppendString(hasher, constraint.Type);
        AppendString(hasher, constraint.Keys ?? string.Empty);
    }

    private void HashStoredProcedure(IncrementalHash hasher, StoredProcedureSchema proc)
    {
        AppendString(hasher, "PROC:");
        // Include schema name to distinguish dbo.GetEmployee from sales.GetEmployee
        AppendString(hasher, proc.SchemaName);
        AppendString(hasher, proc.Name);

        // Optionally include definition hash to detect body changes
        if (_options.IncludeStoredProcedureText)
        {
            AppendString(hasher, "DEFHASH:");
            AppendString(hasher, proc.DefinitionHash);
        }

        foreach (var param in proc.Parameters)
        {
            HashParameter(hasher, param);
        }
    }

    private void HashParameter(IncrementalHash hasher, ParameterSchema param)
    {
        AppendString(hasher, "PARAM:");
        AppendString(hasher, param.Name);
        AppendString(hasher, param.Type);
        AppendInt(hasher, param.MaxLength);
        AppendInt(hasher, param.Precision);
        AppendInt(hasher, param.Scale);
        AppendBool(hasher, param.IsNullable);
    }

    private void HashUserDefinedTableType(IncrementalHash hasher, UserDefinedTableTypeSchema udt)
    {
        AppendString(hasher, "UDT:");
        // Include schema name to distinguish types in different schemas
        AppendString(hasher, udt.SchemaName);
        AppendString(hasher, udt.Name);

        foreach (var column in udt.Columns.OrderBy(c => c.Name, StringComparer.Ordinal))
        {
            HashColumn(hasher, column);
        }
    }

    private static string NormalizeClusteringDescription(string description)
    {
        return Regex.Replace(description, @"(?:non-?)?clustered", "x-clustered", RegexOptions.IgnoreCase);
    }

    /// <summary>
    /// SQL Server system-generated constraint/index names: type prefix, double-underscore separators,
    /// and an 8- or 16-hex-char suffix derived from the object id (e.g. 'PK__Employee__3214EC07A1B2C3D4',
    /// 'DF__Tab__Col__8E1F2D3C').
    /// </summary>
    private static readonly Regex SystemGeneratedNameRegex = new(@"^(PK|UQ|FK|DF|CK)__.+__[0-9A-Fa-f]{8}([0-9A-Fa-f]{8})?$", RegexOptions.Compiled);

    /// <summary>
    /// Detects auto-generated index/constraint names: SQL Server system-generated names
    /// (e.g. 'PK__Employee__3214EC07A1B2C3D4') and GUID-suffixed names
    /// (e.g. 'nci_wi_Asset_EF8A0893C0DB8B0FC4AD9EABBE187744').
    /// </summary>
    internal static bool IsAutoGeneratedName(string name)
    {
        if (string.IsNullOrEmpty(name))
            return false;

        if (SystemGeneratedNameRegex.IsMatch(name))
            return true;

        if (name.Length < 12)
            return false;

        var parts = name.Split('_', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3)
            return false;

        var lastPart = parts[^1];
        if (string.IsNullOrWhiteSpace(lastPart))
            return false;

        // Auto-generated suffixes are hexadecimal with a length that is a multiple of 8 (typically a GUID)
        return lastPart.Length % 8 == 0 && lastPart.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'));
    }

    private static void AppendString(IncrementalHash hasher, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        // Prefix with length to avoid collisions like "AB" + "C" vs "A" + "BC".
        // Write little-endian explicitly so the hash is independent of host byte order.
        Span<byte> lengthPrefix = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(lengthPrefix, bytes.Length);
        hasher.AppendData(lengthPrefix);
        hasher.AppendData(bytes);
    }

    private static void AppendInt(IncrementalHash hasher, int value)
    {
        // Little-endian explicitly so the hash is independent of host byte order.
        Span<byte> buffer = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, value);
        hasher.AppendData(buffer);
    }

    private static void AppendBool(IncrementalHash hasher, bool value)
    {
        hasher.AppendData(BitConverter.GetBytes(value));
    }
}
