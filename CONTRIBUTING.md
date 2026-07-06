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

- [.NET SDK](https://dotnet.microsoft.com/download) matching the version pinned in `global.json` (the library multi-targets `net6.0` through `net10.0`; building the `net10.0` target requires a 10.0.x SDK)
- [Docker](https://www.docker.com/) (for integration tests -- Testcontainers spins up SQL Server 2025)

### Build and Test

```bash
dotnet build SqlSchemaHasher.sln
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

`dotnet test SqlSchemaHasher.sln` runs both.

## Pull Request Guidelines

1. Branch from `main`.
2. Describe what you changed and why.
3. Include tests for new or changed behavior.
4. Make sure CI passes before requesting review.

## Releasing

Releases are manual (no MinVer or tag-driven versioning):

1. Bump `<Version>` in `src/zachtbeer.SqlSchemaHasher.csproj`.
2. Update `CHANGELOG.md`: set the release date on the version's heading (replacing "Unreleased") and confirm its comparison link at the bottom is correct.
3. Merge to `main`.
4. Run the `release.yml` workflow via `workflow_dispatch`, passing the matching version (e.g. `2.0.0`).
5. Verify the NuGet listing, the SLSA provenance attestation, and the GitHub release it produces.

## Code Style

- Function parameters, constructor arguments, and record definitions should always be on a single line. Do not wrap parameters onto multiple lines.
- Keep things simple. This is a focused library, not a framework.
