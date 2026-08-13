# `publish.ps1` stages the package cache inside `dist/`, where a failed cleanup becomes release assets

**Found by:** code review of the `version-selector` branch (the zsup toolchain
manager). Traced from source; no live repro was attempted, and the CI default
described below currently masks the worst outcome.

**Affects:** `publish.ps1`, and through it the `toolchain` job in
`.github/workflows/release.yml`.

## Symptom

If the scratch cleanup does not succeed, `dist/` finishes the run containing
`.pkgcache-build/` alongside the archives. Nothing says so — the cleanup is
silenced — and the script goes on to print its normal summary and exit 0.

Where that goes from there depends on one `actions/upload-artifact` default. The
release job flattens everything it is handed:

```yaml
- run: |
    mkdir -p dist
    find staging -type f -exec mv {} dist/ \;
- name: Generate checksums
  working-directory: dist
  run: sha256sum * > SHA256SUMS
```

`find -type f` descends into subdirectories and `mv` flattens them, so every file
of the package cache would become a top-level release asset — with same-named
files from different packages silently overwriting one another — and the
`SHA256SUMS` generated immediately afterwards would cover them all, making the
result look exactly like a normal release.

## Root cause

The scratch directory lives inside the output directory
(`publish.ps1:61`):

```powershell
$pkgCacheScratch = Join-Path $OutputDir ".pkgcache-build"
```

and its removal is best-effort and silent (`publish.ps1:155`):

```powershell
Remove-Item -Recurse -Force $pkgCacheScratch -ErrorAction SilentlyContinue
```

`$ErrorActionPreference = 'Stop'` governs the rest of the script, so this is the
one step that can fail without stopping anything. Nothing downstream re-checks:
the checksum block reads `Get-ChildItem $OutputDir -File`, which does not recurse
and so neither hashes the cache nor notices it, and the workflow uploads
`path: dist/*` wholesale.

## What currently stops it

`actions/upload-artifact@v4` excludes hidden files unless `include-hidden-files`
is set, and `.pkgcache-build` is dot-prefixed — so on CI today the leak is most
likely blocked by that default rather than by anything in this repo. That is a
thin guarantee to rest on: it is a third-party default, it is invisible from
here, and it protects only because someone happened to pick a dotted name for the
scratch directory. A local `pwsh ./publish.ps1` whose `dist/` is published by any
other route has nothing standing in the way at all.

## Suggested fix direction

1. **Stage outside `$OutputDir`.** The output directory should hold release
   artifacts and nothing else, so that "everything in `dist/` ships" stays true by
   construction rather than by a cleanup succeeding. A sibling scratch path, or
   one under the repo's build output, costs nothing here — `$pkgCacheSource` is
   copied into each per-rid staging tree anyway, so the scratch never needs to be
   on the same volume as the archives.
2. **Fail loudly if the cleanup does not succeed**, wherever it ends up living. A
   package cache that could not be removed means something is holding it, which
   is worth knowing during a release rather than never.
3. Consider tightening `path: dist/*` to the asset shapes actually released
   (`dist/*.zip`, `dist/*.tar.gz`) in `release.yml`. That makes the upload
   explicit about what a release asset is, instead of inheriting it from whatever
   the publish script left behind.

## Priority note

Low, and currently masked by an upload-artifact default rather than by this
repo's own code. Worth fixing because of what it costs when the mask slips: the
failure produces a *published, checksummed release* containing files that were
never meant to ship, with no signal anywhere in the run that anything went wrong.
Step 1 is a one-line move.
