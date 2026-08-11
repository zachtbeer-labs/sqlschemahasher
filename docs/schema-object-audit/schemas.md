# Schemas

## Catalog view(s)

- **`sys.schemas`**, the sole catalog view exposing schema (namespace) objects. One row per database schema.
- **`sys.database_principals`**, must be joined to resolve `sys.schemas.principal_id` to an owner name/type; not itself a "schema" view but required for a human-readable owner.
- **`INFORMATION_SCHEMA.SCHEMATA`**, ANSI-standard equivalent; adds nothing `sys.schemas` doesn't already cover (it pre-joins the owner name and reports a constant `DEFAULT_CHARACTER_SET_NAME`). Microsoft's own docs on `SCHEMATA` explicitly warn: *"Don't use INFORMATION_SCHEMA views to determine the schema of an object... The only reliable way to find the schema of an object is to query the `sys.objects` catalog view."*

## sys.schemas columns

`sys.schemas` is unusually narrow: verified directly (`SELECT * FROM sys.schemas` returns exactly these 3 columns, confirmed against the Microsoft Learn "Schema catalog view - sys.schemas" page):

| Column | Type | Notes |
|---|---|---|
| `name` | `sysname` | Schema name. Unique within the database. |
| `schema_id` | `int` | ID of the schema, unique within the database. **Not stable across drop/recreate**, see edge case below. Non-deterministic across databases (allocated at creation time), same category as `object_id`: must be resolved to `name` at extraction time, never stored/compared directly. |
| `principal_id` | `int` | ID of the owning principal (a database principal, user, role, or application role). Must be resolved via `sys.database_principals.principal_id` to get a comparable name. |

That's the entire row shape: there is no `create_date`, `modify_date`, `is_ms_shipped`, or any flags column on `sys.schemas` itself. This means:

- **Change-detection surface for a schema is just two things: its name, and its resolved owner name.** There is nothing else on the row to hash.
- There is **no timestamp** to exclude as "runtime state": the view simply doesn't carry one (unlike `sys.objects`, which has `create_date`/`modify_date`).
- Owner comparison should be done by **resolved principal name**, not raw `principal_id`: `principal_id` values are allocated per-database and are not comparable across databases (verified: `dbo` is always `principal_id = 1` and matches `schema_id = 1` in every database checked, but a custom role/user's `principal_id` is assignment-order-dependent, e.g. our test role got `principal_id = 5` and test user got `principal_id = 6` purely because they were the 5th/6th principals created in that database).

## Fields relevant to detecting a schema change

For an *existing* schema, meaningful drift is limited to:
1. **Name** (`sys.schemas.name`): schema renamed.
2. **Owner** (`sys.schemas.principal_id` → resolved principal `name`/`type_desc` via `sys.database_principals`): changed via `ALTER AUTHORIZATION ON SCHEMA::<name> TO <principal>`.

Nothing else on `sys.schemas` varies. (Extended properties attached to the schema, covered separately below, are the one other axis of "metadata about the schema" that can drift.)

## Explicitly excluded (runtime state / non-deterministic)

- **`schema_id`**: allocation artifact, not portable across databases, and (see below) not even stable across a drop+recreate cycle in the *same* database. Must never be hashed directly; only used as a join key during extraction, same discipline the codebase already applies to `object_id`.
- **`principal_id`** (raw integer), same reasoning; resolve to the principal's `name` before hashing/comparing.

## Notable edge cases found by direct experimentation

All tests run against a SQL Server 2025 (17.0.1000.7) container in a scratch database (`audit_db`), under a custom schema `audit_schemas` plus sibling test schemas `audit_schemas_empty`, `audit_schemas_role_owned2`/`audit_schemas_role_owned`, `audit_schemas_user_owned`.

1. **An empty schema is *not* distinguishable from a populated one in `sys.schemas` itself.** The row shape (`name`, `schema_id`, `principal_id`) is identical whether the schema holds objects or not; `sys.schemas` is a pure namespace declaration, decoupled from its contents. To detect "does this schema have anything in it," a separate join against `sys.objects` is required:
   ```sql
   SELECT s.name, COUNT(o.object_id) AS object_count
   FROM sys.schemas s
   LEFT JOIN sys.objects o ON o.schema_id = s.schema_id AND o.is_ms_shipped = 0
   GROUP BY s.name;
   ```
   Verified: `audit_schemas` (holds a table) reported `object_count = 2` (table + its PK constraint, both rows in `sys.objects`); `audit_schemas_empty`, `audit_schemas_role_owned2`, `audit_schemas_user_owned` all reported `0`, yet all four have structurally identical `sys.schemas` rows. **Implication for a schema-hash calculator: a schema's own row contributes only name+owner to the hash; "is this schema empty" is not a property of the schema object itself; it falls out naturally from whether any child objects extract under it, and needs no separate flag.**

2. **`ALTER AUTHORIZATION` changes `principal_id` but leaves `schema_id` (and `name`) untouched.** Ran `ALTER AUTHORIZATION ON SCHEMA::audit_schemas_empty TO audit_schema_owner_user;`: `schema_id` stayed `10`, `principal_id` moved from `1` (dbo) to `6` (the new owner). Confirms ownership is a mutable, independently-trackable attribute distinct from schema identity.

3. **`schema_id` is reused after a `DROP SCHEMA`.** Created `audit_schemas_role_owned` (got `schema_id = 11`), dropped it, then created `audit_schemas_role_owned2`; it was assigned `schema_id = 11` again (the freed slot), not a fresh higher number. This is a stronger non-determinism guarantee than `object_id` reuse subtleties elsewhere in the codebase's design notes: `schema_id` absolutely must never be treated as a stable identity key, even within a single database over time, let alone across databases.

4. **Fixed schema_id/principal_id assignments are consistent across every database.** In both `master` and `audit_db`: `dbo` = `schema_id 1` / `principal_id 1`; `guest` = `2`/`2`; `INFORMATION_SCHEMA` = `3`/`3`; `sys` = `4`/`4`. User-created schemas get sequential IDs starting at 5 (in creation order); fixed database roles (`db_owner`, `db_datareader`, etc.) get IDs starting at `16384`. So `schema_id < 16384` roughly demarcates "user + built-in namespace schemas" vs. fixed-role schemas, but this is an unreliable heuristic for "is this a system schema"; better to filter by known names (`dbo`, `guest`, `INFORMATION_SCHEMA`, `sys`) if system-schema exclusion is ever needed, since there is no `is_ms_shipped` column on `sys.schemas` to filter on directly.

5. **`sys.schemas` has looser visibility than most catalog views.** Its Microsoft Learn permissions note reads: *"Requires membership in the **public** role"*, i.e. every schema in the database is visible to every user, unlike most catalog views (e.g. `sys.extended_properties`) which are filtered to securables the caller owns or has been granted permission on ("Metadata Visibility Configuration"). This means a low-privilege extraction connection can always enumerate the full schema list even if it can't see everything *inside* those schemas.

6. **A non-empty schema cannot be dropped.** `DROP SCHEMA audit_schemas` (which still owned `ExampleTable`) failed with `Msg 3729: Cannot drop schema 'audit_schemas' because it is being referenced by object 'ExampleTable'`. Not directly a hashing concern, but confirms there's no "orphaned"/dangling schema state to worry about: a schema row with dependents is protected by the engine.

7. **Schema name comparison follows the database's collation, not a fixed catalog collation.** Under the audit database's default collation (`SQL_Latin1_General_CP1_CI_AS`, case-insensitive), attempting `CREATE SCHEMA AUDIT_SCHEMAS` when `audit_schemas` already existed failed with `Msg 2714: There is already an object named 'AUDIT_SCHEMAS' in the database`. i.e., collided as a duplicate name. To confirm this is collation-driven rather than a hardcoded case-insensitive rule, a second scratch database was created with `COLLATE SQL_Latin1_General_CP1_CS_AS` (case-sensitive): there, `CREATE SCHEMA audit_case_test` and `CREATE SCHEMA Audit_Case_Test` **both succeeded** as two distinct schemas (`schema_id 5` and `6` respectively). **Implication:** whether two differently-cased schema names are "the same object" for change-detection purposes is a database-collation-dependent question, exactly parallel to the codebase's existing `SchemaHashOptions.ObjectNameComparer` concern for ignore-list matching, but here it's the *source database's own engine semantics* determining uniqueness, not a library option. If cross-database comparison is ever done between databases with different collations, schema-name matching semantics could disagree with the target database's own rules.

8. **Extended properties on a schema use `class = 3` (`class_desc = 'SCHEMA'`), with `major_id = schema_id` and `minor_id = 0`.** Verified directly: after `sp_addextendedproperty @level0type = 'SCHEMA', @level0name = 'audit_schemas', ...`, the resulting `sys.extended_properties` row showed `class = 3`, `class_desc = 'SCHEMA'`, `major_id = 9` (matching `audit_schemas`'s own `schema_id`), `minor_id = 0`. This confirms schema-level extended properties resolve via `sys.schemas.schema_id` as the join key, consistent with the pattern used for other object classes.

## Version gating

- `sys.schemas` (and schema-as-namespace generally) dates to SQL Server 2005's schema/user separation; it is present, unchanged in shape, on every version back through 2005, well below this project's SQL Server 2016 floor. **No version gating applies**: no columns were added/removed across versions per the Microsoft Learn "Applies to" matrix (SQL Server, Azure SQL Managed Instance, Azure Synapse Analytics, Analytics Platform System (PDW), Fabric SQL/Warehouse all show the same 3-column shape).
- Confirmed live against SQL Server 2025 (17.0.1000.7), the row shape (`name`, `schema_id`, `principal_id`) is identical to what Microsoft Learn documents for `sys.schemas` at `view=sql-server-ver17`, i.e. no SQL Server 2025-specific additions were found.

## Summary for a hash calculator

A `SchemaSchema` record (to avoid confusion with `SchemaMetadata` container schema of catalog concepts) needs only:
- `Name` (raw catalog name, resolve/compare per project convention)
- `OwnerName` (resolved from `principal_id` via `sys.database_principals`, not the raw ID)

No dedicated normalization enum bits appear necessary purely for this object type: there's no auto-generated-name pattern, no disabled/enabled flag, no clustering/collation variant to normalize away; the *only* documented axis of change is rename and re-ownership, both of which are substantive schema changes that should always be visible in a strict hash. (Whether the library wants an `IgnoreSchemaOwner` style option is a policy decision beyond what the catalog itself dictates: plenty of teams don't care who owns a schema for drift-detection purposes.)

## Coverage audit

- **Schema (namespace) objects are not extracted as their own catalog entity**: `sys.schemas` is never queried by `SchemaExtractor`, and `SchemaMetadata` has no corresponding list, unlike every other in-scope object kind (tables, stored procedures, table types, views, functions, triggers, sequences, synonyms). A schema's name surfaces only indirectly, as the `SchemaName` qualifier resolved via `SCHEMA_NAME(...)` on the objects extracted under it. An empty schema, or one whose entire contents are excluded via `ObjectNamesToIgnore`/`SchemaFilter`, therefore has no representation anywhere in the extracted metadata: creating, dropping, or renaming such a schema does not change the hash. Not documented as a deliberate exclusion in `BUGS.md` or `website/docs/what-gets-hashed.md` prior to this audit (both checked in full); the project's own "in scope" object list (CLAUDE.md, `what-gets-hashed.md`) never mentions schemas as a hashable object kind at all, only as a scoping mechanism (`SchemaFilter`).
- **Schema ownership (`ALTER AUTHORIZATION ON SCHEMA`) is not captured**: `sys.schemas.principal_id` (resolvable to a principal name via `sys.database_principals`) is not extracted or hashed for any schema. `ALTER AUTHORIZATION ON SCHEMA::<name> TO <principal>` is a real, DDL-driven ownership change that leaves `schema_id`/`name` untouched (confirmed above), so it is not incidentally captured by anything else in the hash. This is a consequence of the same root cause as the first gap (no `sys.schemas` extraction exists at all), but is called out separately because it is a distinct catalog field (`principal_id` vs. `name`) that would remain uncaptured even if a future fix added the schema's name to the hash without also resolving its owner.
