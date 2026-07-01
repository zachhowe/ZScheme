# IL backend: `(object : Type ...)` literal subclass touches inherited private members — unverifiable IL

**Found by:** fuzzer run, seed `0x912140c6`, 1000 iterations
(`fuzz-runs/20260701-065416-seed912140c6/`)

**Affects:** all 7 `ilverify` failures in this run.

**Representative seeds:** `6612b330`, `1f7a1250`, `54ad5131`, `7f877e6d`,
`909885e5`, `b06424c5`, `e121e88f`.

Repro:
```
dotnet run --project src/ZScheme.Fuzzer -- --repro fuzz-runs/20260701-065416-seed912140c6/artifacts/fuzz-failure-6612b330/original.zs
```

## Symptom

`ilverify` rejects the emitted IL for programs using the `(object : FCls_N ...)`
anonymous-object-literal form:

```
[IL]: Error [MethodAccess]: [...__Object_0::.ctor(int32, int32)][offset 0x3E] Method is not visible.
[IL]: Error [FieldAccess]:  [...__Object_0::.ctor(int32, int32)][offset 0x48] Field is not visible.
[IL]: Error [MethodAccess]: [...__Object_0::.ctor(int32, int32)][offset 0x4D] Method is not visible.
```

The violations are all inside the synthesized `__Object_N` type's own
constructor (see the naming in
[ObjectLifter.cs:326](src/ZScheme.Compiler/Ir/ObjectLifter.cs:326)), and
always involve `MethodAccess` immediately followed by `FieldAccess` at
adjacent offsets — consistent with the constructor calling or touching a
member of the base class (`FCls_N`, the type named in `object : FCls_N`) that
was emitted with `private`/non-inheritable visibility instead of something a
derived type's constructor can legally reach (e.g. `protected`/`family`, or
going through a `base(...)` constructor call instead of poking the field
directly). The C# backend doesn't hit this because C#'s own accessibility
rules (or a different lowering strategy for `object :` there) happen to avoid
it — this is IL-backend-specific.

All 7 occurrences are in cases using a class-deriving `(object : FCls_N ...)`
literal (confirmed in the source of `6612b330`, `54ad5131`), which the docs
already flag as a known-shaky path
([docs/FUZZER.md](docs/FUZZER.md) §4.2: "There is a known class-instance-call
IL bug; the generator gates that path to ~30% of cases"). This looks like the
same family of bug, or a closely related one, still not fully fixed — worth
checking whether the existing gate/fix covers this specific "object-literal
ctor touches inherited private member" shape.

## Priority note

Real IL backend bug, small repro count (7/93) but part of a known,
already-flagged bug family — worth confirming whether it's the same root
cause as the existing 30%-gate or a distinct gap in that fix.
