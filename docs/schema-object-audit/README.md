# SQL Server Object-Type Coverage Audit

Sixteen SQL Server schema object types, each documented blind against a live SQL Server 2025 catalog
(no repository source consulted), then audited against `SchemaExtractor.cs`/`SchemaMetadata.cs`/
`SchemaHashCalculator.cs`. Confirmed gaps are filed in [`BUGS.md`](../../BUGS.md) under
"Open / known limitations". See the design spec at
[`docs/superpowers/specs/2026-08-09-schema-object-audit-design.md`](../superpowers/specs/2026-08-09-schema-object-audit-design.md)
and the implementation plan at
[`docs/superpowers/plans/2026-08-09-schema-object-audit-plan.md`](../superpowers/plans/2026-08-09-schema-object-audit-plan.md).

32 gaps were confirmed and filed across the 16 topics. None of them were fixed as part of this audit —
remediation is separate, future, scoped work.

| Topic | Doc | Gaps filed |
|---|---|---|
| Tables | [tables.md](tables.md) | 3 |
| Indexes | [indexes.md](indexes.md) | 4 |
| Constraints | [constraints.md](constraints.md) | 1 |
| Extended Properties | [extended-properties.md](extended-properties.md) | 0 |
| Stored Procedures | [stored-procedures.md](stored-procedures.md) | 6 |
| Functions | [functions.md](functions.md) | 5 |
| Triggers | [triggers.md](triggers.md) | 2 |
| Views | [views.md](views.md) | 3 |
| User-Defined Table Types | [user-defined-table-types.md](user-defined-table-types.md) | 0 |
| Sequences | [sequences.md](sequences.md) | 0 |
| Synonyms | [synonyms.md](synonyms.md) | 1 |
| Alias/CLR User-Defined Types | [alias-clr-udts.md](alias-clr-udts.md) | 1 |
| Schemas | [schemas.md](schemas.md) | 2 |
| Full-Text Indexes/Catalogs | [full-text.md](full-text.md) | 3 |
| XML Schema Collections | [xml-schema-collections.md](xml-schema-collections.md) | 1 |
| Partition Functions/Schemes & Filegroups | [partition-filegroups.md](partition-filegroups.md) | 0 |

## Method

Each topic ran through two independent agent passes:

1. **Document (blind)** — researched the live catalog and Microsoft Learn documentation only, with no
   access to this repository's source, and produced a self-contained reference for the object type.
2. **Audit** — read `SchemaExtractor.cs`/`SchemaMetadata.cs`/`SchemaHashCalculator.cs` and compared them
   against the blind doc, filing a `BUGS.md` entry for each catalog field that is queryable, schema-
   relevant (not runtime state), and not currently extracted or hashed correctly. A finding already
   covered by an existing documented exclusion (`website/docs/what-gets-hashed.md`'s "Not yet captured"
   section, or an existing `BUGS.md` entry) was not re-filed.

Four batches of four topics ran against one shared `mcr.microsoft.com/mssql/server:2025-latest` Docker
container (the same image the integration test suite uses), each topic working in its own uniquely-named
schema to avoid colliding with the other topics running concurrently in the same batch.
