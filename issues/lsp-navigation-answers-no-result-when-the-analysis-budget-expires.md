# A document whose analysis exceeds its budget publishes diagnostics but answers every navigation request with "no result"

**Found by:** the full test suite during release verification for 0.3, on the
working tree that became `383178c` (post-`0.3`-tag version bump). Not caused by
that bump — version strings cannot reach this path, and the same suite was green
on `ed07769` twenty minutes earlier. It is load-dependent; see "Not reproduced since".

**Affects:** 2 intermittent test failures —
`ZScheme.LanguageServer.Tests.StdioServerTests.DidOpen_PublishesDiagnostics_AndDefinitionAnswersWithinDeadline`
and `…StdioServerTests.DidOpen_ThatRacesTheHandshake_IsStillAnalysed`. Both are
stdio round-trip tests against a real `zs-lsp` subprocess; both failed in the
same run, at the same assertion, for the same reason.

**Not reproduced since.** One targeted run of `StdioServerTests` (4/4 passed) and
three consecutive full-solution `dotnet test` runs (0 failures each) after the
observation. Everything below the "Symptom" section is traced from source, not
from a live repro.

Repro (does not currently fail — see "How to force it"):

```
dotnet test tests/ZScheme.LanguageServer.Tests --filter "FullyQualifiedName~StdioServerTests"
```

## Symptom

```
ZScheme.LanguageServer.Tests.StdioServerTests.DidOpen_ThatRacesTheHandshake_IsStillAnalysed [FAIL]
  System.InvalidOperationException : Cannot access child value on Newtonsoft.Json.Linq.JValue.
     at Newtonsoft.Json.Linq.JToken.get_Item(Object key)
     at …StdioServerTests.DidOpen_ThatRacesTheHandshake_IsStillAnalysed() in
        tests/ZScheme.LanguageServer.Tests/StdioServerTests.cs:line 125
```

The exception is the *shape* of the failure, not the failure itself. Both tests
do this (`StdioServerTests.cs:86-87`, `:124-125`):

```csharp
Assert.NotNull(result);
Assert.Equal(1, (int)result["range"]!["start"]!["line"]!);
```

`PositionRequest` returns `AwaitResponse(id, timeout)["result"]`
(`TestFixtures/StdioLspClient.cs:127`). When the server answers
`textDocument/definition` with `"result": null`, that is a `JValue` holding null,
which is **not** a C# null — so `Assert.NotNull` passes and the *next* line throws
on indexing. The real observed behaviour is: **go-to-definition answered "no
result"**, which is precisely the regression these two tests exist to catch
(`StdioServerTests.cs:113-114`: "a dropped didOpen makes every navigation request
answer 'no result'").

Note what had already succeeded by that point: `AwaitDiagnostics` returned a
non-null payload on both tests (`:73-74`, `:110-111`). So the server *did* publish
diagnostics for the document and then failed to navigate within it. Diagnostics
working while navigation does not is the discriminating detail.

## Root cause

A `DocumentState` can carry **diagnostics but a null `Ast`**, and every navigation
handler degrades to "no result" on exactly that state — silently, because from the
client's side it is indistinguishable from "this name has no definition".

`DefinitionHandler.Handle` (`src/ZScheme.LanguageServer/Handlers/DefinitionHandler.cs:33-42`)
fetches the document and hands it to `ResolveDefinition`, which has no AST to walk
and returns null, which becomes a null LSP result. It never distinguishes "the
cursor is not on a name" from "this document was never successfully analysed".

`AnalysisService.AnalyzeGuarded` (`src/ZScheme.LanguageServer/Analysis/AnalysisService.cs:185-262`)
produces such a state on three of its four exits:

```csharp
var analysis = Task.Run(() => RunAnalysis(uri, source, version));   // :196
finished = analysis.Wait(AnalysisBudget);                           // :205, 20s (:20)
```

- **Budget expiry** (`:235-260`) — logs `"Analysis of {Uri} exceeded {Budget}s"`,
  stores `Failed(...)`, and returns it.
- **`RunAnalysis` threw** (`:208-216`) — stores `Failed(...)`.
- **Analysis completed unsuccessfully** (`:230-232`) — stores `Failed(...)`.

And `Failed` (`:276-290`) is:

```csharp
var diagnostics = new DiagnosticBag();
diagnostics.Error(message, new SourceSpan(UriToFilePath(uri), 1, 1, 1));
return new DocumentState(uri, version, source, null, diagnostics, …);
                                            //   ^^^^ Ast
```

One diagnostic, null AST. The didOpen handler publishes that single diagnostic —
satisfying `AwaitDiagnostics` — and every subsequent definition/hover/reference
request against the document answers null. That is the observed pair of facts
exactly.

**Which of the three exits fired is not established.** The tests assert only that
diagnostics are non-null and discard their contents, so the run's evidence cannot
distinguish "exceeded 20s" from "analysis threw". The budget-expiry path is the
better hypothesis because it is the load-sensitive one and the failure appeared
only under a fully parallel suite, but it is a hypothesis.

If it is budget expiry, the likely mechanism is **thread-pool starvation of the
LSP's own process**: `AnalyzeGuarded` blocks a pool thread on `analysis.Wait(...)`
while the work it waits for was queued to that same pool via `Task.Run`. The
`VSTHRD002` suppression at `:201-206` documents the blocking as deliberate
("blocking is the point"), and it is bounded, but bounded-and-blocking still means
the queued analysis has to win a thread from a pool the wait is occupying. Under
the CPU contention of a full parallel suite the pool grows only at the hill-climbing
rate, so a 4-line file can plausibly burn 20s without the compile ever starting.
That would also explain why a targeted run — same code, idle machine — passes
every time.

## How to force it

To confirm the mechanism before fixing it, either:

- Drop `AnalysisBudget` (`AnalysisService.cs:20`) to something like 50ms and run
  the two tests. If they fail with this exact exception, the null-AST-plus-
  diagnostics path is confirmed as the failure shape.
- Or make the tests say what actually happened: assert on the published
  diagnostics' contents rather than just non-nullness. The `Failed` messages are
  distinctive (`"taking longer than 20s"` vs `"ZScheme analysis failed: …"`), so
  one assertion turns this from an unexplained `JValue` exception into a named
  cause the next time it fires.

## Suggested fix direction

Three separable pieces, in increasing order of scope:

1. **Make the tests fail legibly.** `PositionRequest` should distinguish a JSON
   null result from a missing one, and the assertion should report the published
   diagnostics when navigation answers null. This costs nothing and is worth doing
   regardless of the rest — the current failure text names neither the cause nor
   even the subsystem.
2. **Stop navigation from silently conflating the two cases.** A `DocumentState`
   with a null `Ast` is not "no definition here", it is "ask again later". The
   handlers could answer with the last-good state, or the service could keep the
   previous successful AST alongside the failure diagnostic rather than replacing
   it — `AnalyzeGuarded` already keeps a `previous` state at `:187-193` for the
   placeholder, so the material is at hand.
3. **Remove the block-on-own-pool pattern** if the starvation hypothesis holds.
   Making the didOpen path async to the top would let the analysis run without a
   pool thread parked on it. That is the real fix and the largest change; the
   comment at `:169-184` shows the current design is a deliberate compromise, so
   this needs its own decision rather than a drive-by change.

## Priority note

Ranks below a miscompilation but above ordinary polish. It is a **user-visible
correctness bug in the editor experience**, not merely a flaky test: any user on a
loaded machine whose first compile of a file exceeds 20s gets a file that shows
diagnostics but where go-to-definition, hover and find-references all silently
answer nothing, with no indication that the document is in a degraded state. The
recovery path exists (`:242-254` adopts the late result whenever it lands), so the
window closes on its own — which is also why this is easy to dismiss as flakiness.

The same "silent degradation" family as the two bugs already fixed this cycle
(`952d009`'s scripted debounce and `a39d858`'s canceled-analysis unregistration),
and it is worth checking whether the fix for item 2 above subsumes the placeholder
path at `:187-194`, which returns an empty-diagnostic null-AST state by the same
mechanism.
