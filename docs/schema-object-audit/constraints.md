# Constraints

Scope: PRIMARY KEY, UNIQUE, FOREIGN KEY, CHECK, and DEFAULT constraints on user tables. (Table/column *identity*, computed columns, and index-level metadata are separate object types — this note covers the four constraint catalog views plus `sys.key_constraints`' index linkage only as needed to interpret them.)

All experimentation below was run against a live **SQL Server 2025 (RTM) 17.0.1000.7, Enterprise Developer Edition on Linux**, in a schema `audit_constraints` created for this audit, cross-checked against Microsoft Learn (`?view=sql-server-ver17`).

## Catalog views

| Constraint type | Primary view | Column-list view |
|---|---|---|
| PRIMARY KEY / UNIQUE | `sys.key_constraints` (`type` = `PK`/`UQ`) | via `unique_index_id` → `sys.index_columns` (+ `sys.indexes` for clustering/name) |
| FOREIGN KEY | `sys.foreign_keys` (`type` = `F`) | `sys.foreign_key_columns` |
| CHECK | `sys.check_constraints` (`type` = `C`) | n/a — `definition` is the whole expression; `parent_column_id` names the single column for column-scoped checks |
| DEFAULT | `sys.default_constraints` (`type` = `D`) | n/a — always exactly one column (`parent_column_id`) |

All four are **derived views of `sys.objects`** — they inherit `object_id`, `schema_id`, `parent_object_id`, `name`, `create_date`, `modify_date`, `is_ms_shipped`, `is_published`, `is_schema_published`. Resolve `schema_id`/`parent_object_id` to names via `sys.schemas`/`sys.tables` (or `sys.objects`) — do not persist the raw ids, they are not stable across databases.

Full column lists observed live (SQL Server 2025), in catalog order:

- **`sys.key_constraints`**: `name`, `object_id`, `principal_id`, `schema_id`, `parent_object_id`, `type`, `type_desc`, `create_date`, `modify_date`, `is_ms_shipped`, `is_published`, `is_schema_published`, `unique_index_id`, `is_system_named`, **`is_enforced`**.
- **`sys.foreign_keys`**: …, `referenced_object_id`, `key_index_id`, `is_disabled`, `is_not_for_replication`, `is_not_trusted`, `delete_referential_action`, `delete_referential_action_desc`, `update_referential_action`, `update_referential_action_desc`, `is_system_named`.
- **`sys.foreign_key_columns`**: `constraint_object_id`, `constraint_column_id`, `parent_object_id`, `parent_column_id`, `referenced_object_id`, `referenced_column_id`.
- **`sys.check_constraints`**: …, `is_disabled`, `is_not_for_replication`, `is_not_trusted`, `parent_column_id`, `definition`, `uses_database_collation`, `is_system_named`.
- **`sys.default_constraints`**: …, `parent_column_id`, `definition`, `is_system_named`. **No** `is_disabled`/`is_not_for_replication`/`is_not_trusted` — a DEFAULT constraint cannot be disabled or marked NOT FOR REPLICATION; only PK/UQ, FK, and CHECK carry those flags (and PK/UQ has neither `is_disabled` nor `is_not_for_replication`, only the new `is_enforced`).

## Fields relevant to change detection (excluding runtime state)

For all four: `name` (subject to system-naming, see below), `type`/`type_desc`, and the owning table (`parent_object_id` → schema-qualified name).

- **PK/UNIQUE** (`sys.key_constraints`): `type` (`PK` vs `UQ`), `is_system_named`; via `unique_index_id` joined to `sys.indexes`/`sys.index_columns`: clustering (`sys.indexes.type_desc` = `CLUSTERED`/`NONCLUSTERED`), and per-column `key_ordinal` + `column_id` + `is_descending_key`. `is_enforced` is present but see version-gating note below — on-box it is always `1` and cannot currently vary.
- **FOREIGN KEY** (`sys.foreign_keys` + `sys.foreign_key_columns`): `is_system_named`, `delete_referential_action_desc`, `update_referential_action_desc`, `is_disabled`, `is_not_for_replication`, `is_not_trusted`; per-column pairs ordered by `constraint_column_id` (parent column ↔ referenced column — order matters for composite keys); `referenced_object_id`/`key_index_id` resolve to the **target constraint or unique index actually referenced** (not necessarily the target table's PK — see edge case below).
- **CHECK** (`sys.check_constraints`): `definition` (verbatim expression text — this is the load-bearing field), `is_system_named`, `is_disabled`, `is_not_for_replication`, `is_not_trusted`, `parent_column_id` (0 = table-level, else the single referenced column), `uses_database_collation` (worth capturing — flags a hidden dependency on DB default collation for correct evaluation, without which two identically-defined constraints can behave differently across databases with different default collations).
- **DEFAULT** (`sys.default_constraints`): `definition` (verbatim expression, e.g. `(sysutcdatetime())` — captures the *function call text*, not an evaluated value, so it hashes deterministically even though the function is non-deterministic at runtime), `is_system_named`, `parent_column_id` (always non-zero — defaults are inherently column-scoped, there is no table-level default).

**Explicitly runtime/non-structural, exclude from a hash**: `create_date`, `modify_date`, `object_id`/`schema_id`/`parent_object_id` (raw ids — resolve to names instead), `principal_id` (ownership, not schema), `is_ms_shipped`/`is_published`/`is_schema_published` (replication/deployment bookkeeping, not schema shape for a normal user table).

## System-generated vs. user-supplied names

**Yes — the catalog distinguishes this directly and authoritatively**: all four views expose `is_system_named` (`bit`): `1` = name was generated by the system, `0` = name was supplied by the user in the DDL. This was confirmed live:

| Table.Constraint | is_system_named | Generated name shape |
|---|---|---|
| `Employees` PK (no name given) | 1 | `PK__Employee__7AD04F1118E99139` |
| `Employees.Salary` DEFAULT (no name) | 1 | `DF__Employees__Salar__02FC7413` |
| `Employees.Status` CHECK (no name) | 1 | `CK__Employees__Statu__04E4BC85` |
| `Tags.DeptId` FK (no name, inline `REFERENCES`) | 1 | `FK__Tags__DeptId__1BC821DD` |
| All explicitly `CONSTRAINT <name>`-declared constraints | 0 | the literal name given |

The generated shape is consistently `<Prefix>__<truncated-table-or-column>__<8-hex-digit-suffix>` (`PK`/`UQ` truncate to the table name; `DF`/`CK`/`FK` truncate to `table__column`). The hex suffix is derived from the object id and is **not** stable across databases/recreations — a hasher normalizing system-generated names should match on the `is_system_named` flag (authoritative) rather than re-deriving the shape, though a shape-based regex fallback (`^(PK|UQ|DF|CK|FK)__.+__[0-9A-F]{8}$`) is a reasonable heuristic for names scripted out and replayed where the flag may not reflect original intent.

## Notable edge cases found by direct experimentation

1. **`is_disabled` and `is_not_trusted` are independent axes**, both for FK and CHECK. Disabling a constraint (`NOCHECK CONSTRAINT`) sets `is_disabled=1` but does not touch `is_not_trusted`. Re-enabling with plain `CHECK CONSTRAINT` (no `WITH CHECK`) clears `is_disabled` back to `0` **but leaves `is_not_trusted=1`** — the constraint is enforced going forward but the optimizer still can't trust existing rows, because they were never (re)validated. Only `ALTER TABLE ... WITH CHECK CHECK CONSTRAINT <name>` clears `is_not_trusted` to `0`. **Implication for schema hashing**: these are two independently meaningful bits of state that must both be captured; observing only `is_disabled` misses a real, DDL-history-driven difference between two schemas that otherwise look identical.

2. **`NOT FOR REPLICATION` constraints are always reported `is_not_trusted=1`**, even when created with `WITH CHECK` against an empty table (which normally auto-trusts a fresh constraint). Verified on both a FOREIGN KEY and a CHECK constraint, and reproduced in isolation on a fresh table with `ALTER TABLE ... WITH CHECK ADD CONSTRAINT ... CHECK NOT FOR REPLICATION (...)`. This means `is_not_trusted=1` does **not** reliably signal "unvalidated data" by itself — it is also the permanent state of any NOT FOR REPLICATION constraint. A naive "trusted iff not `is_not_trusted`" check would misreport every NFR constraint as suspect.

3. **`parent_column_id` for CHECK constraints is driven by how many distinct columns the expression touches, not by where in the DDL the constraint was declared.** A `CONSTRAINT ... CHECK (...)` written in *table-constraint* position (trailing, not attached to a specific column) still gets a non-zero `parent_column_id` (classified as column-level) if its expression references exactly one column; only expressions spanning ≥2 columns get `parent_column_id = 0` (table-level). Verified: `CK_Departments_Budget`, declared as a trailing table constraint referencing only `Budget`, resolved to `parent_column_id = 3` (Budget's column id), not `0`. Conversely SQL Server *rejects at parse time* (`Msg 8141`) an inline column-attached CHECK that references a second column — so inline syntax is column-count-restricted, but trailing/table syntax is not, and the catalog's classification tracks the actual column-reference count either way. A hasher keying on `parent_column_id == 0` as "table-level, sort last/first" should be aware two logically-equivalent single-column checks written in different DDL styles land identically in the catalog (which is actually the *desirable*, deterministic outcome).

4. **An FK's referenced key need not be the parent table's PRIMARY KEY — it can be any UNIQUE constraint (or presumably a matching unique index).** Built `FK_ProjectAssignments_Employees` referencing the composite `UQ_Employees_Id_Dept` unique constraint rather than `Employees`' primary key. `sys.foreign_keys.key_index_id` correctly resolves (via `sys.indexes`) to `UQ_Employees_Id_Dept`. A correct extractor must resolve `(referenced_object_id, key_index_id)` to whichever unique key/index it actually is, not assume "the PK."

5. **Composite FK column order is authoritative and independently ordered from the target key's declared order** via `sys.foreign_key_columns.constraint_column_id` (1-based). Verified the two-column FK `FK_ProjectAssignments_Employees` orders `(EmployeeId, DeptId)` matching the declared `UQ_Employees_Id_Dept (EmployeeId, DeptId)` key order — this ordering must be preserved (not re-sorted) since it is semantically meaningful for multi-column joins.

6. **`SET NULL`/`SET DEFAULT` referential actions have preconditions the catalog doesn't itself enforce visibly**: attempting `ON DELETE SET NULL` against a `NOT NULL` FK column fails at `CREATE`/`ALTER` time (`Msg 1761`) — so by the time a constraint exists in the catalog with `delete_referential_action_desc = 'SET_NULL'`, the referencing column(s) are guaranteed nullable. Not itself a catalog field, but a useful cross-check invariant when validating extracted metadata.

7. **Constraint names are schema-scoped (database-object-namespace-scoped), not table-scoped.** Attempting to create a second constraint named `PK_Departments` on an unrelated table in the same schema fails with `Msg 2714 "There is already an object named ... in the database"` — constraints share the same object-name namespace as tables, views, procedures, etc. within a schema. Relevant if a hasher ever needs to reason about name collisions/renames across constraint types.

8. **DEFAULT constraint `definition` captures the verbatim expression text, not an evaluated value** — e.g. `(sysutcdatetime())`, `((50000))`. This is what makes default-constraint hashing deterministic despite the underlying function being non-deterministic at execution time: the *text* is stable, only its future evaluation isn't.

9. **`uses_database_collation` on `sys.check_constraints`** flags check expressions whose evaluation depends on the database's default collation (e.g. string comparisons/`LIKE` without an explicit `COLLATE`). Two databases with identical `definition` text but different default collations can behave differently at runtime even though the DDL text hashes identically — worth surfacing as a caveat even though it isn't itself a "the schema changed" signal for a pure text-based hash.

## Version gating observed

- **`sys.key_constraints.is_enforced`** (`bit`) is present in the live SQL Server 2025 catalog but **is not documented** on the current Microsoft Learn `sys.key_constraints` page (`?view=sql-server-ver17`, fetched directly — the documented column list stops at `is_system_named`). It corresponds to the `NOT ENFORCED` clause added to `ALTER TABLE`/`CREATE TABLE` PRIMARY KEY/UNIQUE/FOREIGN KEY constraint syntax, which Microsoft Learn documents as required-for and scoped to **Microsoft Fabric Warehouse**, not general on-premises/box SQL Server. Confirmed empirically: attempting `PRIMARY KEY NONCLUSTERED (...) NOT ENFORCED` on this SQL Server 2025 (Enterprise, Linux, on-box) instance fails with `Msg 40514: 'NOT ENFORCED' is not supported in this version of SQL Server`. On this box every observed `is_enforced` value was `1`. Treat this column as **present-but-inert on box SQL Server today** — a schema hasher can capture it for forward-compatibility but should not expect to see `0` outside Fabric Warehouse.
- **`NOT FOR REPLICATION`** on FK/CHECK constraints: Microsoft Learn's `ALTER TABLE column_constraint` page states this clause **applies to SQL Server 2008 (10.0.x) and later** — well below this library's SQL Server 2016 floor, so no additional gating needed for that lower bound.
- **`is_system_named`, `is_disabled`, `is_not_for_replication`, `is_not_trusted`, `uses_database_collation`, `parent_column_id`** are documented with no version qualifier beyond the standard "Applies to: SQL Server / Azure SQL Database / Azure SQL Managed Instance / Azure Synapse Analytics" banner common to all catalog views checked — no evidence of a minimum-version cliff for these within the library's supported range.
- No CHECK-constraint or DEFAULT-constraint equivalent of `is_enforced` exists in the live catalog (only `sys.key_constraints` gained it) — CHECK's "not enforced" concept remains expressed purely through the pre-existing `is_disabled` flag.

## Objects created for this audit (schema `audit_db.audit_constraints`)

- `Departments` — user-named PK, UNIQUE, DEFAULT, CHECK.
- `Employees` — **system-named** PK, DEFAULT ×2, CHECK; user-named composite UNIQUE (`UQ_Employees_Id_Dept`); FK to `Departments` with `ON DELETE CASCADE ON UPDATE NO ACTION`.
- `Projects` — nullable FK column, FK to `Departments` with `ON DELETE SET NULL ON UPDATE CASCADE`; composite UNIQUE.
- `Widgets` — CHECK added `WITH NOCHECK` then explicitly disabled/re-enabled (drove edge cases #1).
- `Widgets2` — self-referencing FK and CHECK, both `NOT FOR REPLICATION` (drove edge case #2).
- `ProjectAssignments` — multi-column FK targeting a UNIQUE constraint rather than a PK (edge cases #4, #5).
- `Orders` — table-level (two-column) CHECK constraint contrasted against single-column checks (edge case #3).
- `Tags`, `NfrTest`, `ColLevelTest` — small scratch tables used only to isolate individual behaviors (system-named FK shape, NOT FOR REPLICATION trust in isolation, and the parent_column_id experiment).

## Coverage audit

- **PRIMARY KEY / UNIQUE constraint `is_enforced` not captured**: `sys.key_constraints.is_enforced` (the flag behind the `NOT ENFORCED` clause) is neither queried by `SchemaExtractor.ExtractKeyConstraintsAsync` nor represented on `KeyConstraintSchema` — the PK/UNIQUE counterpart to the `is_disabled`/`is_not_trusted` enforcement bits already captured for FOREIGN KEY and CHECK constraints is missing. On this library's currently-supported platforms (SQL Server 2016+, Azure SQL DB/MI) the flag is observed to always be `1` (`NOT ENFORCED` on a PK/UNIQUE is Fabric Warehouse-only and is rejected elsewhere with `Msg 40514`), so it cannot presently diverge on a supported server, but it is a real, always-present catalog column and its absence is worth tracking for forward-compatibility. Filed in `BUGS.md` under "PRIMARY KEY / UNIQUE constraint `is_enforced` not captured".

All other fields identified as relevant to change detection in the catalog research above — `is_system_named` (all four constraint kinds), FK's `is_disabled`/`is_not_trusted`/`is_not_for_replication`/`delete_referential_action_desc`/`update_referential_action_desc`/per-column pairs in `constraint_column_id` order/actual-referenced-key resolution, CHECK's `definition`/`is_disabled`/`is_not_trusted`/`is_not_for_replication`, and DEFAULT's `definition`/`parent_column_id` (resolved to `ColumnName`) — are extracted and hashed correctly (`SchemaExtractor.cs`, `SchemaMetadata.cs`, `SchemaHashCalculator.cs`). CHECK's `parent_column_id` is not independently captured, but per edge case #3 above it is fully derived from which columns `definition` references, so it carries no distinguishing information beyond the already-hashed `Definition` text and is not counted as a gap. `uses_database_collation` is explicitly called out in the catalog research itself as not a "schema changed" signal for a text-based hash, so it is likewise not counted as a gap.
