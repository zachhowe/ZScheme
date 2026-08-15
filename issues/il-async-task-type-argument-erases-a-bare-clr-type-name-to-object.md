# A bare CLR type name in an async `(Task T)` return erases to `Task<object>` (IL backend)

**Found by:** hand-reduced from a downstream failure in ZWorld (`run/scripts/src/lib/commands.zs`),
not the fuzzer. Reproduced against `f843ed7` (0.5.0).

**Affects:** every `define-async` whose `(Task T)` type argument is written as a bare CLR type
name resolved through an `import-clr` namespace, rather than fully qualified. Two severities:

- **T is a value type — fatal.** The stub declares `Task<object>` and the builder is closed over
  `object`, but the body returns the value unboxed. `InvalidProgramException` at JIT time.
- **T is a reference type — silent.** It runs correctly, but the emitted public signature is
  `Task<object>` instead of `Task<T>`. Any C# consumer, or any interface conformance check
  against that method, sees the wrong type.

**Not caught by `ilverify`.** The emitted assembly verifies clean in both cases; only the JIT
rejects it. So the existing verification oracle will not find this.

**The C# backend is correct**, which is what makes this a backend divergence rather than a
front-end bug: it emits the bare name as source text and lets Roslyn resolve it against the
generated `using` directives. The IL backend has to resolve the name to a real metadata token
itself, and its resolver cannot.

## Repro

```
dotnet run --project src/ZScheme.Fuzzer -- \
  --repro issues/repros/async-task-bare-clr-type-name-erases-to-object.zs
```

```
[compile] PASS: ok
[il-run] threw System.InvalidProgramException: Common Language Runtime detected an invalid program.
[diffexec] FAIL: Compute() outcome diverged (one threw, one returned)
[IL] threw System.InvalidProgramException
[CS] returned 7

[IL stack]
System.InvalidProgramException: Common Language Runtime detected an invalid program.
   at ZSchemeRepro.AsyncTaskBareClrTypeNameErasesToObjectModule.BareResult()
   at ZSchemeRepro.AsyncTaskBareClrTypeNameErasesToObjectModule.Compute()
```

## Minimal repro

Self-contained — no referenced assembly needed, `System.Guid` is enough:

```scheme
(namespace ZSchemeRepro)
(module m)

(import-clr
  [new-guid System.Guid/NewGuid]
  [guid-cmp System.Guid.CompareTo :instance : (System.Guid System.Guid -> Int)]
  System
  System.Threading.Tasks)

;; `Guid` resolved through the imported `System` namespace, not written out.
(define-async (bare-result) : (Task Guid) (new-guid))

(define-async (compute) : (Task Int)
  (let ([g (await (bare-result))])
    (+ 7 (guid-cmp g g))))
```

## What is and is not affected

Emitted signatures, all from one module with `System` imported (reflection over the IL-backend
output):

| spelling | position | emitted | runtime |
|---|---|---|---|
| `Guid` | sync return `: Guid` | `System.Guid` | ok |
| `System.Guid` | sync return | `System.Guid` | ok |
| `Guid` | param `[g : Guid]` | `System.Guid` | ok |
| `Guid` | inside `Func` — `(Guid -> Int)` | `Func<Guid,Int32>` | ok |
| `System.Guid` | **async** `(Task System.Guid)` | `Task<System.Guid>` | ok |
| `Guid` | **async** `(Task Guid)` | **`Task<System.Object>`** | **InvalidProgramException** |

So it is not "bare names don't resolve" in general, and not "generic type arguments don't
resolve" — a bare name inside `Func` resolves fine. It is specifically the `(Task T)` argument
of an async function.

The same table with a `readonly record struct` from a `(ref …)` assembly behaves identically,
so the BCL/external distinction is irrelevant; and a bare reference type from that assembly
emits `Task<object>` too — it just survives, because a reference needs no boxing.

## Root cause

Three findings, the first two verified directly, the third inferred:

**1. The erasure happens at `IlAsyncEmitter.cs:119`.**

```csharp
var builder = isVoid
    ? MakeTypeRef(typeof(AsyncTaskMethodBuilder), null)
    : MakeTypeRef(typeof(AsyncTaskMethodBuilder<>), _host.MapToClr(func.ReturnType, ctx));
```

For an async function `func.ReturnType` is the already-unwrapped *result* type — it is used the
same way for the result local at `:367` — so what reaches `MapToClr` here is the bare `Guid`.

`MapToClr` bottoms out in `TypeMapperCore.ResolveClrNamedType` (`:386-387`), which for the
arity-0 case does:

```csharp
var clrType = FindClrType(nt.Name);
return clrType is null ? null : f.FromClrType(clrType, corLibAware: false);
```

and `FindClrType` (`TypeMapperCore.cs:392-406`) is fully-qualified-only — `Type.GetType(name)`,
`Type.GetType($"{name}, System.Runtime")`, then `asm.GetType(name)` over loaded assemblies.
`Assembly.GetType` also requires the namespace. A bare name can never resolve through any of
the three. Null propagates to `Unmappable`, which returns `object`.

Note that `ResolveClrNamedType` *is handed* a `ClrInterop` — the object that knows the imported
namespaces — and the arity-0 path never consults it.

**2. The fallback is silent on this backend.** `AsmResolverTypeMapper.cs:135-138`:

```csharp
public void Warn(string message)
{
    // No diagnostics surface on the IL backend; the reflection backend reports these.
}
```

`Unmappable` (`:140-144`) does call `Warn($"TypeMapper: Cannot map type '{type}' …")`, but the
IL backend drops it on the floor. Compiling the repro prints nothing. `IlTypeMapper.Warn`
(`:121-124`) forwards to diagnostics, so the reflection backend would have reported this.

**3. Why the other positions work.** Everything in the "ok" rows goes through
`TypeNameCanonicalizer`, which rewrites bare names to fully-qualified ones before codegen, so
`FindClrType`'s FQN-only lookup succeeds. The canonicalizer looks correct for this shape —
`Canonicalize` (`TypeNameCanonicalizer.cs:130-141`) descends into `nt.TypeArgs`, and `"Task"`
being in `NeverCanonicalized` (`:39-46`) only pins the *name* `Task`, not its arguments. So the
async result type appears to reach codegen without having been canonicalized, rather than the
canonicalizer mishandling it. **I did not pin down the stage where the `(Task T)` unwrap for
async functions happens, or confirm that it copies the pre-canonicalization annotation** — that
is the one piece of this worth checking before fixing.

## Suggested fix direction

- Canonicalize the async result type at the unwrap site, so `func.ReturnType` for a
  `define-async` carries the same fully-qualified name a sync return would. This is the fix that
  matches how every working position above already behaves.
- Independently, make `ResolveClrNamedType`'s arity-0 path (`TypeMapperCore.cs:386`) fall back to
  the `ClrInterop` it already receives when `FindClrType` returns null — `ClrInterop.FindType`
  (`:1482`) has the namespace probing this needs. That is defence in depth: it would have turned
  this into a correct compile rather than a silent erasure, whatever the front-end did.
- Consider whether `AsmResolverTypeMapper.Warn` should stay a no-op. A silent
  "cannot map, falling back to object" is exactly the shape of defect that reaches a user as
  `InvalidProgramException` with no compiler output. The 0.3 release notes already flag this
  hazard for local `define-struct` erasure; this is the same failure mode reached by a different
  path.
- Regression tests belong in the async integration suite: one asserting the emitted return type
  of `(Task BareName)` is `Task<BareName>` and not `Task<object>`, and one executing a
  value-typed async result. `ilverify` will not catch a regression here, so the test must
  actually run the method or inspect the signature.

## Priority note

The value-type case has no workaround other than knowing the rule, and the failure gives the
user nothing to go on: no compiler warning, a clean `ilverify`, and a runtime
`InvalidProgramException` naming a method they wrote in a language where they never mentioned
`object`. The reference-type case is quieter but arguably worse to leave: it silently publishes
`Task<object>` in a public API, and a scripted class whose method is supposed to implement a CLR
interface returning `Task<T>` would fail its conformance check at type load — which surfaces as
the type simply not being discovered, with no error at all in a host that swallows
`TypeLoadException`.

**Workaround:** fully qualify the type argument — `(Task System.Guid)`, not `(Task Guid)`.
