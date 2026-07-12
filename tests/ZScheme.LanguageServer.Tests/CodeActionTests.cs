using Newtonsoft.Json.Linq;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Xunit;
using ZScheme.Compiler.Diagnostics;
using ZScheme.LanguageServer.Analysis;
using ZScheme.LanguageServer.Handlers;
using ZScheme.LanguageServer.Tests.TestFixtures;
using Diagnostic = OmniSharp.Extensions.LanguageServer.Protocol.Models.Diagnostic;
using Range = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;

namespace ZScheme.LanguageServer.Tests;

public sealed class CodeActionTests
{
    /// <summary>The compiler's ZS0002 diagnostic for the (single) non-exhaustive match
    ///     in <paramref name="state" />, as the LSP range/data the handler receives.</summary>
    private static (Range Range, IReadOnlyList<string> Data) NonExhaustiveDiagnostic(
        DocumentState state
    )
    {
        var diag = Assert.Single(
            state.Diagnostics.Diagnostics,
            d => d.Code == DiagnosticCodes.NonExhaustiveMatch
        );
        Assert.NotNull(diag.Data);
        return (TextDocumentSyncHandler.SpanToRange(diag.Span), diag.Data!);
    }

    [Fact]
    public void MissingArms_InsertedAfterLastArmWithIndentation()
    {
        var src = """
            (module test)
            (define-union Color (Red) (Green) (Blue))
            (define (name [c : Color]) : String
              (match c
                [(Red) "red"]))
            """;
        var (svc, uri) = LspTestSession.Open(src);
        var state = svc.GetDocument(uri)!;
        var (range, data) = NonExhaustiveDiagnostic(state);

        var edit = CodeActionHandler.BuildMissingArmsEdit(state, range, data);

        Assert.NotNull(edit);
        Assert.Equal("\n    [Green (raise (new System.Exception \"TODO\"))]"
            + "\n    [Blue (raise (new System.Exception \"TODO\"))]", edit!.NewText);
        // Insertion lands immediately after the Red arm's closing bracket.
        var (line, col) = LspTestSession.Locate(src, "[(Red) \"red\"]");
        Assert.Equal(line - 1, edit.Range.Start.Line);
        Assert.Equal(col - 1 + "[(Red) \"red\"]".Length, edit.Range.Start.Character);
        Assert.Equal(edit.Range.Start, edit.Range.End);
    }

    [Fact]
    public void MissingArms_PayloadCaseGetsWildcardSubpatterns()
    {
        var src = """
            (module test)
            (define-union Shape (Circle [r : Int]) (Rect [w : Int] [h : Int]))
            (define (area [s : Shape]) : Int
              (match s
                [(Circle r) r]))
            """;
        var (svc, uri) = LspTestSession.Open(src);
        var state = svc.GetDocument(uri)!;
        var (range, data) = NonExhaustiveDiagnostic(state);

        var edit = CodeActionHandler.BuildMissingArmsEdit(state, range, data);

        Assert.NotNull(edit);
        Assert.Contains("[(Rect _ _) ", edit!.NewText);
    }

    [Fact]
    public void MissingArms_MultiLineLastArm_InsertsAfterItsClosingBracket()
    {
        var src = """
            (module test)
            (define-union Color (Red) (Green) (Blue))
            (define (name [c : Color]) : String
              (match c
                [(Red)
                  (let ([tone "dark"])
                    tone)]))
            """;
        var (svc, uri) = LspTestSession.Open(src);
        var state = svc.GetDocument(uri)!;
        var (range, data) = NonExhaustiveDiagnostic(state);

        var edit = CodeActionHandler.BuildMissingArmsEdit(state, range, data);

        Assert.NotNull(edit);
        // The arm closes on the "tone)]" line; insertion is right after that "]".
        var (line, col) = LspTestSession.Locate(src, "tone)]");
        Assert.Equal(line - 1, edit!.Range.Start.Line);
        Assert.Equal(col - 1 + "tone)]".Length, edit.Range.Start.Character);
        Assert.StartsWith("\n    [Green ", edit.NewText);
    }

    [Fact]
    public void ImportEdit_LandsAfterLastImport()
    {
        var src = """
            (module test)
            (import stdlib/list)
            (define (f) 1)
            """;

        var edit = CodeActionHandler.BuildImportEdit(src, "stdlib/option");

        Assert.Equal("(import stdlib/option)\n", edit.NewText);
        Assert.Equal(2, edit.Range.Start.Line);
        Assert.Equal(0, edit.Range.Start.Character);
    }

    [Fact]
    public void ImportEdit_NoImports_LandsAfterModuleDecl()
    {
        var src = """
            (module test)
            (define (f) 1)
            """;

        var edit = CodeActionHandler.BuildImportEdit(src, "stdlib/option");

        Assert.Equal(1, edit.Range.Start.Line);
    }

    [Fact]
    public void ImportEdit_NoModuleDecl_LandsAtTop()
    {
        var edit = CodeActionHandler.BuildImportEdit("(define (f) 1)", "stdlib/option");

        Assert.Equal(0, edit.Range.Start.Line);
    }

    [Fact]
    public async Task Handler_NonExhaustiveMatch_ReturnsQuickFixWithWorkspaceEdit()
    {
        var src = """
            (module test)
            (define-union Color (Red) (Green) (Blue))
            (define (name [c : Color]) : String
              (match c
                [(Red) "red"]))
            """;
        var (svc, uri) = LspTestSession.Open(src);
        var state = svc.GetDocument(uri)!;
        var (range, data) = NonExhaustiveDiagnostic(state);

        var handler = new CodeActionHandler(svc);
        var result = await handler.Handle(
            new CodeActionParams
            {
                TextDocument = new TextDocumentIdentifier(DocumentUri.Parse(uri)),
                Range = range,
                Context = new CodeActionContext
                {
                    Diagnostics = new Container<Diagnostic>(
                        new Diagnostic
                        {
                            Range = range,
                            Source = "zscheme",
                            Message = "Non-exhaustive match: missing cases Green, Blue",
                            Code = new DiagnosticCode(DiagnosticCodes.NonExhaustiveMatch),
                            Data = JArray.FromObject(data),
                        }
                    ),
                },
            },
            CancellationToken.None
        );

        Assert.NotNull(result);
        var action = Assert.Single(result!).CodeAction;
        Assert.NotNull(action);
        Assert.Equal(CodeActionKind.QuickFix, action!.Kind);
        Assert.Contains("Green", action.Title);
        Assert.NotNull(action.Edit?.Changes);
        var edits = Assert.Single(action.Edit!.Changes!).Value;
        Assert.Contains("[Green ", Assert.Single(edits).NewText);
    }

    [Fact]
    public async Task Handler_UndefinedVariable_OffersImportPerCandidateModule()
    {
        using var ws = new TempPackageWorkspace(
            "importpkg",
            new Dictionary<string, string>
            {
                ["widgets.zs"] = "(define (make-gadget) 1)\n(export make-gadget)\n",
                ["main.zs"] = "(module main)\n(define (go) (make-gadget))\n",
            }
        );
        ws.Service.ReindexFromDisk(ws.PathOf("widgets.zs"));
        var state = ws.Open("main.zs");

        var diag = Assert.Single(
            state.Diagnostics.Diagnostics,
            d => d.Code == DiagnosticCodes.UndefinedVariable
        );
        var range = TextDocumentSyncHandler.SpanToRange(diag.Span);

        var handler = new CodeActionHandler(ws.Service);
        var result = await handler.Handle(
            new CodeActionParams
            {
                TextDocument = new TextDocumentIdentifier(DocumentUri.Parse(ws.UriOf("main.zs"))),
                Range = range,
                Context = new CodeActionContext
                {
                    Diagnostics = new Container<Diagnostic>(
                        new Diagnostic
                        {
                            Range = range,
                            Source = "zscheme",
                            Message = diag.Message,
                            Code = new DiagnosticCode(DiagnosticCodes.UndefinedVariable),
                            Data = JArray.FromObject(diag.Data!),
                        }
                    ),
                },
            },
            CancellationToken.None
        );

        Assert.NotNull(result);
        var action = Assert.Single(result!).CodeAction;
        Assert.NotNull(action);
        Assert.Contains("make-gadget", action!.Title);
        Assert.Contains("widgets", action.Title);
        var edits = Assert.Single(action.Edit!.Changes!).Value;
        var edit = Assert.Single(edits);
        Assert.StartsWith("(import ", edit.NewText);
        Assert.Contains("widgets", edit.NewText);
        // After the (module main) line.
        Assert.Equal(1, edit.Range.Start.Line);
    }

    [Fact]
    public async Task Handler_ForeignDiagnostic_Ignored()
    {
        var (svc, uri) = LspTestSession.Open("(module test)");
        var handler = new CodeActionHandler(svc);

        var result = await handler.Handle(
            new CodeActionParams
            {
                TextDocument = new TextDocumentIdentifier(DocumentUri.Parse(uri)),
                Range = new Range(new Position(0, 0), new Position(0, 1)),
                Context = new CodeActionContext
                {
                    Diagnostics = new Container<Diagnostic>(
                        new Diagnostic
                        {
                            Range = new Range(new Position(0, 0), new Position(0, 1)),
                            Source = "csharp",
                            Message = "unrelated",
                            Code = new DiagnosticCode(DiagnosticCodes.NonExhaustiveMatch),
                        }
                    ),
                },
            },
            CancellationToken.None
        );

        Assert.NotNull(result);
        Assert.Empty(result!);
    }

    // ---- Unused binding (ZS0003) fixes ----

    private static (DocumentState State, Range Range) UnusedBindingDiagnostic(string source)
    {
        var (svc, uri) = LspTestSession.Open(source);
        var state = svc.GetDocument(uri)!;
        var diag = Assert.Single(
            state.Diagnostics.Diagnostics,
            d => d.Code == DiagnosticCodes.UnusedBinding
        );
        return (state, TextDocumentSyncHandler.SpanToRange(diag.Span));
    }

    /// <summary>Applies non-overlapping edits to the source (last-to-first).</summary>
    private static string ApplyEdits(string source, IEnumerable<TextEdit> edits)
    {
        var result = source;
        foreach (var edit in edits.OrderByDescending(e => (e.Range.Start.Line, e.Range.Start.Character)))
        {
            var start = OffsetOf(result, edit.Range.Start);
            var end = OffsetOf(result, edit.Range.End);
            result = result[..start] + edit.NewText + result[end..];
        }

        return result;
    }

    private static int OffsetOf(string source, Position position)
    {
        var offset = 0;
        for (var line = 0; line < position.Line; line++)
            offset = source.IndexOf('\n', offset) + 1;
        return offset + position.Character;
    }

    [Fact]
    public void RemoveUnusedBinding_PureValueSingleBody_ReplacesFormWithBody()
    {
        var source = """
            (module test)
            (define (f) (let ([x 1]) 2))
            """;
        var (state, range) = UnusedBindingDiagnostic(source);

        var edits = CodeActionHandler.BuildRemoveUnusedBindingEdits(state, range);

        Assert.NotNull(edits);
        Assert.Equal(
            """
            (module test)
            (define (f) 2)
            """,
            ApplyEdits(source, edits!)
        );
    }

    [Fact]
    public void RemoveUnusedBinding_SideEffectValue_RewritesToBegin()
    {
        var source = """
            (module test)
            (define (g) 1)
            (define (f) (let ([x (g)]) 2))
            """;
        var (state, range) = UnusedBindingDiagnostic(source);

        var edits = CodeActionHandler.BuildRemoveUnusedBindingEdits(state, range);

        Assert.NotNull(edits);
        Assert.Contains("(define (f) (begin (g) 2))", ApplyEdits(source, edits!));
    }

    [Fact]
    public void RemoveUnusedBinding_MultiBodyLet_RewritesToBegin()
    {
        var source = """
            (module test)
            (define (g) 1)
            (define (f) (let ([x 5]) (g) 2))
            """;
        var (state, range) = UnusedBindingDiagnostic(source);

        var edits = CodeActionHandler.BuildRemoveUnusedBindingEdits(state, range);

        Assert.NotNull(edits);
        Assert.Contains("(define (f) (begin 5 (g) 2))", ApplyEdits(source, edits!));
    }

    [Fact]
    public void RemoveUnusedBinding_LetStar_NotOffered()
    {
        var source = """
            (module test)
            (define (f) (let* ([a 1] [b 2]) a))
            """;
        var (state, range) = UnusedBindingDiagnostic(source);

        Assert.Null(CodeActionHandler.BuildRemoveUnusedBindingEdits(state, range));
    }

    [Fact]
    public async Task UnusedBinding_OffersUnderscorePrefixFix()
    {
        var source = """
            (module test)
            (define (f) (let ([x 1]) 2))
            """;
        var (svc, uri) = LspTestSession.Open(source);
        var state = svc.GetDocument(uri)!;
        var diag = Assert.Single(
            state.Diagnostics.Diagnostics,
            d => d.Code == DiagnosticCodes.UnusedBinding
        );

        var handler = new CodeActionHandler(svc);
        var result = await handler.Handle(
            new CodeActionParams
            {
                TextDocument = new TextDocumentIdentifier(DocumentUri.Parse(uri)),
                Range = TextDocumentSyncHandler.SpanToRange(diag.Span),
                Context = new CodeActionContext
                {
                    Diagnostics = new Container<Diagnostic>(
                        new Diagnostic
                        {
                            Range = TextDocumentSyncHandler.SpanToRange(diag.Span),
                            Source = "zscheme",
                            Message = diag.Message,
                            Code = new DiagnosticCode(DiagnosticCodes.UnusedBinding),
                            Data = JArray.FromObject(diag.Data!),
                        }
                    ),
                },
            },
            CancellationToken.None
        );

        Assert.NotNull(result);
        var titles = result!.Select(a => a.CodeAction!.Title).ToList();
        Assert.Contains("Prefix 'x' with underscore", titles);
        Assert.Contains("Remove unused binding", titles);

        // Applying the underscore fix yields the opt-out spelling.
        var prefix = result!.First(a => a.CodeAction!.Title.StartsWith("Prefix"));
        var edit = prefix.CodeAction!.Edit!.Changes!.Values.Single().Single();
        Assert.Contains("(let ([_x 1]) 2)", ApplyEdits(source, [edit]));
    }
}
