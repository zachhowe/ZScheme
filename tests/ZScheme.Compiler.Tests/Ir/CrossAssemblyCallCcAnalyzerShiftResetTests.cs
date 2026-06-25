using Xunit;
using ZScheme.Compiler.Diagnostics;
using ZScheme.Compiler.Ir;
using ZScheme.Compiler.Types;

namespace ZScheme.Compiler.Tests.Ir;

public class CrossAssemblyCallCcAnalyzerShiftResetTests
{
    private static IrNode.ClrCall Reset(IrNode body) =>
        new("ZScheme.Runtime.Runtime", "Reset", [body]) { Type = ZType.Int };

    private static IrNode.ClrCall Shift(IrNode body) =>
        new("ZScheme.Runtime.Runtime", "ShiftTyped", [body]) { Type = ZType.Int };

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
    public void Reports_WhenCallbackContainsShift()
    {
        // (define (cb x) (shift k …)) — passing cb to list/map is unsafe.
        var cb = Func(
            "cb",
            Shift(new IrNode.Var("k") { Type = ZType.Int }),
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
    public void Reports_WhenCallbackContainsReset()
    {
        // Reset alone (without shift) is also a taint source: a SaveContinuation could fly past
        // the prompt installed inside cb and reach the precompiled stack frames.
        var cb = Func(
            "cb",
            Reset(new IrNode.IntConst(1) { Type = ZType.Int }),
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
    }

    [Fact]
    public void Reports_TransitiveShiftTaint()
    {
        // (define (deep x) (shift k …))
        // (define (cb x) (deep x))   ; tainted via call to deep
        // (list/map cb xs)
        var deep = Func(
            "deep",
            Shift(new IrNode.Var("k") { Type = ZType.Int }),
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
    }

    [Fact]
    public void DoesNotReport_PureCallback()
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
    public void ErrorMessageMentionsAllThreeOperators()
    {
        var cb = Func(
            "cb",
            Shift(new IrNode.Var("k") { Type = ZType.Int }),
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
        Assert.Contains("call/cc", msg);
        Assert.Contains("shift", msg);
        Assert.Contains("reset", msg);
    }
}
