using ZScheme.Compiler.Diagnostics;
using ZScheme.Compiler.Syntax;
using ZScheme.Compiler.Types;

namespace ZScheme.LanguageServer.Analysis;

/// <summary>
///     Reports a fully-qualified CLR type name whose namespace the same file already declares
///     with <c>(import-clr Ns …)</c>, so the short name would denote the identical type
///     (<see cref="DiagnosticCodes.RedundantTypeQualifier" />). The diagnostic spans only the
///     redundant <c>Ns.</c> characters, which makes the quick fix a plain deletion and lets
///     clients grey the prefix out rather than squiggle the whole name.
///     <para>
///         Editor-only by design: writing a type out in full is a style choice, not a defect, so
///         it has no business failing a build or filling CLI output. Nothing in the compiler
///         emits this code.
///     </para>
///     <para>
///         Only the file's <em>own</em> namespace hints count. The canonicalizer would also
///         accept one exported by an imported module, but then the justification for the short
///         spelling would be invisible here and an unrelated edit elsewhere could invalidate it.
///     </para>
/// </summary>
public sealed class RedundantTypeQualifierAnalyzer(DiagnosticBag diagnostics)
{
    /// <summary>
    ///     Bare names that <c>AstBuilder.ParseTypeExpr</c> maps to a
    ///     <see cref="ZType.ZPrimitiveType" /> rather than a named type. <c>Unifier</c> has no
    ///     primitive-to-named bridge, so rewriting <c>System.String</c> to <c>String</c> would
    ///     silently change the annotation's type even though both spellings resolve to the same
    ///     CLR type.
    /// </summary>
    private static readonly HashSet<string> PrimitiveNames = new(StringComparer.Ordinal)
    {
        "Int",
        "Long",
        "Float",
        "Double",
        "Byte",
        "Char",
        "Bool",
        "String",
        "Unit",
        "Symbol",
    };

    /// <param name="canonicalizer">
    ///     The canonicalizer the compilation used for this file
    ///     (<c>Compilation.Canonicalizer</c>) — not a fresh one. Its namespace set includes the
    ///     hints imported modules export, which is exactly what makes the equality test below
    ///     reject a short name that would bind to a same-named type in some other namespace.
    /// </param>
    public void Analyze(string source, string fileName, TypeNameCanonicalizer canonicalizer)
    {
        var scan = TypeNameScanner.Scan(LexicalStructure.Tokens(source, fileName));
        if (scan.ClrNamespaces.Count == 0)
            return;

        var namespaces = new Dictionary<string, Token>(StringComparer.Ordinal);
        foreach (var token in scan.ClrNamespaces)
            namespaces.TryAdd(token.Text, token);

        foreach (var (token, name, arity) in scan.TypeNames)
        {
            var dot = name.LastIndexOf('.');
            if (dot <= 0 || dot == name.Length - 1)
                continue;

            var prefix = name[..dot];
            var shortName = name[(dot + 1)..];
            if (PrimitiveNames.Contains(shortName))
                continue;
            if (!namespaces.TryGetValue(prefix, out var nsToken))
                continue;

            // The whole soundness argument. Canonical leaves a name alone when it is a
            // ZScheme-declared type, a registered alias, or unresolvable, and resolves a bare
            // name through the namespace hints in declaration order — so a shadowing ZScheme
            // type, an ambiguous simple name, System.Object/Task, and a namespace whose
            // assembly is missing all fail this test without any special-casing here.
            if (canonicalizer.Canonical(shortName, arity) != canonicalizer.Canonical(name, arity))
                continue;

            diagnostics.Hint(
                $"'{name}' can be written as '{shortName}'",
                new SourceSpan(fileName, token.Span.Line, token.Span.Column, prefix.Length + 1),
                DiagnosticCodes.RedundantTypeQualifier,
                [shortName, prefix],
                [new DiagnosticRelatedInfo(nsToken.Span, $"'{prefix}' is imported here")]
            );
        }
    }
}
