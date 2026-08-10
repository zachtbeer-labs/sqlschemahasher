# Alias and CLR User-Defined Types

Scope: `sys.types` rows where `is_user_defined = 1` — both **alias scalar types** (`CREATE TYPE ... FROM <base_type>`) and **CLR user-defined types** (`CREATE TYPE ... EXTERNAL NAME assembly.class`, requiring an assembly registered with `CREATE ASSEMBLY`). This excludes user-defined **table types** (`is_table_type = 1`), which are a distinct object shape.

## Catalog views

| View | Role |
|---|---|
| `sys.types` | One row per type (system and user-defined). The primary detection surface: shape, nullability default, and — for alias types — the effective base type via `system_type_id`/`max_length`/`precision`/`scale`/`collation_name`. |
| `sys.assembly_types` | One row **per CLR type only** (inherits/extends `sys.types`, keyed by `user_type_id`). Identifies the backing assembly and class, plus binary-ordering/fixed-length facts. Empty for alias types. |
| `sys.assemblies` | One row per registered assembly (the CLR type's container). Needed to resolve `assembly_id` → name, permission set, and (indirectly, via `sys.assembly_files`) the implementation bytes. |
| `sys.assembly_files` | Holds the actual assembly binary (`content`, `varbinary(max)`) plus a `name` that reflects the **embedded PE module name**, not necessarily the `CREATE ASSEMBLY` name (see edge case below). Server-side `HASHBYTES('SHA2_256', content)` gives a deterministic fingerprint of the CLR implementation without transferring the bytes. |
| `sys.extended_properties` (class = 6, `TYPE`) | Extended properties targeting a user-defined type (alias or CLR — see edge case below), keyed by `major_id = sys.types.user_type_id`, `minor_id = 0`. |

For alias types, `sys.assembly_types` is simply absent — presence/absence of a matching `sys.assembly_types` row (or equivalently, `sys.types.is_assembly_type`) is the discriminator between the two kinds.

## Relevant columns / flags (schema-change detection)

### `sys.types` (both kinds)

| Column | Meaning | Notes |
|---|---|---|
| `name`, `schema_id` | Identity | Schema-qualify via `SCHEMA_NAME(schema_id)`. |
| `user_type_id` | Stable per-database type ID | Non-deterministic across databases (like other catalog IDs) — resolve to name, don't store the raw ID. |
| `system_type_id` | Backing system type ID for an **alias** type (e.g. 167 = varchar, 106 = decimal). For a **CLR** type this is always the fixed sentinel value **240**, shared by every CLR-backed type in the instance (both user CLR UDTs and the built-in system CLR types `hierarchyid`/`geometry`/`geography`, which have `is_user_defined = 0`). `system_type_id` alone cannot distinguish CLR UDTs from each other or give a "base type" — that only exists for alias types. |
| `is_user_defined` | `1` for both alias and CLR UDTs; `0` for system types (including the built-in CLR types). Scoping flag for this whole audit topic. |
| `is_assembly_type` | `1` = CLR type, `0` = alias type. The kind discriminator. |
| `is_table_type` | Always `0` for alias/CLR scalar types (`1` only for `CREATE TYPE ... AS TABLE`). Confirms out of scope for this topic. |
| `max_length` | Bytes. For an alias type this is the effective storage length of the base type (`-1` for the `MAX` variants). For a CLR type it is the **native-format serialized size** computed by the CLR host from the struct's fields (in the audit's `PointType` — one `bool` + two `int32` — this came out to `9`), *not* something the CREATE TYPE statement declares. |
| `precision`, `scale` | Populated only for a numeric-based alias type; `0` for everything else including CLR types. |
| `collation_name` | Populated only for a character-based alias type (inherits the **database's default collation** — see edge case below); `NULL` for non-character alias types and always `NULL` for CLR types. |
| `is_nullable` | For an alias type, the default nullability declared in `CREATE TYPE ... NULL/NOT NULL` (or `NULL` if omitted — confirmed by experiment). For a CLR type this is **always `1`**, because `CREATE TYPE ... EXTERNAL NAME` has no `NULL`/`NOT NULL` clause at all — nullability for CLR-typed columns is controlled entirely at the column level. |
| `default_object_id`, `rule_object_id` | Legacy `sp_bindefault`/`sp_bindrule` bindings (pre-`CREATE TYPE` mechanism). `0` when unused; both were `0` for every type created via `CREATE TYPE` in this audit. |

Note: a column's own `sys.columns.is_nullable` can **override** an alias type's declared nullability (confirmed by experiment — a `NOT NULL` alias type used in a column declared `NULL` keeps the column nullable). The type's `is_nullable` is only a *default* applied when the column doesn't specify its own; the column is the source of truth for a specific table.

### `sys.assembly_types` (CLR types only)

| Column | Meaning | Notes |
|---|---|---|
| `assembly_id` | FK to `sys.assemblies` | Resolve to the assembly name; join key. |
| `assembly_class` | The class name inside the assembly implementing the type | Renaming the C# class (keeping the same DLL/assembly name) changes this without changing `sys.assemblies`. |
| `is_binary_ordered` | `1` if byte-comparison order equals the type's comparison-operator order | `0` for the audit's native-format `PointType` (no `IComparable`/`IsByteOrdered` declared). Relevant to index/sort semantics if the type is ever used as a key. |
| `is_fixed_length` | `1` if the serialized length always equals `max_length` | `1` for `Format.Native` fixed structs; would be `0` for `Format.UserDefined` (`IBinarySerialize`) types with variable-length serialization. |
| `prog_id` | COM ProgID exposure | `NULL` unless explicitly exposed to COM (not the common case; `NULL` in the audit). |
| `assembly_qualified_name` | Full CLR type name string (`ClassName, AssemblyName, Version=..., Culture=..., PublicKeyToken=...`) | Changes if the class name, assembly (simple) name, or **assembly version** changes. **Caution**: if the C# project doesn't declare `[assembly: AssemblyVersion(...)]`, the compiler defaults to `Version=0.0.0.0` and this string does **not** change even when the implementation is edited and redeployed — see edge case below. |

### `sys.assemblies` (backing the CLR type)

| Column | Meaning | Notes |
|---|---|---|
| `name` | Assembly name given in `CREATE ASSEMBLY` | Independent of the embedded PE module name (see edge case). |
| `permission_set_desc` | `SAFE_ACCESS` / `EXTERNAL_ACCESS` / `UNSAFE_ACCESS` | A real, deployable schema-affecting setting — changing the permission set changes what the assembly is allowed to do. |
| `is_visible` | Whether T-SQL entry points are exposed vs. an internal helper assembly | Schema-relevant for assemblies backing functions/procs/triggers; for a pure UDT-backing assembly it's `1`. |
| `clr_name` | Canonical CLR identity string (name, version, culture, public key, architecture) | Same versioning caveat as `assembly_qualified_name` above. |
| `create_date`, `modify_date` | **Runtime state — exclude.** Timestamps of registration/last change; not deterministic across environments/deployments. |
| `principal_id` | Owner if not the schema owner | Rarely set; parallels the same field on `sys.types`. |

### `sys.assembly_files` (implementation content)

| Column | Meaning | Notes |
|---|---|---|
| `content` | The actual assembly bytes (`varbinary(max)`) | The one column that truly captures "did the CLR implementation change" — best consumed via a server-side `HASHBYTES('SHA2_256', content)` rather than transferring the bytes, mirroring how module bodies are typically hashed. |
| `name` | **Not** the `CREATE ASSEMBLY` name — it's the module name embedded in the PE file at compile time. In the audit, `CREATE ASSEMBLY [PointTypeAssembly] FROM 0x...` produced a `sys.assembly_files.name` of `PointType` (the compiler's output filename, `PointType.dll`, minus extension), while `sys.assemblies.name` remained `PointTypeAssembly`. See edge case below. |

### `sys.extended_properties` (class 6, `TYPE`)

Same shape as extended properties on any other class: `class_desc = 'TYPE'`, `major_id` = the type's `user_type_id`, `minor_id = 0`, plus `name`/`value`. Confirmed by direct experiment that **both** an alias type and a CLR type accept an extended property under this same class — `sp_addextendedproperty @level1type = N'TYPE', @level1name = N'Money2'` and `..., @level1name = N'PointType'` both succeeded and both surfaced with `class = 6`. Class 6 is not table-type-exclusive; it is the generic "TYPE" class for any user-defined type (alias, CLR, or table).

## Notable edge cases (found by direct experimentation)

1. **Alias-of-alias is rejected.** `CREATE TYPE X FROM sysname` (itself a built-in alias over `nvarchar(128)`) fails: *"The base type 'sysname' is not a valid base type for the alias data type."* An alias type's base must be a genuine system type, not another alias.
2. **`MAX` length variants are allowed for `CREATE TYPE` alias types** (`varchar(max)`, by extension `nvarchar(max)`/`varbinary(max)`), producing `max_length = -1` — this is a deliberate divergence from the legacy `sp_addtype` (deprecated), whose documentation explicitly disallows `timestamp`, `table`, `xml`, and the `MAX` variants as base types. `CREATE TYPE` only disallows `xml`, `timestamp`/`rowversion`, and `table` (confirmed by experiment: both `FROM xml` and `FROM rowversion` fail with base-type errors); `sql_variant` **is** a valid base for `CREATE TYPE` (confirmed by experiment — it succeeded, `system_type_id = 98`, `max_length = 8016`) despite being disallowed for legacy `sp_addtype`.
3. **No `ALTER TYPE` statement exists at all.** `ALTER TYPE ...` is a syntax error ("Incorrect syntax near 'TYPE'") for both alias and CLR types — any change to an alias type's base/length/precision/scale/nullability, or to a CLR type's backing assembly identity, requires `DROP TYPE` + `CREATE TYPE` (for alias types) or (for CLR types) can instead go through `ALTER ASSEMBLY` to swap the implementation bytes while keeping the type identity, or `DROP TYPE`/`DROP ASSEMBLY`/recreate for a structural change.
4. **Column-level nullability overrides the alias type's declared nullability**, but not its length/precision/scale (per Microsoft Learn's `CREATE TABLE` docs: "The `NULL` or `NOT NULL` assignment for an alias data type can be overridden... However, the length specification can't be changed"). Confirmed: a `NOT NULL` alias type (`Money2`) used in a column declared plain (nullable) yields `sys.columns.is_nullable = 1` for that column while `sys.types.is_nullable` for `Money2` itself stays `0`.
5. **Dropping a type in use is blocked with a clear dependency error** ("Cannot drop type '...' because it is being referenced by object '...'"), and symmetrically **`DROP ASSEMBLY` is blocked while a CLR type built from it still exists** ("DROP ASSEMBLY failed because '...' is referenced by CLR type '...'"). The correct teardown order for a CLR type is `DROP TYPE` before `DROP ASSEMBLY`.
6. **No explicit `COLLATE` clause is accepted in `CREATE TYPE`** for a character-based alias type — attempting `CREATE TYPE X FROM varchar(50) COLLATE French_CI_AS NOT NULL` is a syntax error. A character-based alias type's `collation_name` is always the **database's default collation** at creation time (matches Microsoft Learn's `sp_addtype` remarks: "Alias data types inherit the default collation of the database... Changing the default collation of the database applies only to new columns and variables of the type"). This means an alias type's effective collation is an environment-dependent value, not something recorded explicitly in the `CREATE TYPE` statement — worth flagging for any collation-normalization behavior applied elsewhere to columns.
7. **`system_type_id = 240` is a shared sentinel across *all* CLR-backed types**, not just user ones — it's also the `system_type_id` for the built-in system CLR types `hierarchyid` (240/128), `geometry` (240/129), `geography` (240/130), which have `is_user_defined = 0`. Filtering strictly on `is_user_defined = 1 AND is_assembly_type = 1` is required to isolate genuine user CLR UDTs from these built-ins.
8. **`sys.assembly_files.name` reflects the compiled module's embedded filename, not the `CREATE ASSEMBLY` name.** In the audit, `CREATE ASSEMBLY [PointTypeAssembly] FROM 0x...` (compiled from `PointType.cs` → `PointType.dll`) produced `sys.assemblies.name = 'PointTypeAssembly'` but `sys.assembly_files.name = 'PointType'`. A schema-hash implementation keying off the wrong one of these two names could produce spurious diffs when an assembly is renamed at the `CREATE ASSEMBLY` level without recompiling, or vice versa.
9. **`clr_name`/`assembly_qualified_name` embed an assembly version that defaults to `0.0.0.0` and doesn't auto-increment.** Without an explicit `[assembly: AssemblyVersion(...)]`, every recompilation of the same source still reports `Version=0.0.0.0` in both `sys.assemblies.clr_name` and `sys.assembly_types.assembly_qualified_name`. A real implementation change (different IL) is therefore **not visible** through those two string columns alone — only `sys.assembly_files.content` (or its hash) reliably captures a CLR UDT's implementation change; the version-string columns can silently stay identical across a functional change.
10. **`sys.assembly_types` exists only for CLR types; there is no analogous per-alias-type detail view** — an alias type's "definition" is fully captured by the parent `sys.types` row (`system_type_id` + `max_length`/`precision`/`scale`/`collation_name`/`is_nullable`); there's nothing more to join.
11. **`clr enabled` was off by default** in this SQL Server 2025 instance and had to be turned on via `sp_configure` before any CLR object (including a CLR UDT) could be created — an operational/deployment prerequisite, not a schema-hash-relevant catalog value itself (it's a server-level configuration setting, out of a per-database schema hash's scope), but worth knowing if reproducing this audit against a fresh instance.
12. **`clr strict security` was on by default** (`value_in_use = 1`), which requires the assembly to be either signed/certificate-trusted or pre-registered via `sys.sp_add_trusted_assembly` (keyed by the assembly's SHA-512 hash) before `CREATE ASSEMBLY` succeeds — again an operational prerequisite rather than a persisted schema fact once the assembly is registered.

## Version gating

- No `sys.types` / `sys.assembly_types` / `sys.assemblies` / `sys.assembly_files` column used above carries an "Applies to" version restriction in Microsoft Learn beyond blanket "SQL Server" support — CLR integration and alias types both predate SQL Server 2016 (CLR integration and `CREATE TYPE` were introduced in SQL Server 2005), so nothing here is gated relative to the 2016 minimum baseline.
- `clr strict security` (the trusted-assembly/signing requirement encountered while registering the CLR UDT) was introduced in **SQL Server 2017** and is on by default from 2017 onward; SQL Server 2016 does not have this configuration option, so on a 2016 target `CREATE ASSEMBLY` would succeed without the `sp_add_trusted_assembly` step (subject to the `TRUSTWORTHY`/signing rules that already existed pre-2017). This is a deployment-time gate on the *object*, not a difference in the catalog columns' meaning.
- All catalog views/columns above were verified live against **SQL Server 2025 (17.0.1000.7) Enterprise Developer Edition on Linux**, and cross-checked against the `ver17`-branded Microsoft Learn documentation (which is written generically for "SQL Server" without an upper version bound), so no upper-bound gating was found either.

## Coverage audit

- **Unreferenced alias scalar types are invisible to the hash**: `SchemaExtractor` never queries `sys.types` directly for alias scalar types (`is_user_defined = 1 AND is_assembly_type = 0`) as their own object list. An alias type's identity (schema-qualified name plus its underlying base type/length/precision/scale/nullability) is captured only as a side effect of `EffectiveDataType`, invoked when a table column, table-type column, procedure/function parameter, or sequence references the type via `user_type_id`. An alias type that exists but is not referenced by anything is completely invisible: `CREATE TYPE`, `DROP TYPE`, or dropping-and-recreating over a different base type/length/precision/scale/nullability while unused does not change the hash. This is distinct from CLR user-defined types, whose entire object kind (`sys.assembly_types`/`sys.assemblies`/`sys.assembly_files`) is an already-documented, deliberate exclusion (see `website/docs/what-gets-hashed.md`'s "Not yet captured" section and BUGS.md's "Out-of-scope object kinds"); alias scalar types, by contrast, are explicitly listed as in scope, so this gap is about an in-scope feature's incomplete extraction, not an out-of-scope object kind.

Two related items were considered and are **not** filed as gaps because they are already documented, deliberate exclusions:

- Extended properties on non-table-type user-defined types (class 6 `TYPE`, alias or CLR) are not extracted — `SchemaExtractor.ExtractExtendedPropertiesAsync` restricts class 6 to `ty.is_table_type = 1`. `CLAUDE.md` explicitly documents this as "class 6 to table types," and `docs/schema-object-audit/extended-properties.md`'s own coverage audit already reasoned through this exact scenario and classified it as a deliberate scope boundary (there is no independent alias/CLR type identity for such a property to attach to).
- CLR user-defined types as a whole (assembly identity, class name, permission set, implementation bytes) are out of scope, per `website/docs/what-gets-hashed.md`'s "Not yet captured" section ("CLR modules and scalar CLR UDTs") and BUGS.md's "Out-of-scope object kinds" entry.
