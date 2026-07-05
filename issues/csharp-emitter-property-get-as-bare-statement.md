# C# backend emits a discarded property/indexer *get* as a bare statement — CS0201

**Found by:** fuzzer run, seed `0xf00dcafe`, 1000 iterations
(`fuzz-runs/20260705-233027-seedf00dcafe/`)

**Affects:** the sole failure in this run — 1 `diffexec` failure (Roslyn refuses
to compile the emitted C#).

**Representative seeds:** `4002c87f`.

Repro:
```
dotnet run --project src/ZScheme.Fuzzer -- --repro fuzz-runs/20260705-233027-seedf00dcafe/artifacts/fuzz-failure-4002c87f/original.zs
```

## Symptom

The C# backend emits code Roslyn itself rejects:

```
(258,17): error CS0201: Only assignment, call, increment, decrement, await, and new object expressions can be used as a statement
```

Line 258 of the emitted `csharp-output.cs` is a discarded record-field access
sitting bare in statement position:

```csharp
catch (System.DivideByZeroException x151)
{
    new FRec_0<int>(X: 33, Y: x128).X;   // <-- CS0201: member access is not a statement
}
```

It comes from the ZScheme handler body `(FRec_0/x (FRec_0 33 x128))` (line 63 of
`original.zs`), whose value is discarded because the enclosing `with-handlers`
is a non-final element of a `(begin …)`:

```scheme
([System.DivideByZeroException x151] (FRec_0/x (FRec_0 33 x128)))
```

Every *sibling* discarded handler in the same `with-handlers` compiles fine —
they are all plain calls (`(f1 x129 x128)`, `(string->int …)`, `(fuzz-min-int
93 44747)`) or already assignment-discarded (`_ = new FCls_0(…)`). Only the one
whose discarded value is a **field/property read** trips the error.

The IL backend never gets a say here: `DifferentialExecOracle` Roslyn-compiles
the C# output *first*, so the case fails before both backends are executed and
compared. This is a C#-emitter-only bug.

## Root cause

A record/class field accessor like `FRec_0/x` lowers to an `IrNode.MethodCall`
with `IsProperty = true`
([IrLowering.cs:604](src/ZScheme.Compiler/Ir/IrLowering.cs:604)):

```csharp
return new IrNode.MethodCall(Lower(n.Args[0]), fieldName, [], true, false) { … };
```

`EmitMethodCall`
([CSharpEmitter.Emit.cs:1638](src/ZScheme.Compiler/Codegen/CSharpEmitter.Emit.cs:1638))
renders the `IsProperty` variant as a bare member access, and the `IsIndexer`
variant as a bare element access — neither of which is a call:

```csharp
if (n.IsProperty) return $"{receiver}.{methodName}";     // property GET  -> receiver.X
if (n.IsIndexer)  return $"{receiver}[{EmitExpr(n.Args[0])}]"; // indexer  GET  -> receiver[i]
```

But the discard-in-statement-position decision,
[`IsValidStatementExpr`](src/ZScheme.Compiler/Codegen/CSharpEmitter.Emit.cs:335),
accepts **every** `IrNode.MethodCall` as a legal bare statement expression:

```csharp
private static bool IsValidStatementExpr(IrNode node) =>
    node is IrNode.Call or IrNode.ClrCall or IrNode.MethodCall or … ;
```

So when a discarded value is a property-get or indexer-get `MethodCall`,
`DiscardStatement`
([CSharpEmitter.Emit.cs:403](src/ZScheme.Compiler/Codegen/CSharpEmitter.Emit.cs:403))
takes the "bare" branch (`$"{emitted};"`) instead of the always-safe
`$"_ = {emitted};"` branch, emitting `receiver.X;` — which CS0201 forbids. The
classification is too coarse: for `MethodCall` it is only correct when the node
is an actual invocation (`receiver.M(args)`), a property *set* (`receiver.X =
v`, an assignment), or an indexer *set* (`receiver[i] = v`, an assignment). The
property/indexer *get* forms emit member/element-access expressions, which are
not among C#'s statement-expression forms.

The path in this case runs through `EmitAsyncStatementsBody` (G1 is `async
Task<int>`): the discarded `with-handlers` is emitted as a real try/catch via
`EmitWithHandlersStmt`, whose handler bodies recurse back with
`isVoidReturn:true`, landing the field-get in `EmitUnitStatement` →
`DiscardStatement`
([CSharpEmitter.Emit.cs:1774](src/ZScheme.Compiler/Codegen/CSharpEmitter.Emit.cs:1774)).
The synchronous `EmitStatementsBody` / `EmitTcoBody` spines reach the same
`DiscardStatement` helper, so the bug is not specific to the async spine — any
discarded property/indexer get in statement position (e.g. inside a `begin`,
a discarded `use`/`with-handlers` body, or a Unit-returning function tail)
should hit it.

## Suggested fix direction

Tighten `IsValidStatementExpr` so a `MethodCall` only counts as a valid bare
statement when it actually emits a call or an assignment — i.e. exclude the
pure-get variants:

```csharp
IrNode.MethodCall mc => !(mc.IsProperty || mc.IsIndexer) || mc.IsPropertySet || mc.IsIndexerSet,
```

(A property/indexer *set* keeps `IsProperty`/`IsIndexer` true but also sets the
corresponding `…Set` flag and emits an assignment, so it must remain valid.)
With the get variants excluded, `DiscardStatement` falls through to the
always-legal `_ = {emitted};` form, exactly as it already does for literals,
var refs, `FieldGet`, operators, and ternaries.

## Priority note

Real C#-backend miscompilation — malformed C# emitted, not a rejected-but-valid
program — but low frequency (1/1000 in this run, and it needs the specific
shape "field/indexer read whose value is discarded in statement position").
It is a distinct root cause from the previously-fixed C#-emitter bugs
(`csharp-emitter-field-as-method-call` was CS1955, a field *used like a method*;
034cb7f was an inherited field emitted as a method call in object bodies) — this
one is the inverse mis-classification, a property *get* emitted where only a
call/assignment is legal. The indexer-get variant is latent under the same
root cause even though this run only surfaced the property-get path.
