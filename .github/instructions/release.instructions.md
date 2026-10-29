---
description: 'Building, packaging and publishing releases: the CreateZip target, the release workflow, versions and changelog coupling. Trigger phrases: build, publish, release, workflow, zip, version bump, tag.'
applyTo: '.github/workflows/*.yml'
---

# Build & Release

> Part of the Ranks instruction set. `§N` cross-references resolve through the table in [`copilot-instructions.md`](../copilot-instructions.md), which is always loaded; `docs/` references are to the separate requirements and implementation guides.

### Build

```powershell
dotnet build Ranks/Ranks.csproj -c Release
dotnet publish Ranks/Ranks.csproj -c Release
```

`Ranks.csproj` emits `build/publish/Ranks/` and zips it to
`build/Ranks-v<version>.zip` via the `CreateZip` target, which reads
`PluginMetadata.Version` out of `Ranks/src/Entrypoint.cs` with the `ExtractPluginVersion`
inline task. The version lives in exactly one place; nothing else needs stamping.

### Cutting a release

`.github/workflows/release.yml` runs on `workflow_dispatch`, or on a push to `main`
whose commit message contains `[release]`. It reads the same
`PluginMetadata.Version`, takes the matching `## [x.y.z]` section of the component's
`CHANGELOG.md` as the release body, and uploads the zip.

**One run publishes every component that has something to publish.** The `target`
input defaults to `all`: core and the four modules each get their own tag, notes and
release, from a single build. Core and the modules version independently, so under
`all` a component already published at its current version is **skipped with a
notice** rather than failing the run — otherwise bumping one module would be blocked
by the four that did not change. Naming a single component instead keeps the old
behaviour and still **errors** on an existing tag, which is what makes an accidental
re-release loud. A run where everything is already published is an error, not a
silent success.

Every component is validated before anything is built, so a version mismatch in one
cannot leave the others already released. A core release additionally attaches the
module zips built from the same commit, so an operator never has to work out which
module zip goes with which core version.

So a release needs **both** a version bump and a matching changelog section — a
missing section fails the run by design. Style rules live in
`.github/instructions/changelog.instructions.md`.

`docs/verify-release-workflow.sh` runs that logic against the real repo with `gh`
stubbed (`docker run --rm -v "${PWD}:/src:ro" debian:bookworm-slim`), which is the
only way to exercise it without publishing something.

### Install layout

```
addons/swiftlys2/plugins/Ranks/
  Ranks.dll
  resources/
    translations/
    exports/Ranks.Contract.dll
```

Configs are generated on first load into the plugin's config directory.

### Verifying the contract actually ships

The export is gitignored and build-generated, so the failure is CI-only and
silent. Simulate a clean checkout — deleting build state is not enough, the
gitignored artifact must go too:

```powershell
Remove-Item 'Ranks/resources/exports','Ranks/build','Ranks/obj' -Recurse -Force -ErrorAction SilentlyContinue
dotnet publish Ranks/Ranks.csproj -c Release
Test-Path 'Ranks/build/publish/Ranks/resources/exports/Ranks.Contract.dll'
```

Always run the control too — revert the packaging fix and confirm it **fails** —
otherwise you cannot tell the fix from a warm-cache effect.

### Release checklist

- [ ] Version bumped in `[PluginMetadata]` **and** `.csproj`
- [ ] `Ranks.Contract.dll` present in the publish output
- [ ] `en.jsonc` complete; contributed translations key-complete or omitted
- [ ] Migrations verified on MySQL, PostgreSQL and SQLite — fresh and existing
- [ ] Breaking contract change → key bumped to `.v2`, `.v1` still registered
- [ ] `CHANGELOG.md` has a `## [x.y.z]` section matching the new version

---

