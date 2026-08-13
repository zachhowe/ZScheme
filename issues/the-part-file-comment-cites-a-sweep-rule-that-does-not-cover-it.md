# The `.part` naming comment cites a sweep rule that never sees the file

**Found by:** code review of the `version-selector` branch (the zsup toolchain
manager). Traced from source; no live repro was attempted.

**Affects:** the comment at
`src/ZScheme.Toolchain/GitHubReleaseClient.cs:211-217`, against
`ToolchainInstaller.SweepTransients`
(`src/ZScheme.Toolchain/ToolchainInstaller.cs:441-483`).

## Symptom

No runtime symptom. This is a comment stating an invariant that does not hold,
which makes it a trap for the next change: it justifies a naming decision by a
mechanism that is not actually what reclaims the file, so anyone reasoning from it
will draw the wrong conclusion about where a `.part` may live.

## Root cause

The comment reads:

```csharp
// ... The guid goes before the extension rather than after:
// ToolchainInstaller.SweepTransients matches a trailing ".part", so a ".part-<guid>" slot
// would silently stop being swept and leave a toolchain-sized file behind after every killed
// run.
var partPath = $"{destPath}.{Guid.NewGuid().ToString("N")[..8]}.part";
```

The trailing-`.part` rule it names is real (`ToolchainInstaller.cs:471-482`), but
it only scans files sitting **directly in** `downloads/`:

```csharp
foreach (var file in Directory.EnumerateFiles(downloads))
```

`Directory.EnumerateFiles` without `SearchOption.AllDirectories` does not recurse.
And `destPath` is never directly in `downloads/` — `InstallCommand` builds it
inside a per-download slot (`InstallCommand.cs:174-175`):

```csharp
var slot = ToolchainInstaller.CreateDownloadSlot(downloads);
var archivePath = Path.Combine(slot, assetName);
```

`CreateDownloadSlot` returns `downloads/.dl-<guid>` (`ToolchainInstaller.cs:407-413`).
So the `.part` lives one level down, and what actually reclaims it is the
*directory* rule at `:449-465`, matching the `.dl-` prefix and deleting the whole
slot recursively. The file rule never sees it.

The comment was accurate when it was written and was invalidated afterwards:
`1b888415` ("Stop two downloads of one asset sharing a fixed staging file") added
both the guid and this justification, back when `destPath` really was
`downloads/<asset name>`. `6ae42821` ("Stop two downloads of one version sharing
an archive path") then moved the download into a `.dl-<guid>` slot and left the
comment behind.

## Suggested fix direction

Correct the comment to name the rule that applies. The guid-before-extension
choice is still worth keeping and still worth explaining — a `.part-<guid>` file
would not match the trailing-`.part` rule *if* one ever landed directly in
`downloads/`, and `DownloadAssetAsync` is public API that does not require its
`destPath` to be inside a slot:

```csharp
// ... The guid goes before the extension rather than after so the name still ends in ".part":
// a destPath directly in downloads/ is then reclaimed by SweepTransients' trailing-".part"
// file rule. Through zsup's own callers the file lands inside a .dl-<guid> slot instead, and
// the slot-directory rule is what sweeps it -- the file rule does not recurse.
```

Nothing else needs to change. Worth checking at the same time whether
`SweepTransients` should recurse into slots at all: today a slot whose deletion
failed takes its contents with it on the next sweep, which is the right outcome,
so probably not.
