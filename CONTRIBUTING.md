# Contributing

Thanks for your interest in contributing to SqlSchemaHasher! Here's everything you need to get started.

## Reporting Bugs / Requesting Features

Use [GitHub Issues](https://github.com/zachtbeer-labs/sqlschemahasher/issues). For bugs, include:

- What you expected to happen
- What actually happened
- SQL Server version and .NET version
- A minimal reproduction if possible

## Development Setup

### Prerequisites

- [.NET SDK](https://dotnet.microsoft.com/download) matching the version pinned in `global.json` (the library multi-targets `net8.0` through `net10.0`; building the `net10.0` target requires a 10.0.x SDK)
- [Docker](https://www.docker.com/) (for integration tests -- Testcontainers spins up SQL Server 2025)
- [SQL Server LocalDB](https://learn.microsoft.com/sql/database-engine/configure-windows/sql-server-express-localdb) (benchmarks only -- the integration benchmark tier defaults to LocalDB; see Benchmarks below)

### Build and Test

```bash
dotnet build SqlSchemaHasher.slnx
dotnet pack src/zachtbeer.SqlSchemaHasher.csproj -c Release
```

Tests are split into two projects:

- **Unit tests** (`tests/SqlSchemaHash.UnitTests`) — fast, no database required:
  ```bash
  dotnet test tests/SqlSchemaHash.UnitTests
  ```
- **Integration tests** (`tests/SqlSchemaHash.IntegrationTests`) — require Docker (Testcontainers spins up SQL Server):
  ```bash
  dotnet test tests/SqlSchemaHash.IntegrationTests
  ```

`dotnet test SqlSchemaHasher.slnx` runs both.

### Dependencies

This repo uses [Central Package Management](https://learn.microsoft.com/nuget/consume-packages/central-package-management). NuGet versions live in `Directory.Packages.props` at the repo root: project files reference packages without a `Version` attribute. To add or bump a dependency, add or edit its `<PackageVersion>` entry there, then regenerate the lock files with `dotnet restore SqlSchemaHasher.slnx --force-evaluate` and commit them alongside the change (CI restores with `--locked-mode`).

### Benchmarks

Performance benchmarks live in `benchmarks/` and are run by hand — there is no CI performance gate,
because shared runners vary too much between runs for a threshold to be meaningful. Committed
results under `benchmarks/results/<version>/` are the baseline; comparing releases is a `git diff`.
The `<version>` folder is the MinVer version of the build, so refresh a baseline from a tagged
commit — an untagged run lands in a prerelease folder (`2.0.1-alpha.0.7/`) rather than overwriting
a release's committed numbers.

**Calculator tier** — hashes synthetic in-memory schemas. No database, a few minutes to run. Use it
before and after any change to `SchemaHashCalculator`:

```bash
dotnet run --project benchmarks/SqlSchemaHasher.Benchmarks -c Release
```

**Integration tier** — end-to-end extraction and hashing against a real database, seeded from
generated DDL. Uses SQL Server LocalDB by default, so no Docker is needed. Run it at major and minor
releases to refresh the published characterization table:

```bash
dotnet run --project benchmarks/SqlSchemaHasher.Benchmarks -c Release -- --anyCategories Integration
```

**The LocalDB default only works on Windows** — SQL Server LocalDB has no Linux or macOS build. On
those platforms, set `SQLSCHEMAHASHER_BENCHMARK_CONNECTIONSTRING` to point at any reachable SQL
Server instead — a Docker container works fine here, even though the tier itself needs no Docker on
Windows. On Windows, set the same variable to measure against a different server. LocalDB excludes
network latency, which makes it a cleaner regression signal but understates what a networked
deployment sees.

Close other applications before a run whose results you intend to commit, and note that numbers are
only comparable across runs on the same machine — BenchmarkDotNet records the host environment in
every export for that reason.

## Pull Request Guidelines

1. Branch from `main`.
2. Describe what you changed and why.
3. Include tests for new or changed behavior.
4. Make sure CI passes before requesting review.

## Versioning

Versions come from git tags via [MinVer](https://github.com/adamralph/minver) — no `<Version>` lives in any project file. A tagged commit builds as that version (`v99.0.0` → `99.0.0`); an untagged commit builds as a height-based prerelease off the last tag (`2.0.1-alpha.0.7`). Because MinVer reads the repository, a build needs full history and tags: CI checks out with `fetch-depth: 0`, and a shallow local clone will produce the wrong number.

The hash-format version in the `<version>:<base64hash>` envelope is deliberately *not* derived from this. It is the `HashFormatVersion` constant in `src/SqlSchemaHash.cs`, bumped by hand only when the hash contract changes — a public data contract must not be able to move because of how a build was cloned.

## Releasing

The tag is the release: pushing it triggers `release.yml`, which builds, packs, publishes to NuGet, and creates the GitHub release.

```bash
git tag v99.0.0
git push origin v99.0.0
```

See **[RELEASING.md](RELEASING.md)** for the full walkthrough — picking the version number, the `CHANGELOG.md` step the workflow enforces, publishing a preview package, and what to do when something goes wrong.

## Code Style

- Function parameters, constructor arguments, and record definitions should always be on a single line. Do not wrap parameters onto multiple lines.
- Keep things simple. This is a focused library, not a framework.
