# IL-emitted package assemblies reference System.Private.CoreLib, so C# cannot consume them

## Symptom

A C# project that references a ZScheme package assembly from the package cache fails on
every public signature naming a corelib type:

```
di-tests.cs(22,17): error CS0012: The type 'Type' is defined in an assembly that is not
referenced. You must add a reference to assembly 'System.Private.CoreLib, Version=10.0.0.0,
Culture=neutral, PublicKeyToken=7cec85d7bea7798e'.

aspnet-auth-tests.cs(33,36): error CS0012: The type 'Task<>' is defined in an assembly that
is not referenced. …
```

The assemblies really do reference it:

```
$ # assembly references of ~/.zscheme/cache/pkg/0.3.0/zscheme-di-abstractions/0.2.0/zscheme-di-abstractions.dll
Microsoft.Extensions.DependencyInjection.Abstractions
System.Private.CoreLib      <-- implementation assembly
System.Runtime              <-- reference assembly (the module's declared corlib)
System.ComponentModel
```

## Root cause

`IlEmitter` creates its module with `System.Runtime` as the corlib
(`IlEmitter.Emit.cs`, `new ModuleDefinition(assemblyName + ".dll", corLib)`), but every
individual import goes through `_module.DefaultImporter.ImportType(typeof(T))`, which takes
the assembly identity straight from reflection. At run time a corlib type reports
`System.Private.CoreLib`, so each import mints a reference to the implementation assembly
rather than the reference assembly other .NET compilers emit.

The runtime binds either spelling, which is why nothing noticed until a C# compilation had
to read the metadata.

## Why the obvious workarounds do not work

Both were tried and measured, not assumed:

1. **Add `System.Private.CoreLib` to the consuming csproj.** It then declares
   `System.Object` while `System.Runtime` only forwards it, so Roslyn picks it as the
   corlib and the reference assembly's own declarations collide:
   `error CS0433: The type 'AssemblyCompanyAttribute' exists in both …` (×8, all from the
   SDK's generated AssemblyInfo — `GenerateAssemblyInfo=false` clears those but
   `ExcludeFromCodeCoverageAttribute` and `TargetFrameworkAttribute` remain).
2. **Add it with `<Aliases>`.** Aliasing removes it from the global namespace, so now
   *nothing* declares `System.Object`: `error CS0518: Predefined type 'System.Boolean' is
   not defined or imported` for every predefined type.

## Why a blanket redirect to System.Runtime is wrong

The tempting fix — rewrite `System.Private.CoreLib` references to the corlib scope, either
post-emit or via a `ReferenceImporter` subclass overriding `ImportAssembly` — was
implemented and reverted. It produces clean metadata and the C# side compiles, but
`System.Runtime` does not forward every corlib type. `System.Threading.Thread` lives behind
the `System.Threading.Thread` facade, so the redirect breaks it at load time:

```
FAIL: AspNetRoutingTests.Post_with_body
      Could not load type 'System.Threading.Thread' from assembly 'System.Runtime, …'.
```

31 of 32 aspnet tests failed on the IL backend, plus stdlib and http. Regressing the
working backend to accommodate the other one is not a trade worth making.

## The real fix

Resolve the *reference assembly* per type rather than mapping the whole implementation
assembly to one facade — which is exactly what a reference pack gives a normal compiler.
Either:

- probe facades at import time, cached: try `Assembly.Load("System.Runtime").GetType(fullName)`
  (`Assembly.GetType` follows type forwarders, so a miss means some other facade owns it),
  then fall back to the dotted prefixes of the type's full name (`System.Threading.Thread`
  is both the type and its facade); or
- build the map once from the shared-framework directory's exported types.

Then route IL emission through a `ReferenceImporter` subclass that applies it — the plumbing
for that is mechanical (`_module.DefaultImporter` is read-only, so every import site swaps to
a per-module importer; ~120 call sites across `IlEmitter*.cs`, `IlAsyncEmitter.cs`, and
`AsmResolverTypeMapper.cs`).

## Current mitigation

`zs generate-project` does not reference cached package assemblies at all — it compiles every
ZScheme dependency from source into the generated C#. That sidesteps the problem entirely and
has the side benefit of making each generated tree self-contained and readable, at the cost of
re-emitting a dependency's modules per consuming project. See the `generate-project` section
of `docs/COMPILER-PIPELINE.md`.

Nothing else consumes these assemblies from C# today, so the impact is confined to that one
decision — but it is the reason for it, and any future "just reference the cached dll" change
will run straight into CS0012.
