using Serilog;
using ZScript.Compiler.Diagnostics;

namespace ZScript.Compiler.Modules;

public sealed class ModuleGraph(DiagnosticBag diagnostics)
{
    private readonly Dictionary<string, HashSet<string>> _edges = new();

    public void AddModule(string name)
    {
        _edges.TryAdd(name, []);
        Log.Debug("ModuleGraph: registered module {ModuleName}", name);
    }

    public void AddDependency(string from, string to)
    {
        if (!_edges.ContainsKey(from))
            _edges[from] = [];
        _edges[from].Add(to);
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
            if (!Visit(mod, visited, visiting, result))
            {
                Log.Debug("ModuleGraph: topological sort failed (cycle detected)");
                return null;
            }

        Log.Debug("ModuleGraph: topological sort produced {Count} modules: {Order}", result.Count, string.Join(" -> ", result));
        return result;
    }

    private bool Visit(string node, HashSet<string> visited, HashSet<string> visiting, List<string> result)
    {
        if (visited.Contains(node))
            return true;

        if (!visiting.Add(node))
        {
            diagnostics.Error($"Circular module dependency involving '{node}'", SourceSpan.None);
            return false;
        }

        if (_edges.TryGetValue(node, out var deps))
            foreach (var dep in deps)
                if (!Visit(dep, visited, visiting, result))
                    return false;

        visiting.Remove(node);
        visited.Add(node);
        result.Add(node);
        return true;
    }
}
