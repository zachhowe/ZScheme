using Xunit;
using ZScheme.Compiler.Codegen;
using ZScheme.Compiler.Diagnostics;
using ZScheme.Compiler.Types;

namespace ZScheme.Compiler.Tests.Codegen;

/// <summary>
///     Direct tests of the shared <see cref="TypeMapperCore" /> traversal through a string-building
///     fake factory, so the decision logic (alias resolution, arity limits, fallbacks, warnings) is
///     pinned independently of either real backend. Parity between the two real backends is covered
///     by <see cref="TypeMapperParityTests" />.
/// </summary>
public class TypeMapperCoreTests
{
    /// <summary>No-logic fake per docs/MOCKS.md: canonical strings out, calls recorded.</summary>
    private sealed class FakeTypeFactory : ITypeFactory<string>
    {
        public List<string> Warnings { get; } = [];

        /// <summary>Mapped values treated as value types (configurable result, no logic).</summary>
        public HashSet<string> ValueTypes { get; } = [];

        public string Object => "object";

        public string Primitive(PrimitiveKind kind) => $"prim:{kind}";

        public bool IsValueType(string t) => ValueTypes.Contains(t);

        public bool IsGenericDefinition(string t) => t.StartsWith("open:");

        public string MakeArray(string element) => $"{element}[]";

        public string FromClrType(Type clrType, bool corLibAware) => $"clr:{clrType.FullName}";

        public string CloseClrGeneric(Type openClrType, string[] args) =>
            $"clr:{openClrType.FullName}<{string.Join(",", args)}>";

        public string CloseMappedGeneric(string openMapped, string[] args) =>
            $"{openMapped}<{string.Join(",", args)}>";

        public void Warn(string message) => Warnings.Add(message);

        public string Unmappable(ZType type)
        {
            Warn($"TypeMapper: Cannot map type '{type}' to CLR type, falling back to object");
            return Object;
        }
    }

    private static string Map(
        ZType type,
        FakeTypeFactory f,
        IReadOnlyDictionary<string, string>? userTypes = null,
        IReadOnlyDictionary<string, string>? typeParamMap = null,
        IReadOnlyDictionary<int, string>? typeVarMap = null,
        TypeAliasRegistry? typeAliases = null
    )
    {
        return TypeMapperCore.Map(
            type,
            f,
            userTypes,
            typeParamMap,
            typeVarMap,
            typeAliases,
            clrInterop: null
        );
    }

    private static TypeAliasRegistry ArrayAliasRegistry()
    {
        var reg = new TypeAliasRegistry();
        reg.RegisterBuiltIn(
            new TypeAliasInfo(
                "Mutable-Vector",
                ["^a"],
                "",
                null,
                TypeAliasKind.SzArray,
                SourceSpan.None
            )
        );
        return reg;
    }

    [Fact]
    public void PrimitivesDispatchToFactory()
    {
        var f = new FakeTypeFactory();
        Assert.Equal("prim:Int", Map(ZType.Int, f));
        Assert.Equal("prim:String", Map(ZType.String, f));
        Assert.Equal("prim:Unit", Map(ZType.Unit, f));
        Assert.Empty(f.Warnings);
    }

    [Fact]
    public void TypeVarMapResolvesInferenceVariables()
    {
        var f = new FakeTypeFactory();
        var typeVarMap = new Dictionary<int, string> { [3] = "T0" };

        Assert.Equal("T0", Map(new ZType.ZTypeVar(3), f, typeVarMap: typeVarMap));
        Assert.Equal(
            "T0",
            Map(
                new ZType.ZConstrainedVar(3, new HashSet<PrimitiveKind> { PrimitiveKind.Int }),
                f,
                typeVarMap: typeVarMap
            )
        );
    }

    [Fact]
    public void TypeParamMapResolvesZeroArgNamedTypes()
    {
        var f = new FakeTypeFactory();
        var typeParamMap = new Dictionary<string, string> { ["a"] = "TP" };

        Assert.Equal("TP", Map(new ZType.ZNamedType("a", []), f, typeParamMap: typeParamMap));
    }

    [Fact]
    public void ValueTupleClosesOverMappedArgs()
    {
        var f = new FakeTypeFactory();
        var result = Map(new ZType.ZNamedType("ValueTuple", [ZType.Int, ZType.String]), f);

        Assert.Equal("clr:System.ValueTuple`2<prim:Int,prim:String>", result);
    }

    [Fact]
    public void ValueTupleArityOverflowWarnsAndFallsBackToObject()
    {
        var f = new FakeTypeFactory();
        var eight = Enumerable.Repeat(ZType.Int, 8).ToList<ZType>();
        var result = Map(new ZType.ZNamedType("ValueTuple", eight), f);

        Assert.Equal("object", result);
        Assert.Contains(f.Warnings, w => w.Contains("exceeds maximum of 7"));
    }

    [Fact]
    public void TaskIsRecognizedWithoutRegistry()
    {
        var f = new FakeTypeFactory();
        Assert.Equal("clr:System.Threading.Tasks.Task", Map(new ZType.ZNamedType("Task", []), f));
        Assert.Equal(
            "clr:System.Threading.Tasks.Task`1<prim:Int>",
            Map(new ZType.ZNamedType("Task", [ZType.Int]), f)
        );
    }

    [Fact]
    public void SzArrayAliasMapsToArrayOfElement()
    {
        var f = new FakeTypeFactory();
        var result = Map(
            new ZType.ZNamedType("Mutable-Vector", [ZType.Int]),
            f,
            typeAliases: ArrayAliasRegistry()
        );

        Assert.Equal("prim:Int[]", result);
    }

    [Fact]
    public void AliasWithWrongArgCountWarnsAndFallsBackToObject()
    {
        var f = new FakeTypeFactory();
        var result = Map(
            new ZType.ZNamedType("Mutable-Vector", []),
            f,
            typeAliases: ArrayAliasRegistry()
        );

        Assert.Equal("object", result);
        Assert.Contains(f.Warnings, w => w.Contains("expects 1 type args, got 0"));
    }

    [Fact]
    public void GenericAliasResolvesTargetAndCloses()
    {
        var f = new FakeTypeFactory();
        var reg = new TypeAliasRegistry();
        reg.RegisterBuiltIn(
            new TypeAliasInfo(
                "List",
                ["^a"],
                "System.Collections.Immutable.ImmutableList",
                "System.Collections.Immutable",
                TypeAliasKind.GenericClrType,
                SourceSpan.None
            )
        );

        var result = Map(new ZType.ZNamedType("List", [ZType.Int]), f, typeAliases: reg);

        Assert.Equal("clr:System.Collections.Immutable.ImmutableList`1<prim:Int>", result);
    }

    [Fact]
    public void UserGenericDefinitionIsClosedOverMappedArgs()
    {
        var f = new FakeTypeFactory();
        var userTypes = new Dictionary<string, string> { ["Box"] = "open:Box" };

        var result = Map(new ZType.ZNamedType("Box", [ZType.Int]), f, userTypes);

        Assert.Equal("open:Box<prim:Int>", result);
    }

    [Fact]
    public void UserNonDefinitionIsReturnedAsIsEvenWithTypeArgs()
    {
        var f = new FakeTypeFactory();
        var userTypes = new Dictionary<string, string> { ["Plain"] = "def:Plain" };

        var result = Map(new ZType.ZNamedType("Plain", [ZType.Int]), f, userTypes);

        Assert.Equal("def:Plain", result);
    }

    [Fact]
    public void UnitReturningFuncsMapToActions()
    {
        var f = new FakeTypeFactory();
        Assert.Equal("clr:System.Action", Map(new ZType.ZFuncType([], ZType.Unit), f));
        Assert.Equal(
            "clr:System.Action`1<prim:Int>",
            Map(new ZType.ZFuncType([ZType.Int], ZType.Unit), f)
        );
        Assert.Equal(
            "clr:System.Action`4<prim:Int,prim:Int,prim:Int,prim:Int>",
            Map(new ZType.ZFuncType([ZType.Int, ZType.Int, ZType.Int, ZType.Int], ZType.Unit), f)
        );
    }

    [Fact]
    public void ActionArityOverflowWarnsAndFallsBackToObject()
    {
        var f = new FakeTypeFactory();
        var five = Enumerable.Repeat(ZType.Int, 5).ToList<ZType>();
        var result = Map(new ZType.ZFuncType(five, ZType.Unit), f);

        Assert.Equal("object", result);
        Assert.Contains(f.Warnings, w => w.Contains("exceeds maximum of 4"));
    }

    [Fact]
    public void ValueReturningFuncsMapToFuncs()
    {
        var f = new FakeTypeFactory();
        Assert.Equal("clr:System.Func`1<prim:Int>", Map(new ZType.ZFuncType([], ZType.Int), f));
        Assert.Equal(
            "clr:System.Func`2<prim:String,prim:Int>",
            Map(new ZType.ZFuncType([ZType.String], ZType.Int), f)
        );
    }

    [Fact]
    public void FuncArityOverflowWarnsAndFallsBackToObject()
    {
        var f = new FakeTypeFactory();
        var five = Enumerable.Repeat(ZType.Int, 5).ToList<ZType>();
        var result = Map(new ZType.ZFuncType(five, ZType.Int), f);

        Assert.Equal("object", result);
        Assert.Contains(f.Warnings, w => w.Contains("exceeds maximum of 5"));
    }

    [Fact]
    public void NullableOverValueTypeClosesNullable()
    {
        var f = new FakeTypeFactory();
        f.ValueTypes.Add("prim:Int");

        var result = Map(new ZType.ZNullableType(ZType.Int), f);

        Assert.Equal("clr:System.Nullable`1<prim:Int>", result);
    }

    [Fact]
    public void NullableOverReferenceTypePassesInnerThrough()
    {
        var f = new FakeTypeFactory();

        var result = Map(new ZType.ZNullableType(ZType.String), f);

        Assert.Equal("prim:String", result);
    }

    [Fact]
    public void ResolvableDelegateTypeIsImported()
    {
        var f = new FakeTypeFactory();
        var result = Map(new ZType.ZDelegateType("System.Action"), f);

        Assert.Equal("clr:System.Action", result);
        Assert.Empty(f.Warnings);
    }

    [Fact]
    public void UnresolvableDelegateWarnsAndFallsBackToObject()
    {
        var f = new FakeTypeFactory();
        var result = Map(new ZType.ZDelegateType("Totally.Bogus.Delegate"), f);

        Assert.Equal("object", result);
        Assert.Contains(f.Warnings, w => w.Contains("Cannot resolve delegate type"));
    }

    [Fact]
    public void NonDelegateTypeNameWarnsAndFallsBackToObject()
    {
        var f = new FakeTypeFactory();
        var result = Map(new ZType.ZDelegateType("System.String"), f);

        Assert.Equal("object", result);
        Assert.Contains(f.Warnings, w => w.Contains("is not a delegate type"));
    }

    [Fact]
    public void DotQualifiedClrNameResolvesDirectly()
    {
        var f = new FakeTypeFactory();
        Assert.Equal(
            "clr:System.Text.StringBuilder",
            Map(new ZType.ZNamedType("System.Text.StringBuilder", []), f)
        );
        Assert.Equal(
            "clr:System.Collections.Generic.List`1<prim:Int>",
            Map(new ZType.ZNamedType("System.Collections.Generic.List", [ZType.Int]), f)
        );
    }

    [Fact]
    public void UnknownNamedTypeWarnsAndFallsBackToObject()
    {
        var f = new FakeTypeFactory();
        Assert.Equal("object", Map(new ZType.ZNamedType("Totally.Bogus.Type", []), f));
        Assert.Equal("object", Map(new ZType.ZNamedType("Unknown", []), f));
        Assert.Equal(2, f.Warnings.Count(w => w.Contains("falling back to object")));
    }
}
