# InteropLoadContext.Probe has no version floor, and Load may hand back a lower version

**Found by:** audit of `b7d1961`. Code inspection. Deliberately **documented rather than
changed** in `2dd6b52`, because choosing a policy here needs a reproduction.

**Affects:** `InteropLoadContext.Probe` and `InteropLoadContext.Load`
(`src/ZScheme.Compiler/Codegen/InteropLoadContext.cs:138`, `:84`).

## Symptom

None observed. This is a latent mismatch between what the code promises and what it does, with
two plausible failure modes if it fires: a `FileLoadException` from the runtime, or reflection
proceeding against an assembly older than the compilation asked for.

## Root cause

The doc comment originally promised "the newest candidate that **satisfies** `wanted` wins".
There is no `version >= wanted` check anywhere in `Probe` — an exact match returns early, and
otherwise the newest copy is taken regardless of whether it clears the requested version. When
every copy on the search paths is older than `wanted`, one is still returned.

In practice this is equivalent to the documented behaviour in almost every case (the newest copy
satisfies `wanted` exactly when *any* copy does), which is why `2dd6b52` corrected the comment
to admit there is no floor rather than adding one. The genuinely open question is what *should*
happen when nothing satisfies `wanted`:

- return the newest anyway (current) — fails loudly naming a real file, but may bind a version
  the caller cannot use;
- return null — makes the runtime fall back to the default context, which is
  [exactly the bug the class exists to prevent](interop-load-context-host-assembly-preempts-private-load.md).

Neither is obviously right without a case that distinguishes them.

Separately, `Load`'s reuse branch (`InteropLoadContext.cs:93`) hands back whatever copy is
already in the context:

```csharp
// A context can only hold one assembly per simple name, so handing back a
// near-enough version beats failing the load
var loaded = Assemblies.FirstOrDefault(a => a.GetName().Name == simpleName);
if (loaded is not null)
    return loaded;
```

This also pins the first-loaded version for the cached context's entire (unbounded) lifetime —
see
[interop-load-context-leaks-a-context-per-search-path-ordering.md](interop-load-context-leaks-a-context-per-search-path-ordering.md).

`LoadByName` (`InteropLoadContext.cs:120`) builds `new AssemblyName(simpleName)` with a null
`Version`, so for that path `wanted` is always null and the exact-match early return can never
fire. That is now documented at the method.

## Suggested fix direction

Only worth acting on once a real version-skew case exists. Then decide the
nothing-satisfies-`wanted` policy explicitly and encode it, rather than leaving it as a
fall-through. `tests/ZScheme.Compiler.Tests/Codegen/InteropLoadContextTests.cs` can emit
assemblies at arbitrary versions, so such a case is cheap to construct once its shape is known.

## Priority note

Lowest of the four `InteropLoadContext` issues. Recorded so the absent floor is a known,
deliberate gap rather than something a future reader has to re-derive from the comment. Note
that the existing tests pin "newest wins" behaviour, so changing the policy will require
updating `Probe_PrefersNewestVersion_WhenSeveralSearchPathsCarryTheAssembly`.
