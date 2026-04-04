using Serilog;
using ZScheme.Compiler.Diagnostics;

namespace ZScheme.Compiler.Modules;

public sealed class ModuleGraph(DiagnosticBag diagnostics)
{
    private readonly Dictionary<string, List<(string Target, SourceSpan Span)>> _edges = new();

    public void AddModule(string name)
    {
        _edges.TryAdd(name, []);
        Log.Debug("ModuleGraph: registered module {ModuleName}", name);
    }

    public void AddDependency(string from, string to, SourceSpan span)
    {
        if (!_edges.ContainsKey(from))
            _edges[from] = [];
        _edges[from].Add((to, span));
        Log.Debug("ModuleGraph: dependency {From} -> {To}", from, to);
    }

    /// <summary>
    ///     Returns modules in topological order (dependencies first).
    ///     Reports an error if a cycle is detected.
    /// </summary>
    public List<string>? TopologicalSort()
    {
        var visited = new HashSet<string>();
        var visiting = new HashSet<string>();
        var result = new List<string>();

        foreach (var mod in _edges.Keys)
            if (!Visit(mod, default, visited, visiting, result))
            {
                Log.Debug("ModuleGraph: topological sort failed (cycle detected)");
                return null;
            }

        Log.Debug("ModuleGraph: topological sort produced {Count} modules: {Order}", result.Count,
            string.Join(" -> ", result));
        return result;
    }

    private bool Visit(string node, SourceSpan edgeSpan, HashSet<string> visited, HashSet<string> visiting,
        List<string> result)
    {
        if (visited.Contains(node))
            return true;

        if (!visiting.Add(node))
        {
            diagnostics.Error($"Circular module dependency involving '{node}'", edgeSpan);
            return false;
        }

        if (_edges.TryGetValue(node, out var deps))
            foreach (var (target, span) in deps)
                if (!Visit(target, span, visited, visiting, result))
                    return false;

        visiting.Remove(node);
        visited.Add(node);
        result.Add(node);
        return true;
    }
}
