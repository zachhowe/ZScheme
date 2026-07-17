# C# backend: TCO-loop match leaks a pattern-variable rename across sibling arms → CS0103

**Found by:** the differential fuzzer (diffexec oracle, "Roslyn failed to compile
C# output"), then reduced to the minimal repro below. Master run
`seed=0x2f8c3da6` iteration 22, case `seed=0x9e09fbe3`.

**Affects:** `CSharpEmitter.EmitTcoLoopMatch`
(`src/ZScheme.Compiler/Codegen/CSharpEmitter.Emit.cs:520`) — the statement-form
(`switch` statement) match renderer used for the body of a TCO-loop function
(`IsTcoLoop`, emitted as `while (true)`). **C# backend only.** The IL backend
compiles and runs the same program fine (the original case passed ilverify;
the minimal repro below compiles clean under `-b il`).

This is a **fail-loud miscompile**: valid ZScheme that the IL backend accepts is
emitted as C# that Roslyn rejects, so the two backends diverge on whether the
program compiles at all.

## Minimal repro

```scheme
(module test)
(define-union Pair (Both [a : Int] [b : Int]) (Neither))
(define (loop [x : Int]) : Int
  (match (Both 7 8)
    [(Both x y) (if (= y 0) x (loop 0))]   ; binds x (shadows param) → renamed x__s1
    [(Neither) x]))                         ; refers to param x → emitted as x__s1 (out of scope)
(define (Compute) : Int (loop 5))
```

`loop` tail-calls itself, so it is emitted as a `while (true)` loop and its body
goes through `EmitTcoLoopMatch`. Compile the C# backend and build the output:

```
dotnet run --project src/ZScheme.Cli -- compile loop.zs -o out
dotnet build out.csproj    # error CS0103: The name 'x__s1' does not exist in the current context
```

(The `-b il` build of the same file succeeds.)

## Symptom — emitted C#

```csharp
while (true)
{
    var __match0 = new Both(7, 8);
    switch (__match0)
    {
        case Both(var x__s1, var y):     // param `x` collided, so this binder was renamed
        {
            ...
            return x__s1;                // in scope here
        }
        case Neither:
        {
            return x__s1;                // ← CS0103: x__s1 is scoped to the Both arm's block
        }
        default:
            throw new System.InvalidOperationException("Non-exhaustive match");
    }
}
```

The `(Both x y)` arm's binder `x` collides with the `x` parameter, so `PushLocal`
renames it to `x__s1` and repoints `_localRenames["x"] = "x__s1"`. The sibling
`(Neither)` arm's body references the *parameter* `x`, but the rename is still
active, so it emits `x__s1` — a name declared only inside the `Both` arm's `{ }`
block.

## Root cause

`EmitTcoLoopMatch` pushes **every arm's** pattern binders up front and pops them
all only after the whole `switch`:

```csharp
// CSharpEmitter.Emit.cs:538-589
for (var i = 0; i < arms.Count; i++)
{
    var arm = arms[i];
    allBindings.AddRange(arm.Pattern.BoundNames().Select(PushLocal));   // :541  push arm i's binders
    // ... emit `case ...: { EmitTcoLoopBody(arm.Body, ...) }` — each arm in its own block
}
...
PopLocals(allBindings);                                                 // :589  pop them all at the end
```

So while arm *i+1* is emitted, arm *i*'s rename is still live in
`_localRenames`. Because each arm is emitted into its **own braced block**, a
renamed binder (`x__s1`) is lexically confined to the arm that declared it, and
any reference to the original source name from a later arm resolves to that
out-of-scope identifier.

The header comment (`:517-519`) states the opposite as its justification —
"Pattern bindings stay in scope across all arms (a switch shares one declaration
space)". That assumption is stale: each arm gets its own `{ }`, so the binders
do **not** share scope, and cross-arm rename leakage is a bug.

The switch-**expression** renderer `EmitMatch` (`:1417`) does not have this
problem — it pushes and pops each arm's bindings **inside** the loop, per arm:

```csharp
// CSharpEmitter.Emit.cs:1443-1462
var bindings = arm.Pattern.BoundNames().Select(PushLocal).ToList();
var body = EmitExpr(arm.Body);
...
PopLocals(bindings);   // popped before the next arm
```

The two renderers have drifted: this is exactly the hazard called out by
[tco-loop-emitters-duplicate-statement-rendering.md](tco-loop-emitters-duplicate-statement-rendering.md)
— a second match renderer overlapping the first, then diverging.

## Suggested fix direction

Make `EmitTcoLoopMatch` scope each arm's pattern binders to that arm only,
matching `EmitMatch`: push the arm's `BoundNames()` immediately before emitting
its `case`/`default` block and `PopLocals` them immediately after, instead of
collecting into one `allBindings` list popped at `:589`. Each arm already
terminates (return/continue/throw) and is individually braced, so per-arm
scoping is correct and needs no shared declaration space.

Better still, per the tech-debt issue above, unify the two so pattern-binding
scope lives in one helper both renderers call — this bug is the concrete cost of
the duplication.

## Test coverage gap

No test exercises a TCO-loop match where one arm's pattern binder shadows a
parameter/enclosing local *and* a sibling arm references that outer name. Add a
C#-backend end-to-end test (compile-and-run) on the minimal repro shape, and an
IL counterpart to lock in that both backends agree.

## Priority note

Medium. Not a silent wrong-value bug and not a runtime crash — Roslyn rejects
the output, so it fails loudly at C# compile time. But it makes otherwise-valid
programs uncompilable on the C# backend (while the IL backend accepts them), and
the trigger (a match arm binding a name that shadows an enclosing local, with a
sibling arm using that name) is not exotic. Fuzzer hit it within 300 iterations.
