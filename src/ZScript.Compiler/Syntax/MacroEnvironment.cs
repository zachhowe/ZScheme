namespace ZScript.Compiler.Syntax;

using ZScript.Compiler.Diagnostics;

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
        var env = new MacroEnvironment();
        var span = SourceSpan.None;

        // Built-in macro: (test-case name body ...)
        // → (begin (@ Xunit.FactAttribute) (define (name) (begin body ...)))
        var testCaseRule = new MacroRule(
            new MacroPattern.PatList(
            [
                new MacroPattern.Literal("test-case", span),
                new MacroPattern.Variable("name", span),
                new MacroPattern.Ellipsis(new MacroPattern.Variable("body", span), span)
            ], span),
            new MacroTemplate.TList(
            [
                new MacroTemplate.Datum(
                    new SExpr.Atom(new Token(TokenKind.Symbol, "begin", span)), span),
                new MacroTemplate.TList(
                [
                    new MacroTemplate.Datum(
                        new SExpr.Atom(new Token(TokenKind.Symbol, "@", span)), span),
                    new MacroTemplate.Datum(
                        new SExpr.Atom(new Token(TokenKind.Symbol, "Xunit.FactAttribute", span)), span),
                ], span),
                new MacroTemplate.TList(
                [
                    new MacroTemplate.Datum(
                        new SExpr.Atom(new Token(TokenKind.Symbol, "define", span)), span),
                    new MacroTemplate.TList(
                    [
                        new MacroTemplate.Variable("name", span),
                    ], span),
                    new MacroTemplate.TList(
                    [
                        new MacroTemplate.Datum(
                            new SExpr.Atom(new Token(TokenKind.Symbol, "begin", span)), span),
                        new MacroTemplate.Ellipsis(new MacroTemplate.Variable("body", span), span),
                    ], span),
                ], span),
            ], span),
            span);

        env.Define("test-case", new MacroDefinition(
            "test-case", [], [testCaseRule], span));

        return env;
    }
}
