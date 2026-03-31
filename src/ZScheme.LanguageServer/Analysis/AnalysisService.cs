namespace ZScheme.LanguageServer.Analysis;

using System.Collections.Concurrent;
using ZScheme.Compiler.Ast;
using ZScheme.Compiler.Diagnostics;
using ZScheme.Compiler.Modules;
using ZScheme.Compiler.Pipeline;
using ZScheme.Compiler.Syntax;
using ZScheme.Compiler.Types;

public sealed class AnalysisService
{
    private readonly ConcurrentDictionary<string, DocumentState> _documents = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _pendingAnalysis = new();

    public DocumentState? GetDocument(string uri) =>
        _documents.TryGetValue(uri, out var state) ? state : null;

    public async Task<DocumentState> AnalyzeAsync(string uri, string source, int version)
    {
        // Cancel any pending analysis for this document
        if (_pendingAnalysis.TryRemove(uri, out var previousCts))
            await previousCts.CancelAsync();

        var cts = new CancellationTokenSource();
        _pendingAnalysis[uri] = cts;

        try
        {
            // Debounce: wait 300ms before analyzing
            await Task.Delay(300, cts.Token);
        }
        catch (TaskCanceledException)
        {
            return _documents.TryGetValue(uri, out var existing)
                ? existing
                : new DocumentState(uri, version, source, null, new DiagnosticBag(), [], new Dictionary<string, SymbolInfo>());
        }
        finally
        {
            _pendingAnalysis.TryRemove(uri, out _);
        }

        var state = RunAnalysis(uri, source, version);
        _documents[uri] = state;
        return state;
    }

    public DocumentState AnalyzeImmediate(string uri, string source, int version)
    {
        var state = RunAnalysis(uri, source, version);
        _documents[uri] = state;
        return state;
    }

    public void RemoveDocument(string uri)
    {
        _documents.TryRemove(uri, out _);
        if (_pendingAnalysis.TryRemove(uri, out var cts))
            cts.Cancel();
    }

    private static DocumentState RunAnalysis(string uri, string source, int version)
    {
        var diagnostics = new DiagnosticBag();
        var fileName = UriToFilePath(uri);

        // Stage 1: Lex
        var lexer = new Lexer(source, fileName, diagnostics);
        var tokens = lexer.Tokenize();
        if (diagnostics.HasErrors)
            return MakeState(uri, version, source, null, diagnostics);

        // Stage 2: Parse S-expressions
        var parser = new SExprParser(tokens, diagnostics);
        var sexprs = parser.ParseAll();
        if (diagnostics.HasErrors)
            return MakeState(uri, version, source, null, diagnostics);

        // Stage 2.5: Macro expansion (with default macros only for now)
        var macroEnv = MacroEnvironment.Default();
        var expander = new MacroExpander(diagnostics);
        sexprs = expander.ExpandAll(sexprs, macroEnv);
        if (diagnostics.HasErrors)
            return MakeState(uri, version, source, null, diagnostics);

        // Stage 3: Build AST
        var astBuilder = new AstBuilder(diagnostics);
        var program = astBuilder.BuildProgram(sexprs);
        if (diagnostics.HasErrors)
            return MakeState(uri, version, source, program, diagnostics);

        // Stage 4: Type inference
        var env = TypeEnv.CreateRoot();
        var inferer = new TypeInferer(diagnostics);
        inferer.Infer(program, env);
        inferer.Resolve(program);

        // Collect symbols even if there are type errors — partial results are useful
        var collector = new SymbolCollector();
        collector.Collect(program);

        return new DocumentState(
            uri, version, source, program, diagnostics,
            collector.Symbols, collector.NameToDefinition);
    }

    private static DocumentState MakeState(
        string uri, int version, string source,
        AstNode.Program? program, DiagnosticBag diagnostics)
    {
        IReadOnlyList<SymbolInfo> symbols = [];
        IReadOnlyDictionary<string, SymbolInfo> nameToDefinition = new Dictionary<string, SymbolInfo>();

        if (program is not null)
        {
            var collector = new SymbolCollector();
            collector.Collect(program);
            symbols = collector.Symbols;
            nameToDefinition = collector.NameToDefinition;
        }

        return new DocumentState(uri, version, source, program, diagnostics, symbols, nameToDefinition);
    }

    private static string UriToFilePath(string uri)
    {
        if (Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && parsed.IsFile)
            return parsed.LocalPath;
        return uri;
    }
}
