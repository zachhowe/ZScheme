# A class may omit an interface member inherited through the interface hierarchy, and the C# output then fails Roslyn

**Found by:** hand-probing while extending the fuzzer's interface-chain
coverage (2026-09-29). The fuzzer deliberately does **not** generate the
omitting shape — see `docs/FUZZER.md` §4.4 — so this is documented, not fuzzed.

**Affects:** the C# backend (and arguably the front end's checking). The IL
backend synthesizes an implementation for an interface member the class
inherits through the interface hierarchy (`840cb073`), but the C# backend
emits the class with only its declared members. When the class implements
`IChild : IBase` and declares only `ChildM`, the emitted C# is
`class Impl : IChild` without `BaseM` — Roslyn rejects it:

```text
error CS0535: 'Probe2Module.Impl' does not implement interface member
'Probe2Module.Ibase.BaseM(int)'
```

The ZScheme compiler itself accepts the program on both backends (the
interface accessors for inherited members resolve, per `88a3ab54`), so the
front end under-checks relative to what the C# backend needs — or the backend
under-synthesizes relative to what the front end accepts. Either way the two
disagree.

Repro (not under `issues/repros/` since the fuzzer never emits this shape):
compile

```scheme
(module probe2)

(interface ibase
  (BaseM [x : Int] : Int))

(interface ichild : ibase
  (ChildM : Int))

(class impl : ichild
  [f : Int]
  (define (ChildM) : Int f))

(define (compute) : Int
  (let ([o (new impl 5)])
    (ichild-ChildM o)))
```

with `--backend csharp` and build the generated `.csproj`.

## Suggested fix direction

Pick a side: have `TypeInferer.InferClassDecl` require every member of the
interface's transitive base set (a clear diagnostic, matching what csc would
say), or have `CSharpEmitter` synthesize the missing inherited members the way
the IL backend does. The fuzzer generates the supplying shape (every chain
member implemented), so fixing either side unblocks raising the gate.
