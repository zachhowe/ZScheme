using Newtonsoft.Json.Linq;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using ZScheme.Compiler.Ast;
using ZScheme.Compiler.Diagnostics;
using ZScheme.LanguageServer.Analysis;
using Diagnostic = OmniSharp.Extensions.LanguageServer.Protocol.Models.Diagnostic;
using Range = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;

namespace ZScheme.LanguageServer.Handlers;

/// <summary>
///     Quick fixes keyed off the diagnostic codes the compiler attaches
///     (<see cref="DiagnosticCodes" />): add the missing arms of a non-exhaustive match
///     (ZS0002, from the structured missing-case payload) and add a missing import for
///     an undefined variable that some indexed module exports (ZS0001).
/// </summary>
public sealed class CodeActionHandler(AnalysisService analysisService) : CodeActionHandlerBase
{
    private const string ArmBody = "(raise (new System.Exception \"TODO\"))";

    protected override CodeActionRegistrationOptions CreateRegistrationOptions(
        CodeActionCapability capability,
        ClientCapabilities clientCapabilities
    )
    {
        return new CodeActionRegistrationOptions
        {
            DocumentSelector = new TextDocumentSelector(
                TextDocumentFilter.ForLanguage("zscheme"),
                TextDocumentFilter.ForPattern("**/*.zs"),
                TextDocumentFilter.ForPattern("**/*.zspkg")
            ),
            CodeActionKinds = new Container<CodeActionKind>(CodeActionKind.QuickFix),
            ResolveProvider = false,
        };
    }

    public override Task<CommandOrCodeActionContainer?> Handle(
        CodeActionParams request,
        CancellationToken cancellationToken
    )
    {
        var uri = request.TextDocument.Uri.ToString();
        var state = analysisService.GetDocument(uri);
        if (state is null)
            return Task.FromResult<CommandOrCodeActionContainer?>(
                new CommandOrCodeActionContainer()
            );

        var actions = new List<CommandOrCodeAction>();
        foreach (var diagnostic in request.Context.Diagnostics)
        {
            if (diagnostic.Source != "zscheme")
                continue;

            var code = diagnostic.Code is { IsString: true } dc ? dc.String : null;
            switch (code)
            {
                case DiagnosticCodes.NonExhaustiveMatch:
                    AddMissingArmsAction(actions, request, state, diagnostic);
                    break;
                case DiagnosticCodes.UndefinedVariable:
                    AddImportActions(actions, request, state, diagnostic);
                    break;
                case DiagnosticCodes.UnusedBinding:
                    AddUnusedBindingActions(actions, request, state, diagnostic);
                    break;
            }
        }

        return Task.FromResult<CommandOrCodeActionContainer?>(
            new CommandOrCodeActionContainer(actions)
        );
    }

    public override Task<CodeAction> Handle(CodeAction request, CancellationToken cancellationToken)
    {
        return Task.FromResult(request);
    }

    private static void AddMissingArmsAction(
        List<CommandOrCodeAction> actions,
        CodeActionParams request,
        DocumentState state,
        Diagnostic diagnostic
    )
    {
        var missing = ReadData(diagnostic.Data);
        if (missing.Count == 0)
            return;

        var edit = BuildMissingArmsEdit(state, diagnostic.Range, missing);
        if (edit is null)
            return;

        var caseNames = string.Join(", ", missing.Select(m => m.Split('/')[0]));
        actions.Add(
            MakeQuickFix(
                $"Add missing match arms ({caseNames})",
                request,
                diagnostic,
                edit
            )
        );
    }

    private void AddImportActions(
        List<CommandOrCodeAction> actions,
        CodeActionParams request,
        DocumentState state,
        Diagnostic diagnostic
    )
    {
        var data = ReadData(diagnostic.Data);
        if (data.Count == 0)
            return;
        var name = data[0];

        var modules = analysisService
            .Index.ResolveDefinition(null, name)
            .Select(d => d.ContainerModule)
            .Where(m => !string.IsNullOrEmpty(m))
            .Select(m => m!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(m => m, StringComparer.Ordinal);

        foreach (var module in modules)
            actions.Add(
                MakeQuickFix(
                    $"Import '{name}' from {module}",
                    request,
                    diagnostic,
                    BuildImportEdit(state.Source, module)
                )
            );
    }

    private static void AddUnusedBindingActions(
        List<CommandOrCodeAction> actions,
        CodeActionParams request,
        DocumentState state,
        Diagnostic diagnostic
    )
    {
        var data = ReadData(diagnostic.Data);
        var name = data.Count > 0 ? data[0] : null;
        if (name is null)
            return;

        // Prefixing with underscore (the opt-out convention) is always safe — and the
        // only fix offered for `use`, where deleting the binding would change disposal.
        var start = diagnostic.Range.Start;
        actions.Add(
            MakeQuickFix(
                $"Prefix '{name}' with underscore",
                request,
                diagnostic,
                new TextEdit { Range = new Range(start, start), NewText = "_" }
            )
        );

        if (BuildRemoveUnusedBindingEdits(state, diagnostic.Range) is { } edits)
            actions.Add(
                new CommandOrCodeAction(
                    new CodeAction
                    {
                        Title = "Remove unused binding",
                        Kind = CodeActionKind.QuickFix,
                        Diagnostics = new Container<Diagnostic>(diagnostic),
                        Edit = new WorkspaceEdit
                        {
                            Changes = new Dictionary<DocumentUri, IEnumerable<TextEdit>>
                            {
                                [request.TextDocument.Uri] = edits,
                            },
                        },
                    }
                )
            );
    }

    /// <summary>
    ///     Edits that delete the unused binding of the plain <c>let</c> whose bound
    ///     name starts at the diagnostic range. When the bound value is pure (a
    ///     literal, name, or lambda) and the let has one body expression, the whole
    ///     form is replaced by that body; otherwise the form is rewritten to
    ///     <c>(begin value body…)</c> so the value's effects are preserved. Returns
    ///     null for <c>use</c> (deleting changes disposal), <c>let*</c> (the node
    ///     spans don't isolate one binding), or when the source can't be re-lexed
    ///     into the expected shape.
    /// </summary>
    public static IReadOnlyList<TextEdit>? BuildRemoveUnusedBindingEdits(
        DocumentState state,
        Range diagnosticRange
    )
    {
        if (state.Ast is null)
            return null;

        var line = diagnosticRange.Start.Line + 1;
        var column = diagnosticRange.Start.Character + 1;
        if (FindLetByNameSpan(state.Ast, line, column) is not { } let)
            return null;

        var source = state.Source;
        var tokens = LexicalStructure.Tokens(source);
        var form = FindBracketAt(LexicalStructure.BuildTree(tokens), let.Span);
        // Only a plain single-binding let has the (let ([x …]) …) shape; let* shares
        // one form span across its desugared Let nodes and is left to the underscore fix.
        if (form is null
            || form.AtomTokens.Count == 0
            || form.AtomTokens[0].Text != "let"
            || form.Children.Count == 0)
            return null;

        var bindings = form.Children[0];
        if (bindings.Children.Count != 1)
            return null;
        var binding = bindings.Children[0];

        var bindingsStart = TokenStartOffset(source, bindings.Open);
        var bindingsEnd = TokenEndOffset(source, bindings.Close);
        var formStart = TokenStartOffset(source, form.Open);
        var formEnd = TokenEndOffset(source, form.Close);

        // The bound value is the last item inside the binding bracket (after the name
        // and any `: Type` annotation) — an atom or a nested bracket.
        var (valueStart, valueEnd) = LastItemExtent(source, binding);
        if (valueEnd <= valueStart)
            return null;

        var bodyItems = form
            .AtomTokens.Where(t =>
                t.Kind != Compiler.Syntax.TokenKind.Comment
                && TokenStartOffset(source, t) > bindingsEnd
            )
            .Select(t => (Start: TokenStartOffset(source, t), End: TokenEndOffset(source, t)))
            .Concat(
                form.Children.Skip(1)
                    .Select(c =>
                        (
                            Start: TokenStartOffset(source, c.Open),
                            End: TokenEndOffset(source, c.Close)
                        )
                    )
            )
            .OrderBy(item => item.Start)
            .ToList();
        if (bodyItems.Count == 0)
            return null;

        var valueIsPure = let.Value
            is AstNode.IntLit
                or AstNode.FloatLit
                or AstNode.BoolLit
                or AstNode.StringLit
                or AstNode.SymbolLit
                or AstNode.NullLit
                or AstNode.UnitLit
                or AstNode.Name
                or AstNode.Lambda;

        if (valueIsPure && bodyItems.Count == 1)
            // Replace the whole form with its single body expression.
            return
            [
                new TextEdit
                {
                    Range = OffsetsToRange(source, formStart, formEnd),
                    NewText = source[bodyItems[0].Start..bodyItems[0].End],
                },
            ];

        // (let ([x value]) body…) → (begin value body…): keep the value's effects and
        // the body's evaluation order.
        var keyword = form.AtomTokens[0];
        return
        [
            new TextEdit
            {
                Range = OffsetsToRange(
                    source,
                    TokenStartOffset(source, keyword),
                    TokenEndOffset(source, keyword)
                ),
                NewText = "begin",
            },
            new TextEdit
            {
                Range = OffsetsToRange(source, bindingsStart, valueStart),
                NewText = "",
            },
            new TextEdit
            {
                Range = OffsetsToRange(source, valueEnd, bindingsEnd),
                NewText = "",
            },
        ];
    }

    private static AstNode.Let? FindLetByNameSpan(AstNode node, int line, int column)
    {
        if (
            node is AstNode.Let let
            && let.NameSpan.Line == line
            && let.NameSpan.Column == column
            && let.NameSpan.Length > 0
        )
            return let;

        foreach (var child in AstNavigation.Children(node))
        {
            var found = FindLetByNameSpan(child, line, column);
            if (found is not null)
                return found;
        }

        return null;
    }

    private static BracketNode? FindBracketAt(
        IReadOnlyList<BracketNode> nodes,
        Compiler.Diagnostics.SourceSpan span
    )
    {
        foreach (var node in nodes)
        {
            if (node.Open.Span.Line == span.Line && node.Open.Span.Column == span.Column)
                return node;
            if (FindBracketAt(node.Children, span) is { } nested)
                return nested;
        }

        return null;
    }

    /// <summary>Raw extent of the last item (atom or nested bracket) inside a bracket
    ///     node — items are position-ordered across the atom/child split.</summary>
    private static (int Start, int End) LastItemExtent(string source, BracketNode bracket)
    {
        var best = (Start: 0, End: 0);
        foreach (var atom in bracket.AtomTokens)
        {
            if (atom.Kind == Compiler.Syntax.TokenKind.Comment)
                continue;
            var start = TokenStartOffset(source, atom);
            if (start > best.Start)
                best = (start, TokenEndOffset(source, atom));
        }

        foreach (var child in bracket.Children)
        {
            var start = TokenStartOffset(source, child.Open);
            if (start > best.Start)
                best = (start, TokenEndOffset(source, child.Close));
        }

        return best;
    }

    private static int TokenStartOffset(string source, Compiler.Syntax.Token token)
    {
        return SourceText.OffsetAt(source, token.Span.Line - 1, token.Span.Column - 1);
    }

    private static int TokenEndOffset(string source, Compiler.Syntax.Token token)
    {
        if (token.Kind == Compiler.Syntax.TokenKind.StringLit)
            return LexicalStructure.StringEndOffset(source, TokenStartOffset(source, token));
        return TokenStartOffset(source, token) + token.Span.Length;
    }

    private static Range OffsetsToRange(string source, int start, int end)
    {
        var (startLine, startCharacter) = SourceText.PositionAt(source, start);
        var (endLine, endCharacter) = SourceText.PositionAt(source, end);
        return new Range(startLine, startCharacter, endLine, endCharacter);
    }

    private static CommandOrCodeAction MakeQuickFix(
        string title,
        CodeActionParams request,
        Diagnostic diagnostic,
        TextEdit edit
    )
    {
        return new CodeAction
        {
            Title = title,
            Kind = CodeActionKind.QuickFix,
            Diagnostics = new Container<Diagnostic>(diagnostic),
            IsPreferred = true,
            Edit = new WorkspaceEdit
            {
                Changes = new Dictionary<DocumentUri, IEnumerable<TextEdit>>
                {
                    [request.TextDocument.Uri] = [edit],
                },
            },
        };
    }

    private static IReadOnlyList<string> ReadData(JToken? data)
    {
        if (data is not JArray array)
            return [];
        return [.. array.Values<string>().Where(s => !string.IsNullOrEmpty(s)).Select(s => s!)];
    }

    /// <summary>
    ///     Builds the insertion that appends one arm per missing case after the match's
    ///     last existing arm, matching that arm's indentation. Missing cases arrive as
    ///     <c>"CaseName/Arity"</c> entries (the ZS0002 data convention); payload-carrying
    ///     cases get wildcard subpatterns, e.g. <c>[(Some _) …]</c>, payload-free cases
    ///     the bare-name form <c>[None …]</c> the stdlib uses.
    /// </summary>
    public static TextEdit? BuildMissingArmsEdit(
        DocumentState state,
        Range diagnosticRange,
        IReadOnlyList<string> missingCaseData
    )
    {
        var match = FindMatchNode(state, diagnosticRange);
        if (match is null || match.Arms.Count == 0)
            return null;

        var source = state.Source;
        var lastArm = match.Arms[^1];
        var armOffset = SourceText.OffsetAt(
            source,
            lastArm.Span.Line - 1,
            lastArm.Span.Column - 1
        );
        if (armOffset >= source.Length || source[armOffset] is not ('(' or '[' or '{'))
            return null;

        var insertOffset = SourceText.SkipBalanced(source, armOffset);
        if (insertOffset < 0)
            return null;

        var indent = new string(' ', Math.Max(0, lastArm.Span.Column - 1));
        var text = string.Concat(
            missingCaseData.Select(entry =>
            {
                var (caseName, arity) = ParseCaseEntry(entry);
                var pattern =
                    arity == 0
                        ? caseName
                        : $"({caseName} {string.Join(" ", Enumerable.Repeat("_", arity))})";
                return $"\n{indent}[{pattern} {ArmBody}]";
            })
        );

        var (line, character) = SourceText.PositionAt(source, insertOffset);
        var position = new Position(line, character);
        return new TextEdit { Range = new Range(position, position), NewText = text };
    }

    /// <summary>Inserts <c>(import …)</c> after the last existing top-level import,
    ///     else after the <c>(module …)</c> declaration, else at the top of the file.</summary>
    public static TextEdit BuildImportEdit(string source, string moduleName)
    {
        var lines = source.Split('\n');
        var insertLine = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (trimmed.StartsWith("(import ", StringComparison.Ordinal))
                insertLine = i + 1;
            else if (
                insertLine == 0
                && trimmed.StartsWith("(module", StringComparison.Ordinal)
            )
                insertLine = i + 1;
        }

        var position = new Position(insertLine, 0);
        return new TextEdit
        {
            Range = new Range(position, position),
            NewText = $"(import {moduleName})\n",
        };
    }

    private static (string Name, int Arity) ParseCaseEntry(string entry)
    {
        var slash = entry.LastIndexOf('/');
        if (slash < 0)
            return (entry, 0);
        return (
            entry[..slash],
            int.TryParse(entry[(slash + 1)..], out var arity) ? Math.Max(0, arity) : 0
        );
    }

    /// <summary>The match node the ZS0002 diagnostic was emitted for — its span start is
    ///     exactly the diagnostic's start (the diagnostic uses <c>match.Span</c>).</summary>
    private static AstNode.Match? FindMatchNode(DocumentState state, Range diagnosticRange)
    {
        if (state.Ast is null)
            return null;

        var line = diagnosticRange.Start.Line + 1;
        var column = diagnosticRange.Start.Character + 1;
        return FindMatch(state.Ast, line, column);
    }

    private static AstNode.Match? FindMatch(AstNode node, int line, int column)
    {
        if (node is AstNode.Match m && m.Span.Line == line && m.Span.Column == column)
            return m;

        foreach (var child in AstNavigation.Children(node))
        {
            var found = FindMatch(child, line, column);
            if (found is not null)
                return found;
        }

        return null;
    }
}
