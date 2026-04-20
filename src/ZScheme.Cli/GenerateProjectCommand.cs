using Serilog;
using ZScheme.Compiler.Codegen;
using ZScheme.Compiler.Diagnostics;
using ZScheme.Compiler.Package;
using ZScheme.Compiler.Pipeline;

namespace ZScheme.Cli;

internal static class GenerateProjectCommand
{
    public static int Run(string[] args)
    {
        var outputDir = "output";
        string? projectOutputType = null;
        string? langVersion = null;
        string? manifestPath = null;
        var nugetPackages = new List<(string PackageId, string Version)>();

        for (var i = 0; i < args.Length; i++)
            switch (args[i])
            {
                case "--output" or "-o" when i + 1 < args.Length:
                    outputDir = args[++i];
                    break;
                case "--output-type" when i + 1 < args.Length:
                    projectOutputType = args[++i];
                    break;
                case "--lang-version" when i + 1 < args.Length:
                    langVersion = args[++i];
                    break;
                case "--manifest" or "-m" when i + 1 < args.Length:
                    manifestPath = args[++i];
                    break;
                case "--nuget" when i + 1 < args.Length:
                {
                    var parts = args[++i].Split(':', 2);
                    if (parts.Length == 2)
                        nugetPackages.Add((parts[0], parts[1]));
                    else
                        Console.Error.WriteLine($"Invalid --nuget format: {args[i]} (expected PackageId:Version)");
                    break;
                }
            }

        // Auto-detect .zspkg in CWD if not explicitly given
        if (manifestPath is null)
        {
            var candidates = Directory.GetFiles(Directory.GetCurrentDirectory(), "*.zspkg");
            if (candidates.Length == 1)
                manifestPath = candidates[0];
        }

        if (manifestPath is not null)
            return RunPackageMode(manifestPath, outputDir);

        return RunLegacyMode(outputDir, projectOutputType, langVersion, nugetPackages);
    }

    private static int RunLegacyMode(
        string outputDir,
        string? projectOutputType,
        string? langVersion,
        IReadOnlyList<(string PackageId, string Version)> nugetPackages)
    {
        var fullOutputDir = Path.GetFullPath(outputDir);
        var projectName = Path.GetFileName(fullOutputDir);
        var options = new CSharpProjectOptions
        {
            OutputType = projectOutputType ?? "Exe",
            LangVersion = langVersion,
            NuGetPackages = nugetPackages
        };

        Directory.CreateDirectory(fullOutputDir);
        var csprojPath = Path.Combine(fullOutputDir, $"{projectName}.csproj");
        File.WriteAllText(csprojPath, CSharpProjectGenerator.GenerateCsproj(options));
        Console.WriteLine($"Generated: {csprojPath}");
        return 0;
    }

    private static int RunPackageMode(string manifestPath, string outputDir)
    {
        var fullManifestPath = Path.GetFullPath(manifestPath);
        if (!File.Exists(fullManifestPath))
        {
            Console.Error.WriteLine($"Manifest not found: {fullManifestPath}");
            return 1;
        }

        var manifestDir = Path.GetDirectoryName(fullManifestPath)!;
        var diagnostics = new DiagnosticBag();
        var parser = new ManifestParser(diagnostics);
        var manifest = parser.Parse(File.ReadAllText(fullManifestPath), fullManifestPath);
        if (manifest is null)
        {
            foreach (var d in diagnostics.Diagnostics)
                Console.Error.WriteLine(d);
            return 1;
        }

        var context = PackageEmissionContext.Build(diagnostics, manifestDir, manifest);
        if (context is null || diagnostics.HasErrors)
        {
            foreach (var d in diagnostics.Diagnostics)
                Console.Error.WriteLine(d);
            return 1;
        }

        var fullOutputDir = Path.GetFullPath(outputDir);
        Directory.CreateDirectory(fullOutputDir);

        var mainProjectName = ResolveMainProjectName(manifest);
        var mainDir = Path.Combine(fullOutputDir, mainProjectName);

        var mainResult = EmitMainProject(diagnostics, manifestDir, manifest, context, mainDir, mainProjectName);
        if (mainResult is null)
        {
            foreach (var d in diagnostics.Diagnostics)
                Console.Error.WriteLine(d);
            return 1;
        }

        var solutionEntries = new List<SolutionProjectEntry>
        {
            new("src", $"{mainProjectName}/{mainProjectName}.csproj")
        };

        // Emit test project if the package declares tests
        if (manifest.Sources?.Test is not null)
        {
            var testDir = Path.GetFullPath(Path.Combine(manifestDir, manifest.Sources.Test));
            if (Directory.Exists(testDir))
            {
                var testFiles = Directory.GetFiles(testDir, "*.zs", SearchOption.AllDirectories);
                if (testFiles.Length > 0)
                {
                    var testProjectName = $"{mainProjectName}.Tests";
                    var testProjectDir = Path.Combine(fullOutputDir, testProjectName);
                    var testOk = EmitTestProject(
                        diagnostics, manifestDir, manifest, context,
                        testFiles, mainResult,
                        testProjectDir, testProjectName,
                        $"../{mainProjectName}/{mainProjectName}.csproj");
                    if (!testOk)
                    {
                        foreach (var d in diagnostics.Diagnostics)
                            Console.Error.WriteLine(d);
                        return 1;
                    }

                    solutionEntries.Add(new SolutionProjectEntry(
                        "tests", $"{testProjectName}/{testProjectName}.csproj"));
                }
            }
        }

        var slnxPath = Path.Combine(fullOutputDir, $"{mainProjectName}.slnx");
        CSharpSolutionGenerator.WriteSlnx(slnxPath, solutionEntries);
        Console.WriteLine($"Generated: {slnxPath}");

        return 0;
    }

    private static string ResolveMainProjectName(PackageManifest manifest)
    {
        if (!string.IsNullOrWhiteSpace(manifest.Build.Namespace))
            return manifest.Build.Namespace!;
        return PascalCase(manifest.Name);
    }

    private static string PascalCase(string input)
    {
        var parts = input.Split(['-', '_', ' '], StringSplitOptions.RemoveEmptyEntries);
        return string.Concat(parts.Select(p => char.ToUpperInvariant(p[0]) + p[1..]));
    }

    private static LibraryCSharpResult? EmitMainProject(
        DiagnosticBag diagnostics,
        string manifestDir,
        PackageManifest manifest,
        PackageEmissionContext context,
        string mainDir,
        string mainProjectName)
    {
        var mainOptions = new CompilerOptions
        {
            OutputMode = OutputMode.CSharp,
            Namespace = manifest.Build.Namespace ?? "ZSchemeGenerated",
            AssemblySearchPaths = [..context.AssemblySearchPaths],
            ModuleSearchPaths = [..context.ModuleSearchPaths],
            PackagePaths = new Dictionary<string, string>(context.PackagePaths),
            ModuleAliases = new Dictionary<string, string>(context.ModuleAliases)
        };

        var libraryCompiler = new LibraryCompiler(diagnostics);
        var mainResult = libraryCompiler.CompileToCSharp(manifestDir, manifest, mainOptions);
        if (mainResult is null)
            return null;

        var csFileName = $"{mainProjectName}.cs";
        var projectOptions = new CSharpProjectOptions
        {
            OutputType = "Library",
            AssemblyReferences = mainResult.PrecompiledDependencyPaths,
            NuGetPackages = manifest.Dependencies.NuGet
                .Select(p => (p.PackageId, p.Version))
                .ToList()
        };

        CSharpProjectGenerator.WriteProjectDirectory(
            mainDir, mainProjectName,
            [(csFileName, mainResult.CsOutput)],
            projectOptions);

        Console.WriteLine($"Generated: {Path.Combine(mainDir, $"{mainProjectName}.csproj")}");
        Console.WriteLine($"Generated: {Path.Combine(mainDir, csFileName)}");
        return mainResult;
    }

    private static bool EmitTestProject(
        DiagnosticBag diagnostics,
        string manifestDir,
        PackageManifest manifest,
        PackageEmissionContext context,
        IReadOnlyList<string> testFiles,
        LibraryCSharpResult mainResult,
        string testDir,
        string testProjectName,
        string mainCsprojRelative)
    {
        var mainSourceDir = manifest.Sources?.Main is not null
            ? Path.GetFullPath(Path.Combine(manifestDir, manifest.Sources.Main))
            : manifestDir;
        var testSourceDir = Path.GetFullPath(Path.Combine(manifestDir, manifest.Sources!.Test!));

        var csFiles = new List<(string FileName, string Content)>();

        foreach (var testFile in testFiles)
        {
            Log.Debug("generate-project: transpiling test {File}", Path.GetFileName(testFile));
            var testSource = File.ReadAllText(testFile);
            var testOptions = new CompilerOptions
            {
                OutputMode = OutputMode.CSharp,
                Namespace = manifest.Build.Namespace ?? "ZSchemeGenerated",
                AssemblySearchPaths = [..context.TestAssemblySearchPaths],
                ModuleSearchPaths = [mainSourceDir, testSourceDir, ..context.ModuleSearchPaths],
                PackagePaths = new Dictionary<string, string>(context.PackagePaths)
                {
                    [manifest.ImportPrefix ?? ""] = mainSourceDir
                },
                ModuleAliases = new Dictionary<string, string>(context.ModuleAliases)
            };

            var compilation = new Compilation(testOptions);
            foreach (var (name, mod) in mainResult.Modules)
                compilation.InjectModule(name, mod);

            CompilationResult result;
            try
            {
                result = compilation.Compile(testSource, testFile);
            }
            catch (Exception ex)
            {
                diagnostics.Error(
                    $"Failed to compile {Path.GetFileName(testFile)}: {ex.Message}",
                    Compiler.Diagnostics.SourceSpan.None);
                return false;
            }

            if (!result.Success)
            {
                foreach (var diag in result.Diagnostics.Diagnostics)
                    diagnostics.Error(
                        $"{Path.GetFileName(testFile)}: {diag.Message}", diag.Span);
                return false;
            }

            if (result is CompilationResult.CSharpOutputResult csResult)
            {
                var relative = Path.GetRelativePath(testSourceDir, testFile);
                var csFileName = Path.ChangeExtension(relative, ".cs")
                    .Replace(Path.DirectorySeparatorChar, '/');
                csFiles.Add((csFileName, csResult.CsOutput));
            }
        }

        var testNuGetPackages = new Dictionary<string, string>();
        foreach (var dep in manifest.TestDependencies.NuGet)
            testNuGetPackages[dep.PackageId] = dep.Version;
        foreach (var dep in context.TransitiveTestNuGet)
            testNuGetPackages.TryAdd(dep.PackageId, dep.Version);

        // Library-shaped test projects use the extensibility-core + assert split rather
        // than the `xunit.v3` metapackage (which would force OutputType=Exe).
        var testRunnerDefaults = new (string Id, string Version)[]
        {
            ("Microsoft.NET.Test.Sdk", "17.13.0"),
            ("xunit.v3.extensibility.core", "3.2.2"),
            ("xunit.v3.assert", "3.2.2"),
            ("xunit.runner.visualstudio", "3.1.0")
        };
        foreach (var (id, version) in testRunnerDefaults)
            testNuGetPackages.TryAdd(id, version);

        var testProjectOptions = new CSharpProjectOptions
        {
            OutputType = "Library",
            AssemblyReferences = mainResult.PrecompiledDependencyPaths,
            NuGetPackages = testNuGetPackages.Select(kv => (kv.Key, kv.Value)).ToList(),
            ProjectReferences = [mainCsprojRelative]
        };

        CSharpProjectGenerator.WriteProjectDirectory(
            testDir, testProjectName, csFiles, testProjectOptions);

        Console.WriteLine($"Generated: {Path.Combine(testDir, $"{testProjectName}.csproj")}");
        foreach (var (fileName, _) in csFiles)
            Console.WriteLine($"Generated: {Path.Combine(testDir, fileName)}");
        return true;
    }
}

/// <summary>
///     Resolved dependency context for a package: ZScheme + NuGet deps for both main and test,
///     including transitive NuGet pulled from dependency manifests.
/// </summary>
internal sealed record PackageEmissionContext(
    IReadOnlyList<string> AssemblySearchPaths,
    IReadOnlyList<string> TestAssemblySearchPaths,
    IReadOnlyList<string> ModuleSearchPaths,
    IReadOnlyDictionary<string, string> PackagePaths,
    IReadOnlyDictionary<string, string> ModuleAliases,
    IReadOnlyList<NuGetDependency> TransitiveTestNuGet)
{
    public static PackageEmissionContext? Build(
        DiagnosticBag diagnostics, string manifestDir, PackageManifest manifest)
    {
        var assemblyRefPaths = new List<string>();
        foreach (var refPath in manifest.Build.RefPaths)
            assemblyRefPaths.Add(Path.GetFullPath(Path.Combine(manifestDir, refPath)));

        // Resolve ZScheme dependencies (main + test)
        var moduleSearchPaths = new List<string>();
        var packagePaths = new Dictionary<string, string>();
        var moduleAliases = new Dictionary<string, string>();

        var allZSchemeDeps = manifest.Dependencies.ZScheme
            .Concat(manifest.TestDependencies.ZScheme)
            .ToList();
        if (allZSchemeDeps.Count > 0)
        {
            var resolver = new ZSchemeDependencyResolver(diagnostics, manifestDir);
            var depPaths = resolver.Resolve(allZSchemeDeps);
            if (diagnostics.HasErrors)
                return null;

            foreach (var depPath in depPaths)
            {
                var resolved = CliHelpers.ResolvePackagePath(depPath);
                if (resolved is not null)
                {
                    moduleSearchPaths.Add(resolved.Value.SourceDir);
                    packagePaths.TryAdd(resolved.Value.Prefix, resolved.Value.SourceDir);
                    if (resolved.Value.DefaultModule is { } defMod)
                        moduleAliases.TryAdd(resolved.Value.Prefix, $"{resolved.Value.Prefix}/{defMod}");
                }
            }
        }

        // Collect transitive NuGet deps from dependency manifests (e.g., zunit → xunit)
        var transitiveTestNuGet = new List<NuGetDependency>();
        foreach (var modPath in moduleSearchPaths)
        {
            var parentDir = Path.GetDirectoryName(modPath)!;
            foreach (var candidate in new[]
                     {
                         Path.Combine(parentDir, "package.zspkg"),
                         Path.Combine(modPath, "package.zspkg")
                     })
            {
                if (!File.Exists(candidate)) continue;
                var subDiag = new DiagnosticBag();
                var subParser = new ManifestParser(subDiag);
                var subManifest = subParser.Parse(File.ReadAllText(candidate), candidate);
                if (subManifest is not null)
                    transitiveTestNuGet.AddRange(subManifest.Dependencies.NuGet);
                break;
            }
        }

        // Resolve NuGet packages (main-only first, then combined for tests)
        var mainAssemblySearchPaths = new List<string>(assemblyRefPaths);
        if (manifest.Dependencies.NuGet.Count > 0)
        {
            var nugetResolver = new NuGetResolver(diagnostics);
            var nugetOutputDir = nugetResolver.Resolve(manifest.Dependencies.NuGet);
            if (diagnostics.HasErrors)
                return null;
            if (nugetOutputDir is not null)
                mainAssemblySearchPaths.Add(nugetOutputDir);
        }

        var testAssemblySearchPaths = new List<string>(mainAssemblySearchPaths);
        var allNuGetDeps = new List<NuGetDependency>(manifest.Dependencies.NuGet);
        allNuGetDeps.AddRange(manifest.TestDependencies.NuGet);
        allNuGetDeps.AddRange(transitiveTestNuGet);
        if (allNuGetDeps.Count > 0)
        {
            var testNugetResolver = new NuGetResolver(diagnostics);
            var testNugetDir = testNugetResolver.Resolve(allNuGetDeps);
            if (diagnostics.HasErrors)
                return null;
            if (testNugetDir is not null && !testAssemblySearchPaths.Contains(testNugetDir))
                testAssemblySearchPaths.Add(testNugetDir);
        }

        return new PackageEmissionContext(
            mainAssemblySearchPaths,
            testAssemblySearchPaths,
            moduleSearchPaths,
            packagePaths,
            moduleAliases,
            transitiveTestNuGet);
    }
}
