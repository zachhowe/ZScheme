# IL backend erases type arguments at higher-order boundaries, producing unverifiable metadata

## Symptom

`ilverify` rejects 14 methods in a real IL-compiled package (ZWorld's `zworld-scripts`,
~40 modules). Every one is `StackUnexpected`, and every one is a generic type argument that
became `object` on one side of a boundary but not the other:

```
[IL]: Error [StackUnexpected]: […ZworldScripts_Lib_SharedStatesModule::SharedIdleTick(INpcContext, float32)]
  [offset 0x000000AD]
  [found ref    'System.Threading.Tasks.Task`1<object>']
  [expected ref 'System.Threading.Tasks.Task`1<ZWorld.Scripts.…FsmModule+FsmResult>']
  Unexpected type on the stack.

[IL]: Error [StackUnexpected]: […ZworldScripts_Lib_FsmModule::AddState_b(FsmSpec, string, Func`2<INpcContext,Task>, …)]
  [offset 0x0000001D]
  [found ref    'System.Func`2<ZWorld.GameServer.NPC.INpcContext,System.Threading.Tasks.Task>']
  [expected ref 'System.Func`2<object,System.Threading.Tasks.Task>']
  Unexpected type on the stack.

[IL]: Error [StackUnexpected]: […FsmModule+<FsmTickCurrent_b>d__31::MoveNext()]
  [offset 0x0000014C] [found ref 'object'] [expected ref '…FsmModule+FsmResult']
```

Full breakdown, all 14: `AddState_b` ×3, the seven `Shared*Tick` handlers, `BanditScanningTick`,
`MerchantOpenTick`, and `<FsmTickCurrent_b>d__31::MoveNext` ×2.

The erasure runs in both directions — some sites hand a concrete `Task<FsmResult>` where
`Task<object>` is expected, others the reverse — so it is not a single missing cast but an
inconsistent decision about *when* a type argument is erased.

## Reproduce

```
$ zs build -m <zworld>/run/scripts/package.zspkg
$ dotnet tool run ilverify -- output.dll \
    -r "$DOTNET/shared/Microsoft.NETCore.App/10.0.10/*.dll" \
    -r "<zworld>/src/ZWorld.GameServer/bin/Debug/net10.0/*.dll" \
    -r "<zworld>/src/ZWorld.Scripting/bin/Debug/net10.0/*.dll" \
    -r ./ZScheme.Runtime.dll -s System.Private.CoreLib
14 Error(s) Verifying output.dll
```

`-r` for **both** ZWorld assemblies and `-s System.Private.CoreLib` are required; without
them you get 150 spurious `FileLoadErrorGeneric`/`mscorlib` errors that hide the real ones.
(`-s System.Private.CoreLib` is itself a consequence of
`issues/il-package-assemblies-reference-system-private-corelib.md`.)

## Status

Pre-existing and stable. The error set is byte-for-byte identical before and after the
async-TCO work (compared with `Compare-Object` over the normalised output on `f04dd655` vs
the async-TCO tree): same 14 methods, same offsets, same expected/found pairs. So async TCO
neither causes nor perturbs it — this is filed because measuring it turned it up, and
because 14 standing errors are exactly the noise floor that will hide the fifteenth.

The runtime is more permissive than `ilverify`, which is why these run fine: all 199 of
ZWorld's script tests pass against this assembly. But unverifiable metadata is a real
constraint — it blocks any future use of these assemblies where verification is enforced,
and it makes `ilverify` unusable as a CI gate or as the fuzzer's oracle until the count is
zero.

## Where to look

The concentration is telling: every failing site passes a lambda through a higher-order
boundary that is generic in the ZScheme source — `add-state!` taking `on-enter`/`on-tick`/
`on-exit` handlers, and the `Task<FsmResult>`-returning tick handlers stored in and read back
out of `FsmSpec`'s state table (a `hash` keyed by string, so its value type erases).

That points at the seam between the type mapper's decision to erase a type variable to
`object` and the emitter's decision to insert a cast, most likely in `AsmResolverTypeMapper`
and the delegate/closure construction path in `IlEmitter`. Note `TypeMapper` also warns
`Cannot map type 'X' to CLR type, falling back to object` for union types in some
single-file compiles, which is plausibly the same decision surfacing where it can be seen.

## Suggested first step

Make the count visible before trying to fix it: an `ilverify` step over the built packages
in `run-package-tests.ps1`, asserting against a checked-in expected-failure list rather than
zero. That freezes the current 14, fails on a fifteenth, and turns the eventual fix into
deletions from the list.
