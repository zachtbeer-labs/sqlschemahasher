# Triggers

## Scope

This covers **DML triggers** (`AFTER`/`FOR` and `INSTEAD OF`, fired by `INSERT`/`UPDATE`/`DELETE` against a table or view) as the primary schema-object surface, and separately notes **DDL triggers** (database- and server-scoped) as a related but structurally distinct catalog surface that shares some of the same views.

Experiments were run against a live SQL Server 2025 (17.0.1000.7) instance in schema `audit_triggers` of the shared `audit_db` database.

## Catalog views

| View | Scope | Purpose |
|---|---|---|
| `sys.triggers` | One row per trigger — DML **and** DDL | Core trigger metadata: name, parent, type, flags |
| `sys.trigger_events` | One row per (trigger, event-type) pair | Which `INSERT`/`UPDATE`/`DELETE`/DDL events fire the trigger, plus FIRST/LAST ordering |
| `sys.trigger_event_types` | Static reference | Enumerates all legal event/event-group type codes usable in `CREATE TRIGGER ... FOR` |
| `sys.sql_modules` | One row per T-SQL module (incl. triggers) | Trigger body text + the `SET` options captured at create/alter time |
| `sys.objects` | One row per schema-scoped object | DML triggers *also* appear here (`type = 'TR'`) since they are schema-scoped; DDL triggers do **not** appear here |
| `sys.server_triggers` | Server-scoped DDL/logon triggers only | Separate catalog surface, parallel structure to `sys.triggers` |
| `sys.server_trigger_events` | Server-scoped trigger events | Parallel structure to `sys.trigger_events`, inherits from `sys.server_events` instead of `sys.events` |
| `sys.events` | Underlying event catalog `sys.trigger_events` inherits from | `object_id`, `type`, `type_desc`, `is_trigger_event`, `event_group_type[_desc]` |

Both DML and DDL (database-scoped) triggers live in the **same** `sys.triggers` view, distinguished only by `parent_class` (`0` = DATABASE for DDL triggers, `1` = OBJECT_OR_COLUMN for DML triggers). Server-scoped DDL/logon triggers are a genuinely separate view (`sys.server_triggers` / `sys.server_trigger_events`), not rows in `sys.triggers`. This confirms the note above: **DDL/server-scoped triggers are a distinct catalog surface** from object (DML) triggers, even though `sys.triggers` itself is shared between DML and *database-scoped* DDL triggers.

## `sys.triggers` — columns relevant to schema-change detection

Queried directly against the created objects:

```
object_id | trigger_name | parent_class | parent_class_desc | parent_id | ...
        1 = OBJECT_OR_COLUMN for every DML trigger created here
        0 = DATABASE          for a database-scoped DDL trigger (probe, verified then dropped)
```

| Column | Type | Change-relevant? | Notes |
|---|---|---|---|
| `name` | sysname | Yes | Trigger identity. DML trigger names are schema-scoped (visible/unique per schema); DDL trigger names are scoped to their parent entity (database or server) instead. |
| `object_id` | int | No (surrogate) | Database-local catalog ID; not stable across databases — do not hash directly. |
| `parent_class` | tinyint | Yes | `0` = Database (DDL trigger), `1` = Object_or_column (DML trigger). Distinguishes the two trigger kinds sharing this view. |
| `parent_class_desc` | nvarchar(60) | Yes (redundant with above) | `DATABASE` / `OBJECT_OR_COLUMN`. |
| `parent_id` | int | Yes, but must be resolved | For DML triggers, the `object_id` of the table/view the trigger is defined on — resolve to schema-qualified name via `OBJECT_SCHEMA_NAME`/`OBJECT_NAME` before hashing (raw ID is DB-local). `0` for database-scoped DDL triggers. |
| `type` / `type_desc` | char(2) / nvarchar(60) | Yes | `TR`/`SQL_TRIGGER` vs `TA`/`CLR_TRIGGER` (CLR triggers are out of scope for this library per project conventions, but the flag itself is what would gate that exclusion). |
| `create_date` | datetime | **No** | Runtime/provenance state — excluded from hashing. |
| `modify_date` | datetime | **No** | Runtime/provenance state — excluded from hashing (changes on `ALTER TRIGGER` even when semantics are equivalent, e.g. whitespace-only edits). |
| `is_ms_shipped` | bit | Yes (as a filter) | Distinguishes system-shipped triggers from user objects; `0` for everything created here. |
| `is_disabled` | bit | **Yes — critical** | `DISABLE TRIGGER ... ON ...` flips this without changing `sql_modules.definition`, `create_date`, or event rows at all. Verified experimentally: our `trg_Orders_UpdateDisabledNFR` was disabled via `DISABLE TRIGGER` and functionally confirmed **not to fire** on a subsequent `UPDATE`, while `modify_date` still reflected only the trigger's own alter time, not a "was disabled" timestamp separate from that. A hash that omits `is_disabled` would miss a real, silent behavior change. |
| `is_instead_of_trigger` | bit | Yes | `1` = `INSTEAD OF`, `0` = `AFTER`/`FOR`. Semantically these are entirely different execution models (INSTEAD OF suppresses the base action; AFTER runs post-action) — must be part of the hash. |
| `is_not_for_replication` | bit | Yes | Reflects `NOT FOR REPLICATION` in the `CREATE`/`ALTER TRIGGER` statement. Suppresses firing only for replication-agent and (per current docs) certain change-tracking-driven modifications — **not** suppressed for ordinary application `INSERT`/`UPDATE`/`DELETE`. Independent of `is_disabled`; both bits were simultaneously `1` on our test trigger, confirming they are orthogonal flags. |

`parent_class`/`name` together are documented by Microsoft as the compound key that uniquely identifies a trigger row (since DDL trigger names aren't schema-qualified the way DML names are).

## `sys.trigger_events` — event set and FIRST/LAST ordering

One row per (trigger, event type). Columns of interest:

| Column | Change-relevant? | Notes |
|---|---|---|
| `object_id`, `type`, `type_desc` (inherited from `sys.events`) | Yes | `type`/`type_desc` is the actual firing event: `1`/`INSERT`, `2`/`UPDATE`, `3`/`DELETE` for DML; DDL event codes (e.g. `CREATE_TABLE`) for DDL triggers. A multi-event trigger (`AFTER INSERT, UPDATE, DELETE`) produces **one row per event**, not one combined row — confirmed: our `trg_Orders_AfterAll` produced exactly 3 rows (types 1, 2, 3). |
| `is_first` | Yes | Set via `sp_settriggerorder @order = 'FIRST'`. Scoped **per (table, statement-type)** — not per trigger overall. Confirmed functionally: inserting a row fired triggers in the order `INSERT-FIRST → (unordered AfterAll) → INSERT-LAST`. |
| `is_last` | Yes | Set via `sp_settriggerorder @order = 'LAST'`. Only one trigger per table+event type may hold `is_last = 1` (and independently one may hold `is_first = 1`); attempting a second `FIRST`/`LAST` for the same table+event errors. |
| `event_group_type` / `event_group_type_desc` | Yes, when populated | Non-null only when the trigger was created `FOR`/`AFTER` an *event group* (e.g. `DDL_TABLE_EVENTS`) rather than individual event names — relevant to DDL triggers, not DML triggers. `NULL` for every DML trigger and for a DDL trigger created on a single named event (`CREATE_TABLE`), confirmed by direct probe. |

Important semantic note from Microsoft Learn (`Specify First and Last Triggers`): triggers that are neither FIRST nor LAST fire in **undefined** order relative to each other — this is not something a schema hash can or should try to make deterministic beyond capturing which trigger(s) hold the FIRST/LAST designation. Also: **`INSTEAD OF` triggers cannot be designated FIRST or LAST** — `sp_settriggerorder` errors if attempted, so `is_first`/`is_last` are only ever meaningfully `1` for `AFTER` triggers.

`ALTER TRIGGER` silently **drops** an existing FIRST/LAST designation back to `None` — a schema hash that captures `is_first`/`is_last` will correctly detect this as a change even though the trigger body may be textually identical.

## `sys.sql_modules` — body + SET options

Every trigger created has a corresponding row in `sys.sql_modules` (keyed by `object_id`), just like a stored procedure or view. Relevant columns:

| Column | Change-relevant? | Notes |
|---|---|---|
| `definition` | Yes | Full trigger body text (`CREATE TRIGGER ...`). Semantically this is the strongest signal of a real change but is also the most whitespace/formatting-sensitive; the library's convention elsewhere is to hash a server-computed digest rather than raw text for module bodies. |
| `uses_ansi_nulls` | Yes | Captured from the session's `SET ANSI_NULLS` at `CREATE`/`ALTER TRIGGER` time — **not** a property of the current session when queried. |
| `uses_quoted_identifier` | Yes | Same pattern, captured from `SET QUOTED_IDENTIFIER` at create time. **Edge case found by direct experimentation**: creating triggers via `sqlcmd` with no explicit `SET QUOTED_IDENTIFIER` statement produced `uses_quoted_identifier = 0` for all six triggers, even though `uses_ansi_nulls = 1`. Explicitly issuing `SET QUOTED_IDENTIFIER ON;` before a `CREATE TRIGGER` in the same session changed the captured value to `1` for that trigger, with an otherwise byte-identical body. This means two databases can have a trigger with **identical name, body text, and event set** but different `uses_quoted_identifier`/`uses_ansi_nulls` — a real, detectable difference in module fidelity that a hash ignoring these columns would miss. |
| `is_schema_bound` | Yes | `WITH SCHEMABINDING` — legal syntax for DML triggers (though rare in practice); `0` for all triggers tested here. |
| `execute_as_principal_id` | Yes | Non-null only when `EXECUTE AS` was specified on the trigger; `NULL` for all triggers tested here. |

## `sys.objects` — DML triggers are also visible here

Per Microsoft Learn (`sys.triggers` remarks): *"DML trigger names are schema-scoped and, therefore, are visible in sys.objects. DDL trigger names are scoped by the parent entity and are only visible in [sys.triggers]."* Confirmed experimentally: querying `sys.objects` with `type = 'TR'` returned all 6 DML triggers (with `schema_id` and `parent_object_id` populated), while the database-scoped DDL trigger probe did **not** appear in `sys.objects` at all — only in `sys.triggers` with `parent_class = 0`.

## Edge cases found by direct experimentation

1. **`is_disabled` is independent of body/definition/event-set changes.** `DISABLE TRIGGER` changes only the `is_disabled` bit; `sys.sql_modules.definition`, `sys.trigger_events` rows, and `create_date` are all untouched. Functionally confirmed the disabled trigger did not fire on an `UPDATE` that would otherwise have triggered it.
2. **`is_disabled` and `is_not_for_replication` are orthogonal** — a trigger can carry both simultaneously (confirmed on `trg_Orders_UpdateDisabledNFR`), and neither implies the other.
3. **FIRST/LAST ordering is per (table, statement type), not per trigger.** A single trigger created with a combined event list (`AFTER INSERT, UPDATE, DELETE`) can independently be FIRST for one event type and unordered (or LAST) for another — `sp_settriggerorder` takes an explicit `@stmttype` argument, and `sys.trigger_events` carries `is_first`/`is_last` per event row, not per trigger row.
4. **INSTEAD OF triggers cannot participate in FIRST/LAST ordering at all** (`sp_settriggerorder` errors on them) — confirmed against docs, not re-tested against the engine (the error is documented behavior, not ambiguous).
5. **An `INSTEAD OF INSERT` trigger on a view, whose body performs an `INSERT` against the underlying base table, causes the base table's own `AFTER INSERT` triggers (including FIRST/LAST-ordered ones) to fire in their normal order** — functionally confirmed: inserting through `audit_triggers.OrdersView` produced the same `INSERT-FIRST → AfterAll → INSERT-LAST` sequence as inserting directly into the base table. This matters for anyone reasoning about "what fires" from schema metadata alone — the view trigger and table triggers are independent catalog rows with no formal link other than the view trigger's body text referencing the base table.
6. **`uses_ansi_nulls`/`uses_quoted_identifier` are creation-session artifacts, not fixed properties of the trigger text** — same CREATE TRIGGER statement text produces different `sys.sql_modules` flag values depending on the session's `SET` options at the time of execution (see above). This is a real source of hash divergence between two databases whose triggers otherwise look textually identical, and matches the project's general pattern (noted for other module kinds) of module-level SET options needing explicit capture/normalization.
7. **Multi-event triggers expand to multiple `sys.trigger_events` rows**, one per event type — a trigger's "event set" is a set of rows to be collected and hashed together (e.g., as a sorted list), not a single scalar column.
8. **DML vs. DDL trigger disambiguation requires `parent_class`, not just presence in `sys.triggers`** — both share the view; extraction logic must filter `parent_class = 1` (OBJECT_OR_COLUMN) to get only DML triggers, or explicitly branch on `parent_class = 0` to separately handle database-scoped DDL triggers.
9. **Server-scoped DDL/logon triggers are invisible to any database-scoped query** — `sys.server_triggers`/`sys.server_trigger_events` are server-level catalog views (not per-database), structurally parallel to `sys.triggers`/`sys.trigger_events` (`parent_class = 100`/`SERVER`, and `is_first`/`is_last` present on `sys.server_trigger_events` too) but require a separate extraction pass scoped to the server rather than the target database, and represent state outside any single database's schema.

## Version gating

No SQL Server version gating was found specific to the trigger catalog views themselves for the columns exercised here (`sys.triggers`, `sys.trigger_events`, `sys.sql_modules`) — Microsoft Learn lists all queried columns as generally available across current SQL Server, Azure SQL Database, Azure SQL Managed Instance, and SQL database in Fabric, with no "Applies to: SQL Server 2016 and later" style annotation on any of the columns used here (unlike, e.g., temporal-table or masking columns elsewhere in this catalog family). `NOT FOR REPLICATION` on DML triggers, `sp_settriggerorder`, and `INSTEAD OF` triggers are long-standing T-SQL features predating SQL Server 2016, consistent with this library's SQL Server 2016 minimum-target baseline requiring no additional gating for trigger extraction specifically.

One structural point worth flagging for extraction logic rather than version gating: DDL triggers (database- and server-scoped) are **out of the DML-trigger extraction path entirely** — they require querying `parent_class = 0` rows of `sys.triggers` (database-scoped) and the wholly separate `sys.server_triggers`/`sys.server_trigger_events` views (server-scoped), neither of which was exercised by a `CREATE TRIGGER ... ON <table>` style query.

## Objects created for this audit (in `audit_db.audit_triggers`)

- `Orders` (base table), `OrderAudit` (audit sink table), `OrdersView` (view over `Orders`)
- `trg_Orders_AfterAll` — `AFTER INSERT, UPDATE, DELETE` (multi-event set)
- `trg_Orders_InsertFirst` / `trg_Orders_InsertLast` — two `AFTER INSERT` triggers with `sp_settriggerorder` FIRST/LAST
- `trg_Orders_AfterDelete` — plain `AFTER DELETE`
- `trg_OrdersView_InsteadOfInsert` — `INSTEAD OF INSERT` on the view
- `trg_Orders_UpdateDisabledNFR` — `AFTER UPDATE ... NOT FOR REPLICATION`, then `DISABLE TRIGGER`'d (both flags simultaneously set)

A database-scoped DDL trigger (`FOR CREATE_TABLE`) and a server-scoped DDL trigger (`ON ALL SERVER FOR CREATE_DATABASE`) were also created transiently to inspect `parent_class = 0`/`sys.server_triggers` behavior, then dropped; they are not left in the shared container.

## Coverage audit

- **`sys.sql_modules.is_schema_bound` not extracted or hashed for triggers**: `TriggerSchema` (`src/SchemaMetadata.cs`) has no corresponding field, and the trigger extraction query in `SchemaExtractor.ExtractTriggersAsync` does not select `m.is_schema_bound`. `WITH SCHEMABINDING` is legal (if rare) on `CREATE`/`ALTER TRIGGER` and changes real behavior (it locks the referenced table/columns against ALTERs that would break the trigger). Two triggers identical in name, parent, event set, flags, and body hash currently compare as identical whether or not one carries `WITH SCHEMABINDING`. Not documented as a deliberate exclusion anywhere in BUGS.md or `website/docs/what-gets-hashed.md`.
- **`sys.sql_modules.execute_as_principal_id` not extracted or hashed for triggers**: same file/method — no `ExecuteAsPrincipal`-style field on `TriggerSchema`, and the query does not select `m.execute_as_principal_id`. Non-null only when `EXECUTE AS <principal>` was specified; resolving it to a name would use the same id-to-name technique the extractor already applies elsewhere (`SCHEMA_NAME`/`OBJECT_NAME`-style lookups). Adding, removing, or changing a trigger's `EXECUTE AS` clause — a real change to its execution security context — currently does not change the hash. Not documented as a deliberate exclusion anywhere in BUGS.md or `website/docs/what-gets-hashed.md`.

All other columns confirmed relevant by the blind doc (`name`, `parent_class`-implied DML-only scoping, resolved `parent_id` → `ParentSchemaName`/`ParentName`, `is_disabled`, `is_instead_of_trigger`, `is_not_for_replication`, trigger body via `DefinitionHash`, `uses_ansi_nulls`, `uses_quoted_identifier`, and the full `sys.trigger_events` set including `type`/`is_first`/`is_last`) are extracted and hashed correctly in `SchemaExtractor.ExtractTriggersAsync`/`SchemaHashCalculator.HashTrigger`. `create_date`/`modify_date` are correctly excluded as runtime state. CLR triggers (`type = 'TA'`) and DDL/server-scoped triggers (`parent_class != 1`, `sys.server_triggers`) are correctly out of the DML-trigger extraction path and are already documented as out-of-scope object kinds in BUGS.md's "Out-of-scope object kinds" entry and `website/docs/what-gets-hashed.md`'s "Not yet captured" section.
