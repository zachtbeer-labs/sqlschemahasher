# Partition Functions/Schemes and Filegroups

Blind research pass against a live SQL Server 2025 instance (container `sqlschemahasher-audit`, database `AuditDb`, schema `[audit_partition-filegroups]`). All findings below were confirmed either by direct catalog query against objects created for this pass, or cross-checked against Microsoft Learn (cited inline). Nothing in this document was read from the sqlschemahasher source tree.

## Objects created for this pass

```sql
CREATE SCHEMA [audit_partition-filegroups];

-- Three user filegroups, each with one file
ALTER DATABASE AuditDb ADD FILEGROUP FG_audpf_Primary2;
ALTER DATABASE AuditDb ADD FILE (NAME = 'audpf_fg1_file', FILENAME = '/var/opt/mssql/data/audpf_fg1.ndf', SIZE = 8MB) TO FILEGROUP FG_audpf_Primary2;
ALTER DATABASE AuditDb ADD FILEGROUP FG_audpf_Secondary;
ALTER DATABASE AuditDb ADD FILE (NAME = 'audpf_fg2_file', FILENAME = '/var/opt/mssql/data/audpf_fg2.ndf', SIZE = 8MB) TO FILEGROUP FG_audpf_Secondary;
ALTER DATABASE AuditDb ADD FILEGROUP FG_audpf_ReadOnly;
ALTER DATABASE AuditDb ADD FILE (NAME = 'audpf_fg3_file', FILENAME = '/var/opt/mssql/data/audpf_fg3.ndf', SIZE = 8MB) TO FILEGROUP FG_audpf_ReadOnly;
ALTER DATABASE AuditDb MODIFY FILEGROUP FG_audpf_ReadOnly READONLY;   -- edge case: read-only filegroup

-- Two partition functions: RANGE LEFT (int) and RANGE RIGHT (datetime2(3))
CREATE PARTITION FUNCTION pf_audpf_RangeLeft (int) AS RANGE LEFT FOR VALUES (100, 200, 300);
CREATE PARTITION FUNCTION pf_audpf_RangeRight (datetime2(3)) AS RANGE RIGHT FOR VALUES ('2020-01-01', '2021-01-01', '2022-01-01');

-- Two partition schemes: explicit filegroup list, and ALL TO
CREATE PARTITION SCHEME ps_audpf_RangeLeft AS PARTITION pf_audpf_RangeLeft TO (FG_audpf_Primary2, FG_audpf_Secondary, FG_audpf_Primary2, FG_audpf_Secondary);
CREATE PARTITION SCHEME ps_audpf_AllPrimary AS PARTITION pf_audpf_RangeRight ALL TO ([PRIMARY]);

-- Tables covering the different ways data_space_id is used
CREATE TABLE [audit_partition-filegroups].PartitionedTable (Id int NOT NULL, Val nvarchar(50) NULL, CONSTRAINT PK_PartitionedTable PRIMARY KEY CLUSTERED (Id)) ON ps_audpf_RangeLeft(Id);
CREATE TABLE [audit_partition-filegroups].SimpleFilegroupTable (Id int NOT NULL PRIMARY KEY, Val nvarchar(50) NULL) ON FG_audpf_Secondary;
CREATE TABLE [audit_partition-filegroups].DefaultFilegroupTable (Id int NOT NULL PRIMARY KEY, Val nvarchar(50) NULL) ON [PRIMARY];
CREATE TABLE [audit_partition-filegroups].LobSplitTable (Id int NOT NULL PRIMARY KEY, BigData nvarchar(max) NULL) ON FG_audpf_Primary2 TEXTIMAGE_ON FG_audpf_Secondary;

-- Edge case: an "unaligned" nonclustered index — base table partitioned, one index on a plain filegroup instead
CREATE TABLE [audit_partition-filegroups].UnalignedIndexTable (Id int NOT NULL, Code int NOT NULL, CONSTRAINT PK_UnalignedIndexTable PRIMARY KEY CLUSTERED (Id)) ON ps_audpf_RangeLeft(Id);
CREATE NONCLUSTERED INDEX IX_UnalignedIndexTable_Code ON [audit_partition-filegroups].UnalignedIndexTable(Code) ON FG_audpf_ReadOnly;
CREATE NONCLUSTERED INDEX IX_UnalignedIndexTable_Code_Aligned ON [audit_partition-filegroups].UnalignedIndexTable(Code) ON ps_audpf_RangeLeft(Id);
```

## Catalog views that expose this object type

| View | Rows for | Key columns |
|---|---|---|
| `sys.partition_functions` | Each partition function | `name`, `function_id`, `type`/`type_desc`, `fanout`, `boundary_value_on_right`, `is_system`, `create_date`, `modify_date` |
| `sys.partition_parameters` | The (single) input parameter of each partition function | `function_id`, `parameter_id`, `system_type_id`, `max_length`, `precision`, `scale`, `collation_name`, `user_type_id` |
| `sys.partition_range_values` | Each boundary value of each function | `function_id`, `boundary_id`, `parameter_id`, `value` (`sql_variant`) |
| `sys.partition_schemes` | Each partition scheme | inherits `sys.data_spaces` columns, plus `function_id` |
| `sys.destination_data_spaces` | Each partition→filegroup mapping of a scheme | `partition_scheme_id`, `destination_id`, `data_space_id` |
| `sys.data_spaces` | Every filegroup **and** every partition scheme (unified) | `name`, `data_space_id`, `type`/`type_desc`, `is_default`, `is_system` |
| `sys.filegroups` | Each filegroup only (subset of `sys.data_spaces` where `type='FG'`) | inherits `sys.data_spaces` columns, plus `filegroup_guid`, `is_read_only`, `is_autogrow_all_files` |
| `sys.indexes` | The **actual link point**: every heap/clustered index/nonclustered index | `data_space_id` — FK into `sys.data_spaces.data_space_id` (may resolve to either a filegroup or a partition scheme) |
| `sys.tables` | LOB/FILESTREAM overrides only | `lob_data_space_id`, `filestream_data_space_id` (0 when no override — **not** the table's primary data space) |
| `sys.partitions` | Each physical partition of each heap/index | `object_id`, `index_id`, `partition_number`, `data_compression`/`data_compression_desc`, `xml_compression`/`xml_compression_desc` (2022+), `rows` (runtime) |

## How the link actually works — `data_space_id`

There is **no single "table's filegroup" column**. Confirmed by inspecting `sys.all_columns` for `sys.tables`: no `data_space_id` there at all. The storage location lives on **`sys.indexes.data_space_id`**, one row per index (including the heap/clustered index representing the table's row data, `index_id` 0 or 1). Verified against the test tables:

```
table_name            index_name                          index_id  data_space_name       data_space_type
DefaultFilegroupTable PK__DefaultF...                      1        PRIMARY               ROWS_FILEGROUP
LobSplitTable          PK__LobSplit...                     1        FG_audpf_Primary2      ROWS_FILEGROUP
PartitionedTable       PK_PartitionedTable                 1        ps_audpf_RangeLeft     PARTITION_SCHEME
UnalignedIndexTable    PK_UnalignedIndexTable               1        ps_audpf_RangeLeft     PARTITION_SCHEME
UnalignedIndexTable    IX_UnalignedIndexTable_Code          2        FG_audpf_ReadOnly      ROWS_FILEGROUP   <- unaligned
UnalignedIndexTable    IX_UnalignedIndexTable_Code_Aligned  3        ps_audpf_RangeLeft     PARTITION_SCHEME
```

`sys.data_spaces` is the unifying catalog view: **both filegroups and partition schemes share one `data_space_id` numbering space**, disambiguated only by `type`/`type_desc` (`FG`=`ROWS_FILEGROUP`, `PS`=`PARTITION_SCHEME`, plus `FD`=`FILESTREAM_DATA_FILEGROUP` and, SQL Server 2014+, `FX`=`MEMORY_OPTIMIZED_DATA_FILEGROUP` — not produced in this pass). A schema-hashing tool must resolve `sys.indexes.data_space_id` through `sys.data_spaces` and branch on `type`, not assume it's always a filegroup name.

To resolve a *specific partition's* physical filegroup (not just "the scheme"), join `sys.partitions.partition_number` to `sys.destination_data_spaces.destination_id` (scoped by `partition_scheme_id = index's data_space_id`), then to `sys.filegroups`:

```
table_name         partition_number  filegroup_name
PartitionedTable   1                 FG_audpf_Primary2
PartitionedTable   2                 FG_audpf_Secondary
PartitionedTable   3                 FG_audpf_Primary2
PartitionedTable   4                 FG_audpf_Secondary
```

## LOB / FILESTREAM placement is separate from row-data placement

`sys.tables.lob_data_space_id` is **0** (not NULL) for every table with no LOB override, and only non-zero when `TEXTIMAGE_ON` (or a FILESTREAM/off-row column set) targets a different filegroup than the base row data:

```
table_name              lob_data_space_id  lob_fg_name
DefaultFilegroupTable   0                  NULL
LobSplitTable           3                  FG_audpf_Secondary   <- TEXTIMAGE_ON FG_audpf_Secondary, base rows on FG_audpf_Primary2
PartitionedTable        0                  NULL
```

This is a genuinely independent piece of schema state: two tables with identical columns/indexes can differ only in where their LOB overflow data lives, and that's invisible unless `lob_data_space_id` is hashed separately from the index `data_space_id`.

## Notable edge cases found by direct experimentation

1. **Partition functions/schemes/filegroups are not schema-scoped.** `sys.all_columns` confirms `sys.partition_functions`, `sys.partition_schemes`, `sys.filegroups`, and `sys.data_spaces` all lack a `schema_id` column. Microsoft Learn confirms this explicitly: *"The scope of a partition function and scheme is limited to the database in which they have been created. Within the database, partition functions reside in a separate namespace from other functions. Partition functions and partition schemes don't belong to a schema."* (Partitioned tables and indexes — Limitations). Any exclusion/scoping logic built around `[schema].[name]` qualification (as used elsewhere for tables/procs/etc.) does not apply here — these three object kinds need bare-name matching only, and live in one flat database-wide namespace (confirmed our `ps_audpf_*` and `pf_audpf_*` names collided against nothing despite being created outside any specific schema).

2. **`ALL TO` schemes pre-allocate an extra "NEXT USED" destination.** `ps_audpf_AllPrimary` (built with `ALL TO ([PRIMARY])` over a function with `fanout = 4`) produced **5** rows in `sys.destination_data_spaces`, not 4 — SQL Server printed `'PRIMARY' is marked as the next used filegroup` at creation time. By contrast, `ps_audpf_RangeLeft` (built with an explicit 4-filegroup `TO (...)` list over the same fanout) produced exactly 4 rows. A naive assumption that `destination_data_spaces` row count always equals `fanout` is wrong for `ALL TO` schemes; the extra row is a live "next split goes here" placeholder, not a used partition, and needs to be excluded (or handled) when hashing the scheme's fanout-to-filegroup mapping. Confirmed by direct query.

3. **`boundary_value_on_right` changes semantics without changing the boundary values.** Two functions can have identical `sys.partition_range_values.value` sets but opposite `RANGE LEFT`/`RANGE RIGHT` interpretation (`pf_audpf_RangeLeft` has `boundary_value_on_right = 0`, `pf_audpf_RangeRight` has `= 1`). This flag must be hashed alongside the boundary list, not treated as cosmetic — per Microsoft Learn, it also controls NULL-row placement (NULLs go to the left-most partition, unless `RANGE RIGHT` *and* the first boundary is NULL, in which case the left-most partition stays empty and NULLs land in the second partition).

4. **`SPLIT`/`MERGE` mutate the function in place and bump `modify_date`.** Verified with a scratch function: `ALTER PARTITION FUNCTION ... SPLIT RANGE (15)` incremented `fanout` 3→4, inserted a new `boundary_id` row between the existing two, and changed `modify_date`; `MERGE RANGE (15)` reversed all three. `create_date`/`modify_date` exist only on `sys.partition_functions` (not on `sys.partition_schemes` or `sys.filegroups`) — confirmed via column enumeration. `modify_date` itself is a timestamp and non-deterministic across databases (same caveat as any other catalog timestamp), so it's a signal that *something* changed, not something to hash directly — the actual detectable content is still `fanout` + the ordered `(boundary_id, value)` list + `boundary_value_on_right`.

5. **A table's row data and its individual indexes can live on different data spaces ("unaligned" indexes).** `UnalignedIndexTable`'s clustered PK sits on partition scheme `ps_audpf_RangeLeft`, one nonclustered index (`IX_UnalignedIndexTable_Code`) sits on a single plain filegroup (`FG_audpf_ReadOnly`, not partitioned at all), and a second nonclustered index (`IX_UnalignedIndexTable_Code_Aligned`) is explicitly placed on the same scheme to stay "aligned." All three are visible only via `sys.indexes.data_space_id`, confirming placement must be tracked per-index, not per-table.

6. **`data_compression` is per-partition, not per-index or per-table.** After `ALTER TABLE ... REBUILD PARTITION = 2 WITH (DATA_COMPRESSION = PAGE)` on `PartitionedTable`, `sys.partitions.data_compression_desc` showed `NONE, PAGE, NONE, NONE` across the four partitions of the same clustered index — confirmed directly. A hash that only records one compression value per index would miss this. (Whether compression belongs in a *schema* hash at all is a design decision — it's a physical/performance attribute analogous to index fill factor — but if it's ever included, it must be captured per `partition_number`, not per index.)

7. **`filegroup_guid` is a random per-creation GUID — must be excluded from any deterministic hash.** Verified: `PRIMARY` has `filegroup_guid = NULL`, while each user filegroup got a distinct random GUID (`B677E6B8-...`, `755E2A81-...`, `1FBD9B86-...`) at `ADD FILEGROUP` time, with no relation to filegroup name or content. This is the filegroup analogue of an object_id-derived artifact — recreating the identical filegroup on another database produces a different GUID, so it must never be hashed, only the filegroup's `name`/`type`/`is_read_only`/`is_autogrow_all_files`.

8. **Read-only status is filegroup-level catalog state, not tied to any object.** `ALTER DATABASE ... MODIFY FILEGROUP FG_audpf_ReadOnly READONLY` flips `sys.filegroups.is_read_only` from 0→1 with no schema change to the tables/indexes stored in it — confirmed. This is a legitimate schema-relevant flag to track (it constrains what operations are valid against objects placed there) but is entirely independent from the object definitions.

9. **`sys.database_files` (physical file paths/sizes/growth) is adjacent but explicitly out of scope for a schema hash.** Confirmed `physical_name` is an absolute container-local filesystem path (`/var/opt/mssql/data/audpf_fg1.ndf`) — inherently non-portable across environments/servers, exactly the kind of runtime/deployment state this library's design already excludes elsewhere (e.g. current index fragmentation, row counts). `size`/`growth`/`is_percent_growth` are likewise physical-storage runtime configuration, not logical schema. Only the *filegroup* (logical container) is schema-relevant; its *files* are not.

10. **`is_system` on `sys.partition_functions`/`sys.data_spaces` (documented, not directly reproduced here):** per Microsoft Learn, applies to SQL Server 2012 (11.x)+, flags objects auto-created for full-text index fragmentation (1 = used for full-text fragments). Reproducing this would require creating a full-text catalog over a partitioned column, which was out of scope for this pass — flagged here as a documented value worth excluding/filtering (analogous to other system-generated objects this library already excludes) rather than experimentally confirmed.

## Runtime state explicitly excluded from schema-change detection

- `sys.partitions.rows` (approximate live row count), `partition_id`, `hobt_id` — pure runtime/storage identifiers, regenerate on every rebuild.
- `sys.partition_functions.create_date`/`modify_date` — timestamps, not portable across databases (same class as other catalog timestamps already excluded elsewhere).
- `sys.filegroups.filegroup_guid` — random per creation (see edge case 7).
- `sys.database_files.*` (physical path, current size, VLF/growth state) — deployment-specific, not logical schema (see edge case 9).
- `sys.filegroups.log_filegroup_id` — Microsoft Learn: *"Identified for informational purposes only. Not supported... the value is NULL"* in current SQL Server; confirmed NULL for every filegroup in this pass.

## Version gating observed / documented

- **`sys.filegroups.is_autogrow_all_files`** — Microsoft Learn: *"Applies to: SQL Server 2016 (13.x) and later versions."* Since this library's minimum target is SQL Server 2016 (13.x), this column is safe to read unconditionally against every supported server.
- **`sys.data_spaces.is_system` / `sys.partition_functions.is_system`** — Microsoft Learn: *"Applies to: SQL Server 2012 (11.x) and later."* Below that (not a concern given the 2016 baseline) the column doesn't exist.
- **`FX` (`MEMORY_OPTIMIZED_DATA_FILEGROUP`) as a `sys.data_spaces.type`** — Microsoft Learn: *"Applies to: SQL Server 2014 (12.x) and later."* Below that, memory-optimized filegroups don't exist as a distinct type.
- **`sys.partitions.xml_compression`/`xml_compression_desc`** — Microsoft Learn: *"Applies to: SQL Server 2022 (16.x) and later versions."* Not present in earlier catalog versions.
- **Partitioning itself was Enterprise-only before SQL Server 2016 (13.x) SP1**; from 2016 SP1 onward it's available in every edition. Since the library's floor is "SQL Server 2016 (13.x)" (not explicitly SP1+), a bare 2016 RTM target could theoretically be Standard/Web edition without partitioning support at all — worth confirming whether the library's stated minimum implicitly assumes SP1+.
- **Azure SQL Database constraint (documented, not reproduced against Azure in this pass):** Microsoft Learn: *"In Azure SQL Database and SQL database in Fabric, all partitions must be placed on the `PRIMARY` filegroup because only the `PRIMARY` filegroup is provided."* Since this library explicitly targets Azure SQL as well as on-prem SQL Server (per its documented minimum-target statement), a schema extracted from Azure SQL DB will always show every partition destination resolving to `PRIMARY` — multi-filegroup partition layouts are an on-prem/MI-only distinction, not something Azure SQL DB schemas can ever exhibit.
- **Max partition count**: 15,000 since SQL Server 2012 (11.x); 1,000 before that. Not relevant given the 2016+ baseline, noted only for completeness.

## Coverage audit

`SchemaExtractor.cs`, `SchemaMetadata.cs`, and `SchemaHashCalculator.cs` were checked for any reference to `sys.partition_functions`, `sys.partition_parameters`, `sys.partition_range_values`, `sys.partition_schemes`, `sys.destination_data_spaces`, `sys.data_spaces`, `sys.filegroups`, `sys.indexes.data_space_id`, `sys.tables.lob_data_space_id`/`filestream_data_space_id`, or `sys.partitions`. None of these catalog views or columns are queried anywhere in the extractor, no corresponding fields exist on `TableSchema`/`IndexSchema`/any other `SchemaMetadata` record, and no partition/filegroup-related normalization exists in `SchemaHashCalculator` or `SchemaNormalization.cs`.

This entire object type is already covered by an existing, deliberate, documented exclusion: BUGS.md's "Partitioning and filegroup/data-space placement" entry under "Open / known limitations" ("Table/index partitioning and filegroup or data-space placement are not captured. Moving a table to a different filegroup, or repartitioning it, does not change the hash."), mirrored in `website/docs/what-gets-hashed.md`'s "Not yet captured" section. That statement is broad enough to encompass every sub-finding in this pass — partition function definitions (fanout, boundary values, `boundary_value_on_right`), partition scheme-to-filegroup mappings (including the `ALL TO` extra-destination behavior), per-index `data_space_id` resolution (including unaligned indexes), LOB/FILESTREAM placement overrides, and filegroup-level state (`is_read_only`, `is_autogrow_all_files`). Per the audit's own criteria, a finding already covered by a documented, deliberate exclusion is not a new gap.

No gaps found.
