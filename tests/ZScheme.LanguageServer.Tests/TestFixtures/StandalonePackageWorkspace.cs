using System.Reflection;
using System.Reflection.Emit;
using ZScheme.LanguageServer.Analysis;

namespace ZScheme.LanguageServer.Tests.TestFixtures;

/// <summary>
///     Creates a throwaway package that does <em>not</em> live under a <c>packages/</c>
///     directory — the layout a consumer repo uses when it vendors ZScheme and keeps its
///     scripts somewhere of its own choosing. Package discovery therefore cannot find it by
///     walking up to a sibling <c>packages/</c>; everything must come from the package's own
///     <c>package.zspkg</c>, which is exactly what <c>ApplyOwnerContext</c> resolves.
/// </summary>
internal sealed class StandalonePackageWorkspace : IDisposable
{
    private readonly Dictionary<string, string> _paths = new();

    /// <param name="manifestExtras">
    ///     Raw s-expression forms appended inside the <c>(package …)</c> form — a
    ///     <c>(dependencies …)</c> or <c>(build …)</c> clause the test wants exercised.
    /// </param>
    public StandalonePackageWorkspace(
        string importPrefix,
        IReadOnlyDictionary<string, string> files,
        string manifestExtras = "",
        IReadOnlyDictionary<string, string>? testFiles = null
    )
    {
        Root = Path.Combine(Path.GetTempPath(), "zslsp-standalone-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);

        var sources = testFiles is null
            ? """(sources (main "src"))"""
            : """(sources (main "src") (test "test"))""";
        File.WriteAllText(
            Path.Combine(Root, "package.zspkg"),
            $"(package (name \"{importPrefix}\") (version \"0.1.0\") "
                + $"(import-prefix \"{importPrefix}\") {sources} {manifestExtras})"
        );

        WriteAll("src", files);
        if (testFiles is not null)
            WriteAll("test", testFiles);
    }

    public string Root { get; }
    public AnalysisService Service { get; } = new();

    /// <summary>Opens (and thus analyzes) the given file, returning its document state.</summary>
    public DocumentState Open(string rel) =>
        Service.AnalyzeImmediate(LspUri.Of(_paths[rel]), File.ReadAllText(_paths[rel]), 1);

    /// <summary>
    ///     Emits a minimal <c><paramref name="assemblyName" />.dll</c> into
    ///     <paramref name="relativeDir" /> exporting <c>&lt;assemblyName&gt;.Marker/Ping</c>.
    ///     Written with <see cref="PersistedAssemblyBuilder" /> rather than copied from the
    ///     test output so the type is reachable <em>only</em> through a manifest
    ///     <c>(ref …)</c> path — the compiler always probes its own base directory, so a test
    ///     that pointed at an assembly already sitting there would pass with or without the
    ///     ref path being honoured.
    ///     <para>
    ///         Each test must pass a name no other test uses. Assemblies stay loaded in the
    ///         interop load context for the life of the process, so a shared name lets one
    ///         test's probe assembly satisfy another test's lookup — which silently defeats
    ///         any negative control.
    ///     </para>
    /// </summary>
    public void WriteProbeAssembly(string relativeDir, string assemblyName)
    {
        var dir = Path.Combine(Root, relativeDir);
        Directory.CreateDirectory(dir);

        var builder = new PersistedAssemblyBuilder(
            new AssemblyName(assemblyName),
            typeof(object).Assembly
        );
        var type = builder
            .DefineDynamicModule(assemblyName)
            .DefineType($"{assemblyName}.Marker", TypeAttributes.Public | TypeAttributes.Class);
        var method = type.DefineMethod(
            "Ping",
            MethodAttributes.Public | MethodAttributes.Static,
            typeof(int),
            Type.EmptyTypes
        );
        var il = method.GetILGenerator();
        il.Emit(OpCodes.Ldc_I4, 42);
        il.Emit(OpCodes.Ret);
        type.CreateType();

        builder.Save(Path.Combine(dir, $"{assemblyName}.dll"));
    }

    /// <summary>Writes a sibling standalone package and returns its directory.</summary>
    public static string WriteDependencyPackage(
        string root,
        string importPrefix,
        IReadOnlyDictionary<string, string> files
    )
    {
        var srcDir = Path.Combine(root, "src");
        Directory.CreateDirectory(srcDir);
        File.WriteAllText(
            Path.Combine(root, "package.zspkg"),
            $"(package (name \"{importPrefix}\") (version \"0.1.0\") "
                + $"(import-prefix \"{importPrefix}\") (sources (main \"src\")))"
        );

        foreach (var (rel, content) in files)
        {
            var full = Path.Combine(srcDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }

        return root;
    }

    private void WriteAll(string subDir, IReadOnlyDictionary<string, string> files)
    {
        foreach (var (rel, content) in files)
        {
            var full = Path.Combine(Root, subDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
            _paths[$"{subDir}/{rel}"] = full;
        }
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
