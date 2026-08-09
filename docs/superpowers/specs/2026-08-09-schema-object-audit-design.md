# SQL Server Object-Type Coverage Audit — Design

**Date:** 2026-08-09
**Branch:** `zb/v2-prep`
**Goal:** independently document what the SQL Server catalog exposes per major schema object type, verified against a live server, then audit that documentation against `SchemaExtractor.cs`/`SchemaMetadata.cs`/`SchemaHashCalculator.cs` to surface extraction gaps ahead of the v2 release.

## Why

`website/docs/what-gets-hashed.md` already asserts what the library captures per object type, but that list was written *from* the code — it can't catch a gap the author didn't think to look for. This audit deliberately builds the reference the other way: document each object type from the live catalog and Microsoft's own docs first, with no reference to this repo's source, then compare the two. A mismatch found this way is evidence, not a hunch.

This complements rather than replaces the `hash-bug-hunter` agent (which audits fidelity/determinism on demand against the existing code) — this audit's phase 1 is deliberately blind to the code, which `hash-bug-hunter` is not designed to be.

## Scope: 16 object types, batched into 4 workflow runs

Grouped so each batch's topics share enough surface area to cross-reference each other, and so each batch stays near the session's default workflow size guideline (medium, ~15 agents):

| Batch | Topics |
|---|---|
| **A — table anatomy** | Tables, Indexes, Constraints, Extended Properties |
| **B — programmability** | Stored Procedures, Functions, Triggers, Views |
| **C — type system & small objects** | User-Defined Table Types, Sequences, Synonyms, Alias/CLR UDTs |
| **D — currently out-of-scope objects** | Schemas, Full-Text Indexes/Catalogs, XML Schema Collections, Partition Functions/Schemes & Filegroups |

Batches run sequentially, one `Workflow` invocation each, with a review checkpoint between batches. Batch D covers object types the library's own docs already list as **not** captured (`what-gets-hashed.md` → "Not yet captured" / never-discussed categories) — the audit there either confirms the exclusion is deliberate and documented, or flags it as an undocumented gap.

## Shared SQL Server container

One `mcr.microsoft.com/mssql/server:2025-latest` container (the same image `SqlServerFixture` uses for integration tests, password matching that fixture's convention), started via `docker run` by the orchestrating session — **not** inside the workflow script, since Workflow scripts have no shell access and the container must outlive a single `agent()` call. Started once before Batch A, kept running across all 4 batches to amortize SQL Server's startup cost, torn down after Batch D.

Each topic's agents work inside a uniquely-named schema (e.g. `CREATE SCHEMA audit_temporal_tables`) so that the 4 topics running concurrently within a batch don't collide on the shared instance. Agents reach the container via `docker exec <container> /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P <password> -C -Q "..."` — no client tooling install required on the host.

## Per-batch pipeline

Each batch is a `pipeline` over its 4 topics (not a barrier — a topic's audit stage starts as soon as its own document stage finishes, independent of the other 3 topics):

1. **Document (blind).** One agent per topic. Explicitly instructed **not** to read this repository's source. It creates a couple of representative example objects for its topic in its own schema in the shared container, queries the relevant `sys.*` catalog views against them, cross-checks against Microsoft Learn documentation, and writes `docs/schema-object-audit/<topic-slug>.md`: what's catalogued, what's queryable, notable edge cases (anonymous vs. named objects, system-named vs. user-named, version-gated columns).
2. **Audit.** Same topic, next pipeline stage, receives the blind doc. This agent now reads `SchemaExtractor.cs`, `SchemaMetadata.cs`, and `SchemaHashCalculator.cs`, and compares the blind doc against what's actually extracted and hashed. It lists concrete gaps — catalog fields that exist, are semantically relevant to schema comparison, and are not extracted; or extraction that contradicts documented catalog behavior. It appends each confirmed gap to `BUGS.md` as a new dated entry, following that file's existing entry format, and records its findings inline in the topic's `docs/schema-object-audit/<topic-slug>.md` under a "Coverage audit" heading.

A finding that turns out to already be covered (just under a different field name, or intentionally excluded per `what-gets-hashed.md`'s "Not yet captured" section) is not a gap — the audit stage checks this before writing to `BUGS.md`, to avoid re-filing known, deliberate exclusions.

## Output

- `docs/schema-object-audit/<topic-slug>.md` — 16 files, one per topic, each with the blind catalog documentation plus the audit's "Coverage audit" section.
- `BUGS.md` — new dated entries for confirmed gaps only.
- `website/docs/` is untouched — that's public-facing library documentation, out of scope for this internal audit.

## Error handling

If the shared container fails to start, or a topic's agent can't reach it mid-batch, that topic is skipped and reported rather than falling back to producing a doc from model knowledge alone — an unverified doc defeats the point of the exercise. A skipped topic is retried in a later batch run rather than silently dropped.

## Out of scope

- Public-facing docs (`website/docs/`) — this audit's output is internal.
- Fixing any gap found — this audit documents and files (`BUGS.md`); remediation is separate follow-up work, scoped and planned individually once the gap list exists.
- Object types outside the 16 listed (security principals, Service Broker, CLR assemblies as modules, statistics objects, linked servers) — noted as a follow-up candidate list, not run in this pass.
