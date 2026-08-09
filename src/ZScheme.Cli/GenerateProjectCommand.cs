using Serilog;
using ZScheme.Compiler.Codegen;
using ZScheme.Compiler.Diagnostics;
using ZScheme.Compiler.Modules;
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
                        Console.Error.WriteLine(
                            $"Invalid --nuget format: {args[i]} (expected PackageId:Version)"
                        );
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
        IReadOnlyList<(string PackageId, string Version)> nugetPackages
    )
    {
        var fullOutputDir = Path.GetFullPath(outputDir);
        var projectName = Path.GetFileName(fullOutputDir);
        var options = new CSharpProjectOptions
        {
            OutputType = projectOutputType ?? "Exe",
            LangVersion = langVersion,
            NuGetPackages = nugetPackages,
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
        File.WriteAllText(
            Path.Combine(fullOutputDir, "Directory.Build.props"),
            CSharpProjectGenerator.GenerateIsolatingDirectoryBuildProps()
        );

        var mainProjectName = ResolveMainProjectName(manifest);
        var mainDir = Path.Combine(fullOutputDir, mainProjectName);

        var mainResult = EmitMainProject(
            diagnostics,
            manifestDir,
            manifest,
            context,
            mainDir,
            mainProjectName
        );
        if (mainResult is null)
        {
            foreach (var d in diagnostics.Diagnostics)
                Console.Error.WriteLine(d);
            return 1;
        }

        var solutionEntries = new List<SolutionProjectEntry>
        {
            new("src", $"{mainProjectName}/{mainProjectName}.csproj"),
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
                        diagnostics,
                        manifestDir,
                        manifest,
                        context,
                        testFiles,
                        mainResult,
                        testProjectDir,
                        testProjectName,
                        $"../{mainProjectName}/{mainProjectName}.csproj"
                    );
                    if (!testOk)
                    {
                        foreach (var d in diagnostics.Diagnostics)
                            Console.Error.WriteLine(d);
                        return 1;
                    }

                    solutionEntries.Add(
                        new SolutionProjectEntry(
                            "tests",
                            $"{testProjectName}/{testProjectName}.csproj"
                        )
                    );
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
        if (!string.IsNullOrWhiteSpace(manifest.Build.Main?.Namespace))
            return manifest.Build.Main!.Namespace!;
        return PascalCase(manifest.Name);
    }

    private static string PascalCase(string input)
    {
        var parts = input.Split(['-', '_', ' '], StringSplitOptions.RemoveEmptyEntries);
        return string.Concat(parts.Select(p => char.ToUpperInvariant(p[0]) + p[1..]));
    }

    /// <summary>
    ///     Union of direct + transitive framework references, deduplicated, preserving direct-first order.
    /// </summary>
    private static IReadOnlyList<string> CollectFrameworkRefs(
        IReadOnlyList<FrameworkDependency> direct,
        IReadOnlyList<FrameworkDependency> transitive
    )
    {
        var seen = new HashSet<string>();
        var result = new List<string>();
        foreach (var fw in direct.Concat(transitive))
            if (seen.Add(fw.Id))
                result.Add(fw.Id);
        return result;
    }

    private static IReadOnlyList<string> MergeRefs(
        IReadOnlyList<string> precompiled,
        IReadOnlyList<string> transitive
    )
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Dedupe within `precompiled` too: it is itself a union of several compilations'
        // dependency lists, so the runtime assembly in particular shows up repeatedly, and a
        // repeated <Reference Include="..."> is an MSBuild duplicate-item error waiting to happen.
        var result = precompiled.Where(seen.Add).ToList();
        foreach (var path in transitive)
            // (ref ...) values may point at either a directory (search dir) or a
            // single .dll file. For csproj <Reference> we need explicit DLL paths,
            // so expand directories into the .dll files they contain.
            if (Directory.Exists(path))
            {
                foreach (var dll in Directory.EnumerateFiles(path, "*.dll"))
                    if (seen.Add(dll))
                        result.Add(dll);
            }
            else if (seen.Add(path))
            {
                result.Add(path);
            }

        return result;
    }

    /// <summary>
    ///     Shared-framework assemblies that duplicate a full type name already exported by
    ///     another assembly in the same framework, keyed by the framework that ships both.
    ///     The IL backend is immune — it binds each member reference to one assembly, guided
    ///     by the import's <c>:from</c> hint — but C# sees two candidates for the type name
    ///     and reports CS0433. Hiding the listed assembly behind an <c>extern alias</c> leaves
    ///     one candidate in the global namespace.
    ///     <para>
    ///         This is a list of known offenders, not a general solution: the principled fix
    ///         is to emit <c>extern alias</c> declarations driven by each resolved member's
    ///         declaring assembly. Tracked in
    ///         <c>issues/csharp-backend-cs0433-on-duplicated-framework-type-names.md</c>.
    ///     </para>
    /// </summary>
    private static readonly Dictionary<string, string[]> FrameworkAmbiguousAssemblies = new()
    {
        // Microsoft.Extensions.Logging.LoggingBuilderExtensions is defined in both
        // Microsoft.Extensions.Logging and Microsoft.Extensions.Logging.Configuration.
        // The packages bind ClearProviders/SetMinimumLevel, which live in the former.
        ["Microsoft.AspNetCore.App"] = ["Microsoft.Extensions.Logging.Configuration"],
    };

    private static IReadOnlyList<string> CollectAliasedAssemblies(
        IReadOnlyList<string> frameworkRefs,
        string sdk
    )
    {
        // Microsoft.NET.Sdk.Web pulls Microsoft.AspNetCore.App in implicitly, so the
        // ambiguity is present whether or not the reference is spelled out.
        var effective = frameworkRefs.ToList();
        if (sdk == "Microsoft.NET.Sdk.Web" && !effective.Contains("Microsoft.AspNetCore.App"))
            effective.Add("Microsoft.AspNetCore.App");

        return effective
            .SelectMany(id =>
                FrameworkAmbiguousAssemblies.TryGetValue(id, out var names) ? names : []
            )
            .Distinct()
            .ToList();
    }

    /// <summary>
    ///     Picks the right MSBuild Sdk: explicit override wins; otherwise switch to
    ///     <c>Microsoft.NET.Sdk.Web</c> when an ASP.NET Core framework reference is present.
    /// </summary>
    private static string ResolveSdk(string? explicitSdk, IReadOnlyList<string> frameworkRefs)
    {
        if (!string.IsNullOrWhiteSpace(explicitSdk))
            return explicitSdk;
        if (frameworkRefs.Any(id => id == "Microsoft.AspNetCore.App"))
            return "Microsoft.NET.Sdk.Web";
        return "Microsoft.NET.Sdk";
    }

    private static LibraryCSharpResult? EmitMainProject(
        DiagnosticBag diagnostics,
        string manifestDir,
        PackageManifest manifest,
        PackageEmissionContext context,
        string mainDir,
        string mainProjectName
    )
    {
        var mainOptions = new CompilerOptions
        {
            OutputMode = OutputMode.CSharp,
            Namespace = manifest.Build.Main?.Namespace ?? "ZSchemeGenerated",
            AssemblySearchPaths = [.. context.AssemblySearchPaths],
            ModuleSearchPaths = [.. context.ModuleSearchPaths],
            PackagePaths = new Dictionary<string, string>(context.PackagePaths),
            ModuleAliases = new Dictionary<string, string>(context.ModuleAliases),
        };

        var libraryCompiler = new LibraryCompiler(diagnostics);
        var mainResult = libraryCompiler.CompileToCSharp(manifestDir, manifest, mainOptions);
        if (mainResult is null)
            return null;

        var csFileName = $"{mainProjectName}.cs";
        var frameworkRefs = CollectFrameworkRefs(
            manifest.Dependencies.Frameworks,
            context.TransitiveFrameworks
        );
        var assemblyRefs = MergeRefs(
            mainResult.PrecompiledDependencyPaths,
            context.TransitiveRefPaths
        );
        var sdk = ResolveSdk(manifest.Build.Main?.Sdk, frameworkRefs);
        var projectOptions = new CSharpProjectOptions
        {
            OutputType = manifest.Build.Main?.OutputType ?? "Library",
            AssemblyReferences = assemblyRefs,
            NuGetPackages = manifest
                .Dependencies.NuGet.Select(p => (p.PackageId, p.Version))
                .ToList(),
            FrameworkReferences = frameworkRefs,
            Sdk = sdk,
            AliasedAssemblies = CollectAliasedAssemblies(frameworkRefs, sdk),
        };

        CSharpProjectGenerator.WriteProjectDirectory(
            mainDir,
            mainProjectName,
            [(csFileName, mainResult.CsOutput)],
            projectOptions
        );

        Console.WriteLine($"Generated: {Path.Combine(mainDir, $"{mainProjectName}.csproj")}");
        Console.WriteLine($"Generated: {Path.Combine(mainDir, csFileName)}");
        return mainResult;
    }

    /// <summary>
    ///     The module name a file under <paramref name="rootDir" /> is imported by: its path
    ///     relative to the root, without extension, with <c>/</c> separators — the spelling
    ///     <see cref="ZScheme.Compiler.Modules.ModuleResolver" /> resolves against a search path.
    /// </summary>
    private static string ModuleNameOf(string filePath, string rootDir)
    {
        return Path.ChangeExtension(Path.GetRelativePath(rootDir, filePath), null)
            .Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/');
    }

    /// <summary>
    ///     Orders test files so a file is compiled after every sibling test module it imports,
    ///     and reports which of them are imported by a sibling at all. Cycles and unresolvable
    ///     imports are left to the compiler — this only schedules.
    /// </summary>
    private static (
        IReadOnlyList<string> Ordered,
        IReadOnlySet<string> SharedModules
    ) OrderTestFiles(IReadOnlyList<string> testFiles, string testSourceDir)
    {
        var byModule = testFiles.ToDictionary(f => ModuleNameOf(f, testSourceDir), f => f);
        var siblingDeps = new Dictionary<string, List<string>>();
        var shared = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (moduleName, file) in byModule)
        {
            var deps = ImportScanner
                .Scan(File.ReadAllText(file), file)
                .Select(i => i.Name)
                .Where(name => name != moduleName && byModule.ContainsKey(name))
                .Distinct()
                .ToList();
            siblingDeps[moduleName] = deps;
            shared.UnionWith(deps);
        }

        var ordered = new List<string>();
        var state = new Dictionary<string, bool>(); // false = visiting, true = done
        foreach (var moduleName in byModule.Keys.OrderBy(n => n, StringComparer.Ordinal))
            Visit(moduleName);
        return (ordered, shared);

        void Visit(string moduleName)
        {
            if (state.ContainsKey(moduleName))
                return; // done, or a cycle — emit in whatever order we reached it
            state[moduleName] = false;
            foreach (var dep in siblingDeps[moduleName])
                Visit(dep);
            state[moduleName] = true;
            ordered.Add(byModule[moduleName]);
        }
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
        string mainCsprojRelative
    )
    {
        var mainSourceDir = manifest.Sources?.Main is not null
            ? Path.GetFullPath(Path.Combine(manifestDir, manifest.Sources.Main))
            : manifestDir;
        var testSourceDir = Path.GetFullPath(Path.Combine(manifestDir, manifest.Sources!.Test!));
        var testNamespace = manifest.Build.Test?.Namespace ?? "ZSchemeGenerated";

        // The main package's modules are compiled into the project this test project
        // references, so they must be referenced rather than re-emitted. Every test file is a
        // separate compilation but they all land in one csproj: inlining each file's whole
        // import graph redefines the same classes once per file.
        var externalMainModules = mainResult.Modules.ToDictionary(
            kv => kv.Key,
            kv =>
                kv.Value with
                {
                    EmitAsExternalReference = true,
                    BuildNamespace = manifest.Build.Main?.Namespace,
                }
        );

        // Modules that end up compiled into the test assembly itself: a sibling test module
        // (e.g. a shared `test-support`) and every test-only ZScheme dependency (zunit, and
        // whatever else only the tests import). Each is emitted by the first test file that
        // pulls it in and referenced by plain class name from then on — they share the test
        // project's assembly and namespace, so BuildNamespace stays null.
        var (orderedTestFiles, sharedTestModules) = OrderTestFiles(testFiles, testSourceDir);
        var testModules = new Dictionary<string, CompiledModule>();
        var csFiles = new List<(string FileName, string Content)>();

        // Precompiled ZScheme dependency assemblies (and ZScheme.Runtime) the emitted test
        // sources bind against. Every test compilation contributes; the csproj needs the union.
        var testAssemblyReferences = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var failed = false;

        foreach (var testFile in orderedTestFiles)
        {
            Log.Debug("generate-project: transpiling test {File}", Path.GetFileName(testFile));
            var testSource = File.ReadAllText(testFile);
            var testOptions = new CompilerOptions
            {
                OutputMode = OutputMode.CSharp,
                Namespace = testNamespace,
                AssemblySearchPaths = [.. context.TestAssemblySearchPaths],
                ModuleSearchPaths = [mainSourceDir, testSourceDir, .. context.ModuleSearchPaths],
                PackagePaths = new Dictionary<string, string>(context.PackagePaths)
                {
                    [manifest.ImportPrefix ?? ""] = mainSourceDir,
                },
                ModuleAliases = new Dictionary<string, string>(context.ModuleAliases),
            };

            var compilation = new Compilation(testOptions);
            foreach (var (name, mod) in externalMainModules)
                compilation.InjectModule(name, mod);
            foreach (var (name, mod) in testModules)
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
                    SourceSpan.None
                );
                failed = true;
                continue;
            }

            if (!result.Success)
            {
                // Keep going: reporting every test file's errors in one run beats surfacing
                // them one rebuild at a time.
                foreach (var diag in result.Diagnostics.Diagnostics)
                    diagnostics.Error($"{Path.GetFileName(testFile)}: {diag.Message}", diag.Span);
                failed = true;
                continue;
            }

            if (result is CompilationResult.CSharpOutputResult csResult)
            {
                var relative = Path.GetRelativePath(testSourceDir, testFile);
                var csFileName = Path.ChangeExtension(relative, ".cs")
                    .Replace(Path.DirectorySeparatorChar, '/');
                csFiles.Add((csFileName, csResult.CsOutput));
                testAssemblyReferences.UnionWith(csResult.PrecompiledAssemblyPaths);
            }

            // Whatever this file compiled from source that nothing had emitted yet is now part
            // of the test assembly. Register it so the next file references those classes
            // instead of emitting a second copy into the same project.
            foreach (var (name, mod) in compilation.GetCachedModules())
                if (!externalMainModules.ContainsKey(name) && !testModules.ContainsKey(name))
                    testModules[name] = mod with { EmitAsExternalReference = true };

            // Register this file's own module so later test files reference it instead of
            // emitting a second copy. CompileAsModule on a fresh compilation, because the
            // program compile above does not surface the primary module as a CompiledModule.
            // Only for files a sibling actually imports — the rest would pay a second compile
            // for nothing.
            var moduleName = ModuleNameOf(testFile, testSourceDir);
            if (sharedTestModules.Contains(moduleName))
            {
                var moduleCompilation = new Compilation(testOptions);
                foreach (var (name, mod) in externalMainModules)
                    moduleCompilation.InjectModule(name, mod);
                foreach (var (name, mod) in testModules)
                    moduleCompilation.InjectModule(name, mod);

                if (moduleCompilation.CompileAsModule(moduleName, testSource, testFile) is { } cm)
                    testModules[moduleName] = cm with { EmitAsExternalReference = true };
            }
        }

        if (failed)
            return false;

        var testNuGetPackages = new Dictionary<string, string>();
        foreach (var dep in manifest.TestDependencies.NuGet)
            testNuGetPackages[dep.PackageId] = dep.Version;
        foreach (var dep in context.TransitiveTestNuGet)
            testNuGetPackages.TryAdd(dep.PackageId, dep.Version);

        // xunit.v3 discovers tests by launching the test assembly as a process, so the project
        // has to produce an app host: with OutputType=Library `dotnet test` fails with
        // "Could not find app host executable". xunit.v3.core (rather than the
        // extensibility-core + assert split) is what generates the entry point and pulls in
        // the in-process console runner.
        var testRunnerDefaults = new (string Id, string Version)[]
        {
            ("Microsoft.NET.Test.Sdk", "17.13.0"),
            ("xunit.v3.core", "3.2.2"),
            ("xunit.v3.assert", "3.2.2"),
            ("xunit.runner.visualstudio", "3.1.0"),
        };
        foreach (var (id, version) in testRunnerDefaults)
            testNuGetPackages.TryAdd(id, version);

        var testFrameworkRefs = CollectFrameworkRefs(
            manifest.Dependencies.Frameworks,
            context.TransitiveFrameworks
        );
        var testAssemblyRefs = MergeRefs(
            [.. mainResult.PrecompiledDependencyPaths, .. testAssemblyReferences],
            context.TransitiveRefPaths
        );
        var testSdk = ResolveSdk(manifest.Build.Main?.Sdk, testFrameworkRefs);
        var testProjectOptions = new CSharpProjectOptions
        {
            OutputType = "Exe",
            AssemblyReferences = testAssemblyRefs,
            NuGetPackages = testNuGetPackages.Select(kv => (kv.Key, kv.Value)).ToList(),
            ProjectReferences = [mainCsprojRelative],
            FrameworkReferences = testFrameworkRefs,
            Sdk = testSdk,
            AliasedAssemblies = CollectAliasedAssemblies(testFrameworkRefs, testSdk),
        };

        CSharpProjectGenerator.WriteProjectDirectory(
            testDir,
            testProjectName,
            csFiles,
            testProjectOptions
        );

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
    IReadOnlyList<NuGetDependency> TransitiveTestNuGet,
    IReadOnlyList<FrameworkDependency> TransitiveFrameworks,
    IReadOnlyList<string> TransitiveRefPaths
)
{
    public static PackageEmissionContext? Build(
        DiagnosticBag diagnostics,
        string manifestDir,
        PackageManifest manifest
    )
    {
        var assemblyRefPaths = new List<string>();
        if (manifest.Build.Main is { } mainBuild)
            foreach (var refPath in mainBuild.RefPaths)
                assemblyRefPaths.Add(Path.GetFullPath(Path.Combine(manifestDir, refPath)));

        // Walk the full transitive closure (main + test deps, plus every dep-of-a-dep) the
        // same way PackageTester does, so a transitive package's prefixed modules resolve
        // and its frameworks/NuGet/ref-paths are inherited without re-declaring them here.
        var allZSchemeDeps = manifest
            .Dependencies.ZScheme.Concat(manifest.TestDependencies.ZScheme)
            .ToList();
        var closure = PackageDependencyResolver.ResolveTransitiveClosure(
            allZSchemeDeps,
            manifestDir,
            diagnostics
        );
        if (diagnostics.HasErrors)
            return null;

        var moduleSearchPaths = new List<string>(closure.ModuleSearchPaths);
        var packagePaths = new Dictionary<string, string>(closure.PackagePaths);
        var moduleAliases = new Dictionary<string, string>(closure.ModuleAliases);

        // Inherited from dependency manifests (e.g. zunit → xunit; aspnet → Microsoft.AspNetCore.App).
        var transitiveTestNuGet = closure.NuGet;
        var transitiveFrameworks = closure.Frameworks;
        var transitiveRefPaths = new List<string>(closure.RefPaths);

        // Note: no precompiled ZScheme package assemblies are fed to these compilations, so
        // every ZScheme dependency is emitted as C# alongside the package's own modules. A
        // cached .dll is IL produced by the other backend, and C# cannot consume its public
        // signatures — see issues/il-package-assemblies-reference-system-private-corelib.md,
        // which also records why the two obvious workarounds do not work. Compiling from
        // source also makes the generated tree self-contained and readable end to end.

        // Resolve NuGet packages (main-only first, then combined for tests).
        // Transitive ref paths from dep manifests flow into the search path so the
        // ZScheme compiler can resolve types declared in those dep DLLs when
        // compiling consumer modules.
        var mainAssemblySearchPaths = new List<string>(assemblyRefPaths);
        foreach (var refPath in transitiveRefPaths)
        {
            var dir = Path.GetDirectoryName(refPath);
            if (dir is not null && Directory.Exists(dir) && !mainAssemblySearchPaths.Contains(dir))
                mainAssemblySearchPaths.Add(dir);
        }

        // Shared-framework directories (e.g. Microsoft.AspNetCore.App) declared by this
        // package or inherited from a dependency. Without these the ZScheme compiler cannot
        // resolve framework types at all — the <FrameworkReference> in the generated csproj
        // only covers the *downstream* C# build.
        mainAssemblySearchPaths.AddRange(
            FrameworkResolver.Resolve(
                manifest.Dependencies.Frameworks.Concat(transitiveFrameworks).ToList(),
                diagnostics
            )
        );
        if (diagnostics.HasErrors)
            return null;

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
        if (manifest.Build.Test is { } testBuild)
            foreach (var refPath in testBuild.RefPaths)
                testAssemblySearchPaths.Add(Path.GetFullPath(Path.Combine(manifestDir, refPath)));

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
            transitiveTestNuGet,
            transitiveFrameworks,
            transitiveRefPaths
        );
    }
}
