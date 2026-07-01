namespace ZScheme.Fuzzer.Runtime;

public static class FuzzEnv
{
    private static string? _repoRoot;
    private static string? _dotnetPath;

    public static string RepoRoot =>
        _repoRoot ?? throw new InvalidOperationException("FuzzEnv.RepoRoot not initialized");

    public static string DotnetPath =>
        _dotnetPath ?? throw new InvalidOperationException("FuzzEnv.DotnetPath not initialized");

    public static void Initialize(string repoRoot)
    {
        _repoRoot = repoRoot;
        _dotnetPath = ResolveDotnet();
    }

    private static string ResolveDotnet()
    {
        var root = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrEmpty(root))
        {
            var candidate = Path.Combine(
                root,
                OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"
            );
            if (File.Exists(candidate))
                return candidate;
        }

        return OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
    }
}
