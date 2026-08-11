# `AnalyzeAsync_SecondCallCancelsFirst` is timing-dependent and fails under load

## Symptom

`ZScheme.LanguageServer.Tests.AnalysisServiceTests.AnalyzeAsync_SecondCallCancelsFirst`
fails during a full `run-all-tests.ps1`, and passes when run on its own:

```
[xUnit.net] AnalysisServiceTests.AnalyzeAsync_SecondCallCancelsFirst [FAIL]
  Assert.Null() Failure: Value is not null
  Expected: null
  Actual:   Program { Span = ...AnalyzeAsync_SecondCallCancelsFirst.zs(1:1),
                      ResolvedType = Unit, TopLevelForms = ... }
  at AnalysisServiceTests.cs:line 261
Failed!  - Failed: 1, Passed: 339, ... ZScheme.LanguageServer.Tests.dll
```

```
$ dotnet test tests/ZScheme.LanguageServer.Tests --no-build \
    --filter "FullyQualifiedName~AnalyzeAsync_SecondCallCancelsFirst"
Passed!  - Failed: 0, Passed: 1
```

## Root cause

The test asserts on wall-clock racing (`AnalysisServiceTests.cs:239-266`):

```csharp
var first = svc.AnalyzeAsync(uri, srcA, 1);
await Task.Delay(50);
var second = svc.AnalyzeAsync(uri, srcB, 2);
...
Assert.Null(firstResult.Ast);   // line 261 — assumes `first` never left its debounce
```

It fires two analyses 50 ms apart and requires the first to still be inside its 300 ms
debounce window when the second arrives. That holds on an idle machine. In a full run —
all test assemblies in parallel, 16 workers, coverage collectors attached — the 50 ms
`Task.Delay` can overshoot 300 ms, the first analysis completes normally, and
`firstResult.Ast` is non-null. The failure is in the test's timing assumption, not in
`AnalysisService`: nothing about the cancellation behaviour is actually wrong when it
fails.

## Reproduce

Not reliably on demand — it is load-dependent. It reproduced once in a full
`pwsh ./run-all-tests.ps1`. To force it, shrink the margin (drop the debounce, or raise
the `Task.Delay` above it) and watch the same assertion fail deterministically.

## Suggested fix

Make the ordering deterministic rather than probabilistic. Either expose the debounce
interval so the test can set it large enough that a scheduling hiccup cannot cross it, or
give `AnalysisService` a test seam (an injectable delay/clock, or a signal the test can
await) so "the second call arrives while the first is still debouncing" is established by
construction instead of by `Task.Delay`.

## Priority note

Lowest severity of the three open issues — no product defect behind it — but it is the
one that costs most per occurrence, because it makes the full-suite gate red for reasons
unrelated to whatever change is being verified. Anyone reading a failing
`run-all-tests.ps1` has to know this test is flaky to correctly ignore it, which is
exactly the kind of knowledge a gate should not require.
