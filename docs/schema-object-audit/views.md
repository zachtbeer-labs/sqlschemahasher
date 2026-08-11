# Views

## Scope

SQL Server views (`CREATE VIEW`), including schema-bound indexed views. This
covers what is needed to detect a *schema* change to a view — not the runtime
result of querying it.

All findings below were produced by hand against a live SQL Server 2025
(17.0.1000.7) container, in a scratch schema `audit_views` created for this
audit, then cross-checked against Microsoft Learn.

## Catalog views that expose it

| Catalog view | What it adds |
|---|---|
| `sys.objects` (`type = 'V'`) | The object identity: `object_id`, `name`, `schema_id`, `is_ms_shipped`. `parent_object_id` is `0` — a view is a top-level object, not owned by anything. |
| `sys.views` | View-specific flags: `with_check_option`, `has_opaque_metadata` (VIEW_METADATA), `is_replicated`, `has_replication_filter`, `has_unchecked_assembly_data`, `is_date_correlation_view`, and (SQL Server 2022+) the ledger columns. `sys.views` is documented as "inherits" the `sys.objects` columns — you join/select both. |
| `sys.sql_modules` | The one row per view holding `definition` (the verbatim `CREATE VIEW` text, or `NULL` if `WITH ENCRYPTION`), `uses_ansi_nulls`, `uses_quoted_identifier`, `is_schema_bound`, `uses_database_collation`. This is where CREATE-time `SET` options and `SCHEMABINDING` actually live. |
| `sys.columns` | The view's own output-column shape: `column_id`, `name`, `system_type_id`/`user_type_id`, `max_length`, `precision`, `scale`, `is_nullable`. **This is a separately-cached, separately-stale copy of "what columns does this view return", independent of the definition text** — see the stale-view section below. |
| `sys.indexes` / `sys.index_columns` | Only populated for **indexed views** (schema-bound + `UNIQUE CLUSTERED INDEX`). Structurally identical to how indexes on tables are represented — same two catalog views, keyed by the view's `object_id`. |
| `sys.sql_expression_dependencies` | Cross-object references the view makes (`referenced_schema_name`, `referenced_entity_name`, `referenced_minor_id`, `is_schema_bound_reference`). Useful for detecting "the view's underlying object was renamed/dropped" scenarios. |

`OBJECT_DEFINITION(object_id)` is a documented shortcut for
`sys.sql_modules.definition` and has exactly the same encryption behavior
(returns `NULL`).

## Columns/flags relevant to detecting a schema change

From `sys.views` (+ inherited `sys.objects` columns):
- `name`, `schema_id` — identity.
- `with_check_option` — `1` if `WITH CHECK OPTION` was specified. Confirmed by direct test (`vOrderSummary_Legacy ... WITH CHECK OPTION` → `with_check_option = 1`, all others `0`).
- `has_opaque_metadata` — `1` if created `WITH VIEW_METADATA`. Confirmed by direct test.
- `is_replicated`, `has_replication_filter` — replication-topology flags; schema-relevant if replication is in scope.
- `has_unchecked_assembly_data` — only meaningful for indexed views that reference CLR functions/types whose assembly was later `ALTER ASSEMBLY`'d; resets on the next `DBCC CHECKDB`/`CHECKTABLE`. This is a special case of *runtime* drift (not a DDL change) — worth explicitly excluding, since it's not something a schema-hash of the view definition would or should react to.
- `ledger_view_type` / `ledger_view_type_desc` / `is_dropped_ledger_view` — **SQL Server 2022+ only** (see version gating below); present but inert (`0`/`NON_LEDGER_VIEW`/`0`) for ordinary views.
- `is_date_correlation_view` — `1` only for system-generated correlation views (`DATE_CORRELATION_OPTIMIZATION`); not something a user `CREATE VIEW` produces, but worth excluding explicitly if enumerating `sys.views` broadly, since these are system-managed rather than user schema.

From `sys.sql_modules`:
- `definition` — the module text. **`NULL` when the view was created `WITH ENCRYPTION`** (confirmed: `vOrderSummary_Encrypted` → `definition IS NULL`, `OBJECTPROPERTY(..., 'IsEncrypted') = 1`). This is a genuine hard edge case — the view's actual logic is unrecoverable from the catalog (even connected as `sa`), so a text-hash approach has nothing to hash for the module body. `sys.columns` is *not* similarly blocked, though (see below) — the output shape of an encrypted view is still fully visible even though its body is not.
- `uses_ansi_nulls`, `uses_quoted_identifier` — recorded once, at `CREATE VIEW` time, from whichever session-level `SET ANSI_NULLS`/`SET QUOTED_IDENTIFIER` were in effect. Per Microsoft Learn's `CREATE VIEW` remarks: *"The Database Engine saves the settings of SET QUOTED_IDENTIFIER and SET ANSI_NULLS when a view is created... any client-session settings... do not affect the view definition when the view is accessed."* Confirmed directly: a view created with `SET ANSI_NULLS OFF; SET QUOTED_IDENTIFIER OFF;` (`vOrderSummary_Legacy`) permanently shows `uses_ansi_nulls = 0, uses_quoted_identifier = 0`, unaffected by later `SET ... ON` in the same or other sessions. **These are genuine, semantically-meaningful schema facts about the view, not incidental session noise, and should be part of any change-detection surface.**
- `is_schema_bound` — `1` iff created `WITH SCHEMABINDING`. Directly gates whether the view can carry an index (see below) and whether the engine enforces referential integrity against the underlying columns.
- `uses_database_collation` — `1` when a schema-bound view's definition depends on the database's default collation (blocks changing that collation later). Only meaningful in combination with `is_schema_bound = 1`.
- `is_recompiled`, `null_on_null_input`, `execute_as_principal_id`, `uses_native_compilation`, `is_inlineable` — documented as applicable to modules generally (procs/functions/triggers); for plain views these are either inert or not meaningful (`is_recompiled` is a stored-procedure-only concept, `is_inlineable`/`uses_native_compilation` are function-only). Not relevant to view schema hashing.

From `sys.columns` (per view):
- Full column shape (`name`, `column_id`, `system_type_id`, `max_length`, `precision`, `scale`, `collation_name`, `is_nullable`) — this is what a consumer actually gets back from `SELECT * FROM view` or from tooling that inspects the view's result-set shape (e.g., ORM code-gen, `INFORMATION_SCHEMA.COLUMNS`).
- **This is a materialized snapshot taken at `CREATE`/`ALTER VIEW` (or `sp_refreshview`) time — not derived live from the definition text every time it's read.** This is the mechanism behind the stale-view edge case below, and it means `sys.columns` and `sys.sql_modules.definition` can each be individually stale relative to the true current shape and to each other.
- Note a modeling detail found by experiment: `COUNT_BIG(*)` and `SUM(decimal(10,2))` aggregate output columns in an indexed view are recorded as `is_nullable = 1` in `sys.columns`, even though `COUNT_BIG` never actually returns `NULL` — the catalog records the type-inference result, not a runtime guarantee.

From `sys.indexes` / `sys.index_columns` (indexed views only):
- Identical shape to table indexes: `type_desc` (`CLUSTERED`/`NONCLUSTERED`), `is_unique`, `is_padded`, `fill_factor`, `has_filter`/`filter_definition`, and per-column `key_ordinal`/`is_descending_key`/`is_included_column`. The unique clustered index is mandatory and *materializes* the view's data; any nonclustered indexes layered on top behave exactly like table nonclustered indexes.

From `sys.sql_expression_dependencies`:
- `referenced_schema_name`, `referenced_entity_name`, `is_schema_bound_reference`. Notable asymmetry found by experiment: for a **non-schema-bound** view, dependency rows only ever have `referenced_minor_id = 0` (a whole-object reference), even when the view's `SELECT` list names specific columns explicitly. For a **schema-bound** view, the engine tracks column-level dependencies (`referenced_minor_id` = each referenced column's `column_id`, one row per column, plus a `0` row for the table itself) — this is the mechanism that lets the engine *enforce* `SCHEMABINDING`'s "you can't drop/alter a column a view depends on" guarantee (see below).

## Notable edge cases found by direct experimentation

### 1. `sqlcmd`'s default session `SET` options are *not* SSMS's defaults, and they get baked into the view forever

Creating a schema-bound view over `sqlcmd`'s default connection captured
`uses_quoted_identifier = 0` (OFF) with no explicit `SET` statement at all —
`sqlcmd`'s default is `QUOTED_IDENTIFIER OFF` (unlike SSMS, which defaults to
ON). This alone doesn't block `CREATE VIEW ... WITH SCHEMABINDING`, but it
**does** block ever indexing that view:

```
Msg 1935: Cannot create index. Object 'vOrderAgg' was created with the
following SET options off: 'QUOTED_IDENTIFIER'.
Msg 1940: Cannot create index on view 'audit_views.vOrderAgg'. It does not
have a unique clustered index.
```

Dropping and recreating the identical view text under `SET QUOTED_IDENTIFIER
ON` immediately allowed the index to be created. This means **the same
`CREATE VIEW` text can produce two catalog-distinguishable, functionally
different objects** depending on the ambient session settings at creation
time — and the only way to tell them apart afterward is
`sys.sql_modules.uses_quoted_identifier`/`uses_ansi_nulls`, not the
definition text itself. Per Microsoft Learn's *Create indexed views*
requirements, the full required set for indexed views (and indexes on
computed columns) is: `ANSI_NULLS`, `ANSI_PADDING`, `ANSI_WARNINGS`,
`ARITHABORT`, `CONCAT_NULL_YIELDS_NULL`, `QUOTED_IDENTIFIER` all `ON`, and
`NUMERIC_ROUNDABORT` `OFF` — but only `ANSI_NULLS` and `QUOTED_IDENTIFIER`
are actually *persisted per-object* in `sys.sql_modules`; the rest are
session-level requirements enforced at DML/query time, not stored per-view.

### 2. `WITH ENCRYPTION` hides the body but not the shape

`vOrderSummary_Encrypted` (`WITH ENCRYPTION`): `sys.sql_modules.definition`
and `OBJECT_DEFINITION(...)` both return `NULL`, confirmed even connected as
`sa`/sysadmin (no special decryption path via plain T-SQL). However,
`sys.columns` for that same view is fully populated
(`OrderId int`, `OrderTotal decimal`, ...) — the output shape survives
encryption even though the logic doesn't. A schema-hash strategy that only
hashes `sys.sql_modules.definition` degenerates to "encrypted views are all
indistinguishable placeholders"; one that also incorporates `sys.columns`
retains at least shape-level change detection for encrypted views.

### 3. Why a view's column list goes stale, demonstrated end-to-end

Built `audit_views.StaleDemo(Id, Col1)`, then `CREATE VIEW vStaleDemo AS
SELECT * FROM audit_views.StaleDemo` (deliberately **not** schema-bound —
`SELECT *` is disallowed under `SCHEMABINDING` in the first place, since
schema binding requires explicit column names). Then `ALTER TABLE
audit_views.StaleDemo ADD Col2 nvarchar(50) NULL`.

Result, before touching the view again:
- `sys.columns` for `StaleDemo` (the table): 3 columns (`Id`, `Col1`, `Col2`).
- `sys.columns` for `vStaleDemo` (the view): still only **2** columns (`Id`, `Col1`) — stale.
- `SELECT * FROM vStaleDemo` at runtime: returns only `Id, Col1` (0 rows in the demo, but 2 columns) — **the query executor also honors the stale cached shape**, it does not re-expand `SELECT *` against the table's live definition. So this isn't just a catalog-metadata cosmetic issue — it changes actual query results.
- `sys.sql_modules.definition` for the view: unchanged (`SELECT * FROM audit_views.StaleDemo`) — the *text* never claims to know the column list, so there's nothing in the definition text itself that's "wrong"; the staleness lives entirely in the separately-materialized `sys.columns` snapshot.

Running `EXEC sp_refreshview 'audit_views.vStaleDemo'` immediately corrects
`sys.columns` to 3 rows (`Id`, `Col1`, `Col2`), with no change to
`sys.sql_modules.definition` at all.

**Extending the experiment to a rename** (not just an add): after refreshing,
`sp_rename 'audit_views.StaleDemo.Col1', 'RenamedCol1', 'COLUMN'` was run.
`sys.columns` for the view *still* reported the column as `Col1` (stale name)
until a second `sp_refreshview` was run, after which it correctly became
`RenamedCol1`. This matches Microsoft Learn's *Rename columns* guidance
verbatim: *"Renaming a column doesn't automatically update the metadata for
any objects which SELECT all columns (using \*) from that table... Refresh
the metadata using sp_refreshsqlmodule or sp_refreshview."*

**Practical implication for schema-change detection**: `sys.sql_modules
.definition` is necessary but not sufficient for views that use `SELECT *`
(or otherwise don't spell out every column) — it can be byte-for-byte
identical across a schema change that silently altered what the view
actually returns. Detecting this class of drift requires also hashing
`sys.columns` for the view, and even then, a genuinely *stale* view (where
nobody has run `sp_refreshview` since the underlying table changed) will
report an old-but-internally-consistent column set that doesn't match the
table's current one — the mismatch itself is only visible by comparing the
view's `sys.columns` against the base table's `sys.columns`, which is a
much heavier cross-referencing exercise than hashing the view alone.

### 4. `SCHEMABINDING` converts "can go stale silently" into "can't be broken at all"

By contrast, `vOrderSummary_Bound` (`WITH SCHEMABINDING`, explicit columns
including `OrderDate`) actively blocked a breaking underlying change:

```sql
ALTER TABLE audit_views.Orders DROP COLUMN OrderDate;
-- Msg 5074: The object 'vOrderSummary_Bound' is dependent on column 'OrderDate'.
-- Msg 4922: ALTER TABLE DROP COLUMN OrderDate failed because one or more
--   objects access this column.
```

This is enforced using exactly the column-level rows in
`sys.sql_expression_dependencies` described above — schema-bound views are
the only ones with column-granularity dependency tracking, which is what
lets the engine refuse the `ALTER TABLE` outright rather than let the view
silently drift. This means schema-bound and non-schema-bound views have
fundamentally different staleness risk profiles, even though nothing in
`sys.views`/`sys.objects` other than `sys.sql_modules.is_schema_bound`
distinguishes them.

### 5. `sp_rename`-ing the view object itself corrupts the definition text's self-reference

Per Microsoft Learn's `sp_rename` remarks (confirmed directly): renaming a
view via `sp_rename` updates `sys.objects.name` immediately, but
`sys.sql_modules.definition` keeps the *original* `CREATE VIEW
<old-schema>.<old-name>` text verbatim forever — there is no automatic
resync. Confirmed: after `sp_rename 'audit_views.vOrderSummary',
'vOrderSummary_Renamed'`, `sys.objects.name = 'vOrderSummary_Renamed'` but
`sys.sql_modules.definition` still reads `CREATE VIEW
audit_views.vOrderSummary`. Microsoft's own documented recommendation is to
never `sp_rename` a view (or proc/function/trigger) and instead drop and
recreate it with the new name. For schema-hashing purposes this means: (a)
the object's *name* and the name embedded in its *definition text* can
legitimately diverge and both are simultaneously "true" from different
catalog views, and (b) if a hash strategy re-derives the object's identity
from parsing the definition text rather than trusting `sys.objects.name`,
a `sp_rename` would go undetected as a rename.

### 6. `ALTER VIEW` unconditionally drops all indexes, even for unrelated edits

Documented on Microsoft Learn (`ALTER VIEW` remarks): *"ALTER VIEW can be
applied to indexed views; however, ALTER VIEW unconditionally drops all
indexes on the view."* Not independently re-verified against the live
container in this pass, but flagged because it's a sharp edge for schema
hashing: an `ALTER VIEW` that changes nothing structurally interesting (say,
just reformats whitespace) still destroys every index on that view as a
side effect, and nothing recreates them automatically — a subsequent hash
of `sys.indexes` for that view would show the indexes vanished, which is a
real, consequential schema change even though the view's own `SELECT` logic
didn't change semantically. Also per the same page: if the previous
definition used `WITH ENCRYPTION` or `WITH CHECK OPTION`, those are dropped
unless explicitly re-specified in the `ALTER VIEW` statement — i.e., `ALTER
VIEW` does not preserve those flags by default, unlike a plain re-run of the
original `CREATE VIEW` text would.

## Version gating observed / documented

- **`ledger_view_type`, `ledger_view_type_desc`, `is_dropped_ledger_view`** on `sys.views` — Microsoft Learn marks these **"Applies to: Starting with SQL Server 2022 (16.x), Azure SQL Database."** Present (and inert) on this SQL Server 2025 instance; would not exist as columns pre-2022.
- **Indexed views in non-Enterprise editions** — historically Enterprise/Developer-only; since SQL Server 2016 SP1, indexed views (like most previously Enterprise-only features) are supported in Standard edition too (the optimizer's automatic use of an indexed view without `NOEXPAND` remained Enterprise-only for longer, but direct querying/creation is broadly available since 2016 SP1). Given this project's stated minimum-supported-server baseline of SQL Server 2016 (13.x), this is relevant: an indexed view's mere *existence* is detectable everywhere in scope, but automatic optimizer use of it may differ by edition — that's a query-plan concern, not a catalog/schema one, so it shouldn't affect a schema hash either way.
- **`sys.sql_modules.is_inlineable`/`inline_type`** — Microsoft Learn: **"Applies to: SQL Server 2019 (15.x) and later versions."** These are documented as scalar-UDF/inline-TVF concepts specifically (always `0` for other module types, which includes views) — not relevant to view schema hashing, but worth noting the columns themselves are version-gated within `sys.sql_modules` generally.
- **`inline_eligibility_mask`** — Microsoft Learn scopes this to **Fabric Warehouse/SQL analytics endpoint only**, not SQL Server proper.
- Everything else exercised here (`sys.views`, base `sys.sql_modules` columns, `sys.columns`, `sys.indexes`/`sys.index_columns`, `sys.sql_expression_dependencies`) is unversioned/long-standing and present at this project's SQL Server 2016 floor.

## Explicitly excluded as runtime state (not schema)

- `sys.objects.create_date` / `modify_date` — timestamps, not schema content; `modify_date` in particular changes on `sp_refreshview`/`sp_refreshsqlmodule` even when nothing schema-visible changed (or, conversely, does *not* change the actual returned-column staleness problem described above — the two are independent signals).
- `sys.views.has_unchecked_assembly_data` — a transient CLR-consistency flag that self-resets on the next `DBCC CHECKDB`/`CHECKTABLE`; reflects assembly-reload history, not the view's own definition.
- Anything from querying the view's *data* (row counts, actual returned values) — out of scope entirely; this audit is about the view object's shape/definition, not its content.

## Example objects created for this audit (`audit_db.audit_views` schema)

- `vOrderSummary` — plain view, explicit column list.
- `vOrderSummary_Bound` — `WITH SCHEMABINDING`, no index (schema-bound but not "indexed").
- `vOrderAgg` — `WITH SCHEMABINDING` + `UNIQUE CLUSTERED INDEX` + a `NONCLUSTERED INDEX` — a true indexed view; also the vehicle for the `QUOTED_IDENTIFIER` edge case above.
- `vOrderSummary_Legacy` — created under `SET ANSI_NULLS OFF; SET QUOTED_IDENTIFIER OFF`, with `WITH CHECK OPTION`.
- `vOrderSummary_Encrypted` — `WITH ENCRYPTION`.
- `vOrderSummary_Metadata` — `WITH VIEW_METADATA`, to exercise `has_opaque_metadata`.
- `vStaleDemo` — the `SELECT *` staleness demo (table widened + column renamed underneath it, with and without `sp_refreshview`).
- `vCustomerOrders` — a two-table join view, to observe `sys.sql_expression_dependencies` with multiple referenced entities.

## Coverage audit

- **`sys.views.with_check_option` not captured**: `ViewSchema` has no field for `WITH CHECK OPTION`. Under default options this happens to be reflected in `DefinitionHash` (it's part of the raw definition text), but it is invisible under `ModuleNormalization.IgnoreBodyText` and there is no discrete field a caller can inspect independent of body-text diffing. Filed in BUGS.md as "View `WITH CHECK OPTION` not captured".
- **`sys.views.has_opaque_metadata` (VIEW_METADATA) not captured**: `ViewSchema` has no field for `WITH VIEW_METADATA`. Same body-text-only visibility problem as `WITH CHECK OPTION` above. Filed in BUGS.md as "View `VIEW_METADATA` (has_opaque_metadata) not captured".
- **`sys.sql_modules.is_schema_bound` not captured for views**: `ViewSchema` has no field for `WITH SCHEMABINDING`, unlike the parallel gap already tracked for triggers and functions. `SCHEMABINDING` is a load-bearing clause (prerequisite for indexing the view; locks referenced columns against breaking `ALTER`s) and, like the two flags above, is only incidentally visible via `DefinitionHash` and disappears under `ModuleNormalization.IgnoreBodyText`. Filed in BUGS.md as "View schema-binding (`WITH SCHEMABINDING`) not captured".

The following blind-doc findings were checked and are **not** gaps — already captured, already excluded by documented design, or out of the stated audit criteria:
- `sys.views.name`/`schema_id` (identity), `sys.sql_modules.definition` (as `DefinitionHash`, with a distinct `<encrypted>` sentinel for `WITH ENCRYPTION`), `uses_ansi_nulls`/`uses_quoted_identifier`, and indexed-view `sys.indexes`/`sys.index_columns` are all extracted and hashed (`SchemaExtractor.ExtractViewsAsync`, `ViewSchema`, `SchemaHashCalculator.HashView`).
- `sys.columns` (the view's own output-column shape) is a deliberate, documented exclusion — see `ViewSchema`'s doc comment and `website/docs/what-gets-hashed.md`'s Views entry: a `SELECT *` view's column metadata is a separately-cached snapshot that goes stale until `sp_refreshview` runs, and capturing it would inject spurious diffs unrelated to any DDL change.
- `has_unchecked_assembly_data` is explicitly excluded as CLR-consistency runtime state per the blind doc itself, matching this project's own runtime-state exclusion policy.
- `ledger_view_type`/`ledger_view_type_desc`/`is_dropped_ledger_view` (SQL Server 2022+) are out of scope — ledger is an explicitly out-of-scope feature per `CLAUDE.md`.
- `is_date_correlation_view` flags a system-generated object, not user schema, per the blind doc itself.
- `is_replicated`/`has_replication_filter` are replication-topology flags; the blind doc itself hedges "schema-relevant if replication is in scope," and no other object type in this library captures replication-topology state (as distinct from the `NOT FOR REPLICATION` enforcement flags it does capture elsewhere), so this tracks with the project's existing scope boundary rather than a gap.
- `uses_database_collation` is a value fully derived from the view's `SELECT` text (whether it references collation-dependent columns) — any change to it necessarily co-occurs with a definition-text change already reflected in `DefinitionHash`, so it adds no independent signal.
- `sys.sql_expression_dependencies` (cross-object reference tracking) is not extracted for any object type in this library; the dependency data it exposes is fully implied by the definition text already captured via `DefinitionHash`, and building it out is a materially different extraction shape (a new cross-reference surface) than a per-object schema fact.
- The `sp_rename` self-reference-corruption edge case (#5) is not a gap: the library always resolves a view's identity from `sys.objects`/`sys.views` (`SchemaName`/`Name`), never by parsing the definition text, so this divergence cannot cause a misattributed identity.
- The `ALTER VIEW` drops-all-indexes edge case (#6) is not a gap: a dropped index is simply absent from `ViewSchema.Indexes` and so is already reflected in the hash via ordinary index-list comparison.
