using Xunit;
using ZScheme.Compiler.Types;

namespace ZScheme.Compiler.Tests.Types;

public class ZTypeFormattingTests
{
    [Fact]
    public void TypeVar_AloneRendersAsCaretA()
    {
        Assert.Equal("^a", new ZType.ZTypeVar(0).ToString());
    }

    [Fact]
    public void TypeVar_NamingIsRelativeNotById()
    {
        // In isolation, any single var is the first slot — the raw Id does not leak.
        Assert.Equal("^a", new ZType.ZTypeVar(5).ToString());
    }

    [Fact]
    public void FuncType_TwoDistinctVars_RenderAsCaretACaretB()
    {
        var fn = new ZType.ZFuncType([new ZType.ZTypeVar(0)], new ZType.ZTypeVar(1));
        Assert.Equal("(^a -> ^b)", fn.ToString());
    }

    [Fact]
    public void FuncType_SameIdReusesSameName()
    {
        var fn = new ZType.ZFuncType(
            [new ZType.ZTypeVar(0), new ZType.ZTypeVar(1), new ZType.ZTypeVar(0)],
            new ZType.ZTypeVar(1));
        Assert.Equal("(^a ^b ^a -> ^b)", fn.ToString());
    }

    [Fact]
    public void ForAll_RendersBoundVarsWithCaretSyntax()
    {
        var body = new ZType.ZFuncType([new ZType.ZTypeVar(0)], new ZType.ZTypeVar(1));
        var forall = new ZType.ZForAllType([0, 1], body);
        Assert.Equal("forall ^a, ^b. (^a -> ^b)", forall.ToString());
    }

    [Fact]
    public void ForAll_SeedsBoundVarsInDeclarationOrder()
    {
        // Body mentions id 1 before id 0; bound-var declaration order should still
        // pin ^a to id 0 and ^b to id 1.
        var body = new ZType.ZFuncType([new ZType.ZTypeVar(1)], new ZType.ZTypeVar(0));
        var forall = new ZType.ZForAllType([0, 1], body);
        Assert.Equal("forall ^a, ^b. (^b -> ^a)", forall.ToString());
    }

    [Fact]
    public void NamedType_GenericArgsRenderWithCaretSyntax()
    {
        var arr = new ZType.ZNamedType("Vector", [new ZType.ZTypeVar(0)]);
        Assert.Equal("Vector<^a>", arr.ToString());
    }

    [Fact]
    public void NestedNamedTypes_ShareSingleNamingMap()
    {
        // ((Vector ^a) -> ^b) — same Id (0) appearing inside a nested type
        // must still resolve to ^a, not get renumbered.
        var arr = new ZType.ZNamedType("Vector", [new ZType.ZTypeVar(0)]);
        var fn = new ZType.ZFuncType([arr], new ZType.ZTypeVar(1));
        Assert.Equal("(Vector<^a> -> ^b)", fn.ToString());
    }

    [Fact]
    public void ConstrainedVar_RendersWithCaretSyntax()
    {
        var c = new ZType.ZConstrainedVar(
            0,
            new HashSet<PrimitiveKind> { PrimitiveKind.Int, PrimitiveKind.Float });
        Assert.Equal("^a:{Int|Float}", c.ToString());
    }

    [Fact]
    public void NullableType_RendersInnerWithCaretSyntax()
    {
        var nu = new ZType.ZNullableType(new ZType.ZTypeVar(0));
        Assert.Equal("^a?", nu.ToString());
    }

    [Fact]
    public void Tuple_RendersWithCaretSyntax()
    {
        var t = ZType.Tuple(new ZType.ZTypeVar(0), new ZType.ZTypeVar(1), new ZType.ZTypeVar(0));
        Assert.Equal("(^a * ^b * ^a)", t.ToString());
    }

    [Fact]
    public void TwentySeventhDistinctVar_RendersWithSuffix()
    {
        var args = new List<ZType>();
        for (var i = 0; i < 27; i++)
            args.Add(new ZType.ZTypeVar(i));
        var named = new ZType.ZNamedType("Box", args);
        // 26th index (0-based) → ^a1
        Assert.EndsWith(", ^z, ^a1>", named.ToString());
    }

    [Fact]
    public void NoLegacyPlaceholders()
    {
        var fn = new ZType.ZFuncType([new ZType.ZTypeVar(0)], new ZType.ZTypeVar(1));
        var forall = new ZType.ZForAllType([0, 1], fn);
        var s = forall.ToString();
        Assert.DoesNotContain("?", s);
        Assert.DoesNotContain("t0", s);
        Assert.DoesNotContain("t1", s);
    }
}
