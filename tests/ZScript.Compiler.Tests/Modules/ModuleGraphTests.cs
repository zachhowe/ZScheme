using Xunit;
using ZScript.Compiler.Diagnostics;
using ZScript.Compiler.Modules;

namespace ZScript.Compiler.Tests.Modules;

public class ModuleGraphTests
{
    [Fact]
    public void SingleModule_NoDeps_ReturnsIt()
    {
        var diag = new DiagnosticBag();
        var graph = new ModuleGraph(diag);
        graph.AddModule("main");

        var result = graph.TopologicalSort();

        Assert.NotNull(result);
        Assert.Single(result!);
        Assert.Equal("main", result[0]);
    }

    [Fact]
    public void LinearChain_ReturnsCorrectOrder()
    {
        var diag = new DiagnosticBag();
        var graph = new ModuleGraph(diag);
        graph.AddModule("a");
        graph.AddModule("b");
        graph.AddModule("c");
        graph.AddDependency("a", "b", SourceSpan.None);
        graph.AddDependency("b", "c", SourceSpan.None);

        var result = graph.TopologicalSort();

        Assert.NotNull(result);
        // c before b before a
        Assert.True(result!.IndexOf("c") < result.IndexOf("b"));
        Assert.True(result.IndexOf("b") < result.IndexOf("a"));
    }

    [Fact]
    public void DiamondDependency_HandledCorrectly()
    {
        var diag = new DiagnosticBag();
        var graph = new ModuleGraph(diag);
        graph.AddModule("a");
        graph.AddModule("b");
        graph.AddModule("c");
        graph.AddModule("d");
        graph.AddDependency("a", "b", SourceSpan.None);
        graph.AddDependency("a", "c", SourceSpan.None);
        graph.AddDependency("b", "d", SourceSpan.None);
        graph.AddDependency("c", "d", SourceSpan.None);

        var result = graph.TopologicalSort();

        Assert.NotNull(result);
        Assert.True(result!.IndexOf("d") < result.IndexOf("b"));
        Assert.True(result.IndexOf("d") < result.IndexOf("c"));
        Assert.True(result.IndexOf("b") < result.IndexOf("a"));
        Assert.True(result.IndexOf("c") < result.IndexOf("a"));
    }

    [Fact]
    public void CircularDependency_ReturnsNull_AndReportsError()
    {
        var diag = new DiagnosticBag();
        var graph = new ModuleGraph(diag);
        graph.AddModule("a");
        graph.AddModule("b");
        graph.AddDependency("a", "b", SourceSpan.None);
        graph.AddDependency("b", "a", SourceSpan.None);

        var result = graph.TopologicalSort();

        Assert.Null(result);
        Assert.True(diag.HasErrors);
        Assert.Contains(diag.Diagnostics, d => d.Message.Contains("Circular"));
    }

    [Fact]
    public void SelfDependency_DetectedAsCycle()
    {
        var diag = new DiagnosticBag();
        var graph = new ModuleGraph(diag);
        graph.AddModule("a");
        graph.AddDependency("a", "a", SourceSpan.None);

        var result = graph.TopologicalSort();

        Assert.Null(result);
        Assert.True(diag.HasErrors);
    }

    [Fact]
    public void EmptyGraph_ReturnsEmptyList()
    {
        var diag = new DiagnosticBag();
        var graph = new ModuleGraph(diag);

        var result = graph.TopologicalSort();

        Assert.NotNull(result);
        Assert.Empty(result!);
    }
}
