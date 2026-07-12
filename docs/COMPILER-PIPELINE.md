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
   │     (sub-passes: ObjectLifter, IiffeBetaReducer, ClosureConverter, PatternResolver)
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

An optional `CompilerOptions.MacroObserver` (`IMacroExpansionObserver`) receives a
`MacroStep` for every rewrite — the macro, the rule index, and before/after
snapshots of the enclosing top-level form with a path to the redex — plus one
`OnTopLevelFormExpanded` callback per non-`define-syntax` top-level form with its
final begin-spliced output (keyed by raw form index; used for the stepper's
progressive full-file view). Only the main file's expansion is observed; imported
modules expand with their own untraced expander. Setting `CompilerOptions.StopAfterMacroExpansion` stops compilation here
and returns a `MacroExpansionResult`; the raw and expanded s-expressions are
exposed via `Compilation.RawSExprs` / `Compilation.ExpandedSExprs` (the latter is
assigned even when expansion fails, so partial results survive e.g. the
depth-limit error). This powers the macro stepper GUI
(`src/ZScheme.MacroDebugger/`, run with
`dotnet run --project src/ZScheme.MacroDebugger -- <file.zs>`).

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
  — the pure logic that records union cases with their arities (`RegisterUnion`)
  and verifies that a `match` covers them (`Check`). It is driven by the Stage 4.6
  validator below, not by inference itself. A partial match over a union emits a
  `ZS0002` diagnostic carrying the missing cases as structured `Diagnostic.Data`
  (`"CaseName/Arity"` entries), which the LSP's add-missing-arms quick fix consumes.

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

## Stage 4.6 — Exhaustiveness checking

- **Input:** the typed `AstNode.Program` + imported `IrNode.UnionDecl`s
- **Driver:** [`ExhaustivenessValidator.Validate(program, importedUnions)`](../src/ZScheme.Compiler/Types/ExhaustivenessValidator.cs)

A standalone post-inference pass (mirroring the entry-point validator) that drives
the [`ExhaustivenessChecker`](../src/ZScheme.Compiler/Types/ExhaustivenessChecker.cs).
It registers every union's case names — locally-declared unions from the AST's
`UnionDecl` forms, and imported unions from each `CompiledModule`'s
`ExportedIrDefinitions` (their full `Cases`, not the exported-only ctor map) — then
recursively walks the tree and checks each `match` against its scrutinee's resolved
union type. A `match` that omits a union case is reported as an
`ExhaustivenessFailure` here, before IR lowering and codegen, so a proven-incomplete
match never compiles (it would otherwise throw `"Non-exhaustive match"` at runtime
via the backend's last-resort fallback arm). Non-union non-exhaustiveness (bool,
bare literals) is a warning, not an error. Like Stage 4.5 this runs before the
`StopAfterTypeInference` early-return so the LSP surfaces the diagnostics.

If `CompilerOptions.StopAfterTypeInference` is set (LSP analysis mode),
compilation returns a `TypeAnalysisResult` after this step, without lowering or
emitting.

## Stage 5 — IR lowering

- **Input:** typed `AstNode.Program`
- **Output:** `IrNode`
- **Driver:** [`IrLowering.Lower(node)`](../src/ZScheme.Compiler/Ir/IrLowering.cs)

Lowering converts the typed AST into the lower-level `IrNode` tree and runs these
sub-passes over the whole program, in order:

- [`ObjectLifter`](../src/ZScheme.Compiler/Ir/ObjectLifter.cs) — lowers `(object ...)`
  anonymous-class expressions into synthesized top-level `IrNode.ClassDecl` nodes plus
  a construction at the original site, so no `IrNode.ObjectExpr` reaches later passes.
- [`IiffeBetaReducer`](../src/ZScheme.Compiler/Ir/IiffeBetaReducer.cs) — beta-reduces
  immediately-invoked lambdas (`((lambda (x) ...) a)`) into `let` spines, so they are
  never needlessly treated as first-class closures.
- [`ClosureConverter`](../src/ZScheme.Compiler/Ir/ClosureConverter.cs) — lambda lifting:
  a lambda that captures variables from an enclosing local scope becomes a top-level
  static function with its captures prepended as parameters, replaced at the use site by
  an `IrNode.Closure` carrying the lifted name and the captured value expressions. Both
  backends consume the `IrNode.Closure` node (the C# emitter emits a native lambda
  forwarding to the lifted function; the IL emitter synthesizes a forwarding display
  class). Two categories are left as bare `IrNode.FuncDef` for the backends' own lambda
  paths, because a context-free IR pass cannot lift them soundly: lambdas that capture
  instance state (`<>this`/class fields) and lambdas that capture an enclosing generic
  function's type variables. Gated by `CompilerOptions.EnableClosureConversion` (on by
  default). Each form's lifted functions are spliced immediately before that form so
  they follow any `ObjectLifter` classes they construct and precede the body that
  references them (the IL main-module emitter registers-and-emits each function in one
  pass).
- [`PatternResolver`](../src/ZScheme.Compiler/Ir/PatternResolver.cs) — resolves each
  `match`'s constructor patterns against the union registry, annotating every
  `IrPattern.Constructor` with its owning union and each field's concrete type. Both
  backends read these annotations instead of re-deriving union metadata; the backends
  still compile the `match` itself (C# to a `switch` expression, IL to `isinst` tests).
  Runs last, so it also resolves patterns inside the lifted closure functions and
  descends into the `IrNode.Closure` nodes `ClosureConverter` produced.

Tail-call optimization is a separate shared rewrite,
[`TailCallLowering`](../src/ZScheme.Compiler/Ir/TailCallLowering.cs), that is deliberately
**not** part of `IrLowering`. It runs just before code generation — at each emitter's entry,
after the with-handlers/await hoisters — so that by then every tail self-call is a plain
`Call` with already-hoisted arguments and no other pass (name resolution, the hoisters)
needs to know about the nodes it introduces. For each top-level function it replaces every
tail *self*-call with an `IrNode.TcoJump` back-edge (carrying the parameter names and the new
argument values) and marks the `FuncDef` with `IsTcoLoop`. Only self-calls in tail position
through `if`/`let`/`match`/`begin` spines are rewritten; mutual/other tail calls and non-tail
self-calls stay plain `Call`s. The IL backend passes `includeAsync: false` (its async
state-machine emitter cannot consume a `TcoJump`); the C# backend passes `includeAsync: true`.
Both backends then emit an `IsTcoLoop` function as a loop — C# as `while(true)` with a
`continue` at each `TcoJump`, IL as a start label with a `Br` back — so self-recursion runs in
constant stack on both, closing a divergence where deep recursion looped under C# but
overflowed under IL. Uses the `.tail.` prefix are deliberately avoided (unverifiable inside
`try`/`finally`, and only a JIT hint). A known shared limitation: a name-based self-jump would
miscompile polymorphic recursion (`f<T>` calling `f<int>`).

Lowering also injects out-parameter metadata for CLR imports (from
`TypeInferer.OutParamsByAlias`), registers union/record constructors for pattern
compilation, and collects the CLR namespaces the program references.

## Stage 6 — Code generation

The backend is chosen by `CompilerOptions.OutputMode`.

Every compiled program references the **`ZScheme.Runtime`** support assembly (the analogue of
`FSharp.Core`; it currently ships the interned `ZSymbol` type behind the `Symbol` primitive). Its
path (`typeof(ZScheme.Runtime.ZSymbol).Assembly.Location`) is appended to `precompiledAssemblyPaths`
in [`Compilation.cs`](../src/ZScheme.Compiler/Pipeline/Compilation.cs), so it rides the same channel
as precompiled package assemblies: the C# backend emits a `<Reference>` for it and the IL backend
copies `ZScheme.Runtime.dll` next to the output.

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
  stamped on the definition and on every reference. `main` is reserved first (the
  entry point references it verbatim); a colliding function/value takes the first free
  `_fn`/`_fn2`/….
- **Type** names (records, unions and their cases, classes, interfaces) likewise keep
  their raw name but get a disambiguated `EmitName` stamped on the **declaration
  only**; a collider takes the first free `_type`/`_type2`/…. References need no
  rewriting because both backends resolve a type reference through a chokepoint keyed
  by the raw name (C# `QualifyType`; the IL `_userTypes`/`_unionCaseTypes` registries),
  which the emitters point at the renamed declaration. Types are allocated before
  functions/values, so a value colliding with a type still yields to it (subsuming the
  old func-vs-nested-type rename). The lone degenerate case left unhandled is a union
  whose name equals one of its own case names.
- **Local** bindings (let/use/lambda params/match/catch) never cross a module
  boundary, so a collider is simply **alpha-renamed** to a fresh raw name that
  sanitizes uniquely, with its in-scope references rewritten to match. Plain
  same-name shadowing is left untouched. The emitters need no change for locals.

The backends read `EmitName` when present and fall back to sanitizing the raw name
otherwise, so non-colliding programs are byte-for-byte unchanged. Renamed **exported**
symbols are persisted into module metadata — values in `CompiledModule.EmittedNames`,
types in `CompiledModule.TypeEmittedNames` — so a consumer of a precompiled module
references them by the name baked into the DLL (the C# backend qualifies with the
persisted name; the IL backend aliases its imported-type registry from the baked name
back to the source name). The IL backend's pre-write `VerifyNoDuplicateMembers` check
is the backstop for any collision that slips through.

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
expressions to statement level, and `AwaitHoister` does the same for awaits. Both
A-normalize by reconstructing the IR tree; each copies the original node's
`SourceSpan` onto the rewritten node so source provenance survives to codegen
(needed by coverage instrumentation). The emitter then builds the assembly,
references `System.Runtime` and any precompiled assemblies, and emits
imported-module and program IR as types/methods. A sync `main` is designated as the
assembly entry point directly. Because the CLR entry point must return `void`/`int`
(ECMA-335 §15.4.1.2), an async `main` instead gets a minimal synchronous `<Main>$`
shim that calls it and blocks on the Task via `GetAwaiter().GetResult()` — the same
wrapper Roslyn generates for `async Task Main`. Module-level method and static-field
names come from the resolver's `EmitName` (falling back to sanitization). As a final
guard, `VerifyNoDuplicateMembers` scans every emitted type just before
`_module.Write` and raises a diagnostic (rather than writing unverifiable metadata)
if any type would contain two methods with the same name+signature or two nested
types with the same name.

### Code coverage instrumentation (opt-in)

When `CompilerOptions.Coverage` is set (`CoverageOptions.Enabled`), the IL emitter weaves
coverage probes into the bodies it emits that call into
[`ZScheme.Runtime.ZSchemeCoverage`](../src/ZScheme.Runtime/Coverage.cs), imported into the
output assembly the same way `ZScheme.Runtime.ZSymbol` is (see
[`IlEmitter.Coverage.cs`](../src/ZScheme.Compiler/Codegen/IlEmitter.Coverage.cs) and the
shared [`CoverageContract`](../src/ZScheme.Compiler/Codegen/CoverageContract.cs)) rather than
synthesizing an equivalent type per compilation.

- A probe is `ldc.i4 <id>; call ZSchemeCoverage.Hit(int)` — stack-neutral, so it
  is safe to prepend before any node's IL. Line probes are emitted at the top of
  `EmitNode` for executable/branch node kinds; branch probes are emitted in
  `EmitIf`/`EmitMatch` (then/else, and per match arm) keyed to the construct's span.
- Only nodes whose `SourceSpan.File` lives under `IncludePathPrefixes` are
  instrumented (the package's main source dir; test files and precompiled deps are
  excluded).
- `ZSchemeCoverage` holds `public static int[] Hits` (counts by point id),
  `public static string Meta` (the point→source table), and `Hit(int)`. Each compiled
  program's `.cctor` (added to its `<Module>` type — the IL equivalent of a C#
  `[ModuleInitializer]`) sizes `Hits` and sets `Meta` before any other code in that module
  runs; since `PackageTester` loads each test DLL into its own collectible
  `AssemblyLoadContext`, every program gets an independent copy of this static state even
  though they share one `ZScheme.Runtime.dll` on disk. The
  `zs test --coverage` runner ([`PackageTester`](../src/ZScheme.Compiler/Package/PackageTester.cs))
  reflects `Hits`/`Meta` out of each test DLL's loaded `ZScheme.Runtime` before unloading it,
  merges across DLLs ([`CoverageAggregator`](../src/ZScheme.Compiler/Package/CoverageAggregator.cs)),
  and writes a Cobertura report ([`CoberturaWriter`](../src/ZScheme.Compiler/Package/CoberturaWriter.cs)).

Coverage is wired only into the IL backend (tests always compile to IL).

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

- `ExportedIrDefinitions` carries only the **type declarations** (`UnionDecl` /
  `RecordDecl` / `TypeAliasDecl`, rebuilt from the metadata sidecar) — not function
  bodies — and `AllIrDefinitions` is `null`; the compiled code lives in the DLL, not
  as IR. The metadata also supplies `ExportedNames`, `ExportedTypes`,
  `ExportedClrImports`, `ExportedMacros`, union/record constructor info, and
  class→interface maps so type inference and exhaustiveness checking work across the
  boundary (the Stage 4.6 validator reads imported unions out of these
  `ExportedIrDefinitions` type decls).
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
| `--no-warn-unused-params` | Disable ZS0003 unused-parameter warnings |

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
| `--no-warn-unused-params` | Disable ZS0003 unused-parameter warnings (overrides the manifest's `(warn-unused-params ...)`) |

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
