# Release Architecture

This document describes the versioning strategy, tag naming conventions, release workflows, and step-by-step release process for each component in the Sqlity monorepo.

---

## Components and Release Types

| Component | Release type | NuGet package | GitHub Release |
|---|---|---|---|
| `Sqlity.Ado` | NuGet package | ✅ `Sqlity.Ado` | — |
| `Sqlity.EFCore` | NuGet package | ✅ `Sqlity.EFCore` | — |
| `Sqlity.Cli` | Standalone app | — | ✅ self-contained binaries |
| `Sqlity.Studio` | Standalone app | — | ✅ self-contained binaries |

Internal libraries (`Sqlity.Core`, `Sqlity.Query`, `Sqlity.Storage`) are **not** released independently — they are bundled directly into the NuGet packages at pack time.

---

## Versioning Strategy

Each component has **independent** versioning. There is no single repository-wide version.

- `src/Sqlity.Ado/Sqlity.Ado.csproj` — owns `<Version>` for the ADO package.
- `src/Sqlity.EFCore/Sqlity.EFCore.csproj` — owns `<Version>` for the EFCore package.
- `samples/Sqlity.Cli/Sqlity.Cli.csproj` — owns `<Version>` for the CLI app.
- `samples/Sqlity.Studio/Sqlity.Studio.csproj` — owns `<Version>` for the Studio app.
- `Directory.Build.props` — provides a shared `<Version>` **fallback** only for internal, non-packable libraries (Core, Query, Storage). This value is **not** used for CI-produced artifacts.

During CI, the version is injected from the git tag at build/pack time:

```
dotnet pack ... -p:Version=<tag-version>
dotnet publish ... -p:Version=<tag-version>
```

This ensures the NuGet package version, assembly version, and GitHub Release tag are all consistent.

---

## Tag Naming Conventions

| Component | Tag pattern | Example |
|---|---|---|
| Sqlity.Ado | `ado-vX.Y.Z` | `ado-v0.2.0` |
| Sqlity.EFCore | `efcore-vX.Y.Z` | `efcore-v0.2.0` |
| Sqlity.Cli | `cli-vX.Y.Z` | `cli-v0.1.0` |
| Sqlity.Studio | `studio-vX.Y.Z` | `studio-v0.1.0` |

Versions follow [Semantic Versioning](https://semver.org/): `MAJOR.MINOR.PATCH`.

Pre-release versions are also supported (e.g., `ado-v0.3.0-beta.1`). The workflow strips the prefix (`ado-v`) and passes the rest directly to `dotnet pack/publish`, so SemVer pre-release labels work as-is.

---

## Workflow Files

| File | Triggered by | Publishes to |
|---|---|---|
| `.github/workflows/publish-ado.yml` | `ado-v*` | NuGet.org |
| `.github/workflows/publish-efcore.yml` | `efcore-v*` | NuGet.org |
| `.github/workflows/release-cli.yml` | `cli-v*` | GitHub Releases |
| `.github/workflows/release-studio.yml` | `studio-v*` | GitHub Releases |

No workflow is triggered by a generic `v*` tag. Each workflow is strictly isolated to its own prefix.

---

## NuGet Release Process (`Sqlity.Ado` / `Sqlity.EFCore`)

### Required secret

`NUGET_API_KEY` — a NuGet.org API key with push permissions, stored as a repository secret.

### Steps

1. Update `<Version>` in the project's `.csproj` to the new version and commit:
   ```sh
   # Example: bumping Sqlity.Ado
   # Edit src/Sqlity.Ado/Sqlity.Ado.csproj: <Version>0.2.0</Version>
   git add src/Sqlity.Ado/Sqlity.Ado.csproj
   git commit -m "chore(ado): bump version to 0.2.0"
   git push
   ```

2. Create and push the release tag:
   ```sh
   git tag ado-v0.2.0
   git push origin ado-v0.2.0
   ```

3. GitHub Actions runs `publish-ado.yml`:
   - Runs scoped tests (Query, Storage, Ado)
   - Packs with `-p:Version=0.2.0`
   - Pushes `Sqlity.Ado.0.2.0.nupkg` to NuGet.org

### EFCore example

```sh
git tag efcore-v0.2.0
git push origin efcore-v0.2.0
```

---

## Application Release Process (`Sqlity.Cli` / `Sqlity.Studio`)

### Steps

1. Update `<Version>` in the project's `.csproj` and commit (optional but recommended):
   ```sh
   # Edit samples/Sqlity.Cli/Sqlity.Cli.csproj: <Version>0.2.0</Version>
   git add samples/Sqlity.Cli/Sqlity.Cli.csproj
   git commit -m "chore(cli): bump version to 0.2.0"
   git push
   ```

2. Create and push the release tag:
   ```sh
   git tag cli-v0.2.0
   git push origin cli-v0.2.0
   ```

3. GitHub Actions runs `release-cli.yml`:
   - Extracts version `0.2.0` from tag
   - Runs scoped tests (Query, Storage, Cli) on Ubuntu
   - Builds self-contained, single-file binaries for all platforms:
     - `sqlity-cli-0.2.0-linux-x64.tar.gz`
     - `sqlity-cli-0.2.0-win-x64.zip`
     - `sqlity-cli-0.2.0-osx-arm64.tar.gz`
     - `sqlity-cli-0.2.0-osx-x64.tar.gz`
   - Creates a GitHub Release tagged `cli-v0.2.0` with all archives attached

### Studio example

```sh
git tag studio-v0.1.0
git push origin studio-v0.1.0
```

GitHub Actions runs `release-studio.yml` and produces:
- `sqlity-studio-0.1.0-linux-x64.tar.gz`
- `sqlity-studio-0.1.0-win-x64.zip`
- `sqlity-studio-0.1.0-osx-arm64.tar.gz`
- `sqlity-studio-0.1.0-osx-x64.tar.gz`

---

## Platform Matrix

Both application release workflows build for the following targets:

| Runner | RID | Archive |
|---|---|---|
| `ubuntu-latest` | `linux-x64` | `.tar.gz` |
| `windows-latest` | `win-x64` | `.zip` |
| `macos-latest` (arm64) | `osx-arm64` | `.tar.gz` |
| `macos-13` (x64) | `osx-x64` | `.tar.gz` |

All binaries are **self-contained** — users do not need a .NET runtime installed. `Sqlity.Cli` is also published as a **single file** to simplify distribution. `Sqlity.Studio` is published as a directory (Avalonia requires its native rendering assets to remain accessible).

---

## Test Scoping Per Workflow

Each workflow runs only the tests relevant to the component being released. This ensures that a failing test in an unrelated component never blocks an independent release.

| Workflow | Tests run |
|---|---|
| `publish-ado.yml` | `Sqlity.Query.Tests`, `Sqlity.Storage.Tests`, `Sqlity.Ado.Tests` |
| `publish-efcore.yml` | `Sqlity.Query.Tests`, `Sqlity.Storage.Tests`, `Sqlity.Ado.Tests`, `Sqlity.EFCore.Tests` |
| `release-cli.yml` | `Sqlity.Query.Tests`, `Sqlity.Storage.Tests`, `Sqlity.Cli.Tests` |
| `release-studio.yml` | Build + executable verification per platform (no test project exists for Studio) |

Full cross-component testing is the responsibility of CI on pull requests / the main branch.

---

## Summary of All Release Commands

```sh
# Release Sqlity.Ado 0.2.0 to NuGet
git tag ado-v0.2.0
git push origin ado-v0.2.0

# Release Sqlity.EFCore 0.2.0 to NuGet
git tag efcore-v0.2.0
git push origin efcore-v0.2.0

# Release Sqlity.Cli 0.1.0 as standalone app
git tag cli-v0.1.0
git push origin cli-v0.1.0

# Release Sqlity.Studio 0.1.0 as standalone app
git tag studio-v0.1.0
git push origin studio-v0.1.0

# Pre-release example
git tag ado-v0.3.0-beta.1
git push origin ado-v0.3.0-beta.1
```
