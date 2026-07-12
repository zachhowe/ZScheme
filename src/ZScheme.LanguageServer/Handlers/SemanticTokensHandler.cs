using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using ZScheme.Compiler.Ast;
using ZScheme.Compiler.Diagnostics;
using ZScheme.Compiler.Syntax;
using ZScheme.Compiler.Types;
using ZScheme.LanguageServer.Analysis;
using SymbolKind = ZScheme.LanguageServer.Analysis.SymbolKind;

namespace ZScheme.LanguageServer.Handlers;

public sealed class SemanticTokensHandler(AnalysisService analysisService)
    : SemanticTokensHandlerBase
{
    /// <summary>A classified token, 0-based, single-line. The seam type tests assert
    ///     on without going through OmniSharp's delta encoding.</summary>
    public sealed record SemToken(
        int Line,
        int Char,
        int Length,
        SemanticTokenType Type,
        bool Declaration
    );

    private static readonly SemanticTokensLegend Legend = new()
    {
        TokenTypes = new Container<SemanticTokenType>(
            SemanticTokenType.Type,
            SemanticTokenType.Class,
            SemanticTokenType.Interface,
            SemanticTokenType.Enum,
            SemanticTokenType.EnumMember,
            SemanticTokenType.TypeParameter,
            SemanticTokenType.Parameter,
            SemanticTokenType.Variable,
            SemanticTokenType.Function,
            SemanticTokenType.Keyword,
            SemanticTokenType.Comment,
            SemanticTokenType.String,
            SemanticTokenType.Number,
            SemanticTokenType.Namespace
        ),
        TokenModifiers = new Container<SemanticTokenModifier>(SemanticTokenModifier.Declaration),
    };

    /// <summary>The special forms of <c>AstBuilder.BuildList</c>'s dispatch switch,
    ///     highlighted as keywords when they appear in head position.</summary>
    private static readonly HashSet<string> SpecialForms =
    [
        "define", "let", "let*", "use", "use*", "if", "lambda", "match",
        "define-record", "define-struct", "define-union", "partial", "import-clr",
        "define-type-alias", "namespace", "module", "import", "export", "object",
        "begin", "new", "typeof", "raise", "define-async", "await", "define-class",
        "define-interface", "with-handlers", "with", "set!", "values", "quote",
    ];

    protected override SemanticTokensRegistrationOptions CreateRegistrationOptions(
        SemanticTokensCapability capability,
        ClientCapabilities clientCapabilities
    )
    {
        return new SemanticTokensRegistrationOptions
        {
            DocumentSelector = new TextDocumentSelector(
                TextDocumentFilter.ForLanguage("zscheme"),
                TextDocumentFilter.ForPattern("**/*.zs"),
                TextDocumentFilter.ForPattern("**/*.zspkg")
            ),
            Legend = Legend,
            Full = true,
            Range = false,
        };
    }

    protected override Task<SemanticTokensDocument> GetSemanticTokensDocument(
        ITextDocumentIdentifierParams @params,
        CancellationToken cancellationToken
    )
    {
        return Task.FromResult(new SemanticTokensDocument(Legend));
    }

    protected override Task Tokenize(
        SemanticTokensBuilder builder,
        ITextDocumentIdentifierParams identifier,
        CancellationToken cancellationToken
    )
    {
        var state = analysisService.GetDocument(identifier.TextDocument.Uri.ToString());
        if (state is null)
            return Task.CompletedTask;

        foreach (var token in ComputeTokens(state, analysisService.Index))
            builder.Push(
                token.Line,
                token.Char,
                token.Length,
                token.Type,
                token.Declaration ? [SemanticTokenModifier.Declaration] : Array.Empty<SemanticTokenModifier>()
            );

        return Task.CompletedTask;
    }

    /// <summary>
    ///     Classifies the document in three layers merged by start position (later
    ///     layers win): lexical (comments, literals, head-position special forms),
    ///     type positions (annotations and known type names — type spans don't survive
    ///     into <see cref="ZType" />), and the typed AST's name occurrences.
    /// </summary>
    public static IReadOnlyList<SemToken> ComputeTokens(
        DocumentState state,
        WorkspaceIndex? index = null
    )
    {
        var result = new Dictionary<(int Line, int Char), SemToken>();
        var tokens = LexicalStructure.Tokens(state.Source);

        void Add(SourceSpan span, SemanticTokenType type, bool declaration = false)
        {
            if (span.Length > 0)
                result[(span.Line - 1, span.Column - 1)] = new SemToken(
                    span.Line - 1,
                    span.Column - 1,
                    span.Length,
                    type,
                    declaration
                );
        }

        AddLexicalLayer(state.Source, tokens, result, Add);
        AddTypeNameLayer(state, index, tokens, Add);
        AddSemanticLayer(state, index, Add);

        return result.Values.OrderBy(t => t.Line).ThenBy(t => t.Char).ToList();
    }

    private static void AddLexicalLayer(
        string source,
        IReadOnlyList<Token> tokens,
        Dictionary<(int Line, int Char), SemToken> result,
        Action<SourceSpan, SemanticTokenType, bool> add
    )
    {
        Token? previous = null;
        foreach (var token in tokens)
        {
            switch (token.Kind)
            {
                case TokenKind.Comment:
                    add(token.Span, SemanticTokenType.Comment, false);
                    break;
                case TokenKind.StringLit:
                    AddStringSegments(source, token, result);
                    break;
                case TokenKind.IntLit or TokenKind.FloatLit:
                    add(token.Span, SemanticTokenType.Number, false);
                    break;
                case TokenKind.BoolLit or TokenKind.NullLit:
                    add(token.Span, SemanticTokenType.Keyword, false);
                    break;
                case TokenKind.Symbol when
                    previous?.Kind == TokenKind.LParen && SpecialForms.Contains(token.Text):
                    add(token.Span, SemanticTokenType.Keyword, false);
                    break;
                case TokenKind.Symbol when token.Text.StartsWith("#:", StringComparison.Ordinal):
                    add(token.Span, SemanticTokenType.Keyword, false);
                    break;
                case TokenKind.Symbol when token.Text.Length > 1 && token.Text[0] == '^':
                    add(token.Span, SemanticTokenType.TypeParameter, false);
                    break;
            }

            previous = token;
        }
    }

    /// <summary>A string literal's raw extent must be rescanned (span length is the
    ///     unescaped value length) and split per line — semantic tokens are single-line.</summary>
    private static void AddStringSegments(
        string source,
        Token token,
        Dictionary<(int Line, int Char), SemToken> result
    )
    {
        var start = SourceText.OffsetAt(source, token.Span.Line - 1, token.Span.Column - 1);
        var end = LexicalStructure.StringEndOffset(source, start);
        var segmentStart = start;
        for (var i = start; i < end; i++)
            if (source[i] == '\n' || i == end - 1)
            {
                var segmentEnd = source[i] == '\n' ? i : i + 1;
                var (line, character) = SourceText.PositionAt(source, segmentStart);
                if (segmentEnd > segmentStart)
                    result[(line, character)] = new SemToken(
                        line,
                        character,
                        segmentEnd - segmentStart,
                        SemanticTokenType.String,
                        false
                    );
                segmentStart = i + 1;
            }
    }

    private static void AddTypeNameLayer(
        DocumentState state,
        WorkspaceIndex? index,
        IReadOnlyList<Token> tokens,
        Action<SourceSpan, SemanticTokenType, bool> add
    )
    {
        var typeKinds = TypeNameKinds(state, index);
        Token? previous = null;
        foreach (var token in tokens)
        {
            if (token.Kind == TokenKind.Symbol)
            {
                if (typeKinds.TryGetValue(token.Text, out var kind))
                    add(token.Span, kind, false);
                else if (previous?.Kind == TokenKind.Colon)
                    // Annotation position (`[x : Foo]`) for a type we can't resolve
                    // (builtin, generic, imported-but-unindexed): still a type.
                    add(token.Span, SemanticTokenType.Type, false);
            }

            previous = token;
        }
    }

    /// <summary>Known type names, from same-file symbols first, then the workspace
    ///     index (bare names of type-kind definitions).</summary>
    private static Dictionary<string, SemanticTokenType> TypeNameKinds(
        DocumentState state,
        WorkspaceIndex? index
    )
    {
        var kinds = new Dictionary<string, SemanticTokenType>(StringComparer.Ordinal);
        if (index is not null)
            foreach (var candidate in index.CompletionCandidates("", int.MaxValue))
                if (TypeKindToken(candidate.Kind) is { } token)
                    kinds[candidate.BareName] = token;

        foreach (var symbol in state.Symbols)
            if (TypeKindToken(symbol.Kind) is { } token)
                kinds[symbol.Name] = token;

        return kinds;
    }

    private static SemanticTokenType? TypeKindToken(SymbolKind kind)
    {
        return kind switch
        {
            SymbolKind.Record => SemanticTokenType.Type,
            SymbolKind.TypeAlias => SemanticTokenType.Type,
            SymbolKind.Union => SemanticTokenType.Enum,
            SymbolKind.Class => SemanticTokenType.Class,
            SymbolKind.Interface => SemanticTokenType.Interface,
            _ => (SemanticTokenType?)null,
        };
    }

    private static void AddSemanticLayer(
        DocumentState state,
        WorkspaceIndex? index,
        Action<SourceSpan, SemanticTokenType, bool> add
    )
    {
        if (state.Ast is null)
            return;

        var symbolsByName = new Dictionary<string, SymbolInfo>(StringComparer.Ordinal);
        foreach (var symbol in state.Symbols)
            symbolsByName.TryAdd(symbol.Name, symbol);

        foreach (var name in AstNavigation.AllNames(state.Ast))
        {
            // #: flags reach the AST as Name nodes in argument position; keep the
            // lexical layer's keyword classification for them.
            if (name.Value.StartsWith("#:", StringComparison.Ordinal))
                continue;
            var classified = Classify(name, state, symbolsByName, index);
            if (classified is var (type, definitionSpan))
                add(name.Span, type, name.Span == definitionSpan);
        }

        foreach (var match in AllMatches(state.Ast))
        foreach (var arm in match.Arms)
            AddPatternTokens(state.Source, arm.Pattern, add);
    }

    private static (SemanticTokenType Type, SourceSpan DefinitionSpan)? Classify(
        AstNode.Name name,
        DocumentState state,
        Dictionary<string, SymbolInfo> symbolsByName,
        WorkspaceIndex? index
    )
    {
        if (state.NameToDefinition.TryGetValue(name.Value, out var definition))
            return (KindToken(definition.Kind), definition.DefinitionSpan);
        if (symbolsByName.TryGetValue(name.Value, out var symbol))
            return (KindToken(symbol.Kind), symbol.DefinitionSpan);

        if (index?.ResolveDefinition(name.ResolvedQualifiedName, name.Value) is { Count: 1 } hits)
            return (KindToken(hits[0].Kind), hits[0].Span);

        var resolved = name.ResolvedType;
        if (resolved is ZType.ZForAllType forAll)
            resolved = forAll.Body;
        return resolved is ZType.ZFuncType
            ? (SemanticTokenType.Function, default)
            : (SemanticTokenType.Variable, default);
    }

    private static SemanticTokenType KindToken(SymbolKind kind)
    {
        return kind switch
        {
            SymbolKind.Function => SemanticTokenType.Function,
            SymbolKind.Parameter => SemanticTokenType.Parameter,
            SymbolKind.Variable => SemanticTokenType.Variable,
            SymbolKind.Record => SemanticTokenType.Type,
            SymbolKind.TypeAlias => SemanticTokenType.Type,
            SymbolKind.Union => SemanticTokenType.Enum,
            SymbolKind.UnionCase => SemanticTokenType.EnumMember,
            SymbolKind.Class => SemanticTokenType.Class,
            SymbolKind.Interface => SemanticTokenType.Interface,
            SymbolKind.Module => SemanticTokenType.Namespace,
            _ => SemanticTokenType.Variable,
        };
    }

    private static IEnumerable<AstNode.Match> AllMatches(AstNode node)
    {
        if (node is AstNode.Match match)
            yield return match;
        foreach (var child in AstNavigation.Children(node))
        foreach (var found in AllMatches(child))
            yield return found;
    }

    /// <summary>Match patterns aren't part of <see cref="AstNavigation.Children" />
    ///     (they're not expressions), so they're walked separately: constructor
    ///     patterns as enum members, variable patterns as variable declarations.</summary>
    private static void AddPatternTokens(
        string source,
        Pattern pattern,
        Action<SourceSpan, SemanticTokenType, bool> add
    )
    {
        switch (pattern)
        {
            case Pattern.Constructor constructor:
                // The span covers `(Some x)`; the name starts after the paren for the
                // parenthesized form, at the span start for a bare constructor.
                var offset = SourceText.OffsetAt(
                    source,
                    constructor.Span.Line - 1,
                    constructor.Span.Column - 1
                );
                var atParen = offset < source.Length && source[offset] == '(';
                add(
                    new SourceSpan(
                        constructor.Span.File,
                        constructor.Span.Line,
                        constructor.Span.Column + (atParen ? 1 : 0),
                        constructor.Name.Length
                    ),
                    SemanticTokenType.EnumMember,
                    false
                );
                foreach (var field in constructor.Fields)
                    AddPatternTokens(source, field, add);
                break;
            case Pattern.Variable variable:
                add(variable.Span, SemanticTokenType.Variable, true);
                break;
            case Pattern.Tuple tuple:
                foreach (var element in tuple.Elements)
                    AddPatternTokens(source, element, add);
                break;
        }
    }
}
