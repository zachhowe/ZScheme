# WorkspaceExclusionTests fails outright if the scan drops a single file

**Found by:** the nine-failure language-server run. This test failed only in the **full** suite
and passed in isolation (6/6, 197 ms vs 9 s under load). Its trigger was removed in `aa457e2`,
and it has since been stable across three consecutive full runs — but the fragility that let load
break it is unchanged.

**Affects:** `WorkspaceExclusionTests.NestedGitIgnore_AnchorsToItsOwnDirectory`
(`tests/ZScheme.LanguageServer.Tests/WorkspaceExclusionTests.cs:68`).

## Symptom

```
Assert.True() Failure
Expected: True
Actual:   False
```

on `Assert.True(ws.Service.Index.Contains(kept))` for `<tempRoot>\tools\grammars\sample.zs`.

Note the shape of the failure: every *negative* assertion in this class passes trivially when
nothing at all is indexed, so a scan that silently drops files looks like a targeted
exclusion-logic bug. That is what made it initially look like a defect in `GitIgnoreRules`.

## Root cause

**Not the exclusion logic.** That was checked directly against the built code and is correct:
`editor/zed/grammars` is excluded, `tools/grammars` is not; `GitIgnoreRules.Parse(["/grammars"])`
anchors to its own directory; the temp root has no ancestor `.git` or `.gitignore`; the walk does
not truncate; and compiling/indexing that exact file shape works, including 8 concurrent
in-process runs.

The trigger was contention. `StdioServerTests` was spinning a real `zs-lsp` that scanned the repo
for its full 90-second deadline (a URI-comparison bug, fixed in `aa457e2`), and under that load
`ScanWorkspace` dropped the file. `ScanWorkspace`
(`src/ZScheme.LanguageServer/Analysis/AnalysisService.cs`) has three failure paths that skip a
file, and a transient sharing lock on `File.ReadAllText` of a just-written temp file lands in the
second one. Those three now log at debug (`aa457e2`), so the next occurrence is diagnosable — but
they still swallow, by design, and the test still asserts on the end state rather than on what the
scan actually did.

So the test is **load-sensitive by construction**: it asserts a file was indexed, with no way to
distinguish "wrongly excluded" (the thing under test) from "enumerated but transiently unreadable"
(unrelated).

## Suggested fix direction

Make the test assert on the scan's own report rather than only on the index. The fixture already
hands it a `RecordingReporter`, so:

- assert the file appears in `reporter.ReportedFiles` — that is the actual claim, "the exclusion
  rules did not filter it out";
- keep the `Index.Contains` assertion, but only as a secondary check, or drop it in favour of the
  reporter so a transient read failure can no longer masquerade as an exclusion bug.

Do **not** change `WorkspaceExclusions.cs` or `GitIgnoreRules.cs` — both were verified correct.

## Priority note

Low, and currently green. Recorded because the diagnosis cost real time: an assertion that reads
as "is the gitignore anchored correctly?" actually fails for any reason a file misses the index,
and the other tests in the class cannot distinguish the two either. Worth fixing the next time
anyone touches this file.
