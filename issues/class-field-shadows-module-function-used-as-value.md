# Both backends miscompile a module function used as a *value* in a constructor when a field shares its name

**Found by:** hand-reduction while triaging the fuzzer run seeded `0x5eed1000`
(`fuzz-runs/20260811-054718-seed5eed1000/`). **The fuzzer did not generate this
shape itself** — see "Why the fuzzer missed it" below.

**Affects:** 0 of 9 failures in that run directly; it is the value-position
sibling of the call-position bug that accounted for all 9, which was fixed
separately by guarding `TryEmitBoundDelegateCall`'s class-field branch on the
field's own signature. That fix does **not** cover this one — the repro below
still fails — and unlike that one, this breaks **both** backends.

Repro:

```
dotnet run --project src/ZScheme.Fuzzer -- --repro issues/repros/class-field-shadows-module-fn-value.zs
```

## Symptom

```scheme
(define (f0 [a : Int] [b : Int]) : Int (+ a b))
(define (apply1 [g : (Int Int -> Int)] [x : Int]) : Int (g x x))

(define-class Derived : Base
  [f0 : Int #:mutable]
  (constructor [p : Int]
    (super (apply1 f0 5))       ; <- `f0` here is the module function
    (set! f0 p))
  (define (M) : Int f0))        ; <- `f0` here is the field
```

The type inferer resolves the `f0` in the `super` argument to the module-level
function (nothing else typechecks against `apply1`'s `(Int Int -> Int)`
parameter) and the program is accepted. Both backends then emit something else:

```
[compile] PASS: ok
[il-run] threw InvalidProgramException: Common Language Runtime detected an invalid program.
[diffexec] FAIL: Roslyn failed to compile C# output
Roslyn emit failed:
  (37,45): error CS0120: An object reference is required for the non-static field,
                         method, or property 'Fieldfn_valueModule.Derived.F0'
```

C# output:

```csharp
public Derived(int p) : base(Apply1(F0, 5))
{
    this.F0 = p;
}
```

IL output, verified separately:

```
[IL]: Error [StackUnexpected]: [… : ZSchemeFuzzed.Derived::.ctor(int32)][offset 0x0000000C]
      [found Int32][expected ref '[S.P.CoreLib]System.Func`3<int32,int32,int32>']
```

So: the C# backend emits code Roslyn rejects outright, and the IL backend emits
an assembly the JIT rejects with `InvalidProgramException`.

## Root cause

Two independent defects that happen to be reached by the same source shape.

**C# backend — `CSharpEmitter.Emit.cs:1192` (`EmitVarRef`).** The lookup chain
is local renames → local bindings → `_currentClassFields` → `_currentClassMethods`
→ `_funcToModuleClass` → `_currentModuleNames`. The last arm is:

```csharp
if (_currentModuleNames.Contains(n.Name))
    return _currentClassFields is not null
        ? $"{className}.{SanitizeFunc(n.EmitName, n.Name)}"
        : SanitizeFunc(n.EmitName, n.Name);
```

`_currentClassFields` is only populated for the **methods** loop
(`CSharpEmitter.Emit.cs:2560`), *after* the constructor has already been emitted
(`:2509-2530`), and is cleared again at `:2613`. So while emitting the
constructor initializer it is `null`, the module-qualified branch is skipped,
and the emitter writes a bare `F0` — which C# then binds to the class's own
instance property `Derived.F0`, hence CS0120. The `className`-qualifying branch
that exists directly above is exactly the right output here; it simply is not
reachable from the constructor.

**IL backend — `IlEmitter.Emit.cs:5169-5176` (`EmitLoadVar`).**

```csharp
if (
    ctx.CurrentClassFields is not null
    && ctx.CurrentClassFields.TryGetValue(name, out var classField)
)
{
    EmitLoadClassThis(il, ctx);
    il.Add(CilOpCodes.Ldfld, classField);
    return;
}
```

There is no type guard at all — any name present in the class-field map wins
over the module-level function that follows in `_staticFields` / `_methods`. The
constructor context is given that map at `IlEmitter.Emit.cs:5785`, so `f0` loads
the `Int` field where a `Func<int,int,int>` is required. This is the same field
map, and the same "name lookup ignores what the field actually is" mistake, as
the already-fixed call-position bug — just in `EmitLoadVar` rather than in
`TryEmitBoundDelegateCall`. The fix applied there (compare the field's
`Signature.FieldType` against `MapToClr(<expected type>, ctx)` with
`TypeSigComparer`) should transfer directly.

Note the two backends are wrong in *different* ways, so this is not simply the
IL backend disagreeing with a correct reference: the C# backend's ctor-scope
`_currentClassFields` gap is its own defect and needs its own fix.

## Why the fuzzer missed it

Nothing here is exotic — the generator already produces the field/function name
collision constantly (all 9 failures in the run have it). What it never produces
is a **bare named-function reference passed as a value**. Higher-order arguments
are generated as lambdas or as `(partial f …)` applications, not as a reference
to a top-level `define`; `ClassExprGenerator.ExplicitCtorFieldRhs` restricts a
`define-class` ctor's `(set! field rhs)` to params, small arithmetic and
constants; and `ObjectExprGenerator`'s `(constructor (super <int-args>))` only
ever holds `Int`-typed expressions, so no function value can appear in a super
argument either. Letting the higher-order-argument generator occasionally pass a
generated function by name — and allowing a non-`Int` super argument — would
close the gap.

## Suggested fix direction

- **C#**: hoist the `_currentClassFields` / `_currentClassMethods` assignment
  above the constructor emission at `CSharpEmitter.Emit.cs:2509` (it is already
  computed from `classDecl.Fields`, which is available there), so the ctor
  initializer takes the `{className}.{Func}` branch. The existing
  `IsObjectLifted` exclusion at `:2567` should carry over unchanged. Verify the
  ctor's own parameters still shadow fields correctly — `_localBindings` is
  consulted before `_currentClassFields`, so opening the declaration space first
  (as `:2520` already does) preserves that.
- **IL**: give `EmitLoadVar`'s class-field branch the same "is this field's type
  actually what the use site needs" guard the call-position fix already got. A
  field whose type is not a delegate cannot satisfy a `ZFuncType` use, so
  falling through to `_methods` and materializing a delegate for the module
  function is the correct behavior. Note `EmitLoadVar` has no `call.Function`
  to map, so the expected signature has to come from the `IrNode.Var`'s own
  `Type`.

Worth checking as part of either fix whether the ctor path should be consulting
the class-field map at all before the base constructor has run — `ldarg.0` plus
`ldfld` on a not-yet-initialized `this` is independently suspect.

## Priority note

Second of the two issues from this run, and lower than the call-position one
(now fixed): it takes a more specific source shape (a named function passed by
value from a constructor), it did not occur in 1000 generated programs, and —
because the C# backend fails loudly with CS0120 rather than silently computing
the wrong answer — it is much less likely to reach a user as a wrong result. It
is still a real correctness bug on both backends, and the IL half should be
cheap now that the call-position guard has established the pattern, since both
consult the same field map.
