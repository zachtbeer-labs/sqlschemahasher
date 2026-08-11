# v2.0.0 Release Readiness — Design

**Date:** 2026-08-09
**Branch:** `zb/v2-prep` (13 commits ahead of `main`, in sync with `origin`)
**Goal:** ship `zachtbeer.SqlSchemaHasher` 2.0.0 to NuGet.org within an 8-hour working budget.

## Starting position

The feature work described in `v2_plan.md` is **complete**. All six phases landed: views, T-SQL functions, DML triggers, sequences, synonyms, extended properties, and the anonymous temporal history-table name normalization. Verified at the start of this session:

- `dotnet build SqlSchemaHasher.sln` — succeeds, 0 warnings (`TreatWarningsAsErrors` is on).
- `dotnet test tests/SqlSchemaHash.UnitTests` — 40 passed, 0 failed.
- 210 `[TestMethod]` integration tests exist; green status unconfirmed since the most recent commit (`wip`, which modified `SchemaHashCalculator.cs`).
- Docs site live on GitHub Pages; `release.yml` performs OIDC trusted publishing with SLSA provenance and a CycloneDX SBOM.

This is not a feature gap. It is an unshipped release with a handful of loose ends.

## Decisions taken

Three decisions were settled before planning, and are not reopened here.

1. **Presets — fold physical bits into `Structural` only.** The per-domain `Structural` combos were originally frozen to reproduce pre-enum preset hashes, leaving the newer normalization bits in no preset at all. Because changing a preset's composition changes the hash for every consumer, this is effectively frozen once 2.0.0 ships. We widen `Structural` now, and only with bits that represent physical layout or enforcement state.
2. **ScriptDom definition-text normalization — deferred to v3.** The decision is closed and recorded, rather than left as an open pre-ship item. A parser-based canonicalization pass is a multi-day project carrying its own determinism risk; the existing rendering-drift limitation stays documented.
3. **Ship all the way to NuGet** in this session, not merely to a merge-ready branch.

## Scope of change

### 1. Preset widening

In `src/SchemaNormalization.cs`:

- `IndexNormalization.Structural` gains `IgnoreFillFactor | IgnorePadIndex | IgnoreLockOptions | IgnoreDisabled`.
- `ConstraintNormalization.Structural` gains `IgnoreDisabled | IgnoreTrust | IgnoreNotForReplication`.
- `TableNormalization.Structural` and `ColumnNormalization.Structural` are deliberately unchanged.

The exclusion of `Columns` is a judgment call worth recording: collation is *semantic*, not cosmetic — a case-insensitive to case-sensitive change alters query results. Identity seed is likewise real schema. Neither belongs in a combo whose question is "is the schema logically the same?" The bits we are folding in — fill factor, pad index, lock options, disabled state, constraint trust and NOT FOR REPLICATION — are storage tuning and enforcement state, which is exactly the noise `Structural` exists to suppress.

This moves the `Structural` golden hash. Exactly one re-pin, recorded in `CHANGELOG.md`.

### 2. Documentation truth-up

Three genuine defects, not polish:

- **`CHANGELOG.md` does not mention the temporal history-table fix at all.** It is the most recent substantive change in the release and it is absent from the release notes. Needs a `Fixed` entry.
- **"What Gets Hashed" is incomplete** in `README.md` and `website/docs/what-gets-hashed.md`. The Tables entry omits system-versioning (`TemporalType`, history linkage, retention, `GENERATED ALWAYS` / hidden columns), memory-optimized tables and durability, sparse columns, rowguidcol, dynamic data masking, XML schema collection binding, and column encryption — all of which are hashed. A reader cannot currently determine what they are getting.
- **`BUGS.md`** should state the ScriptDom deferral as a closed decision rather than an open evaluation.

Plus the mechanical items: date the `[2.0.0]` heading, and update the preset tables in `README.md` and `website/docs/options-and-presets.md` to match the widened combos.

### 3. Release hygiene

- `release.yml` runs the full solution test suite — roughly 210 Docker/Testcontainers tests — inside the release job, gating the publish on a long and flake-prone step that the PR's CI matrix has already covered. Scope it to the unit project.
- CI's integration matrix covers SQL Server 2019 and 2022; local development and the pinned goldens target 2025. Add a 2025 leg.
- `icon.png` is a 311-byte stub and ships as the package icon. Maintainer go/no-go.
- Four open dependabot branches. Merge the two GitHub Actions bumps before the release PR; hold the NuGet dependency bumps until after the tag so dependency churn is not in the release build.
- `README.md` carries a "v2.0.0 is currently in preparation and unreleased" caveat that becomes false on publish. It must be removed in the release commit or immediately after.

## Execution: agent orchestration

Every agent shares one working tree, so the split is driven by write conflicts. `README.md`, `CHANGELOG.md`, and the preset tables are each touched by more than one workstream, which makes parallel writers the obvious failure mode.

- **Fan out:** anything that reads a lot and returns a little.
- **Single writer:** all edits go through the main thread.
- **Human:** all git write operations, and the release trigger.

### Wave 0 — parallel recon (6 agents, read-only)

| Agent | Job |
|---|---|
| `test-runner` | Full 210-test integration baseline. The long pole. |
| `hash-bug-hunter` | Final fidelity audit before hash format 2 freezes. |
| `feature-dev:code-reviewer` | Review the `main...zb/v2-prep` diff, especially the unreviewed `wip` commit. Diff pre-generated to scratchpad, since this agent has no shell. |
| `Explore` | Set difference between fields the calculator hashes and fields the docs claim, in both directions. |
| `general-purpose` | Blast radius of the `Structural` widening: failing tests split into "needs re-pin" vs "needs thought", plus the complete golden-constant list. |
| `general-purpose` | Release and packaging audit: nupkg payload, stale "unreleased" claims, link integrity, dependabot branches, vulnerability scan, workflow review, version consistency. |

All six are instructed to make no edits and run no git write operations.

### Gate 1 — triage

Two results can reshape the session:

- **Integration suite red** → that becomes the work; the release slips.
- **A real determinism defect** → fix it *before* the golden re-pin, or the re-pin happens twice.

### Wave 1 — edits (single writer)

Preset widening and every documentation change, driven by the Wave 0 reports. Delegated only if the docs-gap report is large enough to justify it, and then strictly partitioned by sole file ownership — one agent per file, never two agents in one file.

### Wave 2 — verify and re-pin

`test-runner` → capture the new `Structural` hash → re-pin `RegressionAnchorTests` → `test-runner` again to confirm green. Serial, because Docker, and because re-pinning against an unverified run enshrines whatever bug was present.

### Wave 3 — ship

Dependabot Actions merges, `dotnet pack` sanity check, `release.yml` test-step scoping, SQL 2025 CI leg, PR to `main`, matrix run, merge, date the changelog, tag `v2.0.0`, run `release.yml`, then verify the NuGet listing, `gh attestation verify`, the GitHub release assets, and the docs rebuild.

## Budget

| Phase | Estimate |
|---|---|
| Wave 0 (wall clock, 6 concurrent) | 0.5h |
| Gate 1 triage | 0.3h |
| Wave 1 edits | 1.5h |
| Wave 2 verify + re-pin + re-verify | 1.0h |
| Wave 3 package, PR, matrix, merge | 1.5h |
| Wave 3 tag, release, verify | 1.0h |
| Buffer | 1.0h |
| **Total** | **~6.8h** |

## Out of scope

- ScriptDom / definition-text canonicalization (deferred to v3).
- Folding normalization bits into `V1` or `V2`, or into the `Tables` / `Columns` domains.
- CLR modules, scalar CLR UDTs, DDL and server-scoped triggers.
- A dotnet-tool CLI; a structural diff API.
- An options fingerprint in the hash envelope — a permanent design decision, documented in the FAQ.

## Open questions

- **SQL 2025 CI leg** — assumed in, at roughly 10 minutes of work, since the goldens were pinned against 2025. Reversible.
- **`icon.png`** — ship the 311-byte stub, or supply a real 128×128 asset. Needs a maintainer answer before the pack step in Wave 3.
