# SQL Server Object-Type Coverage Audit Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Produce 16 blind-then-audited SQL Server object-type reference docs under `docs/schema-object-audit/`, and file confirmed extraction gaps in `BUGS.md`, by running one reusable `Workflow` script four times (once per topic batch) against a shared live SQL Server container.

**Architecture:** A single parameterized workflow script (`schema-object-audit.js`) pipelines each topic through two stages — a blind "Document" agent (no repo access, live-catalog + Microsoft Learn only) and an "Audit" agent (repo access, compares the blind doc against `SchemaExtractor.cs`/`SchemaMetadata.cs`/`SchemaHashCalculator.cs`, writes the final doc file, and files confirmed gaps in `BUGS.md`). The script is invoked once per batch with different `args.topics`, reusing the same `scriptPath`.

**Tech Stack:** `Workflow` tool (JS orchestration script), Docker (`mcr.microsoft.com/mssql/server:2025-latest`), `sqlcmd` inside the container, this repo's existing `.cs` extraction code as the audit target.

## Global Constraints

- SQL Server image: `mcr.microsoft.com/mssql/server:2025-latest` (matches `tests/SqlSchemaHash.IntegrationTests/SqlServerFixture.cs:19`).
- SA password: `DeepDishD@tabas3!` (matches `SqlServerFixture.cs:20`) — used only for this ephemeral audit container, never committed anywhere.
- Container name: `sqlschemahasher-audit`. Host port: `14339` (avoids colliding with a default `1433` SQL Server that might already be running locally).
- Doc-stage agents must not read this repository's source before writing their blind doc — this is the entire point of the audit design.
- Audit-stage agents must check `website/docs/what-gets-hashed.md`'s "Not yet captured" section and existing `BUGS.md` entries before filing a "gap" — a documented, deliberate exclusion is not a gap.
- Output paths: `docs/schema-object-audit/<slug>.md` (one per topic) and new `### <Title>` entries appended under `BUGS.md`'s `## Open / known limitations` heading, matching that section's existing format (title, prose description, optional `**Workaround:**` line).
- This plan documents and files gaps. It does not fix any gap found — remediation is separate, future, scoped work.
- No fabricated timestamps in the workflow script itself (`Date.now()`/`new Date()`/`Math.random()` throw inside `Workflow` scripts) — any date needed (e.g. for a `BUGS.md` entry) comes from the agent's own `git log -1` check or is simply omitted (the section is undated prose, not a changelog).

---

## File Structure

- **Create:** `docs/schema-object-audit/tables.md`, `indexes.md`, `constraints.md`, `extended-properties.md`, `stored-procedures.md`, `functions.md`, `triggers.md`, `views.md`, `user-defined-table-types.md`, `sequences.md`, `synonyms.md`, `alias-clr-udts.md`, `schemas.md`, `full-text.md`, `xml-schema-collections.md`, `partition-filegroups.md` — one per topic, written by each topic's Audit-stage agent.
- **Create:** `docs/schema-object-audit/README.md` — index written directly by the plan executor (not an agent) in the final task, summarizing all 16 topics with gap counts.
- **Modify:** `BUGS.md` — new `###` entries appended by Audit-stage agents, only for confirmed gaps.
- **No `.cs` files are modified by this plan.**

---

## Task 1: Start the shared SQL Server container

**Files:** none (infrastructure only; nothing in this task touches the repo).

- [ ] **Step 1: Start the container**

```bash
docker run -d --name sqlschemahasher-audit \
  -e "ACCEPT_EULA=Y" \
  -e "MSSQL_SA_PASSWORD=DeepDishD@tabas3!" \
  -p 14339:1433 \
  mcr.microsoft.com/mssql/server:2025-latest
```

- [ ] **Step 2: Wait for it to accept connections, then verify with a trivial query**

```bash
MSYS_NO_PATHCONV=1 docker exec sqlschemahasher-audit /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "DeepDishD@tabas3!" -C -Q "SELECT 1 AS ok"
```

(`MSYS_NO_PATHCONV=1` avoids Git Bash on Windows mangling the `/opt/...` container path.) Retry every few seconds (SQL Server inside the container typically takes 15-30s to accept connections after `docker run` returns) until this returns `ok / 1` instead of a connection-refused error. Do not proceed until it succeeds.

- [ ] **Step 3: No commit** — this task only starts ephemeral infrastructure; there is no repo state to commit.

---

## Task 2: Author the reusable workflow script and smoke-test it

**Files:**
- Create (via the `Workflow` tool call itself, which persists the script and returns its path): `schema-object-audit.js` — reused by every later task via `scriptPath`.

**Interfaces:**
- Produces: a `Workflow`-compatible script accepting `args = { batchLabel: string, containerName: string, dbPassword: string, topics: [{ slug: string, title: string, hint: string }] }`, returning an array of `{ slug, title, docFileWritten, bugsAppended, gaps: [{summary, detail}] }` — one entry per topic, consumed by Tasks 3-6 to verify each batch's outcome.

- [ ] **Step 1: Call the `Workflow` tool with the script below, `args.batchLabel = 'smoke'`, and a single throwaway topic that is NOT one of the 16 real topics** (so nothing from this step pollutes the real output set):

```js
export const meta = {
  name: 'schema-object-audit',
  description: 'Blind-document a SQL Server object type from a live catalog, then audit it against SchemaExtractor/SchemaMetadata',
  phases: [
    { title: 'Document' },
    { title: 'Audit' },
  ],
}

const DOC_SCHEMA = {
  type: 'object',
  properties: {
    markdown: { type: 'string' },
    catalogViewsUsed: { type: 'array', items: { type: 'string' } },
  },
  required: ['markdown', 'catalogViewsUsed'],
}

const AUDIT_SCHEMA = {
  type: 'object',
  properties: {
    gaps: {
      type: 'array',
      items: {
        type: 'object',
        properties: {
          summary: { type: 'string' },
          detail: { type: 'string' },
        },
        required: ['summary', 'detail'],
      },
    },
    docFileWritten: { type: 'string' },
    bugsAppended: { type: 'boolean' },
  },
  required: ['gaps', 'docFileWritten', 'bugsAppended'],
}

function docPrompt(topic) {
  return `You are documenting the SQL Server "${topic.title}" schema object type for an internal coverage audit in the repository at D:\\code\\sqlschemahasher. This is a BLIND research pass: do NOT read any source file in this repository (no .cs files, no docs/ or website/docs/ content, no BUGS.md). Work only from the live SQL Server catalog and official Microsoft Learn documentation.

A shared SQL Server 2025 container named "${args.containerName}" is already running and reachable. This environment's Bash tool is Git Bash on Windows, which mangles \`/opt/...\` style paths unless MSYS path conversion is disabled — always prefix docker exec calls with MSYS_NO_PATHCONV=1. Query it with:
  MSYS_NO_PATHCONV=1 docker exec ${args.containerName} /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "${args.dbPassword}" -C -Q "<your T-SQL>"

Work inside your own schema so you do not collide with other topics running concurrently against the same shared container:
  CREATE SCHEMA audit_${topic.slug};
Create a couple of representative example objects for "${topic.title}" in that schema, covering at least one interesting edge case. ${topic.hint}
Query the relevant sys.* catalog view(s) against the objects you created. Cross-check field meanings against Microsoft Learn documentation.

Return a JSON object with:
- markdown: a self-contained Markdown document (start with a "# ${topic.title}" heading) describing which sys.* catalog view(s) expose this object type, every column/flag relevant to detecting a schema change (explicitly excluding runtime state), notable edge cases you found by direct experimentation, and any SQL Server version gating you observed or found documented.
- catalogViewsUsed: the sys.* view names you queried.`
}

function auditPrompt(doc, topic) {
  return `You are auditing SQL Server schema-hash extraction coverage for the "${topic.title}" object type in the zachtbeer.SqlSchemaHasher repository at D:\\code\\sqlschemahasher.

Below is a BLIND catalog documentation pass for this object type, written without looking at this repository's source:

---
${doc.markdown}
---

Now read src/SchemaExtractor.cs, src/SchemaMetadata.cs, and src/SchemaHashCalculator.cs. Compare what is actually extracted and hashed for "${topic.title}" against the blind doc above.

List a gap ONLY where a catalog field is: (1) present and queryable per the blind doc, AND (2) semantically relevant to detecting a schema change (not runtime state, not already covered under a different name), AND (3) not currently extracted, or extracted but not hashed, or extracted incorrectly.

Before listing a gap, read website/docs/what-gets-hashed.md's "Not yet captured" section and read BUGS.md in full. If the gap is already a documented, deliberate exclusion, it is NOT a gap — do not list it and do not re-file it.

For each confirmed gap, append a new "### <short title>" entry under BUGS.md's "## Open / known limitations" heading (read the file first and match its existing entries' structure: a title, a prose description of the gap, and an optional "**Workaround:** ..." line if one exists). Do not touch any other part of BUGS.md.

Then write the file docs/schema-object-audit/${topic.slug}.md containing the blind doc's markdown verbatim, followed by a "## Coverage audit" heading listing each gap as a "- **<summary>**: <detail>" bullet, or the single line "No gaps found." if there were none.

Return a JSON object with: gaps (array of {summary, detail} for each confirmed gap — empty array if none), docFileWritten (the path you wrote), bugsAppended (true if you appended to BUGS.md, false otherwise).`
}

phase('Document')
const results = await pipeline(
  args.topics,
  topic => agent(docPrompt(topic), { label: `doc:${topic.slug}`, phase: 'Document', schema: DOC_SCHEMA, agentType: 'general-purpose' }),
  (doc, topic) => agent(auditPrompt(doc, topic), { label: `audit:${topic.slug}`, phase: 'Audit', schema: AUDIT_SCHEMA, agentType: 'general-purpose' }).then(audit => ({ topic, doc, audit }))
)

const settled = results.filter(Boolean)
const totalGaps = settled.reduce((sum, r) => sum + (r.audit?.gaps?.length || 0), 0)
log(`Batch ${args.batchLabel}: ${settled.length}/${args.topics.length} topics completed, ${totalGaps} gaps filed`)

return settled.map(r => ({
  slug: r.topic.slug,
  title: r.topic.title,
  docFileWritten: r.audit?.docFileWritten ?? null,
  bugsAppended: r.audit?.bugsAppended ?? false,
  gaps: r.audit?.gaps ?? [],
}))
```

Invoke it with:

```js
args = {
  batchLabel: 'smoke',
  containerName: 'sqlschemahasher-audit',
  dbPassword: 'DeepDishD@tabas3!',
  topics: [
    { slug: 'smoke-test-permissions', title: '(Smoke Test) Server Permissions', hint: 'This is a plumbing smoke test, not a real audit topic — keep it brief.' },
  ],
}
```

- [ ] **Step 2: Verify the smoke test actually exercised the full pipeline**

Check that `docs/schema-object-audit/smoke-test-permissions.md` was created and contains a non-trivial "# (Smoke Test) Server Permissions" doc plus a "## Coverage audit" section. If the workflow errored, or the file is missing/empty, diagnose (read `<transcriptDir>/journal.jsonl` per the `Workflow` tool's resume guidance) and fix the script before proceeding — do not move on to a real batch on a broken pipeline.

- [ ] **Step 3: Discard the smoke-test artifacts**

```bash
rm -f docs/schema-object-audit/smoke-test-permissions.md
git diff BUGS.md
```

If the smoke test caused a `### ...` entry to be appended to `BUGS.md` about server permissions, revert just that addition (it is not one of the 16 real topics and should not ship). If `docs/schema-object-audit/` is now empty, that's expected — Task 3 populates it for real.

- [ ] **Step 4: No commit** — smoke-test artifacts are deliberately not kept.

---

## Task 3: Run Batch A — Tables, Indexes, Constraints, Extended Properties

**Files:**
- Create: `docs/schema-object-audit/tables.md`, `docs/schema-object-audit/indexes.md`, `docs/schema-object-audit/constraints.md`, `docs/schema-object-audit/extended-properties.md`
- Modify: `BUGS.md` (only if gaps are confirmed)

- [ ] **Step 1: Run the workflow, reusing the script from Task 2 by its `scriptPath`**

```js
args = {
  batchLabel: 'A',
  containerName: 'sqlschemahasher-audit',
  dbPassword: 'DeepDishD@tabas3!',
  topics: [
    { slug: 'tables', title: 'Tables', hint: 'Cover identity columns, computed columns (persisted vs. not), temporal system-versioning (including the history-table linkage), memory-optimized tables, and masked/encrypted columns.' },
    { slug: 'indexes', title: 'Indexes', hint: 'Cover clustered vs. nonclustered, columnstore vs. rowstore, filtered-index predicates, INCLUDE columns, key sort order (ASC/DESC), fill factor, and indexes on views.' },
    { slug: 'constraints', title: 'Constraints', hint: 'Cover PRIMARY KEY/UNIQUE, FOREIGN KEY (including ON DELETE/ON UPDATE actions), CHECK, and DEFAULT constraints. Note whether the catalog distinguishes a system-generated constraint name from a user-supplied one.' },
    { slug: 'extended-properties', title: 'Extended Properties', hint: 'Cover sys.extended_properties classes for database-, schema-, object/column-, parameter-, index-, and table-type-scoped properties, and how (class, major_id, minor_id) resolves to a target.' },
  ],
}
```

Call `Workflow({ scriptPath: '<path returned by Task 2>', args })`.

- [ ] **Step 2: Verify the batch's return value and files**

Confirm the workflow's return array has 4 entries, each with a non-null `docFileWritten`. Confirm the 4 files listed above exist and each has a non-empty "## Coverage audit" section. Read `git diff BUGS.md` and sanity-check any new entries are genuinely gaps (not already-documented exclusions) — if one looks wrong, fix or remove it directly before committing.

- [ ] **Step 3: Commit**

```bash
git add docs/schema-object-audit/tables.md docs/schema-object-audit/indexes.md docs/schema-object-audit/constraints.md docs/schema-object-audit/extended-properties.md BUGS.md
git commit -m "docs: batch A of SQL Server object-type coverage audit (tables, indexes, constraints, extended properties)"
```

(Omit `BUGS.md` from the `git add` if this batch found no gaps.)

---

## Task 4: Run Batch B — Stored Procedures, Functions, Triggers, Views

**Files:**
- Create: `docs/schema-object-audit/stored-procedures.md`, `docs/schema-object-audit/functions.md`, `docs/schema-object-audit/triggers.md`, `docs/schema-object-audit/views.md`
- Modify: `BUGS.md` (only if gaps are confirmed)

- [ ] **Step 1: Run the workflow with the same reused `scriptPath`**

```js
args = {
  batchLabel: 'B',
  containerName: 'sqlschemahasher-audit',
  dbPassword: 'DeepDishD@tabas3!',
  topics: [
    { slug: 'stored-procedures', title: 'Stored Procedures', hint: 'Cover parameters (OUTPUT direction, READONLY table-valued parameters), and how a procedure body definition can be hashed (e.g. via HASHBYTES) versus an older-server fallback.' },
    { slug: 'functions', title: 'Functions', hint: 'Cover the three function type_desc values (scalar / inline table-valued / multi-statement table-valued) and how a scalar function\'s return type appears as its own parameter row.' },
    { slug: 'triggers', title: 'Triggers', hint: 'Cover DML trigger event sets (INSERT/UPDATE/DELETE), FIRST/LAST ordering (sp_settriggerorder), INSTEAD OF vs. AFTER, disabled/NOT FOR REPLICATION flags, and separately note DDL/server-scoped triggers as a distinct catalog surface from object (DML) triggers.' },
    { slug: 'views', title: 'Views', hint: 'Cover indexed (schemabound) views, CREATE-time SET options, and why a view\'s column list can go stale without sp_refreshview.' },
  ],
}
```

Call `Workflow({ scriptPath: '<same path as Task 3>', args })`.

- [ ] **Step 2: Verify** — same checks as Task 3 Step 2, against this batch's 4 files.

- [ ] **Step 3: Commit**

```bash
git add docs/schema-object-audit/stored-procedures.md docs/schema-object-audit/functions.md docs/schema-object-audit/triggers.md docs/schema-object-audit/views.md BUGS.md
git commit -m "docs: batch B of SQL Server object-type coverage audit (stored procedures, functions, triggers, views)"
```

(Omit `BUGS.md` if no gaps were found.)

---

## Task 5: Run Batch C — User-Defined Table Types, Sequences, Synonyms, Alias/CLR UDTs

**Files:**
- Create: `docs/schema-object-audit/user-defined-table-types.md`, `docs/schema-object-audit/sequences.md`, `docs/schema-object-audit/synonyms.md`, `docs/schema-object-audit/alias-clr-udts.md`
- Modify: `BUGS.md` (only if gaps are confirmed)

- [ ] **Step 1: Run the workflow with the same reused `scriptPath`**

```js
args = {
  batchLabel: 'C',
  containerName: 'sqlschemahasher-audit',
  dbPassword: 'DeepDishD@tabas3!',
  topics: [
    { slug: 'user-defined-table-types', title: 'User-Defined Table Types', hint: 'Cover sys.table_types, its link to an underlying sys.tables row via type_table_object_id, and how its columns/constraints share the same catalog views as ordinary tables.' },
    { slug: 'sequences', title: 'Sequences', hint: 'Cover start value, increment, min/max bounds, cycling, and cache size, and explain why current_value is runtime state rather than schema.' },
    { slug: 'synonyms', title: 'Synonyms', hint: 'Cover sys.synonyms and note that base_object_name is stored verbatim and unvalidated at CREATE time.' },
    { slug: 'alias-clr-udts', title: 'Alias and CLR User-Defined Types', hint: 'Cover sys.types where is_user_defined = 1: alias scalar types (backed by a system_type_id) versus CLR user-defined types (assembly-backed), and how an alias type\'s underlying base type/length/precision/scale is queryable.' },
  ],
}
```

Call `Workflow({ scriptPath: '<same path as Task 3>', args })`.

- [ ] **Step 2: Verify** — same checks as Task 3 Step 2, against this batch's 4 files.

- [ ] **Step 3: Commit**

```bash
git add docs/schema-object-audit/user-defined-table-types.md docs/schema-object-audit/sequences.md docs/schema-object-audit/synonyms.md docs/schema-object-audit/alias-clr-udts.md BUGS.md
git commit -m "docs: batch C of SQL Server object-type coverage audit (table types, sequences, synonyms, alias/CLR UDTs)"
```

(Omit `BUGS.md` if no gaps were found.)

---

## Task 6: Run Batch D — Schemas, Full-Text, XML Schema Collections, Partitioning/Filegroups

**Files:**
- Create: `docs/schema-object-audit/schemas.md`, `docs/schema-object-audit/full-text.md`, `docs/schema-object-audit/xml-schema-collections.md`, `docs/schema-object-audit/partition-filegroups.md`
- Modify: `BUGS.md` (only if gaps are confirmed)

- [ ] **Step 1: Run the workflow with the same reused `scriptPath`**

```js
args = {
  batchLabel: 'D',
  containerName: 'sqlschemahasher-audit',
  dbPassword: 'DeepDishD@tabas3!',
  topics: [
    { slug: 'schemas', title: 'Schemas', hint: 'Cover sys.schemas as a container object itself: principal_id ownership, and whether an empty schema with no objects is distinguishable in the catalog.' },
    { slug: 'full-text', title: 'Full-Text Indexes/Catalogs', hint: 'Cover sys.fulltext_catalogs, sys.fulltext_indexes, and sys.fulltext_index_columns: which columns participate, language, and change-tracking mode.' },
    { slug: 'xml-schema-collections', title: 'XML Schema Collections', hint: 'Cover sys.xml_schema_collections, how a column\'s xml_collection_id links to one, and how to retrieve the collection\'s XSD content.' },
    { slug: 'partition-filegroups', title: 'Partition Functions/Schemes and Filegroups', hint: 'Cover sys.partition_functions, sys.partition_schemes, sys.filegroups, and how a table/index\'s data_space_id links to a filegroup or partition scheme.' },
  ],
}
```

Call `Workflow({ scriptPath: '<same path as Task 3>', args })`.

- [ ] **Step 2: Verify** — same checks as Task 3 Step 2, against this batch's 4 files. For this batch specifically, expect most/all findings to confirm the library's documented exclusions (`website/docs/what-gets-hashed.md`'s "Not yet captured" list already covers CLR/partitioning) — a real "gap" here is one that ISN'T already covered by that documented exclusion (e.g., full-text and XML schema collections aren't mentioned there at all, so a finding for either is likely new).

- [ ] **Step 3: Commit**

```bash
git add docs/schema-object-audit/schemas.md docs/schema-object-audit/full-text.md docs/schema-object-audit/xml-schema-collections.md docs/schema-object-audit/partition-filegroups.md BUGS.md
git commit -m "docs: batch D of SQL Server object-type coverage audit (schemas, full-text, XML schema collections, partitioning)"
```

(Omit `BUGS.md` if no gaps were found.)

---

## Task 7: Tear down the container and publish the index

**Files:**
- Create: `docs/schema-object-audit/README.md`

- [ ] **Step 1: Stop and remove the shared container**

```bash
docker stop sqlschemahasher-audit
docker rm sqlschemahasher-audit
```

- [ ] **Step 2: Confirm all 16 topic files exist**

```bash
ls docs/schema-object-audit
```

Expected: `tables.md`, `indexes.md`, `constraints.md`, `extended-properties.md`, `stored-procedures.md`, `functions.md`, `triggers.md`, `views.md`, `user-defined-table-types.md`, `sequences.md`, `synonyms.md`, `alias-clr-udts.md`, `schemas.md`, `full-text.md`, `xml-schema-collections.md`, `partition-filegroups.md` — 16 files, no `smoke-test-permissions.md`.

- [ ] **Step 3: Write the index**

Using the gap counts/titles returned by each batch's `Workflow` call (Tasks 3-6, Step 1 return values), write `docs/schema-object-audit/README.md`:

```markdown
# SQL Server Object-Type Coverage Audit

Sixteen SQL Server schema object types, each documented blind against a live SQL Server 2025 catalog
(no repository source consulted), then audited against `SchemaExtractor.cs`/`SchemaMetadata.cs`/
`SchemaHashCalculator.cs`. Confirmed gaps are filed in [`BUGS.md`](../../BUGS.md) under
"Open / known limitations". See the design spec at
[`docs/superpowers/specs/2026-08-09-schema-object-audit-design.md`](../superpowers/specs/2026-08-09-schema-object-audit-design.md).

| Topic | Doc | Gaps filed |
|---|---|---|
| Tables | [tables.md](tables.md) | <count> |
| Indexes | [indexes.md](indexes.md) | <count> |
| Constraints | [constraints.md](constraints.md) | <count> |
| Extended Properties | [extended-properties.md](extended-properties.md) | <count> |
| Stored Procedures | [stored-procedures.md](stored-procedures.md) | <count> |
| Functions | [functions.md](functions.md) | <count> |
| Triggers | [triggers.md](triggers.md) | <count> |
| Views | [views.md](views.md) | <count> |
| User-Defined Table Types | [user-defined-table-types.md](user-defined-table-types.md) | <count> |
| Sequences | [sequences.md](sequences.md) | <count> |
| Synonyms | [synonyms.md](synonyms.md) | <count> |
| Alias/CLR User-Defined Types | [alias-clr-udts.md](alias-clr-udts.md) | <count> |
| Schemas | [schemas.md](schemas.md) | <count> |
| Full-Text Indexes/Catalogs | [full-text.md](full-text.md) | <count> |
| XML Schema Collections | [xml-schema-collections.md](xml-schema-collections.md) | <count> |
| Partition Functions/Schemes & Filegroups | [partition-filegroups.md](partition-filegroups.md) | <count> |
```

Replace each `<count>` with the actual number of gaps that batch's return value reported for that topic (from Tasks 3-6, Step 1).

- [ ] **Step 4: Commit**

```bash
git add docs/schema-object-audit/README.md
git commit -m "docs: add index for the SQL Server object-type coverage audit"
```

---

## Self-Review Notes

- **Spec coverage:** Task 1 = shared container (spec §"Shared SQL Server container"). Task 2 = pipeline mechanics + smoke test (validates spec §"Per-batch pipeline" before committing to all 16 topics). Tasks 3-6 = the 4 batches exactly as scoped in the spec §"Scope: 16 object types, batched into 4 workflow runs". Task 7 = teardown (spec §"Shared SQL Server container") plus the index deliverable. `BUGS.md`-only, no-fix scope is stated in Global Constraints, matching spec §"Out of scope".
- **Placeholder scan:** the only `<...>` markers left are in the Task 7 README template, which is explicit fill-in-from-real-data (documented as such), not an unresolved unknown.
- **Type consistency:** `DOC_SCHEMA`/`AUDIT_SCHEMA` and the `topic { slug, title, hint }` shape are defined once in Task 2 and referenced identically (same field names) in Tasks 3-6's `args.topics` blocks.
