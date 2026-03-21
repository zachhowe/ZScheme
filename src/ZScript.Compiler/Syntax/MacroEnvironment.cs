namespace ZScript.Compiler.Syntax;

public sealed class MacroEnvironment
{
    private readonly Dictionary<string, MacroDefinition> _macros = new();
    private readonly MacroEnvironment? _parent;

    public MacroEnvironment(MacroEnvironment? parent = null)
    {
        _parent = parent;
    }

    public void Define(string name, MacroDefinition definition) =>
        _macros[name] = definition;

    public MacroDefinition? Lookup(string name) =>
        _macros.TryGetValue(name, out var def) ? def : _parent?.Lookup(name);

    public IReadOnlyDictionary<string, MacroDefinition> OwnMacros => _macros;

    public static MacroEnvironment Default()
    {
        return new MacroEnvironment();
    }
}
