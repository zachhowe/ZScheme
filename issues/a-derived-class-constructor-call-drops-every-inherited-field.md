# A derived class constructor call drops every inherited field

**Found by:** hand-reduced while writing the type-name casing tests on `syntax-update` — the
inheritance fixture I wanted returned the wrong number. Reproduced against `ea33508`.

**Affects:** any `(Derived args…)` call on a class that has a base class and no explicit
`(constructor …)`. Both backends, in different ways. Casing-independent — it reproduces
identically with PascalCase and with hyphenated names.

**Not affected:** `(new Derived args…)`, which lowers through a different path and is correct
on both backends. That is the workaround.

## The defect

Three places describe the constructor of a derived class, and one of them disagrees.

| | inherited fields included? |
|---|---|
| the constructor's **type**, from `TypeInferer.InferClassDecl` | yes — `inheritedFields ++ ownFields -> Derived` |
| the **emitted constructor**, from either backend | yes — `Derived(int N) : base(N)` |
| the **call site**, from `IrLowering` | **no — own fields only** |

So `(Derived 1)` type-checks (arity 1 against a 1-parameter constructor), the class it builds
declares a 1-parameter constructor, and then the call is emitted with the argument list zipped
against a field list that does not contain `n`.

## Repro

```
dotnet run --project src/ZScheme.Cli -- compile \
  issues/repros/derived-class-constructor-drops-inherited-fields.zs
```

```scheme
(define-class #:open BaseThing
  [n : Int]
  (define (Value) : Int n))

;; no fields of its own, so the whole argument list is discarded
(define-class Derived : BaseThing
  (define (Total) : Int (+ n 10)))

(define (compute) : Int (Derived-Total (Derived 1)))   ; expected 11
```

## Two symptoms, by shape

Which one you get depends on whether the derived class has fields of its own, because
`Zip` truncates to the shorter list.

| shape | C# backend | IL backend |
|---|---|---|
| `Derived` has **no** own fields — `(Derived 1)` | emits `new Derived()`; **csc rejects it** (`CS7036: no argument given that corresponds to the required parameter 'N'`) | compiles and runs, **silently returns 10** — `n` is left at its default |
| `Derived` has one own field — `(Derived 1 2)` | emits `new Derived(M: 1)` — argument 1 bound to the *own* field, argument 2 dropped; csc rejects it (`CS7036`, parameter `N`) | hard error: `Type 'Derived' not found or has no matching constructor for AsmResolver IL emission` |

The silent-wrong-answer cell is the one that matters: with no own fields the emitted C# at least
fails loudly at `csc`, but the IL backend produces a running program that quietly builds the
object with every inherited field defaulted.

## Root cause

[`IrLowering.cs:1924`](../src/ZScheme.Compiler/Ir/IrLowering.cs) registers the class's
constructor under its **own** field names:

```csharp
// Register class name so (ClassName args...) lowers to RecordNew.
if (n.Constructor is null)
    _recordCtors[n.ClassName] = n.Fields.Select(f => f.Name).ToList();
```

and the call site at `IrLowering.cs:1035` zips the arguments against that list:

```csharp
var fields = fieldNames.Zip(n.Args, (name, arg) => (name, Lower(arg))).ToList();
return new IrNode.RecordNew(rName.Value, fields) { … };
```

`Zip` stops at the shorter sequence, which is why an empty `fieldNames` swallows the arguments
without complaint rather than failing an arity check.

The fix is to prepend the inherited field names — the same base-chain walk the emitters already
do when they synthesize `Derived(baseFields…, ownFields…) : base(baseFields…)`, and the same one
`TypeInferer.InferClassDecl` does via `GetAllInheritedFields` when it builds the constructor's
type. Worth routing all three through one helper rather than adding a third walk.

Note the guard immediately above the registration: a class *with* an explicit constructor is
deliberately excluded, because the C# emitter passes field names as named arguments and an
explicit constructor's parameter names are user-chosen. Those route through `ClrNew` (positional)
instead, which is why `(new Derived 1)` is correct — any fix has to keep that split intact.

## Why nothing caught it

No test or example constructs a derived class through the bare form. `examples/inheritance.zs`
declares `Dog : Animal` and `GuideDog : Dog` but never builds one — the comment on line 13 says
`Constructor: (Dog "Rex" "Woof" "Labrador")`, which is exactly the call that does not work.
`EndToEndTests` reaches derived classes only via `(new Derived 9)` with an explicit constructor.

## Priority note

Low reach, bad failure mode. The bare form is the documented spelling for records and for
non-derived classes, so it is the one a user will reach for first, and on the IL backend it
returns a wrong answer rather than an error.
