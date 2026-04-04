using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using Serilog;
using ZScheme.Compiler.Diagnostics;
using ZScheme.Compiler.Pipeline;

namespace ZScheme.Compiler.Package;

public enum TestOutcome
{
    Passed,
    Failed,
    Skipped
}

public sealed record TestCaseResult(string TestName, TestOutcome Outcome, string? FailureMessage);

public sealed record PackageTestResult(
    IReadOnlyList<TestCaseResult> Results,
    DiagnosticBag Diagnostics)
{
    public int Passed => Results.Count(r => r.Outcome == TestOutcome.Passed);
    public int Failed => Results.Count(r => r.Outcome == TestOutcome.Failed);
    public int Skipped => Results.Count(r => r.Outcome == TestOutcome.Skipped);
    public int Total => Results.Count;
    public bool Success => !Diagnostics.HasErrors && Failed == 0;
}

public sealed class PackageTester(DiagnosticBag diagnostics)
{
    public PackageTestResult? Test(
        string manifestPath,
        IReadOnlyList<string>? additionalModuleSearchPaths = null,
        IReadOnlyList<string>? additionalAssemblyRefPaths = null,
        IReadOnlyDictionary<string, string>? additionalPackagePaths = null,
        IReadOnlyDictionary<string, string>? additionalModuleAliases = null)
    {
        additionalModuleSearchPaths ??= [];
        additionalAssemblyRefPaths ??= [];
        additionalPackagePaths ??= new Dictionary<string, string>();
        additionalModuleAliases ??= new Dictionary<string, string>();

        Log.Debug("PackageTester: testing from {ManifestPath}", manifestPath);

        if (!File.Exists(manifestPath))
        {
            diagnostics.Error($"Manifest not found: {manifestPath}", SourceSpan.None);
            return null;
        }

        var manifestDir = Path.GetDirectoryName(Path.GetFullPath(manifestPath))!;

        // 1. Parse manifest
        var manifestSource = File.ReadAllText(manifestPath);
        var parser = new ManifestParser(diagnostics);
        var manifest = parser.Parse(manifestSource, manifestPath);
        if (manifest is null)
            return null;

        if (manifest.Sources?.Test is null)
        {
            diagnostics.Error(
                "No test sources defined in manifest. Add (sources (test \"path\")) to your package.zspkg.",
                manifest.Span);
            return null;
        }

        var testDir = Path.GetFullPath(Path.Combine(manifestDir, manifest.Sources.Test));
        if (!Directory.Exists(testDir))
        {
            diagnostics.Error($"Test directory not found: {testDir}", manifest.Span);
            return null;
        }

        var testFiles = Directory.GetFiles(testDir, "*.zs", SearchOption.TopDirectoryOnly);
        if (testFiles.Length == 0)
        {
            diagnostics.Error($"No .zs test files found in: {testDir}", manifest.Span);
            return null;
        }

        Log.Debug("PackageTester: discovered {FileCount} test files in {TestDir}", testFiles.Length, testDir);

        // 2. Resolve dependencies (main + test combined)
        var moduleSearchPaths = new List<string>(additionalModuleSearchPaths);
        var packagePaths = new Dictionary<string, string>(additionalPackagePaths);
        var moduleAliases = new Dictionary<string, string>(additionalModuleAliases);

        var allZSchemeDeps = manifest.Dependencies.ZScheme
            .Concat(manifest.TestDependencies.ZScheme).ToList();
        if (allZSchemeDeps.Count > 0)
        {
            var zsResolver = new ZSchemeDependencyResolver(diagnostics, manifestDir);
            var depPaths = zsResolver.Resolve(allZSchemeDeps);
            if (diagnostics.HasErrors)
                return null;

            foreach (var depPath in depPaths)
            {
                var resolved = ResolvePackagePath(depPath);
                if (resolved is not null)
                {
                    moduleSearchPaths.Add(resolved.Value.SourceDir);
                    packagePaths.TryAdd(resolved.Value.Prefix, resolved.Value.SourceDir);
                    if (resolved.Value.DefaultModule is { } defMod)
                        moduleAliases.TryAdd(resolved.Value.Prefix, $"{resolved.Value.Prefix}/{defMod}");
                }
            }

            Log.Debug("PackageTester: resolved {Count} ZScheme dependencies", depPaths.Count);
        }

        // Add manifest-level ref paths
        var assemblyRefPaths = new List<string>(additionalAssemblyRefPaths);
        foreach (var refPath in manifest.Build.RefPaths)
            assemblyRefPaths.Add(Path.GetFullPath(Path.Combine(manifestDir, refPath)));

        // 3. Resolve NuGet dependencies (main + test + transitive from dependency manifests)
        var assemblySearchPaths = new List<string>(assemblyRefPaths);
        var allNuGetDeps = new List<NuGetDependency>(manifest.Dependencies.NuGet);
        allNuGetDeps.AddRange(manifest.TestDependencies.NuGet);

        // Scan dependency manifests for transitive NuGet deps (e.g., ZUnit needs xunit)
        foreach (var modPath in moduleSearchPaths)
        {
            var parentDir = Path.GetDirectoryName(modPath)!;
            foreach (var candidate in new[]
                     {
                         Path.Combine(parentDir, "package.zspkg"),
                         Path.Combine(modPath, "package.zspkg")
                     })
            {
                var fullCandidate = Path.GetFullPath(candidate);
                if (File.Exists(fullCandidate))
                {
                    var modDiag = new DiagnosticBag();
                    var modParser = new ManifestParser(modDiag);
                    var modManifest = modParser.Parse(File.ReadAllText(fullCandidate), fullCandidate);
                    if (modManifest is not null)
                        allNuGetDeps.AddRange(modManifest.Dependencies.NuGet);
                    break;
                }
            }
        }

        Log.Debug("PackageTester: {NuGetDepCount} total NuGet dependencies (including transitive)",
            allNuGetDeps.Count);

        if (allNuGetDeps.Count > 0)
        {
            var nugetResolver = new NuGetResolver(diagnostics);
            var nugetOutputDir = nugetResolver.Resolve(allNuGetDeps);
            if (nugetOutputDir is null && diagnostics.HasErrors)
                return null;
            if (nugetOutputDir is not null)
                assemblySearchPaths.Add(nugetOutputDir);
        }

        // 4. Compile main sources as library
        var mainOptions = new CompilerOptions
        {
            OutputMode = manifest.Build.Backend ?? OutputMode.Il,
            Namespace = manifest.Build.Namespace ?? "ZSchemeGenerated",
            AssemblySearchPaths = [..assemblySearchPaths]
        };

        var testSw = Stopwatch.StartNew();
        var libraryCompiler = new LibraryCompiler(diagnostics);
        var mainResult = libraryCompiler.Compile(manifestDir, manifest, mainOptions);
        if (mainResult is null)
            return null;

        Log.Debug("PackageTester: main library compiled in {ElapsedMs}ms, {ModuleCount} modules",
            testSw.ElapsedMilliseconds, mainResult.Modules.Count);

        // 5. Compile each test file as IL program
        var mainSourceDir = manifest.Sources?.Main is not null
            ? Path.GetFullPath(Path.Combine(manifestDir, manifest.Sources.Main))
            : manifestDir;

        var tempDir = Path.Combine(Path.GetTempPath(), $"zscheme-test-{Guid.NewGuid():N}"[..24]);
        Directory.CreateDirectory(tempDir);
        Log.Debug("PackageTester: created temp directory {TempDir}", tempDir);

        try
        {
            var testDlls = new List<string>();

            // Copy dependency assemblies to temp dir
            foreach (var searchPath in assemblySearchPaths)
                if (Directory.Exists(searchPath))
                    foreach (var dll in Directory.GetFiles(searchPath, "*.dll"))
                    {
                        var dest = Path.Combine(tempDir, Path.GetFileName(dll));
                        if (!File.Exists(dest))
                            File.Copy(dll, dest);
                    }

            // Copy precompiled dependency assemblies and metadata (e.g. stdlib from package cache)
            var precompiledInTempDir = new List<string>();
            foreach (var depPath in mainResult.PrecompiledDependencyPaths)
                if (File.Exists(depPath))
                {
                    var dest = Path.Combine(tempDir, Path.GetFileName(depPath));
                    if (!File.Exists(dest))
                        File.Copy(depPath, dest);
                    precompiledInTempDir.Add(dest);

                    // Copy metadata JSON so LoadExplicitPrecompiledPackages can resolve modules
                    var metaPath = Path.ChangeExtension(depPath, ".metadata.json");
                    if (File.Exists(metaPath))
                    {
                        var metaDest = Path.Combine(tempDir, Path.GetFileName(metaPath));
                        if (!File.Exists(metaDest))
                            File.Copy(metaPath, metaDest);
                    }
                }

            // Copy main library assembly and pre-load it so ClrInterop.FindType
            // can resolve types from it during test compilation (e.g., for 'new' expressions)
            if (mainResult.AssemblyBytes.Length > 0)
            {
                var mainDllPath = Path.Combine(tempDir, $"{manifest.Name}.dll");
                File.WriteAllBytes(mainDllPath, mainResult.AssemblyBytes);

                try
                {
                    AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(mainDllPath));
                    Log.Debug("PackageTester: pre-loaded main assembly {Path}", mainDllPath);
                }
                catch (Exception ex)
                {
                    Log.Debug("PackageTester: failed to pre-load main assembly: {Error}", ex.Message);
                }
            }

            var compilationFailures = new List<TestCaseResult>();

            foreach (var testFile in testFiles)
            {
                var testName = Path.GetFileNameWithoutExtension(testFile);
                Log.Debug("PackageTester: compiling test file {TestFile}", Path.GetFileName(testFile));
                var testSource = File.ReadAllText(testFile);

                var testOptions = new CompilerOptions
                {
                    OutputMode = OutputMode.Il,
                    AssemblySearchPaths = [tempDir, ..assemblySearchPaths],
                    ModuleSearchPaths = [mainSourceDir, testDir, ..moduleSearchPaths],
                    PackagePaths = new Dictionary<string, string>(packagePaths)
                    {
                        [manifest.ImportPrefix ?? ""] = mainSourceDir
                    },
                    ModuleAliases = new Dictionary<string, string>(moduleAliases),
                    DisablePrelude = false,
                    Namespace = manifest.Build.Namespace ?? "ZSchemeGenerated",
                    PrecompiledPackagePaths = [..precompiledInTempDir]
                };
                var compilation = new Compilation(testOptions);

                // Inject main library modules so they don't get recompiled from source.
                // Modules are source-imported (IR re-emitted in the test DLL) rather than
                // precompiled, since cross-assembly generic type references are not yet supported.
                foreach (var (name, mod) in mainResult.Modules)
                    compilation.InjectModule(name, mod);

                CompilationResult result;
                try
                {
                    result = compilation.Compile(testSource, testFile);
                }
                catch (Exception ex)
                {
                    diagnostics.Error($"Failed to compile: {Path.GetFileName(testFile)}: {ex.Message}",
                        SourceSpan.None);
                    continue;
                }

                if (!result.Success)
                {
                    foreach (var diag in result.Diagnostics.Diagnostics
                                 .Where(d => d.Severity == DiagnosticSeverity.Error))
                        diagnostics.Error(
                            $"Failed to compile {Path.GetFileName(testFile)}: {diag.Message}",
                            diag.Span);
                    continue;
                }

                if (result is CompilationResult.IlOutputResult ilResult)
                {
                    var testDllPath = Path.Combine(tempDir, $"{testName}.dll");
                    File.WriteAllBytes(testDllPath, ilResult.OutputBytes);
                    Log.Debug("PackageTester: wrote test DLL {TestDll} ({Length} bytes)",
                        Path.GetFileName(testDllPath), ilResult.OutputBytes.Length);
                    testDlls.Add(testDllPath);
                }
            }

            if (testDlls.Count == 0)
            {
                diagnostics.Error("No test assemblies produced.", SourceSpan.None);
                return null;
            }

            // 6. Run xUnit tests on each DLL
            var allResults = new List<TestCaseResult>();
            foreach (var testDll in testDlls)
                allResults.AddRange(RunXunitTests(testDll));

            var testResult = new PackageTestResult(allResults, diagnostics);
            Log.Debug("PackageTester: {Passed} passed, {Failed} failed, {Skipped} skipped ({Total} total)",
                testResult.Passed, testResult.Failed, testResult.Skipped, testResult.Total);

            return testResult;
        }
        finally
        {
            Log.Debug("PackageTester: cleaning up temp directory {TempDir}", tempDir);
            try
            {
                Directory.Delete(tempDir, true);
            }
            catch
            {
                /* best effort cleanup */
            }
        }
    }

    private static List<TestCaseResult> RunXunitTests(string testDllPath)
    {
        var loadContext = new AssemblyLoadContext("TestRunner", true);
        var testDir = Path.GetDirectoryName(testDllPath)!;

        loadContext.Resolving += (ctx, name) =>
        {
            var candidate = Path.Combine(testDir, name.Name + ".dll");
            return File.Exists(candidate) ? ctx.LoadFromAssemblyPath(candidate) : null;
        };

        var results = new List<TestCaseResult>();

        try
        {
            var asm = loadContext.LoadFromAssemblyPath(testDllPath);

            // GetTypes() may throw ReflectionTypeLoadException if referenced types
            // have broken IL (e.g., Nullable<Object> from erased nullable types).
            // Use the partial type list from the exception in that case.
            Type[] types;
            try
            {
                types = asm.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types.Where(t => t is not null).ToArray()!;
                Log.Debug("PackageTester: partial type load for {Assembly}, {LoadedCount} of {TotalCount} types loaded",
                    Path.GetFileName(testDllPath), types.Length, ex.Types.Length);
            }

            Log.Debug("PackageTester: loaded test assembly {Assembly}, {TypeCount} types",
                Path.GetFileName(testDllPath), types.Length);

            foreach (var type in types)
            {
                MethodInfo[] methods;
                try
                {
                    methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance);
                }
                catch
                {
                    continue; // Skip types that fail to load methods (broken IL)
                }

                foreach (var method in methods)
                {
                    bool hasFact;
                    try
                    {
                        hasFact = method.GetCustomAttributes(false)
                            .Any(a => a.GetType().FullName == "Xunit.FactAttribute");
                    }
                    catch
                    {
                        continue; // Skip methods with broken attribute metadata
                    }

                    if (!hasFact) continue;

                    var testName = $"{type.Name}.{method.Name}";
                    Log.Debug("PackageTester: running test {TestName}", testName);
                    try
                    {
                        var instance = Activator.CreateInstance(type);
                        method.Invoke(instance, null);
                        results.Add(new TestCaseResult(testName, TestOutcome.Passed, null));
                    }
                    catch (TargetInvocationException ex)
                    {
                        var inner = ex.InnerException?.Message ?? ex.Message;
                        results.Add(new TestCaseResult(testName, TestOutcome.Failed, inner));
                    }
                    catch (Exception ex)
                    {
                        results.Add(new TestCaseResult(testName, TestOutcome.Failed, ex.Message));
                    }
                }
            }
        }
        finally
        {
            loadContext.Unload();
        }

        return results;
    }

    private (string Prefix, string SourceDir, string? DefaultModule)? ResolvePackagePath(string packageDir)
    {
        Log.Debug("PackageTester.ResolvePackagePath: resolving {PackageDir}", packageDir);
        var fullDir = Path.GetFullPath(packageDir);
        var manifestPath = Path.Combine(fullDir, "package.zspkg");
        if (!File.Exists(manifestPath))
        {
            diagnostics.Error($"No package.zspkg found in: {fullDir}", SourceSpan.None);
            return null;
        }

        var diag = new DiagnosticBag();
        var parser = new ManifestParser(diag);
        var manifest = parser.Parse(File.ReadAllText(manifestPath), manifestPath);
        if (manifest is null || diag.HasErrors)
        {
            diagnostics.AddRange(diag);
            return null;
        }

        if (manifest.ImportPrefix is null)
        {
            diagnostics.Error($"Package at '{fullDir}' has no (import-prefix ...) defined", SourceSpan.None);
            return null;
        }

        var sourceDir = manifest.Sources?.Main is not null
            ? Path.GetFullPath(Path.Combine(fullDir, manifest.Sources.Main))
            : fullDir;

        Log.Debug(
            "PackageTester.ResolvePackagePath: resolved prefix={Prefix}, sourceDir={SourceDir}, defaultModule={DefaultModule}",
            manifest.ImportPrefix, sourceDir, manifest.DefaultModule);
        return (manifest.ImportPrefix, sourceDir, manifest.DefaultModule);
    }
}
