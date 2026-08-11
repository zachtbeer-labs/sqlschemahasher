---
id: comparing-hashes
title: Comparing Hashes
sidebar_position: 6
---

# Comparing Hashes

Every hash is returned as a **versioned envelope**: `<version>:<base64hash>`, e.g. `2:dGhpcyBpc...`.

- **`version`** — the hash-format version, currently `2`. The hash output is stable within a hash-format version and changes only when the hash contract does (in practice at a major release, as extraction fidelity improves). This lets you tell a real schema change apart from a library upgrade.
- **`hash`** — the base64 SHA256 of the schema.

The base64 alphabet contains no `:`, so the envelope parses unambiguously.

:::warning Compare only hashes computed with the same options
The envelope does not record which [options](./options-and-presets.md) were used, so a `V2` hash and a `Structural` hash of the *same* database have different hashes and compare as `Different` — indistinguishable from a genuine schema change. Fix your options in one place (a preset or shared config) and use them on both sides, the same way you would keep the library version consistent.
:::

## Using `SchemaHashResult`

For raw storage or logging, plain string equality still works when you know both hashes came from the same library version and options. Otherwise, compare with `SchemaHashResult`, which makes the version distinction explicit:

```csharp
switch (SchemaHashResult.Compare(hashA, hashB))
{
    case SchemaHashComparison.Equal:         // same schema
    case SchemaHashComparison.Different:      // schema differs (or different options were used)
    case SchemaHashComparison.Incomparable:   // different library version — recompute before trusting
}

// Or inspect the parts (e.g. to pull the bare hash back out):
var parsed = SchemaHashResult.Parse(hashA);
Console.WriteLine($"v{parsed.Version} / {parsed.Hash}");
```

`SchemaHashResult.TryParse` returns `false` for a legacy bare-base64 hash produced before envelopes existed, so those safely compare as `Incomparable` rather than silently mismatching.

## Comparison outcomes

| Outcome | Meaning |
|---------|---------|
| `Equal` | Same hash-format version and same hash — the schemas are equal. |
| `Different` | Same version but the hashes differ. Usually a genuine schema difference — but note the hash also differs when the two hashes were computed with different comparison options, which the library does not detect. |
| `Incomparable` | The two hashes were produced by different library versions (or one could not be parsed, including a legacy bare-base64 value). Recompute both with a single library version before trusting the result. |

The `SchemaHashResult` API:

- `override string ToString()` — renders `<version>:<hash>`.
- `static SchemaHashResult Parse(string value)` — throws `FormatException` on malformed input (including a legacy bare-base64 hash with no version prefix).
- `static bool TryParse(string? value, out SchemaHashResult result)` — non-throwing; legacy bare hashes return `false` so callers treat them as `Incomparable`.
- `static SchemaHashComparison Compare(SchemaHashResult a, SchemaHashResult b)` and `static SchemaHashComparison Compare(string a, string b)` — either string failing to parse yields `Incomparable`.
