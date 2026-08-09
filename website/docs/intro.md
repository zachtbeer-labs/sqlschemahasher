---
id: intro
title: SqlSchemaHasher
slug: /
sidebar_label: Overview
sidebar_position: 1
---

# SqlSchemaHasher

**One deterministic SHA256 per database schema — ground truth for drift detection across fleets.** Read-only `sys.*` queries, no agents, no telemetry.

When you manage many SQL Server databases, schemas drift. Someone modifies a table directly. A backup gets restored from the wrong date. A migration partially applies and nobody notices. SqlSchemaHasher computes a deterministic SHA256 hash from the schema itself, so "what schema is this database actually running?" becomes a one-line query with a one-string answer.

```csharp
using zachtbeer.SqlSchemaHasher;

var hash = await SqlSchemaHash.GetHashAsync("Server=localhost;Database=MyDb;Trusted_Connection=true");
// Returns a versioned envelope: "2:dGhpcyBpcyBhIGJhc2U2NCBoYXNo..."
```

Ready to try it? Head to [Getting Started](./getting-started.md).

## Why not version tables, migration journals, or schema compare?

| You want to… | Version table / migration journal | SqlPackage schema compare | **SqlSchemaHasher** |
| --- | --- | --- | --- |
| Detect manual, out-of-band schema changes | No — tracks what *ran*, not what the schema *looks like* | Yes, but only against one chosen baseline | Yes — the hash is computed from the schema itself |
| Trust the answer without process discipline | No — someone has to bump it, everywhere, consistently | Yes | Yes — content-based, it cannot lie |
| Group an entire fleet by actual deployed schema | Only as reliable as the version data | Pairwise comparisons, O(n²) and slow | One hash per database, group by string |
| Get a CI-friendly single value to log or compare | A version string that says what *should* be deployed | A diff report | One base64 SHA256 string |
| Stay fast on large estates | Fast but untrustworthy | Full model extraction per database | One batched read of `sys.*` catalog views |
| Ignore cosmetic differences (auto-named indexes, clustering) | N/A | Limited | Built-in [normalization options](./options-and-presets.md) |

Version numbers tell you what *should* be deployed, not what *actually is* deployed. A content-based hash is ground truth: two databases with the same hash are structurally identical, period. Hashes also surface unexpected groupings — you might discover that 300 databases are on schema A, 50 are on schema B, and 3 are on something nobody recognizes.

## Built to be trusted

- **Read-only by design** — the library only reads `sys.*` catalog views; it never modifies the target database.
- **No network calls except to your SQL Server, no telemetry**, no analytics, no license checks.
- **Deterministic, reproducible builds** with SourceLink and symbol packages (`.snupkg`).
- **Locked-mode NuGet restore** — dependency versions are pinned via committed lock files.
- **All GitHub Actions pinned to full commit SHAs** with least-privilege `permissions` blocks.
- **CodeQL static analysis, OpenSSF Scorecard, and Dependabot** run continuously.
- **OIDC trusted publishing to NuGet.org** — no long-lived API keys.
- **Signed SLSA build provenance and a CycloneDX SBOM** attached to every release.

## Use it for

- **Define the golden schema** — Hash a reference database and compare everything against it. Any mismatch is immediately actionable.
- **Continuous drift monitoring** — Compute schema hashes on a schedule and report them centrally. Deviations surface in dashboards instead of in support tickets.
- **Migration tooling** — Compare a database's hash against the target schema before generating diff SQL. If hashes match, skip the diff entirely.
- **Fleet-wide grouping** — Group all databases by hash to get the full picture of what's actually deployed.

## Performance

SqlSchemaHasher is fast — schema hashing is measured in milliseconds, not seconds. See [Performance](./performance.md) for measured numbers, methodology and caveats.

:::note Project status
v2.0.0 is currently in preparation. The badges in the repository and the package on NuGet.org reflect the latest *published* release; this documentation describes the code in the repository, which may be ahead of what's published.
:::
