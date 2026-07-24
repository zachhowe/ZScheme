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

    public TempPackageWorkspace(string importPrefix, IReadOnlyDictionary<string, string> files)
    {
        Root = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "zslsp-" + Guid.NewGuid().ToString("N")
        );
        var packageDir = System.IO.Path.Combine(Root, "packages", importPrefix);
        var srcDir = System.IO.Path.Combine(packageDir, "src");
        Directory.CreateDirectory(srcDir);

        File.WriteAllText(
            System.IO.Path.Combine(packageDir, "package.zspkg"),
            $"(package (name \"{importPrefix}\") (version \"0.1.0\") "
                + $"(import-prefix \"{importPrefix}\") (sources (main \"src\")))"
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
    public AnalysisService Service { get; } = new();

    public string PathOf(string rel) => _paths[rel];

    public string UriOf(string rel) => new Uri(_paths[rel]).AbsoluteUri;

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
