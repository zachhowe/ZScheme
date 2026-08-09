# C# backend: CS0433 when two referenced assemblies export the same type name

## Symptom

Building the C# a package transpiles to fails when a shared framework ships the
same full type name in two assemblies:

```
ZScheme.AspNet.cs(565,16): error CS0433: The type 'LoggingBuilderExtensions' exists in both
'Microsoft.Extensions.Logging.Configuration, Version=10.0.0.0, …' and
'Microsoft.Extensions.Logging, Version=10.0.0.0, …'
```

Generated source:

```csharp
return Microsoft.Extensions.Logging.LoggingBuilderExtensions.ClearProviders(builder);
```

From `packages/logging/src/builder.zs`, whose import already names the assembly it
means:

```scheme
[clr-clear-providers LoggingBuilderExtensions/ClearProviders
  :from "Microsoft.Extensions.Logging"
  : (ILoggingBuilder -> ILoggingBuilder)]
```

The IL backend is immune: it imports a member reference bound to one specific
assembly, chosen at resolution time using the `:from` hint. The C# backend emits
a *name*, and C# name resolution sees two candidates.

The standalone `logging` package is unaffected — it references only the NuGet
`Microsoft.Extensions.Logging`. The ambiguity appears once
`Microsoft.AspNetCore.App` is in the reference set (directly, or implicitly via
`Microsoft.NET.Sdk.Web`), which is why only `aspnet` hits it.

## Current workaround

`GenerateProjectCommand.FrameworkAmbiguousAssemblies` holds a hardcoded map of
framework id → assemblies to hide, and `CSharpProjectGenerator` emits an MSBuild
target that gives each an `extern alias`, removing it from the global namespace:

```xml
<Target Name="ZsAliasAmbiguousReferences" AfterTargets="ResolveReferences">
  <ItemGroup>
    <ReferencePath Condition="'%(FileName)' == 'Microsoft.Extensions.Logging.Configuration'">
      <Aliases>zs_Microsoft_Extensions_Logging_Configuration</Aliases>
    </ReferencePath>
  </ItemGroup>
</Target>
```

This works because nothing the packages generate references the hidden
assembly. It is a list of known offenders, not a rule.

## The real fix

Drive aliasing from the resolution the compiler already performed, rather than a
list:

1. `CSharpEmitter` records, per CLR type name it spells out, the
   `ResolvedMethodInfo.DeclaringType.Assembly` it resolved through. That
   information is already on the IR node — `IrNode.ClrCall.ResolvedMethodInfo` —
   and is exactly what the `:from` hint disambiguated.
2. The emitted source qualifies such a type through an `extern alias` for its
   declaring assembly (`zs_Microsoft_Extensions_Logging::Microsoft.Extensions.Logging.LoggingBuilderExtensions`)
   whenever a second candidate exists, or the project generator aliases every
   *other* candidate away.
3. `CSharpProjectOptions.AliasedAssemblies` (already present) carries the result
   to the csproj; for shared-framework assemblies it still needs the
   `AfterTargets="ResolveReferences"` shape above, since those are not
   `<Reference>` items the generator authors.

The blocker for (1) is that detecting "a second candidate exists" needs a query
for every assembly in the reference closure exporting a given full type name.
`ClrInterop.ScanForMember` already walks that closure and even computes a
`firstMatch` distinct from the declaring type — that is the same ambiguity,
detected and then discarded.
