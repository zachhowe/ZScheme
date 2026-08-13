# `install --from` a directory above `downloads/` copies into its own source

**Found by:** code review of the `version-selector` branch (the zsup toolchain
manager). Traced from source; no live repro was attempted.

**Affects:** `ArchiveExtractor.CopyDirectory`
(`src/ZScheme.Toolchain/ArchiveExtractor.cs:51-66`), reached by
`ToolchainInstaller.InstallFrom` (`src/ZScheme.Toolchain/ToolchainInstaller.cs:85-86`)
for `zsup install <name> --from <dir>`.

## Symptom

`zsup install dev --from ~/.zscheme` — or `--from ~`, or `--from /` — never
finishes. It writes until the disk fills or until a path grows past the OS limit,
at which point `InstallFrom`'s catch runs a **recursive delete** over the runaway
tree it just created. Nothing in the output says the source and the destination
overlap.

Passing the home is not a contrived invocation: `downloads/` is where zsup parks
release archives, `SweepTransients`' `keep:` exemption exists precisely because
users reinstall from paths inside the home, and "point it at my ZScheme directory"
is a natural mistake.

## Root cause

`InstallFrom` stages under the home, by design:

```csharp
var downloads = ZSchemeHome.GetDownloadsDir(_home);
...
var staging = Path.Combine(downloads, ".staging-" + Guid.NewGuid().ToString("N")[..12]);
...
if (Directory.Exists(source))
    ArchiveExtractor.CopyDirectory(source, staging);
```

So whenever `source` is the home or any ancestor of it, `destDir` is a
*descendant of `sourceDir`*. `CopyDirectory` then walks the source **lazily**:

```csharp
foreach (var dir in Directory.EnumerateDirectories(sourceDir, "*", SearchOption.AllDirectories))
    Directory.CreateDirectory(Path.Combine(destDir, Path.GetRelativePath(sourceDir, dir)));

foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
```

`EnumerateDirectories`/`EnumerateFiles` stream results as the walk proceeds, so
each directory this loop creates under `destDir` is itself a subdirectory of
`sourceDir` that the same enumerator has not visited yet. It descends into it,
creates a copy one level deeper, and descends into that. The recursion has no
fixed point.

`NormalizeLayout` in the same install already treats this hazard as known, and
takes the opposite approach for it (`ToolchainInstaller.cs:230-232`):

> Materialized before the loop: enumeration is lazy, and moving entries out of the
> directory being walked can silently skip entries on both NTFS and ext4.

A second, independent instance of the same call: `Directory.EnumerateFiles(path,
pattern, SearchOption.AllDirectories)` resolves to `EnumerationOptions` with
`AttributesToSkip = 0`, so the walk follows directory symlinks and junctions.
A dev tree whose `bin/runtimes` is a junction back to the repo root recurses the
same way, with no overlap between source and destination needed.

## Suggested fix direction

Two separate defects, both worth closing:

1. **Refuse an overlapping source.** The cheapest place is `InstallFrom`, before
   the copy — it is the only caller that has both paths and the only one that can
   name the problem usefully:

   ```csharp
   if (Directory.Exists(source) && IsAtOrAbove(source, downloads))
       throw new IOException(
           $"{source} contains zsup's own staging directory; install from a path outside {_home}"
       );
   ```

   `ZSchemeHome.IsBinDir`'s guarded `GetFullPath` comparison is the shape to copy
   for the path test (it already catches the `ArgumentException`/
   `PathTooLongException`/`NotSupportedException` triple).

2. **Materialize the enumeration** in `CopyDirectory` the way `NormalizeLayout`
   does, and skip reparse points, so a self-nesting or symlinked tree is bounded
   rather than infinite even when a future caller reaches it another way.

Worth a test in `ArchiveExtractorTests`: copy a directory into a subdirectory of
itself and assert it terminates; and an `InstallFrom` test asserting `--from` the
home is rejected with a message naming the overlap.
