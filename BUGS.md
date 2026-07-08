# Known Bugs / Design Gaps

Tracked issues discovered during the settings-matrix test build (see `TESTPLAN.md`) and an earlier
hash-fidelity audit of `SchemaExtractor`/`SchemaMetadata`/`SchemaHashCalculator`/`SchemaHashOptions`.

## Open / known limitations

### Definition-text rendering drift

CHECK constraint, DEFAULT constraint, computed-column, and filtered-index definitions are hashed as
SQL Server renders them (`sys.check_constraints.definition`, `sys.default_constraints.definition`,
`sys.computed_columns.definition`, `sys.indexes.filter_definition`). SQL Server re-renders these
expressions itself (spacing, parenthesization, casing of built-in functions, numeric literal
formatting), and rendering can differ across major versions/CUs. The same DDL script run on SQL Server
2019 vs. 2022 could therefore produce a spurious `Different` comparison for a computed column, default,
CHECK, or filtered index even though nothing semantically changed.

**Workaround:** compare hashes taken from the same SQL Server major version. This is intentionally not
normalized in v2 — rewriting/canonicalizing expression text risks introducing new determinism bugs, and
no such rewriting is currently implemented.

### Partitioning and filegroup/data-space placement

Table/index partitioning and filegroup or data-space placement are not captured. Moving a table to a
different filegroup, or repartitioning it, does not change the hash.

### Out-of-scope object kinds

Extraction covers tables, stored procedures, user-defined table types, views, T-SQL functions (scalar,
inline TVF, multi-statement TVF), DML triggers, sequences, and synonyms. CLR modules (functions,
triggers, aggregates), scalar CLR user-defined types, and DDL/database/server-scoped triggers are not
read, so adding, altering, or dropping them does not change the hash. See the README's "Not Yet
Captured" section.

### Encrypted modules

Two different `WITH ENCRYPTION` modules (stored procedures, views, functions, or triggers) with
identical signatures collide on the `<encrypted>` sentinel used in place of a definition hash. This is
inherent — the module body is unreadable server-side once encrypted, so there is no signal available
to distinguish them.

### Orphaned ex-history tables

After `ALTER TABLE ... SET (SYSTEM_VERSIONING = OFF)`, an anonymous history table
(`MSSQL_TemporalHistoryFor_<object_id>`) is left behind as an ordinary table — `temporal_type` reverts to
`NON_TEMPORAL_TABLE`, so `SchemaHashCalculator.EffectiveTableName`'s guard no longer fires and the
table's raw, non-deterministic name hashes exactly. At that point it genuinely is an ordinary table
whose physical name is real schema, so this is accepted rather than treated as a bug. **Workaround:**
rename the orphaned table, or add it to `ObjectNamesToIgnore`.

### Extended properties on an anonymous history table

An extended property targeting an anonymous temporal history table resolves `ObjectName` to the table's
raw, non-deterministic `MSSQL_TemporalHistoryFor_<object_id>` name — `EffectiveTableName`'s normalization
applies only to `TableSchema.Name`, not to `ExtendedPropertySchema.ObjectName`. Rare enough (extended
properties are seldom attached to an auto-named history table rather than its versioned parent) not to
warrant the extra parent join in the extended-property query now; revisit if reported.

## Resolved in v2

- **Anonymous temporal history table/index names leaked `object_id`** — a system-versioned table
  created without an explicit `HISTORY_TABLE` gets an auto-named history table
  (`MSSQL_TemporalHistoryFor_<object_id>`) and auto-created clustered index
  (`ix_MSSQL_TemporalHistoryFor_<object_id>`), both embedding a per-database object_id that previously
  hashed raw, so two databases built from identical DDL compared as `Different` under every preset.
  `SchemaHashCalculator.EffectiveTableName`/`HistoryIndexNameRewrite` now substitute a name derived from
  the versioned parent (captured via a new `sys.tables` reverse join exposed as
  `TableSchema.VersionedParentSchema`/`VersionedParentName`), unconditionally — an explicitly-named
  history table is unaffected. See `Fidelity/TemporalHistoryTableFidelityTests` and
  `Determinism/TemporalHistoryTableNormalizationTests`.
- **Object-coverage expansion** — extraction and hashing now cover views (including indexed-view
  indexes), T-SQL functions (scalar, inline TVF, multi-statement TVF, with return type captured even
  under `IgnoreBodyText`), DML triggers (including FIRST/LAST ordering and the excluded-parent-excludes-
  its-triggers scoping rule), sequences (deliberately excluding `current_value`, which is runtime state),
  and synonyms. CLR modules, scalar CLR UDTs, and DDL/server-scoped triggers remain out of scope.
- **Column order** — `column_id` is now captured and drives ordering, so two tables/table types
  differing only in column order (e.g. positionally-marshaled TVP columns) hash differently.
- **Identity seed/increment/NOT FOR REPLICATION** — `sys.identity_columns.seed_value`,
  `increment_value`, and `is_not_for_replication` are now captured and hashed.
- **Constraint disabled/trust state** — `is_disabled`/`is_not_trusted`/`is_not_for_replication` are now
  captured for FOREIGN KEY and CHECK constraints.
- **Constraint names + `IsSystemNamed`** — constraint names are now captured and hashed (with normalization
  options mirroring index name handling), and the authoritative `is_system_named` flag distinguishes a
  user-named constraint from a system-generated one.
- **Encrypted-proc sentinel** — a `WITH ENCRYPTION` procedure's unreadable body is now hashed as a
  distinct `<encrypted>` sentinel instead of collapsing to the hash of an empty definition.
- **Sparse/rowguidcol/masking/XML capture** — `is_sparse`, `is_rowguidcol`, Dynamic Data Masking state,
  and typed-XML schema-collection binding are now captured and hashed.
- **UDT schema-qualification** — a column or parameter typed with a user-defined type now hashes the
  type's schema-qualified name, so e.g. `dbo.IntList` and `staging.IntList` no longer collide.
- **Alias scalar type base type** — an alias scalar type's underlying system base type, length/precision/scale,
  and nullability are now captured for both columns and stored procedure parameters, so recreating an
  alias over a different base type (e.g. `DECIMAL(9,2)` → `BIGINT`) changes the hash.
- **`ObjectNamesToIgnore` matched by bare name, ignoring schema** — matching now also accepts a
  schema-qualified entry (`sales.Things`) to target a single schema; a bare entry (`Things`) remains an
  explicit all-schemas shorthand.
- **`ObjectNamesToIgnore` case-insensitivity was caller-dependent** — added
  `SchemaHashOptions.ObjectNameComparer` (default `StringComparer.OrdinalIgnoreCase`) so the library owns
  case behavior regardless of the comparer on the caller's set.

### Resolved fix details (`ObjectNamesToIgnore`)

#### `ObjectNamesToIgnore` matched by bare name, ignoring schema

**Was:** the extractor filtered with `objectNamesToIgnore.Contains(bareName)`, so an entry like
`"Things"` dropped the object from **every** schema (`dbo.Things` *and* `sales.Things`), with no way to
target one schema.

**Fix:** matching now accepts a schema-qualified entry (`sales.Things`, matches only that schema) or a
bare entry (`Things`, an explicit all-schemas shorthand). An object is excluded when a configured entry
equals its bare name or its `schema.name`. See `SchemaExtractor.IsIgnored`.

**Tests:** `Settings/ScopingTests.ObjectNamesToIgnore_SchemaQualified_TargetsSingleSchema` (precise
targeting) and `_BareName_MatchesAcrossSchemas` (the documented shorthand).

#### `ObjectNamesToIgnore` case-insensitivity was caller-dependent

**Was:** matching used the comparer of whatever `IReadOnlySet<string>` the caller assigned, so the
documented "case-insensitive" behavior silently broke if a caller passed a plain (ordinal) `HashSet`.

**Fix:** added `SchemaHashOptions.ObjectNameComparer` (default `StringComparer.OrdinalIgnoreCase`). The
extractor rebuilds the match set with this comparer, so the library owns case behavior regardless of the
assigned set. Callers set `ObjectNameComparer = StringComparer.Ordinal` for case-sensitive matching.

**Tests:** `Settings/ScopingTests.ObjectNamesToIgnore_IsCaseInsensitiveByDefault_RegardlessOfSetComparer`
and `_CaseSensitive_WhenComparerIsOrdinal`.
