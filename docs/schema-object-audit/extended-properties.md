# Extended Properties

## Catalog view

`sys.extended_properties` is the single catalog view that exposes every extended property in the current database, regardless of what kind of object the property is attached to. There is no per-object-type extended-property view (unlike, say, indexes or columns) — one row per `(class, major_id, minor_id, name)` tuple covers database-, schema-, object/column-, parameter-, index-, and table-type-scoped properties alike. `sys.fn_listextendedproperty(...)` is a legacy table-valued function that wraps the same data with name-based (rather than id-based) filtering; it is a convenient cross-check but the raw catalog view is the right extraction source.

Columns (confirmed against Microsoft Learn and by direct experimentation on SQL Server 2025, engine 17.0.1000.7):

| Column | Type | Meaning |
|---|---|---|
| `class` | `tinyint` | Discriminator for what kind of item owns the property. See class table below. |
| `class_desc` | `nvarchar(60)` | Text form of `class` (`DATABASE`, `OBJECT_OR_COLUMN`, `PARAMETER`, `SCHEMA`, `DATABASE_PRINCIPAL`, `ASSEMBLY`, `TYPE`, `INDEX`, `XML_SCHEMA_COLLECTION`, `MESSAGE_TYPE`, `SERVICE_CONTRACT`, `SERVICE`, `REMOTE_SERVICE_BINDING`, `ROUTE`, `DATASPACE`, `PARTITION_FUNCTION`, `DATABASE_FILE`, `PLAN_GUIDE`; class 8 is not given a name in the doc's `class_desc` list but empirically comes back as `TYPE_COLUMN`). Purely a display convenience over `class`; not independently significant. |
| `major_id` | `int` | Owning object id, interpreted per `class` (table below). Non-deterministic across databases on its own — must be resolved to a name. |
| `minor_id` | `int` | Sub-object id within the owner, interpreted per `class` (table below). Also non-deterministic on its own. |
| `name` | `sysname` | Property name. Unique together with `(class, major_id, minor_id)` — `sp_addextendedproperty` raises error 15233 ("Property cannot be added... already exists") on a duplicate, confirmed experimentally. |
| `value` | `sql_variant` | Property value. Any `sql_variant`-compatible type (tested `nvarchar` and `int` values directly; base type recoverable with `SQL_VARIANT_PROPERTY(value, 'BaseType')`). This is exactly the "schema change" signal for this object type, so both `name` and `value` must be hashed. |

All five columns are relevant to change detection; the view has no additional runtime-state columns to exclude — it is metadata-only by construction. The one caveat is that `major_id`/`minor_id` are catalog ids and must never be hashed raw (per-database, non-deterministic ids); they must be resolved to schema-qualified names/column names/index names/etc. before hashing.

## Class table (confirmed against docs + experiment)

Full class enumeration per Microsoft Learn (`sys.extended_properties` doc page, `ver17`): `0`=Database, `1`=Object or column, `2`=Parameter, `3`=Schema, `4`=Database principal, `5`=Assembly, `6`=Type, `7`=Index, `8`=User defined table type column, `10`=XML schema collection, `15`=Message type, `16`=Service contract, `17`=Service, `18`=Remote service binding, `19`=Route, `20`=Dataspace (filegroup or partition scheme), `21`=Partition function, `22`=Database file, `27`=Plan guide.

The six classes relevant to this audit's scope:

| class | class_desc | major_id resolves via | minor_id resolves via | Confirmed by |
|---|---|---|---|---|
| 0 | DATABASE | always `0` | always `0` | Database-level property (all `@level*` args omitted); row `major_id=0, minor_id=0`. |
| 1 | OBJECT_OR_COLUMN | `object_id` of the owning schema-scoped object (table, view, procedure, function, trigger, sequence, synonym, **or constraint** — anything with its own `sys.objects` row) | `0` for the object itself; `column_id` when targeting a column | Table-level: `major_id=OBJECT_ID(Widget), minor_id=0`. Column-level: same `major_id`, `minor_id=column_id`. Also independently confirmed for a `TRIGGER` (`major_id` = the trigger's own `object_id`) and a `DEFAULT` **constraint** (`major_id` = the constraint's own `object_id`, distinct from its parent table's). |
| 2 | PARAMETER | `object_id` of the owning procedure/function | `parameter_id` (1-based) | Property on `@WidgetId` (first param of `usp_GetWidget`) → `major_id`=proc's `object_id`, `minor_id=1`. |
| 3 | SCHEMA | `schema_id` | `0` | `major_id` matched `SCHEMA_ID('audit_extended_properties')` exactly. |
| 6 | TYPE | `user_type_id` (from `sys.types`) — **not** `sys.table_types.type_table_object_id** | `0` | See edge case 1 below — the single most important gotcha for table-type properties. |
| 7 | INDEX | `object_id` of the table/indexed view the index belongs to (same id space as class 1) | `index_id` | Property on `IX_Widget_Name` (nonclustered, `index_id=2`; PK clustered index is `index_id=1`) → `major_id`=`Widget`'s `object_id`, `minor_id=2`. |

Class 8 (`TYPE_COLUMN`) is also relevant — see edge case 2.

## `sp_addextendedproperty` level → class mapping

Verified valid inputs (Microsoft Learn, `sp_addextendedproperty` doc page):

- `@level0type`: `ASSEMBLY`, `CONTRACT`, `EVENT NOTIFICATION`, `FILEGROUP`, `MESSAGE TYPE`, `PARTITION FUNCTION`, `PARTITION SCHEME`, `REMOTE SERVICE BINDING`, `ROUTE`, `SCHEMA`, `SERVICE`, `USER`, `TRIGGER`, `TYPE`, `PLAN GUIDE`, `NULL`.
- `@level1type`: `AGGREGATE`, `DEFAULT`, `FUNCTION`, `LOGICAL FILE NAME`, `PROCEDURE`, `QUEUE`, `RULE`, `SEQUENCE`, `SYNONYM`, `TABLE`, `TABLE_TYPE`, `TYPE`, `VIEW`, `XML SCHEMA COLLECTION`, `NULL`.
- `@level2type`: `COLUMN`, `CONSTRAINT`, `EVENT NOTIFICATION`, `INDEX`, `PARAMETER`, `TRIGGER`, `NULL`.

Microsoft's docs explicitly deprecate two `@level0type` options relevant here: **`USER` is being removed — use `SCHEMA` instead**, and **`TYPE` as level-0 is being removed — use `SCHEMA` as level-0 and `TYPE` as level-1 instead**. The resulting catalog row (class 6, `major_id=user_type_id`) is identical either way — the deprecation affects only the authoring call surface, not what lands in `sys.extended_properties`.

## Objects created for this audit

All created under a dedicated schema (`audit_extended_properties`) in the shared `sqlschemahasher-audit` SQL Server 2025 container:

```sql
CREATE TABLE audit_extended_properties.Widget (
  Id INT NOT NULL PRIMARY KEY,
  Name NVARCHAR(100) NOT NULL,
  Notes NVARCHAR(MAX) NULL          -- later dropped, see edge case 7
);
CREATE INDEX IX_Widget_Name ON audit_extended_properties.Widget(Name);

CREATE TYPE audit_extended_properties.WidgetTableType AS TABLE (
  Id INT NOT NULL,
  Label NVARCHAR(50) NULL
);

CREATE PROCEDURE audit_extended_properties.usp_GetWidget
  @WidgetId INT,
  @IncludeNotes BIT = 0
AS BEGIN
  SELECT Id, Name FROM audit_extended_properties.Widget WHERE Id = @WidgetId;
END;

CREATE TRIGGER audit_extended_properties.trg_Widget_Audit
  ON audit_extended_properties.Widget AFTER INSERT AS BEGIN SET NOCOUNT ON; END;

ALTER TABLE audit_extended_properties.Widget
  ADD CONSTRAINT DF_Widget_Name DEFAULT (N'unnamed') FOR Name;
```

Final snapshot of every extended property created, `(class, major_id, minor_id)` order:

```
class|class_desc       |major_id |minor_id|name           |value_preview
-----|------------------|---------|--------|---------------|------------------------------
0    |DATABASE          |0        |0       |MS_Description |Audit database-level property
1    |OBJECT_OR_COLUMN  |615673241|0       |MS_Description |Widget table property
1    |OBJECT_OR_COLUMN  |615673241|0       |MS_EmptyTest   |(empty string, not NULL)
1    |OBJECT_OR_COLUMN  |615673241|0       |MS_IntTest     |42
1    |OBJECT_OR_COLUMN  |615673241|0       |MS_NullTest    |NULL
1    |OBJECT_OR_COLUMN  |615673241|2       |MS_Description |Widget.Name column property
1    |OBJECT_OR_COLUMN  |967674495|0       |MS_Description |Default constraint property
1    |OBJECT_OR_COLUMN  |983674552|0       |MS_Description |Trigger property
2    |PARAMETER         |663673412|1       |MS_Description |WidgetId parameter property
3    |SCHEMA            |7        |0       |MS_Description |Audit schema-level property
6    |TYPE              |257      |0       |MS_Description |WidgetTableType type property
7    |INDEX             |615673241|2       |MS_Description |IX_Widget_Name index property
8    |TYPE_COLUMN       |257      |2       |MS_Description |WidgetTableType.Label column property
```

`615673241` (Widget's `object_id`) is the join key shared by the table-, column-, and index-scoped class-1/class-7 rows; `257` (`WidgetTableType`'s `user_type_id`) is the join key shared by its class-6 and class-8 rows.

## Edge cases found by direct experimentation

1. **Table-type properties resolve through `user_type_id`, not `type_table_object_id`.** `TYPE_ID('...WidgetTableType')` = `sys.types.user_type_id` = `257`, matching `major_id` on both the class-6 and class-8 rows — while `sys.table_types.type_table_object_id` for the same type was a completely different number (`647673355`) that never appears in `sys.extended_properties`. Any extraction join to `sys.table_types` via `type_table_object_id` instead of `sys.types.user_type_id` will silently fail to resolve table-type extended properties.

2. **A table type's *column* extended properties land in a fourth class (8, `TYPE_COLUMN`)**, separate from class 6 (the type itself) and separate from class 1 (a regular table's columns). Confirmed by adding a property to `WidgetTableType.Label`: `class=8, major_id=257 (user_type_id), minor_id=2 (Label's column_id)`. Easy to miss if extraction treats "table type" as "just like a table, but class 6."

3. **`CONSTRAINT`-scoped properties land in class 1 keyed by the constraint's own `object_id`**, not the parent table's (constraints have their own `sys.objects` row). Nothing in the extended-property row itself distinguishes "this major_id is a constraint" from "this major_id is a table/trigger" — you must separately look up `sys.objects.type_desc` for the resolved id. This directly explains why extended properties on system-named constraints are hard to hash deterministically (auto-generated constraint names aren't stable across databases).

4. **`TRIGGER`-scoped properties are the simple case** — class 1 keyed by the trigger's own `object_id` (confirmed exactly matching `OBJECT_ID('...trg_Widget_Audit')`), resolved the same way as any other directly-extracted object kind.

5. **NULL value vs. empty-string value are genuinely distinct, and both differ from "property absent."** `@value = NULL` produces a row where `value` and `SQL_VARIANT_PROPERTY(value,'BaseType')` are both `NULL` (type info is lost entirely). `@value = N''` produces a zero-length `nvarchar` — `BaseType` still reports `nvarchar`, `LEN(...) = 0`. A hash must encode "value is NULL" as a distinct sentinel from "empty string."

6. **`(class, major_id, minor_id, name)` uniqueness is enforced by SQL Server itself** — a duplicate `sp_addextendedproperty` on the same target fails with error 15233. The resolved-target-name + property-name pair is therefore a safe natural sort/hash key with no synthetic tiebreaker needed.

7. **Extended properties cascade-delete with their owning column/object** — no orphaned rows. Added a property to a throwaway column (`Temp1`, `column_id=4`; note `column_id`s are never reused — a previously dropped `Notes` column had already consumed `column_id=3`), confirmed it existed, then `ALTER TABLE ... DROP COLUMN Temp1`: the property row was gone with no error. Extraction never needs to defensively filter stale-target rows.

8. **Extended properties are not allowed on memory-optimized tables** (documented restriction in `sp_addextendedproperty` Remarks; not independently re-tested here, but a hard server-enforced constraint worth flagging).

9. **`USER` and bare `TYPE` as level-0 types are documented as deprecated / scheduled for removal** in a future SQL Server version. Both still work today; Microsoft recommends `SCHEMA` as level-0 for everything schema-scoped, including table types and alias types. Only affects property-authoring T-SQL, not reading `sys.extended_properties` back out.

## Version gating

- No SQL Server version restriction was found specific to `sys.extended_properties` in current Microsoft Learn docs beyond the general multi-platform "Applies to" banner (SQL Server, Azure SQL Database, Azure SQL Managed Instance, Azure Synapse Analytics, PDW, Fabric SQL/Warehouse) — no minimum-version callout, consistent with this being a long-stable, pre-2016 catalog view. Well within this repo's documented SQL Server 2016+ baseline.
- All testing here was performed live against SQL Server 2025 (RTM), engine `17.0.1000.7`, Enterprise Developer Edition on Linux — the class list, `sp_addextendedproperty` level-type lists, and every behavioral edge case above were confirmed against that version; nothing in the class list or column semantics appeared version-gated within the product line.
- The only forward-looking version note found is the deprecation of `USER`/bare-`TYPE` as `@level0type` values (edge case 9) — scheduled for removal in "a future version of SQL Server," per the docs, with no specific version number given. Affects only property-authoring call sites, not extraction/read logic.

## Coverage audit

No gaps found.

`SchemaExtractor.ExtractExtendedPropertiesAsync`'s single query over `sys.extended_properties` matches this audit's findings closely, including every edge case called out above:

- Class 6 resolves `major_id` via `sys.types.user_type_id` (`ty.user_type_id`), not `sys.table_types.type_table_object_id` — edge case 1's gotcha is correctly avoided. The class-8 → column join (`ttc.object_id = tt.type_table_object_id`) correctly uses `type_table_object_id` for the *backing table* lookup, which is the right id for that join.
- Class 8 (`TYPE_COLUMN`) is extracted as its own case, distinct from class 6 and class 1, with `ObjectName` = the table type's name and `SubObjectName` = the column's name (edge case 2).
- Class-1 targets are restricted to the extracted object kinds (`U`, `V`, `P`, `FN`, `IF`, `TF`, `TR`, `SO`, `SN`), which deliberately excludes constraint object types — matching the existing, documented exclusion ("Properties on constraint objects are not captured" in `website/docs/what-gets-hashed.md`, and BUGS.md's "Definition-text..." is unrelated, but the constraint exclusion itself is called out in the "Extended properties" bullet of what-gets-hashed.md and mirrors edge case 3).
- Trigger-scoped properties (edge case 4) are captured directly via the trigger's own `object_id`, with a separate `LEFT JOIN sys.objects po` resolving the trigger's parent purely so a property on an excluded table's trigger is dropped along with it (mirrors the general "excluding a table takes its triggers with it" rule).
- NULL vs. empty-string values (edge case 5) are kept distinct: `ValueType` (`SQL_VARIANT_PROPERTY(..., 'BaseType')`) is NULL only for a NULL value, while an empty-string value still reports `ValueType = 'nvarchar'`, so `SchemaHashCalculator.HashExtendedProperty` hashing `(ValueType ?? "", Value ?? "")` cannot collide a NULL value with an empty string.
- `(class, major_id, minor_id)` are never hashed raw — all are resolved to schema-qualified names/column names/index names before reaching `ExtendedPropertySchema`, consistent with the "must be resolved to a name" requirement.

Class 6 properties on non-table-type scalar user-defined types (`CREATE TYPE ... FROM <base type>`) are not extracted — the query restricts class 6 to `ty.is_table_type = 1`. This was considered but not filed as a gap: `CLAUDE.md` explicitly scopes extended-property extraction as "table-type-scoped" (not "type-scoped" generally), consistent with the library's broader documented exclusion of "scalar/CLR user-defined types" as standalone tracked objects — the alias type itself is never extracted as an independent schema entity (only its underlying base type is captured inline wherever a column/parameter/sequence references it), so an extended property on the alias type's own declaration has no corresponding tracked identity to attach to. This is a documented, deliberate scope boundary, not an oversight.

Classes outside this audit's scope (4 `DATABASE_PRINCIPAL`, 5 `ASSEMBLY`, 10 `XML_SCHEMA_COLLECTION`, 15–22, 27 `PLAN_GUIDE`) are correctly absent from the query — none of their owning object kinds (principals, assemblies, XML schema collections, service broker objects, filegroups/partition functions/database files, plan guides) are extracted as first-class schema objects by this library, matching the "Out of scope object kinds" ledger in BUGS.md.
