using Xunit;
using ZScheme.Compiler.Diagnostics;
using ZScheme.Compiler.Ir;
using ZScheme.Compiler.Types;

namespace ZScheme.Compiler.Tests.Ir;

public class CrossAssemblyCallCcAnalyzerTests
{
    private static IrNode.ClrCall CallCc(IrNode userFn) =>
        new("ZScheme.Runtime.Runtime", "CallCcTyped", [userFn]) { Type = ZType.Int };

    private static IrNode.FuncDef Func(string name, IrNode body, params IrParam[] @params) =>
        new(name, @params, ZType.Int, body, IsSelfRecursive: false) { Type = ZType.Int };

    private static IrNode.Call CallNamed(string name, params IrNode[] args) =>
        new(new IrNode.Var(name) { Type = ZType.Int }, args) { Type = ZType.Int };

    private static (DiagnosticBag Diagnostics, CrossAssemblyCallCcAnalyzer Analyzer) MakeAnalyzer(
        params string[] precompiledNames
    )
    {
        var diags = new DiagnosticBag();
        var analyzer = new CrossAssemblyCallCcAnalyzer(diags, precompiledNames);
        return (diags, analyzer);
    }

    [Fact]
    public void Reports_WhenNamedTaintedFuncPassedToPrecompiledHof()
    {
        // (define (cb x) (call/cc ...))
        // (list/map cb xs)
        var cb = Func(
            "cb",
            CallCc(new IrNode.Var("k") { Type = ZType.Int }),
            new IrParam("x", ZType.Int)
        );

        var caller = Func(
            "caller",
            CallNamed(
                "list/map",
                new IrNode.Var("cb") { Type = ZType.Int },
                new IrNode.Var("xs") { Type = ZType.Int }
            )
        );

        var program = new IrNode.Seq([cb, caller]);

        var (diags, analyzer) = MakeAnalyzer("list/map");
        analyzer.Analyze(program);

        Assert.True(diags.HasErrors);
        var msg = diags.Diagnostics[0].Message;
        Assert.Contains("'cb'", msg);
        Assert.Contains("'list/map'", msg);
    }

    [Fact]
    public void Reports_WhenClosureToTaintedFuncPassedToPrecompiledHof()
    {
        // Inline lambda is closure-converted into a top-level FuncDef + Closure ref.
        var lifted = Func(
            "__lifted_0",
            CallCc(new IrNode.Var("k") { Type = ZType.Int }),
            new IrParam("x", ZType.Int)
        );

        var caller = Func(
            "caller",
            CallNamed(
                "list/map",
                new IrNode.Closure("__lifted_0", []) { Type = ZType.Int },
                new IrNode.Var("xs") { Type = ZType.Int }
            )
        );

        var program = new IrNode.Seq([lifted, caller]);

        var (diags, analyzer) = MakeAnalyzer("list/map");
        analyzer.Analyze(program);

        Assert.True(diags.HasErrors);
        Assert.Contains("'__lifted_0'", diags.Diagnostics[0].Message);
    }

    [Fact]
    public void Reports_WhenTaintIsTransitive()
    {
        // (define (deep x) (call/cc ...))
        // (define (cb x) (deep x))      ; tainted via call to deep
        // (list/map cb xs)
        var deep = Func(
            "deep",
            CallCc(new IrNode.Var("k") { Type = ZType.Int }),
            new IrParam("x", ZType.Int)
        );

        var cb = Func(
            "cb",
            CallNamed("deep", new IrNode.Var("x") { Type = ZType.Int }),
            new IrParam("x", ZType.Int)
        );

        var caller = Func(
            "caller",
            CallNamed(
                "list/map",
                new IrNode.Var("cb") { Type = ZType.Int },
                new IrNode.Var("xs") { Type = ZType.Int }
            )
        );

        var program = new IrNode.Seq([deep, cb, caller]);

        var (diags, analyzer) = MakeAnalyzer("list/map");
        analyzer.Analyze(program);

        Assert.True(diags.HasErrors);
        Assert.Contains("'cb'", diags.Diagnostics[0].Message);
    }

    [Fact]
    public void DoesNotReport_WhenCallbackIsCallCcFree()
    {
        var cb = Func("cb", new IrNode.Var("x") { Type = ZType.Int }, new IrParam("x", ZType.Int));

        var caller = Func(
            "caller",
            CallNamed(
                "list/map",
                new IrNode.Var("cb") { Type = ZType.Int },
                new IrNode.Var("xs") { Type = ZType.Int }
            )
        );

        var program = new IrNode.Seq([cb, caller]);

        var (diags, analyzer) = MakeAnalyzer("list/map");
        analyzer.Analyze(program);

        Assert.False(diags.HasErrors);
    }

    [Fact]
    public void DoesNotReport_WhenCallCcUsedButNoPrecompiledHofInvolved()
    {
        // (define (uses-cc) (call/cc ...))
        // (define (caller) (uses-cc))   ; user-only call chain
        var ccFn = Func("uses-cc", CallCc(new IrNode.Var("k") { Type = ZType.Int }));

        var caller = Func("caller", CallNamed("uses-cc"));

        var program = new IrNode.Seq([ccFn, caller]);

        var (diags, analyzer) = MakeAnalyzer("list/map", "vector/fold");
        analyzer.Analyze(program);

        Assert.False(diags.HasErrors);
    }

    [Fact]
    public void DoesNotReport_OpaqueFunctionValuedArgument()
    {
        // (list/map (record/get rec :handler) xs) — handler reached via FieldGet, not a Var
        // referring to a known FuncDef. We deliberately don't fire here (documented
        // limitation) to avoid false positives on opaque function values.
        var fieldGet = new IrNode.FieldGet(new IrNode.Var("rec") { Type = ZType.Int }, "handler")
        {
            Type = ZType.Int,
        };

        // Even if some callcc-using user function exists in the program, we shouldn't
        // attribute it to the FieldGet.
        var ccFn = Func("uses-cc", CallCc(new IrNode.Var("k") { Type = ZType.Int }));

        var caller = Func(
            "caller",
            CallNamed("list/map", fieldGet, new IrNode.Var("xs") { Type = ZType.Int })
        );

        var program = new IrNode.Seq([ccFn, caller]);

        var (diags, analyzer) = MakeAnalyzer("list/map");
        analyzer.Analyze(program);

        Assert.False(diags.HasErrors);
    }

    [Fact]
    public void NoOp_WhenPrecompiledSetIsEmpty()
    {
        var cb = Func(
            "cb",
            CallCc(new IrNode.Var("k") { Type = ZType.Int }),
            new IrParam("x", ZType.Int)
        );

        var caller = Func(
            "caller",
            CallNamed(
                "list/map",
                new IrNode.Var("cb") { Type = ZType.Int },
                new IrNode.Var("xs") { Type = ZType.Int }
            )
        );

        var program = new IrNode.Seq([cb, caller]);

        var (diags, analyzer) = MakeAnalyzer();
        analyzer.Analyze(program);

        Assert.False(diags.HasErrors);
    }
}
