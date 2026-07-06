namespace zachtbeer.SqlSchemaHasher;

/// <summary>
/// Simple static API for computing deterministic schema hashes from SQL Server databases.
/// </summary>
/// <remarks>
/// The returned string is a versioned envelope of the form <c>&lt;version&gt;:&lt;base64hash&gt;</c>
/// (e.g. <c>2:dGhpcyBpc...</c>). The version guards against comparing hashes across library versions whose
/// extraction/hashing changed. Parse and compare envelopes with <see cref="SchemaHashResult"/>; take
/// <see cref="SchemaHashResult.Hash"/> for the bare base64 value if you need it. Only compare hashes
/// computed with the same <see cref="SchemaHashOptions"/> — the envelope does not encode the options used.
/// </remarks>
public static class SqlSchemaHash
{
    /// <summary>
    /// Computes a deterministic schema-hash envelope of the database schema
    /// using <see cref="SchemaHashOptions.Default"/> (an alias for <see cref="SchemaHashOptions.V2"/>).
    /// </summary>
    /// <param name="connectionString">SQL Server connection string.</param>
    /// <returns>Versioned envelope <c>&lt;version&gt;:&lt;base64hash&gt;</c>. See <see cref="SchemaHashResult"/>.</returns>
    /// <example>
    /// <code>
    /// var hash = await SqlSchemaHash.GetHashAsync("Server=localhost;Database=MyDb;...");
    /// // Returns: "2:dGhpcyBpcyBhIGhhc2g..."
    /// </code>
    /// </example>
    public static async Task<string> GetHashAsync(string connectionString)
    {
        return await GetHashAsync(connectionString, SchemaHashOptions.Default);
    }

    /// <summary>
    /// Computes a deterministic schema-hash envelope of the database schema with custom options.
    /// </summary>
    /// <param name="connectionString">SQL Server connection string.</param>
    /// <param name="options">Options for extraction and hash calculation (schema filter, index name normalization, etc.).</param>
    /// <returns>Versioned envelope <c>&lt;version&gt;:&lt;base64hash&gt;</c>. See <see cref="SchemaHashResult"/>.</returns>
    /// <example>
    /// <code>
    /// // Use a preset (V1, V2, Structural)...
    /// var hash = await SqlSchemaHash.GetHashAsync("Server=localhost;Database=MyDb;...", SchemaHashOptions.Structural);
    ///
    /// // ...or customize one
    /// var options = SchemaHashOptions.V2;
    /// options.SchemaFilter = "dbo";  // Only hash objects in the dbo schema
    /// var hash2 = await SqlSchemaHash.GetHashAsync("Server=localhost;Database=MyDb;...", options);
    /// </code>
    /// </example>
    public static async Task<string> GetHashAsync(string connectionString, SchemaHashOptions options)
    {
        var extractor = new SchemaExtractor();
        var calculator = new SchemaHashCalculator(options);

        var schema = await extractor.ExtractSchemaAsync(connectionString, options);
        var hexHash = calculator.ComputeHash(schema);

        return Envelope(hexHash);
    }

    /// <summary>
    /// Extracts detailed schema metadata from a SQL Server database.
    /// Use this if you need access to the raw schema information.
    /// </summary>
    /// <param name="connectionString">SQL Server connection string.</param>
    /// <returns>Schema metadata containing tables, stored procedures, and user-defined types.</returns>
    public static async Task<SchemaMetadata> ExtractSchemaAsync(string connectionString)
    {
        return await ExtractSchemaAsync(connectionString, null);
    }

    /// <summary>
    /// Extracts detailed schema metadata from a SQL Server database with optional filtering.
    /// Use this if you need access to the raw schema information.
    /// </summary>
    /// <param name="connectionString">SQL Server connection string.</param>
    /// <param name="options">Options for extraction (e.g., schema filter). Pass null for defaults.</param>
    /// <returns>Schema metadata containing tables, stored procedures, and user-defined types.</returns>
    public static async Task<SchemaMetadata> ExtractSchemaAsync(string connectionString, SchemaHashOptions? options)
    {
        var extractor = new SchemaExtractor();
        return await extractor.ExtractSchemaAsync(connectionString, options);
    }

    /// <summary>
    /// Computes a base64-encoded hash from pre-extracted schema metadata.
    /// Useful when you already have schema metadata and want to compute multiple hashes with different options.
    /// </summary>
    /// <param name="schema">Previously extracted schema metadata.</param>
    /// <param name="options">Options for hash calculation.</param>
    /// <returns>Versioned envelope <c>&lt;version&gt;:&lt;base64hash&gt;</c>. See <see cref="SchemaHashResult"/>.</returns>
    public static string ComputeHash(SchemaMetadata schema, SchemaHashOptions? options = null)
    {
        var calculator = new SchemaHashCalculator(options ?? SchemaHashOptions.Default);
        var hexHash = calculator.ComputeHash(schema);
        return Envelope(hexHash);
    }

    /// <summary>
    /// Wraps a raw hex hash in the versioned envelope <c>&lt;version&gt;:&lt;base64hash&gt;</c>.
    /// </summary>
    private static string Envelope(string hexHash)
    {
        var hash = Convert.ToBase64String(Convert.FromHexString(hexHash));
        return new SchemaHashResult(ResolveHashFormatVersion(), hash).ToString();
    }

    /// <summary>
    /// Resolves the hash-format version embedded in the envelope. Defaults to the assembly major version:
    /// the hash output is contractually stable within a major and may change across majors. This is the
    /// single swap point — replace the body with a <c>major =&gt; hashVersion</c> map if a future major
    /// ever leaves the hash contract unchanged (or a minor is forced to break it).
    /// </summary>
    private static int ResolveHashFormatVersion()
    {
        return typeof(SqlSchemaHash).Assembly.GetName().Version?.Major ?? 0;
    }
}
