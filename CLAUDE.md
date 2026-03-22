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
5. **IR Lowering** (`Ir/IrLowering.cs`) — Typed AST → `IrNode` tree. Sub-passes: `ClosureConverter` (lambda lifting), `TailCallAnalyzer` (TCO identification), `PatternCompiler` (match → decision trees). Collection methods resolved via `BuiltinMethodRegistry`.
6. **Code Generation** (`Codegen/CSharpEmitter.cs` or `Codegen/IlEmitter.cs`) — IR → C# source or IL. TCO is lowered to `while(true)` loops in C#. CLR interop handled by `ClrInterop.cs`.

Module resolution (`Modules/ModuleResolver.cs`, `ModuleGraph.cs`) runs between AST building and type inference, using topological sort for dependency ordering.

## Project Layout

- `src/ZScript.Cli/` — CLI entry point (`compile`, `run`, `repl` commands) and REPL
- `src/ZScript.Compiler/` — Core compiler (Syntax, Ast, Types, Ir, Codegen, Pipeline, Modules, Diagnostics)
- `src/ZScript.Runtime/` — Runtime types: `ZsList<T>`, `ZsVector<T>`, `ZsMap<K,V>`, `ZsOption<T>`, `ZsResult<T,E>`, `ZsError`, `ZsUnit`
- `src/ZScript.Generators/` — Roslyn incremental source generator that builds `BuiltinMethodRegistry` from `[ZsBuiltin]` attributes on runtime types
- `src/ZScript.StdLib/` — Standard library `.zs` files (embedded resources)
- `tests/ZScript.Compiler.Tests/` — xUnit tests mirroring compiler structure (Syntax/, Ast/, Types/, Ir/, Codegen/, Integration/, Modules/, Diagnostics/)
- `examples/` — Example `.zs` programs

## Key Conventions

- All data structures are `sealed record` types (`AstNode`, `IrNode`, `ZType`, `SExpr`, `Token`)
- Dispatching on node types uses C# `switch` expressions with type patterns
- Errors accumulate in `DiagnosticBag` rather than throwing exceptions
- Every AST/IR node carries a `SourceSpan` for diagnostic reporting
- Runtime collection types use `[ZsBuiltin("name")]` attributes; the Roslyn source generator auto-discovers these to populate `BuiltinMethodRegistry`
- `ZType` hierarchy: `Int`, `Float`, `Bool`, `String`, `Unit`, `Fn`, `ZTypeVar` (inference variables), `Forall` (polymorphism), `Con` (type constructors like `List[Int]`)
