# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What is ZScript?

ZScript is a Scheme-like functional programming language that compiles to .NET. It uses S-expression syntax and features static type inference (Hindley-Milner), immutable collections, pattern matching with exhaustiveness checking, tail call optimization, Result/Option types, and CLR interoperability.

## Build & Test Commands

```bash
dotnet build                          # Build all projects
dotnet test                           # Run all tests
dotnet test --filter "ClassName"      # Run a specific test class
dotnet test --filter "FullyQualifiedName~MethodName"  # Run a single test
```

Run all tests:
on Linux/macOS:
```bash
./run-all-tests.sh
```

on Windows:
```
.\run-all-tests.ps1
```

We should also run the stdlib package tests via the ZScript test runner when any compiler changes are made:

```bash
dotnet run --project src/ZScript.Cli -- test -m packages/stdlib/package.zspkg --module-path packages/zunit/src --package-path packages/zunit
```

We should also verify all the examples compile when any compiler changes are made:

on Linux/macOS:
```bash
./build-examples.sh
```

on Windows:
```
.\build-examples.ps1
```

The solution file is `ZScript.slnx`. Target framework is .NET 10.0 with C# preview features. `TreatWarningsAsErrors` is enabled globally via `Directory.Build.props`.

## Compiler Pipeline (6 stages)

The pipeline is orchestrated in `Compilation.cs` (`src/ZScript.Compiler/Pipeline/`):

1. **Lexing** (`Syntax/Lexer.cs`) — Source string → `List<Token>`
2. **S-Expression Parsing** (`Syntax/SExprParser.cs`) — Tokens → `List<SExpr>`
3. **AST Building** (`Ast/AstBuilder.cs`) — S-expressions → `AstNode.Program` (handles special forms: `define`, `let`, `if`, `fn`, `match`, `record`, `union`, `object`, etc.)
4. **Type Inference** (`Types/TypeInferer.cs`) — AST → Typed AST using Hindley-Milner unification (`Unifier.cs`, `Substitution.cs`, `TypeEnv.cs`). Includes `ExhaustivenessChecker` for match expressions.
5. **IR Lowering** (`Ir/IrLowering.cs`) — Typed AST → `IrNode` tree. Sub-passes: `ClosureConverter` (lambda lifting), `TailCallAnalyzer` (TCO identification), `PatternCompiler` (match → decision trees). Collection operations are defined in stdlib modules (`list.zs`, `vector.zs`, `map.zs`) using `import-clr :instance` to call methods on underlying CLR immutable types.
6. **Code Generation** (`Codegen/CSharpEmitter.cs` or `Codegen/IlEmitter.cs`) — IR → C# source or IL (via Mono.Cecil). TCO is lowered to `while(true)` loops in C#. CLR interop handled by `ClrInterop.cs`.

Module resolution (`Modules/ModuleResolver.cs`, `ModuleGraph.cs`) runs between AST building and type inference, using topological sort for dependency ordering.

## Project Layout

- `src/ZScript.Cli/` — CLI entry point (`compile`, `build`, `pack`, `test`, `run`, `repl` commands) and REPL
- `src/ZScript.Compiler/` — Core compiler (Syntax, Ast, Types, Ir, Codegen, Pipeline, Modules, Diagnostics, Package)
- `packages/stdlib/` — Standard library `.zs` files: `option.zs`, `result.zs`, `error.zs`, `core.zs`, `list.zs`, `vector.zs`, `map.zs` (imported via qualified names like `(import stdlib/option)`)
- `packages/zunit/` — ZUnit testing framework (xUnit-based assertions and test macros)
- `tests/ZScript.Compiler.Tests/` — xUnit tests mirroring compiler structure (Syntax/, Ast/, Types/, Ir/, Codegen/, Integration/, Modules/, Diagnostics/, Package/)
- `examples/` — Example `.zs` programs

## Key Conventions

- All data structures are `sealed record` types (`AstNode`, `IrNode`, `ZType`, `SExpr`, `Token`)
- Dispatching on node types uses C# `switch` expressions with type patterns
- Errors accumulate in `DiagnosticBag` rather than throwing exceptions
- Every AST/IR node carries a `SourceSpan` for diagnostic reporting
- Collection operations (`list/map`, `vector/fold`, `map/get`, etc.) are defined in ZScript stdlib modules, using `import-clr :instance` to call methods on the underlying CLR immutable types
- `ZType` hierarchy: `Int`, `Float`, `Bool`, `String`, `Unit`, `Fn`, `ZTypeVar` (inference variables), `Forall` (polymorphism), `Con` (type constructors like `List[Int]`)
- Mock testing follows a "no logic" principle — see `docs/MOCKS.md` for patterns (call recording, configurable results, event triggering, `ClearTracking()`)
