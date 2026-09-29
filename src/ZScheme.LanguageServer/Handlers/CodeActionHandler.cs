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
///     Quick fixes keyed off the diagnostic codes attached upstream
///     (<see cref="DiagnosticCodes" />): add the missing arms of a non-exhaustive match
///     (ZS0002, from the structured missing-case payload), add a missing import for
///     an undefined variable that some indexed module exports (ZS0001), silence or delete an
///     unused binding (ZS0003), and drop a namespace qualifier the file's own
///     <c>(import-clr …)</c> makes redundant (ZS0004).
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
                    AddUnusedBindingActions(
                        actions,
                        request,
                        state,
                        diagnostic,
                        analysisService.Index
                    );
                    break;
                case DiagnosticCodes.RedundantTypeQualifier:
                    AddSimplifyNameAction(actions, request, diagnostic);
                    break;
                case DiagnosticCodes.DeprecatedAccessorSyntax:
                case DiagnosticCodes.DeprecatedKeyword:
                    AddReplaceWithAction(actions, request, diagnostic);
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
            MakeQuickFix($"Add missing match arms ({caseNames})", request, diagnostic, edit)
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

    /// <summary>ZS0004 spans exactly the redundant <c>Namespace.</c> characters, so the fix is
    ///     a deletion of the diagnostic's own range — no document lookup needed.</summary>
    private static void AddSimplifyNameAction(
        List<CommandOrCodeAction> actions,
        CodeActionParams request,
        Diagnostic diagnostic
    )
    {
        var data = ReadData(diagnostic.Data);
        if (data.Count == 0)
            return;

        actions.Add(
            MakeQuickFix(
                $"Simplify name to '{data[0]}'",
                request,
                diagnostic,
                new TextEdit { Range = diagnostic.Range, NewText = "" }
            )
        );
    }

    // ZS0006 and ZS0007 share a contract: [0] = the text as written, [1] = the modern
    // spelling, and the diagnostic spans exactly that text — so the fix is a straight
    // replacement for both.
    private static void AddReplaceWithAction(
        List<CommandOrCodeAction> actions,
        CodeActionParams request,
        Diagnostic diagnostic
    )
    {
        var data = ReadData(diagnostic.Data);
        if (data.Count < 2)
            return;

        actions.Add(
            MakeQuickFix(
                $"Replace '{data[0]}' with '{data[1]}'",
                request,
                diagnostic,
                new TextEdit { Range = diagnostic.Range, NewText = data[1] }
            )
        );
    }

    private static void AddUnusedBindingActions(
        List<CommandOrCodeAction> actions,
        CodeActionParams request,
        DocumentState state,
        Diagnostic diagnostic,
        WorkspaceIndex index
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

        // Removing a parameter changes the function's arity, so it is only offered when
        // every call site can be rewritten too; otherwise only the fixes above apply.
        if (
            BuildRemoveUnusedParameterEdits(
                state,
                index,
                diagnostic.Range,
                request.TextDocument.Uri
            ) is
            { } parameterEdits
        )
            actions.Add(
                new CommandOrCodeAction(
                    new CodeAction
                    {
                        Title = $"Remove parameter '{name}' and update call sites",
                        Kind = CodeActionKind.QuickFix,
                        Diagnostics = new Container<Diagnostic>(diagnostic),
                        Edit = new WorkspaceEdit
                        {
                            Changes = parameterEdits.ToDictionary(
                                kv => kv.Key,
                                kv => (IEnumerable<TextEdit>)kv.Value
                            ),
                        },
                    }
                )
            );
    }

    /// <summary>
    ///     Workspace edits that remove the unused parameter whose name starts at the
    ///     diagnostic range: the parameter element disappears from the owning form's
    ///     parameter list, and every indexed call site loses the corresponding argument.
    ///     Only top-level <c>define</c>/<c>define-async</c> parameters are offered this —
    ///     their call sites are in the reference index; lambda/method/constructor
    ///     parameters keep the underscore fix, since their callers cannot be traced
    ///     safely. The whole action declines (returns null) when any call site is not a
    ///     plain call of the exact arity: variadic parameters, higher-order or
    ///     <c>partial</c> uses, arity mismatches, unreadable referencing files, or source
    ///     that doesn't re-lex into the expected shape — a broken edit is worse than no
    ///     action. Cross-file sites are only rewritten when the function's qualified key
    ///     is known or the name is unique to this file.
    /// </summary>
    public static IReadOnlyDictionary<
        DocumentUri,
        IReadOnlyList<TextEdit>
    >? BuildRemoveUnusedParameterEdits(
        DocumentState state,
        WorkspaceIndex index,
        Range diagnosticRange,
        DocumentUri fallbackUri
    )
    {
        if (state.Ast is null)
            return null;

        var line = diagnosticRange.Start.Line + 1;
        var column = diagnosticRange.Start.Character + 1;
        if (FindParamOwner(state.Ast, line, column) is not { } owner)
            return null;
        if (owner.Param.IsVariadic)
            return null;

        // Param-side edit: delete the parameter element (the [name : Type] bracket or
        // bare atom) plus the separator before it, from the parameter list bracket.
        var source = state.Source;
        var tree = LexicalStructure.BuildTree(LexicalStructure.Tokens(source));
        if (FindBracketWhoseDirectItemsContain(tree, owner.Param.Span) is not { } paramList)
            return null;
        var items = DirectItems(source, paramList);
        var elementStart = ElementStartOffset(source, owner.Param);
        var elementIndex = items.FindIndex(item => item.Start == elementStart);
        if (elementIndex < 0)
            return null;
        // Delete the element plus the separator before it — or, when it is the list's
        // first item (e.g. a lambda's only parameter), the one after it.
        var deleteStart =
            elementIndex > 0 ? items[elementIndex - 1].End : TokenEndOffset(source, paramList.Open);
        // A comment inside the deleted extent would be destroyed; bail out instead.
        if (
            LexicalStructure
                .Tokens(source)
                .Any(t =>
                    t.Kind == Compiler.Syntax.TokenKind.Comment
                    && TokenStartOffset(source, t) > deleteStart
                    && TokenEndOffset(source, t) < items[elementIndex].End
                )
        )
            return null;
        var paramEdits = new List<TextEdit>
        {
            new()
            {
                Range = OffsetsToRange(source, deleteStart, items[elementIndex].End),
                NewText = "",
            },
        };

        // Call sites. Same-file references ride bare-name matching (with the usual
        // shadowing subtraction); cross-file ones only when the function's qualified
        // key is known — otherwise a same-named function elsewhere could be rewritten
        // by mistake.
        if (!state.NameToDefinition.TryGetValue(owner.FunctionName, out var local))
            return null;
        var defFile = local.DefinitionSpan.File;
        var qualifiedKey = index.DefinitionInFile(defFile, owner.FunctionName)?.QualifiedKey;
        var locallyBound = ScopeAnalysis.OccurrencesBoundLocally(state.Ast, owner.FunctionName);

        var byUri = new Dictionary<DocumentUri, IReadOnlyList<TextEdit>>
        {
            [fallbackUri] = paramEdits,
        };

        foreach (var reference in index.FindReferences(qualifiedKey, owner.FunctionName, defFile))
        {
            if (reference.Span == local.DefinitionSpan)
                continue;
            if (locallyBound.Contains(reference.Span))
                continue;

            var sameFile =
                string.IsNullOrEmpty(reference.Span.File)
                || string.Equals(reference.Span.File, defFile, StringComparison.OrdinalIgnoreCase);
            if (!sameFile && qualifiedKey is null)
                continue;

            string refSource;
            DocumentUri refUri;
            if (sameFile)
            {
                refSource = source;
                refUri = fallbackUri;
            }
            else
            {
                try
                {
                    refSource = File.ReadAllText(reference.Span.File);
                }
                catch
                {
                    return null; // Cannot verify this call site — decline the action.
                }
                refUri = DocumentUri.FromFileSystemPath(reference.Span.File);
            }

            if (
                BuildCallSiteArgumentDeletion(
                    refSource,
                    reference.Span,
                    owner.FunctionName,
                    owner.ParamIndex,
                    owner.Params.Count
                )
                is not { } deletion
            )
                return null;

            if (!byUri.TryGetValue(refUri, out var edits))
                byUri[refUri] = edits = new List<TextEdit>();
            ((List<TextEdit>)edits).Add(deletion);
        }

        return byUri;
    }

    private sealed record ParamOwner(
        string FunctionName,
        IReadOnlyList<Param> Params,
        Param Param,
        int ParamIndex
    );

    /// <summary>The top-level <c>define</c>/<c>define-async</c> owning the parameter whose
    ///     name starts at this position, or null — lambda, method, and constructor
    ///     parameters deliberately return null (see
    ///     <see cref="BuildRemoveUnusedParameterEdits" />).</summary>
    private static ParamOwner? FindParamOwner(AstNode node, int line, int column)
    {
        static bool At(Compiler.Diagnostics.SourceSpan span, int l, int c)
        {
            return span.Line == l && span.Column == c && span.Length > 0;
        }

        switch (node)
        {
            case AstNode.Define d:
                for (var i = 0; i < d.Params.Count; i++)
                    if (At(d.Params[i].NameSpan, line, column))
                        return new ParamOwner(d.FnName, d.Params, d.Params[i], i);
                break;
            case AstNode.DefineAsync d:
                for (var i = 0; i < d.Params.Count; i++)
                    if (At(d.Params[i].NameSpan, line, column))
                        return new ParamOwner(d.FnName, d.Params, d.Params[i], i);
                break;
        }

        foreach (var child in AstNavigation.Children(node))
        {
            var found = FindParamOwner(child, line, column);
            if (found is not null)
                return found;
        }

        return null;
    }

    /// <summary>Source offset of the parameter element's first token — the <c>[</c> of a
    ///     typed <c>[name : Type]</c> parameter, or the name atom itself when untyped.</summary>
    private static int ElementStartOffset(string source, Param param)
    {
        var token = LexicalStructure
            .Tokens(source)
            .Where(t => t.Kind != Compiler.Syntax.TokenKind.Comment)
            .First(t => t.Span.Line == param.Span.Line && t.Span.Column == param.Span.Column);
        return TokenStartOffset(source, token);
    }

    /// <summary>The bracket that has the span as one of its <em>direct</em> items (a child
    ///     bracket's extent or a direct atom), or null. For a typed parameter this is
    ///     the parameter-list bracket; the parameter's own bracket is nested deeper.</summary>
    private static BracketNode? FindBracketWhoseDirectItemsContain(
        IReadOnlyList<BracketNode> nodes,
        Compiler.Diagnostics.SourceSpan span
    )
    {
        foreach (var node in nodes)
        {
            if (node.Children.Any(child => TokenStartOffsetIs(child.Open.Span, span)))
                return node;
            if (
                node.AtomTokens.Any(t =>
                    t.Kind != Compiler.Syntax.TokenKind.Comment
                    && t.Span.Line == span.Line
                    && t.Span.Column == span.Column
                )
            )
                return node;
            if (FindBracketWhoseDirectItemsContain(node.Children, span) is { } nested)
                return nested;
        }
        return null;
    }

    private static bool TokenStartOffsetIs(
        Compiler.Diagnostics.SourceSpan tokenSpan,
        Compiler.Diagnostics.SourceSpan target
    )
    {
        return tokenSpan.Line == target.Line && tokenSpan.Column == target.Column;
    }

    /// <summary>Every direct item of a bracket in source order — plain atoms and nested
    ///     brackets alike — as (start, end) offsets. Comments are skipped.</summary>
    private static List<(int Start, int End)> DirectItems(string source, BracketNode bracket)
    {
        var items = new List<(int Start, int End)>();
        var children = bracket.Children.OrderBy(c => TokenStartOffset(source, c.Open)).ToList();
        var cursor = TokenEndOffset(source, bracket.Open);

        void AddAtomsUpTo(int limit)
        {
            foreach (var t in bracket.AtomTokens)
            {
                if (t.Kind == Compiler.Syntax.TokenKind.Comment)
                    continue;
                var start = TokenStartOffset(source, t);
                if (start >= cursor && TokenEndOffset(source, t) <= limit)
                    items.Add((start, TokenEndOffset(source, t)));
            }
        }

        foreach (var child in children)
        {
            var childStart = TokenStartOffset(source, child.Open);
            AddAtomsUpTo(childStart);
            items.Add((childStart, TokenEndOffset(source, child.Close)));
            cursor = TokenEndOffset(source, child.Close);
        }
        AddAtomsUpTo(TokenStartOffset(source, bracket.Close));
        return items.OrderBy(i => i.Start).ToList();
    }

    /// <summary>The edit deleting the <paramref name="argIndex" />-th argument at one call
    ///     site, or null when the site is not a plain call of the expected arity — see
    ///     <see cref="BuildRemoveUnusedParameterEdits" /> for the safety rules.</summary>
    private static TextEdit? BuildCallSiteArgumentDeletion(
        string source,
        Compiler.Diagnostics.SourceSpan nameSpan,
        string functionName,
        int argIndex,
        int paramCount
    )
    {
        var tree = LexicalStructure.BuildTree(LexicalStructure.Tokens(source));
        if (FindBracketWhoseDirectItemsContain(tree, nameSpan) is not { } call)
            return null;

        var items = DirectItems(source, call);
        if (items.Count < 2)
            return null;

        // The function name must be the call's head atom — a bare-name reference in any
        // other position (argument, higher-order use) cannot be rewritten safely.
        var headText = source[items[0].Start..items[0].End];
        if (headText != functionName || headText == "partial")
            return null;

        var args = items.Skip(1).ToList();
        if (args.Count != paramCount || argIndex >= args.Count)
            return null;

        // A comment inside the deleted extent would be destroyed; bail out instead.
        var start = argIndex == 0 ? items[0].End : args[argIndex - 1].End;
        var end = args[argIndex].End;
        if (
            LexicalStructure
                .Tokens(source)
                .Any(t =>
                    t.Kind == Compiler.Syntax.TokenKind.Comment
                    && TokenStartOffset(source, t) > start
                    && TokenEndOffset(source, t) < end
                )
        )
            return null;

        return new TextEdit { Range = OffsetsToRange(source, start, end), NewText = "" };
    }

    /// <summary>
    ///     Edits that delete the unused binding of the <c>let</c>/<c>let*</c> whose
    ///     bound name starts at the diagnostic range. Single-binding forms: when the
    ///     bound value is pure (a literal, name, or lambda) and there is one body
    ///     expression, the whole form is replaced by that body; otherwise the form is
    ///     rewritten to <c>(begin value body…)</c> so the value's effects are
    ///     preserved. Multi-binding forms delete just the <c>[name value]</c> pair —
    ///     pure values only, since effects can't be hoisted out of the sequential
    ///     binding chain. Returns null for <c>use</c> (deleting changes disposal) or
    ///     when the source can't be re-lexed into the expected shape.
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
        if (FindBindingByNameSpan(state.Ast, line, column) is not { } binder)
            return null;

        var source = state.Source;
        var tokens = LexicalStructure.Tokens(source);
        var form = FindBracketAt(LexicalStructure.BuildTree(tokens), binder.Span);
        // The desugared Let nodes of a let* (and multi-binding let) all share the
        // outer form span, so the diagnostic's name position picks the actual pair.
        if (
            form is null
            || form.AtomTokens.Count == 0
            || form.AtomTokens[0].Text is not ("let" or "let*" or "letrec")
            || form.Children.Count == 0
        )
            return null;

        var bindings = form.Children[0];
        if (bindings.Children.Count != 1)
            return RemoveBindingPairEdit(source, bindings, line, column, binder.Value);
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

        var valueIsPure = IsPureValue(binder.Value);

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
            new TextEdit { Range = OffsetsToRange(source, valueEnd, bindingsEnd), NewText = "" },
        ];
    }

    /// <summary>Deletes one <c>[name value]</c> pair from a multi-binding
    ///     <c>let</c>/<c>let*</c> bindings list — from the previous item's end (or the
    ///     list's opening bracket) through the pair's closing bracket. Pure values
    ///     only: in a sequential binding chain there is nowhere to hoist effects.</summary>
    private static IReadOnlyList<TextEdit>? RemoveBindingPairEdit(
        string source,
        BracketNode bindings,
        int line,
        int column,
        AstNode value
    )
    {
        if (!IsPureValue(value))
            return null;

        for (var i = 0; i < bindings.Children.Count; i++)
        {
            var pair = bindings.Children[i];
            var nameAtom = pair.AtomTokens.FirstOrDefault(t =>
                t.Kind != Compiler.Syntax.TokenKind.Comment
            );
            if (nameAtom is null || nameAtom.Span.Line != line || nameAtom.Span.Column != column)
                continue;

            var start =
                i == 0
                    ? TokenEndOffset(source, bindings.Open)
                    : TokenEndOffset(source, bindings.Children[i - 1].Close);
            var end = TokenEndOffset(source, pair.Close);
            return [new TextEdit { Range = OffsetsToRange(source, start, end), NewText = "" }];
        }

        return null;
    }

    private static bool IsPureValue(AstNode value)
    {
        return value
            is AstNode.IntLit
                or AstNode.FloatLit
                or AstNode.BoolLit
                or AstNode.StringLit
                or AstNode.SymbolLit
                or AstNode.NullLit
                or AstNode.UnitLit
                or AstNode.Name
                or AstNode.Lambda;
    }

    /// <summary>The binding form whose binder sits at this position, as the two things the
    ///     edit needs: the whole form's span (to locate its brackets) and the bound value.
    ///     Covers <c>let</c>/<c>let*</c> — whose desugared <see cref="AstNode.Let" /> nodes all
    ///     share the outer form span — and one binding of a <c>letrec</c> group.</summary>
    private static (Compiler.Diagnostics.SourceSpan Span, AstNode Value)? FindBindingByNameSpan(
        AstNode node,
        int line,
        int column
    )
    {
        static bool At(Compiler.Diagnostics.SourceSpan span, int line, int column)
        {
            return span.Line == line && span.Column == column && span.Length > 0;
        }

        switch (node)
        {
            case AstNode.Let let when At(let.NameSpan, line, column):
                return (let.Span, let.Value);
            case AstNode.Letrec letrec
                when letrec.Bindings.FirstOrDefault(b => At(b.NameSpan, line, column))
                    is { } binding:
                return (letrec.Span, binding.Value);
        }

        foreach (var child in AstNavigation.Children(node))
        {
            var found = FindBindingByNameSpan(child, line, column);
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
        var armOffset = SourceText.OffsetAt(source, lastArm.Span.Line - 1, lastArm.Span.Column - 1);
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
            else if (insertLine == 0 && trimmed.StartsWith("(module", StringComparison.Ordinal))
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
