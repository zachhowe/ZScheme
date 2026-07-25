# Four LSP tests still build URI expectations with System.Uri

**Found by:** fixing the eight drive-letter-casing failures in `aa457e2`. These four were left
converted-by-omission: they pass today, so changing them was out of scope for a fix aimed at
failing tests.

**Affects:**

- `tests/ZScheme.LanguageServer.Tests/AnalysisBudgetTests.cs:112`
- `tests/ZScheme.LanguageServer.Tests/AnalysisServiceTests.cs:136`, `:169`, `:197`, `:222`

and `StdioLspClient.Initialize`
(`tests/ZScheme.LanguageServer.Tests/TestFixtures/StdioLspClient.cs:68`), which builds `rootUri`
the same way.

## Symptom

None today. Each of these is `new Uri(path).AbsoluteUri` used purely as an **input** — the URI is
handed to `AnalyzeImmediate` or sent as `rootUri`, and the server normalises internally, so the
casing difference never surfaces. The trap is that the moment one of them compares its URI
against something the server *emitted*, it fails on Windows only, in the way eight tests already
did.

## Root cause

`DocumentUri` (OmniSharp's port of vscode-uri) lower-cases the Windows drive letter in both
`GetFileSystemPath()` and `ToString()`; `System.Uri` preserves it:

```
DocumentUri.FromFileSystemPath(@"C:\...\lib.zs").GetFileSystemPath()  -> c:\...\lib.zs
new Uri(@"C:\...\lib.zs").AbsoluteUri                                -> file:///C:/...
```

`aa457e2` routed the fixtures (`TempPackageWorkspace.PathOf`/`UriOf`, `LspTestSession`) through a
new `LspUri` helper
(`tests/ZScheme.LanguageServer.Tests/TestFixtures/LspUri.cs`) that spells things the way the
server does. These four sites predate that and were not converted.

Worth being explicit about why this went unnoticed for so long: the eight tests that *did* compare
against server output had been failing since the day they were written and could only ever have
passed on Linux or macOS. There is no CI in this repo (`.github/workflows` does not exist), so
"passes on the author's machine" was the only gate.

## Suggested fix direction

Mechanical: replace `new Uri(path).AbsoluteUri` with `LspUri.Of(path)` at the five sites above.
The helper already exists and documents the reasoning, so this is a rename-level change with no
behavioural risk — the server normalises these inputs either way.

Optionally add an analyzer/grep guard (or just a note in the fixture) so `new Uri(` in this test
project is a deliberate choice rather than a default.

## Priority note

Low — pure latent-hazard cleanup, no failing test. Bundle it with the next change that touches
these files rather than as its own commit.
