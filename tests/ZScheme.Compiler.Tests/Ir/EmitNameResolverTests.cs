using Xunit;
using ZScheme.Compiler.Ir;
using ZScheme.Compiler.Types;

namespace ZScheme.Compiler.Tests.Ir;

public class EmitNameResolverTests
{
    private static IrNode.FuncDef Func(string name, IrNode body) =>
        new(name, [], ZType.Int, body, false);

    private static IrNode.IntConst Int(int v) => new(v) { Type = ZType.Int };

    [Fact]
    public void Resolve_CollidingFunctions_RenamesLaterClaimant()
    {
        var seq = new IrNode.Seq([Func("this-function", Int(1)), Func("ThisFunction", Int(2))])
        {
            Type = ZType.Unit,
        };

        var result = EmitNameResolver.Resolve("TestModule", seq, []);
        var defs = ((IrNode.Seq)result.CurrentIr).Nodes;

        // First claimant keeps its sanitized name (no EmitName), later one is renamed.
        Assert.Null(((IrNode.FuncDef)defs[0]).EmitName);
        Assert.Equal("ThisFunction_fn", ((IrNode.FuncDef)defs[1]).EmitName);
        Assert.Equal("ThisFunction_fn", result.ModuleRenames["TestModule"]["ThisFunction"]);
        Assert.False(result.ModuleRenames["TestModule"].ContainsKey("this-function"));
    }

    [Fact]
    public void Resolve_ReferenceToRenamedFunction_IsStamped()
    {
        var callBody = new IrNode.Call(new IrNode.Var("ThisFunction") { Type = ZType.Int }, [])
        {
            Type = ZType.Int,
        };
        var seq = new IrNode.Seq([
            Func("this-function", Int(1)),
            Func("ThisFunction", Int(2)),
            Func("compute", callBody),
        ])
        {
            Type = ZType.Unit,
        };

        var result = EmitNameResolver.Resolve("TestModule", seq, []);
        var compute = (IrNode.FuncDef)((IrNode.Seq)result.CurrentIr).Nodes[2];
        var callee = (IrNode.Var)((IrNode.Call)compute.Body).Function;

        Assert.Equal("ThisFunction_fn", callee.EmitName);
        Assert.Equal("ThisFunction", callee.Name); // original name preserved
    }

    [Fact]
    public void Resolve_MainIsNeverRenamed_AndReservesItsName()
    {
        // `main` claims `Main`; a later `Main`-sanitizing function must move aside.
        var seq = new IrNode.Seq([Func("main", Int(0)), Func("Main", Int(1))])
        {
            Type = ZType.Unit,
        };

        var result = EmitNameResolver.Resolve("TestModule", seq, []);
        var defs = ((IrNode.Seq)result.CurrentIr).Nodes;

        Assert.Null(((IrNode.FuncDef)defs[0]).EmitName); // main stays Main
        Assert.Equal("Main_fn", ((IrNode.FuncDef)defs[1]).EmitName);
    }

    [Fact]
    public void Resolve_FunctionCollidingWithTypeName_IsRenamed()
    {
        // A record `Box` reserves the nested-type name `Box`; a function `box`
        // (same sanitized identifier) must be disambiguated (C# CS0102).
        var record = new IrNode.RecordDecl("Box", [], [new IrField("v", ZType.Int)]);
        var seq = new IrNode.Seq([record, Func("box", Int(1))]) { Type = ZType.Unit };

        var result = EmitNameResolver.Resolve("TestModule", seq, []);
        Assert.Equal("Box_fn", result.ModuleRenames["TestModule"]["box"]);
    }

    [Fact]
    public void Resolve_CollidingLocals_AreAlphaRenamedNotStampedOnDefs()
    {
        // `this-var` and `ThisVar` both sanitize to `thisVar`; the inner local is
        // alpha-renamed (its raw VarName changes), leaving top-level EmitName clean.
        var inner = new IrNode.Let(
            "ThisVar",
            Int(2),
            new IrNode.Var("ThisVar") { Type = ZType.Int }
        )
        {
            Type = ZType.Int,
        };
        var outer = new IrNode.Let("this-var", Int(1), inner) { Type = ZType.Int };
        var compute = Func("compute", outer);
        var seq = new IrNode.Seq([compute]) { Type = ZType.Unit };

        var result = EmitNameResolver.Resolve("TestModule", seq, []);
        var resolvedCompute = (IrNode.FuncDef)((IrNode.Seq)result.CurrentIr).Nodes[0];
        var resolvedOuter = (IrNode.Let)resolvedCompute.Body;
        var resolvedInner = (IrNode.Let)resolvedOuter.Body;

        Assert.Equal("this-var", resolvedOuter.VarName); // first keeps its name
        Assert.NotEqual("ThisVar", resolvedInner.VarName); // collider alpha-renamed
        // The in-body reference is rewritten to the same fresh raw name.
        Assert.Equal(resolvedInner.VarName, ((IrNode.Var)resolvedInner.Body).Name);
    }

    [Fact]
    public void Resolve_NoCollision_LeavesEverythingUnstamped()
    {
        var seq = new IrNode.Seq([Func("add", Int(1)), Func("mul", Int(2))]) { Type = ZType.Unit };

        var result = EmitNameResolver.Resolve("TestModule", seq, []);
        var defs = ((IrNode.Seq)result.CurrentIr).Nodes;

        Assert.Null(((IrNode.FuncDef)defs[0]).EmitName);
        Assert.Null(((IrNode.FuncDef)defs[1]).EmitName);
        Assert.Empty(result.ModuleRenames["TestModule"]);
    }

    private static IrNode.RecordDecl Record(string name) =>
        new(name, [], [new IrField("v", ZType.Int)]);

    [Fact]
    public void Resolve_CollidingRecords_RenamesLaterClaimant()
    {
        // `this-record` and `ThisRecord` both sanitize to `ThisRecord`; the later one moves.
        var seq = new IrNode.Seq([Record("this-record"), Record("ThisRecord")])
        {
            Type = ZType.Unit,
        };

        var result = EmitNameResolver.Resolve("TestModule", seq, []);
        var defs = ((IrNode.Seq)result.CurrentIr).Nodes;

        Assert.Null(((IrNode.RecordDecl)defs[0]).EmitName);
        Assert.Equal("ThisRecord_type", ((IrNode.RecordDecl)defs[1]).EmitName);
        Assert.Equal("ThisRecord_type", result.ModuleTypeRenames["TestModule"]["ThisRecord"]);
        Assert.False(result.ModuleTypeRenames["TestModule"].ContainsKey("this-record"));
    }

    [Fact]
    public void Resolve_CollidingUnionCases_RenamesLaterCase()
    {
        // Cases `my-case` and `MyCase` (in distinct unions) both sanitize to `MyCase`.
        var ua = new IrNode.UnionDecl("ua", [], [new IrUnionCase("my-case", [])]);
        var ub = new IrNode.UnionDecl("ub", [], [new IrUnionCase("MyCase", [])]);
        var seq = new IrNode.Seq([ua, ub]) { Type = ZType.Unit };

        var result = EmitNameResolver.Resolve("TestModule", seq, []);
        var defs = ((IrNode.Seq)result.CurrentIr).Nodes;

        Assert.Null(((IrNode.UnionDecl)defs[0]).Cases[0].EmitName);
        Assert.Equal("MyCase_type", ((IrNode.UnionDecl)defs[1]).Cases[0].EmitName);
        Assert.Equal("MyCase_type", result.ModuleTypeRenames["TestModule"]["MyCase"]);
    }

    [Fact]
    public void Resolve_TypeAndValueSameSourceName_GetDistinctEmitNames()
    {
        // A record and a function share the source name `Foo`. The type claims `Foo`; the
        // value yields with `_fn` — they are tracked in separate rename maps.
        var seq = new IrNode.Seq([Record("Foo"), Func("Foo", Int(1))]) { Type = ZType.Unit };

        var result = EmitNameResolver.Resolve("TestModule", seq, []);

        Assert.False(result.ModuleTypeRenames["TestModule"].ContainsKey("Foo")); // type kept base
        Assert.Equal("Foo_fn", result.ModuleRenames["TestModule"]["Foo"]); // value moved
        Assert.Null(((IrNode.RecordDecl)((IrNode.Seq)result.CurrentIr).Nodes[0]).EmitName);
        Assert.Equal("Foo_fn", ((IrNode.FuncDef)((IrNode.Seq)result.CurrentIr).Nodes[1]).EmitName);
    }

    [Fact]
    public void Resolve_RecordVsInterfaceCollision_RenamesLaterClaimant()
    {
        // A record `Box` and interface `box` both sanitize to `Box`; collision crosses kinds.
        var record = Record("Box");
        var iface = new IrNode.InterfaceDecl("box", [], [], []);
        var seq = new IrNode.Seq([record, iface]) { Type = ZType.Unit };

        var result = EmitNameResolver.Resolve("TestModule", seq, []);
        var defs = ((IrNode.Seq)result.CurrentIr).Nodes;

        Assert.Null(((IrNode.RecordDecl)defs[0]).EmitName);
        Assert.Equal("Box_type", ((IrNode.InterfaceDecl)defs[1]).EmitName);
    }

    [Fact]
    public void Resolve_HyphenatedAndPascalRecords_GetDistinctEmitNames()
    {
        // The everyday shape of the collision: two multi-word spellings of one concept.
        // `http-response` and `HttpResponse` are separate types that both sanitize to
        // `HttpResponse`, so the second declared has to move.
        var seq = new IrNode.Seq([Record("http-response"), Record("HttpResponse")])
        {
            Type = ZType.Unit,
        };

        var result = EmitNameResolver.Resolve("TestModule", seq, []);
        var defs = ((IrNode.Seq)result.CurrentIr).Nodes;

        Assert.Null(((IrNode.RecordDecl)defs[0]).EmitName);
        Assert.Equal("HttpResponse_type", ((IrNode.RecordDecl)defs[1]).EmitName);
        Assert.Equal("HttpResponse_type", result.ModuleTypeRenames["TestModule"]["HttpResponse"]);
    }

    [Fact]
    public void Resolve_ThreeCollidingTypes_TakeSuccessiveSuffixes()
    {
        // The suffix ladder keeps counting rather than reusing `_type` — three spellings of one
        // sanitized name is the smallest case that tells the two apart.
        var seq = new IrNode.Seq(
            [Record("http-response"), Record("HttpResponse"), Record("Http-Response")]
        )
        {
            Type = ZType.Unit,
        };

        var result = EmitNameResolver.Resolve("TestModule", seq, []);
        var defs = ((IrNode.Seq)result.CurrentIr).Nodes;

        Assert.Null(((IrNode.RecordDecl)defs[0]).EmitName);
        Assert.Equal("HttpResponse_type", ((IrNode.RecordDecl)defs[1]).EmitName);
        Assert.Equal("HttpResponse_type2", ((IrNode.RecordDecl)defs[2]).EmitName);
    }

    [Fact]
    public void Resolve_CollidingClasses_RenamesLaterClaimant()
    {
        var seq = new IrNode.Seq(
            [
                new IrNode.ClassDecl("http-response", [], [], [], [], false, null, null),
                new IrNode.ClassDecl("HttpResponse", [], [], [], [], false, null, null),
            ]
        )
        {
            Type = ZType.Unit,
        };

        var result = EmitNameResolver.Resolve("TestModule", seq, []);
        var defs = ((IrNode.Seq)result.CurrentIr).Nodes;

        Assert.Null(((IrNode.ClassDecl)defs[0]).EmitName);
        Assert.Equal("HttpResponse_type", ((IrNode.ClassDecl)defs[1]).EmitName);
    }

    [Fact]
    public void Resolve_NoTypeCollision_LeavesTypeEmitNamesNull()
    {
        var seq = new IrNode.Seq([Record("widget")]) { Type = ZType.Unit };

        var result = EmitNameResolver.Resolve("TestModule", seq, []);

        Assert.Null(((IrNode.RecordDecl)((IrNode.Seq)result.CurrentIr).Nodes[0]).EmitName);
        Assert.Empty(result.ModuleTypeRenames["TestModule"]);
    }
}
