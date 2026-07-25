using System.Runtime.CompilerServices;
using ZScheme.LanguageServer.Analysis;

namespace ZScheme.LanguageServer.Tests.TestFixtures;

internal static class LspTestSession
{
    public static (AnalysisService Service, string Uri) Open(
        string source,
        string extension = ".zs",
        [CallerMemberName] string testName = ""
    )
    {
        var path = SyntheticPath(testName, extension);
        var uri = LspUri.Of(path);

        var service = new AnalysisService();
        service.AnalyzeImmediate(uri, source, 1);
        return (service, uri);
    }

    public static string SyntheticUri(string testName, string extension = ".zs")
    {
        return LspUri.Of(SyntheticPath(testName, extension));
    }

    /// <summary>1-based (line, col) of the start of the <paramref name="occurrence" />-th
    ///     (1-based) occurrence of <paramref name="token" /> in <paramref name="source" />.</summary>
    public static (int Line, int Col) Locate(string source, string token, int occurrence = 1)
    {
        var idx = -1;
        for (var i = 0; i < occurrence; i++)
            idx = source.IndexOf(token, idx + 1, StringComparison.Ordinal);
        if (idx < 0)
            throw new InvalidOperationException($"'{token}' #{occurrence} not found");

        var line = 1;
        var col = 1;
        for (var i = 0; i < idx; i++)
            if (source[i] == '\n')
            {
                line++;
                col = 1;
            }
            else
            {
                col++;
            }

        return (line, col);
    }

    public static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "packages")))
            dir = Path.GetDirectoryName(dir);
        return dir
            ?? throw new InvalidOperationException(
                "Could not locate repo root with packages/ directory"
            );
    }

    private static string SyntheticPath(string testName, string extension)
    {
        // Place the synthetic file inside the repo so package discovery walks find packages/.
        return Path.Combine(
            FindRepoRoot(),
            "tests",
            "ZScheme.LanguageServer.Tests",
            "tmp",
            $"{testName}{extension}"
        );
    }
}
