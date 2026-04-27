using System.Collections.Concurrent;
using ZScheme.Compiler.Ast;
using ZScheme.Compiler.Diagnostics;
using ZScheme.Compiler.Package;
using ZScheme.Compiler.Pipeline;

namespace ZScheme.LanguageServer.Analysis;

public sealed class AnalysisService
{
    private readonly ConcurrentDictionary<string, DocumentState> _documents = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _pendingAnalysis = new();

    public DocumentState? GetDocument(string uri)
    {
        return _documents.TryGetValue(uri, out var state) ? state : null;
    }

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
                : new DocumentState(uri, version, source, null, new DiagnosticBag(), [],
                    new Dictionary<string, SymbolInfo>());
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

    private DocumentState RunAnalysis(string uri, string source, int version)
    {
        var fileName = UriToFilePath(uri);
        var packagePaths = DiscoverPackagePaths(fileName);

        var options = new CompilerOptions
        {
            StopAfterTypeInference = true,
            AllowsImplicitModuleName = true,
            PackagePaths = packagePaths
        };

        var compilation = new Compilation(options);
        compilation.Compile(source, fileName);

        var diagnostics = compilation.GetDiagnostics();
        var program = compilation.TypedProgram;

        // Last-good fallback: when the current source fails before type inference (transient
        // parse errors during typing), reuse the previous typed AST + symbols so hover, go-to,
        // and completion keep working. Fresh diagnostics still surface.
        if (program is null && _documents.TryGetValue(uri, out var previous) && previous.Ast is not null)
            return new DocumentState(
                uri, version, source, previous.Ast, diagnostics,
                previous.Symbols, previous.NameToDefinition);

        return MakeState(uri, version, source, program, diagnostics);
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

    /// <summary>
    ///     Walks up from the file's directory looking for a sibling <c>packages/</c> directory
    ///     containing subdirectories with <c>package.zspkg</c> manifests. Returns a map of
    ///     import-prefix → source directory suitable for <see cref="CompilerOptions.PackagePaths"/>.
    /// </summary>
    private static Dictionary<string, string> DiscoverPackagePaths(string filePath)
    {
        var result = new Dictionary<string, string>();
        var dir = Path.GetDirectoryName(Path.GetFullPath(filePath));
        while (dir is not null)
        {
            var packagesDir = Path.Combine(dir, "packages");
            if (Directory.Exists(packagesDir))
            {
                foreach (var sub in Directory.EnumerateDirectories(packagesDir))
                {
                    var manifestPath = Path.Combine(sub, "package.zspkg");
                    if (!File.Exists(manifestPath))
                        continue;

                    var diag = new DiagnosticBag();
                    var parser = new ManifestParser(diag);
                    var manifest = parser.Parse(File.ReadAllText(manifestPath), manifestPath);
                    if (manifest?.ImportPrefix is null || diag.HasErrors)
                        continue;

                    var sourceDir = manifest.Sources?.Main is not null
                        ? Path.GetFullPath(Path.Combine(sub, manifest.Sources.Main))
                        : sub;

                    result[manifest.ImportPrefix] = sourceDir;
                }

                if (result.Count > 0)
                    return result;
            }

            dir = Path.GetDirectoryName(dir);
        }

        return result;
    }
}
