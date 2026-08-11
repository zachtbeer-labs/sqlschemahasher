---
id: faq
title: FAQ
sidebar_position: 8
---

# FAQ

## Why doesn't the hash envelope encode the comparison options?

Three reasons, in order of weight:

1. **A deterministic fingerprint of `SchemaHashOptions` would have to stay stable across library evolution.** Every new option, enum bit, or property rename would change the fingerprint — and therefore turn every previously-stored hash `Incomparable`, even when the actual comparison semantics didn't move. Baking an options fingerprint into the envelope would effectively freeze the options design forever, since adding anything new becomes a breaking change for every caller with hashes at rest.
2. **The tool targets engineers comparing environments they control.** Using matching options across both sides of a comparison is the same published discipline as using a matching library version — both are documented caller responsibilities, not something the library can infer from the hash alone.
3. **A wrong `Incomparable` is worse than the documented caller contract.** If the envelope tried to track options and got it wrong (e.g. two option objects that are semantically equivalent but fingerprint differently), it would falsely tell a caller "these are incomparable" when they aren't. The current contract — a mismatch surfaces as `Different` — is simpler and correct for the intended audience.

## Why does comparing hashes computed with different options return `Different` rather than `Incomparable`?

Because of the answer above: the envelope carries no options fingerprint, so there's nothing for `SchemaHashResult.Compare` to detect a mismatch from. A hash computed under `Structural` and a hash of the same database computed under `V2` are just two different byte strings — the library has no way to tell "these differ because of options" from "these differ because the schema changed." Keep your options fixed in one place (a preset or shared config) and use the same one on both sides of every comparison, the same way you'd pin the library version.

## Why is definition text hashed exactly as SQL Server renders it?

CHECK constraint, DEFAULT constraint, computed-column, and filtered-index definitions are hashed as `sys.*` exposes them — SQL Server's own re-rendering of the original expression text, not the original text itself. Different SQL Server major versions (and sometimes CUs) can render the same expression differently: spacing, parenthesization, casing of built-in functions. That can produce a spurious `Different` comparison across server versions even though nothing semantically changed. See [BUGS.md](https://github.com/zachtbeer-labs/sqlschemahasher/blob/main/BUGS.md#definition-text-rendering-drift) for the full writeup. A parser-based (ScriptDom) normalization pass that canonicalizes expression text before hashing is under maintainer evaluation for a future release — it isn't in v2 because rewriting/canonicalizing expression text risks introducing new determinism bugs of its own, and no such rewriting is implemented yet.

## Why is the hash an envelope (`2:<base64>`) instead of a bare hash?

So a genuine schema change can be told apart from a library upgrade that changed *how* schemas are hashed. The `<version>` in `<version>:<base64hash>` is the library's hash-format version (currently tied to the major version); it lets `SchemaHashResult.Compare` return `Incomparable` when two hashes come from different hash-format versions, instead of a version-2 extraction fix silently masquerading as a schema change against a version-1 hash. Legacy bare-base64 hashes (produced before envelopes existed) fail to parse and also read as `Incomparable`, rather than being silently compared byte-for-byte against a value computed under a completely different hash contract.

## Why does a sequence's current value not affect the hash?

`sys.sequences.current_value` is runtime state, not schema — it advances every time any session runs `NEXT VALUE FOR`, including sessions that have nothing to do with a deliberate schema change. If it were part of the hash, two databases with byte-identical `CREATE SEQUENCE` definitions would drift apart the moment either one issued a single `NEXT VALUE FOR` call, which defeats the entire point of a schema hash. `SequenceSchema` captures the sequence's *definition* — start value, increment, bounds, cycle, cache — and deliberately omits `current_value`.

## Why aren't CLR objects or DDL/server triggers captured?

Scope. v2 added T-SQL views, scalar/table-valued functions, DML triggers, sequences, and synonyms — the object kinds an ordinary application schema is built from. CLR modules (`FUNCTION`/`AGGREGATE`/`TA` triggers), scalar CLR user-defined types, and DDL/database/server-scoped triggers remain out of scope: they're rarer in practice, and CLR assemblies in particular would require hashing compiled binary content rather than a T-SQL definition, a different extraction shape than everything else this library captures. See [What Gets Hashed](./what-gets-hashed.md) and [BUGS.md](https://github.com/zachtbeer-labs/sqlschemahasher/blob/main/BUGS.md) for the current out-of-scope ledger.
