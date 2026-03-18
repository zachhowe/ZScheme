namespace ZScript.Compiler.Tests.Types;

using ZScript.Compiler.Types;
using Xunit;

public class SubstitutionTests
{
    [Fact]
    public void AddAndTryGet_RoundTrip()
    {
        var sub = new Substitution();
        sub.Add(1, ZType.Int);
        Assert.True(sub.TryGet(1, out var result));
        Assert.Equal(ZType.Int, result);
    }

    [Fact]
    public void Apply_UnboundTypeVar_ReturnsSameVar()
    {
        var sub = new Substitution();
        var tv = new ZType.ZTypeVar(42);
        var result = sub.Apply(tv);
        Assert.Equal(tv, result);
    }

    [Fact]
    public void Apply_BoundTypeVar_ReturnsResolvedType()
    {
        var sub = new Substitution();
        sub.Add(1, ZType.Bool);
        var result = sub.Apply(new ZType.ZTypeVar(1));
        Assert.Equal(ZType.Bool, result);
    }

    [Fact]
    public void Apply_ChasesTransitiveBindings()
    {
        var sub = new Substitution();
        sub.Add(1, new ZType.ZTypeVar(2));
        sub.Add(2, ZType.String);

        var result = sub.Apply(new ZType.ZTypeVar(1));
        Assert.Equal(ZType.String, result);
    }

    [Fact]
    public void Apply_FunctionType_SubstitutesParamsAndReturn()
    {
        var sub = new Substitution();
        sub.Add(1, ZType.Int);
        sub.Add(2, ZType.Bool);

        var funcType = new ZType.ZFuncType([new ZType.ZTypeVar(1)], new ZType.ZTypeVar(2));
        var result = sub.Apply(funcType);

        var ft = Assert.IsType<ZType.ZFuncType>(result);
        Assert.Equal(ZType.Int, ft.Params[0]);
        Assert.Equal(ZType.Bool, ft.Return);
    }

    [Fact]
    public void Apply_NamedType_SubstitutesTypeArgs()
    {
        var sub = new Substitution();
        sub.Add(1, ZType.Int);

        var named = new ZType.ZNamedType("List", [new ZType.ZTypeVar(1)]);
        var result = sub.Apply(named);

        var nt = Assert.IsType<ZType.ZNamedType>(result);
        Assert.Equal("List", nt.Name);
        Assert.Equal(ZType.Int, nt.TypeArgs[0]);
    }

    [Fact]
    public void Compose_MergesAndAppliesExistingToNew()
    {
        var sub1 = new Substitution();
        sub1.Add(1, ZType.Int);

        var sub2 = new Substitution();
        sub2.Add(2, new ZType.ZTypeVar(1)); // refers to var 1

        sub1.Compose(sub2);

        // var 2 should resolve to Int (because sub1 maps 1 -> Int)
        Assert.True(sub1.TryGet(2, out var resolved));
        Assert.Equal(ZType.Int, resolved);
    }

    [Fact]
    public void FreeVars_ReturnsTypeVarIds()
    {
        var fv = Substitution.FreeVars(new ZType.ZFuncType(
            [new ZType.ZTypeVar(1), new ZType.ZTypeVar(2)],
            new ZType.ZTypeVar(3)));

        Assert.Contains(1, fv);
        Assert.Contains(2, fv);
        Assert.Contains(3, fv);
    }

    [Fact]
    public void FreeVars_ForAllType_ExcludesBoundVars()
    {
        var forAll = new ZType.ZForAllType([1, 2],
            new ZType.ZFuncType([new ZType.ZTypeVar(1), new ZType.ZTypeVar(3)], new ZType.ZTypeVar(2)));

        var fv = Substitution.FreeVars(forAll);
        Assert.DoesNotContain(1, fv);
        Assert.DoesNotContain(2, fv);
        Assert.Contains(3, fv);
    }
}
