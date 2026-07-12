# C# backend: object-expression capture resolves to an enclosing local, not the capture field (CS0103)

**Found by:** fuzzer run, seed `0x00018697` (99991), 3000 iterations — a fresh
run of the current compiler + generator.

**Affects:** 1 of the 4 failures in this run (`diffexec`, reported as "Roslyn
failed to compile C# output"). The IL backend compiles and runs it fine.

**Representative seeds:** `d83ce5be`

Repro:
```
dotnet run --project src/ZScheme.Fuzzer -- --repro issues/repros/fuzz-failure-d83ce5be.zs
```

## Symptom

```
(68,20): error CS0103: The name 'x0' does not exist in the current context
```

The emitted C# for a lifted object expression:

```csharp
public sealed class __Object_1 : IFuz_1
{
    public int X0 { get; }
    public __Object_1(int x0) { this.X0 = x0; }      // capture stored as a field

    public int M1_0(int p0, int p1) { return x0; }   // <-- should be `this.X0`
}
```

The method body references the captured variable as a bare local `x0`, but no
such local exists inside `M1_0` — the capture lives in the field `X0`.

The ZScheme source is an object expression nested inside a macro that binds `x0`:

```scheme
;; macro body binds x0, and the spliced object captures it
[(fuzz-hyg-388 body) (let* ([x0 42]) (+ x0 body))]
...
(object IFuz_1
  (define (M1_0 [p0 : Int] [p1 : Int]) : Int x0))   ; x0 is a capture
```

## Root cause

`EmitInstanceMethodBody` (`CSharpEmitter.Emit.cs:1890`) saves `_localBindings`
and restores it afterward, but **never clears it on entry**:

```csharp
var savedLocals = new HashSet<string>(_localBindings);
var savedSpace = BeginDeclarationSpace(methodParams);
foreach (var p in methodParams)
    _localBindings.Add(p.Name);          // adds params, but the enclosing
                                         // function's locals are still in there
```

Its own doc comment describes the save/restore as stopping let-bindings leaking
*out* into sibling methods. It does that — but the leak here is *inward*: an
object expression is lifted out of a function body, and at the moment the
emitter descends into the lifted method it is still holding the **enclosing
function's** locals in `_localBindings`. The enclosing function has a real
`var x0 = 42;` local (from the macro's `let*`), so `x0` is in the set.

`EmitVarRef` (`CSharpEmitter.Emit.cs:1023`) then checks `_localBindings` first
and emits the bare name `x0` instead of falling through to the field `this.X0`.

This is a **regression introduced by the locals-before-fields change** described
as fix #5 in the (now-deleted) generator-expansion notes. Before that change
fields won reference resolution, so this same program emitted `this.X0` and
compiled. Reordering the lookup to locals-first was correct for the
param-shadows-field case it targeted, but it exposed the stale-locals leak.

## Suggested fix direction

Clear `_localBindings` at the top of `EmitInstanceMethodBody` (after saving it),
then add the method parameters:

```csharp
var savedLocals = new HashSet<string>(_localBindings);
_localBindings.Clear();                  // <-- an instance method body cannot see
                                         //     the enclosing emit context's locals
var savedSpace = BeginDeclarationSpace(methodParams);
foreach (var p in methodParams)
    _localBindings.Add(p.Name);
```

An instance-method body can only legitimately reference its own parameters, its
own locals, and fields/captures — never the locals of whatever function the
emitter happened to be inside when it lifted the object.

Check whether the class-method path (`define-class`) and the lambda/closure
lifting path need the same treatment; the fuzzer only exercised the object
path here.

## Priority note

**Highest priority of the two findings in this run.** This one is only *loud*
by luck: it fails to compile because the leaked local's name (`x0`) happens not
to exist in the lifted method. When a capture's name coincides with a local that
*does* exist in scope at the emit site with a compatible type, the same defect
emits a reference to the wrong variable and **silently miscompiles** — a
wrong-value bug the compile oracle cannot see, and exactly the failure class the
notes flagged when fix #5 landed ("same-typed, it was a silent wrong-value
miscompile").

Ranks above
[csharp-pattern-vars-collide-with-enclosing-binders.md](csharp-pattern-vars-collide-with-enclosing-binders.md)
(CS0136), which is fail-loud in every instance. Both are in the same emitter
subsystem and are probably best fixed together.
