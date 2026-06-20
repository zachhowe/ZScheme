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
        var t2 = new ZType.ZNamedType("Vector", [ZType.Int]);
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

    [Fact]
    public void UnifyNonNullableToNullable_Succeeds()
    {
        var (unifier, _, diag) = Create();
        var floatType = ZType.Float;
        var nullableFloat = new ZType.ZNullableType(ZType.Float);
        Assert.True(unifier.Unify(floatType, nullableFloat, SourceSpan.None));
        Assert.False(diag.HasErrors);
    }

    [Fact]
    public void UnifyNullableToNonNullable_Succeeds()
    {
        var (unifier, _, diag) = Create();
        var nullableInt = new ZType.ZNullableType(ZType.Int);
        var intType = ZType.Int;
        Assert.True(unifier.Unify(nullableInt, intType, SourceSpan.None));
        Assert.False(diag.HasErrors);
    }

    [Fact]
    public void UnifyNullableToNullable_SameInner_Succeeds()
    {
        var (unifier, _, diag) = Create();
        var a = new ZType.ZNullableType(ZType.Float);
        var b = new ZType.ZNullableType(ZType.Float);
        Assert.True(unifier.Unify(a, b, SourceSpan.None));
        Assert.False(diag.HasErrors);
    }

    [Fact]
    public void UnifyNullableToNullable_DifferentInner_Fails()
    {
        var (unifier, _, diag) = Create();
        var a = new ZType.ZNullableType(ZType.Float);
        var b = new ZType.ZNullableType(ZType.Int);
        Assert.False(unifier.Unify(a, b, SourceSpan.None));
        Assert.True(diag.HasErrors);
    }

    [Fact]
    public void UnifyNonNullableToNullable_DifferentInner_Fails()
    {
        var (unifier, _, diag) = Create();
        Assert.False(
            unifier.Unify(ZType.Int, new ZType.ZNullableType(ZType.Float), SourceSpan.None)
        );
        Assert.True(diag.HasErrors);
    }

    [Fact]
    public void UnifyTypeVarWithNullable()
    {
        var (unifier, subst, diag) = Create();
        var tv = new ZType.ZTypeVar(0);
        var nullableFloat = new ZType.ZNullableType(ZType.Float);
        Assert.True(unifier.Unify(tv, nullableFloat, SourceSpan.None));
        Assert.False(diag.HasErrors);
        Assert.Equal(nullableFloat, subst.Apply(tv));
    }

    [Fact]
    public void UnifyClrSubtype_InterfaceImplementation_Succeeds()
    {
        var (unifier, _, diag) = Create();
        var type = new ZType.ZNamedType("System.String", []);
        var iface = new ZType.ZNamedType("System.IComparable", []);
        Assert.True(unifier.Unify(type, iface, SourceSpan.None));
        Assert.False(diag.HasErrors);
    }

    [Fact]
    public void UnifyClrSubtype_BogusTypeName_FailsGracefully()
    {
        var (unifier, _, diag) = Create();
        var a = new ZType.ZNamedType("NonExistent.BrokenType", []);
        var b = new ZType.ZNamedType("System.String", []);
        // Should fail without throwing an exception (try-catch guard in IsClrSubtype)
        Assert.False(unifier.Unify(a, b, SourceSpan.None));
        Assert.True(diag.HasErrors);
    }

    [Fact]
    public void UnifyDelegateTypeWithFuncType_Succeeds()
    {
        var (unifier, _, diag) = Create();
        var dt = new ZType.ZDelegateType("System.Action");
        var ft = new ZType.ZFuncType([], ZType.Unit);
        Assert.True(unifier.Unify(dt, ft, SourceSpan.None));
        Assert.False(diag.HasErrors);
    }

    [Fact]
    public void UnifyFuncTypeWithDelegateType_Succeeds()
    {
        var (unifier, _, diag) = Create();
        var ft = new ZType.ZFuncType([], ZType.Unit);
        var dt = new ZType.ZDelegateType("System.Action");
        Assert.True(unifier.Unify(ft, dt, SourceSpan.None));
        Assert.False(diag.HasErrors);
    }

    [Fact]
    public void UnifyDelegateTypeWithFuncType_MultiParam_Succeeds()
    {
        var (unifier, _, diag) = Create();
        var dt = new ZType.ZDelegateType("System.Func<int,int,int>");
        var ft = new ZType.ZFuncType([ZType.Int, ZType.Int], ZType.Int);
        Assert.True(unifier.Unify(dt, ft, SourceSpan.None));
        Assert.False(diag.HasErrors);
    }

    [Fact]
    public void UnifyTypeVarWithDelegateType_Succeeds()
    {
        var (unifier, subst, diag) = Create();
        var tv = new ZType.ZTypeVar(0);
        var dt = new ZType.ZDelegateType("System.Action");
        Assert.True(unifier.Unify(tv, dt, SourceSpan.None));
        Assert.False(diag.HasErrors);
        Assert.Equal(dt, subst.Apply(tv));
    }

    [Fact]
    public void UnifyDelegateTypeWithTypeVar_Succeeds()
    {
        var (unifier, subst, diag) = Create();
        var dt = new ZType.ZDelegateType("System.Action");
        var tv = new ZType.ZTypeVar(0);
        Assert.True(unifier.Unify(dt, tv, SourceSpan.None));
        Assert.False(diag.HasErrors);
        Assert.Equal(dt, subst.Apply(tv));
    }

    [Fact]
    public void UnifyTwoDelegateType_SameName_Succeeds()
    {
        var (unifier, _, diag) = Create();
        var dt1 = new ZType.ZDelegateType("System.Action");
        var dt2 = new ZType.ZDelegateType("System.Action");
        Assert.True(unifier.Unify(dt1, dt2, SourceSpan.None));
        Assert.False(diag.HasErrors);
    }

    [Fact]
    public void UnifyTwoDelegateType_DifferentName_Fails()
    {
        var (unifier, _, diag) = Create();
        var dt1 = new ZType.ZDelegateType("System.Action");
        var dt2 = new ZType.ZDelegateType("System.Func<int>");
        Assert.False(unifier.Unify(dt1, dt2, SourceSpan.None));
        Assert.True(diag.HasErrors);
    }

    [Fact]
    public void UnifyDelegateTypeWithFuncType_DifferentArity_Fails()
    {
        var (unifier, _, diag) = Create();
        // System.Action resolves to a real parameterless delegate; a 1-param function
        // does not match its Invoke arity, so unification must fail. This arity check
        // is what lets overload resolution pick Func<Task> over RequestDelegate.
        var dt = new ZType.ZDelegateType("System.Action");
        var ft = new ZType.ZFuncType([ZType.Int], ZType.Unit);
        Assert.False(unifier.Unify(dt, ft, SourceSpan.None));
        Assert.True(diag.HasErrors);
    }

    [Fact]
    public void UnifyDelegateTypeWithFuncType_UnresolvableName_StaysPermissive()
    {
        var (unifier, _, diag) = Create();
        // When the delegate type name cannot be resolved to a CLR type, unification
        // stays permissive rather than spuriously failing on unknown shapes.
        var dt = new ZType.ZDelegateType("Some.Unresolvable.Delegate");
        var ft = new ZType.ZFuncType([ZType.Int], ZType.Unit);
        Assert.True(unifier.Unify(dt, ft, SourceSpan.None));
        Assert.False(diag.HasErrors);
    }

    // Unifying a concrete delegate against a function whose return is an unbound type
    // variable must pin that variable to the delegate's Invoke return type. The arity
    // check alone (DelegateMatchesFunc) leaves the variable free, which later defaults
    // to `object` in codegen while the delegate's Invoke expects the value type —
    // producing IL that fails verification. Found by the fuzzer.
    [Fact]
    public void UnifyDelegateTypeWithFuncType_BindsUnboundReturnVarToInvokeReturn()
    {
        var (unifier, subst, diag) = Create();
        var ret = new ZType.ZTypeVar(0);
        var dt = new ZType.ZDelegateType("System.Func<int,int>");
        var ft = new ZType.ZFuncType([ZType.Int], ret);
        Assert.True(unifier.Unify(dt, ft, SourceSpan.None));
        Assert.False(diag.HasErrors);
        Assert.Equal(ZType.Int, subst.Apply(ret));
    }

    [Fact]
    public void UnifyFuncTypeWithDelegateType_BindsUnboundReturnVarToInvokeReturn()
    {
        var (unifier, subst, diag) = Create();
        var ret = new ZType.ZTypeVar(0);
        var ft = new ZType.ZFuncType([ZType.Int], ret);
        var dt = new ZType.ZDelegateType("System.Func<int,int>");
        Assert.True(unifier.Unify(ft, dt, SourceSpan.None));
        Assert.False(diag.HasErrors);
        Assert.Equal(ZType.Int, subst.Apply(ret));
    }

    [Fact]
    public void UnifyDelegateTypeWithFuncType_BindsUnboundParamVarToInvokeParam()
    {
        var (unifier, subst, diag) = Create();
        var param = new ZType.ZTypeVar(0);
        var dt = new ZType.ZDelegateType("System.Func<int,int>");
        var ft = new ZType.ZFuncType([param], ZType.Int);
        Assert.True(unifier.Unify(dt, ft, SourceSpan.None));
        Assert.False(diag.HasErrors);
        Assert.Equal(ZType.Int, subst.Apply(param));
    }

    // Concrete (already-bound) leaves are left untouched so the permissive alias-name
    // behavior is preserved: a function whose return is concrete still unifies even
    // though the leaf type is not compared against the delegate's Invoke return.
    [Fact]
    public void UnifyDelegateTypeWithFuncType_ConcreteReturn_StaysPermissive()
    {
        var (unifier, _, diag) = Create();
        var dt = new ZType.ZDelegateType("System.Func<int,int>");
        var ft = new ZType.ZFuncType([ZType.Int], ZType.Int);
        Assert.True(unifier.Unify(dt, ft, SourceSpan.None));
        Assert.False(diag.HasErrors);
    }
}
