# IL backend silently skips constructor-pattern field tests when field metadata is missing

**Introduced by:** the `PatternResolver` change (commit `18b4a9d`, "Replace dead
PatternCompiler with a live PatternResolver IR sub-pass"), 2026-07-15.

**Severity:** high (latent). When it triggers, a `match` arm matches inputs it
should reject — a silent wrong-result miscompile with **zero diagnostics**. One
trigger (records) was found and fixed before landing; the general silent-skip
behaviour remains, and the path most likely to expose a new trigger (precompiled
`.dll` imports) is completely untested.

## Background — how the IL backend decides to test a field

`Codegen/IlEmitter.Emit.cs:EmitConstructorPatternTest` extracts and tests each
field of a constructor pattern. Three lookups gate whether a field is actually
tested, and **each falls through silently when its metadata is absent**:

1. **Getter/property key** (`IlEmitter.Emit.cs:2442-2451`): builds
   `caseKey = "{effectiveUnion}.{ctor.Name}"`. If the union/record isn't registered,
   `caseKey`/`propertyNames` degrade and the getter lookup below misses.

2. **Getter lookup** (`~IlEmitter.Emit.cs:2470`):
   ```csharp
   if (getterKey is null || !_unionCaseGetters.TryGetValue(getterKey, out var getter))
       continue;   // <-- field never extracted, never tested
   ```

3. **Field-type / nested guard** (`IlEmitter.Emit.cs:2566-2568`):
   ```csharp
   var fieldZType = ctor.FieldTypes?[i];
   var unresolvableNestedCtor = field is IrPattern.Constructor { ResolvedUnion: null };
   if (fieldZType is not null && !unresolvableNestedCtor)
       EmitPatternTest(field, fieldLocal, fieldZType, ...);
   // else: the sub-pattern (including a *literal* like `5`) is silently not tested
   ```

If any of these skips fires for a **literal** or **nested-constructor** field, the
IL backend emits the `isinst` for the outer case but never checks the inner
value — so `(SRec 5 x)` matches *any* `SRec`, or `(Some (Some y))` matches any
`Some`. The arm runs with the wrong (or unbound) values. The C# backend does not
share this failure mode: it renders the literal directly into the `switch`
pattern regardless of resolved type, so C# and IL **diverge** with no error on
either side.

## Why the change made this load-bearing

Before this commit, the IL backend reconstructed union/record field-type
templates for **imported and precompiled** types by reflection over the assembly
(`ZTypeFromClrType` + `_unionCaseFieldTypes` population loops in `IlEmitter.cs`,
and `RegisterSingleCasePattern` in `IlEmitter.Define.cs`). That reflection path
was deleted. Field types now come solely from PatternResolver's annotations,
populated from `IrNode.UnionDecl`/`RecordDecl` in each module's
`ExportedIrDefinitions`. Where that annotation is missing or null, the skips above
fire silently instead of the old reflection-backed lookup succeeding.

The regression this created for **records** (a record was not registered in the
registry, so `FieldTypes` was null and skip #3 dropped the literal test) was
caught by the differential fuzzer and fixed by registering records
(`UnionCaseRegistry.RegisterRecord`, called from `LowerRecordDecl` and the
imported-module injection points). But that was one trigger; the skips remain the
general behaviour.

## The untested path

The precompiled-package field-type plumbing *looks* correct — `MetadataSerializer`
round-trips `RecordDecl`/`UnionDecl` field ZTypes
(`Cache/MetadataSerializer.cs:240-249, 300-304, 537-556`) and
`Compilation.PackageLoading.cs` feeds them into `ExportedIrDefinitions`. But **no
test matches a constructor pattern over a type imported from a precompiled `.dll`**:

- The precompiled-`.dll` tests (`EndToEndTests.cs:2311-2660`, built via
  `BuildPrecompiledRecordPackage` etc.) only do field access / construction, never `match`.
- Every constructor-pattern `match` test (including
  `EndToEndTests.RecordConstructorPatternWithLiteralField_*` and the nested
  `(Some (Some y))` tests) uses **source-compiled** stdlib
  (`GetStdLibPath()` → `packages/stdlib/src`) or locally-defined types — the local
  registry path, never the metadata round-trip.
- The differential fuzzer likewise imports stdlib **as source**, so its
  `diffexec` oracle never exercised the precompiled path either.

So if any subtle break exists in the serialize → deserialize → `RegisterImported*`
→ PatternResolver chain for precompiled imports, nothing currently catches it.

## Fix — two parts

1. **Make the skips loud.** Skips #2 and #3 should not silently `continue` /
   no-op for a *refutable* sub-pattern (literal, or constructor). Emit a diagnostic
   the way `EmitPatternTest`'s literal/`default` arms now do (that pattern was added
   in the same commit for unhandled literal value types). Only an *irrefutable*
   field sub-pattern (Wildcard/Variable) may be skipped without a test. This turns
   any future missing-metadata trigger into a compile error instead of a wrong
   answer. Keep the C# and IL last-resort throws in sync.

2. **Test the precompiled path.** Add an end-to-end test that builds a precompiled
   `.dll` package exporting a record (with a literal-testable field) and a generic
   union, imports it via `PrecompiledPackagePaths`, and `match`es a constructor
   pattern with a literal field and a nested constructor — asserting the C# and IL
   backends agree (mirroring `CompileCSharpAndRunInt` / `CompileIlAndRunInt`). This
   is the coverage the change removed the reflection safety-net for.

## Related

The `unresolvableNestedCtor` guard (skip #3) deliberately preserves the prior
conservative skip for a nested constructor whose union PatternResolver could not
resolve (e.g. a field typed as an unsubstituted type parameter). That case is
believed unreachable in well-typed programs — a constructor cannot be matched
against a bare type variable — but it is unproven. If it is genuinely
unreachable, skip #3 should be an assertion/diagnostic, not a silent skip; if it
is reachable, it is a second silent-miscompile path.
