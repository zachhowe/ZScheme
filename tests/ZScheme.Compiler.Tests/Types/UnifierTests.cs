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

    /// <summary>
    ///     A unifier wired the way the <see cref="TypeInferer" /> wires it in production: with CLR
    ///     namespace hints and the canonicalizer that resolves a short type name against them.
    ///     <see cref="Create" /> supplies neither, so it cannot exercise the short-name path.
    /// </summary>
    private static (Unifier unifier, Substitution subst, DiagnosticBag diag) CreateWithNamespaces(
        params string[] namespaces
    )
    {
        var subst = new Substitution();
        var diag = new DiagnosticBag();
        var canonicalizer = new TypeNameCanonicalizer(namespaces);
        var unifier = new Unifier(
            subst,
            diag,
            null,
            null,
            namespaces,
            name => canonicalizer.Canonical(name, 0)
        );
        return (unifier, subst, diag);
    }

    [Fact]
    public void UnifyShortAndFullyQualifiedName_Succeeds()
    {
        var (unifier, _, diag) = CreateWithNamespaces("System.Text");
        var shortForm = new ZType.ZNamedType("StringBuilder", []);
        var longForm = new ZType.ZNamedType("System.Text.StringBuilder", []);
        Assert.True(unifier.Unify(shortForm, longForm, SourceSpan.None));
        Assert.False(diag.HasErrors);
    }

    [Fact]
    public void UnifyFullyQualifiedAndShortName_Succeeds()
    {
        var (unifier, _, diag) = CreateWithNamespaces("System.Text");
        var longForm = new ZType.ZNamedType("System.Text.StringBuilder", []);
        var shortForm = new ZType.ZNamedType("StringBuilder", []);
        Assert.True(unifier.Unify(longForm, shortForm, SourceSpan.None));
        Assert.False(diag.HasErrors);
    }

    /// <summary>
    ///     Inside a generic argument the CLR-subtype fallback never runs — that path is gated on
    ///     <c>TypeArgs.Count == 0</c> — so only canonicalization can reconcile the two spellings
    ///     here.
    /// </summary>
    [Fact]
    public void UnifyShortAndFullyQualifiedName_InsideAGenericArgument_Succeeds()
    {
        var (unifier, _, diag) = CreateWithNamespaces("System.Text");
        var shortForm = new ZType.ZNamedType("Task", [new ZType.ZNamedType("StringBuilder", [])]);
        var longForm = new ZType.ZNamedType(
            "Task",
            [new ZType.ZNamedType("System.Text.StringBuilder", [])]
        );
        Assert.True(unifier.Unify(shortForm, longForm, SourceSpan.None));
        Assert.False(diag.HasErrors);
    }

    /// <summary>
    ///     A unifier for a compilation in which a ZScheme declaration owns
    ///     <paramref name="declaredName" />. <see cref="TypeInferer" /> supplies the same origin
    ///     lookup; <see cref="CreateWithNamespaces" /> supplies none, so there every name is fair
    ///     game for the namespace hints.
    /// </summary>
    private static (Unifier unifier, DiagnosticBag diag) CreateWithDeclaredType(
        string declaredName,
        string origin,
        params string[] namespaces
    )
    {
        var diag = new DiagnosticBag();
        var unifier = new Unifier(
            new Substitution(),
            diag,
            null,
            null,
            namespaces,
            null,
            name => name == declaredName ? origin : null
        );
        return (unifier, diag);
    }

    /// <summary>
    ///     stdlib's <c>List</c> and <c>System.Collections.Generic.List</c> genuinely are different
    ///     types, so rejecting this is correct — but rendered alone the message reads as a
    ///     canonicalization failure, the very thing namespace hints exist to prevent. It has to say
    ///     which side the ZScheme declaration owns, and where that declaration lives: <c>List</c>
    ///     arrives through the prelude, so a user never wrote it.
    /// </summary>
    [Fact]
    public void ShadowedZSchemeTypeName_MismatchNamesTheDeclarationThatOwnsIt()
    {
        var (unifier, diag) = CreateWithDeclaredType(
            "List",
            "stdlib/list",
            "System.Collections.Generic"
        );
        var zs = new ZType.ZNamedType("List", [ZType.Int]);
        var clr = new ZType.ZNamedType("System.Collections.Generic.List", [ZType.Int]);

        Assert.False(unifier.Unify(zs, clr, SourceSpan.None));
        var message = Assert.Single(diag.Diagnostics).Message;
        Assert.Contains("'List' is a ZScheme type declared in stdlib/list", message);
        Assert.Contains("not 'System.Collections.Generic.List'", message);
    }

    [Fact]
    public void ShadowedZSchemeTypeName_IsNamedWhicheverSideOfTheMismatchItIsOn()
    {
        var (unifier, diag) = CreateWithDeclaredType(
            "List",
            "stdlib/list",
            "System.Collections.Generic"
        );
        var clr = new ZType.ZNamedType("System.Collections.Generic.List", [ZType.Int]);
        var zs = new ZType.ZNamedType("List", [ZType.Int]);

        Assert.False(unifier.Unify(clr, zs, SourceSpan.None));
        Assert.Contains(
            "'List' is a ZScheme type declared in stdlib/list",
            Assert.Single(diag.Diagnostics).Message
        );
    }

    /// <summary>
    ///     At arity 0 the CLR-subtype fallback used to complete the short name through the
    ///     namespace hint, find the CLR type and <em>accept</em> the mismatch — the annotation type
    ///     checked and codegen then emitted the ZScheme record where the CLR type was required.
    /// </summary>
    [Fact]
    public void ZSchemeTypeShadowingAClrSimpleName_IsNotResolvedThroughANamespaceHint()
    {
        var (unifier, diag) = CreateWithDeclaredType("StringBuilder", "this file", "System.Text");
        var zs = new ZType.ZNamedType("StringBuilder", []);
        var clr = new ZType.ZNamedType("System.Text.StringBuilder", []);

        Assert.False(unifier.Unify(zs, clr, SourceSpan.None));
        Assert.Contains(
            "'StringBuilder' is a ZScheme type declared in this file",
            Assert.Single(diag.Diagnostics).Message
        );
    }

    /// <summary>Two names that merely fail to unify share no simple name, so the note — which only
    ///     ever explains shadowing — stays out of the message.</summary>
    [Fact]
    public void MismatchOfUnrelatedNames_CarriesNoShadowingNote()
    {
        var (unifier, diag) = CreateWithDeclaredType("Buffer", "this file", "System.IO");
        var declared = new ZType.ZNamedType("Buffer", []);
        var other = new ZType.ZNamedType("System.IO.Stream", []);

        Assert.False(unifier.Unify(declared, other, SourceSpan.None));
        Assert.DoesNotContain(
            "is a ZScheme type declared in",
            Assert.Single(diag.Diagnostics).Message
        );
    }

    [Fact]
    public void UnifyShortNamesOfUnrelatedTypes_StillFails()
    {
        var (unifier, _, diag) = CreateWithNamespaces("System.Text", "System.IO");
        var a = new ZType.ZNamedType("StringBuilder", []);
        var b = new ZType.ZNamedType("Stream", []);
        Assert.False(unifier.Unify(a, b, SourceSpan.None));
        Assert.True(diag.HasErrors);
    }

    [Fact]
    public void ZSchemeClassImplementingAnInterface_MatchesEitherSpelling()
    {
        var subst = new Substitution();
        var diag = new DiagnosticBag();
        var canonicalizer = new TypeNameCanonicalizer(["System"]);
        // The class declares the interface fully qualified; the use site says the short name.
        var unifier = new Unifier(
            subst,
            diag,
            null,
            className => className == "Greeter" ? ["System.IComparable"] : null,
            ["System"],
            name => canonicalizer.Canonical(name, 0)
        );

        var cls = new ZType.ZNamedType("Greeter", []);
        var iface = new ZType.ZNamedType("IComparable", []);
        Assert.True(unifier.Unify(cls, iface, SourceSpan.None));
        Assert.False(diag.HasErrors);
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
