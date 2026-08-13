# `zsup install` downloads the whole toolchain before rejecting a name that is already installed

**Found by:** code review of the `version-selector` branch (the zsup toolchain
manager).

**Affects:** `zsup install <version>` and `zsup install latest` without `--force`,
when that version is already installed or already linked. The `--from` path is
unaffected — it never downloads anything.

## Symptom

```
$ zsup install 0.4.0
downloading zscheme-0.4.0-win-x64.zip
   ... a hundred-odd megabytes later ...
error: toolchain '0.4.0' is already installed; pass --force to replace it
```

The rejection is decidable from the filesystem alone, before a byte moves. The
cost is a full archive download, a SHA-256 over it, and — on a metered or slow
link — a wait long enough that the user assumes the install is working.

`zsup install latest` makes this the routine case rather than the mistaken one:
it is the natural "am I up to date?" command, and answering "yes" costs a full
download every time.

## Root cause

`InstallFromReleaseAsync` (`src/ZScheme.Zsup/Commands/InstallCommand.cs:141-208`)
resolves the version, fetches `SHA256SUMS`, downloads the archive and verifies it,
and only then calls `InstallFrom`:

```csharp
Console.WriteLine($"downloading {assetName}");
var actual = await client.DownloadAssetAsync(release, assetName, archivePath);
// ... checksum ...
return new ToolchainInstaller(home).InstallFrom(archivePath, release.Version, force, actual);
```

`InstallFrom`'s first two acts (`src/ZScheme.Toolchain/ToolchainInstaller.cs:51-63`)
are the guards that reject it:

```csharp
var destDir = ZSchemeHome.GetToolchainDir(name, _home);
if (Directory.Exists(destDir) && !force)
    throw new IOException($"toolchain '{name}' is already installed; pass --force to replace it");

var linkFile = ZSchemeHome.GetToolchainLinkFile(name, _home);
if (File.Exists(linkFile) && !force)
    throw new IOException($"'{name}' is a linked toolchain; run `zsup unlink {name}` first, ...");
```

Neither guard reads the archive. They are in the right place — `InstallFrom` is
the one that must not be bypassed, and `--from` reaches it directly — but nothing
consults them earlier, so the network path pays for the whole download to learn
what a `Directory.Exists` would have answered.

The version has to be resolved first when the spec is `latest`, which is one
cheap API call; everything after that resolution is skippable.

## Suggested fix direction

1. **Check after resolving the version and before downloading.** In
   `InstallFromReleaseAsync`, once `release.Version` is known and validated, bail
   out when `!force` and either `ZSchemeHome.GetToolchainDir(release.Version, home)`
   exists or its `.link` file does. For `latest` this reads better than an error:
   `zsup install latest` finding the latest already installed could print
   `toolchain '0.4.0' is already installed` and exit 0.
2. **Keep the guards in `InstallFrom` exactly as they are.** The early check is an
   optimization and a better message, not the enforcement point — `--from`,
   the installer scripts and any future caller still have to hit the real one.
3. Factor the "is this name taken" test into one place — `ToolchainRegistry`
   already has `TryGet` — so the early check and `InstallFrom` cannot disagree
   about what counts as installed. Duplicating the two `Exists` calls is how they
   drift.

## Priority note

Cosmetic in the sense that nothing is lost or corrupted, and the user's next move
(`--force`) is spelled out for them. Worth fixing because the wasted work is
unbounded — it scales with the size of the release, and it lands on the one
invocation (`zsup install latest`) a user is most likely to run repeatedly.
