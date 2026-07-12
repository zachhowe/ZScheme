using System.Reflection;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;
using Xunit;
using ZScheme.Compiler.Codegen;
using ZScheme.Compiler.Diagnostics;
using ZScheme.Compiler.Types;

namespace ZScheme.Compiler.Tests.Codegen;

/// <summary>
///     Direct tests of <see cref="AsmResolverTypeMapper" /> (and its
///     <c>AsmResolverTypeFactory</c>), pinning the IL backend's signature construction —
///     especially the corlib scope rerouting exceptions that parity tests can't observe.
/// </summary>
public class AsmResolverTypeMapperTests
{
    private static ModuleDefinition NewModule()
    {
        var sysRuntimeAsm = Assembly.Load("System.Runtime");
        var corLib = new AssemblyReference("System.Runtime", sysRuntimeAsm.GetName().Version!)
        {
            PublicKeyOrToken = sysRuntimeAsm.GetName().GetPublicKeyToken(),
        };
        return new ModuleDefinition("AsmResolverTypeMapperTests.dll", corLib);
    }

    private static TypeSignature UnitType(ModuleDefinition module)
    {
        return module.DefaultImporter.ImportType(typeof(ValueTuple)).ToTypeSignature(true);
    }

    private static string ScopeNameOfGenericType(TypeSignature signature)
    {
        var gi = Assert.IsType<GenericInstanceTypeSignature>(signature);
        var tr = Assert.IsAssignableFrom<TypeReference>(gi.GenericType);
        Assert.NotNull(tr.Scope);
        return tr.Scope.Name!;
    }

    [Fact]
    public void UnitReturnTypeMapsToVoid()
    {
        var module = NewModule();
        var result = AsmResolverTypeMapper.MapReturnTypeToClr(ZType.Unit, module, UnitType(module));

        Assert.Equal("System.Void", result.FullName);
    }

    [Fact]
    public void UnitValueTypeMapsToProvidedUnitSignature()
    {
        var module = NewModule();
        var unit = UnitType(module);
        var result = AsmResolverTypeMapper.MapToClr(ZType.Unit, module, unit);

        Assert.Same(unit, result);
    }

    [Fact]
    public void NonUnitReturnTypeMapsLikeAnyValue()
    {
        var module = NewModule();
        var result = AsmResolverTypeMapper.MapReturnTypeToClr(ZType.Int, module, UnitType(module));

        Assert.Equal("System.Int32", result.FullName);
    }

    [Theory]
    [InlineData(PrimitiveKind.Int, "System.Int32")]
    [InlineData(PrimitiveKind.Long, "System.Int64")]
    [InlineData(PrimitiveKind.Float, "System.Single")]
    [InlineData(PrimitiveKind.Double, "System.Double")]
    [InlineData(PrimitiveKind.Byte, "System.Byte")]
    [InlineData(PrimitiveKind.Char, "System.Char")]
    [InlineData(PrimitiveKind.Bool, "System.Boolean")]
    [InlineData(PrimitiveKind.String, "System.String")]
    public void PrimitivesMapToCorlibSignatures(PrimitiveKind kind, string expectedFullName)
    {
        var module = NewModule();
        var result = AsmResolverTypeMapper.MapToClr(
            new ZType.ZPrimitiveType(kind),
            module,
            UnitType(module)
        );

        Assert.Equal(expectedFullName, result.FullName);
    }

    [Fact]
    public void SymbolMapsToImportedZSymbol()
    {
        var module = NewModule();
        var result = AsmResolverTypeMapper.MapToClr(ZType.Symbol, module, UnitType(module));

        Assert.Equal("ZScheme.Runtime.ZSymbol", result.FullName);
    }

    [Fact]
    public void TaskGenericIsReroutedToCorlibScope()
    {
        var module = NewModule();
        var result = AsmResolverTypeMapper.MapToClr(
            new ZType.ZNamedType("Task", [ZType.Int]),
            module,
            UnitType(module)
        );

        // Task<T> lives in System.Private.CoreLib but is forwarded through System.Runtime,
        // so the emitted reference must use the corlib scope.
        Assert.Equal("System.Runtime", ScopeNameOfGenericType(result));
    }

    [Fact]
    public void CollectionsGenericKeepsItsOriginalScope()
    {
        var module = NewModule();
        var result = AsmResolverTypeMapper.MapToClr(
            new ZType.ZNamedType("System.Collections.Generic.List", [ZType.Int]),
            module,
            UnitType(module)
        );

        // List<T> is forwarded through System.Collections, NOT System.Runtime, so it
        // must keep the scope the importer assigned rather than being rerouted.
        Assert.NotEqual("System.Runtime", ScopeNameOfGenericType(result));
    }

    [Fact]
    public void KeyValuePairIsTheCollectionsExceptionAndIsRerouted()
    {
        var module = NewModule();
        var reg = new TypeAliasRegistry();
        reg.RegisterBuiltIn(
            new TypeAliasInfo(
                "Pair",
                ["^k", "^v"],
                "System.Collections.Generic.KeyValuePair",
                "System.Collections.Generic",
                TypeAliasKind.GenericClrType,
                SourceSpan.None
            )
        );

        var result = AsmResolverTypeMapper.MapToClr(
            new ZType.ZNamedType("Pair", [ZType.String, ZType.Int]),
            module,
            UnitType(module),
            typeAliases: reg
        );

        // KeyValuePair<,> sits in System.Collections.Generic but is forwarded through
        // System.Runtime — the one namespace exception that must be rerouted.
        Assert.Equal("System.Runtime", ScopeNameOfGenericType(result));
    }

    [Fact]
    public void SzArrayAliasProducesSzArraySignature()
    {
        var module = NewModule();
        var reg = new TypeAliasRegistry();
        reg.RegisterBuiltIn(
            new TypeAliasInfo("Mutable-Vector", ["^a"], "", null, TypeAliasKind.SzArray, SourceSpan.None)
        );

        var result = AsmResolverTypeMapper.MapToClr(
            new ZType.ZNamedType("Mutable-Vector", [ZType.Int]),
            module,
            UnitType(module),
            typeAliases: reg
        );

        var arr = Assert.IsType<SzArrayTypeSignature>(result);
        Assert.Equal("System.Int32", arr.BaseType.FullName);
    }

    [Fact]
    public void TypeParamMapReturnsTheProvidedGenericParameterSignature()
    {
        var module = NewModule();
        var gps = new GenericParameterSignature(GenericParameterType.Type, 0);

        var result = AsmResolverTypeMapper.MapToClr(
            new ZType.ZNamedType("a", []),
            module,
            UnitType(module),
            typeParamMap: new Dictionary<string, TypeSignature> { ["a"] = gps }
        );

        Assert.Same(gps, result);
    }

    [Fact]
    public void ValueTupleClosesAsValueType()
    {
        var module = NewModule();
        var result = AsmResolverTypeMapper.MapToClr(
            new ZType.ZNamedType("ValueTuple", [ZType.Int, ZType.String]),
            module,
            UnitType(module)
        );

        var gi = Assert.IsType<GenericInstanceTypeSignature>(result);
        Assert.True(gi.IsValueType);
        Assert.Equal(2, gi.TypeArguments.Count);
        Assert.Equal("System.Int32", gi.TypeArguments[0].FullName);
    }

    [Fact]
    public void NullableOverValueTypeClosesNullable()
    {
        var module = NewModule();
        var result = AsmResolverTypeMapper.MapToClr(
            new ZType.ZNullableType(ZType.Int),
            module,
            UnitType(module)
        );

        var gi = Assert.IsType<GenericInstanceTypeSignature>(result);
        Assert.StartsWith("System.Nullable`1", gi.GenericType.FullName);
    }

    [Fact]
    public void NullableOverReferenceTypePassesInnerThrough()
    {
        var module = NewModule();
        var result = AsmResolverTypeMapper.MapToClr(
            new ZType.ZNullableType(ZType.String),
            module,
            UnitType(module)
        );

        Assert.Equal("System.String", result.FullName);
    }
}
