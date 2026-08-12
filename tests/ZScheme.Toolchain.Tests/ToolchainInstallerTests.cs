using System.IO.Compression;
using Xunit;

namespace ZScheme.Toolchain.Tests;

public sealed class ToolchainInstallerTests
{
    /// <summary>Builds a source tree shaped like a release archive's contents.</summary>
    /// <summary>The compiler version the fake payloads are built as.</summary>
    private const string PayloadVersion = "0.4.0";

    private static string MakeToolchainPayload(
        TempHome home,
        string dirName,
        bool nested = true,
        bool withPkgCache = true,
        string compilerVersion = PayloadVersion
    )
    {
        var root = home.Dir(dirName);
        var binDir = nested ? Path.Combine(root, "bin") : root;
        Directory.CreateDirectory(binDir);

        File.WriteAllText(Path.Combine(binDir, ZSchemeHome.ExeName("zs")), "zs binary");
        File.WriteAllText(Path.Combine(binDir, ZSchemeHome.ExeName("zs-lsp")), "lsp binary");
        File.WriteAllText(Path.Combine(binDir, "ZScheme.Compiler.dll"), "dll");

        var stdlib = Path.Combine(root, "packages", "stdlib");
        Directory.CreateDirectory(stdlib);
        File.WriteAllText(Path.Combine(stdlib, "package.zspkg"), "(name \"zscheme-stdlib\")");

        if (withPkgCache)
        {
            // pkgcache/<compiler version>/<package>/<package version>/ -- the compiler version is
            // part of the payload because it, not the install name, keys the shared cache.
            var cached = Path.Combine(
                root,
                PackageCacheSeeder.DirectoryName,
                compilerVersion,
                "zscheme-stdlib",
                "1.0.0"
            );
            Directory.CreateDirectory(cached);
            File.WriteAllText(Path.Combine(cached, "zscheme-stdlib.dll"), "compiled");
            File.WriteAllText(Path.Combine(cached, "zscheme-stdlib.metadata.json"), "{}");
        }

        return root;
    }

    [Fact]
    public void InstallFrom_Directory_LandsTheExpectedLayout()
    {
        using var home = new TempHome();
        var payload = MakeToolchainPayload(home, "payload");

        var result = new ToolchainInstaller(home.Path).InstallFrom(payload, "0.4.0");

        Assert.Equal("0.4.0", result.Name);
        Assert.Equal(ZSchemeHome.GetToolchainDir("0.4.0", home.Path), result.Dir);
        Assert.True(File.Exists(Path.Combine(result.Dir, "bin", ZSchemeHome.ExeName("zs"))));
        Assert.True(File.Exists(Path.Combine(result.Dir, "packages", "stdlib", "package.zspkg")));
        Assert.True(File.Exists(Path.Combine(result.Dir, "toolchain.json")));
    }

    [Fact]
    public void InstallFrom_SeedsThePrebuiltPackageCache()
    {
        using var home = new TempHome();
        var payload = MakeToolchainPayload(home, "payload");

        var result = new ToolchainInstaller(home.Path).InstallFrom(payload, PayloadVersion);

        Assert.Equal(1, result.PackagesSeeded);
        var seeded = Path.Combine(
            ZSchemeHome.GetPackageCacheRootFor(PayloadVersion, home.Path),
            "zscheme-stdlib",
            "1.0.0",
            "zscheme-stdlib.dll"
        );
        Assert.True(File.Exists(seeded), $"expected a seeded cache entry at {seeded}");
    }

    [Fact]
    public void InstallFrom_UnderADifferentName_StillSeedsTheCompilerVersionsCache()
    {
        // The compiler reads cache/pkg/<its own version>/, which has nothing to do with the name
        // the toolchain was installed under. Seeding by install name would put the prebuilt
        // packages somewhere nothing ever looks, silently forcing a from-source stdlib build.
        using var home = new TempHome();
        var payload = MakeToolchainPayload(home, "payload");

        var result = new ToolchainInstaller(home.Path).InstallFrom(payload, "dev");

        Assert.Equal(1, result.PackagesSeeded);
        Assert.True(
            File.Exists(
                Path.Combine(
                    ZSchemeHome.GetPackageCacheRootFor(PayloadVersion, home.Path),
                    "zscheme-stdlib",
                    "1.0.0",
                    "zscheme-stdlib.dll"
                )
            ),
            "the prebuilt cache should be keyed by the payload's compiler version"
        );
        Assert.False(
            Directory.Exists(ZSchemeHome.GetPackageCacheRootFor("dev", home.Path)),
            "nothing should be written under the install name"
        );
    }

    [Fact]
    public void InstallFrom_SeedsIntoAVersionKeyedDirectory_SoToolchainsStayIsolated()
    {
        using var home = new TempHome();
        var a = MakeToolchainPayload(home, "payload-a", compilerVersion: "0.4.0");
        var b = MakeToolchainPayload(home, "payload-b", compilerVersion: "0.3.0");

        var installer = new ToolchainInstaller(home.Path);
        installer.InstallFrom(a, "0.4.0");
        installer.InstallFrom(b, "0.3.0");

        Assert.True(
            Directory.Exists(ZSchemeHome.GetPackageCacheRootFor("0.4.0", home.Path))
                && Directory.Exists(ZSchemeHome.GetPackageCacheRootFor("0.3.0", home.Path))
        );
    }

    [Fact]
    public void InstallFrom_RecordsThePayloadsCompilerVersionInMetadata()
    {
        using var home = new TempHome();
        var payload = MakeToolchainPayload(home, "payload");

        var result = new ToolchainInstaller(home.Path).InstallFrom(payload, "dev");

        Assert.Equal(
            PayloadVersion,
            PackageCacheSeeder.ResolveCompilerVersion(result.Dir, installedAs: "dev")
        );
    }

    [Fact]
    public void InstallFrom_FlatArchive_IsNormalizedIntoBin()
    {
        using var home = new TempHome();
        var payload = MakeToolchainPayload(home, "flat", nested: false);

        var result = new ToolchainInstaller(home.Path).InstallFrom(payload, "0.4.0");

        Assert.True(File.Exists(Path.Combine(result.Dir, "bin", ZSchemeHome.ExeName("zs"))));
        Assert.True(File.Exists(Path.Combine(result.Dir, "bin", "ZScheme.Compiler.dll")));
        // The payload directories stay beside bin/ rather than being swept into it.
        Assert.True(Directory.Exists(Path.Combine(result.Dir, "packages")));
        Assert.True(Directory.Exists(Path.Combine(result.Dir, PackageCacheSeeder.DirectoryName)));
    }

    [Fact]
    public void InstallFrom_Archive_Works()
    {
        using var home = new TempHome();
        var payload = MakeToolchainPayload(home, "payload");
        var zip = Path.Combine(home.Path, "toolchain.zip");
        ZipFile.CreateFromDirectory(payload, zip);

        var result = new ToolchainInstaller(home.Path).InstallFrom(zip, "0.4.0");

        Assert.True(File.Exists(Path.Combine(result.Dir, "bin", ZSchemeHome.ExeName("zs"))));
        Assert.Equal(1, result.PackagesSeeded);
    }

    [Fact]
    public void InstallFrom_WithoutForce_RefusesToReplace()
    {
        using var home = new TempHome();
        var payload = MakeToolchainPayload(home, "payload");
        var installer = new ToolchainInstaller(home.Path);
        installer.InstallFrom(payload, "0.4.0");

        Assert.Throws<IOException>(() => installer.InstallFrom(payload, "0.4.0"));
    }

    [Fact]
    public void InstallFrom_WithForce_Replaces()
    {
        using var home = new TempHome();
        var first = MakeToolchainPayload(home, "first");
        var installer = new ToolchainInstaller(home.Path);
        installer.InstallFrom(first, "0.4.0");

        var second = MakeToolchainPayload(home, "second");
        File.WriteAllText(Path.Combine(second, "bin", ZSchemeHome.ExeName("zs")), "newer");

        var result = installer.InstallFrom(second, "0.4.0", force: true);

        Assert.Equal(
            "newer",
            File.ReadAllText(Path.Combine(result.Dir, "bin", ZSchemeHome.ExeName("zs")))
        );
    }

    [Fact]
    public void InstallFrom_WithoutForce_RefusesToShadowALinkedToolchain()
    {
        // The reciprocal of the guard in ToolchainRegistry.Link. Both entries existing for one name
        // is what makes `zsup list` show it twice and `zsup uninstall` only half remove it.
        using var home = new TempHome();
        var payload = MakeToolchainPayload(home, "payload");
        home.AddLink("dev", home.Dir("devtree"));

        Assert.Throws<IOException>(() =>
            new ToolchainInstaller(home.Path).InstallFrom(payload, "dev")
        );
    }

    [Fact]
    public void InstallFrom_WithForce_ReplacesALinkedToolchainOutright()
    {
        using var home = new TempHome();
        var payload = MakeToolchainPayload(home, "payload");
        home.AddLink("dev", home.Dir("devtree"));

        var result = new ToolchainInstaller(home.Path).InstallFrom(payload, "dev", force: true);

        Assert.False(File.Exists(ZSchemeHome.GetToolchainLinkFile("dev", home.Path)));
        Assert.Empty(result.Warnings);

        var listed = new ToolchainRegistry(home.Path).List();
        Assert.Equal(["dev"], listed.Select(t => t.Name));
        Assert.False(listed[0].IsLinked);
    }

    [Fact]
    public void InstallFrom_NotAToolchain_ThrowsAndLeavesNothingBehind()
    {
        using var home = new TempHome();
        var junk = home.Dir("junk");
        File.WriteAllText(Path.Combine(junk, "readme.txt"), "not a toolchain");

        Assert.Throws<InvalidOperationException>(() =>
            new ToolchainInstaller(home.Path).InstallFrom(junk, "0.4.0")
        );

        Assert.False(Directory.Exists(ZSchemeHome.GetToolchainDir("0.4.0", home.Path)));
        Assert.Empty(Directory.EnumerateDirectories(ZSchemeHome.GetDownloadsDir(home.Path)));
    }

    [Fact]
    public void InstallFrom_MissingSource_Throws()
    {
        using var home = new TempHome();

        Assert.Throws<FileNotFoundException>(() =>
            new ToolchainInstaller(home.Path).InstallFrom(
                Path.Combine(home.Path, "nope.zip"),
                "0.4.0"
            )
        );
    }

    [Fact]
    public void InstallFrom_UnsafeName_Throws()
    {
        using var home = new TempHome();
        var payload = MakeToolchainPayload(home, "payload");

        Assert.Throws<ArgumentException>(() =>
            new ToolchainInstaller(home.Path).InstallFrom(payload, "../escape")
        );
    }

    [Fact]
    public void InstallFrom_SweepsStaleStagingDirectories()
    {
        using var home = new TempHome();
        var downloads = ZSchemeHome.GetDownloadsDir(home.Path);
        var stale = Path.Combine(downloads, ".staging-oldrun");
        var staleTrash = Path.Combine(downloads, ".trash-oldrun");
        Directory.CreateDirectory(stale);
        Directory.CreateDirectory(staleTrash);
        Directory.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddDays(-2));
        Directory.SetLastWriteTimeUtc(staleTrash, DateTime.UtcNow.AddDays(-2));

        var payload = MakeToolchainPayload(home, "payload");
        new ToolchainInstaller(home.Path).InstallFrom(payload, "0.4.0");

        Assert.Empty(Directory.EnumerateDirectories(downloads));
    }

    [Fact]
    public void SweepTransients_RemovesAnAbandonedSelfUpdateStagingTree()
    {
        // `zsup self update` stages under .zsup- in this same directory and never reaches
        // InstallFrom, so before this prefix was swept an interrupted self update left its
        // extracted tree in downloads/ with nothing that would ever remove it.
        using var home = new TempHome();
        var downloads = ZSchemeHome.GetDownloadsDir(home.Path);
        var stale = Path.Combine(downloads, ".zsup-oldrun");
        var inFlight = Path.Combine(downloads, ".zsup-concurrent");
        Directory.CreateDirectory(stale);
        Directory.CreateDirectory(inFlight);
        Directory.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddDays(-2));

        ToolchainInstaller.SweepTransients(downloads);

        Assert.False(Directory.Exists(stale));
        Assert.True(Directory.Exists(inFlight), "a self update in progress must not be swept");
    }

    [Fact]
    public void SweepTransients_RemovesAnAbandonedReleaseArchive()
    {
        // The archive is deleted as soon as the install or self update that downloaded it is done.
        // A scanner holding the file at that moment is routine on Windows, and the delete is
        // deliberately best-effort there -- so without this the hundreds of megabytes stay.
        using var home = new TempHome();
        var downloads = ZSchemeHome.GetDownloadsDir(home.Path);
        Directory.CreateDirectory(downloads);

        var staleToolchain = Path.Combine(downloads, "zscheme-0.3.0-win-x64.zip");
        var staleZsup = Path.Combine(downloads, "zsup-0.3.0-linux-x64.tar.gz");
        var inFlight = Path.Combine(downloads, "zscheme-0.4.0-win-x64.zip");
        // Not one of ours: a file the user parked here to install with --from.
        var userSupplied = Path.Combine(downloads, "my-build.zip");
        foreach (var path in new[] { staleToolchain, staleZsup, inFlight, userSupplied })
            File.WriteAllText(path, "archive");
        foreach (var path in new[] { staleToolchain, staleZsup, userSupplied })
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-2));

        ToolchainInstaller.SweepTransients(downloads);

        Assert.False(File.Exists(staleToolchain));
        Assert.False(File.Exists(staleZsup));
        Assert.True(File.Exists(inFlight), "a download in progress must not be swept");
        Assert.True(File.Exists(userSupplied), "only zsup's own release assets are swept");
    }

    [Fact]
    public void InstallFrom_LeavesARecentStagingDirectoryAlone()
    {
        // A staging directory that was created moments ago belongs to a concurrent install --
        // two terminals, or an editor triggering one while the user runs another. Deleting it
        // would destroy that install's tree mid-extraction.
        using var home = new TempHome();
        var downloads = ZSchemeHome.GetDownloadsDir(home.Path);
        var inFlight = Path.Combine(downloads, ".staging-concurrent");
        Directory.CreateDirectory(inFlight);
        File.WriteAllText(Path.Combine(inFlight, "partial.bin"), "half-extracted");

        var payload = MakeToolchainPayload(home, "payload");
        new ToolchainInstaller(home.Path).InstallFrom(payload, "0.4.0");

        Assert.True(File.Exists(Path.Combine(inFlight, "partial.bin")));
    }

    [Fact]
    public void InstallFrom_KeepsAStampedTrashDirectoryThatWasCreatedLongAgo()
    {
        // A trash directory is an installed toolchain renamed aside, and a rename carries the
        // original timestamps -- so one holding a toolchain installed months ago is born older than
        // the cutoff. Ageing it by its creation time would let a concurrent install delete the only
        // remaining copy of what the other install had just moved aside, which is exactly what the
        // sweep exists to prevent. InstallFrom stamps the write time to mark it as live.
        using var home = new TempHome();
        var downloads = ZSchemeHome.GetDownloadsDir(home.Path);
        Directory.CreateDirectory(downloads);

        var stamped = Path.Combine(downloads, ".trash-concurrent");
        Directory.CreateDirectory(stamped);
        File.WriteAllText(Path.Combine(stamped, "zs"), "the previous toolchain");
        Directory.SetCreationTimeUtc(stamped, DateTime.UtcNow.AddDays(-30));
        Directory.SetLastWriteTimeUtc(stamped, DateTime.UtcNow);

        var abandoned = Path.Combine(downloads, ".trash-oldrun");
        Directory.CreateDirectory(abandoned);
        Directory.SetLastWriteTimeUtc(abandoned, DateTime.UtcNow.AddDays(-2));

        new ToolchainInstaller(home.Path).InstallFrom(
            MakeToolchainPayload(home, "payload"),
            "0.4.0"
        );

        Assert.True(File.Exists(Path.Combine(stamped, "zs")), "a live transient was swept");
        Assert.False(Directory.Exists(abandoned));
    }

    [Fact]
    public void InstallFrom_SweepsAStalePartialDownload()
    {
        using var home = new TempHome();
        var downloads = ZSchemeHome.GetDownloadsDir(home.Path);
        Directory.CreateDirectory(downloads);

        var stale = Path.Combine(downloads, "zscheme-0.3.0-win-x64.zip.part");
        var fresh = Path.Combine(downloads, "zscheme-0.4.0-win-x64.zip.part");
        File.WriteAllText(stale, "abandoned");
        File.WriteAllText(fresh, "in flight");
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddDays(-2));

        new ToolchainInstaller(home.Path).InstallFrom(
            MakeToolchainPayload(home, "payload"),
            "0.4.0"
        );

        // Nothing ever resumes a .part, so an abandoned one would otherwise accumulate forever.
        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(fresh), "a download in progress must not be swept");
    }

    [Fact]
    public void InstallFrom_PayloadWithABinDirectoryThatHasNoZs_MergesRatherThanFailing()
    {
        // A dev staging tree, or a future layout change. Moving over the existing bin/ would throw
        // a bare "cannot create ... already exists" that says nothing about the real cause.
        using var home = new TempHome();
        var payload = MakeToolchainPayload(home, "payload", nested: false);
        var strayBin = Path.Combine(payload, "bin");
        Directory.CreateDirectory(strayBin);
        File.WriteAllText(Path.Combine(strayBin, "runtimeconfig.json"), "{}");

        var result = new ToolchainInstaller(home.Path).InstallFrom(payload, "0.4.0");

        Assert.True(File.Exists(Path.Combine(result.Dir, "bin", ZSchemeHome.ExeName("zs"))));
        Assert.True(File.Exists(Path.Combine(result.Dir, "bin", "runtimeconfig.json")));
    }

    [Fact]
    public void InstallFrom_PayloadWhoseBinCollidesOnASubdirectory_MergesThatToo()
    {
        // The same collision one level down. A plain Directory.Move onto an existing bin/runtimes/
        // throws, which is exactly the bare "already exists" the merge above exists to avoid --
        // and a payload that ships runtimes/ beside a staging tree that has its own is enough.
        using var home = new TempHome();
        var payload = MakeToolchainPayload(home, "payload", nested: false);

        var rootRuntimes = Path.Combine(payload, "runtimes");
        Directory.CreateDirectory(rootRuntimes);
        File.WriteAllText(Path.Combine(rootRuntimes, "from-root.txt"), "root");

        var binRuntimes = Path.Combine(payload, "bin", "runtimes");
        Directory.CreateDirectory(binRuntimes);
        File.WriteAllText(Path.Combine(binRuntimes, "from-bin.txt"), "bin");

        var result = new ToolchainInstaller(home.Path).InstallFrom(payload, "0.4.0");

        var merged = Path.Combine(result.Dir, "bin", "runtimes");
        Assert.True(File.Exists(Path.Combine(merged, "from-root.txt")));
        Assert.True(File.Exists(Path.Combine(merged, "from-bin.txt")));
    }

    [Fact]
    public void InstallFrom_PayloadWithAFileNamedBin_SaysWhatIsWrong()
    {
        using var home = new TempHome();
        var payload = MakeToolchainPayload(home, "payload", nested: false);
        File.WriteAllText(Path.Combine(payload, "bin"), "not a directory");

        var error = Assert.Throws<InvalidOperationException>(() =>
            new ToolchainInstaller(home.Path).InstallFrom(payload, "0.4.0")
        );

        Assert.Contains(payload, error.Message);
    }

    [Fact]
    public void InstallFrom_MarksTheExecutablesRunnableOnUnix()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var home = new TempHome();
        var payload = MakeToolchainPayload(home, "payload");
        // Mimic a tarball built on Windows, which records mode 0644.
        File.SetUnixFileMode(
            Path.Combine(payload, "bin", "zs"),
            UnixFileMode.UserRead | UnixFileMode.UserWrite
        );

        var result = new ToolchainInstaller(home.Path).InstallFrom(payload, "0.4.0");

        var mode = File.GetUnixFileMode(Path.Combine(result.Dir, "bin", "zs"));
        Assert.True(mode.HasFlag(UnixFileMode.UserExecute));
    }

    [Fact]
    public void Seed_DoesNotOverwriteAnExistingCacheEntryUnlessForced()
    {
        using var home = new TempHome();
        var payload = MakeToolchainPayload(home, "payload");
        new ToolchainInstaller(home.Path).InstallFrom(payload, "0.4.0");

        var cached = Path.Combine(
            ZSchemeHome.GetPackageCacheRootFor(PayloadVersion, home.Path),
            "zscheme-stdlib",
            "1.0.0",
            "zscheme-stdlib.dll"
        );
        File.WriteAllText(cached, "rebuilt from source");

        var toolchainDir = ZSchemeHome.GetToolchainDir("0.4.0", home.Path);
        Assert.Equal(0, PackageCacheSeeder.Seed(toolchainDir, home.Path));
        Assert.Equal("rebuilt from source", File.ReadAllText(cached));

        Assert.Equal(1, PackageCacheSeeder.Seed(toolchainDir, home.Path, force: true));
        Assert.Equal("compiled", File.ReadAllText(cached));
    }

    [Fact]
    public void Seed_NoPrebuiltCache_IsANoOp()
    {
        using var home = new TempHome();
        var payload = MakeToolchainPayload(home, "payload", withPkgCache: false);

        var result = new ToolchainInstaller(home.Path).InstallFrom(payload, "0.4.0");

        Assert.Equal(0, result.PackagesSeeded);
    }
}
