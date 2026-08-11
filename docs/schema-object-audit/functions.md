# Functions

Covers SQL Server user-defined T-SQL functions: scalar (`FN`), inline table-valued (`IF`), and multi-statement table-valued (`TF`). CLR functions (`FS`/`FT`/`AF`) are a related but out-of-scope object type — see the "CLR functions" note near the end.

All findings below were produced by creating six functions in a scratch schema (`audit_functions`) on a live SQL Server 2025 (17.0.1000.7) container and querying the catalog directly; column semantics were cross-checked against Microsoft Learn.

## Catalog views used

| View | Purpose |
|---|---|
| `sys.objects` | One row per function: identity, `type`/`type_desc`, timestamps, `is_ms_shipped` |
| `sys.sql_modules` | Module body text and module-level flags (schema binding, SET options, inlining eligibility) |
| `sys.parameters` | Parameters, **and** — for scalar functions only — a synthetic `parameter_id = 0` row describing the return type |
| `sys.types` | Resolves `sys.parameters.user_type_id` to a type name |
| `sys.columns` | Result-set column shape for both inline and multi-statement TVFs (see edge case below) |
| `sys.indexes` | The auto-created `PRIMARY KEY` index on a multi-statement TVF's declared return table |
| `sys.schemas` | Schema-qualification for the object name |

`OBJECTPROPERTY(object_id, 'IsEncrypted')` was also used as a helper (not a catalog view) since `sys.sql_modules` has no direct `is_encrypted` bit — see the encryption edge case.

## sys.objects — identity and type discrimination

Filter with `type IN ('FN','IF','TF')`. Observed/documented values:

| `type` | `type_desc` | Meaning |
|---|---|---|
| `FN` | `SQL_SCALAR_FUNCTION` | Scalar function |
| `IF` | `SQL_INLINE_TABLE_VALUED_FUNCTION` | Inline TVF (single `RETURN (SELECT …)`, no `BEGIN/END`) |
| `TF` | `SQL_TABLE_VALUED_FUNCTION` | Multi-statement TVF (`RETURNS @x TABLE (...) BEGIN … END`) |

These were the only three `type` values produced by T-SQL `CREATE FUNCTION`; documentation additionally lists `FS`/`CLR_SCALAR_FUNCTION`, `FT`/`CLR_TABLE_VALUED_FUNCTION`, and `AF`/`AGGREGATE_FUNCTION` for CLR-based functions (out of this library's scope per its own stated boundaries; also unreachable on this Linux container, which has no CLR-integration support to test against).

Fields relevant to change detection:
- `name`, `schema_id` (resolve via `sys.schemas` to a schema-qualified name) — identity.
- `type`/`type_desc` — a function changing shape (e.g. scalar → TVF) is only representable as drop+recreate in T-SQL, but `type_desc` should still be part of the identity/hash key in case of same-name-different-kind scenarios across databases.
- `is_ms_shipped` — always `0` for user objects in a user schema; only relevant if a hasher also scans system schemas.
- `object_id` — **not deterministic across databases; must not be hashed.** Verified stable across `ALTER FUNCTION` (does not change on ALTER, only on DROP+CREATE).

Runtime state to explicitly exclude:
- `create_date` / `modify_date` — both are wall-clock timestamps. Confirmed by experiment: `ALTER FUNCTION` on `fn_scalar_add` left `create_date` unchanged but bumped `modify_date` (`2026-08-10 00:02:25.640` → `00:05:26.233`); `object_id` was unchanged across the same ALTER.
- `is_published` / `is_schema_published` — replication runtime state, not schema shape.

## sys.sql_modules — the module body and its flags

One row per function object_id (join on `sm.object_id = o.object_id`), containing:

- **`definition`** (`nvarchar(max)`) — the verbatim `CREATE FUNCTION` text. `NULL` when the function was created `WITH ENCRYPTION`. This is the primary signal for module-body hashing.
- **`uses_ansi_nulls`**, **`uses_quoted_identifier`** — the `SET ANSI_NULLS`/`SET QUOTED_IDENTIFIER` session options captured at creation and baked into the module; a function recreated with different session settings but byte-identical text is a genuine (if subtle) schema difference.
- **`is_schema_bound`** — reflects `WITH SCHEMABINDING`. Verified `1` for `fn_scalar_add` (created `WITH SCHEMABINDING`), `0` for the others.
- **`uses_database_collation`** — `1` when a schema-bound module's correctness depends on the database's default collation (this blocks changing the database's default collation while the module exists). Observed `1` only on the schema-bound function in this test batch, `0` elsewhere — worth a flag on its own since it changes cross-database comparability semantics even without a text change.
- **`is_recompiled`** — reflects `WITH RECOMPILE` (procedure-oriented option; not settable on `CREATE FUNCTION`, always `0` here).
- **`null_on_null_input`** — `RETURNS NULL ON NULL INPUT`; always `NULL`/`0` unless declared (not exercised in this batch, but a real schema difference when set).
- **`execute_as_principal_id`** — reflects `WITH EXECUTE AS`. `NULL` = default/`CALLER`; a principal ID for `SELF`/named principal; `-2` = `OWNER`. Observed `NULL` for `fn_scalar_greet` (`WITH EXECUTE AS CALLER`), matching the documented "NULL by default or if EXECUTE AS CALLER" — **note this means `CALLER` (explicit) is catalog-indistinguishable from the unset default**, so a hasher cannot tell "no EXECUTE AS clause" from "explicit `EXECUTE AS CALLER`" via this column alone; only the module `definition` text carries that distinction.
- **`uses_native_compilation`** — natively-compiled (In-Memory OLTP) scalar functions; `0` in all cases tested (not exercised; requires a memory-optimized filegroup).
- **`inline_type`** / **`is_inlineable`** *(SQL Server 2019+)* — scalar-UDF-inlining eligibility/state. Observed `1`/`1` for every scalar function and both TVF kinds *except* the multi-statement TVF, which was `0`/`0`. Per Microsoft Learn: `is_inlineable` is always `1` for inline TVFs and reflects actual eligibility for scalar UDFs; always `0` for MSTVFs and all other module types. This is a derived/computed property of the current definition (SQL Server's static analysis of the body), so it is a genuine function of schema shape and worth hashing if the library ever compares physical/optimization-relevant properties — but it is currently not part of this library's documented "structural" surface for other object kinds either, so treat it as an optional/advanced signal, not a must-hash field.

**Encryption edge case:** `WITH ENCRYPTION` (tested on `fn_scalar_secret`) makes `sys.sql_modules.definition` `NULL`, but the row still exists with `is_schema_bound = 0`, `uses_ansi_nulls = 1`, etc. all populated — encryption hides only the text, not the structural flags. There is **no direct catalog bit for "is encrypted"** in `sys.sql_modules`/`sys.objects`; the only way to detect it is either `OBJECTPROPERTY(object_id, 'IsEncrypted') = 1` or inferring it from `definition IS NULL` while the object still has a `sys.sql_modules` row (vs. genuinely absent, e.g. a synonym). A schema hasher that hashes `definition` verbatim will naturally treat an encrypted function as an "opaque NULL" blob — two encrypted functions with different bodies would hash identically on body text alone, which is a real blind spot worth documenting if body-hashing is used as a change-detection proxy.

## sys.parameters — parameters, and the scalar return-type row

One row per declared parameter, in `parameter_id` order starting at 1. For **scalar functions only**, there is an additional row with `parameter_id = 0` and `name = ''` (empty string, not `NULL`) representing the function's own return type — confirmed by direct query (`fn_scalar_add` parameter_id 0 → `int`, `is_output = 1`) and matches Microsoft Learn: *"If the object is a scalar function, parameter_id = 0 represents the return value... the parameter name is an empty string in the row representing the return value."*

**Table-valued functions have no `parameter_id = 0` row.** Verified directly: `fn_mstvf_numbers` has exactly one `sys.parameters` row (`@maxVal`, id 1) and no return-type row — its return shape lives elsewhere (see the `sys.columns`/`sys.indexes` edge case below), not in `sys.parameters`.

Columns relevant to change detection, per parameter (and, where applicable, the id-0 return row):
- `name`, `parameter_id` — identity/ordering (parameter order is part of the signature and must be hashed in order, not sorted).
- `system_type_id` / `user_type_id` (join `sys.types` for the name) — the declared type, including user-defined table types (e.g. `@ids audit_functions.tt_IntList READONLY` resolved to type name `tt_IntList`).
- `max_length`, `precision`, `scale` — full type specification (e.g. `nvarchar(100)` → `max_length = 200` bytes; `int` → `precision = 10, scale = 0`).
- `is_output` — `1` only for the scalar-function return row in this test batch; T-SQL user-defined functions do not support `OUTPUT` parameters (only stored procedures do), so `is_output` should always be `0` for every *actual* parameter row and `1` only for the synthetic return row.
- `is_readonly` — `1` for table-valued parameters (`READONLY` is mandatory for TVPs); verified `1` on `fn_itvf_from_tvp`'s `@ids` parameter.
- `is_nullable` — mostly relevant to natively-compiled modules (`0` disables nullability for perf); observed `1` throughout for normal (non-natively-compiled) functions.
- `is_cursor_ref` — cursor-reference parameters; `0` throughout (functions can't take cursor parameters the way procs can — not exercised, listed for completeness).
- `encryption_type` / `encryption_type_desc` / `encryption_algorithm_name` / `column_encryption_key_id` / `column_encryption_key_database_name` *(SQL Server 2016+)* — Always Encrypted parameter metadata; not exercised (requires a CMK/CEK setup) but present in the catalog and directly analogous to the same columns already tracked on `sys.columns` for tables.
- `vector_dimensions` / `vector_base_type` / `vector_base_type_desc` *(SQL Server 2025 (17.x)+, new)* — for a `VECTOR(n)`-typed parameter. Verified directly by creating `fn_scalar_vec(@v VECTOR(3))`: `vector_dimensions = 3`, `vector_base_type = 0`, `vector_base_type_desc = 'float32'`. This is a brand-new catalog surface exposed only on 2025+; consistent with the project's documented out-of-scope note for "SQL 2025 vectors," but worth flagging since it lives on the *same* `sys.parameters` row/columns as everything else, not a separate view — a naive "select all sys.parameters columns" extraction would pick these up automatically once a 2025-minimum baseline is adopted.

**`has_default_value` / `default_value` are dead weight for T-SQL functions.** Experimentally verified: `fn_scalar_add(@a INT, @b INT = 5)` shows `has_default_value = 0` and `default_value = NULL` for `@b` even though the DDL declares a default and `SELECT audit_functions.fn_scalar_add(1, DEFAULT)` correctly evaluates to `6`. Microsoft Learn confirms this is by design: *"SQL Server only maintains default values for CLR objects in this catalog view; therefore, this column has a value of 0 for Transact-SQL objects. To view the default value of a parameter in a Transact-SQL object, query the definition column of sys.sql_modules."* **Implication for schema hashing: a parameter default value can only be detected/verified via the module `definition` text, never via `sys.parameters` alone** — any change-detection approach that skips body-text hashing for functions and relies only on structural parameter metadata will silently miss default-value changes.

**Another T-SQL-vs-procedure asymmetry worth documenting**: per Microsoft Learn, when a function parameter has a default, the caller **must** write the literal keyword `DEFAULT` to invoke it (unlike stored procedures, where simply omitting the argument implies the default). This is a calling-convention detail, not a catalog column, but explains why `has_default_value` isn't load-bearing at the catalog level for functions the way it might seem it should be.

## Table-valued function result shape — sys.columns and sys.indexes (edge case)

Neither `IF` nor `TF` functions have a `parameter_id = 0` return row, but their result-set shape *is* independently discoverable:

- **Inline TVF** (`fn_itvf_evens`, single `SELECT`): `sys.columns WHERE object_id = <function object_id>` returns one row per output column with inferred name/type (`EvenNumber, int, 4 bytes, not nullable`) — SQL Server statically derives the shape from the single `SELECT`.
- **Table-typed parameter pass-through** (`fn_itvf_from_tvp`, `SELECT Val FROM @ids`): likewise resolves to one `sys.columns` row (`Val, int`), confirming inline TVFs always have a statically knowable result shape via `sys.columns`.
- **Multi-statement TVF** (`fn_mstvf_numbers`, `RETURNS @Result TABLE (Num INT NOT NULL PRIMARY KEY, IsEven BIT NOT NULL)`): the *declared* return-table columns are directly in `sys.columns` keyed by the function's own `object_id` (`Num int`, `IsEven bit`), and — notably — the inline `PRIMARY KEY` on the table variable produces a real row in `sys.indexes` keyed by that same `object_id`: `PK__fn_mstvf__C7D08B63ADF7EC43`, `type_desc = CLUSTERED`, `is_primary_key = 1`.

**Auto-generated name edge case**: that PK index name (`PK__fn_mstvf__C7D08B63ADF7EC43`) is the same hex-object-id-suffixed auto-generated shape SQL Server uses for unnamed table constraints elsewhere in the catalog (`PK__<truncated-table>__<hex>`) — it is **not deterministic across databases** even though the DDL (`PRIMARY KEY` with no explicit name inside the `RETURNS @Result TABLE (...)` clause) is byte-identical. A schema hasher that walks `sys.indexes` for MSTVF return tables needs the same auto-generated-name normalization treatment already applied to ordinary table constraints, or it will manufacture false positives between otherwise-identical MSTVFs.

## Version gating summary

| Column / feature | Minimum version | Notes |
|---|---|---|
| `sys.objects`, `sys.sql_modules` core columns, `sys.parameters` core columns | Pre-2016 (baseline) | — |
| `sys.parameters.encryption_type`/`encryption_type_desc`/`encryption_algorithm_name`/`column_encryption_key_id`/`column_encryption_key_database_name` | SQL Server 2016 (13.x)+ | Always Encrypted parameter metadata |
| `sys.sql_modules.inline_type` / `is_inlineable` | SQL Server 2019 (15.x)+ | Scalar UDF inlining; documented behavior verified directly against a 2025 instance |
| `sys.sql_modules.inline_eligibility_mask` | Fabric-only (per docs) | Not applicable to standalone SQL Server; not tested |
| `sys.parameters.vector_dimensions` / `vector_base_type` / `vector_base_type_desc` | SQL Server 2025 (17.x)+ | New in this release; verified directly by creating a `VECTOR(3)` parameter |
| `sys.sql_modules.uses_native_compilation` | SQL Server 2014 (12.x)+ | Natively-compiled (In-Memory OLTP) scalar functions; column present but not exercised (needs a memory-optimized filegroup) |

No behavior specific to SQL Server 2025 beyond the vector columns was observed for ordinary (non-vector, non-natively-compiled) T-SQL functions; the core `FN`/`IF`/`TF` extraction path should work unchanged back to SQL Server 2016.

## Summary of fields that matter for change detection

**Identity / structural** (should be hashed): schema-qualified name, `type`/`type_desc`, module `definition` text (the single most load-bearing field — it is the only reliable source for parameter defaults and encrypted-body detection caveats aside), `is_schema_bound`, `uses_ansi_nulls`, `uses_quoted_identifier`, `uses_database_collation`, `null_on_null_input`, `execute_as_principal_id`, per-parameter `name`/`parameter_id`(order)/type triple (`system_type_id`+`user_type_id`→name/`max_length`/`precision`/`scale`)/`is_readonly`/`is_output`, and — if Always-Encrypted or vector support is in scope — the corresponding `encryption_*`/`vector_*` columns.

**Explicitly excluded (runtime/non-deterministic state)**: `object_id`, `create_date`, `modify_date`, `is_published`, `is_schema_published`, and (for MSTVF auto-generated PK index names) the raw hex-suffixed index name without normalization.

**Known catalog blind spots** (must fall back to `definition` text): parameter default values (`has_default_value`/`default_value` are always `0`/`NULL` for T-SQL objects), and whether a module is encrypted (no direct bit — only `definition IS NULL` + `OBJECTPROPERTY(...,'IsEncrypted')`).

## Coverage audit

- **`sys.sql_modules.is_schema_bound` not extracted or hashed for functions**: `FunctionSchema` (`src/SchemaMetadata.cs`) has no corresponding field, and the function extraction query in `SchemaExtractor.ExtractFunctionsAsync` selects only `m.definition` (via the hash expression), `m.uses_ansi_nulls`, and `m.uses_quoted_identifier` from `sys.sql_modules` — not `m.is_schema_bound`. `WITH SCHEMABINDING` is common and behaviorally significant (it locks referenced objects against breaking ALTERs) for scalar/inline functions used inside indexed views or computed columns. Two functions identical in name, type, parameters, flags, and body hash currently compare as identical whether or not one carries `WITH SCHEMABINDING`. Not documented as a deliberate exclusion in BUGS.md or `website/docs/what-gets-hashed.md` prior to this audit.
- **`sys.sql_modules.execute_as_principal_id` not extracted or hashed for functions**: same file/method — no `ExecuteAsPrincipal`-style field on `FunctionSchema`, and the query does not select `m.execute_as_principal_id`. Adding, removing, or changing a function's `EXECUTE AS` clause to/from `SELF`/`OWNER`/a named principal — a real change to its execution security context — currently does not change the hash.
- **`sys.sql_modules.null_on_null_input` not extracted or hashed for functions**: same file/method. `RETURNS NULL ON NULL INPUT` is a genuine calling-convention/optimization contract selected at CREATE/ALTER time, not runtime state, and is entirely uncaptured.
- **`sys.sql_modules.uses_database_collation` not extracted or hashed for functions**: same file/method. This flag is a catalog-observable cross-database comparability signal (it gates `ALTER DATABASE ... COLLATE`) and is entirely uncaptured.
- **`sys.sql_modules.uses_native_compilation` not extracted or hashed for functions**: same file/method. A natively-compiled scalar function (`WITH NATIVE_COMPILATION, SCHEMABINDING`) is a fundamentally different execution-engine object from an interpreted function with the same signature/body, analogous to `IsMemoryOptimized` already captured for tables/table types, and is entirely uncaptured for functions.

All other fields the blind doc calls out as change-detection-relevant are correctly extracted and hashed in `SchemaExtractor.ExtractFunctionsAsync`/`ExtractParametersAsync` and `SchemaHashCalculator.HashFunction`/`HashParameter`: schema-qualified name, `type_desc` (kept unconditional, surviving `ModuleNormalization.IgnoreBodyText`), the module body hash (`DefinitionHash`, with a distinct `<encrypted>` sentinel for `WITH ENCRYPTION` functions rather than colliding with an empty body), `uses_ansi_nulls`/`uses_quoted_identifier`, and per-parameter `name`/order/type-triple/`is_readonly`/`is_output`/XML-typed-column facets — including the scalar function's synthetic `parameter_id = 0` return row, hashed in list order rather than sorted. `object_id`/`create_date`/`modify_date`/`is_published`/`is_schema_published` are correctly excluded as runtime state. `has_default_value`/`default_value` being dead weight for T-SQL objects is an inherent catalog blind spot (not a library gap) already covered by body-hash reliance, matching the doc's own framing. `sys.parameters.vector_dimensions`/`vector_base_type`/`vector_base_type_desc` (SQL Server 2025+) are correctly out of scope per this library's documented SQL-2025-vectors exclusion (`CLAUDE.md`, BUGS.md's "Out-of-scope object kinds" entry). `sys.sql_modules.inline_type`/`is_inlineable` are, per the blind doc's own assessment, an optional/advanced signal rather than a must-hash field, consistent with no other object kind in this library capturing an analogous inlining-eligibility signal. CLR functions (`FS`/`FT`/`AF`) are correctly out of scope (documented in BUGS.md's "Out-of-scope object kinds" entry and `website/docs/what-gets-hashed.md`'s "Not yet captured" section).

Two catalog-observable fields already have their own **pre-existing, non-function-specific** BUGS.md entries and are not re-filed here: `sys.parameters` Always Encrypted metadata (`encryption_type`/`encryption_type_desc`/`encryption_algorithm_name`/`column_encryption_key_id`) is covered by the existing "Stored procedure parameter Always Encrypted metadata not captured" entry, which explicitly extends to function parameters (shared `ParameterSchema`/`ExtractParametersAsync`); `is_recompiled` is confirmed by this audit to always read `0` for functions (not settable via `CREATE FUNCTION`), so it cannot actually vary and is not a real gap for this object type.
