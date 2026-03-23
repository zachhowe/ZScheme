namespace ZScript.Compiler.Package;

using ZScript.Compiler.Diagnostics;
using ZScript.Compiler.Pipeline;

public sealed class PackageBuilder(DiagnosticBag diagnostics)
{
    public CompilationResult? Build(string manifestPath, CompilerOptions? cliOverrides = null)
    {
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

        // 2. Resolve NuGet dependencies
        var assemblySearchPaths = new List<string>();
        if (manifest.Dependencies.NuGet.Count > 0)
        {
            var nugetResolver = new NuGetResolver(diagnostics);
            var nugetOutputDir = nugetResolver.Resolve(manifest.Dependencies.NuGet);
            if (nugetOutputDir is null && diagnostics.HasErrors)
                return null;
            if (nugetOutputDir is not null)
                assemblySearchPaths.Add(nugetOutputDir);
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
        }

        // 4. Merge manifest BuildConfig with CLI overrides (CLI wins)
        var options = MergeOptions(manifest.Build, cliOverrides);
        options.AssemblySearchPaths.AddRange(assemblySearchPaths);
        options.ModuleSearchPaths.AddRange(moduleSearchPaths);

        // Add manifest-level ref paths
        foreach (var refPath in manifest.Build.RefPaths)
            options.AssemblySearchPaths.Add(Path.GetFullPath(Path.Combine(manifestDir, refPath)));

        // 5. Read entry file and compile
        var entryPath = Path.GetFullPath(Path.Combine(manifestDir, manifest.Entry));
        if (!File.Exists(entryPath))
        {
            diagnostics.Error($"Entry file not found: {entryPath}", manifest.Span);
            return null;
        }

        var source = File.ReadAllText(entryPath);
        var compilation = new Compilation(options);
        return compilation.Compile(source, entryPath);
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
        if (buildConfig.StdLibPath is not null)
            options.StdLibPath = buildConfig.StdLibPath;

        // CLI overrides win
        if (cliOverrides is null)
            return options;

        if (cliOverrides.OutputPath != "output")
            options.OutputPath = cliOverrides.OutputPath;
        if (cliOverrides.OutputMode != OutputMode.CSharp)
            options.OutputMode = cliOverrides.OutputMode;
        if (cliOverrides.Namespace != "ZScriptGenerated")
            options.Namespace = cliOverrides.Namespace;
        if (cliOverrides.StdLibPath is not null)
            options.StdLibPath = cliOverrides.StdLibPath;
        if (cliOverrides.AssemblySearchPaths.Count > 0)
            options.AssemblySearchPaths.AddRange(cliOverrides.AssemblySearchPaths);
        if (cliOverrides.ModuleSearchPaths.Count > 0)
            options.ModuleSearchPaths.AddRange(cliOverrides.ModuleSearchPaths);

        return options;
    }
}
