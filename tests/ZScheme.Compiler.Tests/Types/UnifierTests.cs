using Xunit;
using ZScheme.Compiler.Diagnostics;
using ZScheme.Compiler.Types;

namespace ZScheme.Compiler.Tests.Types;

public class UnifierTests
{
    private static (Unifier unifier, Substitution subst, DiagnosticBag diag) Create()
    {
        var subst = new Substitution();
        var diag = new DiagnosticBag();
        var unifier = new Unifier(subst, diag);
        return (unifier, subst, diag);
    }

    [Fact]
    public void UnifySameType()
    {
        var (unifier, _, diag) = Create();
        Assert.True(unifier.Unify(ZType.Int, ZType.Int, SourceSpan.None));
        Assert.False(diag.HasErrors);
    }

    [Fact]
    public void UnifyDifferentPrimitives_Fails()
    {
        var (unifier, _, diag) = Create();
        Assert.False(unifier.Unify(ZType.Int, ZType.Bool, SourceSpan.None));
        Assert.True(diag.HasErrors);
    }

    [Fact]
    public void UnifyTypeVarWithConcrete()
    {
        var (unifier, subst, diag) = Create();
        var tv = new ZType.ZTypeVar(0);
        Assert.True(unifier.Unify(tv, ZType.Int, SourceSpan.None));
        Assert.False(diag.HasErrors);
        Assert.Equal(ZType.Int, subst.Apply(tv));
    }

    [Fact]
    public void UnifyConcreteWithTypeVar()
    {
        var (unifier, subst, diag) = Create();
        var tv = new ZType.ZTypeVar(0);
        Assert.True(unifier.Unify(ZType.String, tv, SourceSpan.None));
        Assert.Equal(ZType.String, subst.Apply(tv));
    }

    [Fact]
    public void UnifyTwoTypeVars()
    {
        var (unifier, subst, _) = Create();
        var t0 = new ZType.ZTypeVar(0);
        var t1 = new ZType.ZTypeVar(1);
        Assert.True(unifier.Unify(t0, t1, SourceSpan.None));
        // After unifying, both should resolve to the same type
        Assert.Equal(subst.Apply(t0), subst.Apply(t1));
    }

    [Fact]
    public void UnifyFunctionTypes()
    {
        var (unifier, subst, diag) = Create();
        var t0 = new ZType.ZTypeVar(0);
        var f1 = new ZType.ZFuncType([ZType.Int], t0);
        var f2 = new ZType.ZFuncType([ZType.Int], ZType.Bool);
        Assert.True(unifier.Unify(f1, f2, SourceSpan.None));
        Assert.Equal(ZType.Bool, subst.Apply(t0));
    }

    [Fact]
    public void UnifyFunctionArityMismatch_Fails()
    {
        var (unifier, _, diag) = Create();
        var f1 = new ZType.ZFuncType([ZType.Int], ZType.Bool);
        var f2 = new ZType.ZFuncType([ZType.Int, ZType.Int], ZType.Bool);
        Assert.False(unifier.Unify(f1, f2, SourceSpan.None));
        Assert.True(diag.HasErrors);
    }

    [Fact]
    public void UnifyNamedTypes()
    {
        var (unifier, subst, diag) = Create();
        var tv = new ZType.ZTypeVar(0);
        var opt1 = new ZType.ZNamedType("Option", [tv]);
        var opt2 = new ZType.ZNamedType("Option", [ZType.Int]);
        Assert.True(unifier.Unify(opt1, opt2, SourceSpan.None));
        Assert.Equal(ZType.Int, subst.Apply(tv));
    }

    [Fact]
    public void UnifyDifferentNamedTypes_Fails()
    {
        var (unifier, _, diag) = Create();
        var t1 = new ZType.ZNamedType("List", [ZType.Int]);
        var t2 = new ZType.ZNamedType("Array", [ZType.Int]);
        Assert.False(unifier.Unify(t1, t2, SourceSpan.None));
        Assert.True(diag.HasErrors);
    }

    [Fact]
    public void OccursCheck_Fails()
    {
        var (unifier, _, diag) = Create();
        var tv = new ZType.ZTypeVar(0);
        var recursive = new ZType.ZFuncType([tv], ZType.Int);
        Assert.False(unifier.Unify(tv, recursive, SourceSpan.None));
        Assert.True(diag.HasErrors);
    }

    [Fact]
    public void TransitiveUnification()
    {
        var (unifier, subst, _) = Create();
        var t0 = new ZType.ZTypeVar(0);
        var t1 = new ZType.ZTypeVar(1);
        unifier.Unify(t0, t1, SourceSpan.None);
        unifier.Unify(t1, ZType.Float, SourceSpan.None);
        Assert.Equal(ZType.Float, subst.Apply(t0));
        Assert.Equal(ZType.Float, subst.Apply(t1));
    }

    [Fact]
    public void UnifyClrSubtype_SubtypeToSupertype()
    {
        var (unifier, _, diag) = Create();
        var sub = new ZType.ZNamedType("System.IO.MemoryStream", []);
        var super_ = new ZType.ZNamedType("System.IO.Stream", []);
        Assert.True(unifier.Unify(sub, super_, SourceSpan.None));
        Assert.False(diag.HasErrors);
    }

    [Fact]
    public void UnifyClrSubtype_SupertypeToSubtype()
    {
        var (unifier, _, diag) = Create();
        var super_ = new ZType.ZNamedType("System.IO.Stream", []);
        var sub = new ZType.ZNamedType("System.IO.MemoryStream", []);
        Assert.True(unifier.Unify(super_, sub, SourceSpan.None));
        Assert.False(diag.HasErrors);
    }

    [Fact]
    public void UnifyClrSubtype_UnrelatedTypes_Fails()
    {
        var (unifier, _, diag) = Create();
        var a = new ZType.ZNamedType("System.IO.Stream", []);
        var b = new ZType.ZNamedType("System.Text.StringBuilder", []);
        Assert.False(unifier.Unify(a, b, SourceSpan.None));
        Assert.True(diag.HasErrors);
    }
}
