namespace ZScript.Compiler.Syntax;

public sealed class MacroEnvironment(MacroEnvironment? parent = null)
{
    private readonly Dictionary<string, MacroDefinition> _macros = new();

    public void Define(string name, MacroDefinition definition) =>
        _macros[name] = definition;

    public MacroDefinition? Lookup(string name) =>
        _macros.TryGetValue(name, out var def) ? def : parent?.Lookup(name);

    public IReadOnlyDictionary<string, MacroDefinition> OwnMacros => _macros;

    public static MacroEnvironment Default()
    {
        return new MacroEnvironment();
    }
}
