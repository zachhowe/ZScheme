using System.Runtime.CompilerServices;
using ZScheme.LanguageServer.Analysis;

namespace ZScheme.LanguageServer.Tests.TestFixtures;

internal static class LspTestSession
{
    public static (AnalysisService Service, string Uri) Open(
        string source,
        string extension = ".zs",
        [CallerMemberName] string testName = "")
    {
        var path = SyntheticPath(testName, extension);
        var uri = new Uri(path).AbsoluteUri;

        var service = new AnalysisService();
        service.AnalyzeImmediate(uri, source, 1);
        return (service, uri);
    }

    public static string SyntheticUri(string testName, string extension = ".zs")
    {
        return new Uri(SyntheticPath(testName, extension)).AbsoluteUri;
    }

    public static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "packages")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException(
            "Could not locate repo root with packages/ directory");
    }

    private static string SyntheticPath(string testName, string extension)
    {
        // Place the synthetic file inside the repo so package discovery walks find packages/.
        return Path.Combine(
            FindRepoRoot(),
            "tests", "ZScheme.LanguageServer.Tests", "tmp",
            $"{testName}{extension}");
    }
}
