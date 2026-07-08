# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/), and this project adheres to [Semantic Versioning](https://semver.org/).

## [2.0.0] - Unreleased

Hashes produced by 2.0.0 are **not comparable** to hashes from 1.x: extraction fidelity fixes and new hash inputs change the output for most schemas. Re-baseline stored hashes after upgrading.

### Breaking

- `SchemaExtractor` and `SchemaHashCalculator` are now `internal`. The public API is the `SqlSchemaHash` facade, `SchemaHashOptions`, `SchemaHashResult`/`SchemaHashComparison`, the five normalization enums, and the `SchemaMetadata` record family
- `SqlSchemaHash.ExtractSchemaAsync(string, SchemaHashOptions)`'s `options` parameter is no longer nullable, for consistency with `GetHashAsync`'s two-argument overload; pass `SchemaHashOptions.Default` explicitly or use the one-argument overload

### Added

- Extraction and hashing now cover views, T-SQL functions (scalar, inline TVF, multi-statement TVF), DML triggers, sequences, and synonyms — the last major object-coverage gap for a "schema hasher". Hashes change only for databases that contain one or more of these object kinds; a table/proc/UDT-only schema hashes byte-identically to before this change
  - **Views** (`ViewSchema`): a hash of the view body, CREATE-time SET options, and — for a schemabound indexed view — its indexes (reusing the existing index normalization). Deliberately no columns (the definition hash is the view's identity; `SELECT *` column metadata goes stale until `sp_refreshview`)
  - **Functions** (`FunctionSchema`): the raw `sys.objects.type_desc`, unconditionally hashed so a scalar/inline-TVF/multi-statement-TVF shape change is detected even under `IgnoreBodyText`; parameters (a scalar function's return type arrives as its own `parameter_id = 0` row); a hash of the function body
  - **DML triggers** (`TriggerSchema`/`TriggerEventSchema`): parent table/view, disabled/`INSTEAD OF`/`NOT FOR REPLICATION` flags, the DML event set together with `FIRST`/`LAST` ordering (set out-of-band via `sp_settriggerorder`, so it lives only in the catalog); excluding a table's `ObjectNamesToIgnore` entry takes its triggers with it
  - **Sequences** (`SequenceSchema`): data type (including alias-scalar-type enrichment), start value, increment, min/max bounds, cycling, and cache size — deliberately excludes `current_value`, so a hash is stable across `NEXT VALUE FOR` calls
  - **Synonyms** (`SynonymSchema`): the target object name exactly as the catalog stores it, unvalidated
  - `IgnoreSysDiagramObjects` now actually excludes `fn_diagramobjects`, which was previously dead weight since functions weren't extracted
  - Encrypted-module `<encrypted>` sentinel handling (previously stored-procedure-only) now applies to views, functions, and triggers as well
- Extended properties (`ExtendedPropertySchema`, mirroring `sys.extended_properties`, e.g. `MS_Description`) are now extracted and hashed: property name, value, and the value's `sql_variant` base type, for properties scoped to the database, a schema, an in-scope object, a column, a parameter, an index, or a user-defined table type. Targets are resolved to names (never catalog ids); a property follows its owner out of the hash when the owner is excluded via `ObjectNamesToIgnore` (a table's exclusion also drops its triggers' properties) or `SchemaFilter` (which also drops database-scoped properties — they belong to no schema). Properties on constraint objects are deliberately not captured (system-generated constraint names are not deterministic across databases). New `IgnoreExtendedProperties` scoping option excludes them entirely (no preset sets it). Hashes change only for databases that actually carry extended properties; the golden reference schema gained representative properties and the anchor hashes were re-pinned accordingly
  - Golden re-pin: the `RegressionAnchorTests` reference schema now includes representative instances of all five new object kinds (an ordinary view, a schemabound indexed view, a scalar function with an alias-typed parameter, an inline TVF, a multi-statement TVF, a disabled DML trigger, a sequence with a non-default start/increment, and a synonym); the Strict/V1/V2/Structural golden hashes were re-pinned to match. The DB-free unit `Determinism/DeterminismTests` wire-vector golden did **not** move, confirming the new sections add zero bytes for the empty collections that hand-built metadata carries
- `CancellationToken` support on every public async method on `SqlSchemaHash` (`GetHashAsync`, `ExtractSchemaAsync`), threaded through extraction's connection open and all queries
- Argument validation on the `SqlSchemaHash` facade: null/empty/whitespace `connectionString` throws `ArgumentNullException`/`ArgumentException`; null `options` or `schema` throws `ArgumentNullException` (previously a raw `NullReferenceException` for a null schema)
- Alias scalar types (`CREATE TYPE dbo.OrderTotal FROM DECIMAL(9,2) NOT NULL`) now capture their underlying base type, length/precision/scale, and nullability for both columns and stored procedure parameters, so recreating the alias with a different base type changes the hash even though the alias name is unchanged
- Memory-optimized table test coverage: `TableSchema.IsMemoryOptimized`/`DurabilityDesc` are pinned by dedicated fidelity tests (memory-optimized vs. disk-based, `SCHEMA_AND_DATA` vs. `SCHEMA_ONLY`, and determinism across databases)
- New `tests/SqlSchemaHash.UnitTests` project: fast, DB-free tests (envelope parsing, facade argument validation, hash determinism/culture-independence, option sanity checks) runnable without Docker
- Presets on `SchemaHashOptions`: `V1` (v1 comparison semantics — sort order not compared), `V2` (recommended, now what `Default` returns), and `Structural` (structure-only comparison ignoring index names, clustering type, and key sort order)
- Index key sort order (ASC/DESC) is now extracted and hashed; new `IgnoreIndexSortOrder` option to exclude it
- New `IgnoreIndexNames` option to exclude index names from the hash entirely (takes precedence over `NormalizeAutoGeneratedIndexNames`)
- New `IgnoreSysDiagramObjects` option replacing the hardcoded exclusion list in `Default`; composes additively with `ObjectNamesToIgnore`
- Index INCLUDE columns are extracted separately from key columns and hashed as an unordered set
- Foreign key constraints now capture the referenced table and column; default constraints capture their target column
- Foreign key referential actions (`ON DELETE` / `ON UPDATE`, from `sys.foreign_keys`) are now captured and hashed, so changing e.g. `NO ACTION` to `CASCADE` changes the hash
- Column collation (`sys.columns.collation_name`) is now captured and hashed for table and user-defined table type columns, so a collation change (e.g. case-insensitive to case-sensitive) changes the hash; non-string columns are unaffected
- Stored procedure parameter direction (`OUTPUT`) and the `READONLY` flag are now captured and hashed, so a signature change is detected even with `IncludeStoredProcedureText = false`
- Computed column definitions are now extracted and hashed — the formula and whether it is `PERSISTED` (from `sys.computed_columns`), so a formula change no longer hashes identically to the original. Applies to table columns and user-defined table type columns
- Filtered index predicates (the `WHERE` clause, from `sys.indexes.filter_definition`) are now extracted and hashed
- Package icon, `global.json` (pins the SDK band used for the multi-targeted build), and `nuget.config` (nuget.org as the sole package source, with package-source mapping) added to round out the supply-chain/packaging posture

### Changed

- `NormalizeAutoGeneratedIndexNames` now replaces detected auto-generated names with a fixed sentinel instead of stripping the suffix, and additionally detects SQL Server system-generated names (`PK__Table__3214EC07A1B2C3D4` style) alongside GUID-suffixed names
- `SchemaHashOptions.Default` is now an alias for `V2`; diagram object exclusion is driven by `IgnoreSysDiagramObjects` instead of pre-populated `ObjectNamesToIgnore` (replacing `ObjectNamesToIgnore` no longer silently drops the diagram exclusions)
- Indexes are sorted by their effective (post-normalization) values before hashing, so databases that are identical under a normalization option hash identically regardless of raw name/direction ordering

### Fixed

- All sorting now uses ordinal string comparison — hashes no longer vary with the machine's culture or ICU version
- Integer and string-length values are now serialized little-endian explicitly (previously via `BitConverter`, whose byte order is host-dependent), so a schema hashes identically on big- and little-endian hosts
- `ObjectNamesToIgnore` now also excludes user-defined table types, consistent with how it already filtered tables and stored procedures
- Identity column detection works for table names containing dots (now joins `sys.identity_columns` by object id instead of resolving names through `OBJECT_ID`)
- Stored procedure body hashing uses `HASHBYTES` on Azure SQL Database, which reports major version 12 but supports unlimited input (previously fell back to `CHECKSUM`)
- `NormalizeAutoGeneratedIndexNames` no longer misclassifies ordinary index names whose final underscore-segment is a short 8-character hex run (e.g. `IX_Audit_Record_CAFEBABE`); the GUID-suffix heuristic now requires a 16+ character hex suffix, while SQL Server system names (`PK__`/`DF__` style) continue to match regardless of length

### Docs

- A documentation site now ships on GitHub Pages (`https://zachtbeer-labs.github.io/sqlschemahasher/`), built with Docusaurus from the sources under `website/` and deployed by the `docs.yml` workflow. The FAQ moved off the GitHub wiki into the site; `PackageProjectUrl` and the README now point at it

## [1.0.0] - 2025-02-05

### Added

- Deterministic SHA256 hashing of SQL Server database schemas
- Schema extraction for tables, stored procedures, and user-defined table types
- Column metadata: name, type, precision, scale, max length, nullability, identity
- Index and constraint extraction with full type descriptions
- Stored procedure body hashing via server-side `HASHBYTES` (no text transfer)
- `SchemaFilter` option to hash only specific schemas (e.g., `dbo`)
- `NormalizeAutoGeneratedIndexNames` option to strip GUID suffixes from index names
- `NormalizeClusteringType` option to treat clustered/nonclustered as equivalent
- `IncludeStoredProcedureText` option to include/exclude procedure body from hash
- Configurable object exclusion list (`ObjectNamesToIgnore`) with opinionated defaults
- Base64-encoded hash output for the public API
- Static `SqlSchemaHash` facade with `GetHashAsync`, `ExtractSchemaAsync`, and `ComputeHash`

[2.0.0]: https://github.com/zachtbeer-labs/sqlschemahasher/compare/v1.0.0...v2.0.0
[1.0.0]: https://github.com/zachtbeer-labs/sqlschemahasher/releases/tag/v1.0.0
