# SqlSchemaHasher

One deterministic SHA256 per database schema — ground truth for drift detection across fleets. Read-only `sys.*` queries, no agents, no telemetry.

[![CI](https://github.com/zachtbeer-labs/sqlschemahasher/actions/workflows/ci.yml/badge.svg)](https://github.com/zachtbeer-labs/sqlschemahasher/actions/workflows/ci.yml)
[![CodeQL](https://github.com/zachtbeer-labs/sqlschemahasher/actions/workflows/codeql.yml/badge.svg)](https://github.com/zachtbeer-labs/sqlschemahasher/actions/workflows/codeql.yml)
[![OpenSSF Scorecard](https://api.securityscorecards.dev/projects/github.com/zachtbeer-labs/sqlschemahasher/badge)](https://securityscorecards.dev/viewer/?uri=github.com/zachtbeer-labs/sqlschemahasher)
[![NuGet](https://img.shields.io/nuget/v/zachtbeer.SqlSchemaHasher)](https://www.nuget.org/packages/zachtbeer.SqlSchemaHasher)
[![NuGet Downloads](https://img.shields.io/nuget/dt/zachtbeer.SqlSchemaHasher)](https://www.nuget.org/packages/zachtbeer.SqlSchemaHasher)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
[![Target frameworks](https://img.shields.io/badge/targets-net6.0%20%7C%20net7.0%20%7C%20net8.0%20%7C%20net9.0%20%7C%20net10.0-512bd4.svg)](src/zachtbeer.SqlSchemaHasher.csproj)
[![Docs](https://img.shields.io/badge/docs-online-512bd4.svg)](https://zachtbeer-labs.github.io/sqlschemahasher/)

📖 **[Full documentation →](https://zachtbeer-labs.github.io/sqlschemahasher/)**

When you manage many SQL Server databases, schemas drift. Someone modifies a table directly. A backup gets restored from the wrong date. A migration partially applies and nobody notices. SqlSchemaHasher computes a deterministic SHA256 hash from the schema itself, so "what schema is this database actually running?" becomes a one-line query with a one-string answer.

## Install

```bash
dotnet add package zachtbeer.SqlSchemaHasher
```

## Requirements

- **SQL Server**: 2016 (13.x) or later, or Azure SQL. Extraction reads catalog columns introduced in 2016 (temporal `temporal_type`/`generated_always_type`, `sys.masked_columns`, `encryption_type_desc`), so older servers are not supported.
- **Exercised against**: SQL Server 2019 and 2022 in CI, SQL Server 2025 locally in the test suite.
- **.NET**: targets `net6.0`, `net7.0`, `net8.0`, `net9.0`, and `net10.0`.

## Quickstart

```csharp
using zachtbeer.SqlSchemaHasher;

// Simple one-liner to get a schema hash
var hash = await SqlSchemaHash.GetHashAsync("Server=localhost;Database=MyDb;Trusted_Connection=true");
// Returns a versioned envelope: "2:dGhpcyBpcyBhIGJhc2U2NCBoYXNo..."
```

The returned string is a **versioned envelope** — `<version>:<base64hash>` — not a bare hash. The `<version>` (the library's major version) lets you tell a genuine schema change apart from a library upgrade that changed how schemas are hashed. Compare envelopes with `SchemaHashResult` rather than raw string equality (see [Comparing hashes](#comparing-hashes)).

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

## Performance

SqlSchemaHasher is fast — schema hashing is measured in milliseconds, not seconds. See [Performance](https://zachtbeer-labs.github.io/sqlschemahasher/performance) for measured numbers, methodology and caveats.

## API Reference

### `SqlSchemaHash.GetHashAsync(connectionString)`

Returns a versioned schema-hash envelope `<version>:<base64hash>` (see [Comparing hashes](#comparing-hashes)).

```csharp
var hash = await SqlSchemaHash.GetHashAsync(connectionString);
```

### `SqlSchemaHash.GetHashAsync(connectionString, options)`

Returns a hash with custom comparison settings. Start from a [preset](#presets) or build your own:

```csharp
// Preset: structure-only comparison
var hash = await SqlSchemaHash.GetHashAsync(connectionString, SchemaHashOptions.Structural);

// Or customize per-domain normalization (each domain is a [Flags] enum)
var options = new SchemaHashOptions
{
    // Neutralize auto-generated names and treat clustered/nonclustered as equivalent
    Indexes = IndexNormalization.NormalizeAutoGeneratedNames | IndexNormalization.NormalizeClustering,
    Constraints = ConstraintNormalization.NormalizeAutoGeneratedNames
};
var hash2 = await SqlSchemaHash.GetHashAsync(connectionString, options);
```

### `SqlSchemaHash.ExtractSchemaAsync(connectionString)`

Returns detailed schema metadata if you need to inspect the schema structure.

```csharp
var schema = await SqlSchemaHash.ExtractSchemaAsync(connectionString);

Console.WriteLine($"Tables: {schema.Tables.Count}");
Console.WriteLine($"Stored Procedures: {schema.StoredProcedures.Count}");
Console.WriteLine($"User-Defined Types: {schema.UserDefinedTableTypes.Count}");
Console.WriteLine($"Views: {schema.Views.Count}");
Console.WriteLine($"Functions: {schema.Functions.Count}");
Console.WriteLine($"Triggers: {schema.Triggers.Count}");
Console.WriteLine($"Sequences: {schema.Sequences.Count}");
Console.WriteLine($"Synonyms: {schema.Synonyms.Count}");
```

### `SqlSchemaHash.ComputeHash(schema, options)`

Computes a hash from pre-extracted schema metadata. Useful when comparing the same schema with different normalization options.

```csharp
var schema = await SqlSchemaHash.ExtractSchemaAsync(connectionString);

var strictHash = SqlSchemaHash.ComputeHash(schema);
var normalizedHash = SqlSchemaHash.ComputeHash(schema, new SchemaHashOptions
{
    Indexes = IndexNormalization.NormalizeAutoGeneratedNames,
    Constraints = ConstraintNormalization.NormalizeAutoGeneratedNames
});
```

## Comparing hashes

Every hash is returned as a **versioned envelope**: `<version>:<base64hash>`, e.g. `2:dGhpcyBpc...`.

- **`version`** — the library's major version. The hash output is stable within a major version and may change across majors (as extraction fidelity improves). This lets you tell a real schema change apart from a library upgrade.
- **`hash`** — the base64 SHA256 of the schema.

> **Compare only hashes computed with the same [options](#presets).** The envelope does not record which options were used, so a `V2` hash and a `Structural` hash of the *same* database have different hashes and compare as `Different` — indistinguishable from a genuine schema change. Fix your options in one place (a preset or shared config) and use them on both sides, the same way you would keep the library version consistent.

For raw storage or logging, plain string equality still works when you know both hashes came from the same library version and options. Otherwise, compare with `SchemaHashResult`, which makes the version distinction explicit:

```csharp
switch (SchemaHashResult.Compare(hashA, hashB))
{
    case SchemaHashComparison.Equal:         // same schema
    case SchemaHashComparison.Different:      // schema differs (or different options were used)
    case SchemaHashComparison.Incomparable:   // different library version — recompute before trusting
}

// Or inspect the parts (e.g. to pull the bare hash back out):
var parsed = SchemaHashResult.Parse(hashA);
Console.WriteLine($"v{parsed.Version} / {parsed.Hash}");
```

`SchemaHashResult.TryParse` returns `false` for a legacy bare-base64 hash produced before envelopes existed, so those safely compare as `Incomparable` rather than silently mismatching.

## What Gets Hashed

The hash includes:

- **Tables**: Schema name, table name, columns (name, type, precision, nullability, string collation, and — for computed columns — the formula and whether it is `PERSISTED`), indexes (including key sort order, INCLUDE columns, and filtered-index predicates), constraints (foreign keys include the referenced table/column and the `ON DELETE`/`ON UPDATE` referential actions), identity columns
- **Stored Procedures**: Schema name, procedure name, parameters (including `OUTPUT` direction and the `READONLY` flag), and a hash of the procedure body (detects logic changes)
- **User-Defined Table Types**: Schema name, type name, columns (including string collation and computed column formulas)
- **Views**: Schema name, view name, a hash of the view body, CREATE-time SET options, and — for a schemabound indexed view — its indexes. View columns are deliberately not extracted (the definition hash is the view's identity; a `SELECT *` view's column metadata goes stale until `sp_refreshview` runs, which would otherwise inject spurious diffs)
- **Functions**: Schema name, function name, the raw `type_desc` (distinguishing scalar / inline table-valued / multi-statement table-valued functions even when body text is excluded), parameters (a scalar function's return type arrives as its own parameter row), and a hash of the function body
- **Triggers**: Schema name, trigger name, parent table/view, disabled/`INSTEAD OF`/`NOT FOR REPLICATION` flags, the DML event set together with `FIRST`/`LAST` ordering (set out-of-band via `sp_settriggerorder`), and a hash of the trigger body
- **Sequences**: Schema name, sequence name, data type, precision, start value, increment, min/max bounds, cycling, and cache size — **not** the current value, which is runtime state that advances on every `NEXT VALUE FOR` and would make the hash unstable across otherwise-identical databases
- **Synonyms**: Schema name, synonym name, and the target object name exactly as the catalog stores it (synonym targets are not validated or resolved at CREATE time)
- **Extended properties**: name, value, and value base type of every `sys.extended_properties` entry (e.g. `MS_Description`) scoped to the database, a schema, an in-scope object, a column, a parameter, an index, or a user-defined table type — targets are resolved to names, and a property follows its owner out of the hash when the owner is excluded. Set `IgnoreExtendedProperties` to leave them out entirely. Properties on constraint objects are not captured (system-generated constraint names are not deterministic across databases)
- **Alias scalar types**: a column, parameter, or sequence typed with an alias scalar type (`CREATE TYPE dbo.OrderTotal FROM DECIMAL(9,2) NOT NULL`) hashes both the schema-qualified alias name and its underlying base type/length/precision/scale/nullability, so dropping and recreating the alias with a different base type changes the hash

### Excluded Objects

When `IgnoreSysDiagramObjects` is enabled (it is in every preset, including `Default`), SSMS database diagram objects are excluded from hashing:
- `sysdiagrams` table and related diagram helper procedures (`fn_diagramobjects`, `sp_alterdiagram`, `sp_creatediagram`, `sp_dropdiagram`, `sp_helpdiagramdefinition`, `sp_helpdiagrams`, `sp_renamediagram`)

This composes additively with any names you put in `ObjectNamesToIgnore`. A bare `new SchemaHashOptions()` excludes nothing.

### Not Yet Captured

The following are **not** read, so adding, altering, or dropping them does not change the hash:

- **CLR modules and scalar CLR UDTs**: CLR functions/triggers/aggregates and scalar CLR user-defined types are out of scope — hashing a compiled binary body is a fundamentally different extraction shape than everything else this library captures.
- **DDL and server-scoped triggers**: only object (DML) triggers on tables and views are captured; database-scoped DDL triggers and server-scoped triggers are not.
- **Partitioning and filegroup/data-space placement**: moving a table to a different filegroup, or repartitioning it, does not change the hash.
- **Encrypted module bodies**: two different `WITH ENCRYPTION` modules (procedures, views, functions, or triggers) with identical signatures collide on a shared `<encrypted>` sentinel — inherent, since the body is unreadable once encrypted.

See the [FAQ](https://zachtbeer-labs.github.io/sqlschemahasher/faq) for the reasoning behind these scope decisions and other frequently-asked design questions.

### Known limitation: definition-text rendering drift

CHECK constraint, DEFAULT constraint, computed-column, and filtered-index definitions are hashed as SQL Server renders them. Different SQL Server major versions can render the same expression differently (spacing, parenthesization, casing of built-in functions), which can produce a spurious `Different` comparison across server versions even though nothing semantically changed. Compare hashes taken from the same SQL Server major version to avoid this. See the docs site's [Known Limitations](https://zachtbeer-labs.github.io/sqlschemahasher/known-limitations) page for details and other coverage gaps — this is intentionally not normalized.

## Presets

Static properties on `SchemaHashOptions` provide ready-made comparison profiles. Each access returns a fresh instance, so you can customize one without affecting others.

| Preset | Question it answers | Intent |
|--------|---------------------|--------|
| `SchemaHashOptions.V2` (= `Default`) | "Is this byte-for-byte the same schema?" | Recommended. Everything compared exactly — index names, clustering, key sort order — with SSMS diagram objects ignored. |
| `SchemaHashOptions.V1` | Same, with v1's blind spot | The comparison semantics of v1 of this library: like V2, but index key sort order is not compared (v1 never captured it). Note: hashes still differ from package v1.x output because v2 extraction fixes apply unconditionally. |
| `SchemaHashOptions.Structural` | "Is the schema logically the same?" | Ignores naming, physical layout and enforcement-state noise: index and constraint names, clustering, key sort order, storage/locking options, disabled/untrusted state, and table column order. Columns, key column sets, uniqueness, constraint definitions, identity columns, and module bodies are still compared. |

Every bit, and which presets set it. ✅ = **set**, so the difference is ignored; — = not set, so the difference is compared exactly. Each bit's meaning is in [Options](#options) below.

| Domain | Bit | `new()` | `V1` | `V2` / `Default` | `Structural` |
|--------|-----|:-------:|:----:|:----------------:|:------------:|
| `Tables` | `IgnoreColumnOrder` | — | — | — | ✅ |
| | `IgnoreIdentitySeed` | — | — | — | — |
| | `IgnoreIdentityNotForReplication` | — | — | — | — |
| | `IgnoreTemporalRetention` | — | — | — | — |
| `Columns` | `IgnoreCollation` | — | — | — | — |
| | `IgnoreAnsiPadding` | — | — | — | — |
| | `IgnoreDynamicDataMasking` | — | — | — | — |
| `Indexes` | `IgnoreNames` | — | — | — | ✅ |
| | `NormalizeAutoGeneratedNames` | — | — | — | — <sup>†</sup> |
| | `NormalizeClustering` | — | — | — | ✅ |
| | `IgnoreSortOrder` | — | ✅ | — | ✅ |
| | `IgnoreFillFactor` | — | — | — | ✅ |
| | `IgnorePadIndex` | — | — | — | ✅ |
| | `IgnoreLockOptions` | — | — | — | ✅ |
| | `IgnoreDisabled` | — | — | — | ✅ |
| `Constraints` | `IgnoreNames` | — | — | — | ✅ |
| | `NormalizeAutoGeneratedNames` | — | — | — | — <sup>†</sup> |
| | `IgnoreDisabled` | — | — | — | ✅ |
| | `IgnoreTrust` | — | — | — | ✅ |
| | `IgnoreNotForReplication` | — | — | — | ✅ |
| `Modules` | `IgnoreBodyText` | — | — | — | — |
| | `IgnoreSetOptions` | — | — | — | — |
| *scoping* | `IgnoreSysDiagramObjects` | — | ✅ | ✅ | ✅ |
| | `IgnoreExtendedProperties` | — | — | — | — |

<sup>†</sup> Not set, but moot: `Structural` sets `IgnoreNames` in the same domain, which drops *all* names and wins over `NormalizeAutoGeneratedNames`.

In enum terms:

| Setting | `new()` | `V1` | `V2` / `Default` | `Structural` |
|---------|---------|------|------------------|--------------|
| `Tables` | `Strict` | `Strict` | `Strict` | `Structural` |
| `Columns` | `Strict` | `Strict` | `Strict` | `Strict` |
| `Indexes` | `Strict` | `IgnoreSortOrder` | `Strict` | `Structural` |
| `Constraints` | `Strict` | `Strict` | `Strict` | `Structural` |
| `Modules` | `Strict` | `Strict` | `Strict` | `Strict` |
| `IgnoreSysDiagramObjects` | `false` | `true` | `true` | `true` |

Where the per-domain `Structural` combos are `TableNormalization.Structural` = `IgnoreColumnOrder`; `IndexNormalization.Structural` = `IgnoreNames | NormalizeClustering | IgnoreSortOrder | IgnoreFillFactor | IgnorePadIndex | IgnoreLockOptions | IgnoreDisabled`; `ConstraintNormalization.Structural` = `IgnoreNames | IgnoreDisabled | IgnoreTrust | IgnoreNotForReplication`; and `ColumnNormalization.Structural` = `ModuleNormalization.Structural` = `Strict` (collation is semantic and a module body *is* the module — neither is layout noise).

## Options

Normalization is grouped into five `[Flags]` enums, one per schema domain. Each defaults to `Strict` (= `0`, full fidelity); set individual bits or take a per-domain combo. **Every bit only ever *removes* a distinction from the hash** — it can never make two identical schemas hash differently.

Read the table as: *set this bit, and two databases that differ **only** in this way hash identically.*

| Domain | Bit | What stops mattering | Example of a difference it hides |
|--------|-----|----------------------|----------------------------------|
| `Tables`<br/>`TableNormalization` | `IgnoreColumnOrder` | A table column's ordinal position; columns are matched by name instead | `(Id, Name, Email)` ≡ `(Id, Email, Name)`. Tables only — a table type stays order-sensitive, since a TVP marshals positionally |
| | `IgnoreIdentitySeed` | The identity seed and increment | `IDENTITY(1,1)` ≡ `IDENTITY(1000,5)` |
| | `IgnoreIdentityNotForReplication` | The identity column's NOT FOR REPLICATION flag | `IDENTITY(1,1)` ≡ `IDENTITY(1,1) NOT FOR REPLICATION` |
| | `IgnoreTemporalRetention` | A system-versioned table's finite history retention policy (period + unit) | `HISTORY_RETENTION_PERIOD = 6 MONTHS` ≡ `= 2 YEARS` |
| `Columns`<br/>`ColumnNormalization` | `IgnoreCollation` | A column's collation | `SQL_Latin1_General_CP1_CI_AS` ≡ `Latin1_General_100_CI_AS` — the usual cause is a differing server/database default collation per environment |
| | `IgnoreAnsiPadding` | The ANSI_PADDING (`is_ansi_padded`) storage flag | The same `varchar` column created under `SET ANSI_PADDING ON` ≡ under `OFF` |
| | `IgnoreDynamicDataMasking` | Dynamic Data Masking state (`is_masked` and the masking function) | `Email varchar(200)` ≡ `Email varchar(200) MASKED WITH (FUNCTION = 'email()')` |
| `Indexes`<br/>`IndexNormalization` | `IgnoreNames` | The index name, entirely | `IX_Orders_CustomerId` ≡ `IX_Cust` over the same keys. Wins over `NormalizeAutoGeneratedNames` |
| | `NormalizeAutoGeneratedNames` | Only *generated-shaped* index names, collapsed to a sentinel; explicit names still compare exactly | `PK__Orders__3214EC07A1B2C3D4` ≡ `PK__Orders__3214EC0712345678` (matched by name shape — indexes carry no `is_system_named` flag) |
| | `NormalizeClustering` | Rowstore CLUSTERED vs NONCLUSTERED placement | A clustered PK ≡ a nonclustered PK on the same keys. Columnstore stays distinct from rowstore |
| | `IgnoreSortOrder` | Each key column's ASC/DESC direction (key column *order* still counts) | `(OrderDate ASC)` ≡ `(OrderDate DESC)` |
| | `IgnoreFillFactor` | The FILLFACTOR storage option | `FILLFACTOR = 90` ≡ the default |
| | `IgnorePadIndex` | The PAD_INDEX storage option | `PAD_INDEX = ON` ≡ `PAD_INDEX = OFF` |
| | `IgnoreLockOptions` | ALLOW_ROW_LOCKS / ALLOW_PAGE_LOCKS | `ALLOW_PAGE_LOCKS = OFF` ≡ `ON` |
| | `IgnoreDisabled` | An index's `is_disabled` state | A disabled index ≡ the same index enabled |
| `Constraints`<br/>`ConstraintNormalization` | `IgnoreNames` | The constraint name, entirely | `CK_Price_Positive` ≡ `CK_Orders_Price` over the same predicate. Wins over `NormalizeAutoGeneratedNames` |
| | `NormalizeAutoGeneratedNames` | Only *system-generated* constraint names; explicit names still compare exactly | `DF__Orders__Status__1AB2C3D4` ≡ `DF__Orders__Status__5EF67890`. Uses the authoritative `is_system_named` catalog flag, **or** the generated name shape — the shape fallback keeps these normalized across a script-out/redeploy, which flips `is_system_named` to `0` |
| | `IgnoreDisabled` | A FK/CHECK constraint's disabled (NOCHECK) state | `NOCHECK CONSTRAINT FK_Orders_Customer` ≡ the enabled constraint |
| | `IgnoreTrust` | A FK/CHECK constraint's untrusted (`is_not_trusted`) state | A constraint left untrusted by `WITH NOCHECK` after a bulk load ≡ a trusted one — the classic post-load drift |
| | `IgnoreNotForReplication` | A FK/CHECK constraint's NOT FOR REPLICATION flag | `FOREIGN KEY … NOT FOR REPLICATION` ≡ the plain foreign key |
| `Modules`<br/>`ModuleNormalization` | `IgnoreBodyText` | The module body hash — only the name, parameter signatures and structural flags remain | Two procedures with the same signature but different bodies ≡ each other |
| | `IgnoreSetOptions` | The CREATE-time ANSI_NULLS / QUOTED_IDENTIFIER SET options | A procedure created under `SET QUOTED_IDENTIFIER OFF` ≡ the same text under `ON` |

`Modules` covers all four programmable module kinds: stored procedures, views, functions, and triggers. Constraint bits cover PK / UNIQUE / FK / CHECK / DEFAULT.

**What no bit can loosen.** These always participate: column names, data types, length/precision/scale, nullability, computed-column definitions, sparse/rowguid/filestream markers, Always Encrypted and XML typing; index key and included columns, uniqueness, primary-key backing, filter predicates and IGNORE_DUP_KEY; a constraint's type, key columns, CHECK predicate, DEFAULT expression, and foreign-key target and referential actions; module names and parameter signatures. To drop objects from the comparison entirely, use scoping instead — normalization changes *how* an object is hashed, never *whether* it is.

**Scoping** (separate from normalization)

| Option | Default | Description |
|--------|---------|-------------|
| `SchemaFilter` | `null` | Filter to specific schema (e.g., `"dbo"`, `"sales"`). When null, all schemas are included. |
| `ObjectNamesToIgnore` | empty | Object names to exclude from extraction. A bare name matches that name in any schema; a `schema.name` entry matches that schema only. Composes additively with `IgnoreSysDiagramObjects`. |
| `ObjectNameComparer` | `OrdinalIgnoreCase` | The `StringComparer` used to match the ignore list, so case behavior is owned by the library rather than the caller's set. |
| `IgnoreSysDiagramObjects` | `false` | Excludes SSMS database diagram objects (see [Excluded Objects](#excluded-objects)). |
| `IgnoreExtendedProperties` | `false` | Skips extended-property extraction entirely, so `MS_Description` and friends do not affect the hash. No preset sets this. |

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

if (SchemaHashResult.Compare(beforeHash, afterHash) != SchemaHashComparison.Equal)
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

if (SchemaHashResult.Compare(prodHash, stagingHash) != SchemaHashComparison.Equal)
{
    throw new Exception("Staging schema does not match production!");
}
```

## Dependencies

- `Microsoft.Data.SqlClient` - SQL Server connectivity
- `Dapper` - Efficient database queries

## Project Status

Stable and published on [NuGet.org](https://www.nuget.org/packages/zachtbeer.SqlSchemaHasher). See the [CHANGELOG](CHANGELOG.md) for release history.

> **v2.0.0 is currently in preparation and unreleased.** The badges above and the package on NuGet.org reflect the latest *published* release; this document's "What Gets Hashed" section describes the code in this repository, which may be ahead of what's published.

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
dotnet test tests/SqlSchemaHash.UnitTests          # fast, no database required
dotnet test tests/SqlSchemaHash.IntegrationTests   # requires Docker
dotnet test SqlSchemaHasher.slnx                    # runs both
```

Integration tests use [Testcontainers](https://testcontainers.com/) to spin up SQL Server 2025 in Docker, so Docker must be running.

## License

[MIT](LICENSE)
