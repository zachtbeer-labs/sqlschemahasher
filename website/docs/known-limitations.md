---
id: known-limitations
title: Known Limitations
sidebar_position: 4
---

# Known Limitations

These are catalog fields this library doesn't read yet, not correctness bugs — the hash is deterministic and accurate for everything it does capture. Most of these are gated behind a specific, opt-in SQL Server feature: if your schema doesn't use that feature, the gap doesn't apply to you at all.

For the small set of object kinds and features that are permanently out of scope by design — CLR modules, scalar CLR user-defined types, DDL/server-scoped triggers, table partitioning, and encrypted module bodies — see [What Gets Hashed](./what-gets-hashed.md#not-yet-captured). Everything below is a narrower, catalog-field-level gap that could be closed in a future release.

## Definition-text rendering drift

CHECK constraint, DEFAULT constraint, computed-column, and filtered-index definitions are hashed as SQL Server renders them (`sys.check_constraints.definition`, `sys.default_constraints.definition`, `sys.computed_columns.definition`, `sys.indexes.filter_definition`). SQL Server re-renders these expressions itself (spacing, parenthesization, casing of built-in functions, numeric literal formatting), and rendering can differ across major versions/CUs. The same DDL script run on SQL Server 2019 vs. 2022 could therefore produce a spurious `Different` comparison for a computed column, default, CHECK, or filtered index even though nothing semantically changed.

**Workaround:** compare hashes taken from the same SQL Server major version. This is intentionally not normalized — rewriting/canonicalizing expression text risks introducing new determinism bugs, and no such rewriting is currently implemented.

## Storage and physical options

- **Index-level `DATA_COMPRESSION`** — row/page/columnstore compression (`WITH (DATA_COMPRESSION = ...)`) lives on `sys.partitions.data_compression`, joined on `(object_id, index_id[, partition_number])` — a completely unpartitioned index still has exactly one `sys.partitions` row carrying this setting. Neither extraction nor hashing reads `sys.partitions`, so changing an index's compression level does not change the hash.
- **Columnstore compression delay** — `sys.indexes.compression_delay` (the `COMPRESSION_DELAY` option, in minutes) is not captured. It reads `0` (not `NULL`) for every columnstore index, so it's a real, always-present columnstore attribute, not runtime state.
- **Columnstore explicit column order** — `sys.index_columns.column_store_order_ordinal` (SQL Server 2022+, populated by `CREATE ... COLUMNSTORE INDEX ... ORDER (col1, col2)`) is not captured. Columnstore columns already hash with `key_ordinal` normalized and sorted by name, so an ordered columnstore index and the same index without the `ORDER` clause (or with a different order) hash identically.
- **Index physical option flags** — `sys.indexes.suppress_dup_key_messages` (2017+) and `optimize_for_sequential_key` (2019+, the last-page-insert option) are not captured, unlike the index's other physical options (`fill_factor`, `is_padded`, `allow_row_locks`, `allow_page_locks`, `ignore_dup_key`) which are.
- **Table-level `LOCK_ESCALATION`** — `sys.tables.lock_escalation`/`lock_escalation_desc` (`TABLE`/`AUTO`/`DISABLE`, set via `ALTER TABLE ... SET (LOCK_ESCALATION = ...)`) is a DDL-settable behavioral attribute that isn't captured.

## Security and encryption

- **Always Encrypted column key identity** — `sys.columns.column_encryption_key_id` and `encryption_algorithm_name` are not captured; only `encryption_type_desc` (`DETERMINISTIC`/`RANDOMIZED`) is. Swapping an encrypted column from one column encryption key to another, or changing its algorithm, does not change the hash. Detecting an in-place rekey under the *same* key id is a separate, harder problem (it would require hashing key-metadata values outside this library's in-scope object list) and isn't part of this gap.
- **Always Encrypted parameter metadata** — the same gap applies to stored procedure and function parameters (`sys.parameters.encryption_type`/`encryption_algorithm_name`/`column_encryption_key_id`); `ParameterSchema` has no corresponding field, unlike `ColumnSchema.EncryptionTypeDesc`.
- **`EXECUTE AS` execution context** — `sys.sql_modules.execute_as_principal_id` is not captured for triggers, stored procedures, or functions. Adding, removing, or changing an `EXECUTE AS <principal>` clause is a real change to the security context the module runs under, but currently doesn't change the hash. (For functions, `NULL` covers both "unset" and explicit `EXECUTE AS CALLER`, so the column can't distinguish those two either way — but it would still catch `SELF`/`OWNER`/a named principal.)
- **PRIMARY KEY / UNIQUE constraint `is_enforced`** — the flag behind `NOT ENFORCED` on PK/UNIQUE syntax isn't captured. On every SQL Server version and Azure SQL DB/MI this library targets, the flag always reads `1` — `NOT ENFORCED` on a PK/UNIQUE constraint is Fabric Warehouse-only today and is rejected elsewhere. Worth adding if Fabric Warehouse (or a future on-box release) becomes a target; there's no way to observe a `0` on a supported server right now.
- **Explicit ownership (`ALTER AUTHORIZATION`)** — `principal_id` (inherited from `sys.objects`/`sys.schemas`) is not captured for synonyms or schemas. It's `NULL` until an explicit `ALTER AUTHORIZATION ... TO <principal>` reassigns ownership away from the default owner. No object kind in this library currently captures `principal_id`/explicit ownership — this is a general, not-yet-decided scope question, not something specific to synonyms or schemas.

## Module behavior flags

- **`WITH SCHEMABINDING`** — `sys.sql_modules.is_schema_bound` is not captured for triggers, functions, or views. It's a load-bearing clause (it's the prerequisite for indexing a view, and it locks referenced tables/columns against breaking `ALTER`s). Under default options this is incidentally reflected in the body-text hash, but disappears entirely once `ModuleNormalization.IgnoreBodyText` is set, and there's no discrete field to inspect independent of body-text diffing.
- **Stored procedure `WITH RECOMPILE`** — `sys.sql_modules.is_recompiled` is a genuine compile-time option, not runtime state, and isn't captured.
- **Native compilation** — `sys.sql_modules.uses_native_compilation` (In-Memory OLTP, `WITH NATIVE_COMPILATION, SCHEMABINDING`) isn't captured for stored procedures or functions. A natively-compiled module is a fundamentally different execution-engine object from an interpreted one with the same signature and body.
- **Stored procedure replication/startup flags** — `is_auto_executed` (the `sp_procoption 'startup'` flag), `is_execution_replicated`, `is_repl_serializable_only`, and `skips_repl_constraints` reflect DDL-configured behavior and aren't captured.
- **Function `RETURNS NULL ON NULL INPUT`** — `sys.sql_modules.null_on_null_input` is a genuine calling-convention contract (the engine short-circuits to `NULL` without invoking the body) selected at CREATE/ALTER time, and isn't captured.
- **Function database-collation dependency** — `sys.sql_modules.uses_database_collation` is `1` when a schema-bound function's correctness depends on the database's default collation, which blocks `ALTER DATABASE ... COLLATE` while the module exists. Not captured.
- **View `WITH CHECK OPTION` / `VIEW_METADATA`** — `sys.views.with_check_option` and `has_opaque_metadata` aren't captured as discrete fields. Both happen to be reflected in the definition-text hash under default options, but disappear under `IgnoreBodyText`.
- **Table-level `ANSI_NULLS`** — `sys.tables.uses_ansi_nulls` (distinct from the `UsesAnsiNulls` already captured for procedures/views/functions/triggers via `sys.sql_modules`, which has no row for a table) affects computed-column and CHECK-constraint NULL comparison semantics and isn't captured.

## Catalog objects not modeled as first-class entities

Every other in-scope object kind (tables, procedures, table types, views, functions, triggers, sequences, synonyms) has its own driving catalog query and its own list in `SchemaMetadata`. These don't yet:

- **Numbered stored procedures** — deprecated legacy syntax (`CREATE PROCEDURE proc;2`); a numbered procedure beyond `;1` lives only in `sys.numbered_procedures` and is entirely invisible to extraction. Not present in Azure SQL Database at all.
- **Unreferenced alias scalar types** — an alias type's identity (`CREATE TYPE dbo.OrderTotal FROM DECIMAL(9,2) NOT NULL`) is only observed as a side effect of a column/parameter/sequence referencing it. An alias type that exists but isn't referenced anywhere is invisible: creating, dropping, or redefining an unused one doesn't change the hash.
- **Schemas as catalog objects** — `sys.schemas` isn't queried directly. A schema's name only surfaces indirectly via the objects extracted under it, so an empty schema (or one whose contents are entirely excluded via `ObjectNamesToIgnore`/`SchemaFilter`) has no representation anywhere in the output.
- **XML schema collection content** — a column or parameter typed `XML(<collection>)` captures only the collection's name and the `DOCUMENT`/`CONTENT` facet, never the collection's actual namespace/XSD shape. A collection's content can be silently replaced (`ALTER XML SCHEMA COLLECTION ... ADD`, or `DROP`+`CREATE` under the same name with different content) without changing the hash, and an unreferenced collection is invisible entirely.
- **Full-text search** — `sys.fulltext_catalogs`, `sys.fulltext_indexes`, and `sys.fulltext_index_columns` are not queried, so a full-text catalog's name/default/accent-sensitivity, an index's catalog/key-index/change-tracking/enabled state/stoplist/property list, and per-column participation (language, `TYPE COLUMN` pairing, semantic search) are all unobserved. The crawl/population runtime columns are correctly out of scope as transient state.

## Temporal table edge cases

- **Orphaned ex-history tables** — after `ALTER TABLE ... SET (SYSTEM_VERSIONING = OFF)`, an anonymous history table (`MSSQL_TemporalHistoryFor_<object_id>`) is left behind as an ordinary table. At that point it genuinely *is* an ordinary table with a real (if ugly) name, so its raw name hashing exactly is accepted rather than treated as a gap. **Workaround:** rename the orphaned table, or add it to `ObjectNamesToIgnore`.
- **Extended properties on an anonymous history table** — an extended property targeting an anonymous history table resolves to the table's raw, non-deterministic name; the name normalization applied to the table itself doesn't extend to properties that reference it. Rare enough (extended properties are seldom attached to an auto-named history table rather than its versioned parent) that it isn't worth the extra join today.
