# The stale-file prune forces hydration of cloud-file placeholders

**Found by:** code review of the `cs-output-fix` branch (finding on
`CSharpProjectGenerator.PruneGeneratedCsFiles`, at commit `a04d4a2`, "Keep a stale file link as
a prune candidate"). **Unconfirmed** — the reviewer reasoned it out from the enumeration
options and the Windows cloud-files contract; nobody has reproduced it. It needs a Windows
machine with OneDrive Files-On-Demand (or any other cloud-files provider) to verify.

**Affects:** `zs generate-project` only — the one caller that prunes — when its output
directory sits under a cloud-files sync root and a stale generated `.cs` has been dehydrated
("Free up space"). `zs compile --emit-project` does not prune and is not affected.

**Workaround:** hydrate every `.cs` under the output directory (right-click, "Always keep on
this device"), or keep generated trees out of the sync root.

## Symptom

Expected, on a dehydrated placeholder with the sync client paused or the network off:

```
generate-project: <IOException message from the cloud-files provider naming the placeholder>
```

with exit code 1 and no project written, on every run until every `.cs` under the output
directory is hydrated. Before `a04d4a2` the prune skipped these files entirely.

## Root cause

[`CSharpProjectGenerator.cs`](../src/ZScheme.Compiler/Codegen/CSharpProjectGenerator.cs),
`PruneGeneratedCsFiles`. The candidate enumeration sets `AttributesToSkip = 0` so that a
generated `.cs` that is itself a symlink is still a prune candidate. `EnumerationOptions`
defaults `AttributesToSkip` to `Hidden | System`, and until `a04d4a2` the prune set it to
`ReparsePoint`, which excluded every reparse point — symlinks, but also cloud-file
placeholders, which Windows implements as reparse points (`IO_REPARSE_TAG_CLOUD*`).

With reparse points no longer skipped, `IsGeneratedFile` opens each candidate with a
`StreamReader` to read its first line. Opening a dehydrated placeholder asks the cloud-files
filter to hydrate it. When the provider cannot (paused, offline, quota), `CreateFile` fails
with a cloud-files HRESULT (`ERROR_CLOUD_FILE_*`, 0x8007xxxx range around 0x0166-0x018F),
which surfaces as an `IOException`. `IsGeneratedFile` catches only `FileNotFoundException`
and `DirectoryNotFoundException`, so it propagates to the `TryWriteOutput` guard in
`GenerateProjectCommand`, which reports it and fails the run.

The comment on the enumeration names "a file that is itself a link" as the intended target
of `AttributesToSkip = 0`; cloud placeholders come along for free and nothing handles the
hydration-failure case.

## Suggested fix direction

Two options, both narrow:

- Skip entries carrying `FileAttributes.Offline`, `RECALL_ON_DATA_ACCESS` (0x400000) or
  `RECALL_ON_OPEN` (0x40000) in `ShouldIncludePredicate`. A dehydrated placeholder is not
  something this run wrote, so leaving it alone is consistent with how a dangling link is
  treated. The last two are not members of `FileAttributes`; they would need to be named
  constants with a comment citing `winnt.h`.
- Or catch the cloud-files HRESULTs in `IsGeneratedFile` and return `false`, treating an
  unhydratable placeholder like a dangling link: no first line to check, so not ours to
  delete.

Either way, add a Windows-only test that can be skipped when no cloud-files provider is
present, and reproduce first: the whole report is inference until then.

## Priority note

Low. It needs a specific hosting arrangement (generated output inside a OneDrive folder with
Files-On-Demand dehydrating stale files) and a provider that cannot hydrate on demand, and it
fails loudly with a message naming the file rather than miscompiling anything. Worth fixing
because the old code handled the case by accident and the new code does not, but not before
it has been seen.
