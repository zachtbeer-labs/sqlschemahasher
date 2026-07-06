using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace zachtbeer.SqlSchemaHasher;

/// <summary>
/// Computes deterministic SHA256 hashes from schema metadata for comparison.
/// Uses IncrementalHash for memory-efficient streaming.
/// All object names are schema-qualified to correctly distinguish objects in different schemas.
///
/// Every field is hashed unconditionally in a fixed order. Strings are length-prefixed (so "AB"+"C"
/// cannot collide with "A"+"BC") and collections are count-prefixed (so the boundary between two
/// adjacent lists is unambiguous). Normalization options are applied by projecting each element to
/// its <em>effective</em> value before it is sorted and hashed, so option-equivalent databases sort
/// their elements the same way and hash identically.
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

        HashIdentity(hasher, table.IdentityColumn, table.IdentitySeed, table.IdentityIncrement, table.IdentityNotForReplication);

        // Temporal / In-Memory OLTP kind: a system-versioned temporal table or a memory-optimized table
        // is a fundamentally different object from an otherwise-identical plain disk table.
        AppendString(hasher, "TABLEKIND:");
        AppendString(hasher, table.TemporalType ?? string.Empty);
        AppendBool(hasher, table.IsMemoryOptimized);
        AppendString(hasher, table.DurabilityDesc ?? string.Empty);
        // Temporal history linkage and retention. The history table name is normalized so an
        // auto-generated MSSQL_TemporalHistoryFor_<object_id> name (non-deterministic across databases)
        // collapses to the empty sentinel, while an explicitly-named history table contributes its name.
        // Under IgnoreTemporalRetention the finite retention policy is neutralized.
        var ignoreRetention = _options.Tables.HasFlag(TableNormalization.IgnoreTemporalRetention);
        AppendString(hasher, NormalizeHistoryTableName(table.HistoryTableName));
        AppendInt(hasher, ignoreRetention ? 0 : (table.HistoryRetentionPeriod ?? 0));
        AppendString(hasher, ignoreRetention ? string.Empty : (table.HistoryRetentionPeriodUnit ?? string.Empty));

        // Columns in stored (column_id) order, so reordering columns changes the hash — column position
        // is behaviorally significant for positional access (SELECT *, INSERT without a column list).
        // Under IgnoreColumnOrder they are ordered by name instead, making column order irrelevant.
        // (Table types keep column-order significance unconditionally; see HashUserDefinedTableType.)
        var orderedColumns = _options.Tables.HasFlag(TableNormalization.IgnoreColumnOrder)
            ? table.Columns.OrderBy(c => c.Name, StringComparer.Ordinal)
            : table.Columns.OrderBy(c => c.ColumnId).ThenBy(c => c.Name, StringComparer.Ordinal);
        foreach (var column in orderedColumns)
        {
            HashColumn(hasher, column);
        }

        HashIndexes(hasher, table.Indexes);
        HashKeyConstraints(hasher, table.KeyConstraints);
        HashForeignKeys(hasher, table.ForeignKeys);
        HashCheckConstraints(hasher, table.CheckConstraints);
        HashDefaultConstraints(hasher, table.DefaultConstraints);
    }

    private void HashIdentity(IncrementalHash hasher, string? identityColumn, string? seed, string? increment, bool notForReplication)
    {
        // Identity column, including its seed/increment and NOT FOR REPLICATION flag so that
        // IDENTITY(1,1) and IDENTITY(1000,5) on the same column hash differently. The block is present
        // only when an identity column exists, so non-identity containers are unaffected.
        // IgnoreIdentitySeed neutralizes the seed/increment; IgnoreIdentityNotForReplication the NFR flag.
        // These Table bits govern identity wherever it appears (tables and table types).
        if (string.IsNullOrEmpty(identityColumn))
            return;

        var ignoreSeed = _options.Tables.HasFlag(TableNormalization.IgnoreIdentitySeed);
        AppendString(hasher, "IDENTITY:");
        AppendString(hasher, identityColumn);
        AppendString(hasher, ignoreSeed ? string.Empty : (seed ?? string.Empty));
        AppendString(hasher, ignoreSeed ? string.Empty : (increment ?? string.Empty));
        AppendBool(hasher, _options.Tables.HasFlag(TableNormalization.IgnoreIdentityNotForReplication) ? false : notForReplication);
    }

    private void HashColumn(IncrementalHash hasher, ColumnSchema column)
    {
        var cols = _options.Columns;
        var ignoreMasking = cols.HasFlag(ColumnNormalization.IgnoreDynamicDataMasking);
        AppendString(hasher, "COL:");
        AppendString(hasher, column.Name);
        AppendString(hasher, column.DataType);
        AppendInt(hasher, column.MaxLength);
        AppendInt(hasher, column.Precision);
        AppendInt(hasher, column.Scale);
        AppendBool(hasher, column.IsNullable);
        AppendBool(hasher, column.IsComputed);
        AppendString(hasher, column.ComputedDefinition ?? string.Empty);
        AppendBool(hasher, column.IsPersisted);
        AppendString(hasher, cols.HasFlag(ColumnNormalization.IgnoreCollation) ? string.Empty : (column.CollationName ?? string.Empty));
        AppendBool(hasher, column.IsSparse);
        AppendBool(hasher, column.IsRowGuidCol);
        AppendBool(hasher, column.IsFilestream);
        AppendBool(hasher, ignoreMasking ? false : column.IsMasked);
        AppendString(hasher, ignoreMasking ? string.Empty : (column.MaskingFunction ?? string.Empty));
        AppendString(hasher, column.EncryptionTypeDesc ?? string.Empty);
        AppendString(hasher, column.XmlSchemaCollectionName ?? string.Empty);
        AppendBool(hasher, column.IsXmlDocument);
        AppendString(hasher, column.GeneratedAlwaysType ?? string.Empty);
        AppendBool(hasher, column.IsHidden);
        AppendBool(hasher, cols.HasFlag(ColumnNormalization.IgnoreAnsiPadding) ? true : column.IsAnsiPadded);
    }

    private void HashIndexes(IncrementalHash hasher, List<IndexSchema> indexes)
    {
        // Project each index to its effective (post-option) values, then sort by those values so that
        // option-equivalent databases order their indexes identically before hashing.
        foreach (var index in indexes.Select(GetEffectiveIndex).OrderBy(IndexSortKey, StringComparer.Ordinal))
        {
            AppendString(hasher, "IDX:");
            AppendString(hasher, index.Name);
            AppendString(hasher, index.TypeDesc);
            AppendBool(hasher, index.IsUnique);
            AppendBool(hasher, index.IsUniqueConstraint);
            AppendBool(hasher, index.IsPrimaryKey);
            AppendBool(hasher, index.IsDisabled);
            AppendBool(hasher, index.IgnoreDupKey);
            AppendKeyColumns(hasher, index.KeyColumns);
            AppendInt(hasher, index.IncludedColumns.Count);
            foreach (var included in index.IncludedColumns)
                AppendString(hasher, included);
            AppendString(hasher, index.FilterDefinition ?? string.Empty);
            // Physical storage / locking options. These are behavioral (ALLOW_PAGE_LOCKS=OFF changes
            // locking) and physical (FILLFACTOR/PAD_INDEX affect page density), captured under the exact baseline.
            AppendInt(hasher, index.FillFactor);
            AppendBool(hasher, index.IsPadded);
            AppendBool(hasher, index.AllowRowLocks);
            AppendBool(hasher, index.AllowPageLocks);
        }
    }

    private void HashKeyConstraints(IncrementalHash hasher, List<KeyConstraintSchema> constraints)
    {
        foreach (var kc in constraints
            .Select(c => (c.Type, Name: EffectiveConstraintName(c.Name, c.IsSystemNamed), Keys: EffectiveKeyColumns(c.KeyColumns)))
            .OrderBy(x => x.Type, StringComparer.Ordinal)
            .ThenBy(x => x.Name, StringComparer.Ordinal)
            .ThenBy(x => KeyColumnsSortKey(x.Keys), StringComparer.Ordinal))
        {
            AppendString(hasher, "KEYCONST:");
            AppendString(hasher, kc.Type);
            AppendString(hasher, kc.Name);
            AppendKeyColumns(hasher, kc.Keys);
        }
    }

    private void HashForeignKeys(IncrementalHash hasher, List<ForeignKeyConstraintSchema> foreignKeys)
    {
        // The sort key covers every hashed field so ordering is a total order over the record's content:
        // when Constraints.IgnoreNames collapses Name to the empty sentinel, the enforcement flags still
        // break ties (two FKs to the same table over the same columns can differ only in NOCHECK state),
        // and the extraction queries carry no ORDER BY to fall back on.
        foreach (var fk in foreignKeys
            .Select(f => (
                f.ReferencedSchema,
                f.ReferencedTable,
                f.ColumnPairs,
                f.DeleteAction,
                f.UpdateAction,
                Name: EffectiveConstraintName(f.Name, f.IsSystemNamed),
                IsDisabled: EffectiveConstraintDisabled(f.IsDisabled),
                IsNotTrusted: EffectiveConstraintTrust(f.IsNotTrusted),
                IsNotForReplication: EffectiveConstraintNotForReplication(f.IsNotForReplication)))
            .OrderBy(x => x.ReferencedSchema, StringComparer.Ordinal)
            .ThenBy(x => x.ReferencedTable, StringComparer.Ordinal)
            .ThenBy(x => ForeignKeyColumnPairsSortKey(x.ColumnPairs), StringComparer.Ordinal)
            .ThenBy(x => x.DeleteAction, StringComparer.Ordinal)
            .ThenBy(x => x.UpdateAction, StringComparer.Ordinal)
            .ThenBy(x => x.Name, StringComparer.Ordinal)
            .ThenBy(x => x.IsDisabled)
            .ThenBy(x => x.IsNotTrusted)
            .ThenBy(x => x.IsNotForReplication))
        {
            AppendString(hasher, "FK:");
            AppendString(hasher, fk.Name);
            AppendString(hasher, fk.ReferencedSchema);
            AppendString(hasher, fk.ReferencedTable);
            AppendInt(hasher, fk.ColumnPairs.Count);
            foreach (var pair in fk.ColumnPairs)
            {
                AppendString(hasher, pair.ParentColumn);
                AppendString(hasher, pair.ReferencedColumn);
            }
            AppendString(hasher, fk.DeleteAction);
            AppendString(hasher, fk.UpdateAction);
            AppendBool(hasher, fk.IsDisabled);
            AppendBool(hasher, fk.IsNotTrusted);
            AppendBool(hasher, fk.IsNotForReplication);
        }
    }

    private void HashCheckConstraints(IncrementalHash hasher, List<CheckConstraintSchema> constraints)
    {
        // Sort by every hashed field (see HashForeignKeys): under Constraints.IgnoreNames two CHECK
        // constraints can share a predicate and differ only in enforcement state, and the extraction
        // query has no ORDER BY, so the enforcement flags must participate in the ordering.
        foreach (var ck in constraints
            .Select(c => (
                c.Definition,
                Name: EffectiveConstraintName(c.Name, c.IsSystemNamed),
                IsDisabled: EffectiveConstraintDisabled(c.IsDisabled),
                IsNotTrusted: EffectiveConstraintTrust(c.IsNotTrusted),
                IsNotForReplication: EffectiveConstraintNotForReplication(c.IsNotForReplication)))
            .OrderBy(x => x.Definition, StringComparer.Ordinal)
            .ThenBy(x => x.Name, StringComparer.Ordinal)
            .ThenBy(x => x.IsDisabled)
            .ThenBy(x => x.IsNotTrusted)
            .ThenBy(x => x.IsNotForReplication))
        {
            AppendString(hasher, "CHECK:");
            AppendString(hasher, ck.Name);
            AppendString(hasher, ck.Definition);
            AppendBool(hasher, ck.IsDisabled);
            AppendBool(hasher, ck.IsNotTrusted);
            AppendBool(hasher, ck.IsNotForReplication);
        }
    }

    private void HashDefaultConstraints(IncrementalHash hasher, List<DefaultConstraintSchema> constraints)
    {
        foreach (var df in constraints
            .Select(c => (Df: c, Name: EffectiveConstraintName(c.Name, c.IsSystemNamed)))
            .OrderBy(x => x.Df.ColumnName, StringComparer.Ordinal)
            .ThenBy(x => x.Df.Definition, StringComparer.Ordinal)
            .ThenBy(x => x.Name, StringComparer.Ordinal))
        {
            AppendString(hasher, "DEFAULT:");
            AppendString(hasher, df.Name);
            AppendString(hasher, df.Df.ColumnName);
            AppendString(hasher, df.Df.Definition);
        }
    }

    private void HashStoredProcedure(IncrementalHash hasher, StoredProcedureSchema proc)
    {
        AppendString(hasher, "PROC:");
        // Include schema name to distinguish dbo.GetEmployee from sales.GetEmployee
        AppendString(hasher, proc.SchemaName);
        AppendString(hasher, proc.Name);

        // Optionally include definition hash to detect body changes
        if (!_options.Modules.HasFlag(ModuleNormalization.IgnoreBodyText))
        {
            AppendString(hasher, "DEFHASH:");
            AppendString(hasher, proc.DefinitionHash);
        }

        // ANSI_NULLS / QUOTED_IDENTIFIER are captured at CREATE time and are not present in the module
        // text, so they participate independently of the definition hash and of the body-text bit.
        // IgnoreSetOptions neutralizes them to the default-on value.
        var ignoreSetOptions = _options.Modules.HasFlag(ModuleNormalization.IgnoreSetOptions);
        AppendString(hasher, "SET:");
        AppendBool(hasher, ignoreSetOptions ? true : proc.UsesAnsiNulls);
        AppendBool(hasher, ignoreSetOptions ? true : proc.UsesQuotedIdentifier);

        foreach (var param in proc.Parameters)
        {
            HashParameter(hasher, param);
        }
    }

    private static void HashParameter(IncrementalHash hasher, ParameterSchema param)
    {
        AppendString(hasher, "PARAM:");
        AppendString(hasher, param.Name);
        AppendString(hasher, param.Type);
        AppendInt(hasher, param.MaxLength);
        AppendInt(hasher, param.Precision);
        AppendInt(hasher, param.Scale);
        AppendBool(hasher, param.IsNullable);
        AppendBool(hasher, param.IsOutput);
        AppendBool(hasher, param.IsReadonly);
        AppendString(hasher, param.XmlSchemaCollectionName ?? string.Empty);
        AppendBool(hasher, param.IsXmlDocument);
    }

    private void HashUserDefinedTableType(IncrementalHash hasher, UserDefinedTableTypeSchema udt)
    {
        AppendString(hasher, "UDT:");
        // Include schema name to distinguish types in different schemas
        AppendString(hasher, udt.SchemaName);
        AppendString(hasher, udt.Name);

        HashIdentity(hasher, udt.IdentityColumn, udt.IdentitySeed, udt.IdentityIncrement, udt.IdentityNotForReplication);

        // A memory-optimized table type marshals differently from a disk-based one.
        AppendString(hasher, "TYPEKIND:");
        AppendBool(hasher, udt.IsMemoryOptimized);

        // Always in column_id order — a TVP marshals its columns positionally, so column order is part
        // of the type's wire contract. IgnoreColumnOrder deliberately does NOT relax this (tables only).
        foreach (var column in udt.Columns.OrderBy(c => c.ColumnId).ThenBy(c => c.Name, StringComparer.Ordinal))
        {
            HashColumn(hasher, column);
        }

        // Table types cannot declare foreign keys, so only key/check/default constraints participate.
        HashKeyConstraints(hasher, udt.KeyConstraints);
        HashCheckConstraints(hasher, udt.CheckConstraints);
        HashDefaultConstraints(hasher, udt.DefaultConstraints);
    }

    /// <summary>
    /// Applies the configured normalization options to an index, producing the values that
    /// actually participate in the hash.
    /// </summary>
    private IndexSchema GetEffectiveIndex(IndexSchema index)
    {
        var idx = _options.Indexes;
        return index with
        {
            Name = EffectiveIndexName(index.Name),
            TypeDesc = EffectiveTypeDesc(index.TypeDesc),
            KeyColumns = EffectiveKeyColumns(index.KeyColumns),
            FillFactor = idx.HasFlag(IndexNormalization.IgnoreFillFactor) ? (byte)0 : index.FillFactor,
            IsPadded = idx.HasFlag(IndexNormalization.IgnorePadIndex) ? false : index.IsPadded,
            AllowRowLocks = idx.HasFlag(IndexNormalization.IgnoreLockOptions) ? true : index.AllowRowLocks,
            AllowPageLocks = idx.HasFlag(IndexNormalization.IgnoreLockOptions) ? true : index.AllowPageLocks,
            IsDisabled = idx.HasFlag(IndexNormalization.IgnoreDisabled) ? false : index.IsDisabled,
        };
    }

    // IgnoreNames wins over NormalizeAutoGeneratedNames. Auto-generated names hash as the
    // empty-string sentinel (length-prefixed hashing keeps "" unambiguous).
    private string EffectiveIndexName(string name)
    {
        if (_options.Indexes.HasFlag(IndexNormalization.IgnoreNames))
            return string.Empty;
        return _options.Indexes.HasFlag(IndexNormalization.NormalizeAutoGeneratedNames) && IsAutoGeneratedName(name) ? string.Empty : name;
    }

    // Constraint names mirror the exact index-name comparison: ignored entirely, normalized to the
    // empty sentinel for auto-generated names, or compared exactly. Under NormalizeAutoGeneratedNames
    // a name is treated as auto-generated when the authoritative catalog flag sys.*.is_system_named is
    // set OR the name matches the generated shape (the same IsAutoGeneratedName heuristic indexes use).
    // The flag alone is not enough: it is set at CREATE time from whether that statement supplied a
    // name, so scripting a system-named constraint out and recreating it with its literal name flips
    // is_system_named to 0 even though the name is still obviously generated. The shape check survives
    // that script-out / DACPAC boundary; the flag still catches freshly-minted names whose shape may
    // differ. IgnoreNames still wins over this.
    private string EffectiveConstraintName(string name, bool isSystemNamed)
    {
        if (_options.Constraints.HasFlag(ConstraintNormalization.IgnoreNames))
            return string.Empty;
        return _options.Constraints.HasFlag(ConstraintNormalization.NormalizeAutoGeneratedNames) && (isSystemNamed || IsAutoGeneratedName(name)) ? string.Empty : name;
    }

    // FK/CHECK enforcement state: neutralized to the trusted/enabled/replicated value when the
    // corresponding Constraint bit is set. These feed both the hash and the ordering sort keys.
    private bool EffectiveConstraintDisabled(bool isDisabled) => _options.Constraints.HasFlag(ConstraintNormalization.IgnoreDisabled) ? false : isDisabled;

    private bool EffectiveConstraintTrust(bool isNotTrusted) => _options.Constraints.HasFlag(ConstraintNormalization.IgnoreTrust) ? false : isNotTrusted;

    private bool EffectiveConstraintNotForReplication(bool isNotForReplication) => _options.Constraints.HasFlag(ConstraintNormalization.IgnoreNotForReplication) ? false : isNotForReplication;

    // NormalizeClustering collapses only the rowstore CLUSTERED/NONCLUSTERED placement to a common
    // token; the COLUMNSTORE distinction (clustered vs nonclustered columnstore are fundamentally
    // different storage strategies) is preserved.
    private string EffectiveTypeDesc(string typeDesc)
    {
        if (!_options.Indexes.HasFlag(IndexNormalization.NormalizeClustering))
            return typeDesc;
        return typeDesc.Contains("COLUMNSTORE", StringComparison.OrdinalIgnoreCase) ? typeDesc : "X-CLUSTERED";
    }

    // Under IgnoreSortOrder the ASC/DESC direction of each key column is dropped; key column
    // order itself is always significant and is never reordered.
    private List<IndexKeyColumn> EffectiveKeyColumns(List<IndexKeyColumn> keyColumns)
    {
        return _options.Indexes.HasFlag(IndexNormalization.IgnoreSortOrder)
            ? keyColumns.Select(k => k with { IsDescendingKey = false }).ToList()
            : keyColumns;
    }

    private static void AppendKeyColumns(IncrementalHash hasher, List<IndexKeyColumn> keyColumns)
    {
        AppendInt(hasher, keyColumns.Count);
        foreach (var k in keyColumns)
        {
            AppendString(hasher, k.Name);
            AppendBool(hasher, k.IsDescendingKey);
        }
    }

    // Ephemeral ordinal sort keys (used only to order elements deterministically, never hashed). The
    // separators are low control characters unlikely to occur in identifiers; a sort-key collision
    // would only perturb ordering, and two genuinely-equal element sets always sort identically anyway.
    private const string SortFieldSep = "";
    private const string SortItemSep = "";
    private const string SortDescMarker = "";

    private static string IndexSortKey(IndexSchema index) =>
        string.Join(SortFieldSep, KeyColumnsSortKey(index.KeyColumns), index.TypeDesc, index.Name, string.Join(SortItemSep, index.IncludedColumns), index.FilterDefinition ?? string.Empty);

    private static string KeyColumnsSortKey(List<IndexKeyColumn> keyColumns) =>
        string.Join(SortItemSep, keyColumns.Select(k => k.IsDescendingKey ? k.Name + SortDescMarker : k.Name));

    private static string ForeignKeyColumnPairsSortKey(List<ForeignKeyColumnPair> columnPairs) =>
        string.Join(SortItemSep, columnPairs.Select(c => c.ParentColumn + SortFieldSep + c.ReferencedColumn));

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

        // Auto-generated suffixes are long hex runs — at least 16 chars and a multiple of 8, e.g.
        // half- or full-GUID suffixes on missing-index 'nci_wi_...' names. Requiring >= 16 avoids
        // misclassifying ordinary names whose final segment is a short 8-char hex run
        // (e.g. 'IX_Audit_Record_CAFEBABE'); SQL Server's own PK__/DF__ system names are matched by
        // SystemGeneratedNameRegex above regardless of suffix length.
        return lastPart.Length >= 16 && lastPart.Length % 8 == 0 && lastPart.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'));
    }

    /// <summary>
    /// Matches SQL Server's auto-generated history table name for a system-versioned table created
    /// without an explicit HISTORY_TABLE: 'MSSQL_TemporalHistoryFor_&lt;object_id&gt;' with an optional
    /// '_&lt;n&gt;' disambiguator, optionally schema-qualified (e.g. 'dbo.MSSQL_TemporalHistoryFor_889838603').
    /// </summary>
    private static readonly Regex AutoGeneratedHistoryTableRegex = new(@"(^|\.)MSSQL_TemporalHistoryFor_[0-9]+(_[0-9]+)?$", RegexOptions.Compiled);

    // The auto-generated history table name embeds the parent table's object_id, which is not stable
    // across databases, so it is normalized to the empty sentinel; an explicitly-named history table
    // keeps its deterministic schema-qualified name.
    private static string NormalizeHistoryTableName(string? historyTableName)
    {
        if (string.IsNullOrEmpty(historyTableName))
            return string.Empty;
        return AutoGeneratedHistoryTableRegex.IsMatch(historyTableName) ? string.Empty : historyTableName;
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
