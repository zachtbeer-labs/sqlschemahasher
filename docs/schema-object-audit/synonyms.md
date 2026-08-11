# Synonyms

## Catalog view

**`sys.synonyms`** is the only catalog view for this object type. It is a *derived* view over `sys.objects` (rows where `type = 'SN'`, `type_desc = 'SYNONYM'`): it returns every column inherited from `sys.objects` plus one synonym-specific column.

| Column | Type | Notes |
|---|---|---|
| `base_object_name` | `nvarchar(1035)` | "Fully quoted name of the object to which the user of this synonym is redirected." Per Microsoft Learn (`sys.synonyms (Transact-SQL)`), this is the *only* column `sys.synonyms` adds beyond `sys.objects`. |

Relevant inherited `sys.objects` columns:

| Column | Relevant to schema-change detection? | Notes |
|---|---|---|
| `name` | Yes | Synonym name. |
| `schema_id` → resolve to schema name | Yes | Synonyms are schema-scoped objects. |
| `object_id` | **No — exclude** | Not stable: verified experimentally that `DROP SYNONYM` + `CREATE SYNONYM` (the only way to change a synonym — see below) assigns a brand-new `object_id` even when name and definition are unchanged. Non-deterministic across databases/recreates, same rationale the codebase already applies to every other object kind. |
| `principal_id` | Yes (conditionally) | `NULL` unless an explicit owner was set via `ALTER AUTHORIZATION ... TO`, in which case it's the owning principal's `principal_id`. Same pattern as tables/views/procs — worth resolving to name when non-null, since it's a real ownership override, not a system artifact. Verified: freshly created synonym has `principal_id = NULL`; after `ALTER AUTHORIZATION ON audit_synonyms.SynRealTable TO dbo`, `principal_id` became `1` (dbo). |
| `create_date` / `modify_date` | **No — exclude (runtime state)** | Timestamps, not schema content. Also verified `create_date == modify_date` always holds for a synonym and can never diverge — see "No ALTER SYNONYM" below. |
| `is_ms_shipped` | Situational | `0` for all user-created synonyms tested; would be `1` only for system-shipped ones, which extraction presumably already filters the way it does for other object kinds. |
| `is_published` / `is_schema_published` | **No — exclude** | Merge-replication publication flags, not schema definition; observed `0`/`0` in this environment (no replication configured). Same category as other objects' replication flags — configuration/runtime state, not structure. |
| `parent_object_id` | Not useful | Always `0` for a synonym — unlike triggers, a synonym is not "owned" by another object; it is a top-level schema-scoped object in its own right. No parent-exclusion cascade needed. |

There is no `sys.system_internals_synonyms` or additional synonym-only detail view; `sys.synonyms` is the complete surface.

## Fields relevant to detecting a schema change

For deterministic-hash purposes, the meaningful, non-runtime fields are:

1. **Schema-qualified name** (`schema_id` resolved to name + `name`).
2. **`base_object_name`** — the full redirect target, verbatim as stored (see below). This is the entire "definition" of a synonym; there is no separate body/definition column, no columns, no constraints, no indexes.
3. **Owner override**, if the codebase tracks explicit ownership elsewhere (`principal_id` resolved to a principal name, when non-null) — optional/consistency question for whoever wires this up, since it mirrors how other object types are (or aren't) handling explicit `ALTER AUTHORIZATION`.

Everything else on the row (`object_id`, `create_date`, `modify_date`, `is_published`, `is_schema_published`) is either non-deterministic identity plumbing or genuine runtime/replication state and should be excluded, consistent with how the rest of this codebase already treats catalog `object_id` and timestamps.

## `base_object_name` is stored verbatim and unvalidated — experimentally confirmed

Created six synonyms in a fresh `audit_synonyms` schema, all of which succeeded with **no error**, including ones targeting objects that do not exist:

```sql
CREATE SYNONYM audit_synonyms.SynRealTable   FOR audit_synonyms.RealTable;              -- real target
CREATE SYNONYM audit_synonyms.SynSysTables   FOR sys.tables;                            -- real, cross-schema
CREATE SYNONYM audit_synonyms.SynNonExistent FOR audit_synonyms.NoSuchObjectAtAll;       -- target doesn't exist
CREATE SYNONYM audit_synonyms.SynFourPart    FOR [NoSuchLinkedServer].[SomeRemoteDb].[dbo].[RemoteTable]; -- linked server doesn't exist
CREATE SYNONYM audit_synonyms.SynOnePart     FOR RealTable;                             -- unqualified 1-part name
CREATE SYNONYM audit_synonyms.SynChained     FOR audit_synonyms.SynRealTable;           -- synonym-to-synonym
```

Resulting `sys.synonyms.base_object_name` values:

| Synonym | `base_object_name` |
|---|---|
| `SynRealTable` | `[audit_synonyms].[RealTable]` |
| `SynSysTables` | `[sys].[tables]` |
| `SynNonExistent` | `[audit_synonyms].[NoSuchObjectAtAll]` |
| `SynFourPart` | `[NoSuchLinkedServer].[SomeRemoteDb].[dbo].[RemoteTable]` |
| `SynOnePart` | `[RealTable]` (no schema added — stored exactly as typed) |
| `SynChained` | `[audit_synonyms].[SynRealTable]` |

Key findings on the "verbatim" behavior, more precise than just "stored as typed":

- **Existence is never checked at CREATE time.** `SynNonExistent` and `SynFourPart` (nonexistent linked server) were created without error. Per Microsoft Learn ("Synonyms (Database Engine)"): *"The binding between a synonym and its base object is by name only. All existence, type, and permissions checking on the base object is deferred until run time."*
- **Casing is preserved exactly as typed, not normalized to the target's actual casing.** `CREATE SYNONYM audit_synonyms.SynLowerCase FOR audit_synonyms.realtable;` (lowercase `realtable`, though the real table is `RealTable`) stored `base_object_name = [audit_synonyms].[realtable]` — lowercase preserved verbatim, resolved case-insensitively only at reference time.
- **Whitespace between name parts is normalized away**, and **brackets are always added** around each identifier part regardless of whether the user supplied them: `CREATE SYNONYM audit_synonyms.SynSpaced FOR   audit_synonyms  .  RealTable  ;` stored as the canonical `[audit_synonyms].[RealTable]` (no embedded spaces, brackets present) — same result whether or not the source SQL used brackets or extra whitespace. So "verbatim" means *verbatim per-part text and casing*, canonicalized only in delimiter/quoting form.
- **Number of parts is preserved as typed** — a 1-part name stays 1-part (`[RealTable]`, no schema silently added); this matters because resolution of an unqualified target name happens at query time using ordinary name-resolution rules, not at CREATE time, so the same synonym could resolve to different objects depending on caller context (confirmed: querying `SynOnePart` succeeded and returned 0 rows against the table, without SQL Server ever rewriting `base_object_name` to be schema-qualified).
- **A synonym can be created pointing at another synonym**, but Microsoft Learn states *"A synonym cannot be the base object for another synonym"* — that constraint is enforced only at **use time**, not at CREATE time. Verified: `CREATE SYNONYM SynChained FOR audit_synonyms.SynRealTable` succeeded silently; `SELECT * FROM audit_synonyms.SynChained` then failed with:
  ```
  Msg 470, Level 16, State 1
  The synonym "audit_synonyms.SynChained" referenced synonym "audit_synonyms.SynRealTable". Synonym chaining is not allowed.
  ```
  This is an important edge case for schema hashing: the hash must faithfully capture `base_object_name` even when it points to a synonym, a nonexistent object, or an unreachable linked server — none of that is a create-time error, and the hash calculator has no basis (nor business) to "validate" it.
- **Querying a synonym whose target genuinely doesn't exist** fails only at run time too:
  ```
  Msg 5313, Level 16, State 1
  Synonym 'audit_synonyms.SynNonExistent' refers to an invalid object.
  ```

## No `ALTER SYNONYM` statement

Confirmed by direct experimentation: T-SQL has **no `ALTER SYNONYM`** statement.

```sql
ALTER SYNONYM audit_synonyms.SynRealTable FOR audit_synonyms.RealTable;
-- Msg 102, Level 15, State 1: Incorrect syntax near 'SYNONYM'.
```

The only way to change a synonym's target is `DROP SYNONYM` followed by `CREATE SYNONYM`. Consequence verified directly: this assigns the synonym a **new `object_id`** even when name and `base_object_name` end up identical to before —

```
-- before: object_id = 1431676148
DROP SYNONYM audit_synonyms.SynRealTable;
CREATE SYNONYM audit_synonyms.SynRealTable FOR audit_synonyms.RealTable;
-- after:  object_id = 1799677459
```

This reinforces why `object_id` must be excluded from any deterministic hash of a synonym (and why `create_date`/`modify_date` always coincide for a synonym — there is no in-place modification path that would ever make them diverge).

## Namespace and ownership

- Verified synonyms share the **schema-scoped object namespace** with tables/views/procs/etc.: attempting `CREATE SYNONYM audit_synonyms.RealTable FOR ...` (colliding with the existing table name) failed with `Msg 2714: There is already an object named 'audit_synonyms.RealTable' in the database.` Consistent with Microsoft Learn: *"A synonym belongs to a schema, and like other objects in a schema, the name of a synonym must be unique."*
- `parent_object_id` is always `0` — a synonym is never "owned" by another object the way a trigger is owned by its table, so there's no parent-exclusion cascade to replicate for this object type.
- Ownership: `principal_id` is `NULL` by default (owner = schema owner) and only becomes non-null after an explicit `ALTER AUTHORIZATION ... TO <principal>` — verified directly, going from `NULL` to `1` (`dbo`) after the ALTER.

## Extended properties

Synonyms support extended properties as **class 1 (`OBJECT_OR_COLUMN`)**, with `minor_id = 0` since a synonym has no columns of its own to attach column-level properties to. Verified with:

```sql
EXEC sys.sp_addextendedproperty @name = N'MS_Description', @value = N'test synonym description',
  @level0type = N'SCHEMA', @level0name = N'audit_synonyms',
  @level1type = N'SYNONYM', @level1name = N'SynRealTable';
```
→ `sys.extended_properties` row: `class = 1, class_desc = 'OBJECT_OR_COLUMN', major_id = <synonym object_id>, minor_id = 0`. Same `(class, major_id, minor_id)` shape as any other schema-scoped object without columns (e.g. a synonym's row looks structurally identical to a stored-procedure-level extended property).

## Allowed base-object kinds (per Microsoft Learn, not independently exhaustively tested)

Per "Synonyms (Database Engine)": a synonym may target a table (including local/global temp tables), view, SQL or CLR scalar function, SQL inline table-valued function, CLR table-valued function, SQL or CLR stored procedure, replication-filter-procedure, or CLR aggregate — but note: *"a synonym cannot reference a user-defined aggregate function"* (contradicts the aggregate-function entry in the same list — documentation is internally inconsistent here; not independently resolved by experiment since CLR modules are already out of scope for this library per its own stated scope). **Four-part names are explicitly unsupported for function base objects** ("Four-part names for function base objects are not supported") — this is a target-shape constraint, not something reflected in `base_object_name`'s storage (it's just unenforced/unchecked either way per the "no validation at create time" finding above).

## Version gating

No version gating was found or observed for `sys.synonyms` itself: `CREATE SYNONYM`/`sys.synonyms` has existed since SQL Server 2005 and the catalog view's column list (`base_object_name`, `nvarchar(1035)`) is identical between SQL Server 2016+ and the SQL Server 2025 instance used for this audit — confirmed by direct query against a live SQL Server 2025 (17.0.1000.7) container, and the Microsoft Learn reference page for `sys.synonyms` shows no version-specific note or column-addition history. The "Applies to" band on that page lists SQL Server, Azure SQL Database, Azure SQL Managed Instance, Azure Synapse Analytics, and the newer Microsoft Fabric SQL surfaces (warehouse / SQL analytics endpoint / SQL database in Fabric) as all currently supported — nothing SQL-Server-2016-and-later-specific to gate on, consistent with this library's general 2016 minimum being driven by other object types, not synonyms.

## Summary of exclusion list (fields to leave out of a deterministic hash)

- `object_id` — regenerated on every drop+recreate, not portable across databases.
- `create_date`, `modify_date` — always equal for a synonym; pure timestamp/runtime state regardless.
- `is_published`, `is_schema_published` — replication configuration/runtime flags, not schema structure.
- `is_ms_shipped` — presumably already handled by the same system-object filtering applied to other object kinds.

## Coverage audit

- **`sys.objects.principal_id` (explicit ownership) not extracted or hashed for synonyms**: `SynonymSchema` (`src/SchemaMetadata.cs`) has three fields (`SchemaName`, `Name`, `BaseObjectName`) with no `Owner`/`PrincipalName`-style field, and the synonym extraction query in `SchemaExtractor.ExtractSynonymsAsync` selects only `SCHEMA_NAME(sn.schema_id)`, `sn.name`, and `sn.base_object_name`; it does not select `sn.principal_id`. `principal_id` is `NULL` unless an explicit owner was set via `ALTER AUTHORIZATION ON <synonym> TO <principal>`, in which case it holds the owning principal's id: a real, DDL-driven ownership override, not a system artifact or runtime state. Resolving it to a principal name would use the same id-to-name technique already used elsewhere in the extractor (`SCHEMA_NAME`/`OBJECT_NAME`-style lookups). Currently, running `ALTER AUTHORIZATION` on a synonym to reassign its owner does not change the hash. Not documented as a deliberate exclusion anywhere in BUGS.md or `website/docs/what-gets-hashed.md` (checked both in full); this gap is not unique to synonyms, since no object kind in the codebase currently captures `sys.objects.principal_id` at all, so this is filed as the synonym instance of a broader, currently untracked gap.

All other fields identified by the blind doc as relevant (schema-qualified name via `SCHEMA_NAME(sn.schema_id)` + `sn.name`, and `base_object_name` hashed verbatim including multi-part/unqualified/nonexistent-target text) are extracted and hashed correctly in `SchemaExtractor.ExtractSynonymsAsync`/`SchemaHashCalculator.HashSynonym`, sorted deterministically by `(SchemaName, Name)`. `object_id`, `create_date`, `modify_date`, `is_published`, `is_schema_published`, and `is_ms_shipped` are correctly excluded (never selected by the extraction query). `ObjectNamesToIgnore`/`SchemaFilter` scoping applies to synonyms the same as every other object kind (`IsIgnored` filters by bare/schema-qualified name before the rows are materialized). Extended properties on a synonym (`sys.extended_properties` class 1, `minor_id = 0`) are correctly included: the extended-property query's class-1 object-kind filter includes `'SN'`.
