namespace ZScript.Compiler.Modules;

using ZScript.Compiler.Diagnostics;

public sealed class ModuleGraph
{
    private readonly Dictionary<string, HashSet<string>> _edges = new();
    private readonly DiagnosticBag _diagnostics;

    public ModuleGraph(DiagnosticBag diagnostics)
    {
        _diagnostics = diagnostics;
    }

    public void AddModule(string name) =>
        _edges.TryAdd(name, []);

    public void AddDependency(string from, string to)
    {
        if (!_edges.ContainsKey(from))
            _edges[from] = [];
        _edges[from].Add(to);
    }

    /// <summary>
    /// Returns modules in topological order (dependencies first).
    /// Reports an error if a cycle is detected.
    /// </summary>
    public List<string>? TopologicalSort()
    {
        var visited = new HashSet<string>();
        var visiting = new HashSet<string>();
        var result = new List<string>();

        foreach (var mod in _edges.Keys)
        {
            if (!Visit(mod, visited, visiting, result))
                return null;
        }

        return result;
    }

    private bool Visit(string node, HashSet<string> visited, HashSet<string> visiting, List<string> result)
    {
        if (visited.Contains(node))
            return true;

        if (visiting.Contains(node))
        {
            _diagnostics.Error($"Circular module dependency involving '{node}'", SourceSpan.None);
            return false;
        }

        visiting.Add(node);

        if (_edges.TryGetValue(node, out var deps))
        {
            foreach (var dep in deps)
            {
                if (!Visit(dep, visited, visiting, result))
                    return false;
            }
        }

        visiting.Remove(node);
        visited.Add(node);
        result.Add(node);
        return true;
    }
}
