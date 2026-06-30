# Compiler Pipeline

This document describes how the ZScheme compiler turns a `.zs` source file into
emitted .NET output (C# source or an IL assembly). It covers every pipeline
stage and explains how *inline compilation* of module packages differs from the
*precompiled* path.

The whole pipeline is orchestrated by `Compilation.Compile(string source,
string fileName)` in
[`src/ZScheme.Compiler/Pipeline/Compilation.cs`](../src/ZScheme.Compiler/Pipeline/Compilation.cs).
That method runs the stages in order, short-circuiting as soon as a stage
reports errors into the shared `DiagnosticBag`.

## High-level flow

```
source string
   │
   ▼
1. Lexing ─────────────► List<Token>
   │
   ▼
2. S-expression parse ─► List<SExpr>
   │
   ├─ (module resolution: discover imports, load/compile dependencies)
   │
   ▼
2.5 Macro expansion ───► List<SExpr>   (seeded from imported modules' macros)
   │
   ▼
3. AST building ───────► AstNode.Program
   │
   ▼
4. Type inference ─────► AstNode.Program (annotated) + Substitution
   │
   ▼
5. IR lowering ────────► IrNode
   │     (sub-passes: PatternCompiler, ClosureConverter, TailCallAnalyzer)
   │
   ▼
6. Code generation ───► C# source string  -or-  IL byte[]
```

Each `AstNode`, `IrNode`, `ZType`, `SExpr`, and `Token` is a `sealed record`,
and every node carries a `SourceSpan` so diagnostics can point back at source.

## Error handling: the DiagnosticBag

A single
[`DiagnosticBag`](../src/ZScheme.Compiler/Diagnostics/DiagnosticBag.cs) is
created when the `Compilation` is constructed and threaded through every stage.
Stages do **not** throw on user errors — they call `diagnostics.Error(message,
span)` / `diagnostics.Warning(message, span)` and keep going where possible.
After each stage `Compile()` checks `_diagnostics.HasErrors` and returns a
stage-specific `CompilationResult.*Failure` value if the bag contains errors:

| Stage | Failure result |
| --- | --- |
| Lexing | `LexerFailure` |
| Parsing | `SExprParserFailure` |
| Module resolution | `DependencyResolutionFailure` |
| Macro expansion | `MacroExpanderFailure` |
| AST building | `AstBuilderFailure` |
| Type inference | `TypeInfererFailure` |
| IR lowering | `IrLoweringFailure` |
| Codegen | success/failure determined by `HasErrors` |

When a module is compiled as a dependency (a nested `Compilation`), its
diagnostics are merged into the parent's bag via `CopyDiagnostics`.

---

## Stage 1 — Lexing

- **Input:** `string source`
- **Output:** `List<Token>`
- **Driver:** [`Lexer.Tokenize()`](../src/ZScheme.Compiler/Syntax/Lexer.cs)

The lexer scans the source character by character, producing tokens for
parentheses, brackets, symbols, numeric/string/boolean/null literals, dots,
colons, and the `...` ellipsis. Comments are stripped during tokenization.
Every token records its `SourceSpan` (line/column) for later diagnostics.

## Stage 2 — S-expression parsing

- **Input:** `List<Token>`
- **Output:** `List<SExpr>`
- **Driver:** [`SExprParser.ParseAll()`](../src/ZScheme.Compiler/Syntax/SExprParser.cs)

The parser turns the flat token stream into a tree of `SExpr` values. It
distinguishes parenthesized lists from bracket lists and desugars reader syntax:
`'expr` → `(quote expr)`, `` `expr `` → `(quasiquote expr)`, `,expr` →
`(unquote expr)`, and `,@expr` → `(unquote-splicing expr)`. Mismatched or
unexpected delimiters are reported as diagnostics.

> Note: the lexer/parser are lossy for source reconstruction — string tokens are
> unquoted/unescaped, list spans omit end positions, and comments/quote-sugar are
> dropped. Anything that re-emits source from this stage must compensate.

## Module resolution (between parsing and macro expansion)

Before macros and the AST are built, `Compile()` discovers and prepares the
module dependencies the source needs. This is the stage where the **inline vs.
precompiled** decision is made (see the dedicated section below). The steps are:

1. **Pre-parse for imports** — `CompilePreParseAndDiscoverImports` builds a
   provisional AST purely to extract `(import ...)` directives, without running a
   full compilation.
2. **Create the resolver** —
   [`ModuleResolver`](../src/ZScheme.Compiler/Modules/ModuleResolver.cs) knows the
   package search paths and resolves a module name to a `(Path, Source)` pair via
   `Resolve(moduleName, span)`. It also tracks aliases (`AddModuleAlias` /
   `ResolveAlias`).
3. **Load precompiled packages** — `CompileLoadModules` scans the package cache
   and stdlib, loading any precompiled DLL + metadata pairs into the module cache.
4. **Compile prelude modules** — `CompilePreludeModules` compiles the standard
   library prelude from source (unless disabled).
5. **Resolve and compile user imports** — `CompileResolveAndCompileImports`
   builds a
   [`ModuleGraph`](../src/ZScheme.Compiler/Modules/ModuleGraph.cs), adds an edge
   for each dependency, calls `TopologicalSort()` to order modules
   dependencies-first (reporting circular dependencies as errors), and compiles
   each source module in order via the recursive `CompileModule` path.

The product is a `List<CompiledModule>` (cached in `_moduleCache`) that feeds the
remaining stages. Type aliases (`define-type-alias`) are collected from both the
current AST and every imported module's IR before type inference runs, so the
alias registry is fully populated.

## Stage 2.5 — Macro expansion

- **Input:** `List<SExpr>`
- **Output:** `List<SExpr>`
- **Driver:** `MacroExpander.ExpandAll(sexprs, macroEnvironment)`

A fresh `MacroEnvironment` is seeded with macros exported by all transitively
imported modules (`CompiledModule.ExportedMacros`), then user-defined macros are
expanded recursively. This runs after module resolution specifically so that
imported macros are available.

## Stage 3 — AST building

- **Input:** `List<SExpr>` (macro-expanded)
- **Output:** `AstNode.Program`
- **Driver:** [`AstBuilder.BuildProgram(exprs)`](../src/ZScheme.Compiler/Ast/AstBuilder.cs)

The AST builder recognizes the special forms (`define`, `let`, `let*`, `use`,
`use*`, `if`, `lambda`, `match`, `define-record`, `define-struct`, `define-union`,
`define-class`, `define-interface`, `with`, `with-handlers`, `object`, etc.) and
produces a strongly-typed `AstNode` tree. `use`/`use*` bind an `IDisposable`
resource (validated at type-checking) and lower to an `IrNode.Use` that emits a
native C# `using` declaration or an IL try/finally so the resource is disposed when
the body's scope exits. Along the way it:

- Expands variable-arity operators into nested binary applications (e.g.
  `(+ a b c)` → `(+ a (+ b c))`, comparison chains, etc.).
- Extracts a `NamespaceDecl` (warning on multiple namespace directives).
- Extracts the `ModuleDecl` and derives the generated class name from the module
  name via `NameConverter.ClassNameFromModuleName` (PascalCase).
- Applies pending `[...]` attributes to the declarations that follow them.

## Stage 4 — Type inference

- **Input:** `AstNode.Program` + imported `CompiledModule` type info
- **Output:** the same `AstNode.Program` with `ResolvedType` annotations, plus a
  `Substitution`
- **Drivers:** [`TypeInferer.Infer(node, env)`](../src/ZScheme.Compiler/Types/TypeInferer.cs)
  then `TypeInferer.Resolve(node)`

ZScheme uses Hindley–Milner inference with unification. The orchestration
(`CompileTypeInference`) creates a root
[`TypeEnv`](../src/ZScheme.Compiler/Types/TypeEnv.cs) seeded with built-in
operator signatures, injects bindings from imported modules
(`DefineImportedBinding` / `DefineOverload` — function-typed imports form
overload sets for multimethod-style dispatch), runs inference, then runs a
`Resolve` pass that applies the accumulated substitution to every node.

Supporting pieces:

- [`Unifier.Unify(a, b, span)`](../src/ZScheme.Compiler/Types/Unifier.cs) — the
  core unification algorithm. Handles implicit boxing to `System.Object`, generic
  argument variance, and constrained type variables.
- [`Substitution`](../src/ZScheme.Compiler/Types/Substitution.cs) — `Apply`
  resolves a type through current bindings; `ApplyAndDefault` additionally
  defaults unconstrained numeric type variables to `Int`.
- [`ExhaustivenessChecker`](../src/ZScheme.Compiler/Types/ExhaustivenessChecker.cs)
  — records union constructors (`RegisterUnion`) and verifies that each `match`
  is exhaustive (`Check`).

The `ZType` hierarchy is `Int`, `Float`, `Bool`, `String`, `Unit`, `ZFuncType`,
`ZTypeVar` (inference variables), `Forall` (polymorphism), and `Con` (type
constructors such as `List[Int]`).

## Stage 4.5 — Entry-point validation

- **Input:** the typed `AstNode.Program`
- **Driver:** [`EntryPointValidator.Validate(program)`](../src/ZScheme.Compiler/Types/EntryPointValidator.cs)

A top-level `main` (sync `define` or async `define-async`) is compiled to the CLR
entry point **directly** — there is no synthesized wrapper that forwards to it and
no implicit argument conversion — so its signature must be one the runtime accepts:

- **at most one parameter**, which (if present) must be a CLR string array —
  `(Mutable-Vector String)` or `(Clr-Array String)` (any `:array` alias of `String`);
- a return type of **`Int` or `Unit`**. An async `main` may return `(Task Int)` or
  `(Task Unit)`/`Task`.

Anything else (2+ params, a non-array or non-`String`-array param, a return type
other than `Int`/`Unit`, a sync `main` returning a `Task`) is reported as an
`EntryPointValidationFailure` here, before IR lowering and codegen. This runs
before the `StopAfterTypeInference` early-return, so the LSP surfaces these
diagnostics too.

If `CompilerOptions.StopAfterTypeInference` is set (LSP analysis mode),
compilation returns a `TypeAnalysisResult` after this step, without lowering or
emitting.

## Stage 5 — IR lowering

- **Input:** typed `AstNode.Program`
- **Output:** `IrNode`
- **Driver:** [`IrLowering.Lower(node)`](../src/ZScheme.Compiler/Ir/IrLowering.cs)

Lowering converts the typed AST into the lower-level `IrNode` tree and runs three
sub-passes:

- [`PatternCompiler`](../src/ZScheme.Compiler/Ir/PatternCompiler.cs) — compiles
  `match` expressions into decision trees of type tests and field accesses.
- [`ClosureConverter`](../src/ZScheme.Compiler/Ir/ClosureConverter.cs) — performs
  lambda lifting: lambdas with free variables become top-level functions with
  explicit capture parameters, replaced at the use site by an `IrNode.Closure`
  carrying the captured values.
- [`TailCallAnalyzer`](../src/ZScheme.Compiler/Ir/TailCallAnalyzer.cs) — walks the
  IR and marks calls in tail position (`IsTailCall = true`) so the backends can
  apply tail-call optimization.

Lowering also injects out-parameter metadata for CLR imports (from
`TypeInferer.OutParamsByAlias`), registers union/record constructors for pattern
compilation, and collects the CLR namespaces the program references.

## Stage 6 — Code generation

The backend is chosen by `CompilerOptions.OutputMode`.

#### Emit-name resolution (shared pre-codegen pass)

`NameConverter` (`?`→`_q`, `*`→`_star`, hyphen/`/` segmentation, PascalCase) is **not
injective**: distinct ZScheme names can sanitize to the same identifier — e.g.
`this-function` and `ThisFunction` both become `ThisFunction`, and the locals
`this-var`/`ThisVar` both become `thisVar`. Left alone these collide in the emitted
assembly (C# `CS0111`/`CS0102`; the IL backend would silently write two methods with
the same name+signature — invalid metadata).

[`EmitNameResolver.Resolve`](../src/ZScheme.Compiler/Ir/EmitNameResolver.cs) runs in
`Compilation.CompileEmit` (and `LibraryCompiler`) over the assembled module set —
the current module plus all source-imported modules — **before** either backend, so
both compute identical names by construction. It rewrites the IR two ways, by scope:

- **Module-level** functions and values keep their original name (cross-module
  references and exported metadata key on it) but get a disambiguated `EmitName`
  stamped on the definition and on every reference. Type names and `main` are
  reserved first; a colliding function/value takes the first free `_fn`/`_fn2`/… —
  this subsumes the old func-vs-nested-type rename.
- **Local** bindings (let/use/lambda params/match/catch) never cross a module
  boundary, so a collider is simply **alpha-renamed** to a fresh raw name that
  sanitizes uniquely, with its in-scope references rewritten to match. Plain
  same-name shadowing is left untouched. The emitters need no change for locals.

The backends read `EmitName` when present and fall back to sanitizing the raw name
otherwise, so non-colliding programs are byte-for-byte unchanged. Type-vs-type
collisions are out of scope (types are kept as fixed points); the IL backend's
pre-write `VerifyNoDuplicateMembers` check is the backstop for any that slip through.

### C# backend

- **Output:** `string` of C# source
- **Driver:** [`CSharpEmitter.Emit(ir)`](../src/ZScheme.Compiler/Codegen/CSharpEmitter.cs)

Emits a `#nullable enable` preamble and using directives, declares the target
namespace, emits inline type declarations for records/unions/classes/interfaces,
and emits the program as a static class of static methods. Top-level statements
go in a static constructor. A `main` function is emitted as an ordinary `Main`
method (`int`/`void`, or `async Task`/`Task<int>`, taking `string[]`) which Roslyn
discovers as the entry point directly — there is no separate wrapper, and
`CSharpEmitter.HasEntryPoint` drives `CSharpOutputResult.IsExecutable`. The emitter
applies any `EmitName` from the resolver (keyword-escaping with `@` on top), sanitizes
identifiers against C# keywords, and instantiates generic methods explicitly to avoid
Roslyn inference failures.

### IL backend

- **Output:** `byte[]` assembly (via AsmResolver)
- **Driver:** [`IlEmitter.Emit(ir)`](../src/ZScheme.Compiler/Codegen/IlEmitter.cs)

Before emission, two hoisting passes run because IL requires an empty evaluation
stack at certain points: `WithHandlersHoister` lifts try-block handlers nested in
expressions to statement level, and `AwaitHoister` does the same for awaits. The
emitter then builds the assembly, references `System.Runtime` and any precompiled
assemblies, and emits imported-module and program IR as types/methods. A sync
`main` is designated as the assembly entry point directly. Because the CLR entry
point must return `void`/`int` (ECMA-335 §15.4.1.2), an async `main` instead gets a
minimal synchronous `<Main>$` shim that calls it and blocks on the Task via
`GetAwaiter().GetResult()` — the same wrapper Roslyn generates for `async Task Main`.
Module-level method and static-field names come from the resolver's `EmitName`
(falling back to sanitization). As a final guard, `VerifyNoDuplicateMembers` scans
every emitted type just before `_module.Write` and raises a diagnostic (rather than
writing unverifiable metadata) if any type would contain two methods with the same
name+signature or two nested types with the same name.

---

## Inline compilation vs. the precompiled path

ZScheme resolves an imported module in one of two ways. The difference is
captured on the [`CompiledModule`](../src/ZScheme.Compiler/Modules/CompiledModule.cs)
record — specifically whether `PrecompiledAssemblyPath` is `null`.

### Inline compilation (source modules)

A module is compiled **inline** when its `.zs` source is found on a search path
and compiled as part of the same overall compilation as the program that imports
it. This is what happens for prelude modules, local package sources, and any
dependency resolved to a source file.

The recursive `CompileModule` path runs the same lex → parse → macro-expand →
AST → type-infer → lower stages on the module's source and produces a
`CompiledModule` with:

- `ExportedIrDefinitions` — IR for the module's exported functions/values. Used
  for cross-module **type resolution** in consuming compilations.
- `AllIrDefinitions` — *every* IR definition, including non-exported internal
  helpers. Used by **IL emission** so an exported function can call an internal
  helper. (Other modules still only see the exported subset.)
- `PrecompiledAssemblyPath = null`.

Because the IR is available, inline modules are **re-emitted into the consuming
output**:

- **C# backend:** each imported module is emitted as an additional static class
  in the same output namespace. References use the bare class name
  (`NameConverter.ClassNameFromModuleName`) or `ClassName.Member`, since
  everything lives in one namespace. The module's CLR namespaces are added to the
  output's `using` directives.
- **IL backend:** the imported module IR is handed to `IlEmitter` and emitted as
  types directly into the assembly.

### Precompiled path (prebuilt assemblies)

A module is **precompiled** when it has already been built into a .NET assembly
and is referenced as a binary dependency instead of being recompiled. This is how
installed packages (and the cached stdlib) are consumed.

- Packages are built/installed via the CLI `install` command, which compiles all
  of a package's modules to IL and stores a `.dll` plus a `.metadata.json`
  sidecar in the package cache (keyed by package name and version).
- `compile --precompiled <path>` can also load an explicit DLL + metadata pair.

At load time, `CompileLoadModules` reads the metadata and produces a
`CompiledModule` whose:

- `ExportedIrDefinitions` is **empty** and `AllIrDefinitions` is `null` — the
  compiled code lives in the DLL, not as IR. The metadata still supplies
  `ExportedNames`, `ExportedTypes`, `ExportedClrImports`, `ExportedMacros`,
  union/record constructor info, and class→interface maps so type inference and
  exhaustiveness checking work across the boundary.
- `PrecompiledAssemblyPath` points at the DLL.
- `BuildNamespace` records the .NET namespace the module's generated class lives
  in (e.g. `ZScheme.StdLib`).
- `EmittedNames` carries the original→disambiguated rename map for any **exported**
  symbol the emit-name resolver had to rename when the library was built (absent when
  nothing collided). A consuming compilation feeds this back into `EmitNameResolver`
  so a reference to such a symbol resolves to the name actually baked into the DLL,
  rather than re-deriving the un-disambiguated sanitization.

Because there is no IR, precompiled modules are **never re-emitted** — they are
*referenced*. This drives the key codegen difference:

- **Fully namespace-qualified references, not `using`.** When the C# backend
  emits a reference to a precompiled module's class or function, it qualifies it
  with the module's `BuildNamespace`, producing e.g.
  `ZScheme.StdLib.Stdlib_OptionModule.some_function` rather than relying on a
  `using ZScheme.StdLib;`. The emitter is given a `precompiledModuleMap` /
  `precompiledModuleNamespaces` for exactly this, and `QualifiedModuleClass`
  prepends the namespace when one is present.
- **CLR namespaces from precompiled modules are excluded from output `using`
  directives** (only `PrecompiledAssemblyPath is null` modules contribute their
  namespaces), which forces the qualified form and avoids `using` conflicts.
- **Assembly paths are collected and linked.** `PrecompiledAssemblyPath` entries
  are gathered and passed to the C# backend (for `.csproj` references) and to the
  IL backend (for assembly linking).

### How the path is chosen

During `CompileResolveAndCompileImports`, for each import:

1. If the module is already in `_moduleCache` (populated by the precompiled
   load step), it is **not** recompiled — the precompiled `CompiledModule` is
   used as-is.
2. Otherwise the `ModuleResolver` tries to resolve the name to a source file on a
   search path; if found, the module is compiled **inline**.
3. If neither succeeds, an error is reported.

In short: precompiled packages are loaded first and win; anything left that
resolves to source is compiled inline.

### Summary (inline vs. precompiled)

| Aspect | Inline | Precompiled |
| --- | --- | --- |
| Backing artifact | `.zs` source on a search path | `.dll` + `.metadata.json` in the package cache |
| When compiled | As part of the current compilation | Once, ahead of time (via `install`) |
| IR available? | Yes (`ExportedIrDefinitions` + `AllIrDefinitions`) | No (IR empty; metadata only) |
| Code emission | Re-emitted into the consuming output | Referenced, never re-emitted |
| C# references | Bare `ClassName` / `ClassName.Member` | Fully qualified `BuildNamespace.ClassName.Member` |
| `using` directives | Module's CLR namespaces included | Excluded — qualification forced |
| Assembly linking | n/a (same output) | DLL added as a reference / linked |
| Produced by | `compile` / `build` | `install` (creates), consumed by `compile --precompiled` and cache loads |

---

## CLI commands

The compiler is driven by the `zs` CLI, whose entry point is
[`Program.Main`](../src/ZScheme.Cli/Program.cs). It dispatches on the first
argument to one command handler. A global `--debug` flag (stripped before
dispatch) enables compiler debug logging to stderr, and the `ZSCHEME_CACHE_DIR`
environment variable overrides the base directory for the package/git caches
(default `~/.zscheme/cache`; the NuGet cache is unaffected).

```
zs <command> [options]
```

| Command | Handler | Purpose |
| --- | --- | --- |
| `compile <file.zs>` | `CompileCommand` | Compile a single ZScheme file |
| `build` | `BuildCommand` | Build from a `.zspkg` package manifest |
| `install` | `InstallCommand` | Compile a library package and cache it (precompiled) |
| `test` | `TestCommand` | Run package tests defined in a manifest |
| `run <file.zs>` | `ExecuteCommand` | Compile and run a file *(not yet implemented)* |
| `repl` | `ReplCommand` | Start the interactive REPL |
| `package <cmd>` | `PackageCommand` | Package management (`init`) |
| `generate-project` | `GenerateProjectCommand` | Generate a `.csproj` project directory |
| `--version` / `-v` | — | Print the compiler version |
| `--help` / `-h` | — | Print usage |

### `compile <file.zs>`

Runs the full pipeline on one source file (plus its resolved dependencies) and
writes the backend's output. Options:

| Option | Description |
| --- | --- |
| `--output`, `-o <path>` | Output path (default `output`) |
| `--backend`, `-b cs\|il` | Backend: C# source or IL assembly (default `cs`) |
| `--ref <dir>` | Directory containing CLR assemblies to reference (repeatable) |
| `--module-path <dir>` | Additional module search directory (repeatable) |
| `--package-path <dir>` | Register a package for qualified imports (repeatable) |
| `--precompiled <path>` | Reference a precompiled `.dll` (repeatable) |

`--precompiled` is the consumer side of the [precompiled
path](#precompiled-path-prebuilt-assemblies): it loads a `.dll` plus its
`.metadata.json` sidecar so the imported module is referenced rather than
recompiled from source.

### `build`

Builds an entry module from a `.zspkg` package manifest, resolving the manifest's
declared dependencies. Options:

| Option | Description |
| --- | --- |
| `--manifest`, `-m <path>` | Path to the `.zspkg` manifest (default: auto-detect) |
| `--output`, `-o <path>` | Output path (overrides the manifest) |
| `--backend`, `-b cs\|il` | Backend (overrides the manifest) |
| `--ref <dir>` | Assembly search directory (repeatable) |
| `--module-path <dir>` | Additional module search directory (repeatable) |
| `--package-path <dir>` | Register a package for qualified imports (repeatable) |
| `--precompiled <path>` | Reference a precompiled `.dll` (repeatable) |

Backend selection: an explicit `--backend` flag wins, otherwise the manifest's
`(backend ...)` field, otherwise the C# backend. Use `(backend "il")` to have `build`
emit a runnable `.exe` rather than C# source. When the IL backend emits an executable it
also writes a `runtimeconfig.json`; if the package declares shared-framework
dependencies (e.g. `Microsoft.AspNetCore.App`), they are listed there so the host loads
the matching shared framework at launch.

### `install`

Compiles every module of a library package to IL and stores the resulting `.dll`
plus a `.metadata.json` sidecar in the package cache (keyed by name and version).
This is the **producer** side of the precompiled path — it is how a package
becomes available for other compilations to consume via cache lookup or
`--precompiled`. Options:

| Option | Description |
| --- | --- |
| `--manifest`, `-m <path>` | Path to the `.zspkg` manifest (default: auto-detect) |
| `--package-path <dir>` | Register a package for qualified imports (repeatable) |

### `test`

Runs the tests declared in a package manifest. Options:

| Option | Description |
| --- | --- |
| `--manifest`, `-m <path>` | Path to the `.zspkg` manifest (default: auto-detect) |
| `--module-path <dir>` | Additional module search directory (repeatable) |
| `--package-path <dir>` | Register a package for qualified imports (repeatable) |

### `run <file.zs>`

Intended to compile and run a file directly. **Not yet implemented** — it
currently prints a message directing you to `compile` followed by `dotnet run`.

### `repl`

Starts the interactive read-eval-print loop ([`ReplCommand`](../src/ZScheme.Cli/ReplCommand.cs)).
Takes no options.

### `package init`

Scaffolds a new package manifest. (`package` with no subcommand prints usage; the
only subcommand is `init`.) Options:

| Option | Description |
| --- | --- |
| `--name <name>` | Package name (default: directory name) |
| `--version <version>` | Version (default `0.1.0`) |
| `--import-prefix <pfx>` | Import prefix (default: name) |
| `--description <desc>` | Package description |
| `--license <license>` | License identifier |
| `--output`, `-o <dir>` | Target directory (default: current directory) |

### `generate-project`

Generates a `.csproj` project directory (using the C# backend output plus a
project file) so a ZScheme program can be built with the standard .NET toolchain.
Options:

| Option | Description |
| --- | --- |
| `--output`, `-o <dir>` | Target directory (default `output`) |
| `--output-type <type>` | Project output type (e.g. `Exe` / `Library`) |
| `--lang-version <ver>` | C# `LangVersion` for the generated project |
| `--manifest`, `-m <path>` | Path to a `.zspkg` manifest |
| `--nuget <PackageId:Version>` | Add a NuGet package reference (repeatable) |
