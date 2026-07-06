# SqlSchemaHasher v2 Wrap-Up — Implementation Plan

This document is a self-contained work plan for finishing the v2 release of `zachtbeer.SqlSchemaHasher`. It is written to be handed to an implementing agent with repo access but no prior conversation context. Read `CLAUDE.md` first — it describes the architecture (facade → extractor → metadata → calculator), the normalization enums, and the versioned hash envelope.

## Background and current state

The library computes deterministic SHA256 hashes of SQL Server database schemas for change detection. The `zb/v2-prep` branch already contains the substance of v2:

- Extraction-fidelity fixes from an earlier bug audit (column order, identity metadata, constraint disabled/trust state, constraint names + `IsSystemNamed`, encrypted-proc sentinel, sparse/rowguid/masking/XML capture, schema-qualified UDT type names).
- Five `[Flags]` normalization enums in `src/SchemaNormalization.cs` with per-domain `Strict`/`Structural` combos and presets `V1`/`V2`/`Structural` on `SchemaHashOptions`.
- A versioned hash envelope `<version>:<base64hash>` produced at the `SqlSchemaHash` facade, parsed/compared via `SchemaHashResult`.
- A rewritten test suite (~195 MSTest integration tests under `tests/SqlSchemaHash.IntegrationTests/`, Testcontainers SQL Server, one container per assembly, per-test databases). Includes pinned golden hashes in `Settings/RegressionAnchorTests.cs`.
- Strong CI/supply-chain posture in `.github/workflows/` (SHA-pinned actions, OIDC trusted publishing, SLSA provenance, SBOM, CodeQL, Scorecard, Dependabot, locked-mode restore).
- `Directory.Build.props`: `Nullable` enabled, `TreatWarningsAsErrors`, deterministic build, SourceLink, snupkg symbols. The csproj multi-targets `net6.0;net7.0;net8.0;net9.0;net10.0`, `<Version>2.0.0</Version>`.

What remains is API finalization (breaking changes are still free until release), one tractable fidelity fix, honest documentation of known limitations, two test-suite additions, and packaging/docs polish. That is this plan.

## Decisions already made (do not relitigate)

These were settled with the maintainer:

1. **API surface**: `SchemaExtractor` and `SchemaHashCalculator` become `internal`. The public API is the `SqlSchemaHash` facade plus `SchemaHashOptions`, `SchemaHashResult`/`SchemaHashComparison`, the five normalization enums, and the `SchemaMetadata` record family (returned by `ExtractSchemaAsync` and accepted by `ComputeHash`).
2. **Open fidelity bugs**: fix **#8** (alias scalar type's underlying base type not captured — see Phase 2); **document** #5 (CHECK/DEFAULT/filter definition text is hashed as SQL Server renders it, so cross-version rendering drift can cause spurious diffs) as a known limitation. Do not attempt definition-text normalization.
3. **Tests**: add memory-optimized table coverage and a new DB-free unit test project. Always Encrypted column coverage is explicitly out of scope for v2.
4. **Versioning/release**: keep the manual flow (csproj `<Version>` + `workflow_dispatch` release with a version input). Do not add MinVer or a version-consistency guard; just document the release steps in CONTRIBUTING.
5. Also explicitly deferred: the "presets round" (folding the newer physical/enforcement/collation bits into presets — already marked deferred in `src/SchemaNormalization.cs`), and extraction of functions/views/triggers/sequences (documented out of scope).

## Working rules for the implementer

- **Never run git write operations.** No commits, branches, tags, stashes, or pushes. Leave all changes in the working tree for the maintainer to review and commit.
- **Run tests via the `test-runner` subagent** (see `CLAUDE.md`), not by shelling out `dotnet test` — it handles the Docker/Testcontainers bootstrap and keeps output manageable. Docker must be running.
- `TreatWarningsAsErrors` is on and XML docs are generated: any new public member without a `<summary>` fails the build, as do nullable warnings.
- **Golden-hash discipline**: `Settings/RegressionAnchorTests.cs` pins byte-exact hashes for the Strict/V1/V2/Structural presets over a rich reference schema, and `Determinism/DeterminismTests.cs` has a wire-format golden vector. If a change alters any pinned hash, stop and confirm the change is intended and scoped as narrowly as possible before re-pinning; every re-pin must be called out in `CHANGELOG.md`. Phases 1 and 5 must not change any hash. Phase 2 may only change hashes for schemas that actually use alias scalar types.
- Code style per `CLAUDE.md`: parameters/records on a single line; the output is called a **hash**, never a "digest".

---

## Phase 1 — API finalization

Files: `src/SchemaExtractor.cs`, `src/SchemaHashCalculator.cs`, `src/SqlSchemaHash.cs`, `src/SchemaMetadata.cs`, `README.md`, `CLAUDE.md`.

### 1.1 Internalize the implementation classes

- `src/SchemaExtractor.cs` (~line 16): `public sealed class SchemaExtractor` → `internal sealed class SchemaExtractor`.
- `src/SchemaHashCalculator.cs` (~line 19): same change.
- The integration test project already has `InternalsVisibleTo` (declared in `src/zachtbeer.SqlSchemaHasher.csproj`); tests keep compiling. The new unit test project (Phase 4.2) needs its own `InternalsVisibleTo` entry.
- Sweep `README.md`'s API Reference section and `CLAUDE.md`'s Key Classes section: neither should present `SchemaExtractor`/`SchemaHashCalculator` as public entry points (describing them as internal architecture is fine).

### 1.2 CancellationToken support

Add an optional trailing `CancellationToken cancellationToken = default` to every public async method on `SqlSchemaHash`:

- `GetHashAsync(string connectionString, CancellationToken cancellationToken = default)`
- `GetHashAsync(string connectionString, SchemaHashOptions options, CancellationToken cancellationToken = default)` — keep `options` required so single-argument calls stay unambiguous.
- Both `ExtractSchemaAsync` overloads likewise.

Thread the token through `SchemaExtractor`: `connection.OpenAsync(cancellationToken)`, and wrap every Dapper call in a `CommandDefinition` (`new CommandDefinition(sql, parameters, cancellationToken: cancellationToken)`) so queries are cancellable, including the server-side `HASHBYTES` proc-definition query and the version/capability probes. `ComputeHash` stays synchronous (pure CPU) — no token.

### 1.3 Argument validation at the facade

In `SqlSchemaHash` only (internals stay lean):

- `connectionString` null/empty/whitespace → `ArgumentException` (null → `ArgumentNullException`) with the parameter name. Use plain guard clauses — the library targets net6.0, where `ArgumentException.ThrowIfNullOrEmpty` does not exist. A small private `static void ValidateConnectionString(string connectionString)` helper is fine.
- `ComputeHash(SchemaMetadata schema, ...)`: null `schema` → `ArgumentNullException` (currently a raw `NullReferenceException`).
- Reconcile `options` nullability: the internals coalesce `options ?? SchemaHashOptions.Default`, but the two-argument `GetHashAsync` declares `options` as non-nullable. Make the public annotations match actual behavior (recommended: keep the 2-arg overloads' `options` non-nullable and throw `ArgumentNullException` on null, since callers who don't care use the 1-arg overload — but pick one story and apply it consistently across `GetHashAsync`/`ExtractSchemaAsync`/`ComputeHash`, whose extract/compute variants currently take `SchemaHashOptions?`).

### 1.4 Fix malformed XML docs on ColumnSchema

In `src/SchemaMetadata.cs` around lines 54–57: the `</summary>` for `ColumnSchema` closes early and three doc lines (covering `GeneratedAlwaysType`, `IsHidden`, `IsAnsiPadded`) dangle outside the tag before the record declaration. Move them inside the summary element.

**Acceptance for Phase 1**: solution builds warning-free; full integration suite green; no golden hash changes; new unit tests (Phase 4) cover the validation paths; cancelling a token before calling `GetHashAsync` produces `OperationCanceledException` (one integration test suffices).

---

## Phase 2 — Fix bug #8: capture alias scalar type underlying definitions

**The bug**: a column or parameter typed with an alias scalar type (`CREATE TYPE dbo.OrderTotal FROM DECIMAL(9,2) NOT NULL`) hashes the schema-qualified alias *name* (`[dbo].[OrderTotal]`), but the alias's underlying definition is never captured. Dropping and recreating the alias with a different base type (a real migration pattern, since `ALTER TYPE` doesn't exist) can leave the hash unchanged.

**Investigate first** (this determines how small the fix is): check what `ColumnSchema`/`ParameterSchema` already capture. `sys.columns.max_length/precision/scale` for an alias-typed column reflect the *underlying* values, so if precision/scale/max_length are already extracted and hashed, the only missing signal is the underlying **system base type name** (e.g. `decimal` vs `bigint` at the same storage size) and the alias's own nullability default.

**Recommended shape** (keeps the "no new top-level object kinds" scope): enrich the rendered type identity for alias-typed columns and parameters rather than adding a new metadata collection. Where the extractor resolves the schema-qualified user type name, join `sys.types t` (the alias, `t.is_user_defined = 1 AND t.is_table_type = 0`) to `sys.types bt ON t.system_type_id = bt.user_type_id` and append the underlying definition to the stored type name, e.g. `[dbo].[OrderTotal]{decimal(9,2) NOT NULL}` — using the alias type's own `max_length`/`precision`/`scale`/`is_nullable` from `sys.types`. Deterministic rendering only (lowercase system type name, fixed format); no server-rendered text.

- This changes hashes **only** for schemas that use alias scalar types. The golden reference schema in `RegressionAnchorTests` must be checked: if it contains no alias scalar types, pinned hashes must not move (verify by running the suite).
- Table-type columns flow through the same column extraction — confirm alias-typed columns inside user-defined table types get the same treatment.
- Update `README.md` (remove/adjust this item under "Not Yet Captured", mention the capture under "What Gets Hashed") and `CHANGELOG.md`.

**Acceptance**: new fidelity test (Phase 4.3) — two databases identical except the alias base type (`DECIMAL(9,2)` vs `BIGINT`) hash differently under Strict; a third database with an identical alias hashes identically. Full suite green; goldens unmoved (or knowingly re-pinned with a CHANGELOG note if the reference schema turns out to use an alias type).

---

## Phase 3 — Truth-up BUGS.md and known-limitation docs

`BUGS.md` currently lists only two resolved `ObjectNamesToIgnore` items and reads as if nothing is open. The original 8-bug audit lives only in git history (`git show 96fb053:KNOWN_BUGS.md` — read it for source material).

Rewrite `BUGS.md` with two sections:

- **Open / known limitations**:
  - **Definition-text rendering drift** (audit #5): CHECK constraint, DEFAULT constraint, computed-column, and filtered-index definitions are hashed as SQL Server renders them (`sys.check_constraints.definition` etc.). Different SQL Server versions can render the same expression differently (spacing, parenthesization, casing of functions), which can produce a spurious `Different` when comparing hashes across server versions. Workaround: compare hashes taken from the same server major version. Intentionally not normalized in v2 (expression rewriting risks new determinism bugs).
  - **Partitioning and filegroup/data-space placement** not captured.
  - **Out-of-scope object kinds**: functions, views, triggers, sequences, synonyms, scalar CLR UDTs (link to the README scope section).
  - **Encrypted modules**: two different `WITH ENCRYPTION` procs with identical signatures collide on the `<encrypted>` sentinel (inherent — the definition is unreadable).
- **Resolved in v2** (brief, one line each, for the audit trail): column order, identity seed/increment/NFR, constraint disabled/trust state, constraint names + `IsSystemNamed`, encrypted-proc sentinel, sparse/rowguidcol/masking/XML capture, UDT schema-qualification, alias base type (Phase 2), plus the two existing `ObjectNamesToIgnore` entries.

Mirror the open items in `README.md`'s "Not Yet Captured" section so NuGet consumers see them without visiting the repo (the README is packed into the .nupkg).

---

## Phase 4 — Test additions

### 4.1 Memory-optimized table coverage

`TableSchema.IsMemoryOptimized` and `DurabilityDesc` are extracted and hashed but have **zero** test coverage.

- Add a helper to `tests/SqlSchemaHash.IntegrationTests/TestHelpers/DatabaseTestHelpers.cs` that prepares a database for In-Memory OLTP: `ALTER DATABASE [db] ADD FILEGROUP [mod] CONTAINS MEMORY_OPTIMIZED_DATA; ALTER DATABASE [db] ADD FILE (NAME='mod1', FILENAME='/var/opt/mssql/data/<db>_mod') TO FILEGROUP [mod];` (the path is inside the Linux container). Verify the container image supports it; per-test databases are already dropped in cleanup — confirm dropping a DB with a memory-optimized filegroup works in the fixture (it should).
- New tests in `tests/SqlSchemaHash.IntegrationTests/Fidelity/TableFidelityTests.cs` (or a new file if cleaner):
  1. Memory-optimized vs. equivalent disk-based table → different hash under Strict.
  2. `DURABILITY = SCHEMA_AND_DATA` vs `SCHEMA_ONLY` → different hash under Strict.
  3. Two identical memory-optimized tables in separate databases → equal hash (determinism).

### 4.2 New DB-free unit test project

Create `tests/SqlSchemaHash.UnitTests/` so contributors and CI can run fast tests without Docker.

- Mirror the integration project's csproj (same MSTest package versions, TFM, conventions); add it to `SqlSchemaHasher.sln`; add an `InternalsVisibleTo` for it in `src/zachtbeer.SqlSchemaHasher.csproj` (needed because Phase 1 internalized the calculator).
- **Move** (not copy) from the integration project:
  - `Api/SchemaHashEnvelopeTests.cs` — already fully DB-free.
  - The three in-memory tests in `Determinism/DeterminismTests.cs` (culture independence, little-endian golden wire vector, `SupportsUnlimitedHashBytes` logic) — they construct `SchemaMetadata` in memory but currently pay the container cost because the class derives from `IntegrationTestBase`. In the unit project they must not derive from it.
- **Add** unit tests for Phase 1: argument validation (`GetHashAsync`/`ExtractSchemaAsync`/`ComputeHash` null/empty inputs throw the documented exception types) and a couple of in-memory `ComputeHash` option sanity checks (e.g. `Default` is `V2`; an all-Strict options object vs `new SchemaHashOptions()` produce identical hashes on a hand-built `SchemaMetadata`).
- **CI**: in `.github/workflows/ci.yml`, add a fast `dotnet test tests/SqlSchemaHash.UnitTests` step to the `build` job (no Docker needed there). The `integration` matrix job can keep running the whole solution — double-running the cheap unit tests is harmless. Keep locked-mode restore consistent (generate the lockfile for the new project; `RestorePackagesWithLockFile` comes from `Directory.Build.props`).
- Update `CONTRIBUTING.md` and `README.md` "Running Tests": unit tests run with plain `dotnet test tests/SqlSchemaHash.UnitTests`; integration tests need Docker.

### 4.3 Small targeted tests

- **Alias-type fidelity** (Phase 2 acceptance) in `Fidelity/ColumnFidelityTests.cs` or `TableTypeFidelityTests.cs` as appropriate.
- **Empty-definition sentinel**: `SchemaExtractor.ComputeEmptyDefinitionHash` (~`src/SchemaExtractor.cs:497`) uses `"0"` to match server-side `CHECKSUM('')` behavior — pin this subtle coupling with a test (a proc whose definition is empty/whitespace after extraction hashes stably; assert the sentinel constant's value so a refactor can't silently change it).

---

## Phase 5 — Packaging & repo polish

1. **Package icon**: add `icon.png` at the repo root (128×128 PNG). Generate a minimal clean placeholder if no asset is provided (any locally available tool; if none, create the csproj wiring anyway and leave a clearly-flagged TODO for the maintainer to drop in the asset). Wire into `src/zachtbeer.SqlSchemaHasher.csproj`:
   `<PackageIcon>icon.png</PackageIcon>` plus `<None Include="../icon.png" Pack="true" PackagePath="/"/>` (pattern matches the existing README pack entry).
2. **`global.json`** at the repo root pinning the SDK, consistent with the deterministic-build positioning. Use the 10.x band (required to build the `net10.0` target): `{ "sdk": { "version": "10.0.100", "rollForward": "latestFeature" } }` — adjust the exact version to what `dotnet --list-sdks` shows locally, and confirm CI (which installs 9.0.x + 10.0.x) still resolves.
3. **`nuget.config`** at the repo root: nuget.org as the sole package source with package-source mapping (`<packageSourceMapping><packageSource key="nuget.org"><package pattern="*"/></packageSource></packageSourceMapping>`). This completes the supply-chain story (lockfiles + locked-mode restore already exist).
4. **README truth-up**:
   - The "Schema Change Detection" example compares raw strings (`beforeHash != afterHash`); rewrite it to use `SchemaHashResult.Compare` per the doc's own guidance.
   - Add a consolidated **Requirements** section: SQL Server 2016+/Azure SQL minimum, versions exercised in CI (2019/2022) and locally (2025), .NET TFMs supported.
   - Verify the badge row and "Stable / published on NuGet.org" language are accurate *at release time* (v2 is currently unreleased; NuGet badges will show v1.x until then — a parenthetical or no change may be fine, but don't ship text that's false on day one).
5. **CHANGELOG.md**: under `[2.0.0]`, add entries for this wrap-up (extractor/calculator internalized — *breaking*, CancellationToken support, argument validation, alias-type capture, memory-optimized test coverage, unit-test project); add the missing `[2.0.0]` comparison link at the bottom (`[2.0.0]: https://github.com/zachtbeer-labs/sqlschemahasher/compare/v1.0.0...v2.0.0`). Leave the date as Unreleased — the maintainer sets it when shipping.
6. **CONTRIBUTING.md**: fix the ".NET 9 SDK" staleness (the library multi-targets through net10.0; the pinned SDK from item 2 is the requirement); describe the unit vs. integration test split; add a short **Releasing** section documenting the manual flow: bump csproj `<Version>` → update CHANGELOG date + link → merge → run `release.yml` via workflow_dispatch with the matching version → verify the NuGet listing, provenance attestation, and GitHub release.
7. **Hygiene**: ensure `.DS_Store` is in `.gitignore`; check whether any `.DS_Store` files are git-tracked (`git ls-files | grep DS_Store` — read-only) and if so list them for the maintainer to `git rm` (do not run git writes). Same check for `.vs/`.

---

## Phase 6 — Verification and handoff

1. `dotnet build SqlSchemaHasher.sln` — must be clean (warnings-as-errors catches XML-doc and nullable regressions from Phase 1).
2. Run the **unit** project without Docker; run the **full integration suite** via the `test-runner` subagent. Everything green; golden hashes unmoved except any knowingly re-pinned with a CHANGELOG note.
3. `dotnet pack src/zachtbeer.SqlSchemaHasher.csproj -c Release` and inspect the .nupkg (it's a zip): confirm `README.md`, `icon.png`, XML docs, and all five TFM libs are present, and the nuspec metadata (license, icon, readme, repo URL) is correct.
4. Leave the working tree uncommitted. Produce a final summary for the maintainer listing: every file changed and why, any golden-hash re-pins, any items flagged for maintainer action (icon asset if not generated, tracked `.DS_Store` removals, release steps).

### Maintainer-only release steps (not for the implementing agent)

Commit/PR/merge `zb/v2-prep` → set the CHANGELOG `[2.0.0]` date → tag `v2.0.0` → run `release.yml` with version `2.0.0` → verify NuGet listing, provenance attestation, and the GitHub release.

## Explicitly out of scope for v2 (do not implement)

- Definition-text normalization (bug #5) — document only.
- Always Encrypted column test coverage.
- The presets round (folding physical/enforcement/collation bits into `V1`/`V2`/`Structural`).
- MinVer / tag-driven versioning or a release version-consistency guard.
- Extraction of functions, views, triggers, sequences, synonyms.
