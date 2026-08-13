# `win-arm64` is published by every release but never AOT smoke-tested

**Found by:** code review of the `version-selector` branch (the zsup toolchain
manager).

**Affects:** `.github/workflows/ci.yml:31-42` (the `aot-smoke` job).

## Symptom

A Native AOT break specific to `win-arm64` passes CI and first appears during a
tagged release, after `verify-version` has already gated and the other five RIDs
have built — which is exactly when a failure is most expensive to act on.

## Root cause

`release.yml:76-82` publishes six RIDs:

```yaml
- { os: ubuntu-latest,    rid: linux-x64 }
- { os: ubuntu-24.04-arm, rid: linux-arm64 }
- { os: macos-13,         rid: osx-x64 }
- { os: macos-latest,     rid: osx-arm64 }
- { os: windows-latest,   rid: win-x64 }
- { os: windows-11-arm,   rid: win-arm64 }
```

`ci.yml`'s `aot-smoke` matrix lists the first five and stops:

```yaml
- { os: ubuntu-latest,   rid: linux-x64 }
- { os: ubuntu-24.04-arm, rid: linux-arm64 }
- { os: macos-13,        rid: osx-x64 }
- { os: macos-latest,    rid: osx-arm64 }
- { os: windows-latest,  rid: win-x64 }
```

The job's own comment states the intent it falls one row short of:

> AOT is the one thing that cannot be validated by a normal build, and it is only
> exercised at release time otherwise -- which is exactly when a failure is most
> expensive.

`win-arm64` is a first-class target elsewhere: `install.ps1` selects it from
`RuntimeInformation.OSArchitecture`, and `GitHubReleaseClient` builds asset names
for it. It is the only published RID with no pre-release AOT coverage.

## Suggested fix direction

Add the row, matching the runner `release.yml` already uses:

```yaml
- { os: windows-11-arm,  rid: win-arm64 }
```

`fail-fast: false` is already set, so it cannot mask the other legs. The one
consideration is runner cost/availability for `windows-11-arm` on every push — if
that is unwanted, the alternative is to run the ARM legs only on `main` or on a
schedule, but leaving the RID with zero coverage should not be the outcome.

## Priority note

Low, and it is a coverage gap rather than a defect — nothing is known to be broken
on `win-arm64` today. It is a one-line change to a job whose entire purpose is to
move this class of failure off the release path.
