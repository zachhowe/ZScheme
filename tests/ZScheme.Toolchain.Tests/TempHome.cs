namespace ZScheme.Toolchain.Tests;

/// <summary>
///     A throwaway <c>~/.zscheme</c> for one test. The home is always passed explicitly into the
///     types under test, so nothing here mutates the environment or touches the real user profile.
/// </summary>
internal sealed class TempHome : IDisposable
{
    public TempHome()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "zscheme-toolchain-test-" + Guid.NewGuid().ToString("N")[..8]
        );
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    /// <summary>Creates an installed toolchain with a stub <c>zs</c> executable in its bin dir.</summary>
    /// <param name="compilerVersion">
    ///     Recorded in <c>toolchain.json</c> when given. Differs from the name whenever a payload is
    ///     installed under another one (<c>zsup install dev --from …</c>), and it is the version
    ///     that keys the shared package cache.
    /// </param>
    public string AddInstalled(string name, string? compilerVersion = null)
    {
        var toolchainDir = ZSchemeHome.GetToolchainDir(name, Path);
        var binDir = System.IO.Path.Combine(toolchainDir, "bin");
        Directory.CreateDirectory(binDir);
        File.WriteAllText(System.IO.Path.Combine(binDir, ZSchemeHome.ExeName("zs")), "stub");

        if (compilerVersion is not null)
            File.WriteAllText(
                System.IO.Path.Combine(toolchainDir, "toolchain.json"),
                $$"""{"name":"{{name}}","version":"{{compilerVersion}}"}"""
            );

        return binDir;
    }

    /// <summary>Registers a link pointing at <paramref name="target" />, which need not exist.</summary>
    public void AddLink(string name, string target)
    {
        Directory.CreateDirectory(ZSchemeHome.GetToolchainsDir(Path));
        File.WriteAllText(
            ZSchemeHome.GetToolchainLinkFile(name, Path),
            target + Environment.NewLine
        );
    }

    /// <summary>Creates a directory under the temp home, e.g. a scratch project tree.</summary>
    public string Dir(params string[] segments)
    {
        var path = System.IO.Path.Combine([Path, .. segments]);
        Directory.CreateDirectory(path);
        return path;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // Cleanup is best-effort; a locked file must not fail an otherwise passing test.
        }
    }
}
