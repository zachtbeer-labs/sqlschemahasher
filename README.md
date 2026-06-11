# SqlSchemaHasher

One deterministic SHA256 per database schema — ground truth for drift detection across fleets. Read-only `sys.*` queries, no agents, no telemetry.

[![CI](https://github.com/zachtbeer-labs/sqlschemahasher/actions/workflows/ci.yml/badge.svg)](https://github.com/zachtbeer-labs/sqlschemahasher/actions/workflows/ci.yml)
[![CodeQL](https://github.com/zachtbeer-labs/sqlschemahasher/actions/workflows/codeql.yml/badge.svg)](https://github.com/zachtbeer-labs/sqlschemahasher/actions/workflows/codeql.yml)
[![OpenSSF Scorecard](https://api.securityscorecards.dev/projects/github.com/zachtbeer-labs/sqlschemahasher/badge)](https://securityscorecards.dev/viewer/?uri=github.com/zachtbeer-labs/sqlschemahasher)
[![NuGet](https://img.shields.io/nuget/v/zachtbeer.SqlSchemaHasher)](https://www.nuget.org/packages/zachtbeer.SqlSchemaHasher)
[![NuGet Downloads](https://img.shields.io/nuget/dt/zachtbeer.SqlSchemaHasher)](https://www.nuget.org/packages/zachtbeer.SqlSchemaHasher)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
[![Target frameworks](https://img.shields.io/badge/targets-net6.0%20%7C%20net7.0%20%7C%20net8.0%20%7C%20net9.0%20%7C%20net10.0-512bd4.svg)](src/zachtbeer.SqlSchemaHasher.csproj)

When you manage many SQL Server databases, schemas drift. Someone modifies a table directly. A backup gets restored from the wrong date. A migration partially applies and nobody notices. SqlSchemaHasher computes a deterministic SHA256 hash from the schema itself, so "what schema is this database actually running?" becomes a one-line query with a one-string answer.

## Install

```bash
dotnet add package zachtbeer.SqlSchemaHasher
```

## Quickstart

```csharp
using zachtbeer.SqlSchemaHasher;

// Simple one-liner to get a schema hash
var hash = await SqlSchemaHash.GetHashAsync("Server=localhost;Database=MyDb;Trusted_Connection=true");
// Returns: "dGhpcyBpcyBhIGJhc2U2NCBoYXNo..."
```

## Why Not Version Tables, Migration Journals, or Schema Compare?

| You want to… | Version table / migration journal | SqlPackage schema compare | **SqlSchemaHasher** |
| --- | --- | --- | --- |
| Detect manual, out-of-band schema changes | No — tracks what *ran*, not what the schema *looks like* | Yes, but only against one chosen baseline | Yes — the hash is computed from the schema itself |
| Trust the answer without process discipline | No — someone has to bump it, everywhere, consistently | Yes | Yes — content-based, it cannot lie |
| Group an entire fleet by actual deployed schema | Only as reliable as the version data | Pairwise comparisons, O(n²) and slow | One hash per database, group by string |
| Get a CI-friendly single value to log or compare | A version string that says what *should* be deployed | A diff report | One base64 SHA256 string |
| Stay fast on large estates | Fast but untrustworthy | Full model extraction per database | One batched read of `sys.*` catalog views |
| Ignore cosmetic differences (auto-named indexes, clustering) | N/A | Limited | Built-in normalization options |

Version numbers tell you what *should* be deployed, not what *actually is* deployed. A content-based hash is ground truth: two databases with the same hash are structurally identical, period. Hashes also surface unexpected groupings — you might discover that 300 databases are on schema A, 50 are on schema B, and 3 are on something nobody recognizes.

## Built To Be Trusted

- **Read-only by design** — the library only reads `sys.*` catalog views; it never modifies the target database.
- **No network calls except to your SQL Server, no telemetry**, no analytics, no license checks.
- **Deterministic, reproducible builds** with SourceLink and symbol packages (`.snupkg`).
- **Locked-mode NuGet restore** — dependency versions are pinned via committed lock files.
- **All GitHub Actions pinned to full commit SHAs** with least-privilege `permissions` blocks.
- **CodeQL static analysis, OpenSSF Scorecard, and Dependabot** run continuously.
- **OIDC trusted publishing to NuGet.org** — no long-lived API keys.
- **Signed SLSA build provenance and a CycloneDX SBOM** attached to every release. See [Verifying Build Provenance](#verifying-build-provenance).

## Use It For

- **Define the golden schema** — Hash a reference database and compare everything against it. Any mismatch is immediately actionable.
- **Continuous drift monitoring** — Compute schema hashes on a schedule and report them centrally. Deviations surface in dashboards instead of in support tickets.
- **Migration tooling** — Compare a database's hash against the target schema before generating diff SQL. If hashes match, skip the diff entirely.
- **Fleet-wide grouping** — Group all databases by hash to get the full picture of what's actually deployed.

## API Reference

### `SqlSchemaHash.GetHashAsync(connectionString)`

Returns a base64-encoded SHA256 hash of the database schema.

```csharp
var hash = await SqlSchemaHash.GetHashAsync(connectionString);
```

### `SqlSchemaHash.GetHashAsync(connectionString, options)`

Returns a hash with custom normalization options.

```csharp
var options = new SchemaHashOptions
{
    NormalizeAutoGeneratedIndexNames = true,  // Strip GUID suffixes from index names
    NormalizeClusteringType = true            // Treat clustered/nonclustered as equivalent
};

var hash = await SqlSchemaHash.GetHashAsync(connectionString, options);
```

### `SqlSchemaHash.ExtractSchemaAsync(connectionString)`

Returns detailed schema metadata if you need to inspect the schema structure.

```csharp
var schema = await SqlSchemaHash.ExtractSchemaAsync(connectionString);

Console.WriteLine($"Tables: {schema.Tables.Count}");
Console.WriteLine($"Stored Procedures: {schema.StoredProcedures.Count}");
Console.WriteLine($"User-Defined Types: {schema.UserDefinedTableTypes.Count}");
```

### `SqlSchemaHash.ComputeHash(schema, options)`

Computes a hash from pre-extracted schema metadata. Useful when comparing the same schema with different normalization options.

```csharp
var schema = await SqlSchemaHash.ExtractSchemaAsync(connectionString);

var strictHash = SqlSchemaHash.ComputeHash(schema);
var normalizedHash = SqlSchemaHash.ComputeHash(schema, new SchemaHashOptions
{
    NormalizeAutoGeneratedIndexNames = true
});
```

## What Gets Hashed

The hash includes:

- **Tables**: Schema name, table name, columns (name, type, precision, nullability), indexes, constraints, identity columns
- **Stored Procedures**: Schema name, procedure name, parameters, and a hash of the procedure body (detects logic changes)
- **User-Defined Table Types**: Schema name, type name, columns

### Excluded Objects

The following objects are automatically excluded from hashing (via `SchemaHashOptions.Default`):
- `sysdiagrams` table and related diagram helper procedures (`fn_diagramobjects`, `sp_alterdiagram`, `sp_creatediagram`, `sp_dropdiagram`, `sp_helpdiagramdefinition`, `sp_helpdiagrams`, `sp_renamediagram`)

## Options

| Option | Default | Description |
|--------|---------|-------------|
| `SchemaFilter` | `null` | Filter to specific schema (e.g., `"dbo"`, `"sales"`). When null, all schemas are included. |
| `NormalizeAutoGeneratedIndexNames` | `false` | Strips GUID suffixes from auto-generated index names (e.g., `nci_wi_Table_ABC123` -> `nci_wi_Table`) |
| `NormalizeClusteringType` | `false` | Normalizes `clustered`/`nonclustered` to a common value |
| `IncludeStoredProcedureText` | `true` | When `false`, only procedure name and parameters are hashed (body changes ignored) |

## Real-World Examples

### Hash Only Specific Schema

```csharp
// Only hash objects in the dbo schema, ignoring other schemas like reporting or staging
var options = new SchemaHashOptions { SchemaFilter = "dbo" };
var hash = await SqlSchemaHash.GetHashAsync(connectionString, options);
```

### Schema Change Detection

```csharp
var beforeHash = await SqlSchemaHash.GetHashAsync(connectionString);
// ... apply migration ...
var afterHash = await SqlSchemaHash.GetHashAsync(connectionString);

if (beforeHash != afterHash)
{
    Console.WriteLine("Schema changed!");
}
```

### Group Databases by Schema

```csharp
var databases = new[] { "Db1", "Db2", "Db3", "Db4" };
var groups = new Dictionary<string, List<string>>();

foreach (var db in databases)
{
    var connStr = $"Server=localhost;Database={db};Trusted_Connection=true";
    var hash = await SqlSchemaHash.GetHashAsync(connStr);

    if (!groups.ContainsKey(hash))
        groups[hash] = new List<string>();
    groups[hash].Add(db);
}

// Databases with identical schemas are grouped together
```

### Environment Verification

```csharp
var prodHash = await SqlSchemaHash.GetHashAsync(prodConnectionString);
var stagingHash = await SqlSchemaHash.GetHashAsync(stagingConnectionString);

if (prodHash != stagingHash)
{
    throw new Exception("Staging schema does not match production!");
}
```

## Dependencies

- `Microsoft.Data.SqlClient` - SQL Server connectivity
- `Dapper` - Efficient database queries

## Project Status

Stable and published on [NuGet.org](https://www.nuget.org/packages/zachtbeer.SqlSchemaHasher). See the [CHANGELOG](CHANGELOG.md) for release history.

## Maintainers

Maintained by [Zachtbeer Labs B.V.](https://github.com/zachtbeer-labs) Security reports go to [security@zachtbeerlabs.nl](mailto:security@zachtbeerlabs.nl) — see [SECURITY.md](SECURITY.md).

## Verifying Build Provenance

Every release ships with a signed SLSA build provenance attestation (`provenance.intoto.jsonl`) and a CycloneDX SBOM (`bom.json`). The attestation covers the package bytes as built in CI, before nuget.org adds its repository signature, so verify against the `.nupkg` attached to the GitHub release:

```bash
gh attestation verify zachtbeer.SqlSchemaHasher.<version>.nupkg \
  --repo zachtbeer-labs/sqlschemahasher \
  --bundle provenance.intoto.jsonl
```

## Running Tests

```bash
dotnet test SqlSchemaHasher.sln
```

Integration tests use [Testcontainers](https://testcontainers.com/) to spin up SQL Server 2025 in Docker, so Docker must be running.

## License

[MIT](LICENSE)
