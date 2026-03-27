using System.Diagnostics;
using Serilog;
using ZScript.Compiler.Diagnostics;
using ZScript.Compiler.Pipeline;

namespace ZScript.Compiler.Package;

public sealed class PackageBuilder(DiagnosticBag diagnostics)
{
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

        Log.Debug("PackageBuilder: manifest parsed, name={Name}, entry={Entry}", manifest.Name, manifest.Entry);

        // 2. Resolve NuGet dependencies
        var assemblySearchPaths = new List<string>();
        if (manifest.Dependencies.NuGet.Count > 0)
        {
            var nugetResolver = new NuGetResolver(diagnostics);
            var nugetOutputDir = nugetResolver.Resolve(manifest.Dependencies.NuGet);
            if (nugetOutputDir is null && diagnostics.HasErrors)
                return null;
            if (nugetOutputDir is not null)
            {
                assemblySearchPaths.Add(nugetOutputDir);
                Log.Debug("PackageBuilder: NuGet dependencies resolved to {OutputDir}", nugetOutputDir);
            }
        }

        // 3. Resolve ZScript dependencies
        var moduleSearchPaths = new List<string>();
        if (manifest.Dependencies.ZScript.Count > 0)
        {
            var zsResolver = new ZScriptDependencyResolver(diagnostics, manifestDir);
            var depPaths = zsResolver.Resolve(manifest.Dependencies.ZScript);
            if (diagnostics.HasErrors)
                return null;
            moduleSearchPaths.AddRange(depPaths);
            Log.Debug("PackageBuilder: ZScript dependencies resolved, {PathCount} search paths", depPaths.Count);
        }

        // 4. Merge manifest BuildConfig with CLI overrides (CLI wins)
        var options = MergeOptions(manifest.Build, cliOverrides);
        options.AssemblySearchPaths.AddRange(assemblySearchPaths);
        options.ModuleSearchPaths.AddRange(moduleSearchPaths);

        // Add manifest-level ref paths
        foreach (var refPath in manifest.Build.RefPaths)
            options.AssemblySearchPaths.Add(Path.GetFullPath(Path.Combine(manifestDir, refPath)));

        // 5. Read entry file and compile
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

    private static CompilerOptions MergeOptions(BuildConfig buildConfig, CompilerOptions? cliOverrides)
    {
        var options = new CompilerOptions();

        // Start with manifest defaults
        if (buildConfig.OutputPath is not null)
            options.OutputPath = buildConfig.OutputPath;
        if (buildConfig.Backend is not null)
            options.OutputMode = buildConfig.Backend.Value;
        if (buildConfig.Namespace is not null)
            options.Namespace = buildConfig.Namespace;

        // CLI overrides win
        if (cliOverrides is null)
            return options;

        if (cliOverrides.OutputPath != "output")
            options.OutputPath = cliOverrides.OutputPath;
        if (cliOverrides.OutputMode != OutputMode.CSharp)
            options.OutputMode = cliOverrides.OutputMode;
        if (cliOverrides.Namespace != "ZScriptGenerated")
            options.Namespace = cliOverrides.Namespace;
        if (cliOverrides.AssemblySearchPaths.Count > 0)
            options.AssemblySearchPaths.AddRange(cliOverrides.AssemblySearchPaths);
        if (cliOverrides.ModuleSearchPaths.Count > 0)
            options.ModuleSearchPaths.AddRange(cliOverrides.ModuleSearchPaths);
        foreach (var (name, path) in cliOverrides.PackagePaths)
            options.PackagePaths[name] = path;

        return options;
    }
}
