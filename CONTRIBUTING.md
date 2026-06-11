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

- [.NET 9 SDK](https://dotnet.microsoft.com/download)
- [Docker](https://www.docker.com/) (for integration tests -- Testcontainers spins up SQL Server 2025)

### Build and Test

```bash
dotnet build SqlSchemaHasher.sln
dotnet test SqlSchemaHasher.sln
dotnet pack src/zachtbeer.SqlSchemaHasher.csproj -c Release
```

## Pull Request Guidelines

1. Branch from `main`.
2. Describe what you changed and why.
3. Include tests for new or changed behavior.
4. Make sure CI passes before requesting review.

## Code Style

- Function parameters, constructor arguments, and record definitions should always be on a single line. Do not wrap parameters onto multiple lines.
- Keep things simple. This is a focused library, not a framework.
