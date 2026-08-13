# The release workflow's `version` input is declared but never read

**Found by:** code review of the `version-selector` branch (the zsup toolchain
manager).

**Affects:** `.github/workflows/release.yml:6-9`.

## Symptom

A maintainer dispatches the release workflow with `version: 0.5.0` and gets a
build of whatever `Directory.Build.props` currently says — silently. The input is
offered in the GitHub UI, described as if it does something, and is dropped.

## Root cause

The input is declared:

```yaml
workflow_dispatch:
  inputs:
    version:
      description: 'Version to build (defaults to Directory.Build.props)'
      required: false
```

and never referenced. The only `inputs.` use anywhere in the file is
`inputs.dry_run` at `:120`. `verify-version` reads the version straight out of
`Directory.Build.props` and compares it against the tag when there is one:

```powershell
[xml]$props = Get-Content Directory.Build.props
$version = ($props.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ }).Trim()
```

The description ("defaults to Directory.Build.props") promises an override that
does not exist, which makes the input worse than absent: it reads as a supported
way to build a one-off version.

## Suggested fix direction

Two coherent options; the second matches how this repo versions things.

1. **Wire it up.** Have `verify-version` fail when `inputs.version` is set and
   disagrees with `Directory.Build.props`, turning the input into an assertion
   rather than an override. That keeps the "compiler and every package share one
   version number" invariant the job exists to enforce.
2. **Delete it.** The version is a property of the tree, bumped by
   `bump-version.ps1` at the start of a cycle, and a dispatch build has no
   business overriding it. This is the smaller change and the honest one.

Either way `dry_run` stays as-is; it is wired and works.

## Priority note

Low, and it cannot corrupt a release — `verify-version` still gates a tagged build
against `Directory.Build.props`, so a wrong expectation surfaces as "you got the
version in the tree" rather than a mis-tagged artifact. It is a trap for whoever
next reaches for it under time pressure.
