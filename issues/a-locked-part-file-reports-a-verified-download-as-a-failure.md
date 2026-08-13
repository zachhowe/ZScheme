# A locked `.part` file reports a verified download as a failure

**Found by:** code review of the `version-selector` branch (the zsup toolchain
manager). Traced from source; no live repro was attempted.

**Affects:** `GitHubReleaseClient.DownloadAssetAsync`
(`src/ZScheme.Toolchain/GitHubReleaseClient.cs:281-283`), reached by
`zsup install <version>` and `zsup self update`.

## Symptom

On Windows, where a scanner routinely opens a file the instant it is closed:

```
$ zsup install 0.4.0
downloading zscheme-0.4.0-win-x64.zip
error: The process cannot access the file '...\.dl-<guid>\zscheme-0.4.0-win-x64.zip.<guid>.part'
       because it is being used by another process.
```

Every byte arrived and the SHA-256 was computed successfully — the archive is
good. The `.part` file is left behind inside the download slot, and the install
fails for a reason that has nothing to do with the release, the network, or the
checksum.

## Root cause

The cleanup `try` ends before the rename:

```csharp
    }
    catch
    {
        // A .part file is never resumed, so leaving one behind just accumulates dead weight in
        // downloads/. ...
        try { File.Delete(partPath); } catch (...) { }
        throw;
    }

    var digest = Checksums.ComputeSha256(partPath);
    File.Move(partPath, destPath, overwrite: true);
    return digest;
```

`File.Move` is outside it, so an `IOException` there — a sharing violation on the
source or on an existing `destPath` — propagates with no cleanup at all.
`ComputeSha256` sits in the same unprotected region.

The `.part` is orphaned rather than deleted. It is reclaimed eventually, but only
by `SweepTransients`' slot-directory rule and only after six hours — see
[[the-part-file-comment-cites-a-sweep-rule-that-does-not-cover-it]], which
documents that the file rule the comment claims covers it does not.

`InstallCommand.Run` catches the `IOException` and prints
`error: <message>` (`InstallCommand.cs:69-90`), so the user sees a bare file-lock
message rather than anything about the download.

## Suggested fix direction

Extend the protected region to the end, so every failure path cleans up
identically:

```csharp
string digest;
try
{
    ... the download loop ...

    digest = Checksums.ComputeSha256(partPath);
    File.Move(partPath, destPath, overwrite: true);
}
catch
{
    try { File.Delete(partPath); } catch (...) { }
    throw;
}

return digest;
```

Worth considering a bounded retry around the move specifically: a scanner's hold
is measured in milliseconds, and `ShimInstaller`/`ToolchainRegistry` both already
treat a Windows lock on a freshly written file as an expected condition rather
than an error. That would turn the most common instance of this from a failed
install into no event at all.

Worth a test in the release-client tests: make `destPath` unwritable (or hold the
`.part` open) and assert no `.part` survives the failure.
