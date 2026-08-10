# Package builds never report ZS0005, so un-looped recursion is invisible where it matters most

## Symptom

A package whose source contains an obviously un-loopable self-recursion builds completely
silently:

```
$ cat probe/src/probe.zs
(module probe)
(define (fact [n : Int]) : Int (if (= n 0) 1 (* n (fact (- n 1)))))
(export fact)

$ zs build -m probe/package.zspkg
Generated: output.cs
Generated: output.csproj
```

No `ZS0005`. Not suppressed by configuration — `WarnUnloopedRecursion` defaults to true and
the manifest sets nothing.

The same function through the single-file path reports it correctly:

```
$ zs compile probe.zs
Warning: Self-recursive function 'fact' is not compiled as a loop: the recursive call is not
in tail position, so deep recursion can overflow the stack at probe.zs(2:10)
```

Verified on `f04dd655`. `zs build -m` on a whole real package (ZWorld's `zworld-scripts`,
~40 modules) likewise emits **zero** diagnostics of any severity.

## Root cause

Stage 4.8 is wired into `Compilation.Compile` only:

```
src/ZScheme.Compiler/Pipeline/Compilation.cs:274-276
    // Stage 4.8: Self-recursion that TailCallLowering will not compile as a loop (ZS0005).
    new TailRecursionAnalyzer(_diagnostics, _options.WarnUnloopedRecursion).Analyze(program!);
```

Neither module-compilation path runs it — `TailRecursionAnalyzer` does not appear in
`Package/LibraryCompiler.cs` or `Pipeline/Compilation.ModuleCompilation.cs` at all. A
package build compiles every one of its modules through `LibraryCompiler.CompileModule`, so
*no* function in a package is ever analysed.

## Why this matters more than it looks

Stage 4.8 is the only signal that a function silently consumes stack, and the code most
likely to need it — library code, run at unknown depth by unknown callers — is exactly the
code that reaches the compiler as a package module. The stdlib analyses itself never; nor
does any downstream package.

This is the second time the package path has quietly diverged from the main path on TCO. The
first was `TailCallLowering` not being applied to imported modules at all, which left the
entire stdlib compiling to stack-consuming recursion (see `docs/changelog/unreleased.md`).
That one was found by accident. This one is its diagnostic twin: the pass now runs, but the
warning that would tell you when it *didn't* still does not.

Concretely, it is why ZWorld's `merchant-restock-loop` sat un-looped and unreported for its
whole life: its package build was silent, and the in-process `ScriptEngine` (which does
receive the `DiagnosticBag`) logged diagnostics only on failure.

## Fix

Run the analyzer in the module paths too. It takes a typed `AstNode.Program` and a
`DiagnosticBag`, both of which those paths already have, so this is a call site rather than
a design change:

- `Pipeline/Compilation.ModuleCompilation.cs` — after that path's type-inference stage.
- `Package/LibraryCompiler.CompileModule` (`:518`) — same, honouring the manifest's
  `(warn-unlooped-recursion …)`.

Two things to settle while doing it:

1. **Attribution.** A warning raised while compiling a dependency should be reported against
   that module's own file/span, and probably suppressed when the module is an *installed*
   dependency rather than package-local source — you cannot act on a warning about somebody
   else's shipped package.
2. **Volume.** Turning this on across stdlib will surface a backlog. Worth a triage pass
   before wiring it up, marking the genuinely-unbounded ones with `#:recursive` and fixing
   the rest.

A drift-style test would keep it wired: the existing `TailRecursionDriftTests` contract
("analyzer silence ⇔ `IsTcoLoop`") is only checked through `Compilation.Compile`, so it
cannot see this gap. An equivalent assertion driven through `LibraryCompiler` would.
