namespace ZScheme.Fuzzer.Generation;

public sealed class Scope
{
    private readonly Dictionary<string, ExprType> _bindings;

    public Scope() { _bindings = new Dictionary<string, ExprType>(); }
    private Scope(Dictionary<string, ExprType> bindings) { _bindings = bindings; }

    public Scope Extend(string name, ExprType type)
    {
        var copy = new Dictionary<string, ExprType>(_bindings) { [name] = type };
        return new Scope(copy);
    }

    public List<string> GetVars(ExprType type)
    {
        var result = new List<string>();
        foreach (var (k, v) in _bindings)
            if (v == type) result.Add(k);
        return result;
    }

    public bool HasVarOf(ExprType type)
    {
        foreach (var v in _bindings.Values)
            if (v == type) return true;
        return false;
    }
}
