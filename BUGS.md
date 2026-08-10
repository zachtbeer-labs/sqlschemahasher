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

### Columnstore compression delay not captured

`sys.indexes.compression_delay` (the `COMPRESSION_DELAY` option controlling columnstore rowgroup
compression, in minutes) is not extracted or hashed. It is `NULL` for every rowstore index but `0`
(not `NULL`) for a columnstore index with no explicit delay configured, so it is a real, always-present
columnstore attribute — not a runtime/state value. Two columnstore indexes differing only in their
configured compression delay currently compare as identical.

### Index physical option flags not captured (SUPPRESS_DUP_KEY_MESSAGES, OPTIMIZE_FOR_SEQUENTIAL_KEY)

`sys.indexes.suppress_dup_key_messages` (SQL Server 2017+) and `optimize_for_sequential_key` (SQL Server
2019+, the `OPTIMIZE_FOR_SEQUENTIAL_KEY` last-page-insert option) are not extracted or hashed, unlike the
index's other physical/behavioral options (`fill_factor`, `is_padded`, `allow_row_locks`,
`allow_page_locks`, `ignore_dup_key`) which are. An index differing only in one of these two flags
compares as identical.

### Columnstore explicit column order (ORDER clause) not captured

`sys.index_columns.column_store_order_ordinal` (SQL Server 2022+, populated by
`CREATE ... COLUMNSTORE INDEX ... ORDER (col1, col2)`) is not extracted or hashed. Columnstore index
columns already hash with `key_ordinal` normalized to 0 and are sorted by name for determinism, so an
ordered columnstore index and the same index without the `ORDER` clause (or with a different column
order) currently hash identically.

### Index-level DATA_COMPRESSION not captured

Row/page/columnstore compression (`DATA_COMPRESSION = ROW/PAGE/COLUMNSTORE`, set via
`WITH (DATA_COMPRESSION = ...)` on `CREATE`/`ALTER INDEX`) lives on `sys.partitions.data_compression`,
not `sys.indexes`, joined on `(object_id, index_id[, partition_number])` — a completely unpartitioned
index still has exactly one `sys.partitions` row carrying this setting. Neither `SchemaExtractor` nor
`SchemaHashCalculator` reads `sys.partitions`, so changing an index's compression level does not change
the hash. This is distinct from the existing "Partitioning and filegroup/data-space placement" gap
(repartitioning / moving filegroups): compression applies even to a non-partitioned index, so it is not
covered by that entry.

### PRIMARY KEY / UNIQUE constraint `is_enforced` not captured

`sys.key_constraints.is_enforced` — the flag behind the `NOT ENFORCED` clause on `PRIMARY KEY`/`UNIQUE`
constraint syntax — is not extracted or hashed; `KeyConstraintSchema` has no corresponding field. This is
the PK/UNIQUE analog of the `is_disabled`/`is_not_trusted` enforcement state already captured for FOREIGN
KEY and CHECK constraints. On general-purpose SQL Server (2016+) and Azure SQL DB/MI it is observed to
always read `1` — `NOT ENFORCED` on a PK/UNIQUE constraint is currently Microsoft Fabric Warehouse-only
and is rejected elsewhere (`Msg 40514: 'NOT ENFORCED' is not supported in this version of SQL Server`) —
so the flag cannot actually vary on any platform this library targets today. Left uncaptured for now
since there is no way to observe a `0` on a supported server; worth adding for forward-compatibility if
Fabric Warehouse (or a future on-box release) becomes a target.

### Table-level ANSI_NULLS setting not captured

`sys.tables.uses_ansi_nulls` — the `ANSI_NULLS` session setting a table was created under — is not
extracted or hashed. It is distinct from the `UsesAnsiNulls` already captured for stored procedures,
views, functions, and triggers (sourced from `sys.sql_modules`, which has no row for a table — tables
carry their own catalog-level flag). This setting affects computed-column and CHECK-constraint NULL
comparison semantics, so it is genuinely schema-relevant, not runtime state. Two otherwise-identical
tables created under different `ANSI_NULLS` settings currently compare as identical.

### Table-level LOCK_ESCALATION setting not captured

`sys.tables.lock_escalation`/`lock_escalation_desc` (`TABLE` / `AUTO` / `DISABLE`, set via
`ALTER TABLE ... SET (LOCK_ESCALATION = ...)`) is not extracted or hashed. This is a DDL-settable
behavioral attribute, not runtime state, and changing it currently does not change the hash.

### Always Encrypted column key identity not captured

`sys.columns.column_encryption_key_id` (the FK into `sys.column_encryption_keys` identifying which
Always Encrypted key protects a column) and `encryption_algorithm_name` are not extracted or hashed —
only `encryption_type_desc` (`DETERMINISTIC`/`RANDOMIZED`) is captured on `ColumnSchema`. Resolving
`column_encryption_key_id` to the key's name (the same id-to-name technique already used for
`schema_id`/`object_id` elsewhere) would let the hash detect a column being rekeyed onto a different
CEK; currently, swapping an encrypted column from one CEK to another, or changing its encryption
algorithm, does not change the hash.

**Workaround:** none currently. Detecting an in-place rekey (a new `ENCRYPTED_VALUE` under the *same*
CEK id) is a separate, harder problem — it would require hashing
`sys.column_encryption_key_values.encrypted_value`, a database-scoped key-metadata object outside this
library's stated in-scope object list — and is not part of this gap.

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
