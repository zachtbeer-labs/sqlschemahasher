# Performance Benchmarks — Design

**Date:** 2026-08-09
**Branch:** `zb/v2-prep`
**Goal:** add a BenchmarkDotNet suite that documents how fast the library is and gives future maintainers a durable baseline for spotting regressions.

## Why

Two questions have no answer today:

1. **"How fast is it?"** — a consumer evaluating the package cannot tell whether hashing their database costs milliseconds or minutes, and the README makes no claim.
2. **"Did that change slow something down?"** — `SchemaHashCalculator` is 796 lines of sort-and-stream over every schema element. It is exactly the kind of code where an accidental re-sort inside a loop, or an `OrderBy` on a projection that allocates, degrades quietly. Nothing would catch it.

These are different questions with different tolerances for noise, and the design keeps them in separate tiers rather than compromising both into one suite.

## Prior art

Surveyed before choosing an approach, because "benchmarks in CI" has a wide range of sane implementations:

- **`dotnet/performance` → `dotnet/runtime`** runs benchmarks continuously against *merged* commits on dedicated, pinned perf-lab hardware, feeds results to a time-series store, and auto-files `[Perf] <os>/<arch>: N Regressions on <date>` issues for human triage. It is retrospective, **not** a PR gate. A separate `ResultsComparer` tool handles manual A-vs-B comparison with a `--threshold`.
- **Andrey Akinshin (BenchmarkDotNet's author)**, on JetBrains Rider's setup: a small per-build suite, a comprehensive daily suite on dedicated machines, and retrospective historical analysis. His caution is the operative part — large standard deviations, multimodal distributions and I/O outliers are normal, and fixed thresholds get overfit and turn flaky as small degradations accumulate.
- **Peer libraries at this repo's size — Dapper, Npgsql, Serilog, MessagePack-CSharp — have no automated regression detection.** They keep a benchmark project with exporters configured and run it by hand in Release. Dapper's entry point is `dotnet run --project benchmarks/Dapper.Tests.Performance -c Release -f net8.0 -- -f * --join`, with CSV, GitHub-Markdown and HTML exporters plus `MemoryDiagnoser`.
- **`benchmark-action/github-action-benchmark`** is the turnkey middle: parses BDN output, appends to `dev/bench/data.js` on `gh-pages`, renders time-series charts, and supports `alert-threshold` / `comment-on-alert` / `fail-on-alert`. Its own documentation warns that GitHub-hosted runners swing ±10–20% (more with I/O) and recommends self-hosted runners otherwise — which is why its default alert threshold is 200%, not 5%.

Repo-specific finding: `docs.yml` publishes via `actions/upload-pages-artifact` + `actions/deploy-pages`, which **replaces the entire site** from `website/build`. There is no committed `gh-pages` content model here, so `github-action-benchmark`'s default storage would either collide with the docs deploy or need a separate branch whose charts nothing serves. Not chosen for this round; recorded so the next person does not rediscover it.

**Decision: follow the peer-library pattern.** Manual runs, committed exports, comparison by `git diff`. No CI perf gate — neither the .NET team nor any surveyed peer gates PRs on benchmark results, and on shared runners a threshold loose enough to avoid flakes only catches catastrophes.

## Architecture

### Two tiers

| Tier | Measures | Docker | Run cadence |
|---|---|---|---|
| **Calculator** | `SqlSchemaHash.ComputeHash` over synthetic `SchemaMetadata` | no | any time; before/after any calculator change |
| **Integration** | `ExtractSchemaAsync` and `GetHashAsync` against a seeded SQL Server | yes | major/minor releases |

The calculator tier is the regression instrument: pure CPU over in-memory records, no I/O, low variance, seconds to run. The integration tier is the characterization instrument: it produces the "a 200-table / 500-procedure database costs roughly X" statement, and it is noisy enough that it is not asked to detect regressions.

### Project

`benchmarks/SqlSchemaHasher.Benchmarks/SqlSchemaHasher.Benchmarks.csproj`, added to `SqlSchemaHasher.sln`.

- `IsPackable=false` — nothing reaches the nupkg.
- `TargetFramework=net10.0`, single, rather than the library's `net6.0`–`net10.0` fan. A five-way matrix multiplies run time by five to measure *runtime* differences when the question is about *our* code. Documented as a scoping decision so the published numbers are not read as a net6.0 promise.
- `RestorePackagesWithLockFile` is inherited from `Directory.Build.props` and CI restores with `--locked-mode`, so `packages.lock.json` must be generated and committed with the project. Omitting it breaks CI restore, not just the benchmark.
- `dotnet test` ignores it (no test SDK); `dotnet build SqlSchemaHasher.sln` builds it, so `TreatWarningsAsErrors` applies and the project cannot rot unnoticed.

### The corpus: one profile model, two renderers

The linchpin of the design. A `SchemaProfile` record describes a database shape — table count, columns and indexes per table, stored procedures, views, functions, triggers, sequences, synonyms, table types, extended properties — with three named instances:

| Profile | Shape |
|---|---|
| `Small` | ~25 tables, ~50 procedures — a typical application database |
| `Medium` | ~200 tables, ~500 procedures — the headline profile |
| `Large` | ~1000 tables, ~2000 procedures — an enterprise upper bound |

Two renderers consume the same profile:

- `MetadataCorpus.Build(profile)` → an in-memory `SchemaMetadata` graph, for the calculator tier.
- `DdlCorpus.Script(profile)` → T-SQL that seeds an equivalent real database, for the integration tier.

Driving both tiers from one profile means the calculator numbers and the end-to-end numbers describe the same shape, so the extract-versus-hash split is directly readable across tiers.

**Generation is fully deterministic.** No unseeded randomness; any variation comes from a fixed seed. A corpus that drifts between runs makes every committed historical number incomparable, which would silently defeat the entire exercise.

**The two renderings are the same shape, not identical metadata.** SQL Server contributes its own defaults, system-named constraints and catalog artifacts, so a `DdlCorpus`-seeded database extracts to something close to but not byte-equal with the corresponding `MetadataCorpus` graph. The published docs must say this rather than implying the tiers are two views of one object.

### Benchmark classes

| Class | Axes | Category |
|---|---|---|
| `CalculatorBenchmarks` | size ladder × preset (`Strict` / `V1` / `V2` / `Structural`) | default |
| `ObjectKindBenchmarks` | tables-only, modules-only, table-types-only, extended-properties-only; `Medium` profile and `V2` preset throughout | default |
| `EndToEndBenchmarks` | 3 profiles × {`ExtractSchemaAsync`, `GetHashAsync`} | `Integration` |

The preset axis matters because normalization gating is where option-dependent work happens — `Structural` does strictly more projection work per element than `Strict`, and the cost of that is currently unknown. The object-kind axis is what makes a regression *attributable*: a slowdown points at a subsystem instead of at `ComputeHash` in general.

`EndToEndBenchmarks` provisions its SQL Server through Testcontainers in `[GlobalSetup]`, mirroring the existing `SqlServerFixture`, with an environment-variable connection-string override so a published number can be produced against real hardware instead of a container.

### Entry point

`Program.cs` is a `BenchmarkSwitcher` with a config carrying `MemoryDiagnoser`, `MarkdownExporter.GitHub`, `CsvExporter`, `JsonExporter.Full`, and an `ArtifactsPath` pointing at the committed results folder. The `Integration` category is excluded from the default run.

```
dotnet run --project benchmarks/SqlSchemaHasher.Benchmarks -c Release
dotnet run --project benchmarks/SqlSchemaHasher.Benchmarks -c Release -- --anyCategories Integration
```

## The failure mode worth guarding

An empty or under-built `SchemaMetadata` hashes in nanoseconds and reads as a spectacular improvement. Two guards:

1. `[GlobalSetup]` asserts corpus object counts against the profile and throws if they disagree, so a broken corpus fails loudly instead of reporting a fast number.
2. The corpus builder gets unit tests in the existing `SqlSchemaHash.UnitTests` project: counts match the profile, and two independent builds of the same profile hash identically. The second test is what stops silent corpus drift from invalidating committed history.

The benchmark methods themselves are not tested — but `ci.yml` gains a `--job Dry` smoke run, which executes each benchmark once with no statistics in a few seconds. That is a compile-and-run check, not a perf gate: it catches a benchmark that throws at PR time, which is the realistic rot for a project nobody runs between releases.

## Results and publication

- `benchmarks/results/2.0.0/` — committed GitHub-Markdown, CSV and JSON exports, including BenchmarkDotNet's host-environment block so every number carries the hardware it was measured on.
- `README.md` — a short Performance section with the `Medium` profile headline, linking to the docs page.
- `website/docs/performance.md` — full tables for both tiers, the extract-versus-hash split, methodology, single-TFM scoping caveat, the shape-not-identical caveat, and reproduction instructions.
- `CONTRIBUTING.md` — the two run commands and when to run each.

Comparing releases is a `git diff` over `benchmarks/results/`.

## Sequencing

Landing inside the v2.0.0 PR, per maintainer decision, so the committed baseline is pinned to exactly the tagged commit rather than to an ambiguous "shortly after". The added scope is acknowledged and accepted.

Work is ordered so the release-critical items (preset widening, documentation truth-up, release hygiene) complete first and the benchmark work can be deferred to a follow-up PR without rework if circumstances change.

## Out of scope

- Any CI performance gate or threshold-based build failure.
- `github-action-benchmark`, time-series charts, and the `gh-pages` storage question — deferred, with the `docs.yml` conflict recorded above.
- A `ResultsComparer`-style automated baseline diff tool.
- Multi-TFM benchmarking, and benchmarking across SQL Server versions.
- Profiling or optimization work. This design measures; it does not tune. Anything the first run exposes is triaged separately.
