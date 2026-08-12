using ZScheme.LanguageServer.Analysis;

namespace ZScheme.LanguageServer.Tests.TestFixtures;

/// <summary>
///     Creates a throwaway on-disk package (<c>&lt;temp&gt;/packages/&lt;prefix&gt;/src/*.zs</c>
///     with a minimal <c>package.zspkg</c>) so cross-file / cross-package navigation can be
///     exercised end-to-end: package discovery walks up to the synthetic <c>packages/</c>
///     directory exactly as it does in the real repo. Files are indexed by opening them
///     through <see cref="AnalysisService.AnalyzeImmediate" />.
/// </summary>
internal sealed class TempPackageWorkspace : IDisposable
{
    private readonly Dictionary<string, string> _paths = new();

    /// <param name="framework">
    ///     Optional shared framework to declare as a dependency (e.g.
    ///     <c>Microsoft.AspNetCore.App</c>). Packages that declare one exercise the
    ///     framework-resolution and assembly-isolation paths, which is where the language
    ///     server used to fail outright.
    /// </param>
    /// <param name="analysisBudget">
    ///     Overrides <see cref="AnalysisService.AnalysisBudget" />. Passing
    ///     <see cref="TimeSpan.Zero" /> makes every analysis overrun by construction — the
    ///     wait polls a task that was only just queued — so the degraded-state path can be
    ///     tested without loading the machine down until a real compile misses a deadline.
    /// </param>
    public TempPackageWorkspace(
        string importPrefix,
        IReadOnlyDictionary<string, string> files,
        string? framework = null,
        TimeSpan? analysisBudget = null
    )
    {
        Service = analysisBudget is { } budget
            ? new AnalysisService { AnalysisBudget = budget }
            : new AnalysisService();

        Root = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "zslsp-" + Guid.NewGuid().ToString("N")
        );
        var packageDir = System.IO.Path.Combine(Root, "packages", importPrefix);
        var srcDir = System.IO.Path.Combine(packageDir, "src");
        Directory.CreateDirectory(srcDir);

        var dependencies = framework is null ? "" : $" (dependencies (framework {framework}))";
        File.WriteAllText(
            System.IO.Path.Combine(packageDir, "package.zspkg"),
            $"(package (name \"{importPrefix}\") (version \"0.1.0\") "
                + $"(import-prefix \"{importPrefix}\") (sources (main \"src\")){dependencies})"
        );

        foreach (var (rel, content) in files)
        {
            var full = System.IO.Path.Combine(srcDir, rel);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
            _paths[rel] = full;
        }
    }

    public string Root { get; }
    public AnalysisService Service { get; }

    /// <summary>The file's path as the server spells it — see <see cref="LspUri" />.</summary>
    public string PathOf(string rel) => LspUri.PathOf(_paths[rel]);

    /// <summary>The file's URI as the server spells it — see <see cref="LspUri" />.</summary>
    public string UriOf(string rel) => LspUri.Of(_paths[rel]);

    /// <summary>Opens (and thus indexes) the given file, returning its document state.</summary>
    public DocumentState Open(string rel) =>
        Service.AnalyzeImmediate(UriOf(rel), File.ReadAllText(_paths[rel]), 1);

    /// <summary>1-based (line, col) of the start of the <paramref name="occurrence" />-th
    ///     (1-based) occurrence of <paramref name="token" /> in the file.</summary>
    public (int Line, int Col) Locate(string rel, string token, int occurrence = 1)
    {
        var text = File.ReadAllText(_paths[rel]);
        var idx = -1;
        for (var i = 0; i < occurrence; i++)
            idx = text.IndexOf(token, idx + 1, StringComparison.Ordinal);
        if (idx < 0)
            throw new InvalidOperationException($"'{token}' #{occurrence} not found in {rel}");

        var line = 1;
        var col = 1;
        for (var i = 0; i < idx; i++)
        {
            if (text[i] == '\n')
            {
                line++;
                col = 1;
            }
            else
            {
                col++;
            }
        }

        return (line, col);
    }

    /// <summary>Writes an arbitrary file at <paramref name="relativeToRoot" /> (creating
    ///     intermediate directories) and returns its full path. Lets a test stage things
    ///     that live outside the package's <c>src</c> — a <c>.gitignore</c>, a generated
    ///     output tree — for the workspace-scan exclusion rules.</summary>
    public string WriteRootFile(string relativeToRoot, string content)
    {
        var full = System.IO.Path.Combine(Root, relativeToRoot);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch
        {
            // best-effort cleanup
        }
    }
}
