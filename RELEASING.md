# Releasing

How to publish a new version of `zachtbeer.SqlSchemaHasher` to NuGet.

**The whole release is a git tag.** You do not edit a version number anywhere. You tag a commit, push the tag, and GitHub Actions does the rest.

```bash
git tag v99.0.0
git push origin v99.0.0
```

> Throughout this page, **`99.0.0` is a stand-in** for the version you're actually releasing. It is deliberately not a real version number, so that pasting a command from here without editing it can't publish anything.

The rest of this page explains what that does, how to pick the number, and what to do when something goes wrong.

## How versioning works here

The project uses [MinVer](https://github.com/adamralph/minver), which reads the version out of git tags at build time. There is no `<Version>` in any `.csproj` — if you go looking for one to bump, you won't find it, and that's on purpose. One number, in one place: the tag.

| You build...                       | The package version is...                  |
| ---------------------------------- | ------------------------------------------ |
| a commit tagged `v99.0.0`          | `99.0.0`                                   |
| a commit 7 commits past `v2.0.0`   | `2.0.1-alpha.0.7` (an automatic prerelease) |

That second row is why you can publish a preview package at any time without deciding on a number first.

## Picking the version number

Standard [semantic versioning](https://semver.org/) — `MAJOR.MINOR.PATCH`:

| Bump      | When                                                | Example for this library                                          |
| --------- | --------------------------------------------------- | ----------------------------------------------------------------- |
| **PATCH** | A bug fix that changes no API and no hash output     | Fixing a query that crashed on a table name containing a quote     |
| **MINOR** | New functionality, existing code keeps working       | A new `SchemaHashOptions` flag that is off by default              |
| **MAJOR** | Existing code breaks, **or the hash output changes** | Extracting a new object kind, so a database hashes differently now |

That last one is the rule people get wrong here. **If a change makes an unchanged database produce a different hash, it is a major release**, even if no method signature moved. Consumers store these hashes and compare them over time; silently changing the output would show up as a phantom schema change in their systems.

### If the hash output changes, also bump the hash-format version

The hash your library returns looks like `2:gLr4x9A7Pihh...`. That leading `2` is the *hash-format version*, and it is a hand-maintained constant, deliberately separate from the package version:

```csharp
// src/SqlSchemaHash.cs
private const int HashFormatVersion = 2;
```

If you are cutting a major release **because the hash output changed**, bump that constant in the same pull request, and re-pin the golden test hashes in `tests/SqlSchemaHash.IntegrationTests/Settings/RegressionAnchorTests.cs`. That is what lets a consumer's `SchemaHashResult.Compare` return `Incomparable` ("this hash came from a different version of the library") instead of `Different` ("your schema changed"). If a major release doesn't touch the hash output, leave the constant alone.

## Step by step

### 1. Check that `main` is green

Open the [Actions tab](https://github.com/zachtbeer-labs/sqlschemahasher/actions) and confirm the latest CI run on `main` passed. The release workflow runs the full test suite too, but finding out here is faster.

### 2. Update `CHANGELOG.md`

Find the heading for the version you're about to release and replace `Unreleased` with today's date:

```diff
-## [99.0.0] - Unreleased
+## [99.0.0] - 2026-08-10
```

Then check the link definitions at the very bottom of the file. Each release has one, pointing at the diff against the previous release — this is the real line for 2.0.0, and yours should follow the same shape:

```
[2.0.0]: https://github.com/zachtbeer-labs/sqlschemahasher/compare/v1.0.0...v2.0.0
```

If the heading for your version doesn't exist yet, write it — the entries belong under `### Added`, `### Changed`, `### Fixed`, and `### Breaking`, following the entries already in the file.

The release workflow **refuses to publish** a version whose changelog heading is missing or still says "Unreleased", so this step is enforced, not just polite.

### 3. Get it onto `main`

Open a pull request with the changelog change, get it reviewed, and merge it. Then make sure your local checkout is up to date:

```bash
git checkout main
git pull
```

### 4. Tag and push

```bash
git tag v99.0.0
git push origin v99.0.0
```

The `v` prefix is required — the workflow only triggers on tags starting with `v`, and MinVer is configured to strip it.

### 5. Watch it run

Pushing the tag starts the **Release** workflow. Watch it in the [Actions tab](https://github.com/zachtbeer-labs/sqlschemahasher/actions). It takes several minutes, because it:

1. checks the changelog entry
2. builds and runs the full test suite, including the Docker-backed integration tests
3. packs the NuGet package (MinVer reads `v99.0.0` from your tag)
4. signs a build-provenance attestation and generates an SBOM
5. pushes to NuGet.org
6. creates the GitHub release, with the packages, attestation, and SBOM attached

### 6. Verify

- **NuGet** — [the package listing](https://www.nuget.org/packages/zachtbeer.SqlSchemaHasher) should show the new version. It can take 5–15 minutes to become installable while nuget.org indexes it.
- **GitHub** — [the releases page](https://github.com/zachtbeer-labs/sqlschemahasher/releases) should have a `v99.0.0` entry with four files attached.
- **Install it** — the real test:
  ```bash
  dotnet add package zachtbeer.SqlSchemaHasher --version 99.0.0
  ```

## Publishing a preview package

To get a build into someone's hands before committing to a version number, run the **Preview** workflow from the [Actions tab](https://github.com/zachtbeer-labs/sqlschemahasher/actions) ("Run workflow"). It takes no inputs — MinVer names the package from how far the commit is past the last tag, so you get something like `2.0.1-alpha.0.7`. The workflow's summary prints the exact `dotnet add package` command.

Preview packages are ordinary NuGet prereleases: they don't show up for users unless they opt into prerelease versions.

## When something goes wrong

**The workflow failed on "Verify CHANGELOG entry."**
The changelog heading for this version is missing, or still says "Unreleased". Nothing was published. Fix the changelog on `main`, then move the tag (see below) and push again.

**I tagged the wrong commit, and the workflow hasn't published yet.**
Delete the tag locally and remotely, then re-tag the right commit:

```bash
git tag -d v99.0.0
git push origin :refs/tags/v99.0.0
git tag v99.0.0 <correct-commit-sha>
git push origin v99.0.0
```

**The package is already on NuGet and it's wrong.**
You cannot replace a published version — NuGet versions are immutable, and this is a good thing: someone may already have restored it. Fix the problem and release the next patch version. If the published package is actively harmful, you can [unlist](https://learn.microsoft.com/nuget/nuget-org/policies/deleting-packages) it, which hides it from search while leaving it restorable for anyone who already depends on it.

**The version number came out wrong (`0.0.0-alpha...` or a version I didn't expect).**
MinVer needs the full git history and tags to work. This happens on a shallow clone. In CI, that means a checkout missing `fetch-depth: 0`; locally, run `git fetch --tags`.

## What you never have to do

- Edit a version number in a `.csproj`, `Directory.Build.props`, or anywhere else
- Pass a version to the release workflow — it reads the tag
- Create the GitHub release by hand — the workflow does it
- Bump the `HashFormatVersion` constant for a release that didn't change hash output
