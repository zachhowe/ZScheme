# C# backend emits a record/class field as if it were a method call

**Found by:** fuzzer run, seed `0x912140c6`, 1000 iterations
(`fuzz-runs/20260701-065416-seed912140c6/`)

**Affects:** 1 `diffexec` failure (Roslyn compile of the emitted C#).

**Seed:** `990777fe`.

Repro:
```
dotnet run --project src/ZScheme.Fuzzer -- --repro fuzz-runs/20260701-065416-seed912140c6/artifacts/fuzz-failure-990777fe/original.zs
```

## Symptom

The C# backend emits code that Roslyn itself rejects:

```
error CS1955: Non-invocable member 'Fuzz_990777feModule.FCls_0.F0' cannot be used like a method.
```

`FCls_0` has `public int F0 { get; set; }` — an auto property from the
class's field list — but somewhere else in the emitted `compute()` body the
C# emitter generates a *call* `....F0(...)` against it, i.e. it has confused
a field/property named `F0` with a method of the same name (likely a
class-member name-collision in the emitter's member-lookup: a property `F0`
and a method that should have a distinct mangled name are both resolving to
the identifier `F0`). This is a C#-emitter-only bug — the IL backend's
"only one succeeded" partner never even shows up here because Roslyn refuses
to compile before `DifferentialExecOracle` gets to run both sides.

Full artifact (source, generated C#, and the failing line) is at
`fuzz-runs/20260701-065416-seed912140c6/artifacts/fuzz-failure-990777fe/`.

## Priority note

Real C# backend bug — wrong code emitted, not just a rejected program — but
only 1/93 occurrences in this run, so lower priority than the other two
findings unless it recurs in future runs.
