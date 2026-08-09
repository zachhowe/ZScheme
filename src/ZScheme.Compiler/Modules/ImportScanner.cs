using ZScheme.Compiler.Ast;
using ZScheme.Compiler.Diagnostics;
using ZScheme.Compiler.Syntax;

namespace ZScheme.Compiler.Modules;

/// <summary>
///     Reads the module names a source file imports, without compiling it. Used to order a
///     set of sibling files so each is compiled after the ones it depends on.
/// </summary>
public static class ImportScanner
{
    /// <summary>
    ///     Returns the module names imported by <paramref name="source" /> with the span of
    ///     each import form, including imports nested inside a <c>(module ...)</c> form. Names
    ///     are returned exactly as written — the caller resolves aliases. A file that fails to
    ///     lex or parse yields no imports: scanning is a scheduling aid, and the real compile
    ///     reports the syntax error.
    /// </summary>
    public static IReadOnlyList<(string Name, SourceSpan Span)> Scan(string source, string filePath)
    {
        var diagnostics = new DiagnosticBag();

        var tokens = new Lexer(source, filePath, diagnostics).Tokenize();
        if (diagnostics.HasErrors)
            return [];

        var sexprs = new SExprParser(tokens, diagnostics).ParseAll();
        if (diagnostics.HasErrors)
            return [];

        var program = new AstBuilder(diagnostics).BuildProgram(sexprs);

        return program
            .TopLevelForms.SelectMany(f =>
                f is AstNode.ModuleDecl m ? new[] { f }.Concat(m.Body) : [f]
            )
            .OfType<AstNode.Import>()
            .Select(i => (i.ModuleName, i.Span))
            .ToList();
    }
}
