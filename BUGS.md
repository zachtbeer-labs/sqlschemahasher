# Known Bugs / Design Gaps

Tracked issues discovered during the settings-matrix test build (see `TESTPLAN.md`).

## Resolved

### 1. `ObjectNamesToIgnore` matched by bare name, ignoring schema — RESOLVED

**Was:** the extractor filtered with `objectNamesToIgnore.Contains(bareName)`, so an entry like
`"Things"` dropped the object from **every** schema (`dbo.Things` *and* `sales.Things`), with no way to
target one schema.

**Fix:** matching now accepts a schema-qualified entry (`sales.Things`, matches only that schema) or a
bare entry (`Things`, an explicit all-schemas shorthand). An object is excluded when a configured entry
equals its bare name or its `schema.name`. See `SchemaExtractor.IsIgnored`.

**Tests:** `Settings/ScopingTests.ObjectNamesToIgnore_SchemaQualified_TargetsSingleSchema` (precise
targeting) and `_BareName_MatchesAcrossSchemas` (the documented shorthand).

### 2. `ObjectNamesToIgnore` case-insensitivity was caller-dependent — RESOLVED

**Was:** matching used the comparer of whatever `IReadOnlySet<string>` the caller assigned, so the
documented "case-insensitive" behavior silently broke if a caller passed a plain (ordinal) `HashSet`.

**Fix:** added `SchemaHashOptions.ObjectNameComparer` (default `StringComparer.OrdinalIgnoreCase`). The
extractor rebuilds the match set with this comparer, so the library owns case behavior regardless of the
assigned set. Callers set `ObjectNameComparer = StringComparer.Ordinal` for case-sensitive matching.

**Tests:** `Settings/ScopingTests.ObjectNamesToIgnore_IsCaseInsensitiveByDefault_RegardlessOfSetComparer`
and `_CaseSensitive_WhenComparerIsOrdinal`.
