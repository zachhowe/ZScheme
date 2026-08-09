# 0.3.0 (unreleased)

In development since 2026-07-05. Two themes dominate: several compiler passes that had been
written but never wired into the pipeline were made live, and the language server grew from
hover-and-completion into a full-featured LSP implementation.

## Added — language and runtime

- **Scheme symbols**, backed by a reintroduced `ZScheme.Runtime` library shipping `ZSymbol`
  behind the `Symbol` primitive.
- **`string-append` is variadic**, and `+` concatenates strings.
- **Code coverage (Cobertura)** for ZScheme tests, with the instrumentation support class
  (`ZSchemeCoverage`) living in `ZScheme.Runtime` and driven by the IL backend's
  `--coverage` flag.
- **GUI macro stepper** (DrRacket-style), with compiler-side expansion tracing, a
  full-file progressive view, and an expansion-site dropdown.

## Added — pipeline passes made live

Four passes that existed as dead or unwired code are now part of the pipeline:

- **`PatternResolver`** replaced the dead `PatternCompiler` as a real IR sub-pass, annotating
  each `match`'s constructor patterns with their owning union and field types.
- **Exhaustiveness checking** wired in as pipeline stage 4.6.
- **`ClosureConverter`** wired in as a live lambda-lifting pass.
- **Tail-call optimization** turned into a shared IR pass (`TailCallLowering`) that runs just
  before codegen, **giving the IL backend real TCO** — self-recursion now runs in constant
  stack on both backends.
- Built-in compiler function definitions centralized in a `BuiltinRegistry`.

## Added — language server

The LSP went from a small feature set to broad coverage:

- **Navigation**: cross-file symbol navigation, call hierarchy, type hierarchy, and
  resolution of locals, `import-clr` aliases, and declarations.
- **Editing**: rename (including from type-declaration names, via declaration `NameSpan`s),
  document highlight, quick fixes, and richer completion — all scope-aware for locals.
- **Display**: inlay hints (including call-site parameter names), signature help with named
  labels, semantic tokens (with delta and range variants), folding, clickable CodeLens.
- **Analysis**: unused-binding analysis, `ZS0003` extended to unused parameters and private
  defines with a `let*` remove fix, exhaustiveness reconciled so the compiler's validator
  drives the LSP-enriched checker.
- **Infrastructure**: file watching, workspace folders, static capability advertisement
  instead of dynamic registration, stderr logging and a `--debug` flag for `zs-lsp`, and a
  workspace scan that skips gitignored and generated trees while reporting what it walked.
- **`#:recursive`** on `define`/`define-async` asserts that a definition's self-recursion is
  intended, silencing `ZS0005` for it. Compiler-only — the marker never leaves the AST.
- A document's analysis can no longer fail silently — that's pinned with tests.
- The owning package's manifest is resolved by the language server.

## Added — diagnostics

- **`ZS0004`** suggests dropping a namespace qualifier that an `import-clr` makes redundant.
- **`zs lint`** reports ZS0004 across a whole package instead of one editor buffer at a time,
  and `--fix` applies it in place. The analyzer moved from the language server into
  `ZScheme.Compiler/Analysis/` so both front ends share it; no compile path runs it.
- **`ZS0005`** warns when a self-recursive function will not be compiled as a loop, and names
  why: the call isn't in tail position, it sits behind a `with-handlers`/`use` frame, or the
  function isn't a top-level `define`. A new stage-4.8 analyzer mirrors `TailCallLowering`'s
  rules on the AST — a drift test pins silence to `FuncDef.IsTcoLoop` — so the language server
  sees it too. Imported modules don't leak their warnings into an importer's build.
  `--no-warn-unlooped-recursion` and `(build (main (warn-unlooped-recursion "false")))` opt out
  wholesale.

## Changed

- **Short `import-clr` spelling** — a short `new` type name and `import-clr` member paths
  resolve through the namespace hints rather than a suffix guess, and all packages were
  migrated to the short spelling.
- **CLR type names are canonicalized** so short and qualified spellings unify, and types are
  compared by identity so a load-context split stops rejecting valid overloads.
- **Package compilation is independent of the hosting process's assemblies**; the private
  interop load context is authoritative for lookup, not just loading, and is cached per
  search-path set rather than per ordering. It reports when a probe binds below the version
  a reference asked for.
- A module is compiled once whichever spelling its importers use, and a package's own module
  once whichever way a sibling spells the import; paths and URIs settled on one spelling
  throughout the language server.
- A package can be compiled **from its manifest alone**, and a library package built without
  demanding an entry file.
- An auto-installed package inherits the frameworks it should, not just its own.
- IL constructor-pattern field skips are now loud rather than silent.
- Compiler version bumped to 0.3.0; all package versions bumped one minor.

## Fixed

- **Multi-expression `define` / `lambda` / `define-async` bodies were silently dropping
  statements** — the most serious bug in this cycle; documented, then fixed.
- **Tail-call optimization never reached module code.** Both emitters lowered only the main
  IR, so an inlined source module stayed recursive under the C# backend, and a package
  library — which emits with an empty main IR and every function arriving as an imported
  module — stayed recursive under *both*. The entire stdlib compiled to stack-consuming
  recursion, silently: `ZS0005` is correctly quiet about a tail self-call, on the assumption
  the pass would loop it. Each emitter now lowers its imported modules too, so the fix covers
  every route module code takes to codegen rather than only the two that were patched.
- **C# TCO-loop match leaked a pattern-variable rename across sibling arms**; TCO-loop and
  ordinary statement/match rendering were then unified in both backends.
- **IL backend compared strings by reference** in `=` and `!=`.
- C# backend: invalid C# for shadowed local bindings; shadowed locals resolving to enclosing
  class fields; object captures resolving to a stale enclosing local; pattern variables
  colliding with enclosing binders; invalid identifiers for comparison-chain temporaries;
  union values not upcast inside tuple scrutinees.
- `ClrTypeNames` mapped `byte` to `UInt32` and mangled nested generic arguments.
- A let-bound `use` emits a real `using` rather than a lambda around one; a bare top-level
  `use` is run instead of dropped; a statement-position `with-handlers` emits a real
  try/catch; a discarded `let` spine is flattened instead of wrapped in a lambda.
- Precompiled record matching fixed alongside the constructor-pattern work.
- A notification racing the `initialize` handshake is held rather than dropped.
- `InteropLoadContext.cs` was being treated as a binary file by Git; it now has test
  coverage and docs that match what it does.
- `http` package default-module set; Zed extension grammar pointed at the public GitHub repo
  and pinned to a commit SHA.

## Tooling

- Fuzzer generator coverage expanded across operators, symbols, patterns, and stdlib; aux
  modules are preserved in failure artifacts so repros replay.
- Direct unit coverage added for compiler classes that previously only had indirect tests.
- Nine problems found by an internal audit were recorded rather than silently dropped; open
  items are tracked under `issues/`.
