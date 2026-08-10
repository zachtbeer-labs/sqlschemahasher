# XML Schema Collections

## Scope and test setup

Tested against SQL Server 2025 (RTM) 17.0.1000.7, container `sqlschemahasher-audit`, database `AuditDb`, schema `[audit_xml-schema-collections]`. Objects created:

- `PersonXsd` — a single-namespace collection (`urn:audit:person`) with one complex element.
- `NoNamespaceXsd` — a collection whose XSD has **no `targetNamespace`** (the "no target namespace" / chameleon-schema edge case).
- `MultiNsXsd` — created with namespace `urn:audit:orderv1`, then **`ALTER XML SCHEMA COLLECTION ... ADD`** a second, independent namespace `urn:audit:orderv2` (multi-namespace + incremental-growth edge case).
- Table `People` with columns: `Profile XML(PersonXsd)` (default CONTENT), `Untyped XML` (no collection), `Ping XML(NoNamespaceXsd)`, and `ProfileDoc XML(DOCUMENT PersonXsd)` (DOCUMENT-constrained, added via `ALTER TABLE`).
- Procedure `usp_TakesPerson(@p XML(PersonXsd))` to confirm the same linkage exists on parameters, not just columns.

## Catalog views that expose this object type

### `sys.xml_schema_collections` — the collection itself

One row per XML schema collection (schema-scoped, like a table). Columns (confirmed live against the engine, all 6 match Microsoft Learn exactly — this view has **no `is_ms_shipped` column**, unlike most other catalog views):

| Column | Type | Relevant to schema-change detection? |
|---|---|---|
| `xml_collection_id` | int | Identity surrogate only — database-local, not portable across databases; resolve to name, don't hash the id itself (same convention the rest of this codebase already follows for `object_id`/`schema_id`). |
| `schema_id` | int | Resolve to schema name; part of the object's qualified identity. |
| `principal_id` | int, nullable | Alternate individual owner (NULL = owned by the schema owner). Ownership metadata, not schema shape — same treatment as other object ownership fields elsewhere in this codebase. |
| `name` | sysname | Identity — part of the `(schema, name)` sort/hash key. |
| `create_date` | datetime | **Runtime state — exclude.** |
| `modify_date` | datetime | **Runtime state — exclude.** Bumps on every `ALTER XML SCHEMA COLLECTION ... ADD` (verified: `MultiNsXsd`'s `modify_date` moved forward after the `ALTER ADD`, `create_date` did not). |

**Filtering out the built-in collection**: every database has a pre-existing row `xml_collection_id = 1, name = 'sys', schema = 'sys'` (the reserved `xml`/`xs`/`xsi`/`fn`/`xdt`/`sqltypes` namespaces). Since there's no `is_ms_shipped` flag to filter it, extraction must exclude it explicitly by schema (`schema_id <> SCHEMA_ID('sys')`, or equivalently name/schema `<> ('sys','sys')`). Confirmed live: `SELECT * FROM sys.xml_schema_collections` returned this row alongside the three user-created ones.

**Important: none of these columns capture the collection's actual content/shape.** A schema collection's XSD payload is *not* a scalar column anywhere in `sys.xml_schema_collections` — it must be reconstructed (see below).

### `sys.xml_schema_namespaces` — one row per namespace in a collection

A collection is **not** 1:1 with a namespace — it's a set of namespaces (one per row here).

| Column | Type | Notes |
|---|---|---|
| `xml_collection_id` | int | FK to the owning collection. |
| `name` | nvarchar(4000) | Namespace URI. **Blank string (not NULL) means "no target namespace"** — confirmed live: `NoNamespaceXsd` has `name = ''`. |
| `xml_namespace_id` | int | Documented as "1-based ordinal that uniquely identifies the XML namespace **in the database**" — this is **inaccurate as written**: empirically it is scoped **per collection**, restarting at 1 for every collection (`PersonXsd` namespace has id 1; `MultiNsXsd`'s two namespaces have ids 1 and 2; the built-in `sys` collection's three namespaces have ids 1-3). Useful as a deterministic *within-collection* ordering key (it reflects creation/ALTER-ADD order — see below) but do not treat it as a global uniqueness key across the database. |

Live result set for the three user collections plus the built-in one:

```
xml_collection_id  xml_namespace_id  name
1                   1                http://www.w3.org/2001/XMLSchema
1                   2                http://schemas.microsoft.com/sqlserver/2004/sqltypes
1                   3                http://www.w3.org/XML/1998/namespace
65536 (PersonXsd)   1                urn:audit:person
65537 (NoNamespaceXsd) 1             (blank)
65538 (MultiNsXsd)  1                urn:audit:orderv1
65538 (MultiNsXsd)  2                urn:audit:orderv2
```

### Linkage on columns: `sys.columns`

| Column | Meaning |
|---|---|
| `xml_collection_id` | `0` when the column is not typed `xml`, or is untyped `xml` with no collection. Non-zero = FK to `sys.xml_schema_collections.xml_collection_id`. Confirmed: `Untyped XML` column has `xml_collection_id = 0`, same as a non-XML column — **`0` is a sentinel, not a valid id**, so a join must be `LEFT JOIN ... ON xsc.xml_collection_id = c.xml_collection_id` guarded by `c.xml_collection_id <> 0` (or rely on the `LEFT JOIN` producing NULL, which it does). |
| `is_xml_document` | bit. `1` = column declared `XML(DOCUMENT collection)` (single top-level element required), `0` = `XML(CONTENT collection)` (default, allows fragments). This is a real, independent schema fact that must be captured **alongside** `xml_collection_id` — same collection, different constraint. Confirmed: `ProfileDoc` (declared `XML(DOCUMENT PersonXsd)`) has `is_xml_document = 1` and the *same* `xml_collection_id` as `Profile` (declared plain `XML(PersonXsd)`, `is_xml_document = 0`). |
| `max_length` | Always `-1` for any `xml` column (typed or not) — not useful for distinguishing collection binding. |

### Linkage on parameters (and, by the same mechanism, return types): `sys.parameters`

Carries the identical two columns — `xml_collection_id` and `is_xml_document` — confirmed live for `usp_TakesPerson(@p XML(PersonXsd))` (`xml_collection_id = 65536`, `is_xml_document = 0`). Relevant if/when this codebase's parameter extraction is extended to type-check XML parameters against a collection; the same linkage/normalization concerns as columns apply (collection id must resolve to a name, not be hashed raw).

### Narrower, purpose-built join views (alternative to filtering `sys.columns`/`sys.parameters`)

- **`sys.column_xml_schema_collection_usages`** — `(object_id, column_id, xml_collection_id)`, one row **only** for columns that are actually xml-typed with a bound collection (no `0`-sentinel rows to filter). Confirmed live: querying it against `People` returned exactly 3 rows (`Profile`, `Ping`, `ProfileDoc`) — the `Id` and `Untyped` columns are absent entirely, unlike the `sys.columns` join which needs `xml_collection_id <> 0` filtering.
- **`sys.parameter_xml_schema_collection_usages`** — the parameter analog, same shape.

These may be a cleaner extraction join than `sys.columns`/`sys.parameters` directly, since they sidestep the `0`-sentinel-vs-NULL distinction.

## Retrieving a collection's XSD content: `XML_SCHEMA_NAMESPACE()`

Signature: `XML_SCHEMA_NAMESPACE(relational_schema, xml_schema_collection_name [, namespace_uri])` → returns an `xml` value.

Behavior confirmed live:

1. **Omitting the namespace argument** returns **every** namespace's fragment concatenated as sibling `<xsd:schema>` elements, in `xml_namespace_id` order (i.e., creation/ALTER-ADD order). Confirmed on `MultiNsXsd`: the 2-arg call returned two full `<xsd:schema>...</xsd:schema>` blocks (`orderv1` then `orderv2`) back to back.
2. **Passing a specific `namespace_uri`** returns only that namespace's fragment — and if the same namespace was populated across multiple `CREATE`/`ALTER ADD` steps, all its components are merged into one `<xsd:schema>` (per Microsoft Learn: *"If you specify a namespace parameter, the resulting schema document will contain definitions for all schema components in that namespace, even if they were added in different schema documents or DDL steps"*).
3. The "no target namespace" case works fine as the 2-arg form (namespace defaults to `''`), returning a `<xsd:schema>` with no `targetNamespace` attribute. Confirmed on `NoNamespaceXsd`.
4. **Cannot be used against the built-in `sys.sys` collection** — documented restriction, consistent with that collection needing to be excluded from extraction entirely anyway.

### Critical edge case: the reconstructed XSD is *not* a byte-faithful copy of what was submitted

This is the single most important finding for hash-determinism purposes. Confirmed empirically — our submitted `PersonXsd` fragment:

```xml
<xsd:schema xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns="urn:audit:person" targetNamespace="urn:audit:person" elementFormDefault="qualified">
  <xsd:element name="Person">
    <xsd:complexType>
      <xsd:sequence>
        <xsd:element name="Name" type="xsd:string"/>
        <xsd:element name="Age" type="xsd:int"/>
      </xsd:sequence>
    </xsd:complexType>
  </xsd:element>
</xsd:schema>
```

...came back from `XML_SCHEMA_NAMESPACE()` as:

```xml
<xsd:schema xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:t="urn:audit:person" targetNamespace="urn:audit:person" elementFormDefault="qualified"><xsd:element name="Person"><xsd:complexType><xsd:complexContent><xsd:restriction base="xsd:anyType"><xsd:sequence><xsd:element name="Name" type="xsd:string"/><xsd:element name="Age" type="xsd:int"/></xsd:sequence></xsd:restriction></xsd:complexContent></xsd:complexType></xsd:element></xsd:schema>
```

Differences: an implicit `<xsd:complexContent><xsd:restriction base="xsd:anyType">` wrapper was inserted; the submitted default-namespace prefix was replaced with a synthetic `t:` prefix (documented: SQL Server always uses `t` for the target namespace and `ns1`, `ns2`, ... for others); whitespace/formatting is gone. Microsoft Learn confirms this is by design: *"the schema reconstructed by xml_schema_namespace is functionally equivalent to the original schema, but it will not necessarily look the same... comments, white spaces, and annotations are lost; and implicit type information is made explicit... namespace prefixes are not preserved."*

**Implication for hashing**: there is no way to retrieve the SQL text originally passed to `CREATE`/`ALTER XML SCHEMA COLLECTION` — the server only stores the compiled component graph. Any hash of a collection's content must be based on the canonical `XML_SCHEMA_NAMESPACE()` output (or on the structural `sys.xml_schema_*` component catalog views), never on an assumption of round-tripping the original DDL text. The upside: this canonical form *is* itself deterministic and comparable across databases (same shape in, same reconstructed XSD out, regardless of original prefixes/whitespace) — which is actually the right property for a structural-equality hash. To get a stable, order-independent hash across namespaces, iterate `sys.xml_schema_namespaces` per collection (ordered by `name`, not by the not-reliably-meaningful `xml_namespace_id` if determinism across recreated databases matters) and call the 3-arg per-namespace form for each, rather than relying on the concatenated whole-collection 2-arg form.

## Other edge cases found by experimentation

- **`ALTER XML SCHEMA COLLECTION` is additive-only.** It can add new namespaces, or add new components to an *existing* namespace, but cannot modify or remove anything already present — the W3C `<xsd:redefine>` directive is explicitly rejected by the server. Practical consequence: a collection's content can only grow or be replaced wholesale (drop + recreate); it can never shrink or be edited in place. This means `modify_date` moving forward always means "something was added," never "something existing changed shape."
- **Drop is dependency-protected.** Attempting `DROP XML SCHEMA COLLECTION` on `PersonXsd` while `People.Profile`/`People.ProfileDoc` still reference it failed with `Msg 6328: Specified collection 'PersonXsd' cannot be dropped because it is used by object 'audit_xml-schema-collections.People'.` Confirms `xml_collection_id` linkages can't dangle mid-database — a referenced collection is guaranteed live.
- **DOCUMENT vs CONTENT is a per-usage flag, not a collection property** — the same collection can back both a `CONTENT`-constrained and a `DOCUMENT`-constrained column/parameter simultaneously (verified: `Profile` and `ProfileDoc` share `xml_collection_id = 65536` but differ in `is_xml_document`).
- **No-target-namespace collections are legitimate** and represented by an empty string (not NULL) in `sys.xml_schema_namespaces.name` — worth an explicit null-vs-empty-string check in any extraction code, matching this codebase's existing convention of treating "null vs empty" as meaningfully distinct (see `ExtendedPropertySchema.Value` in the codebase's own style, per its documented null/empty distinction).
- **`sys.xml_schema_namespaces.xml_namespace_id` documentation is misleading**: Microsoft Learn describes it as unique "in the database," but it is empirically scoped per `xml_collection_id`, restarting at 1 for each collection.
- Object dependency chain if extending coverage beyond the collection itself: `sys.xml_schema_collections` → `sys.xml_schema_namespaces` (namespace set) → `sys.xml_schema_components`/`sys.xml_schema_elements`/`sys.xml_schema_attributes`/`sys.xml_schema_types` (fine-grained component catalog, not queried in this pass) exist if a future finer-grained structural hash (rather than the `XML_SCHEMA_NAMESPACE()` reconstructed-document approach) is wanted.

## Version gating

No version-specific gating found or documented for this feature relative to this codebase's SQL Server 2016 (13.x) floor — XML schema collections and every catalog view/column used here (`sys.xml_schema_collections`, `sys.xml_schema_namespaces`, `sys.columns.xml_collection_id`/`is_xml_document`, `sys.parameters.xml_collection_id`/`is_xml_document`, `XML_SCHEMA_NAMESPACE()`) have existed unchanged since SQL Server 2005/2008 — well before this codebase's supported floor. Tested live against SQL Server 2025 (RTM) 17.0.1000.7 with identical column shapes to what Microsoft Learn documents for the current (`ver17`) docs branch — no drift observed. Two general (non-version-gated, always-true) XSD-processing limitations worth flagging for anyone hand-authoring test fixtures: `<xsd:redefine>` and `<xsd:include>` are always rejected by the server, and substitution-group members must all be declared within the same `CREATE`/`ALTER XML SCHEMA COLLECTION` statement.

## Coverage audit

- **XML schema collection content is never extracted or hashed**: `SchemaExtractor`/`SchemaMetadata` have no `XmlSchemaCollectionSchema` list driven from `sys.xml_schema_collections`. A column or parameter typed `XML(<collection>)` captures only the collection's schema-qualified name (`ColumnSchema.XmlSchemaCollectionName`/`ParameterSchema.XmlSchemaCollectionName`, both hashed in `SchemaHashCalculator`) and the `DOCUMENT`/`CONTENT` facet (`IsXmlDocument`, also hashed) — never the namespace set in `sys.xml_schema_namespaces` or the reconstructed XSD shape from `XML_SCHEMA_NAMESPACE()`. Consequences: a referenced collection's content can be silently replaced (`ALTER XML SCHEMA COLLECTION ... ADD`, or `DROP`+`CREATE` the same name with a different XSD) without changing the hash, since only the name and `is_xml_document` are captured; and a collection that exists but is unreferenced by any column/parameter is entirely invisible to extraction, so creating or dropping an unused collection does not change the hash either. Filed as "XML schema collection content not captured" in BUGS.md.
