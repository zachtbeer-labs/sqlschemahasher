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

## End to end

`GetHashAsync` against a live database, alongside `ExtractSchemaAsync` alone so the split is
visible. Default options (`SchemaHashOptions.V2`).

| Profile | `ExtractSchemaAsync` | `GetHashAsync` | Hashing share |
|---|---:|---:|---:|
| Small | 56.2 ms | 57.0 ms | 1.4% |
| Medium | 444.7 ms | 450.6 ms | 1.3% |
| Large | 5.27 s | 5.30 s | 0.5% |

**Extraction dominates.** The difference between the two columns is the entire hash computation. If
you want a schema hash to be faster, the lever is the number of catalog round-trips, not the hash.

A practical consequence: if you already hold a `SchemaMetadata` from `ExtractSchemaAsync`, computing
additional hashes from it with different options via `SqlSchemaHash.ComputeHash` is nearly free
compared to re-extracting.

## Hash calculation

`SqlSchemaHash.ComputeHash` over pre-extracted metadata — no database involved.

| Profile | Strict | V1 | V2 | Structural |
|---|---:|---:|---:|---:|
| Small | 519 µs | 530 µs | 523 µs | 521 µs |
| Medium | 6.20 ms | 6.24 ms | 6.22 ms | 6.15 ms |
| Large | 35.5 ms | 34.5 ms | 35.5 ms | 34.8 ms |

**Normalization presets cost nothing measurable.** All four land within run-to-run noise of each
other at every size, because they hash the same number of elements and differ only in which field
values get neutralized first. Choose a preset for the comparison semantics you want — the decision
carries no performance trade-off.

Cost scales close to linearly with object count, with no super-linear behavior at the sizes
measured.

## Cost by object kind

Each object kind hashed in isolation, at the Medium profile with `V2`, relative to tables.

| Object kind | Mean | Share of total |
|---|---:|---:|
| Tables (with columns, indexes, constraints) | 3,904 µs | 63% |
| Modules (procedures, views, functions, triggers) | 1,795 µs | 29% |
| Table types | 191 µs | 3.1% |
| Extended properties | 130 µs | 2.1% |
| Sequences and synonyms | 12 µs | 0.2% |

The five sum to 6.03 ms against the Medium profile's 6.22 ms total, so they account for essentially
all the work.

Tables dominate because each carries columns, indexes and four kinds of constraint, all of which are
sorted and streamed. Module hashing is comparatively cheap because module bodies are hashed
**server-side** with `HASHBYTES` during extraction — the text never crosses the wire, and the
client only hashes the resulting digest along with the signature.

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

## Reproducing

```bash
git clone https://github.com/zachtbeer-labs/sqlschemahasher.git
cd sqlschemahasher

# Hash calculation only — no database required
dotnet run --project benchmarks/SqlSchemaHasher.Benchmarks -c Release

# End to end — uses SQL Server LocalDB, no Docker required
dotnet run --project benchmarks/SqlSchemaHasher.Benchmarks -c Release -- --anyCategories Integration
```

Set `SQLSCHEMAHASHER_BENCHMARK_CONNECTIONSTRING` to measure against a different server, for example
to see what network latency costs you.
