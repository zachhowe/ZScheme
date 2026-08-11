using Xunit;
using ZScheme.Compiler.Ir;
using ZScheme.Compiler.Types;

namespace ZScheme.Compiler.Tests.Ir;

public class ObjectLifterTests
{
    private static readonly ZType IFooType = new ZType.ZNamedType("IFoo", []);

    private static IrNode.Var V(string name, ZType? type = null) =>
        new(name) { Type = type ?? ZType.Int };

    private static IrNode.IntConst Int(int value) => new(value) { Type = ZType.Int };

    private static IrNode.ObjectExpr Obj(IrNode methodBody, IrConstructor? ctor = null, string? baseClass = null) =>
        new(
            ["IFoo"],
            [new IrObjectMethod("bar", [], ZType.Int, methodBody)],
            BaseClassName: baseClass,
            Constructor: ctor
        )
        {
            Type = IFooType,
        };

    private static IrNode.FuncDef Func(IrNode body, params IrParam[] parms) =>
        new("make", parms, IFooType, body, IsSelfRecursive: false) { Type = ZType.Unit };

    private static (IrNode.ClassDecl Class, IrNode.FuncDef Func) LiftSingle(IrNode.FuncDef fn)
    {
        var result = new ObjectLifter().Lift(new IrNode.Seq([fn]) { Type = ZType.Unit });
        var seq = Assert.IsType<IrNode.Seq>(result);
        Assert.Equal(2, seq.Nodes.Count);
        return (Assert.IsType<IrNode.ClassDecl>(seq.Nodes[0]), Assert.IsType<IrNode.FuncDef>(seq.Nodes[1]));
    }

    [Fact]
    public void CaptureFreeObjectBecomesClassDeclPlusClrNew()
    {
        var oe = Obj(Int(1));
        var (cls, fn) = LiftSingle(Func(oe));

        Assert.Equal("__Object_0", cls.Name);
        Assert.True(cls.IsObjectLifted);
        Assert.Equal(["IFoo"], cls.InterfaceNames);
        Assert.Empty(cls.Fields);
        Assert.NotNull(cls.Constructor);
        Assert.Empty(cls.Constructor.Params);

        var site = Assert.IsType<IrNode.ClrNew>(fn.Body);
        Assert.Equal("__Object_0", site.QualifiedTypeName);
        Assert.Empty(site.Args);
        Assert.Equal(IFooType, site.Type);
    }

    [Fact]
    public void ReferenceOnlyInsideANestedDefineIsCaptured()
    {
        // CollectFree had no LetRec arm, so a variable that only a nested definition's body
        // read was invisible to capture analysis: the synthesized class got no field for it,
        // its constructor took no argument, and the reference dangled — the C# backend emitted
        // a bare identifier and the IL backend failed with "Variable 'x' not found". Transform
        // had gained a LetRec case in the letrec cycle; this walker had not.
        var group = new IrNode.LetRec(
            [
                new IrNode.LetRecBinding(
                    "go",
                    new IrNode.FuncDef(
                        "go",
                        [new IrParam("k", ZType.Int)],
                        ZType.Int,
                        new IrNode.BinOp("+", V("x"), V("k")) { Type = ZType.Int },
                        IsSelfRecursive: false
                    )
                    {
                        Type = ZType.Int,
                    },
                    null
                ),
            ],
            new IrNode.Call(V("go"), [Int(1)]) { Type = ZType.Int }
        )
        {
            Type = ZType.Int,
        };

        var (cls, fn) = LiftSingle(Func(Obj(group), new IrParam("x", ZType.Int)));

        var field = Assert.Single(cls.Fields);
        Assert.Equal("x", field.Name);
        Assert.Equal("x", Assert.Single(cls.Constructor!.Params).Name);

        var site = Assert.IsType<IrNode.ClrNew>(fn.Body);
        Assert.Equal("x", Assert.IsType<IrNode.Var>(Assert.Single(site.Args)).Name);
    }

    [Fact]
    public void EnclosingParamReferenceIsCaptured()
    {
        var oe = Obj(V("x"));
        var (cls, fn) = LiftSingle(Func(oe, new IrParam("x", ZType.Int)));

        var field = Assert.Single(cls.Fields);
        Assert.Equal("x", field.Name);
        Assert.Equal(ZType.Int, field.Type);

        Assert.NotNull(cls.Constructor);
        var ctorParam = Assert.Single(cls.Constructor.Params);
        Assert.Equal("x", ctorParam.Name);
        var fieldSet = Assert.Single(cls.Constructor.FieldSets);
        Assert.Equal("x", fieldSet.FieldName);
        Assert.Equal("x", Assert.IsType<IrNode.Var>(fieldSet.Value).Name);

        var site = Assert.IsType<IrNode.ClrNew>(fn.Body);
        Assert.Equal("x", Assert.IsType<IrNode.Var>(Assert.Single(site.Args)).Name);
    }

    [Fact]
    public void UnboundGlobalIsNotCaptured()
    {
        // 'helper' is free in the object but not bound in the enclosing local scope,
        // so it stays a static reference and is not captured.
        var oe = Obj(new IrNode.Call(V("helper"), []) { Type = ZType.Int });
        var (cls, fn) = LiftSingle(Func(oe, new IrParam("x", ZType.Int)));

        Assert.Empty(cls.Fields);
        Assert.Empty(Assert.IsType<IrNode.ClrNew>(fn.Body).Args);
    }

    [Fact]
    public void NestedLetBindingIsCaptured()
    {
        var oe = Obj(V("y"));
        var body = new IrNode.Let("y", Int(1), oe) { Type = IFooType };
        var (cls, _) = LiftSingle(Func(body));

        Assert.Equal("y", Assert.Single(cls.Fields).Name);
    }

    [Fact]
    public void TopLevelLetOwnBindingIsNotCaptured()
    {
        // A top-level Let's own name is a module static, never captured.
        var oe = Obj(V("g"));
        var topLet = new IrNode.Let("g", oe, new IrNode.UnitConst()) { Type = ZType.Unit };
        var result = new ObjectLifter().Lift(new IrNode.Seq([topLet]) { Type = ZType.Unit });

        var seq = Assert.IsType<IrNode.Seq>(result);
        var cls = Assert.IsType<IrNode.ClassDecl>(seq.Nodes[0]);
        Assert.Empty(cls.Fields);
    }

    [Fact]
    public void MatchPatternVariableIsCaptured()
    {
        var oe = Obj(V("m"));
        var match = new IrNode.Match(
            V("x"),
            [new IrMatchArm(new IrPattern.Variable("m"), oe)]
        )
        {
            Type = IFooType,
        };
        var (cls, _) = LiftSingle(Func(match, new IrParam("x", ZType.Int)));

        // 'x' is also bound but unreferenced inside the object; only 'm' is captured.
        Assert.Equal("m", Assert.Single(cls.Fields).Name);
    }

    [Fact]
    public void TypeVarCaptureDefaultsFieldTypeToInt()
    {
        var oe = Obj(V("x", new ZType.ZTypeVar(1)));
        var (cls, _) = LiftSingle(Func(oe, new IrParam("x", new ZType.ZTypeVar(1))));

        var field = Assert.Single(cls.Fields);
        Assert.Equal(ZType.Int, field.Type);
    }

    [Fact]
    public void ExplicitConstructorIsMergedAfterCaptureFieldSets()
    {
        var explicitCtor = new IrConstructor(
            [],
            SuperArgs: [V("x")],
            FieldSets: [("f", Int(5))],
            BodyExprs: [Int(9)]
        );
        var oe = Obj(Int(1), explicitCtor, baseClass: "Base");
        var (cls, fn) = LiftSingle(Func(oe, new IrParam("x", ZType.Int)));

        Assert.Equal("Base", cls.BaseClassName);
        Assert.NotNull(cls.Constructor);

        // 'x' is referenced by the explicit ctor's super-args, so it is captured:
        // capture field-set first, then the explicit field-set.
        Assert.Equal("x", Assert.Single(cls.Constructor.Params).Name);
        Assert.Equal(2, cls.Constructor.FieldSets.Count);
        Assert.Equal("x", cls.Constructor.FieldSets[0].FieldName);
        Assert.Equal("f", cls.Constructor.FieldSets[1].FieldName);

        Assert.NotNull(cls.Constructor.SuperArgs);
        Assert.Equal("x", Assert.IsType<IrNode.Var>(Assert.Single(cls.Constructor.SuperArgs)).Name);
        Assert.Equal(9, Assert.IsType<IrNode.IntConst>(Assert.Single(cls.Constructor.BodyExprs)).Value);

        Assert.Equal("x", Assert.IsType<IrNode.Var>(Assert.Single(Assert.IsType<IrNode.ClrNew>(fn.Body).Args)).Name);
    }

    [Fact]
    public void NestedObjectsAreEmittedInnerFirst()
    {
        // The outer object claims __Object_0 (its name is assigned before its method
        // bodies are transformed); the inner one claims __Object_1 but is sunk first,
        // because the IL backend's type table is define-before-use.
        var inner = Obj(Int(1));
        var outer = Obj(inner);
        var result = new ObjectLifter().Lift(new IrNode.Seq([Func(outer)]) { Type = ZType.Unit });

        var seq = Assert.IsType<IrNode.Seq>(result);
        Assert.Equal(3, seq.Nodes.Count);
        Assert.Equal("__Object_1", Assert.IsType<IrNode.ClassDecl>(seq.Nodes[0]).Name);
        Assert.Equal("__Object_0", Assert.IsType<IrNode.ClassDecl>(seq.Nodes[1]).Name);

        var fn = Assert.IsType<IrNode.FuncDef>(seq.Nodes[2]);
        Assert.Equal("__Object_0", Assert.IsType<IrNode.ClrNew>(fn.Body).QualifiedTypeName);

        // The outer class's method body constructs the inner class.
        var outerCls = Assert.IsType<IrNode.ClassDecl>(seq.Nodes[1]);
        var methodBody = Assert.IsType<IrNode.ClrNew>(Assert.Single(outerCls.Methods).Body);
        Assert.Equal("__Object_1", methodBody.QualifiedTypeName);
    }

    [Fact]
    public void NonSeqProgramWithObjectIsWrappedInSeq()
    {
        var fn = Func(Obj(Int(1)));
        var result = new ObjectLifter().Lift(fn);

        var seq = Assert.IsType<IrNode.Seq>(result);
        Assert.Equal(2, seq.Nodes.Count);
        Assert.IsType<IrNode.ClassDecl>(seq.Nodes[0]);
    }

    [Fact]
    public void NonSeqProgramWithoutObjectIsReturnedUnwrapped()
    {
        var fn = Func(Int(1));
        var result = new ObjectLifter().Lift(fn);

        Assert.IsType<IrNode.FuncDef>(result);
    }
}
