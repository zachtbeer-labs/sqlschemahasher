---
id: performance
title: Performance
sidebar_position: 6
---

# Performance

Two things are measured separately: the cost of **extracting** a schema from SQL Server, and the
cost of **hashing** what was extracted. They differ by roughly two orders of magnitude, and only one
of them is affected by your database size in a way you can feel.

## The short version

Hashing a 200-table, 500-procedure database takes about **450 ms**, of which about **6 ms** is the
SHA256 work. The other 98.7% is catalog round-trips.

## What the profiles are

Synthetic schemas generated to a fixed shape. They are not your database, but they bracket a
realistic range.

| Profile | Tables | Columns/table | Indexes/table | Procedures | Views | Functions | Triggers | Table types | Extended properties |
|---|---|---|---|---|---|---|---|---|---|
| Small | 25 | 8 | 2 | 50 | 10 | 5 | 5 | 3 | 25 |
| Medium | 200 | 12 | 3 | 500 | 75 | 40 | 40 | 15 | 200 |
| Large | 1,000 | 16 | 4 | 2,000 | 300 | 150 | 150 | 50 | 1,000 |

The object counts above are honored identically by both renderers behind the two tiers below, but
the end-to-end tier's seeded database is structurally simpler than the hash-calculation tier's
in-memory corpus in ways the counts don't show: no computed columns, no indexed views, no
multi-statement table-valued functions, single-parameter modules instead of varied parameter lists,
plain `AFTER` triggers only (no `INSTEAD OF`, none disabled), object-scoped extended properties only
(no column-scoped ones), and no cycling sequences. Don't read the two tables below as describing
identical schemas — see [`DdlCorpus`](https://github.com/zachtbeer-labs/sqlschemahasher/blob/main/benchmarks/SqlSchemaHasher.Benchmarks.Corpus/DdlCorpus.cs)
for the full list of what the DDL tier does not model.

## End to end

`GetHashAsync` against a live database, alongside `ExtractSchemaAsync` alone so the split is
visible. Default options (`SchemaHashOptions.V2`).

| Profile | `ExtractSchemaAsync` | `GetHashAsync` | Hashing share |
|---|---:|---:|---:|
| Small | 56.2 ms | 57.0 ms | 1.4% |
| Medium | 444.7 ms | 450.6 ms | 1.3% |
| Large | 5.27 s | 5.30 s | &lt;1% (within run-to-run noise) |

**Extraction dominates.** At Small and Medium, the difference between the two columns is the entire
hash computation — 0.82 ms and 5.90 ms respectively, both well outside the ±0.3–3.0 ms error bars on
these runs, so that gap is a real measurement. At Large it isn't: the 25 ms gap between 5,271.61 ms
± 50.702 ms and 5,297.04 ms ± 65.548 ms is smaller than either measurement's own error, and
BenchmarkDotNet's `Ratio` column reports both rows as `1.00` — indistinguishable at this resolution.
It also disagrees with the hash-calculation tier below, which measures Large `V2` hashing at 34.9 ms
on its own, larger than the entire end-to-end delta observed here. The hash-calculation tier hashes
pre-extracted metadata directly and is the reliable source for hash cost at every size; the
end-to-end delta above is only a usable estimate where it clears the error bars, which holds at Small
and Medium but not at Large. If you want a schema hash to be faster, the lever is the number of
catalog round-trips, not the hash.

A practical consequence: if you already hold a `SchemaMetadata` from `ExtractSchemaAsync`, computing
additional hashes from it with different options via `SqlSchemaHash.ComputeHash` is nearly free
compared to re-extracting.

## Hash calculation

`SqlSchemaHash.ComputeHash` over pre-extracted metadata — no database involved.

| Profile | Strict | V1 | V2 | Structural |
|---|---:|---:|---:|---:|
| Small | 522 µs | 525 µs | 500 µs | 523 µs |
| Medium | 6.13 ms | 6.24 ms | 6.16 ms | 6.12 ms |
| Large | 35.2 ms | 35.9 ms | 34.9 ms | 35.6 ms |

**Normalization presets cost nothing measurable.** All four land within run-to-run noise of each
other at every size, because they hash the same number of elements and differ only in which field
values get neutralized first. Choose a preset for the comparison semantics you want — the decision
carries no performance trade-off.

Cost scales close to linearly with object count, with no super-linear behavior at the sizes
measured.

## Cost by object kind

Each object kind hashed in isolation, at the Medium profile with `V2`. "Share of total" is each
kind's mean against the Medium `V2` total from the hash-calculation table above (6.16 ms).

| Object kind | Mean | Share of total |
|---|---:|---:|
| Tables (with columns, indexes, constraints) | 3,888 µs | 63% |
| Modules (procedures, views, functions, triggers) | 1,787 µs | 29% |
| Table types | 195 µs | 3.2% |
| Extended properties | 135 µs | 2.2% |
| Sequences and synonyms | 12 µs | 0.2% |

The five sum to 6.02 ms against the Medium profile's 6.16 ms total, so they account for essentially
all the work.

Tables dominate because each carries columns, indexes and four kinds of constraint, all of which are
sorted and streamed. Module hashing is comparatively cheap because module bodies are hashed
**server-side** with `HASHBYTES` during extraction — the text never crosses the wire, and the
client only hashes the server-computed hash value along with the signature.

## Methodology

```
BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.8973/25H2/2025Update/HudsonValley2)
12th Gen Intel Core i9-12900K 3.19GHz, 1 CPU, 24 logical and 16 physical cores
.NET SDK 10.0.302
  [Host]     : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v3
  DefaultJob : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v3
```

Measured with [BenchmarkDotNet](https://benchmarkdotnet.org). Raw exports for every released version
are committed under
[`benchmarks/results`](https://github.com/zachtbeer-labs/sqlschemahasher/tree/main/benchmarks/results).

### Read these numbers with the following caveats

- **The database was SQL Server LocalDB on the same machine.** There is no network between client
  and server, so extraction times are a floor. A networked or cloud database will be slower, and the
  gap grows with round-trip count — which means the Large profile suffers most.
- **Benchmarks target `net10.0` only.** The package targets `net6.0` through `net10.0`; these
  numbers are not a promise about the others.
- **Synthetic schemas, not yours.** A database with unusually wide tables, very large module bodies,
  or heavy extended-property use will differ.
- **One machine, one run.** Absolute values are specific to the hardware above. Ratios and scaling
  behavior travel better than milliseconds do.
- **The end-to-end tier's LocalDB default is Windows-only.** SQL Server LocalDB has no Linux or
  macOS build. Reproducing the end-to-end numbers elsewhere requires
  `SQLSCHEMAHASHER_BENCHMARK_CONNECTIONSTRING` pointed at any reachable SQL Server, including a
  Docker container — see [Reproducing](#reproducing) below. The hash-calculation tier has no
  database and runs anywhere.

## Reproducing

```bash
git clone https://github.com/zachtbeer-labs/sqlschemahasher.git
cd sqlschemahasher

# Hash calculation only — no database required
dotnet run --project benchmarks/SqlSchemaHasher.Benchmarks -c Release

# End to end — uses SQL Server LocalDB by default, no Docker required (Windows only; see below)
dotnet run --project benchmarks/SqlSchemaHasher.Benchmarks -c Release -- --anyCategories Integration
```

The LocalDB default only works on Windows — SQL Server LocalDB has no Linux or macOS build. On other
platforms, or to measure against a different server (for example to see what network latency costs
you), set `SQLSCHEMAHASHER_BENCHMARK_CONNECTIONSTRING` to point at any reachable SQL Server,
including a Docker container.
