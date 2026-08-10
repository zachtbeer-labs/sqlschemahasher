# Known Bugs / Design Gaps

Tracked issues discovered during the settings-matrix test build (see `TESTPLAN.md`) and an earlier
hash-fidelity audit of `SchemaExtractor`/`SchemaMetadata`/`SchemaHashCalculator`/`SchemaHashOptions`.

## Open / known limitations

### Definition-text rendering drift

CHECK constraint, DEFAULT constraint, computed-column, and filtered-index definitions are hashed as
SQL Server renders them (`sys.check_constraints.definition`, `sys.default_constraints.definition`,
`sys.computed_columns.definition`, `sys.indexes.filter_definition`). SQL Server re-renders these
expressions itself (spacing, parenthesization, casing of built-in functions, numeric literal
formatting), and rendering can differ across major versions/CUs. The same DDL script run on SQL Server
2019 vs. 2022 could therefore produce a spurious `Different` comparison for a computed column, default,
CHECK, or filtered index even though nothing semantically changed.

**Workaround:** compare hashes taken from the same SQL Server major version. This is intentionally not
normalized in v2 — rewriting/canonicalizing expression text risks introducing new determinism bugs, and
no such rewriting is currently implemented.

### Partitioning and filegroup/data-space placement

Table/index partitioning and filegroup or data-space placement are not captured. Moving a table to a
different filegroup, or repartitioning it, does not change the hash.

### Out-of-scope object kinds

Extraction covers tables, stored procedures, user-defined table types, views, T-SQL functions (scalar,
inline TVF, multi-statement TVF), DML triggers, sequences, and synonyms. CLR modules (functions,
triggers, aggregates), scalar CLR user-defined types, and DDL/database/server-scoped triggers are not
read, so adding, altering, or dropping them does not change the hash. See the README's "Not Yet
Captured" section.

### Encrypted modules

Two different `WITH ENCRYPTION` modules (stored procedures, views, functions, or triggers) with
identical signatures collide on the `<encrypted>` sentinel used in place of a definition hash. This is
inherent — the module body is unreadable server-side once encrypted, so there is no signal available
to distinguish them.

### Orphaned ex-history tables

After `ALTER TABLE ... SET (SYSTEM_VERSIONING = OFF)`, an anonymous history table
(`MSSQL_TemporalHistoryFor_<object_id>`) is left behind as an ordinary table — `temporal_type` reverts to
`NON_TEMPORAL_TABLE`, so `SchemaHashCalculator.EffectiveTableName`'s guard no longer fires and the
table's raw, non-deterministic name hashes exactly. At that point it genuinely is an ordinary table
whose physical name is real schema, so this is accepted rather than treated as a bug. **Workaround:**
rename the orphaned table, or add it to `ObjectNamesToIgnore`.

### Extended properties on an anonymous history table

An extended property targeting an anonymous temporal history table resolves `ObjectName` to the table's
raw, non-deterministic `MSSQL_TemporalHistoryFor_<object_id>` name — `EffectiveTableName`'s normalization
applies only to `TableSchema.Name`, not to `ExtendedPropertySchema.ObjectName`. Rare enough (extended
properties are seldom attached to an auto-named history table rather than its versioned parent) not to
warrant the extra parent join in the extended-property query now; revisit if reported.

### Columnstore compression delay not captured

`sys.indexes.compression_delay` (the `COMPRESSION_DELAY` option controlling columnstore rowgroup
compression, in minutes) is not extracted or hashed. It is `NULL` for every rowstore index but `0`
(not `NULL`) for a columnstore index with no explicit delay configured, so it is a real, always-present
columnstore attribute — not a runtime/state value. Two columnstore indexes differing only in their
configured compression delay currently compare as identical.

### Index physical option flags not captured (SUPPRESS_DUP_KEY_MESSAGES, OPTIMIZE_FOR_SEQUENTIAL_KEY)

`sys.indexes.suppress_dup_key_messages` (SQL Server 2017+) and `optimize_for_sequential_key` (SQL Server
2019+, the `OPTIMIZE_FOR_SEQUENTIAL_KEY` last-page-insert option) are not extracted or hashed, unlike the
index's other physical/behavioral options (`fill_factor`, `is_padded`, `allow_row_locks`,
`allow_page_locks`, `ignore_dup_key`) which are. An index differing only in one of these two flags
compares as identical.

### Columnstore explicit column order (ORDER clause) not captured

`sys.index_columns.column_store_order_ordinal` (SQL Server 2022+, populated by
`CREATE ... COLUMNSTORE INDEX ... ORDER (col1, col2)`) is not extracted or hashed. Columnstore index
columns already hash with `key_ordinal` normalized to 0 and are sorted by name for determinism, so an
ordered columnstore index and the same index without the `ORDER` clause (or with a different column
order) currently hash identically.

### Index-level DATA_COMPRESSION not captured

Row/page/columnstore compression (`DATA_COMPRESSION = ROW/PAGE/COLUMNSTORE`, set via
`WITH (DATA_COMPRESSION = ...)` on `CREATE`/`ALTER INDEX`) lives on `sys.partitions.data_compression`,
not `sys.indexes`, joined on `(object_id, index_id[, partition_number])` — a completely unpartitioned
index still has exactly one `sys.partitions` row carrying this setting. Neither `SchemaExtractor` nor
`SchemaHashCalculator` reads `sys.partitions`, so changing an index's compression level does not change
the hash. This is distinct from the existing "Partitioning and filegroup/data-space placement" gap
(repartitioning / moving filegroups): compression applies even to a non-partitioned index, so it is not
covered by that entry.

### PRIMARY KEY / UNIQUE constraint `is_enforced` not captured

`sys.key_constraints.is_enforced` — the flag behind the `NOT ENFORCED` clause on `PRIMARY KEY`/`UNIQUE`
constraint syntax — is not extracted or hashed; `KeyConstraintSchema` has no corresponding field. This is
the PK/UNIQUE analog of the `is_disabled`/`is_not_trusted` enforcement state already captured for FOREIGN
KEY and CHECK constraints. On general-purpose SQL Server (2016+) and Azure SQL DB/MI it is observed to
always read `1` — `NOT ENFORCED` on a PK/UNIQUE constraint is currently Microsoft Fabric Warehouse-only
and is rejected elsewhere (`Msg 40514: 'NOT ENFORCED' is not supported in this version of SQL Server`) —
so the flag cannot actually vary on any platform this library targets today. Left uncaptured for now
since there is no way to observe a `0` on a supported server; worth adding for forward-compatibility if
Fabric Warehouse (or a future on-box release) becomes a target.

### Table-level ANSI_NULLS setting not captured

`sys.tables.uses_ansi_nulls` — the `ANSI_NULLS` session setting a table was created under — is not
extracted or hashed. It is distinct from the `UsesAnsiNulls` already captured for stored procedures,
views, functions, and triggers (sourced from `sys.sql_modules`, which has no row for a table — tables
carry their own catalog-level flag). This setting affects computed-column and CHECK-constraint NULL
comparison semantics, so it is genuinely schema-relevant, not runtime state. Two otherwise-identical
tables created under different `ANSI_NULLS` settings currently compare as identical.

### Table-level LOCK_ESCALATION setting not captured

`sys.tables.lock_escalation`/`lock_escalation_desc` (`TABLE` / `AUTO` / `DISABLE`, set via
`ALTER TABLE ... SET (LOCK_ESCALATION = ...)`) is not extracted or hashed. This is a DDL-settable
behavioral attribute, not runtime state, and changing it currently does not change the hash.

### Always Encrypted column key identity not captured

`sys.columns.column_encryption_key_id` (the FK into `sys.column_encryption_keys` identifying which
Always Encrypted key protects a column) and `encryption_algorithm_name` are not extracted or hashed —
only `encryption_type_desc` (`DETERMINISTIC`/`RANDOMIZED`) is captured on `ColumnSchema`. Resolving
`column_encryption_key_id` to the key's name (the same id-to-name technique already used for
`schema_id`/`object_id` elsewhere) would let the hash detect a column being rekeyed onto a different
CEK; currently, swapping an encrypted column from one CEK to another, or changing its encryption
algorithm, does not change the hash.

**Workaround:** none currently. Detecting an in-place rekey (a new `ENCRYPTED_VALUE` under the *same*
CEK id) is a separate, harder problem — it would require hashing
`sys.column_encryption_key_values.encrypted_value`, a database-scoped key-metadata object outside this
library's stated in-scope object list — and is not part of this gap.

### Trigger schema-binding (`WITH SCHEMABINDING`) not captured

`sys.sql_modules.is_schema_bound` is not extracted or hashed for triggers; `TriggerSchema` has no
corresponding field. `WITH SCHEMABINDING` is legal (if rare) syntax on `CREATE`/`ALTER TRIGGER` and is
genuinely schema-relevant — it locks the referenced table/columns against ALTERs that would break the
trigger, a real behavioral difference from an otherwise-identical unbound trigger. Two triggers
identical in name, parent, event set, flags, and body hash currently compare as identical whether or
not one carries `WITH SCHEMABINDING`.

### Trigger `EXECUTE AS` context not captured

`sys.sql_modules.execute_as_principal_id` — populated only when a trigger specifies
`EXECUTE AS <principal>` — is not extracted or hashed; `TriggerSchema` has no corresponding field. This
is the trigger analog of the id-to-name resolution technique already used elsewhere in the extractor
(e.g. `SCHEMA_NAME`/`OBJECT_NAME` lookups), just not applied here. Adding, removing, or changing a
trigger's `EXECUTE AS` clause — a real change to the security context the trigger body executes under —
currently does not change the hash.

### Stored procedure `WITH RECOMPILE` not captured

`sys.sql_modules.is_recompiled` — set when a procedure is created or altered `WITH RECOMPILE` — is not
extracted or hashed; `StoredProcedureSchema` has no corresponding field. This is a genuine compile-time
behavioral option selected at CREATE/ALTER time, not runtime state. Two procedures identical in name,
parameters, and body hash currently compare as identical whether or not one carries `WITH RECOMPILE`.

### Stored procedure `EXECUTE AS` context not captured

`sys.sql_modules.execute_as_principal_id` — populated only when a procedure specifies
`EXECUTE AS <principal>` — is not extracted or hashed; `StoredProcedureSchema` has no corresponding
field. This is the stored-procedure analog of the "Trigger `EXECUTE AS` context not captured" entry
above: the same column, resolvable with the same id-to-name technique already used elsewhere in the
extractor, just not applied to `sys.procedures`. Adding, removing, or changing a procedure's
`EXECUTE AS` clause — a real change to the security context the body executes under — currently does not
change the hash.

### Stored procedure native compilation not captured (`uses_native_compilation`, `is_schema_bound`)

`sys.sql_modules.uses_native_compilation` is not extracted or hashed for stored procedures. Native
compilation (In-Memory OLTP, `CREATE PROCEDURE ... WITH NATIVE_COMPILATION, SCHEMABINDING`) is a
genuine, DDL-selected execution model, not runtime state. `is_schema_bound` on the same view is the
derived flag Microsoft Learn documents as forced to `1` only for a natively compiled procedure (plain
`CREATE PROCEDURE` has no `SCHEMABINDING` option, so it is otherwise always `0`), so it carries no
information beyond `uses_native_compilation` and does not need separate capture. Two procedures
identical in name, parameters, and body hash currently compare as identical whether one is natively
compiled and the other interpreted.

### Stored procedure replication/startup flags not captured

`sys.procedures` adds four columns beyond the shared `sys.objects` row — `is_auto_executed` (the
`sp_procoption 'startup'` flag), `is_execution_replicated`, `is_repl_serializable_only`, and
`skips_repl_constraints` — none of which are extracted or hashed; `StoredProcedureSchema` has no
corresponding fields. These reflect DDL-configured behavior (whether a procedure auto-runs at server
startup, and how it participates in replication), not runtime state. Two procedures identical in every
other captured field currently compare as identical regardless of these flags.

### Stored procedure parameter Always Encrypted metadata not captured

`sys.parameters.encryption_type`/`encryption_type_desc`/`encryption_algorithm_name`/
`column_encryption_key_id` (Always Encrypted parameterization, SQL Server 2016+) are not extracted or
hashed for stored-procedure (or function) parameters — `ParameterSchema` has no corresponding field,
unlike `ColumnSchema.EncryptionTypeDesc` which is already captured for table columns. Marking,
un-marking, or rekeying a parameter for Always Encrypted parameterization currently does not change the
hash. Distinct from the existing "Always Encrypted column key identity not captured" entry above, which
is about table columns' `column_encryption_key_id`, not parameters.

### Numbered stored procedures not captured

Numbered procedures (`CREATE PROCEDURE proc;2`, `proc;3`, ...) are a deprecated, legacy shape whose own
body/parameter rows for procedure numbers ≥2 live only in `sys.numbered_procedures`/
`sys.numbered_procedure_parameters` — the base procedure (`;1`) is a normal row in
`sys.procedures`/`sys.parameters`, but neither numbered-procedure view is queried anywhere in
`SchemaExtractor`. Not present in Azure SQL Database at all (deprecated everywhere else it exists), but
a real historical shape on-prem: a numbered procedure beyond `;1` is entirely invisible to extraction, so
adding, altering, or dropping one does not change the hash.

### Function schema-binding (`WITH SCHEMABINDING`) not captured

`sys.sql_modules.is_schema_bound` is not extracted or hashed for functions; `FunctionSchema` has no
corresponding field. `WITH SCHEMABINDING` is common on scalar and inline-table-valued functions used
inside indexed views, computed columns, or other schema-bound modules, and is genuinely schema-relevant —
it locks the referenced tables/columns against ALTERs that would break the function, a real behavioral
difference from an otherwise-identical unbound function. Two functions identical in name, type,
parameters, flags, and body hash currently compare as identical whether or not one carries
`WITH SCHEMABINDING`. This is the function analog of the existing "Trigger schema-binding
(`WITH SCHEMABINDING`) not captured" entry above; unlike the stored-procedure case, `SCHEMABINDING` on a
function is not tied to native compilation, so it needs its own field rather than being subsumed by
`uses_native_compilation`.

### Function `EXECUTE AS` context not captured

`sys.sql_modules.execute_as_principal_id` — populated only when a function specifies
`WITH EXECUTE AS <principal>` — is not extracted or hashed; `FunctionSchema` has no corresponding field.
`NULL` covers both the unset default and an explicit `EXECUTE AS CALLER`, so the column alone cannot
distinguish those two, but it does distinguish `SELF`/`OWNER`/a named principal from the default, none of
which is captured today. This is the function analog of the existing "Trigger `EXECUTE AS` context not
captured" and "Stored procedure `EXECUTE AS` context not captured" entries above. Adding, removing, or
changing a function's `EXECUTE AS` clause to/from `SELF`, `OWNER`, or a named principal — a real change to
the security context the function body executes under — currently does not change the hash.

### Function `RETURNS NULL ON NULL INPUT` not captured

`sys.sql_modules.null_on_null_input` is not extracted or hashed for functions; `FunctionSchema` has no
corresponding field. This flag reflects whether a function was declared `RETURNS NULL ON NULL INPUT`, a
genuine calling-convention/optimization contract (the engine short-circuits to `NULL` without invoking the
function body when any argument is `NULL`) selected at CREATE/ALTER time, not runtime state. Two functions
identical in name, type, parameters, and body hash currently compare as identical whether or not one
carries `RETURNS NULL ON NULL INPUT`.

### Function database-collation dependency not captured

`sys.sql_modules.uses_database_collation` is not extracted or hashed for functions; `FunctionSchema` has
no corresponding field. The flag is `1` when a schema-bound function's correctness depends on the
database's default collation (it compares columns or literals without an explicit `COLLATE`) — this
blocks `ALTER DATABASE ... COLLATE` while the module exists, so it is a genuine, catalog-observable
cross-database comparability signal. Two functions identical in every other captured field currently
compare as identical regardless of this flag.

### Function native compilation flag not captured

`sys.sql_modules.uses_native_compilation` (SQL Server 2014+) is not extracted or hashed for functions;
`FunctionSchema` has no corresponding field. A natively-compiled (In-Memory OLTP) scalar function, created
`WITH NATIVE_COMPILATION, SCHEMABINDING`, is a fundamentally different execution-engine object from an
interpreted T-SQL function with the same signature and body — the same distinction `IsMemoryOptimized`
already captures for tables and table types, and the same gap already tracked for stored procedures in the
"Stored procedure native compilation not captured" entry above. Two otherwise-identical functions
currently compare as identical regardless of this flag.

### View `WITH CHECK OPTION` not captured

`sys.views.with_check_option` is not extracted or hashed; `ViewSchema` has no corresponding field.
`WITH CHECK OPTION` is a real DDL clause on `CREATE VIEW`/`ALTER VIEW` that changes the view's
INSERT/UPDATE enforcement behavior (rows that no longer satisfy the view's `WHERE` predicate are
rejected). Under the default `Strict` options, adding or removing `WITH CHECK OPTION` changes the raw
definition text and so happens to be reflected in `DefinitionHash` — but under
`ModuleNormalization.IgnoreBodyText`, or for any comparison that only inspects the typed fields, this
flag is invisible: two views identical apart from `WITH CHECK OPTION` compare as identical whenever
body text is excluded from the comparison.

### View `VIEW_METADATA` (has_opaque_metadata) not captured

`sys.views.has_opaque_metadata` — set when the view is created `WITH VIEW_METADATA` — is not extracted
or hashed; `ViewSchema` has no corresponding field. `VIEW_METADATA` changes what browse-mode metadata
(`sp_describe_first_result_set`, OLE DB/ODBC browse mode) reports for the view's base tables/columns, a
genuine behavioral difference for client tooling. As with `WITH CHECK OPTION` above, this is present in
the raw definition text and so happens to be reflected in `DefinitionHash` under default options, but
disappears entirely from the comparison surface under `ModuleNormalization.IgnoreBodyText`.

### Synonym explicit ownership (`ALTER AUTHORIZATION`) not captured

`sys.synonyms.principal_id` (inherited from `sys.objects`) is not extracted or hashed;
`SynonymSchema` has no corresponding field. It is `NULL` for a synonym owned by its schema's default
owner and becomes a real principal id only after an explicit `ALTER AUTHORIZATION ON <synonym> TO
<principal>`, the same "owner override, distinct from a system artifact" pattern already reasoned about
for other object kinds elsewhere in this file. Resolving it to a principal name (the same id-to-name
technique already used for `SCHEMA_NAME`/`OBJECT_NAME` lookups) would let the hash detect a synonym's
ownership being reassigned; currently, running `ALTER AUTHORIZATION` on a synonym does not change the
hash. Note this gap is not unique to synonyms: no object kind in this codebase currently captures
`sys.objects.principal_id`/explicit ownership, so this entry is the synonym instance of a broader,
currently-untracked gap.

### View schema-binding (`WITH SCHEMABINDING`) not captured

`sys.sql_modules.is_schema_bound` is not extracted or hashed for views; `ViewSchema` has no
corresponding field — the view analog of the "Trigger schema-binding (`WITH SCHEMABINDING`) not
captured" gap above. `WITH SCHEMABINDING` is a load-bearing clause: it is the prerequisite for creating
an index on the view, and it locks the referenced tables/columns against `ALTER`s that would break the
view. Like the check-option and view-metadata gaps above, this is present in the raw definition text (so
it is incidentally reflected in `DefinitionHash` under default options) but is invisible under
`ModuleNormalization.IgnoreBodyText`, and there is no discrete field a caller can inspect independent of
body-text diffing — unlike, e.g., `FunctionSchema.TypeDesc`, which is captured unconditionally precisely
so scalar/TVF identity survives `IgnoreBodyText`.

### Unreferenced alias scalar types not captured as standalone objects

An alias scalar type (`CREATE TYPE dbo.OrderTotal FROM DECIMAL(9,2) NOT NULL`) has no dedicated entry in
`SchemaMetadata` — unlike every other in-scope object kind (tables, stored procedures, table types,
views, functions, triggers, sequences, synonyms), which each have their own driving catalog query and
their own list. `SchemaExtractor` never queries `sys.types` for `is_user_defined = 1 AND is_assembly_type
= 0` on its own; the alias type's identity (schema-qualified name plus its underlying base
type/length/precision/scale/nullability) is observed only as a side effect of `EffectiveDataType` being
invoked when a table column, table-type column, procedure/function parameter, or sequence references it
via `user_type_id`. An alias type that exists but is not referenced anywhere is therefore completely
invisible: `CREATE TYPE`, `DROP TYPE`, or dropping-and-recreating over a different base
type/length/precision/scale/nullability while the type is unused does not change the hash.

**Workaround:** none currently — this would require treating alias scalar types as their own
`SchemaMetadata` list, extracted directly from `sys.types` rather than piggybacked on the objects that
reference them.

### Schema (namespace) objects not captured as their own catalog entity

`sys.schemas` is never queried by `SchemaExtractor`, and `SchemaMetadata` has no corresponding list,
unlike every other in-scope object kind (tables, stored procedures, table types, views, functions,
triggers, sequences, synonyms), each of which has its own driving catalog query and its own list. A
schema's name surfaces only indirectly, as the `SchemaName` qualifier resolved via `SCHEMA_NAME(...)` on
the objects extracted under it. An empty schema, or one whose entire contents are excluded via
`ObjectNamesToIgnore`/`SchemaFilter`, therefore has no representation anywhere in the extracted metadata:
creating, dropping, or renaming such a schema does not change the hash, since nothing else in the output
references its name.

**Workaround:** none currently: this would require a new `SchemaSchema`-style record extracted directly
from `sys.schemas`, the same dedicated-query-and-own-list shape every other in-scope object kind already
has.

### Schema ownership (`ALTER AUTHORIZATION ON SCHEMA`) not captured

`sys.schemas.principal_id` (resolvable to a principal name via `sys.database_principals`) is not
extracted or hashed for any schema, populated or empty. Ownership is a mutable attribute independent of
schema identity: `ALTER AUTHORIZATION ON SCHEMA::<name> TO <principal>` changes `principal_id` while
leaving `schema_id` and `name` untouched, and it is a real, DDL-driven security change (it reassigns who
owns every object subsequently created in that schema without an explicit owner). Since no `sys.schemas`
extraction exists at all (see the "Schema (namespace) objects not captured as their own catalog entity"
entry above), this axis is invisible regardless of whether the schema holds any objects: reassigning a
schema's owner currently does not change the hash.

### XML schema collection content not captured

`sys.xml_schema_collections` collections are not extracted as a standalone object list —
`SchemaMetadata` has no `XmlSchemaCollectionSchema`. A column or parameter typed `XML(<collection>)`
captures only the collection's schema-qualified name (`ColumnSchema.XmlSchemaCollectionName`/
`ParameterSchema.XmlSchemaCollectionName`) and the `DOCUMENT`/`CONTENT` facet (`IsXmlDocument`) — never
the collection's actual namespace set or XSD shape, which lives in `sys.xml_schema_namespaces` and is
only reconstructable via `XML_SCHEMA_NAMESPACE()`. Two consequences: (1) a collection referenced by a
column/parameter can have its content silently replaced — `ALTER XML SCHEMA COLLECTION ... ADD` a new
namespace, or `DROP`+`CREATE` the same name with an entirely different XSD — without changing the hash,
since the name (and `is_xml_document`) stay the same; (2) a collection that exists but is not bound to
any column or parameter is invisible to extraction entirely, so `CREATE`/`DROP XML SCHEMA COLLECTION` on
an unused collection does not change the hash. This is the XML-schema-collection analog of the
"Unreferenced alias scalar types not captured as standalone objects" gap above, but strictly worse: even
a *referenced* alias type's underlying shape is captured today, while a referenced XML schema
collection's content is not.

**Workaround:** none currently — this would require a new `XmlSchemaCollectionSchema` extracted directly
from `sys.xml_schema_collections` (excluding the built-in `sys.sys` collection) joined to
`sys.xml_schema_namespaces` (ordered by namespace `name`, not the not-reliably-meaningful
`xml_namespace_id`), hashing each namespace's `XML_SCHEMA_NAMESPACE(schema, collection, namespace)`
reconstructed form — never the original DDL text, which SQL Server does not preserve.

### Full-text catalogs not captured

`sys.fulltext_catalogs` is not queried anywhere in `SchemaExtractor`, and `SchemaMetadata` has no
corresponding record type. A full-text catalog's schema-relevant state, its `name`, whether it is the
`is_default` catalog used when `CREATE FULLTEXT INDEX` omits an explicit catalog, and
`is_accent_sensitivity_on` (set via `WITH ACCENT_SENSITIVITY = ON|OFF`), is entirely unobserved.
`CREATE FULLTEXT CATALOG` succeeds as pure metadata even on an instance where the Full-Text Search engine
component is not installed, so a database can legitimately carry full-text catalogs independent of
whether indexing on them will work; that makes catalog identity a genuine, always-observable schema fact
rather than something contingent on the feature being usable. This omission is not recorded in
CLAUDE.md's in-scope/out-of-scope object list, this file's "Out-of-scope object kinds" entry, or
`website/docs/what-gets-hashed.md`'s "Not yet captured" section, so a caller has no way to learn that
full-text catalogs are silently excluded. Adding, dropping, or renaming a full-text catalog, changing
which catalog is default, or toggling its accent sensitivity currently does not change the hash. (The
deprecated filegroup-era columns `data_space_id`/`file_id`/`path` and the transient `is_importing` flag
are correctly out of scope and are not part of this gap.)

**Workaround:** none currently, this would require a new `FullTextCatalogSchema`-style record extracted
directly from `sys.fulltext_catalogs`, the same dedicated-query-and-own-list shape every other in-scope
object kind already has.

### Full-text indexes not captured

`sys.fulltext_indexes` is not queried anywhere in `SchemaExtractor`; there is no corresponding record in
`SchemaMetadata`. A table's or indexed view's full-text index (at most one per object) carries several
schema-relevant properties that are entirely unobserved: which catalog it belongs to
(`fulltext_catalog_id`, resolvable to the catalog's name), its `KEY INDEX` (`unique_index_id`, resolvable
to that index's identity the same way other index/object ids are resolved elsewhere in this library),
`is_enabled` (an explicit, deliberately toggled `ENABLE`/`DISABLE` state, not crawl progress),
`change_tracking_state`/`change_tracking_state_desc` (`MANUAL`/`AUTO`/`OFF`), its associated stoplist
(`stoplist_id`, resolvable to identity) and search property list (`property_list_id`, resolvable to
identity, SQL Server 2012+), and, new on SQL Server 2025 (17.x), `index_version` (legacy vs. new
word-breaker/filter binaries, a genuine behavioral distinction on 2025+ that would need the same
version-gating pattern this library already uses elsewhere, e.g. the `HASHBYTES`/`CHECKSUM` capability
probe run once in `ExtractSchemaAsync`). The crawl/population runtime columns (`has_crawl_completed`,
`crawl_type`, `crawl_start_date`, `crawl_end_date`, `incremental_timestamp`) are correctly out of scope as
transient state and are not part of this gap; likewise, whether an index was created with `NO POPULATION`
is not itself recoverable from the catalog after creation, so that specific detail could never be
captured regardless of this gap. Adding, dropping, or reconfiguring a full-text index (its catalog, key
index, change tracking mode, enabled state, stoplist, property list, or, on 2025+, its word-breaker
version) currently does not change the hash.

**Workaround:** none currently, this would require a new `FullTextIndexSchema`-style record and the
associated id-to-name resolution for `unique_index_id`/`stoplist_id`/`property_list_id`.

### Full-text index columns not captured

`sys.fulltext_index_columns` is not queried anywhere in `SchemaExtractor`. Per-column full-text
participation state is entirely unobserved: which columns participate (`column_id`, resolvable via
`sys.columns` the same way other column references are resolved elsewhere in this library), the
per-column word-breaker `language_id` (a single full-text index can mix languages across its columns,
e.g. `LANGUAGE 1033` on one column and the neutral `LANGUAGE 0` on another), the `TYPE COLUMN`
document-filtering pairing (`type_column_id`, resolvable to the extension column's name, populated only
for a `varbinary(max)`/`image` column indexed with an explicit type column), and `statistical_semantics`
(SQL Server 2012+ Semantic Search participation). None of these have any corresponding field anywhere in
`SchemaMetadata`. Adding or removing a column from a full-text index, changing its language, changing its
`TYPE COLUMN` pairing, or toggling `STATISTICAL_SEMANTICS` currently does not change the hash.

**Workaround:** none currently, this is the column-level counterpart of the "Full-text indexes not
captured" entry above and would be extracted alongside it as a `List<FullTextIndexColumnSchema>` (or
similar) on the same new record.

## Resolved in v2

- **Anonymous temporal history table/index names leaked `object_id`** — a system-versioned table
  created without an explicit `HISTORY_TABLE` gets an auto-named history table
  (`MSSQL_TemporalHistoryFor_<object_id>`) and auto-created clustered index
  (`ix_MSSQL_TemporalHistoryFor_<object_id>`), both embedding a per-database object_id that previously
  hashed raw, so two databases built from identical DDL compared as `Different` under every preset.
  `SchemaHashCalculator.EffectiveTableName`/`HistoryIndexNameRewrite` now substitute a name derived from
  the versioned parent (captured via a new `sys.tables` reverse join exposed as
  `TableSchema.VersionedParentSchema`/`VersionedParentName`), unconditionally — an explicitly-named
  history table is unaffected. See `Fidelity/TemporalHistoryTableFidelityTests` and
  `Determinism/TemporalHistoryTableNormalizationTests`.
- **Object-coverage expansion** — extraction and hashing now cover views (including indexed-view
  indexes), T-SQL functions (scalar, inline TVF, multi-statement TVF, with return type captured even
  under `IgnoreBodyText`), DML triggers (including FIRST/LAST ordering and the excluded-parent-excludes-
  its-triggers scoping rule), sequences (deliberately excluding `current_value`, which is runtime state),
  and synonyms. CLR modules, scalar CLR UDTs, and DDL/server-scoped triggers remain out of scope.
- **Column order** — `column_id` is now captured and drives ordering, so two tables/table types
  differing only in column order (e.g. positionally-marshaled TVP columns) hash differently.
- **Identity seed/increment/NOT FOR REPLICATION** — `sys.identity_columns.seed_value`,
  `increment_value`, and `is_not_for_replication` are now captured and hashed.
- **Constraint disabled/trust state** — `is_disabled`/`is_not_trusted`/`is_not_for_replication` are now
  captured for FOREIGN KEY and CHECK constraints.
- **Constraint names + `IsSystemNamed`** — constraint names are now captured and hashed (with normalization
  options mirroring index name handling), and the authoritative `is_system_named` flag distinguishes a
  user-named constraint from a system-generated one.
- **Encrypted-proc sentinel** — a `WITH ENCRYPTION` procedure's unreadable body is now hashed as a
  distinct `<encrypted>` sentinel instead of collapsing to the hash of an empty definition.
- **Sparse/rowguidcol/masking/XML capture** — `is_sparse`, `is_rowguidcol`, Dynamic Data Masking state,
  and typed-XML schema-collection binding are now captured and hashed.
- **UDT schema-qualification** — a column or parameter typed with a user-defined type now hashes the
  type's schema-qualified name, so e.g. `dbo.IntList` and `staging.IntList` no longer collide.
- **Alias scalar type base type** — an alias scalar type's underlying system base type, length/precision/scale,
  and nullability are now captured for both columns and stored procedure parameters, so recreating an
  alias over a different base type (e.g. `DECIMAL(9,2)` → `BIGINT`) changes the hash.
- **`ObjectNamesToIgnore` matched by bare name, ignoring schema** — matching now also accepts a
  schema-qualified entry (`sales.Things`) to target a single schema; a bare entry (`Things`) remains an
  explicit all-schemas shorthand.
- **`ObjectNamesToIgnore` case-insensitivity was caller-dependent** — added
  `SchemaHashOptions.ObjectNameComparer` (default `StringComparer.OrdinalIgnoreCase`) so the library owns
  case behavior regardless of the comparer on the caller's set.

### Resolved fix details (`ObjectNamesToIgnore`)

#### `ObjectNamesToIgnore` matched by bare name, ignoring schema

**Was:** the extractor filtered with `objectNamesToIgnore.Contains(bareName)`, so an entry like
`"Things"` dropped the object from **every** schema (`dbo.Things` *and* `sales.Things`), with no way to
target one schema.

**Fix:** matching now accepts a schema-qualified entry (`sales.Things`, matches only that schema) or a
bare entry (`Things`, an explicit all-schemas shorthand). An object is excluded when a configured entry
equals its bare name or its `schema.name`. See `SchemaExtractor.IsIgnored`.

**Tests:** `Settings/ScopingTests.ObjectNamesToIgnore_SchemaQualified_TargetsSingleSchema` (precise
targeting) and `_BareName_MatchesAcrossSchemas` (the documented shorthand).

#### `ObjectNamesToIgnore` case-insensitivity was caller-dependent

**Was:** matching used the comparer of whatever `IReadOnlySet<string>` the caller assigned, so the
documented "case-insensitive" behavior silently broke if a caller passed a plain (ordinal) `HashSet`.

**Fix:** added `SchemaHashOptions.ObjectNameComparer` (default `StringComparer.OrdinalIgnoreCase`). The
extractor rebuilds the match set with this comparer, so the library owns case behavior regardless of the
assigned set. Callers set `ObjectNameComparer = StringComparer.Ordinal` for case-sensitive matching.

**Tests:** `Settings/ScopingTests.ObjectNamesToIgnore_IsCaseInsensitiveByDefault_RegardlessOfSetComparer`
and `_CaseSensitive_WhenComparerIsOrdinal`.
