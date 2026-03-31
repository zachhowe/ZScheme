using System.Text.Json.Nodes;
using Xunit;
using ZScheme.Compiler.Cache;
using ZScheme.Compiler.Types;

namespace ZScheme.Compiler.Tests.Cache;

public sealed class ZTypeSerializerTests
{
    private static void AssertTypeRoundTrips(ZType type)
    {
        var json = ZTypeSerializer.Serialize(type);
        var result = ZTypeSerializer.Deserialize(json);
        Assert.Equal(type.ToString(), result.ToString());
    }

    [Fact]
    public void RoundTrip_PrimitiveType_Int()
    {
        AssertTypeRoundTrips(new ZType.ZPrimitiveType(PrimitiveKind.Int));
    }

    [Theory]
    [InlineData(PrimitiveKind.Int)]
    [InlineData(PrimitiveKind.Long)]
    [InlineData(PrimitiveKind.Float)]
    [InlineData(PrimitiveKind.Double)]
    [InlineData(PrimitiveKind.Byte)]
    [InlineData(PrimitiveKind.Char)]
    [InlineData(PrimitiveKind.Bool)]
    [InlineData(PrimitiveKind.String)]
    [InlineData(PrimitiveKind.Unit)]
    public void RoundTrip_AllPrimitiveKinds(PrimitiveKind kind)
    {
        AssertTypeRoundTrips(new ZType.ZPrimitiveType(kind));
    }

    [Fact]
    public void RoundTrip_TypeVar()
    {
        AssertTypeRoundTrips(new ZType.ZTypeVar(42));
    }

    [Fact]
    public void RoundTrip_FuncType()
    {
        AssertTypeRoundTrips(new ZType.ZFuncType(
            [ZType.Int, ZType.String],
            ZType.Bool));
    }

    [Fact]
    public void RoundTrip_NamedType_NoArgs()
    {
        AssertTypeRoundTrips(new ZType.ZNamedType("Unit", []));
    }

    [Fact]
    public void RoundTrip_NamedType_WithArgs()
    {
        AssertTypeRoundTrips(new ZType.ZNamedType("Option", [new ZType.ZTypeVar(1)]));
    }

    [Fact]
    public void RoundTrip_ForAllType()
    {
        AssertTypeRoundTrips(new ZType.ZForAllType(
            [1000, 1001],
            new ZType.ZFuncType(
                [new ZType.ZTypeVar(1000)],
                new ZType.ZTypeVar(1001))));
    }

    [Fact]
    public void RoundTrip_ConstrainedVar()
    {
        var type = new ZType.ZConstrainedVar(42,
            new HashSet<PrimitiveKind> { PrimitiveKind.Int, PrimitiveKind.Float });
        var json = ZTypeSerializer.Serialize(type);
        var result = ZTypeSerializer.Deserialize(json);

        var cv = Assert.IsType<ZType.ZConstrainedVar>(result);
        Assert.Equal(42, cv.Id);
        Assert.Contains(PrimitiveKind.Int, cv.AllowedKinds);
        Assert.Contains(PrimitiveKind.Float, cv.AllowedKinds);
        Assert.Equal(2, cv.AllowedKinds.Count);
    }

    [Fact]
    public void RoundTrip_NestedType_ListOfFuncs()
    {
        var funcType = new ZType.ZFuncType([ZType.Int], ZType.Bool);
        AssertTypeRoundTrips(new ZType.ZNamedType("List", [funcType]));
    }

    [Fact]
    public void RoundTrip_ForAllWithNamedBody()
    {
        AssertTypeRoundTrips(new ZType.ZForAllType(
            [1000],
            new ZType.ZNamedType("Option", [new ZType.ZTypeVar(1000)])));
    }

    [Fact]
    public void RoundTrip_VerifiesTypeStructure()
    {
        // Verify deeper structure, not just ToString
        var type = new ZType.ZFuncType([ZType.Int, ZType.String], ZType.Bool);
        var json = ZTypeSerializer.Serialize(type);
        var result = ZTypeSerializer.Deserialize(json);

        var fn = Assert.IsType<ZType.ZFuncType>(result);
        Assert.Equal(2, fn.Params.Count);
        Assert.IsType<ZType.ZPrimitiveType>(fn.Params[0]);
        Assert.IsType<ZType.ZPrimitiveType>(fn.Params[1]);
        Assert.IsType<ZType.ZPrimitiveType>(fn.Return);
    }

    [Fact]
    public void Serialize_PrimitiveType_CorrectJsonFormat()
    {
        var type = new ZType.ZPrimitiveType(PrimitiveKind.Int);
        var json = ZTypeSerializer.Serialize(type) as JsonObject;
        Assert.NotNull(json);
        Assert.Equal("primitive", json["kind"]!.GetValue<string>());
        Assert.Equal("Int", json["primitiveKind"]!.GetValue<string>());
    }

    [Fact]
    public void Deserialize_InvalidKind_Throws()
    {
        var json = new JsonObject { ["kind"] = "unknown" };
        Assert.Throws<ArgumentException>(() => ZTypeSerializer.Deserialize(json));
    }

    [Fact]
    public void Deserialize_MissingKind_Throws()
    {
        var json = new JsonObject { ["foo"] = "bar" };
        Assert.Throws<ArgumentException>(() => ZTypeSerializer.Deserialize(json));
    }
}
