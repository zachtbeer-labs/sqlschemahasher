# Tables

Research performed against a live SQL Server 2025 (17.0.1000.7) instance. All objects were created in a dedicated `audit_tables` schema in the `audit_db` database. Findings were cross-checked against Microsoft Learn (`sys.tables`, `sys.columns`, `sys.computed_columns`, `sys.identity_columns`, `sys.masked_columns`, `sys.periods`, `sys.identity_columns`, Always Encrypted, dynamic data masking, and temporal-table docs).

## Catalog views that expose "Tables"

| View | Role |
|---|---|
| `sys.tables` | Primary/aggregate-root view. One row per user table (derived from `sys.objects`, `type = 'U'`). Carries table-level physical/versioning/durability attributes. |
| `sys.columns` | One row per column (base data type, nullability, collation, identity/computed/masked/encrypted flags). `sys.computed_columns`, `sys.identity_columns`, `sys.masked_columns` all *inherit* this view's columns and add domain-specific ones — they are filtered/joined projections, not independent metadata. |
| `sys.computed_columns` | Subset of `sys.columns` where `is_computed = 1`; adds `definition`, `is_persisted`, `uses_database_collation`. |
| `sys.identity_columns` | Subset of `sys.columns` where `is_identity = 1`; adds `seed_value`, `increment_value`, `last_value`, `is_not_for_replication`. |
| `sys.masked_columns` | Subset of `sys.columns` where `is_masked = 1`; adds `masking_function`. |
| `sys.periods` | One row per `PERIOD FOR SYSTEM_TIME` definition on a temporal table; the authoritative source for which two columns form the period (not just the `generated_always_type` flags on `sys.columns`). |
| `sys.column_master_keys` / `sys.column_encryption_keys` / `sys.column_encryption_key_values` | Database-scoped key **metadata** (not per-table) that `sys.columns.column_encryption_key_id` links to, for Always Encrypted columns. |

`sys.tables` is a *derived* catalog view over `sys.objects` (`type = 'U'`) — it returns the tables-specific columns plus everything `sys.objects` returns (name, object_id, schema_id, create_date, modify_date, is_ms_shipped, …). Microsoft explicitly warns against `SELECT *` on catalog views because new columns get appended in future releases (confirmed: this SQL Server 2025 build already carries several post-2016 additions — see version-gating table below).

## `sys.tables` fields relevant to schema-change detection

Verified by running `SELECT TOP 0 * FROM sys.tables` for the full column list on this build, then querying the populated values for the test objects.

| Column | Meaning | Notes |
|---|---|---|
| `name`, `schema_id` | Table identity | `schema_id` is a per-database internal id — resolve to schema *name*, don't hash the id. |
| `type_desc` | Always `USER_TABLE` for rows in `sys.tables` | Not schema-discriminating by itself (the view is already filtered to `type='U'`). |
| `principal_id` | Non-null only when the table has an owner different from its schema's owner | Schema-relevant if you support per-object ownership overrides. |
| `lob_data_space_id`, `filestream_data_space_id` | Filegroup placement for LOB/FILESTREAM data | Physical placement, arguably out of scope for a *logical* schema hash unless FILESTREAM columns are in scope. |
| `max_column_id_used` | Highest column_id ever assigned (survives dropped columns) | Not schema content by itself, but a proxy for "has a column ever been dropped." |
| `uses_ansi_nulls` | Whether the table was created under `ANSI_NULLS ON` | Affects computed-column/view/index-on-computed-column semantics; genuinely schema-relevant. |
| `lock_escalation` / `lock_escalation_desc` | `TABLE` / `AUTO` / `DISABLE` | Schema-relevant performance/behavior setting (`ALTER TABLE ... SET (LOCK_ESCALATION = ...)`). |
| `is_filetable` | FileTable flag | SQL Server 2012+. |
| `is_memory_optimized` | In-memory (Hekaton) table | SQL Server 2014+. See Memory-optimized section. |
| `durability` / `durability_desc` | `SCHEMA_AND_DATA` (0) vs `SCHEMA_ONLY` (1) | **Only meaningful when `is_memory_optimized = 1`.** Confirmed: disk-based tables always report `durability = 0` / `SCHEMA_AND_DATA` even though the concept doesn't apply to them — don't treat this as a real distinction outside the memory-optimized case. |
| `temporal_type` / `temporal_type_desc` | `0 NON_TEMPORAL_TABLE`, `1 HISTORY_TABLE`, `2 SYSTEM_VERSIONED_TEMPORAL_TABLE` | SQL Server 2016+. |
| `history_table_id` | `object_id` of the history table, when `temporal_type = 2` (also used for ledger's `ledger_type = 2`) | One-directional (parent → history) FK by object_id; walk it in-memory after extraction to resolve the history table's *name*, since raw object_id isn't deterministic across databases. |
| `history_retention_period` / `_unit` / `_unit_desc` | Numeric retention + unit (`INFINITE`, `DAY`, `MONTH`, …) | SQL Server 2017+ (not 2016, despite `temporal_type` itself being 2016). `-1`/`INFINITE` is the default when no `HISTORY_RETENTION_PERIOD` clause is given — confirmed by experiment. |
| `is_remote_data_archive_enabled` | Stretch Database flag | SQL Server 2016+ (deprecated feature going forward). |
| `is_external` | External table flag (PolyBase) | SQL Server 2016+. |
| `is_node` / `is_edge` | SQL Graph node/edge table | SQL Server 2017+. Out of scope for this library per its stated in-scope list, but present on this build. |
| `ledger_type` / `ledger_type_desc` / `ledger_view_id` / `is_dropped_ledger_table` | Ledger table kind (`NON_LEDGER_TABLE` / `HISTORY_TABLE` / `UPDATABLE_LEDGER_TABLE` / `APPEND_ONLY_LEDGER_TABLE`) and its generated ledger view | SQL Server 2022+. Not exercised here (out of the requested edge-case set) but present and version-gated on this build; a future audit of ledger tables should treat this the same way temporal tables are treated (linkage + generated-object naming). |
| `data_retention_period` / `_unit` / `_unit_desc` | Row-level data retention | **Azure SQL Edge only** — confirmed present as a column on this SQL Server 2025 build's `sys.tables` (all `NULL` here) but Microsoft's docs scope it strictly to Azure SQL Edge, not mainstream SQL Server. Don't rely on it being populated. |
| `create_date`, `modify_date` | Timestamps | **Runtime/provenance state, not logical schema — exclude.** Two databases with byte-identical schemas created at different times, or restored from different backups, will have different values here despite being schema-equal. |
| `is_ms_shipped` | System-shipped object flag | Always `0` for user tables reachable via `sys.tables`; not discriminating within this view. |

## Column-level fields (`sys.columns` and friends)

| Column | Meaning | Notes |
|---|---|---|
| `column_id`, `name` | Identity/ordering | `column_id` is stable per-table but **not reused** after a column drop (`max_column_id_used` on `sys.tables` reflects this) — don't treat `column_id` gaps as meaningful on their own, but ordinal position does matter for `SELECT *`/`sp_help` type reproducibility. |
| `system_type_id` / `user_type_id` → `TYPE_NAME()` | Base data type | |
| `max_length` | Storage size in **bytes** | For `nvarchar(n)`, this is `2n` (confirmed: `nvarchar(50)` → `max_length = 100`), not the declared character count. `-1` for `MAX` types. |
| `precision`, `scale` | Numeric precision | |
| `collation_name` | `NULL` for non-character types (confirmed for `int`/`decimal`/`uniqueidentifier`/`datetime2`) | Only meaningful for char/text types. |
| `is_nullable` | Nullability | **Do not assume from context — always read from the catalog.** See computed-column edge case below; it can surprise you. |
| `is_identity` | Identity column flag | Join to `sys.identity_columns` for seed/increment. |
| `is_computed` | Computed column flag | Join to `sys.computed_columns` for definition/persistence. |
| `is_masked` | Dynamic-data-masking flag | Join to `sys.masked_columns` for the masking function text. |
| `encryption_type` / `encryption_type_desc` | Always Encrypted: `1 DETERMINISTIC`, `2 RANDOMIZED`, `NULL` = not encrypted | SQL Server 2016+. |
| `encryption_algorithm_name` | Always `AEAD_AES_256_CBC_HMAC_SHA_256` for standard Always Encrypted | |
| `column_encryption_key_id` | FK into `sys.column_encryption_keys` (database-scoped, by id not name) | To detect a genuine rekey/CEK swap you'd need to resolve this id to the CEK's *name* (ids aren't stable/portable across databases), analogous to how `schema_id`/`object_id` are resolved elsewhere. |
| `column_encryption_key_database_name` | Non-null only for cross-database CEKs (Managed Instance scenario) | SQL Server 2016+; `NULL` in the common same-database case, as observed here. |
| `is_hidden` | Column excluded from `SELECT *` | SQL Server 2016+. Used for temporal period columns declared `HIDDEN`; **does not propagate to the history table** — see edge case below. |
| `generated_always_type` / `_desc` | `0 NOT_APPLICABLE`, `1 AS_ROW_START`, `2 AS_ROW_END` (and, per docs, `7–10` for ledger transaction-id/sequence-number columns) | SQL Server 2016+ for temporal; ledger values are SQL Server 2022+. |
| `graph_type` / `_desc` | SQL Graph internal columns (`GRAPH_ID`, `GRAPH_FROM_ID`, …) | Out of scope for this library; present on this build (2017+ concept). |
| `is_sparse`, `is_column_set`, `is_filestream`, `is_rowguidcol`, `is_ansi_padded` | Physical/storage column properties | Schema-relevant when in scope; not exercised here beyond confirming they're columns on the view. |

## Identity columns (`sys.identity_columns`)

Confirmed columns: `seed_value`, `increment_value`, `last_value`, `is_not_for_replication` (plus everything inherited from `sys.columns`).

- `seed_value`/`increment_value` are `sql_variant`, typed the same as the identity column itself.
- **`last_value` is runtime state — exclude it.** Confirmed empirically: with zero rows inserted into any test table, `last_value` is `NULL` for every identity column, even the custom-seeded one (`Employees.EmployeeId`, seed `1000`). It reflects data, not structure, and changes with every insert/delete-and-reseed — including it in a schema hash would make the hash non-deterministic across otherwise-identical databases with different data.
- `seed_value`/`increment_value`/`is_not_for_replication` are genuine schema properties and were captured correctly (`Employees.EmployeeId` → seed `1000`, increment `1`; all others → seed `1`, increment `1`).
- Only one identity column is allowed per table (documented restriction), so no multiplicity concerns.

## Computed columns (`sys.computed_columns`)

Confirmed columns on this build: `object_id`, `name`, `column_id`, type/collation fields (inherited from `sys.columns`), `definition`, `uses_database_collation`, `is_persisted`, `is_computed`, plus encryption/masking/hidden/graph columns inherited from `sys.columns`, and `is_index_column_expression`. Note: `is_deterministic`/`is_precise` (documented in some older references) are **not** present on this SQL Server 2025 build's `sys.computed_columns` — don't assume they exist without checking the target server.

Test objects: `Employees.FullNameCalc AS (FirstName + ' ' + LastName)` (not persisted) and `Employees.AnnualSalary AS (Salary * 12) PERSISTED`.

- `definition` returns the expression with the engine's own bracketing/normalization applied, not the verbatim source text: `(([FirstName]+' ')+[LastName])` and `([Salary]*(12))`. Comparing `definition` text directly (rather than re-parsing) is safe *because* the engine already normalizes it — two logically-identical `CREATE TABLE` statements typed differently will still produce identical `definition` strings.
- `is_persisted`: `0` for `FullNameCalc`, `1` for `AnnualSalary`, as expected.
- **Edge case — computed-column nullability is not reliably inferable from the expression, must be read from the catalog:** `FullNameCalc` (non-persisted, string concatenation of two `NOT NULL` columns) came back `is_nullable = 0` as you'd expect. But `AnnualSalary` (`PERSISTED`, `Salary * 12` where `Salary` is `NOT NULL DECIMAL(10,2)`) came back `is_nullable = 1`. This matches the documented rule that `NOT NULL` can only be specified for a computed column when it is also `PERSISTED`, and — critically — is not automatically inferred even when persisted and deterministic; you must declare it explicitly (`AnnualSalary AS (Salary*12) PERSISTED NOT NULL` would flip it). **A schema hasher must always read `is_nullable` from `sys.columns`/`sys.computed_columns` for computed columns rather than deriving it from the dependent columns' nullability or from `is_persisted`.**
- A masking rule cannot be applied to a computed column directly (documented limitation), though a computed column that *references* a masked column inherits masked output at query time — that's a runtime/query-shaping behavior, not a stored catalog flag, so it doesn't affect schema-hash scope.

## Temporal system-versioning and history-table linkage

Built three variants: `Product`/`PriceHistory` (explicit history-table name), `Vendor`/`<anonymous>` (system-generated history-table name), and `Orders`/`OrderHistory` (explicit name + `HIDDEN` period columns + explicit `HISTORY_RETENTION_PERIOD = 6 MONTHS`).

- `sys.tables.temporal_type_desc` on `Vendor` = `SYSTEM_VERSIONED_TEMPORAL_TABLE`, `history_table_id` → an auto-created table named **`MSSQL_TemporalHistoryFor_1749581271`**, where `1749581271` is `Vendor`'s **own** `object_id` (the versioned parent's id), not the history table's own id. Confirmed by direct comparison: `Vendor.object_id = 1749581271`.
- The `history_table_id` linkage is one-directional (parent → history) in the catalog. The reverse lookup (history table → its versioned parent) requires a self-join: `SELECT h.name, p.name FROM sys.tables h JOIN sys.tables p ON p.history_table_id = h.object_id` — confirmed this correctly resolves `OrderHistory → Orders`, `PriceHistory → Product`, and the anonymous `MSSQL_TemporalHistoryFor_1749581271 → Vendor`.
- `sys.periods` is the authoritative source for *which two columns* form the system-time period (`start_column_id`, `end_column_id`) — don't rely solely on scanning `generated_always_type` across all columns, since `sys.periods` gives you the pairing directly and is scoped correctly even if column ordering changes.
- **Edge case — a history table's period columns lose all "generated"/"hidden" markers from the parent.** On `Orders`, `ValidFrom`/`ValidTo` are `GENERATED ALWAYS AS ROW START/END HIDDEN`, so `sys.columns` shows `generated_always_type_desc = AS_ROW_START/AS_ROW_END` and `is_hidden = 1`. The corresponding columns on `OrderHistory` (the history table) are **ordinary columns**: `generated_always_type_desc = NOT_APPLICABLE` and `is_hidden = 0` for both. A byte-for-byte "same shape" comparison of parent vs. history table columns will not line up on these two flags even though the column names/types match — this is expected engine behavior (history tables are plain tables with no period definition of their own; `sys.periods` has no row for them), not a bug, but a schema-hasher needs to know not to expect flag parity here.
- `history_retention_period_unit_desc` defaults to `INFINITE` (`-1`/`-1`) when `HISTORY_RETENTION_PERIOD` isn't specified in `WITH (SYSTEM_VERSIONING = ON (...))` — confirmed on `Product` and `Vendor`. `Orders`, created with `HISTORY_RETENTION_PERIOD = 6 MONTHS`, correctly reports `history_retention_period = 6`, `history_retention_period_unit_desc = MONTH`.
- An explicitly-named history table (`Product`/`PriceHistory`) behaves identically in the catalog to an anonymous one except for the name itself — same `temporal_type = 1 (HISTORY_TABLE)`, same reverse-linkage behavior via `history_table_id`.
- Practical DDL gotcha (not a catalog fact, but relevant if scripting temporal tables for testing): `CREATE TABLE` with a computed column or `PERIOD FOR SYSTEM_TIME` fails with error 1934 unless the session has `SET QUOTED_IDENTIFIER ON` (and ideally `ANSI_NULLS ON`) — `sqlcmd`'s default session settings are not guaranteed to have this on.

## Memory-optimized tables

Built `SessionCache` (`DURABILITY = SCHEMA_AND_DATA`, with a `HASH` index) and `ScratchCache` (`DURABILITY = SCHEMA_ONLY`).

- Requires SQL Server 2014+ for `is_memory_optimized`/`durability`/`durability_desc` to exist at all, and requires a `MEMORY_OPTIMIZED_DATA` filegroup to exist in the database before `CREATE TABLE ... WITH (MEMORY_OPTIMIZED = ON)` succeeds (`ALTER DATABASE ... ADD FILEGROUP ... CONTAINS MEMORY_OPTIMIZED_DATA` + `ADD FILE`) — this database had none by default and needed one added.
- Confirmed: `is_memory_optimized = 1` and `durability_desc` correctly differentiates `SCHEMA_AND_DATA` vs `SCHEMA_ONLY` between the two tables.
- Every disk-based table in this test also reported `durability_desc = SCHEMA_AND_DATA` (with `is_memory_optimized = 0`) — confirming this field is meaningless noise outside the memory-optimized case and must be gated on `is_memory_optimized = 1` before being treated as a real schema distinction.
- A memory-optimized table can be combined with system-versioning (not tested here), but per Microsoft docs this requires `DURABILITY = SCHEMA_AND_DATA` (schema-only memory-optimized tables cannot be system-versioned), and the disk-based history table is mandatory even though the "current" table lives in memory — worth knowing if this library ever extends into that combination, since it introduces an internal `Memory_Optimized_History_Table_<object_id>` staging table (visible only via `sys.internal_tables`, not `sys.tables`) as an additional non-schema-hash-relevant implementation detail.

## Masked columns (`sys.masked_columns`)

Built `Employees.SSN` (`MASKED WITH (FUNCTION = 'partial(0,"XXXXX",4)')`) and `Employees.Email` (`MASKED WITH (FUNCTION = 'email()')`).

- `sys.masked_columns` is a pre-filtered view (`is_masked = 1` only) that inherits every `sys.columns` column and adds `is_masked`/`masking_function`. Confirmed `masking_function` returns the function call verbatim as entered: `partial(0, "XXXXX", 4)` and `email()`.
- SQL Server 2016 SP1+ / Azure SQL required (`sys.masked_columns` view itself and `is_masked`/`masking_function` on `sys.columns`).
- **Documented mutual-exclusion edge cases** (not independently re-tested, but load-bearing for a coverage audit): a masking rule cannot be defined on an Always Encrypted column, a FILESTREAM column, a `COLUMN_SET`/sparse-column-in-a-set, or a computed column; and a masked column can't be the key of a full-text index. These are enforced at DDL time, so a schema hasher never needs to reconcile "both masked and encrypted" states on the same column — the server won't allow it.

## Encrypted columns (Always Encrypted)

Built `SecureCustomer.CardNumber` (`DETERMINISTIC`) and `SecureCustomer.Notes` (`RANDOMIZED`), backed by a `CREATE COLUMN MASTER KEY` (`MSSQL_CERTIFICATE_STORE` provider) and `CREATE COLUMN ENCRYPTION KEY` (`RSA_OAEP`) — metadata objects only; no client-side key material was needed to populate the catalog.

- SQL Server 2016+ required for `sys.columns.encryption_type`/`encryption_type_desc`/`encryption_algorithm_name`/`column_encryption_key_id`.
- Confirmed values: `CardNumber → encryption_type_desc = DETERMINISTIC`, `Notes → RANDOMIZED`, both `encryption_algorithm_name = AEAD_AES_256_CBC_HMAC_SHA_256`, both `column_encryption_key_id = 1` (both columns share one CEK, which is a supported/common pattern — one CEK can protect many columns).
- Encrypted columns require an explicit `COLLATE <bin2-collation>` for character types (used `Latin1_General_BIN2` here) — this is itself a schema-relevant, catalog-visible fact (`sys.columns.collation_name`) distinct from the encryption flags themselves; don't conflate "changed collation" with "changed encryption" when diffing.
- The CMK/CEK **metadata objects** (`sys.column_master_keys`, `sys.column_encryption_keys`, `sys.column_encryption_key_values`) are database-scoped, not per-table, and carry their own identity (`column_master_key_id`, `column_encryption_key_id`) plus `key_store_provider_name`/`key_path`/`encryption_algorithm_name` (for wrapping the CEK) and `create_date`. A rekey (new `ENCRYPTED_VALUE` for the same CEK name) or a CMK path change would be invisible if a schema hasher only inspects `sys.columns.column_encryption_key_id` (the id is stable across a rekey) — capturing key-rotation changes would require also hashing `sys.column_encryption_key_values.encrypted_value` and `sys.column_master_keys.key_path`, which are currently outside this library's stated in-scope object list (tables/columns), so this is worth flagging as an explicit non-goal rather than an oversight if left uncovered.

## Fields to explicitly exclude as runtime state

| Field | Why it's runtime state |
|---|---|
| `sys.identity_columns.last_value` | Data-dependent counter; `NULL` with zero rows, changes on every insert; confirmed empirically. |
| `sys.tables.create_date` / `modify_date` | Provenance timestamps, not schema content; identical schemas created/restored at different times will differ here. |
| Any `object_id`/`schema_id`/`principal_id`/`column_encryption_key_id` value used as a **raw number** | Internal, per-database identifiers — must be resolved to names (or, for the anonymous history-table/staging-table naming *pattern*, deliberately reinterpreted) before hashing; the raw integer is not portable/deterministic across databases even for logically identical schemas. |

## Version-gating summary (confirmed via Microsoft Learn `sys.tables`/`sys.columns` docs, cross-checked against catalog output on this 2025 instance)

| Feature / column set | Minimum version |
|---|---|
| `is_filetable` | SQL Server 2012 |
| `is_memory_optimized`, `durability`, `durability_desc` (`sys.tables`) | SQL Server 2014 |
| `temporal_type`, `history_table_id`, `is_remote_data_archive_enabled`, `is_external` (`sys.tables`); `generated_always_type`, `is_hidden`, `is_masked`, `encryption_type`, `column_encryption_key_id` (`sys.columns`); `sys.masked_columns`, `sys.periods` | SQL Server 2016 |
| `history_retention_period`, `history_retention_period_unit(_desc)` (`sys.tables`) | SQL Server 2017 |
| `is_node`, `is_edge` (`sys.tables`); SQL Graph `graph_type`/`graph_type_desc` (`sys.columns`) | SQL Server 2017 |
| `ledger_type`, `ledger_type_desc`, `ledger_view_id`, `is_dropped_ledger_table` (`sys.tables`); ledger-specific `generated_always_type` values 7–10 (`sys.columns`) | SQL Server 2022 |
| `data_retention_period`, `data_retention_period_unit(_desc)` (`sys.tables`) | **Azure SQL Edge only** — present as columns on this SQL Server 2025 build's `sys.tables` (all `NULL` here) but Microsoft's docs scope it strictly to Azure SQL Edge, not mainstream SQL Server. |
| Regex-based dynamic data masking (`REGEXP_REPLACE` function) | Azure SQL Database (preview) only, not exercised here. |

This library's stated minimum (SQL Server 2016) lines up with the version floor needed for every field actually required to cover identity/computed/temporal/masked/encrypted columns; the only fields observed here that need something newer are `history_retention_period` (2017, additive/optional) and the out-of-scope graph/ledger columns (2017/2022).

## Coverage audit

- **Table-level `ANSI_NULLS` setting (`sys.tables.uses_ansi_nulls`) is not extracted or hashed**: this is a distinct catalog field from the `UsesAnsiNulls` already captured for stored procedures, views, functions, and triggers (which comes from `sys.sql_modules`, a view tables have no row in). It affects computed-column and CHECK-constraint NULL-comparison semantics and is genuinely schema-relevant, not runtime state; two otherwise-identical tables created under different `ANSI_NULLS` settings currently hash the same.
- **Table-level `LOCK_ESCALATION` setting (`sys.tables.lock_escalation`/`lock_escalation_desc`) is not extracted or hashed**: a DDL-settable behavioral attribute (`ALTER TABLE ... SET (LOCK_ESCALATION = ...)`), not runtime state, with no corresponding field anywhere in `TableSchema` or `SchemaHashCalculator`.
- **Always Encrypted column key identity is not resolved or hashed**: `sys.columns.column_encryption_key_id` and `encryption_algorithm_name` are not extracted (only `encryption_type_desc` is captured on `ColumnSchema`). Resolving the id to the encryption key's name — the same id-to-name technique already applied to `schema_id`/`object_id` elsewhere in the extractor — would let the hash detect a column being switched to a different Always Encrypted key; today that change is invisible to the hash.
