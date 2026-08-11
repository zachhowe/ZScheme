# Using the same lifted `letrec` / nested-`define` member as a value at two sites emits two nested types with one name (IL backend)

**Found by:** fuzzer run, seed `0x5c1a55e5`, 1000 iterations
(`fuzz-runs/20260811-205845-seed5c1a55e5/`)

**Affects:** 1 of the 220 failures in this run — the only `compile`-oracle failure
(`oracle.compile.failed: 1`); the other 219 are `ilverify`. Rare in the fuzzer only
because it needs the same group member in value position *twice*, and the generator
emits the value-position shape at a low rate and usually once.

**Representative seed:** `93282f1f`

**New on this branch.** `letrec` does not parse on `master` (`Unexpected bracket
expression in expression position`), so this is a bug in the
`tco-in-classes-and-objects` feature rather than one it exposed.

Repro (this one *does* reproduce through the repro runner — it fails the `compile`
oracle, which `--repro` runs):

```
dotnet run --project src/ZScheme.Fuzzer -- \
  --repro issues/repros/duplicate-display-class-for-group-member-as-value.zs
```

```
[compile] FAIL: only one backend succeeded (IL only failed)
[il] type: IlOutputFailure, success: False
```

The original artifact reproduces the same way:

```
dotnet run --project src/ZScheme.Fuzzer -- \
  --repro fuzz-runs/20260811-205845-seed5c1a55e5/artifacts/fuzz-failure-93282f1f/original.zs
```

## Minimal repro

```scheme
(namespace ZSchemeFuzzed)
(module n)

(define (h [f : (Int -> Int)] [n : Int]) : Int (f n))

(define (compute) : Int
  (letrec ([x96 (lambda ([n : Int]) : Int (if (<= n 0) 0 (x96 (- n 1))))])
    (+ (h x96 1) (h x96 2))))          ; <- x96 in value position twice
```

Drop either `(h x96 …)` and it compiles.

## Symptom

```
Error: Internal codegen error: type 'ZSchemeFuzzed.NModule' would emit two nested
types named '<>c____letrec_n_0_x96' — refusing to write invalid metadata. at (0:0)
```

The C# backend compiles the same program without complaint, so this trips the
compile-consistency oracle rather than `ilverify` or `diffexec`. The IL backend
produces no output at all — this is a hard failure, not a miscompilation.

## Root cause

`LetrecLifter` lifts a group's function bindings to top-level statics with their
captures prepended. A reference to a member becomes a direct call; a member used as a
*value* becomes an `IrNode.Closure` naming the lifted function (see the
`LetrecLifter` description in `CLAUDE.md`). Two value-position references to the same
member therefore produce two `IrNode.Closure` nodes carrying the same
`LiftedFuncName`.

The IL backend builds one display class per `Closure` node and names it after that
function (`src/ZScheme.Compiler/Codegen/IlEmitter.Emit.cs:4092-4095`):

```csharp
var displayType = new TypeDefinition(
    "",
    $"<>c__{Sanitize(closure.LiftedFuncName)}",
    TypeAttributes.NestedPrivate | TypeAttributes.Sealed | TypeAttributes.Class
);
ctx.CurrentTypeDefinition!.NestedTypes.Add(displayType);
```

There is no uniquifier and no cache: the name is a pure function of the lifted
function's name, so N closures over one lifted function try to add N identically
named nested types to the same parent. `VerifyNoDuplicateMembers`
(`IlEmitter.Emit.cs:479-517`) catches it at write time and turns it into a
diagnostic — which is why this shows up as a clean error rather than corrupt
metadata. That guard is doing its job; the naming is the bug.

Contrast the other display-class site, `EmitLambda` at `:3847`, which names its type
`<>c__{lambdaName}` where `lambdaName` embeds the monotonic `_lambdaId++`
(`:3692`) — unique by construction. Only the lifted-closure path lacks that.

## Suggested fix direction

Either is straightforward; the second is probably better:

1. **Uniquify.** Append the same monotonic counter `EmitLambda` uses, so each
   `Closure` node gets its own type.
2. **Cache and reuse.** Two closures over the same lifted function with the same
   captured *values* are the same closure; over different captured values they still
   want the same shape, only different field contents. Keying a
   `Dictionary<string, TypeDefinition>` on `LiftedFuncName` and reusing the display
   type (constructing a fresh instance per site) emits less metadata and matches what
   a C# compiler does for the equivalent code.

Option 2 is only safe if the capture *signature* is identical across sites, which it
should be — the captures come from the lifted method's leading parameters
(`:4103-4110`), which are a property of the lifted function, not of the use site.
Worth asserting rather than assuming.

## Priority note

Lowest of the three bugs from this run, and the only one that is not a silent
miscompilation: the compiler refuses to emit rather than emitting something wrong, so
nothing bad reaches runtime. It is still a real backend disagreement — valid ZScheme
that the C# backend compiles and the IL backend cannot — and it is squarely inside the
feature this branch is adding, so it should be fixed before the branch lands.

Cheapest of the three to fix, and the fix is local to one line.
