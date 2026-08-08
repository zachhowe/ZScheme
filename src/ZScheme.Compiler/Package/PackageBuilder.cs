using System.Diagnostics;
using Serilog;
using ZScheme.Compiler.Diagnostics;
using ZScheme.Compiler.Pipeline;

namespace ZScheme.Compiler.Package;

public sealed class PackageBuilder(DiagnosticBag diagnostics)
{
    private static readonly ILogger Log = Serilog.Log.ForContext<PackageBuilder>();

    public CompilationResult? Build(string manifestPath, CompilerOptions? cliOverrides = null)
    {
        Log.Debug("PackageBuilder: building from {ManifestPath}", manifestPath);

        if (!File.Exists(manifestPath))
        {
            diagnostics.Error($"Manifest not found: {manifestPath}", SourceSpan.None);
            return null;
        }

        var manifestDir = Path.GetDirectoryName(Path.GetFullPath(manifestPath))!;
        var manifestSource = File.ReadAllText(manifestPath);

        // 1. Parse manifest
        var parser = new ManifestParser(diagnostics);
        var manifest = parser.Parse(manifestSource, manifestPath);
        if (manifest is null)
            return null;

        Log.Debug(
            "PackageBuilder: manifest parsed, name={Name}, entry={Entry}",
            manifest.Name,
            manifest.Entry
        );

        // 2. Resolve the manifest's dependency closure, frameworks, NuGet packages, and
        //    ref paths. Shared with `test -m` and LibraryCompiler.CompileFromManifest so
        //    every entry point agrees on what a manifest means.
        var inputs = PackageOptionsBuilder.Resolve(manifestDir, manifest, diagnostics);
        if (inputs is null)
            return null;

        // 3. Merge manifest scalar BuildConfig with CLI overrides, then layer collections
        //    auto-resolved → CLI so explicit CLI flags win.
        var options = MergeOptions(manifest.Build, cliOverrides);
        options.FrameworkReferences = [.. inputs.FrameworkIds];

        AddDistinct(options.AssemblySearchPaths, inputs.AssemblySearchPaths);
        AddDistinct(options.ModuleSearchPaths, inputs.ModuleSearchPaths);
        foreach (var (prefix, path) in inputs.PackagePaths)
            options.PackagePaths[prefix] = path;
        foreach (var (prefix, alias) in inputs.ModuleAliases)
            options.ModuleAliases[prefix] = alias;

        if (cliOverrides is not null)
        {
            AddDistinct(options.AssemblySearchPaths, cliOverrides.AssemblySearchPaths);
            AddDistinct(options.ModuleSearchPaths, cliOverrides.ModuleSearchPaths);
            foreach (var (prefix, path) in cliOverrides.PackagePaths)
                options.PackagePaths[prefix] = path;
            foreach (var (prefix, alias) in cliOverrides.ModuleAliases)
                options.ModuleAliases[prefix] = alias;
            AddDistinct(options.PrecompiledPackagePaths, cliOverrides.PrecompiledPackagePaths);
        }

        // 4. Read entry file and compile
        if (manifest.Entry is null)
        {
            diagnostics.Error("No entry file specified; nothing to compile.", manifest.Span);
            return null;
        }

        var entryPath = Path.GetFullPath(Path.Combine(manifestDir, manifest.Entry));
        if (!File.Exists(entryPath))
        {
            diagnostics.Error($"Entry file not found: {entryPath}", manifest.Span);
            return null;
        }

        var source = File.ReadAllText(entryPath);
        var sw = Stopwatch.StartNew();
        var compilation = new Compilation(options);
        var result = compilation.Compile(source, entryPath);
        Log.Debug("PackageBuilder: compilation completed in {ElapsedMs}ms", sw.ElapsedMilliseconds);
        return result;
    }

    private static CompilerOptions MergeOptions(
        BuildConfig buildConfig,
        CompilerOptions? cliOverrides
    )
    {
        var options = new CompilerOptions();

        // Start with manifest defaults (main build config)
        if (buildConfig.Main is { } main)
        {
            if (main.OutputPath is not null)
                options.OutputPath = main.OutputPath;
            if (main.Backend is not null)
                options.OutputMode = main.Backend.Value;
            if (main.Namespace is not null)
                options.Namespace = main.Namespace;
            if (main.WarnUnusedParameters is { } warnParams)
                options.WarnUnusedParameters = warnParams;
        }

        // CLI overrides win
        if (cliOverrides is null)
            return options;

        if (cliOverrides.OutputPath != "output")
            options.OutputPath = cliOverrides.OutputPath;
        if (cliOverrides.OutputMode != OutputMode.CSharp)
            options.OutputMode = cliOverrides.OutputMode;
        if (cliOverrides.Namespace != "ZSchemeGenerated")
            options.Namespace = cliOverrides.Namespace;
        // --no-warn-unused-params disables even when the manifest enables.
        if (!cliOverrides.WarnUnusedParameters)
            options.WarnUnusedParameters = false;

        // Collection merging (assembly/module search paths, package paths, aliases,
        // precompiled paths) is handled in Build() so auto-resolved dependency inputs and
        // CLI overrides are layered in a single, well-defined order.
        return options;
    }

    /// <summary>
    ///     Appends <paramref name="additions" /> to <paramref name="target" />, skipping
    ///     entries already present (case-insensitive, treating values as file-system paths).
    /// </summary>
    private static void AddDistinct(List<string> target, IEnumerable<string> additions)
    {
        var seen = new HashSet<string>(target, StringComparer.OrdinalIgnoreCase);
        foreach (var item in additions)
            if (seen.Add(item))
                target.Add(item);
    }
}
