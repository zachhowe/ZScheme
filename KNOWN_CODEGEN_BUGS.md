# Known C# Codegen Bugs (surfaced by the Roslyn compile-verification harness)

The `CSharpEmitterTests` harness now feeds every emitted module through Roslyn and fails
if it does not compile (errors only). Roslyn links against the full test-host dependency
closure (`TRUSTED_PLATFORM_ASSEMBLIES`), so missing-reference false positives are already
eliminated — **every bug below is the emitter producing C# that references a name existing
in no assembly, or otherwise invalid C#.**

This 1 test is currently exempted from compile-verification via the
`KnownNonCompilingOutput` set in
`tests/ZScheme.Compiler.Tests/Codegen/CSharpEmitterTests.cs`. They still run their original
string assertions. When a root cause below is fixed, delete the corresponding entries from
that set; the harness will then guard those tests and fail loudly if the output still
doesn't compile.

Harness entry points:
- `tests/ZScheme.Compiler.Tests/Codegen/RoslynCompileVerifier.cs` — the Roslyn harness.
- `CSharpEmitterTests.Compile(...)` — wires verification in; skips names in `KnownNonCompilingOutput`.

---

## Group A — FIXED (was: type aliases unresolved in inline-emitted stdlib bodies)

The original writeup blamed the emitter for "not applying aliases when imported modules are
emitted inline." That was wrong: inline-emitted module bodies and the main module use the
*same* `TypeToCs` / `_typeAliases` path. The real defect was that the referenced alias was
never **registered** in the compilation-wide `TypeAliasRegistry`, because the module that
*declares* it was not in the import graph.

Several stdlib collection modules referenced each other's aliases without importing the
declaring module — and couldn't, because the references are circular (`treelist` ↔
`mutable/treelist`, `vector` ↔ `mutable/vector`, `hash` ↔ `mutable/hash`). Real builds never
hit this because they route through `list.zs` / the prelude, which imports every collection
module together; the tests import a single module with the prelude disabled, exposing the gap.
The precompiled-library path was never affected — `LibraryCompiler.BuildAliasRegistry`
compiles the whole stdlib together, so all aliases are collected before emission.

Fixed in the stdlib source by re-declaring each cross-referenced alias locally in the modules
that use it (the duplicate-target diagnostic guards consistency), plus correcting a genuine
type bug in `concurrent/dictionary.zs` (`keys`/`values` were typed against the `List` union
but produce an `ImmutableList`, i.e. `TreeList`). No compiler change.

---

## Group B — FIXED (was: object-expression / class-declaration emission, 5 tests)

The original writeup blamed the emitter (interface decl emission, base-ctor forwarding,
inherited-member dedup). That was wrong, exactly as with Group A: **all 5 were defective test
sources, not emitter defects.** The emitter faithfully emits what each source asks for; the
sources asked for C# that cannot compile. Passing sibling tests
(`EmitObjectExpr_NestedInsideMethodBody`, `EmitObjectExpr_WithBaseClassAndConstructor`) already
exercised the correct forms.

- **B1** (`EmitObjectExpr_SingleInterface`, `EmitObjectExpr_MultipleInterfaces`): the objects
  implemented `IComparer` / `IFoo` / `IBar`, which were never declared (no `define-interface`,
  not imported, not usable CLR types) → `CS0246`. Fixed by declaring the interfaces in the
  sources.
- **B2** (`EmitObjectExpr_WithBaseClass`, `EmitObjectExpr_WithBaseClassAndInterface`): the
  objects extended `Animal` (only ctor `Animal(string Name)`) with no `(constructor (super …))`,
  so the emitter produced `: base()` → `CS7036`. There is no value to forward; the source must
  supply one. `WithBaseClassAndInterface` was given the missing `(super "Cat")`;
  `WithBaseClass` was repurposed (and renamed `EmitObjectExpr_WithFieldlessBaseClass`) to a
  fieldless base, exercising the genuinely-valid no-arg `base()` path instead of duplicating
  `EmitObjectExpr_WithBaseClassAndConstructor`.
- **B3** (`EmitClassDecl_Inheritance_BaseClassAndInterface`): field `[name : String]` PascalCases
  to property `Name`, and method `(define (Name) …)` emits method `Name()`; a C# class can't hold
  both → `CS0102`. Fixed by renaming the interface/method to `GetName`. No compiler change.

**Follow-up — FIXED (was: cross-module interface/base-class references, two compounding bugs):**
A *cross-module* interface or base-class reference (object/class in module B implementing or
extending a type declared in module A) failed to compile. Real builds never hit it because all
current object/class interfaces are same-module. Two distinct defects compounded:

1. **Emitter qualification.** Interface names in object/class base lists and base-interface
   lists (and base-class names) were emitted via `Sanitize` only — or, for interface lists,
   with no processing at all — never `QualifyType` (`CSharpEmitter.Emit.cs` base-list sites in
   `EmitClassDecl`, `EmitInterfaceDecl`, and the object-class emission loop). A cross-module
   name therefore emitted unqualified (`IProcessor`) instead of `IfaceModModule.IProcessor` →
   `CS0246`. Fixed by routing all five sites through `QualifyType` (which falls back to
   `Sanitize` for same-module names, so same-module output is unchanged).

2. **Module pruning.** Even with qualification, a declaring module that exported *only* an
   interface was dropped entirely: `CollectExportedIrDefs` and `CollectAllIrDefs`
   (`Compilation.IrCollection.cs`) had no `IrNode.InterfaceDecl` case, so the interface was
   never collected, the module never emitted, and its type never registered in
   `_typeToModuleClass`. Fixed by adding the missing `InterfaceDecl` case to both collectors.

Regression coverage: `EmitObjectExpr_ImplementsCrossModuleInterface`,
`EmitClassDecl_ExtendsAndImplementsCrossModule`,
`EmitInterfaceDecl_ExtendsCrossModuleBaseInterface` in `CSharpEmitterTests.cs` declare a type
in one module and inherit/implement it in another, then verify the output through the Roslyn
harness (all three fail with `CS0246` if either fix is reverted).

---

## Group C — FIXED (was: method self/sibling calls emit the lowercase ZScheme name, 2 tests)

Unlike Groups A/B, this was a genuine **C# emitter defect**. A method that called itself
recursively or called a sibling method emitted the original lowercase ZScheme identifier
instead of the PascalCase C# method name (and without the `this.` qualifier) →
`CS0103: The name 'countdown' / 'double' does not exist in the current context`:
```csharp
public int Countdown(int n) { return ((n == 0) ? 0 : countdown((n - 1))); }  // should be this.Countdown
```

**Root cause:** `EmitVarRef` (`CSharpEmitter.Emit.cs`) resolved a bare `IrNode.Var` through
a fixed chain (module-qualified → object captured fields → `_currentClassFields` →
`_localBindings` → `_funcToModuleClass` → `_currentModuleNames` → camelCase fallback). It
tracked class *fields* but not class *methods*, so a self/sibling call fell through to the
`SanitizeParam` fallback. The IL emitter already handled this via `_currentClassMethods`.

**Fix:** added a `_currentClassMethods` set parallel to `_currentClassFields`, populated in
`EmitClassDecl` and the object-expression emission from the type's own + inherited method
names, and a check in `EmitVarRef` (after `_localBindings`, before `_funcToModuleClass`) that
returns `this.{Sanitize(name)}`. Mirrors the IL emitter; same-module/non-method output is
unchanged. The two tests are removed from `KnownNonCompilingOutput`, so the Roslyn harness
now guards their output.
- `EmitClassMethod_RecursiveCall`
- `EmitClassMethod_CallsSiblingMethod`

---

## Group D — FIXED (was: discarded non-`Unit` value emitted in statement position, 2 tests)

Like Group C, this was a genuine **C# emitter defect**. An `async` function returning the
*non-generic* `Task` discards its body value (nothing is returned, so the body may be any
type). The emitter dispatched such a body through `EmitUnitStatement`
(`CSharpEmitter.Emit.cs`), which emitted the expression bare (`{EmitExpr(body)};`). That is
correct for a genuinely `Unit`-typed body (a void CLR call or `set!` — both valid C#
statements), but a non-`Unit` value expression such as the integer literal `0` became the
illegal statement `0;` → `CS0201: Only assignment, call, increment, decrement, await, and new
object expressions can be used as a statement`:
```csharp
public static async System.Threading.Tasks.Task SideEffect() { 0; }  // should be _ = 0;
```
The same `EmitUnitStatement` path is reached from `EmitFuncDef` (the
`func.IsAsync && func.ReturnType == ZType.Unit` branch) and from `EmitAsyncStatementsBody`'s
`isVoidReturn` default case (await-containing bodies whose tail expression is non-`Unit`).

**Fix:** `EmitUnitStatement` now checks the body's type — a discarded value whose type is not
`Unit` (and which is not a `Throw`, already a valid statement) is emitted as `_ = expr;`, a
discard assignment that is valid for any non-void value. `Unit`-typed bodies and `Throw` keep
the prior bare-statement emission, so same-module/non-async output is unchanged. This mirrors
the `_`-discard handling `EmitLetStmt` already used for `let [_ value]` bindings. Both tests
are removed from `KnownNonCompilingOutput`, so the Roslyn harness now guards their output.
- `EmitAsyncWithoutAwait_NonGenericTask`
- `EmitAwaitNonGenericTaskInLet`

---

## Group E — FIXED (was: let-bound name out of scope in emitted body, 1 test)

**Symptom:** `CS0103: The name 'y' does not exist in the current context`.

Like Groups C/D, this was a genuine **C# emitter defect**. The original writeup blamed
"lambda-lifting / scope plumbing"; the real cause was narrower. A *nested* top-level `let`
becomes a chain of module-level static fields: `EmitTopLevel` (`CSharpEmitter.Emit.cs`) emits an
`IrNode.Let` as `public static … {SanitizeFunc(...)} = …;` (PascalCase) and then **recurses into
`let.Body`**, so `(let [x …] (let [y …] (writeln y)))` produces fields `X` *and* `Y`. But
`EmitVarRef` only resolves a bare `IrNode.Var` to its PascalCased field name if the name is in
`_currentModuleNames`; otherwise it falls through to the camelCase `SanitizeParam` fallback.

**Root cause:** `CollectModuleNames` (`CSharpEmitter.cs`), which populates `_currentModuleNames`,
inspected only **direct `Seq` children** and **single top-level nodes** — it never recursed into a
`Let`'s `Body`. The outer binding `x` was registered; the inner binding `y` (living in the outer
let's body) was not. So `Y` was emitted as a field but `y` resolved through the camelCase fallback
(`y`) → a name that exists in no scope → `CS0103`. The collector simply failed to mirror what
`EmitTopLevel` actually emits as fields.

**Fix:** `CollectModuleNames` now recurses into `let.Body` in both `Let` cases, mirroring
`EmitTopLevel`'s body recursion, so every nested top-level binding that becomes a static field is
registered. It traverses only `let.Body` (never `let.Value`), so bindings inside a let's value
expression — locals lowered via `EmitExpr`, not module fields — are not mis-registered. No change
to `EmitVarRef` or the name-sanitization helpers was needed. The test is removed from
`KnownNonCompilingOutput` (now empty), so the Roslyn harness guards its output.
- `EmitNestedLetWithClrCallBody`
