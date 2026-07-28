# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What is ZScheme?

ZScheme is a Scheme-like functional programming language that compiles to .NET. It uses S-expression syntax and features static type inference (Hindley-Milner), immutable collections, pattern matching with exhaustiveness checking, tail call optimization, Result/Option types, and CLR interoperability.

## Git workflow

When asked to commit, commit directly to the current branch — including the default branch (`master`). **Ignore** the default guidance to branch before committing on the default branch. Do not create a new branch unless explicitly asked. Do not perform any Git operation **period**, unless explicitly asked.

## Build & Test Commands

Do not run these scripts over and over with different greps. If you need to do this, save the output to a temporary location first, then grep multiple times.

```bash
dotnet build                          # Build all projects
dotnet test                           # Run all tests
dotnet test --filter "ClassName"      # Run a specific test class
dotnet test --filter "FullyQualifiedName~MethodName"  # Run a single test
```

Run all tests:
```
pwsh ./run-all-tests.ps1
```

We should also run the package tests (stdlib and http) via the ZScheme test runner when any compiler changes are made:

```
pwsh ./run-package-tests.ps1
pwsh ./run-package-tests.ps1 -Debug     # Enable debug logging
```

That runs the packages under the **IL** backend. The C#-backend half transpiles every
package to a C# solution, builds it with `csc`, and runs the generated xUnit tests — every
package and every package test must pass under both backends:

```
pwsh ./run-package-csharp-tests.ps1
pwsh ./run-package-csharp-tests.ps1 -Packages stdlib,http   # Only these packages
pwsh ./run-package-csharp-tests.ps1 -NoSetup                # Skip the build + install steps
```

Its artifacts are left in `packages/out/` (gitignored) for inspection, laid out like
`examples/out/`: `transpile/<pkg>/` (the generated C# solution), `csc/<pkg>/` (the
assemblies built from it), `il/<pkg>/` (the IL backend's assembly for the same package).

We should also verify all the examples compile when any compiler changes are made:

```
pwsh ./build-examples.ps1
pwsh ./build-examples.ps1 -Examples factorial,shapes    # Build only specific examples
pwsh ./build-examples.ps1 -Debug -Examples factorial    # Debug logging for a specific example
```

The solution file is `ZScheme.slnx`. Target framework is .NET 10.0 with C# preview features. `TreatWarningsAsErrors` is enabled globally via `Directory.Build.props`.

## Versioning

**The compiler and every package share one version number.** While the packages live in this
repo they are versioned together, not independently: `Directory.Build.props`, both editor
`package.json` files, and all eight `packages/*/package.zspkg` manifests must always read the
same version.

Bump them with the one script that moves all of them together:

```bash
pwsh ./bump-version.ps1 0.4.0     # compiler + editors + every package manifest
```

`bump-package-version.ps1` bumps a single manifest and therefore *breaks* this invariant — it
warns when it leaves a package out of step with the compiler. Reach for it only to correct a
manifest that drifted, never as the normal way to version a package.

The version bumps at the **start** of a cycle, in its own commit right after the release tag,
so every commit in a cycle already carries the version it will ship as.

## Formatting

**Never run `format-all-cs.ps1`.** Formatting happens on commit. Running it by hand reformats the whole repo — hundreds of files nobody touched — and buries the actual change.

## Compiler Pipeline (6 stages)

The pipeline is orchestrated in `Compilation.cs` (`src/ZScheme.Compiler/Pipeline/`):

1. **Lexing** (`Syntax/Lexer.cs`) — Source string → `List<Token>`
2. **S-Expression Parsing** (`Syntax/SExprParser.cs`) — Tokens → `List<SExpr>`
3. **AST Building** (`Ast/AstBuilder.cs`) — S-expressions → `AstNode.Program` (handles special forms: `define`, `let`, `if`, `lambda`, `match`, `define-record`, `define-struct`, `define-union`, `define-class`, `define-interface`, `with`, `object`, etc.)
4. **Type Inference** (`Types/TypeInferer.cs`) — AST → Typed AST using Hindley-Milner unification (`Unifier.cs`, `Substitution.cs`, `TypeEnv.cs`). Includes `ExhaustivenessChecker` for match expressions.
5. **IR Lowering** (`Ir/IrLowering.cs`) — Typed AST → `IrNode` tree. Sub-passes, in order: `ObjectLifter` (lowers `(object ...)` to synthesized classes), `LetrecLifter` (eliminates `letrec` groups by lambda-lifting their function bindings to top-level statics plus `IrNode.Closure` values at the site — runs before every other pass so none of them needs an `IrNode.LetRec` case), `IiffeBetaReducer` (reduces immediately-invoked lambdas to `let` spines), `ClosureConverter` (lambda lifting — lifts capturing lambdas to top-level static functions plus an `IrNode.Closure` at the use site, which both backends consume; lambdas that capture instance state or an enclosing generic function's type variables are left as bare `FuncDef` for the backends' own lambda emission), `PatternResolver` (annotates each `match`'s constructor patterns with their owning union and field types; both backends then compile the `match` themselves). `TailCallLowering` (`Ir/TailCallLowering.cs`) is a shared rewrite that turns each tail *self*-call into an `IrNode.TcoJump` back-edge and marks its `FuncDef.IsTcoLoop`; it is **not** part of `IrLowering` but runs just before codegen (at each emitter's entry, after the hoisters) so no other pass needs to know about `TcoJump`. `async` functions are included on both backends: an async tail self-call can only be spelled `(await (self ...))`, so the pass matches that whole `await` and drops it at the recursion boundary. Collection operations are defined in stdlib modules (`list.zs`, `vector.zs`, `map.zs`) using `import-clr :instance` to call methods on underlying CLR immutable types.
6. **Code Generation** (`Codegen/CSharpEmitter.cs` or `Codegen/IlEmitter.cs`) — IR → C# source or IL (via AsmResolver). Both backends emit an `IsTcoLoop` function as a loop (C# `while(true)` + `continue` at each `TcoJump`; IL a start label + `Br` back), so self-recursion runs in constant stack on both. For an async function that still awaits, the IL backend's loop lives inside the state machine's `MoveNext` (`IlAsyncEmitter`): a back-edge is a `Stloc` into the parameter locals plus a backward `Br`, and each leaf stores the result and `Leave`s the protected region. CLR interop handled by `ClrInterop.cs`.

Module resolution (`Modules/ModuleResolver.cs`, `ModuleGraph.cs`) runs between AST building and type inference, using topological sort for dependency ordering.

`docs/COMPILER-PIPELINE.md` is the detailed reference for the pipeline, the inline-vs-precompiled module paths, and the CLI commands. Keep it up to date whenever a compiler pipeline change invalidates its contents.

## Project Layout

- `src/ZScheme.Cli/` — CLI entry point (`compile`, `build`, `install`, `test`, `run`, `lint`, `repl` commands) and REPL
- `src/ZScheme.Compiler/` — Core compiler (Syntax, Ast, Types, Ir, Codegen, Pipeline, Modules, Diagnostics, Package)
- `src/ZScheme.Runtime/` — Runtime support assembly referenced by every compiled program (analogue of FSharp.Core); ships `ZSymbol` behind the `Symbol` primitive and `ZSchemeCoverage` (hit counters + metadata for the IL backend's `--coverage` instrumentation)
- `packages/stdlib/` — Standard library `.zs` files: `option.zs`, `result.zs`, `error.zs`, `core.zs`, `list.zs`, `vector.zs`, `map.zs` (imported via qualified names like `(import stdlib/option)`)
- `packages/zunit/` — ZUnit testing framework (xUnit-based assertions and test macros)
- `src/ZScheme.Fuzzer/` — Differential fuzzer (`zs-fuzz`): generates random ZScheme programs and checks the C# and IL backends agree (compile, ilverify, diffexec oracles)
- `tests/ZScheme.Compiler.Tests/` — xUnit tests mirroring compiler structure (Syntax/, Ast/, Types/, Ir/, Codegen/, Integration/, Modules/, Diagnostics/, Package/)
- `examples/` — Example `.zs` programs

`docs/FUZZER.md` is the detailed reference for the fuzzer's architecture (generators, oracles, reporting), its detection limits, and its CLI. Keep it up to date whenever a fuzzer change (new generator, oracle, option, or coverage gap) invalidates its contents.

## Key Conventions

- All data structures are `sealed record` types (`AstNode`, `IrNode`, `ZType`, `SExpr`, `Token`)
- Dispatching on node types uses C# `switch` expressions with type patterns
- Errors accumulate in `DiagnosticBag` rather than throwing exceptions
- Every AST/IR node carries a `SourceSpan` for diagnostic reporting
- Collection operations (`list/map`, `vector/fold`, `map/get`, etc.) are defined in ZScheme stdlib modules, using `import-clr :instance` to call methods on the underlying CLR immutable types
- `ZType` hierarchy: `Int`, `Float`, `Bool`, `String`, `Unit`, `ZFuncType`, `ZTypeVar` (inference variables), `Forall` (polymorphism), `Con` (type constructors like `List[Int]`)
- Mock testing follows a "no logic" principle — see `docs/MOCKS.md` for patterns (call recording, configurable results, event triggering, `ClearTracking()`)
