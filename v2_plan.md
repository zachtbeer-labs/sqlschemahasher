# SqlSchemaHasher v2 — Object-Coverage Expansion (Views, Functions, Triggers, Sequences, Synonyms) + Final Polish

This document is a self-contained work plan for the last feature round of the v2 release of `zachtbeer.SqlSchemaHasher`. It is written to be handed to an implementing agent with repo access but no prior conversation context. Read `CLAUDE.md` first — it describes the architecture (facade → extractor → metadata → calculator), the normalization enums, and the versioned hash envelope.

## Background and current state

The library computes deterministic SHA256 hashes of SQL Server database schemas for change detection. The previous v2 wrap-up plan is **fully implemented and audited** on `zb/v2-prep`:

- `SchemaExtractor`/`SchemaHashCalculator` are `internal`; the public API is the `SqlSchemaHash` facade + `SchemaHashOptions`, `SchemaHashResult`/`SchemaHashComparison`, the five normalization enums, and the `SchemaMetadata` record family.
- `CancellationToken` support and facade argument validation are in place.
- Alias scalar types are captured (`[dbo].[OrderTotal]{decimal(9,2) NOT NULL}` rendering) for columns, table-type columns, and proc parameters.
- Tests: ~195 integration tests (Testcontainers) + a DB-free unit project (`tests/SqlSchemaHash.UnitTests`, 31 green) wired into CI. Golden preset hashes are pinned in `Settings/RegressionAnchorTests.cs`; a golden wire vector lives in the unit `Determinism/DeterminismTests.cs`.
- Packaging complete: icon/global.json/nuget.config, CHANGELOG `[2.0.0] - Unreleased`, CONTRIBUTING release flow, `dotnet pack` verified.

What remains before v2 ships is one significant scope expansion — **extraction and hashing of views, functions, DML triggers, sequences, and synonyms** — plus a drafted FAQ page and small doc carry-overs. Extraction currently covers only tables, stored procedures, and user-defined table types; for a library named *SqlSchemaHasher*, the missing object kinds are the biggest credibility gap, and v2 (unreleased) is the last free moment to widen the hash's coverage without a hash-format version bump.

## Decisions already made (do not relitigate)

1. **Views, T-SQL functions (scalar, inline TVF, multi-statement TVF), DML triggers, sequences, and synonyms go into v2.** CLR modules (`FS`/`FT`/`TA`/aggregates) and DDL/database/server triggers stay out of scope (documented).
2. **No options fingerprint in the hash envelope — ever, by design.** A deterministic fingerprint of `SchemaHashOptions` would have to stay stable across library evolution (every new enum bit or option property would otherwise invalidate all stored hashes), which effectively freezes the options design; and the audience is engineers who are expected to compare with matching options, the same discipline as matching library versions. Instead, this decision gets **documented in a FAQ** (Phase 5) so it reads as deliberate, not missing.
3. **ScriptDom-based definition-text normalization**: maintainer-led exploration in a later phase before v2 ships. Not for the implementing agent. Definition-text rendering drift remains a documented limitation.
4. **No dotnet-tool CLI, no diff API, no presets round** in this release.
5. The **hash format version stays `2`**: v2 is unreleased, so the expanded coverage ships inside hash version 2 with no envelope change.

## Working rules for the implementer

- **Never run git write operations.** No commits, branches, tags, stashes, or pushes. Leave all changes in the working tree.
- **Run integration tests via the `test-runner` subagent** (see `CLAUDE.md`); the unit project (`dotnet test tests/SqlSchemaHash.UnitTests`) can be run directly — it needs no Docker.
- `TreatWarningsAsErrors` is on and XML docs are generated: every new public record and member needs a `<summary>`; nullable warnings fail the build.
- Code style per `CLAUDE.md`: parameters/records on a single line; the output is a **hash**, never a "digest".
- **Golden-hash discipline for THIS plan**: the top-level hash sections carry no count prefix and each element opens with a marker string, so appending new sections changes the hash **only for databases that contain views/functions/triggers**. Consequences you must verify, not assume:
  - The pinned preset hashes in `Settings/RegressionAnchorTests.cs` move only because Phase 4 deliberately extends the golden reference schema with the new object kinds. Re-pin **once**, in that phase, with a `CHANGELOG.md` note.
  - The golden wire vector in the unit `Determinism/DeterminismTests.cs` is built from hand-constructed metadata that will carry empty new collections — it must **not** move. If it does, your calculator change is wrong (you added bytes for empty sections).
  - All other existing tests must stay green without edits beyond `SchemaMetadata` construction-site updates.

---

## Phase 1 — Metadata records and extraction

Files: `src/SchemaMetadata.cs`, `src/SchemaExtractor.cs`, `src/SqlSchemaHash.cs` (XML docs only).

### 1.1 New records in `SchemaMetadata.cs`

Follow the existing style exactly (sealed records, single line, `FullName` helper on top-level objects, catalog-named fields, XML doc summaries):

- `public sealed record ViewSchema(string SchemaName, string Name, List<IndexSchema> Indexes, string DefinitionHash, bool UsesAnsiNulls = true, bool UsesQuotedIdentifier = true)` — `Indexes` carries indexed-view indexes (reuses `IndexSchema`); empty for ordinary views. View columns are deliberately **not** extracted: the definition hash is the view's identity, and `SELECT *` views have stale column metadata until `sp_refreshview`, which would inject spurious diffs. Say so in the record's `<summary>`.
- `public sealed record FunctionSchema(string SchemaName, string Name, string TypeDesc, List<ParameterSchema> Parameters, string DefinitionHash, bool UsesAnsiNulls = true, bool UsesQuotedIdentifier = true)` — `TypeDesc` is the raw `sys.objects.type_desc` (`SQL_SCALAR_FUNCTION`, `SQL_INLINE_TABLE_VALUED_FUNCTION`, `SQL_TABLE_VALUED_FUNCTION`) so scalar-vs-TVF stays distinct even under `ModuleNormalization.IgnoreBodyText`. A scalar function's return type arrives as the `parameter_id = 0` row in `Parameters` (empty name, `IsOutput` true). A TVF's return-table shape is covered by the definition hash only — document that in the `<summary>`.
- `public sealed record TriggerEventSchema(string Type, bool IsFirst, bool IsLast)` — one per `sys.trigger_events` row (`type_desc`, `is_first`, `is_last`). Events also appear in the body text, but FIRST/LAST ordering is set out-of-band via `sp_settriggerorder` and exists **only** in the catalog, which is why this record exists.
- `public sealed record TriggerSchema(string SchemaName, string Name, string ParentSchemaName, string ParentName, bool IsDisabled, bool IsInsteadOfTrigger, bool IsNotForReplication, List<TriggerEventSchema> Events, string DefinitionHash, bool UsesAnsiNulls = true, bool UsesQuotedIdentifier = true)` — parent may be a table or a view.
- `public sealed record SequenceSchema(string SchemaName, string Name, string DataType, byte Precision, string StartValue, string Increment, string MinimumValue, string MaximumValue, bool IsCycling, bool IsCached, int? CacheSize)` — the numeric bounds are decimal strings (`CONVERT(NVARCHAR(64), …)`, same technique as identity seed/increment, because the catalog exposes them as `sql_variant`). `CacheSize` is null both for `NO CACHE` and for the default cache — `IsCached` disambiguates. The record's `<summary>` must state that **`current_value` is deliberately not captured**: it is runtime state, not schema, and would change the hash on every `NEXT VALUE FOR`.
- `public sealed record SynonymSchema(string SchemaName, string Name, string BaseObjectName)` — `BaseObjectName` is `sys.synonyms.base_object_name`, the quoted possibly-multi-part target exactly as the catalog stores it (synonym targets are not validated or resolved at CREATE time, and the stored text is deterministic across databases, so it hashes verbatim).
- `SchemaMetadata` gains five required positional collections: `List<ViewSchema> Views, List<FunctionSchema> Functions, List<TriggerSchema> Triggers, List<SequenceSchema> Sequences, List<SynonymSchema> Synonyms` (after `UserDefinedTableTypes`). This breaks every construction site — the extractor, plus the hand-built metadata in the unit tests (`Determinism/DeterminismTests.cs`, `Options/SchemaHashOptionsTests.cs`) and any integration helpers. Update them all with empty lists where the new kinds aren't exercised.

### 1.2 Extraction in `SchemaExtractor.cs`

**Hoist the server probes first (small refactor, no hash impact):** `ExtractTablesAsync` and `ExtractStoredProceduresAsync` each independently read `connection.ServerVersion` and run the `SERVERPROPERTY('EngineEdition')` scalar query. Hoist both into `ExtractSchemaAsync` (once, right after `OpenAsync`), pass `(majorVersion, engineEdition)` down, and build the `definitionHashExpr` (the `HASHBYTES`/`CHECKSUM` routing currently inside `ExtractStoredProceduresAsync`, ~line 388) in one shared private helper used by procs and the three new module extractors (views/functions/triggers — sequences and synonyms are pure catalog reads and need neither the probe nor the expression). Without this, extraction would run the probe five times.

**Views — `ExtractViewsAsync`:** mirror the proc query shape:

```sql
SELECT v.object_id AS ObjectId, SCHEMA_NAME(v.schema_id) AS SchemaName, v.name AS ViewName,
    {definitionHashExpr} AS DefinitionHash,
    CAST(CASE WHEN m.object_id IS NOT NULL AND m.definition IS NULL THEN 1 ELSE 0 END AS bit) AS IsEncrypted,
    CAST(ISNULL(m.uses_ansi_nulls, 1) AS bit) AS UsesAnsiNulls,
    CAST(ISNULL(m.uses_quoted_identifier, 1) AS bit) AS UsesQuotedIdentifier
FROM sys.views v
LEFT JOIN sys.sql_modules m ON v.object_id = m.object_id
```

(`definitionHashExpr` references alias `m` — keep that alias.) `WITH ENCRYPTION` views get the existing `EncryptedDefinitionSentinel`; a null hash falls back to `ComputeEmptyDefinitionHash()` — identical handling to procs. Keep `ObjectId` on the row: it keys the view's indexes.

**Indexed views:** widen `ExtractIndexesAsync` (~line 149) — the query is currently driven by `sys.tables t … WHERE t.type = 'U'`. Drive it from `sys.objects o` with `o.type IN ('U', 'V')` instead (keep `i.name IS NOT NULL AND i.is_hypothetical = 0`). The result dictionary is already keyed by `object_id`, so view indexes distribute to views exactly the way table-type columns distribute to table types today; tables are unaffected. `ExtractViewsAsync` attaches `indexesByObjectId.GetValueOrDefault(ObjectId, …)` to each `ViewSchema`.

**Functions — `ExtractFunctionsAsync`:**

```sql
FROM sys.objects o
LEFT JOIN sys.sql_modules m ON o.object_id = m.object_id
WHERE o.type IN ('FN', 'IF', 'TF')
```

selecting `SCHEMA_NAME(o.schema_id)`, `o.name`, `o.type_desc AS TypeDesc`, plus the same DefinitionHash/IsEncrypted/SET-option columns as views. **Do not add an `is_ms_shipped` filter** — the existing proc extraction has none (SSMS diagram procs are handled by the ignore list instead), and `fn_diagramobjects` must flow through the same `IgnoreSysDiagramObjects` mechanism (see Phase 3).

**Function parameters:** widen the existing `paramsQuery` (~line 419) from `sys.procedures p … WHERE p.type = 'P'` to `sys.objects p … WHERE p.type IN ('P', 'FN', 'IF', 'TF')`. Procs and functions share the schema-scoped object namespace, so the existing `(SchemaName, Name)` dictionary key cannot collide. The alias-type enrichment joins ride along for free. The `ORDER BY … pa.parameter_id` puts a scalar function's return row (`parameter_id = 0`, empty name) first — `ParamName.TrimStart('@')` on an empty string is fine, but confirm Dapper maps the empty (not null) `pa.name`; adjust the tuple type to `string?` + coalesce if it doesn't.

**Triggers — `ExtractTriggersAsync`:**

```sql
SELECT SCHEMA_NAME(o.schema_id) AS SchemaName, tr.name AS TriggerName, tr.object_id AS ObjectId,
    SCHEMA_NAME(po.schema_id) AS ParentSchemaName, po.name AS ParentName,
    tr.is_disabled AS IsDisabled, tr.is_instead_of_trigger AS IsInsteadOfTrigger,
    tr.is_not_for_replication AS IsNotForReplication,
    {definitionHashExpr} AS DefinitionHash, <IsEncrypted/SET columns as above>
FROM sys.triggers tr
INNER JOIN sys.objects o ON tr.object_id = o.object_id
INNER JOIN sys.objects po ON tr.parent_id = po.object_id
LEFT JOIN sys.sql_modules m ON tr.object_id = m.object_id
WHERE tr.parent_class = 1 AND tr.type = 'TR'
```

`parent_class = 1` restricts to object (DML) triggers — database-scoped DDL triggers are out of scope; `type = 'TR'` excludes CLR triggers (`TA`). Catalog subtlety: `sys.triggers` itself has no `schema_id`; take it from the trigger's own `sys.objects` row (a DML trigger always lives in its parent's schema, so `SchemaName == ParentSchemaName` — capture both anyway, the record is honest about the catalog). Events come from one extra batched query over `sys.trigger_events` (select `object_id`, `type_desc`, `is_first`, `is_last`), grouped by trigger `object_id`, **sorted by `Type` with `StringComparer.Ordinal`** before storing.

**Sequences — `ExtractSequencesAsync`:** pure catalog read, no modules involvement:

```sql
SELECT SCHEMA_NAME(s.schema_id) AS SchemaName, s.name AS SequenceName,
    CASE WHEN ty.is_user_defined = 1 THEN SCHEMA_NAME(ty.schema_id) + '.' + ty.name ELSE ty.name END AS DataType,
    bt.name AS BaseTypeName, ty.max_length AS AliasMaxLength, ty.precision AS AliasPrecision, ty.scale AS AliasScale, ty.is_nullable AS AliasIsNullable,
    s.precision AS Precision,
    CONVERT(NVARCHAR(64), s.start_value) AS StartValue,
    CONVERT(NVARCHAR(64), s.increment) AS Increment,
    CONVERT(NVARCHAR(64), s.minimum_value) AS MinimumValue,
    CONVERT(NVARCHAR(64), s.maximum_value) AS MaximumValue,
    s.is_cycling AS IsCycling, s.is_cached AS IsCached, s.cache_size AS CacheSize
FROM sys.sequences s
INNER JOIN sys.types ty ON s.user_type_id = ty.user_type_id
LEFT JOIN sys.types bt ON ty.is_user_defined = 1 AND ty.is_table_type = 0 AND ty.system_type_id = bt.user_type_id
```

Do **not** select `current_value` (runtime state — see the record spec). A sequence may be declared over an alias scalar type (`CREATE SEQUENCE … AS dbo.MyIntType`), so run the type name through the existing `EffectiveDataType` alias enrichment, same as columns and parameters.

**Synonyms — `ExtractSynonymsAsync`:** the simplest of all:

```sql
SELECT SCHEMA_NAME(sn.schema_id) AS SchemaName, sn.name AS SynonymName, sn.base_object_name AS BaseObjectName
FROM sys.synonyms sn
```

**Wire-up in `ExtractSchemaAsync`:** call the five new extractors after the existing ones; add the five collections to the `SchemaMetadata` constructor call; extend the `SchemaFilter` block (~line 68) with `Where` clauses for views, functions, triggers, sequences, and synonyms (triggers filter on their own `SchemaName`).

All new Dapper calls use `CommandDefinition(..., cancellationToken: cancellationToken)` like the existing ones. Update the `ExtractSchemaAsync` XML doc summaries (extractor and facade) that currently say "tables, stored procedures, and UDTs".

---

## Phase 2 — Hashing in `SchemaHashCalculator.cs`

Append five sections to `ComputeHash` after the table-types loop, each sorted by `(SchemaName, Name)` with `StringComparer.Ordinal`, in this fixed order: **views, then functions, then triggers, then sequences, then synonyms.** Follow `HashStoredProcedure` as the template — every field length-prefixed/marker-separated the same way:

- `HashView`: marker `"VIEW:"`, then SchemaName, Name; `"DEFHASH:"` + DefinitionHash gated by `ModuleNormalization.IgnoreBodyText`; `"SET:"` + the two SET-option bools gated by `ModuleNormalization.IgnoreSetOptions` (neutralize to `true`, exactly as procs do); then `HashIndexes(hasher, view.Indexes)` — the existing method, so all `IndexNormalization` bits (names, clustering, sort order, fill factor, …) apply to indexed views automatically.
- `HashFunction`: marker `"FUNC:"`, then SchemaName, Name, **TypeDesc (unconditional — it survives `IgnoreBodyText`)**, then the gated DEFHASH and SET blocks, then the parameter loop via the existing `HashParameter` (parameters are hashed in list order, which is `parameter_id` order from extraction — the return row hashes first).
- `HashTrigger`: marker `"TRIGGER:"`, then SchemaName, Name, ParentSchemaName, ParentName; `AppendBool` for IsDisabled, IsInsteadOfTrigger, IsNotForReplication (all unconditional — no normalization bits for triggers in this round; note the deliberate choice in a comment: `ConstraintNormalization.IgnoreDisabled` is a *constraints*-domain bit and does not reach triggers); then the gated DEFHASH and SET blocks; then the events **count-prefixed** (`AppendInt(events.Count)`, then per event Type/IsFirst/IsLast in stored order).
- `HashSequence`: marker `"SEQUENCE:"`, then SchemaName, Name, DataType, `AppendInt(Precision)`, then the four bound strings (StartValue, Increment, MinimumValue, MaximumValue), `AppendBool(IsCycling)`, `AppendBool(IsCached)`, `AppendInt(CacheSize ?? -1)` — the `-1` sentinel keeps `CACHE 0`-adjacent values distinct from "no explicit size"; `(IsCached, CacheSize)` together encode NO CACHE / default CACHE / `CACHE n` unambiguously. All unconditional (no normalization bits for sequences in this round).
- `HashSynonym`: marker `"SYNONYM:"`, then SchemaName, Name, BaseObjectName. Unconditional.

Do not touch the existing sections and do not add anything (not even a zero count) for databases whose new collections are empty — that is what keeps table-only hashes byte-identical (see working rules).

**Acceptance for Phases 1–2**: solution builds warning-free; unit suite green with the wire-vector golden **unchanged**; full integration suite green with `RegressionAnchorTests` goldens **unchanged** (the reference schema doesn't gain new objects until Phase 4).

---

## Phase 3 — Scoping semantics

Files: `src/SchemaExtractor.cs`, tests in Phase 4.

1. **`ObjectNamesToIgnore`** applies to views, functions, triggers, sequences, and synonyms with the existing `IsIgnored(schema, name)` semantics (bare name any-schema, or `schema.name`). Apply it in each new extractor the same place the proc extractor does.
2. **Triggers of excluded parents are excluded too**: a trigger is dropped when `IsIgnored(triggerSchema, triggerName)` **or** `IsIgnored(parentSchema, parentName)`. Excluding a table must not leak its triggers back into the hash. Add a code comment stating this rule.
3. **`IgnoreSysDiagramObjects`** needs no list change — `SchemaHashOptions.SysDiagramObjectNames` already contains `fn_diagramobjects`, which until now was dead weight because functions weren't extracted. Verify it is now actually excluded (test in Phase 4).

---

## Phase 4 — Tests

New integration files under `tests/SqlSchemaHash.IntegrationTests/Fidelity/`: `ViewFidelityTests.cs`, `FunctionFidelityTests.cs`, `TriggerFidelityTests.cs`, `SequenceFidelityTests.cs`, `SynonymFidelityTests.cs`, mirroring the structure/helpers of the existing fidelity tests.

**Views:**
1. Adding a view changes the hash; two DBs with the identical view hash equal.
2. Body change → different under Strict; equal under `Modules = ModuleNormalization.IgnoreBodyText`.
3. `WITH ENCRYPTION` view vs the same view unencrypted → different (sentinel path); two DBs with the identical encrypted view → equal.
4. Indexed view: `CREATE VIEW … WITH SCHEMABINDING` + unique clustered index; adding/dropping the index changes the hash. (Landmine: indexed views need the strict session SET options — SqlClient's defaults suffice on modern servers, but if `CREATE INDEX` fails in the container, prepend `SET ARITHABORT ON;` to the batch.)
5. SET options: a view created under `SET ANSI_NULLS OFF` differs under Strict, equal under `IgnoreSetOptions` (mirror the existing proc SET-option tests).

**Functions:**
1. Scalar function: body change → different; identical across DBs → equal.
2. Return type change (`RETURNS INT` vs `RETURNS BIGINT`, same body types otherwise) → different **even under `IgnoreBodyText`** (the `parameter_id = 0` row carries it).
3. Parameter add/type change → different.
4. `TypeDesc` fidelity: an inline TVF vs a multi-statement TVF, compared under `IgnoreBodyText`, still differ.
5. Alias-typed function parameter gets the `{base type}` enrichment (mirror the existing proc alias-param test).

**Triggers:**
1. Adding a trigger changes the hash; identical across DBs → equal.
2. `DISABLE TRIGGER` → different under Strict.
3. `INSTEAD OF` trigger on a view is captured (and `IsInsteadOfTrigger` asserted on the extracted metadata).
4. Body change → different; under `IgnoreBodyText` two triggers differing only in body → equal (flags/events still hashed).
5. `sp_settriggerorder … @order = 'First'` → different (IsFirst captured).
6. A table listed in `ObjectNamesToIgnore` takes its triggers with it.

**Sequences:**
1. Adding a sequence changes the hash; identical across DBs → equal.
2. Start value or increment change → different; MIN/MAX/CYCLE change → different; `CACHE 50` vs `NO CACHE` vs default cache → all three pairwise different.
3. **State independence (the key test): run `SELECT NEXT VALUE FOR dbo.TheSequence` a few times, re-hash → identical.** This pins the deliberate exclusion of `current_value`.
4. Data type change (`AS INT` vs `AS BIGINT`) → different; a sequence over an alias type gets the `{base type}` enrichment.

**Synonyms:**
1. Adding a synonym changes the hash; identical across DBs → equal.
2. Drop/recreate pointing at a different target (`base_object_name` change) → different.
3. A synonym whose target object doesn't exist still extracts and hashes deterministically (targets are unvalidated by design).

**Scoping** (put where the existing `ObjectNamesToIgnore`/`SchemaFilter` tests live — locate and extend them): each new object kind excluded by bare and schema-qualified ignore entries; `SchemaFilter` drops other-schema instances of each; `IgnoreSysDiagramObjects` excludes an `fn_diagramobjects`-named function.

**Golden re-pin (the only one, do it last):** extend the `RegressionAnchorTests` reference schema with representative new objects — an ordinary view, a schemabound indexed view, a scalar function (ideally with an alias-typed parameter), an inline TVF, a multi-statement TVF, a DML trigger (give it a non-default flag, e.g. disabled), a sequence (non-default start/increment), and a synonym. Re-pin the Strict/V1/V2/Structural hashes once and record the re-pin in `CHANGELOG.md`.

**Unit tests:** update hand-built `SchemaMetadata` construction sites (empty new collections); assert the wire-vector golden did **not** move; add a couple of DB-free sanity tests (e.g. two in-memory schemas differing only in `TriggerSchema.IsDisabled` or `FunctionSchema.TypeDesc` hash differently).

---

## Phase 5 — Documentation and FAQ

1. **`docs/wiki/FAQ.md`** (new): draft content for a GitHub Wiki "FAQ" page. The implementer cannot publish a wiki (it's a separate git repo — maintainer action); write the draft and flag it in the handoff. Questions to cover, in an honest engineering register:
   - **"Why doesn't the hash envelope encode the comparison options?"** — the anchor entry. Three reasons: (a) a deterministic fingerprint of an options object must remain stable across library evolution — any new option, enum bit, or rename would change the fingerprint and turn every stored hash `Incomparable` even when the semantics didn't move, effectively freezing the options design forever; (b) the tool targets engineers comparing environments they control — using matching options is the same published discipline as using a matching library version; (c) deliberate scope: a wrong `Incomparable` is worse than the documented caller contract, where a mismatch surfaces as `Different`.
   - "Why does comparing hashes computed with different options return `Different` rather than `Incomparable`?" — short, cross-references the answer above.
   - "Why is definition text hashed exactly as SQL Server renders it?" — cross-version rendering drift, link BUGS.md, note that parser-based (ScriptDom) normalization is under evaluation.
   - "Why is the hash an envelope (`2:<base64>`) instead of a bare hash?" — hash-format versioning; legacy bare-base64 values read as `Incomparable`.
   - "Why does a sequence's current value not affect the hash?" — schema vs runtime state; the hash is stable across `NEXT VALUE FOR`.
   - "Why aren't CLR objects or DDL/server triggers captured?" — scope/roadmap, link the README scope section.
2. **README.md**: move views/functions/triggers/sequences/synonyms from "Not Yet Captured" into "What Gets Hashed" (state what each contributes: definition hash, SET options, function TypeDesc/parameters/return type, trigger flags/events/parent, indexed-view indexes, sequence type/bounds/cycle/cache — explicitly *not* current value — and synonym targets); shrink "Not Yet Captured" to what's true now (CLR modules/UDTs, DDL & server triggers, partitioning/filegroups, encrypted-module sentinel, definition-text drift); add a link to the wiki FAQ (flag for the maintainer: publish the wiki page at merge time or the link 404s); add the **unreleased-v2 caveat** to Project Status — one sentence noting 2.0.0 is in preparation and badges reflect the latest published release (carry-over from the audit).
3. **CLAUDE.md**: update the scope paragraph (views/T-SQL functions/DML triggers/sequences/synonyms now in scope; out of scope now: scalar/CLR UDTs, CLR modules, DDL/server triggers, plus the existing ledger/graph/vector line), the Core Flow/Key Classes descriptions (`SchemaExtractor` extracts the eight object kinds; `SchemaMetadata` gains the new records), and the Important Details section if any statement is now stale.
4. **BUGS.md**: shrink the out-of-scope open item to the post-v2 truth; add one "Resolved in v2" line for the object-coverage expansion.
5. **CHANGELOG.md** under `[2.0.0]`: Added — views/functions/DML-triggers/sequences/synonyms extraction and hashing (spell out the sub-features: indexed views, function return types, trigger events/ordering, sequence state independence, encrypted-module sentinel reuse); note that hashes change only for databases containing these objects; note the golden re-pin.

---

## Phase 6 — Verification and handoff

1. `dotnet build SqlSchemaHasher.sln` clean (warnings-as-errors).
2. Unit project green without Docker, wire-vector golden byte-identical; full integration suite green via the `test-runner` subagent, with exactly one golden re-pin (Phase 4) called out in the CHANGELOG.
3. Quick `dotnet pack -c Release` sanity check (should be unaffected; the .nupkg was verified in the previous round).
4. Leave the working tree uncommitted. Final summary for the maintainer: files changed and why, the golden re-pin values, and the maintainer-action list — publish `docs/wiki/FAQ.md` to the GitHub Wiki, replace the placeholder `icon.png` (current file is a 311-byte stub) if a real asset is wanted, then the release steps below.

### Maintainer-only release steps (not for the implementing agent)

ScriptDom exploration (decision: adopt/defer) → commit/PR/merge `zb/v2-prep` → publish the wiki FAQ → set the CHANGELOG `[2.0.0]` date → tag `v2.0.0` → run `release.yml` with version `2.0.0` → verify NuGet listing, provenance attestation, and the GitHub release.

## Explicitly out of scope for v2 (do not implement)

- Options fingerprint in the envelope (permanent design decision — FAQ documents why).
- ScriptDom / definition-text normalization (maintainer-led exploration later).
- A dotnet-tool CLI; a structural diff API.
- CLR modules (functions/triggers/aggregates), scalar CLR UDTs, DDL/database/server triggers.
- The presets round (folding newer bits into `V1`/`V2`/`Structural`); Always Encrypted test coverage.
