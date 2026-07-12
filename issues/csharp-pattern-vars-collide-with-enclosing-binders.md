# C# backend: match pattern variables collide with enclosing binders (CS0136)

**Found by:** fuzzer run, seed `0x00018697` (99991), 3000 iterations — a fresh
run of the current compiler + generator.

**Affects:** 3 of the 4 failures in this run. All are `diffexec` failures whose
real cause is "Roslyn failed to compile C# output" — the C# backend emits code
Roslyn rejects, so the divergence never gets as far as execution. The IL backend
compiles and runs all three fine.

**Representative seeds:** `2fec713f`, `7a3cc786`, `fa7fd90a`

Repro (each replays standalone):
```
dotnet run --project src/ZScheme.Fuzzer -- --repro issues/repros/fuzz-failure-2fec713f.zs
dotnet run --project src/ZScheme.Fuzzer -- --repro issues/repros/fuzz-failure-7a3cc786.zs
dotnet run --project src/ZScheme.Fuzzer -- --repro issues/repros/fuzz-failure-fa7fd90a.zs
```

## Symptom

```
(425,112): error CS0136: A local or parameter named 'f0' cannot be declared in this
scope because that name is used in an enclosing local scope to define a local or parameter
```

This is the same *error code* as the shadowing bug fixed in `3c8ca6c`, and the
per-declaration-space uniquifier from that commit demonstrably works in general
(the same emitted files contain correctly-renamed `p0__s2`, `f0__s3`, `x55__s7`).
What survives is two specific emit contexts where the enclosing binders are not
in the declaration space at the moment the `switch` arms are emitted, so
`PushLocal` sees no collision and leaves the pattern variable's name alone.

### Shape 1 — pattern var vs. the `let`'s own name (seed `2fec713f`)

```csharp
var f0 = (this.F0, 100, 55, 90, -86712, this.F0, this.F0) switch {
    (2, var x5, var f0, _, var x6, _, var x7) => 46, _ => 44, };
```

C# scopes a local to the whole enclosing block, *including its own initializer*,
so the pattern variable `f0` nested in the initializer collides with the `f0`
being declared.

### Shape 2 — pattern var vs. a constructor parameter (seeds `7a3cc786`, `fa7fd90a`)

```csharp
public __Object_1(int x214, int x6)
    : base((x214, x6) switch { (var x214, _) => x6, }, ...)
```

The lifted object's constructor parameter list is not seeded into the declaration
space before the `base(...)` argument expressions are emitted.

## Root cause

**Shape 1** is confirmed: in `CSharpEmitter.Emit.cs:1834`, `EmitLetStmt` computes
`valExpr` *before* calling `PushLocal(let.VarName)`:

```csharp
var binding = PushLocal(let.VarName);
var decl = let.VarType is not null ? TypeToCs(let.VarType) : "var";
EmitLine($"{decl} {binding.EmittedName} = {valExpr};");
```

`valExpr` was already emitted by the time the let's name enters
`_scopedLocalNames`, so any pattern variable inside the initializer uniquifies
against a declaration space that does not yet contain the let's own name.

**Shape 2** is inferred from the emitted output (not yet traced to a line): the
`base(...)` args for a lifted object/class constructor are emitted without a
`BeginDeclarationSpace` seeded with the constructor's parameters, unlike
`EmitInstanceMethodBody` (`CSharpEmitter.Emit.cs:1900`), which does seed it.
Worth confirming against the ObjectLifter constructor-emission path before
fixing.

## Suggested fix direction

Shape 1: reserve the let's binder name in the *declaration space*
(`_scopedLocalNames`) before emitting `valExpr`, while keeping it out of
`_localBindings` until after — a reference to `x` inside `(let ([x (f x)]) ...)`
must still resolve to the *outer* `x`, so the two sets have to be staged
separately rather than both pushed early.

Shape 2: wrap the constructor's base-args emission in a declaration space seeded
with the ctor parameter list, mirroring `EmitInstanceMethodBody`.

## Priority note

Real correctness bug in the sense that valid ZScheme fails to build on the C#
backend, but it is **fail-loud, not a miscompile** — Roslyn rejects the output,
so no wrong value can escape. That makes it lower severity than
[csharp-object-capture-resolves-to-enclosing-local.md](csharp-object-capture-resolves-to-enclosing-local.md),
which is a silent wrong-value hazard. Both live in the same emitter subsystem
(local name resolution / uniquification) and are probably best fixed together.

3 of 4 failures in a 3000-case run, and all three involve object expressions or
nested matches — a recurring family: this is the third distinct bug found in the
C# emitter's binder handling.
