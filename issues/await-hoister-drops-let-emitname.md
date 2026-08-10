# AwaitHoister drops Let.EmitName, discarding the alpha-rename EmitNameResolver assigned

## Symptom

Latent, not yet observed as a failure — filed because the discarded field is load-bearing
and the window it opens is easy to walk into.

`IrNode.Let` carries an `EmitName` that both backends prefer over sanitising `VarName`:

```
src/ZScheme.Compiler/Ir/IrNode.cs:75-82
    public sealed record Let(
        string VarName,
        IrNode Value,
        IrNode Body,
        ZType? VarType = null,
        // Final emitted local/field name, assigned by EmitNameResolver (null => sanitize).
        string? EmitName = null
    ) : IrNode;
```

`AwaitHoister` reconstructs `Let` positionally and stops at `VarType`, so `EmitName` silently
falls back to its `null` default:

```
src/ZScheme.Compiler/Ir/AwaitHoister.cs:64-73
    case IrNode.Let let:
        return new IrNode.Let(
            let.VarName,
            Rewrite(let.Value),
            Rewrite(let.Body),
            let.VarType          // <-- EmitName not passed; defaults to null
        )
        {
            Type = let.Type,
        };
```

Ordering makes this reachable rather than harmless: `EmitNameResolver.Resolve` runs at
`Pipeline/Compilation.cs:1089`, while the hoisters run later — at the IL emitter's entry
(`Codegen/IlEmitter.Emit.cs:26-31`) and at `Compilation.cs:1234-1235` for imported modules.
So the rename is assigned first and then thrown away for any `let` in a subtree that
contains an `await`.

The C# backend does not run the hoisters, so it keeps the name. That makes this a latent
backend divergence as well: the same source can emit a renamed local under C# and an
unrenamed one under IL.

## Why nothing has broken yet

`EmitName` is only non-null when `EmitNameResolver` had a collision to resolve. Losing it
re-collides the name it was renamed away from, which on the IL backend usually means a
harmless duplicate local rather than an error — locals are slots, not names. The visible
failure mode would need the collision to matter to metadata (a hoisted state-machine field,
which *is* name-keyed: see `IlAsyncEmitter.cs:154-165`, keyed on `local.Name`).

That last point is the concerning one, because it is precisely the async path — the same
path the hoister exists to serve.

## Fix

One line, plus the same treatment for `Use` and any other node reconstructed positionally in
this file. Prefer `with` over re-invoking the constructor, so a future field cannot be
dropped the same way:

```csharp
case IrNode.Let let:
    return let with { Value = Rewrite(let.Value), Body = Rewrite(let.Body) };
```

`Use` (`AwaitHoister.cs:75-84`) has no `EmitName` today but has the same positional-rebuild
shape and should move to `with` alongside it. Worth a sweep of the other rewrites in
`Ir/` for the same pattern — `TailCallLowering` had one too (it passes `EmitName` correctly,
but only because someone remembered).
