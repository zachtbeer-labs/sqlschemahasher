---
id: what-gets-hashed
title: What Gets Hashed
sidebar_position: 3
---

# What Gets Hashed

The hash covers the schema objects an ordinary application database is built from. Catalog `object_id`/`schema_id` values are resolved to names at extraction and never stored (they are non-deterministic across databases), and every object is schema-qualified.

## In scope

- **Tables** — Schema name, table name, columns (name, type, precision, nullability, string collation, and — for computed columns — the formula and whether it is `PERSISTED`), indexes (including key sort order, INCLUDE columns, and filtered-index predicates), constraints (foreign keys include the referenced table/column and the `ON DELETE`/`ON UPDATE` referential actions), identity columns.
- **Stored procedures** — Schema name, procedure name, parameters (including `OUTPUT` direction and the `READONLY` flag), and a hash of the procedure body (detects logic changes).
- **User-defined table types** — Schema name, type name, columns (including string collation and computed column formulas).
- **Views** — Schema name, view name, a hash of the view body, CREATE-time SET options, and — for a schemabound indexed view — its indexes. View columns are deliberately not extracted (the definition hash is the view's identity; a `SELECT *` view's column metadata goes stale until `sp_refreshview` runs, which would otherwise inject spurious diffs).
- **Functions** — Schema name, function name, the raw `type_desc` (distinguishing scalar / inline table-valued / multi-statement table-valued functions even when body text is excluded), parameters (a scalar function's return type arrives as its own parameter row), and a hash of the function body.
- **Triggers** — Schema name, trigger name, parent table/view, disabled/`INSTEAD OF`/`NOT FOR REPLICATION` flags, the DML event set together with `FIRST`/`LAST` ordering (set out-of-band via `sp_settriggerorder`), and a hash of the trigger body. Only object (DML) triggers are captured.
- **Sequences** — Schema name, sequence name, data type, precision, start value, increment, min/max bounds, cycling, and cache size — **not** the current value, which is runtime state that advances on every `NEXT VALUE FOR` and would make the hash unstable across otherwise-identical databases.
- **Synonyms** — Schema name, synonym name, and the target object name exactly as the catalog stores it (synonym targets are not validated or resolved at CREATE time).
- **Extended properties** — Name, value, and value base type of every `sys.extended_properties` entry (e.g. `MS_Description`) scoped to the database, a schema, an in-scope object, a column, a parameter, an index, or a user-defined table type. Targets are resolved to names, and a property follows its owner out of the hash when the owner is excluded. Set `IgnoreExtendedProperties` to leave them out entirely. Properties on constraint objects are not captured (system-generated constraint names are not deterministic across databases).
- **Alias scalar types** — A column, parameter, or sequence typed with an alias scalar type (`CREATE TYPE dbo.OrderTotal FROM DECIMAL(9,2) NOT NULL`) hashes both the schema-qualified alias name and its underlying base type/length/precision/scale/nullability, so dropping and recreating the alias with a different base type changes the hash.

## Excluded objects

When `IgnoreSysDiagramObjects` is enabled (it is in every preset, including `Default`), SSMS database diagram objects are excluded from hashing:

- the `sysdiagrams` table and related diagram helpers: `fn_diagramobjects`, `sp_alterdiagram`, `sp_creatediagram`, `sp_dropdiagram`, `sp_helpdiagramdefinition`, `sp_helpdiagrams`, `sp_renamediagram`.

This composes additively with any names you put in `ObjectNamesToIgnore`. A bare `new SchemaHashOptions()` excludes nothing.

## Not yet captured

The following are **not** read, so adding, altering, or dropping them does not change the hash:

- **CLR modules and scalar CLR UDTs** — CLR functions/triggers/aggregates and scalar CLR user-defined types are out of scope; hashing a compiled binary body is a fundamentally different extraction shape than everything else this library captures.
- **DDL and server-scoped triggers** — only object (DML) triggers on tables and views are captured; database-scoped DDL triggers and server-scoped triggers are not.
- **Partitioning and filegroup/data-space placement** — moving a table to a different filegroup, or repartitioning it, does not change the hash.
- **Encrypted module bodies** — two different `WITH ENCRYPTION` modules (procedures, views, functions, or triggers) with identical signatures collide on a shared `<encrypted>` sentinel — inherent, since the body is unreadable once encrypted.

See the [FAQ](./faq.md) for the reasoning behind these scope decisions and other frequently-asked design questions.

## Known limitation: definition-text rendering drift

CHECK constraint, DEFAULT constraint, computed-column, and filtered-index definitions are hashed as SQL Server renders them. Different SQL Server major versions can render the same expression differently (spacing, parenthesization, casing of built-in functions), which can produce a spurious `Different` comparison across server versions even though nothing semantically changed. Compare hashes taken from the same SQL Server major version to avoid this. See [BUGS.md](https://github.com/zachtbeer-labs/sqlschemahasher/blob/main/BUGS.md#definition-text-rendering-drift) for details — this is intentionally not normalized in v2.
