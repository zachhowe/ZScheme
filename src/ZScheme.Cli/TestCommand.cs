using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using Serilog;
using ZScheme.Compiler;
using ZScheme.Compiler.Diagnostics;
using ZScheme.Compiler.Package;
using ZScheme.Compiler.Pipeline;

namespace ZScheme.Cli;

internal static class TestCommand
{
    public static int Run(string[] args)
    {
        string? manifestPath = null;
        var moduleSearchPaths = new List<string>();
        var assemblyRefPaths = new List<string>();
        var testPackagePaths = new Dictionary<string, string>();
        var testModuleAliases = new Dictionary<string, string>();

        for (var i = 0; i < args.Length; i++)
            switch (args[i])
            {
                case "--manifest" or "-m" when i + 1 < args.Length:
                    manifestPath = args[++i];
                    break;
                case "--module-path" when i + 1 < args.Length:
                    moduleSearchPaths.Add(Path.GetFullPath(args[++i]));
                    break;
                case "--package-path" when i + 1 < args.Length:
                    var testResolved = CliHelpers.ResolvePackagePath(args[++i]);
                    if (testResolved is not null)
                    {
                        testPackagePaths[testResolved.Value.Prefix] = testResolved.Value.SourceDir;
                        if (testResolved.Value.DefaultModule is { } testDefMod)
                            testModuleAliases[testResolved.Value.Prefix] = $"{testResolved.Value.Prefix}/{testDefMod}";
                    }

                    break;
                case "--ref" when i + 1 < args.Length:
                    assemblyRefPaths.Add(Path.GetFullPath(args[++i]));
                    break;
            }

        Log.Debug("test: manifest={ManifestPath}, modulePaths={ModulePathCount}, packagePaths={PackagePathCount}",
            manifestPath ?? "(auto-detect)", moduleSearchPaths.Count, testPackagePaths.Count);

        // Find manifest if not specified
        if (manifestPath is null)
        {
            var candidates = Directory.GetFiles(Directory.GetCurrentDirectory(), "*.zspkg");
            if (candidates.Length == 0)
            {
                Console.Error.WriteLine(
                    "No .zspkg manifest found in current directory. Use --manifest to specify one.");
                return 1;
            }

            if (candidates.Length > 1)
            {
                Console.Error.WriteLine("Multiple .zspkg files found. Use --manifest to specify one.");
                return 1;
            }

            manifestPath = candidates[0];
        }

        if (!File.Exists(manifestPath))
        {
            Console.Error.WriteLine($"Manifest not found: {manifestPath}");
            return 1;
        }

        var manifestDir = Path.GetDirectoryName(Path.GetFullPath(manifestPath))!;
        var diagnostics = new DiagnosticBag();

        // Parse manifest
        var manifestSource = File.ReadAllText(manifestPath);
        var parser = new ManifestParser(diagnostics);
        var manifest = parser.Parse(manifestSource, manifestPath);
        if (manifest is null)
        {
            foreach (var diag in diagnostics.Diagnostics)
                Console.Error.WriteLine(diag);
            return 1;
        }

        if (manifest.Sources?.Test is null)
        {
            Console.Error.WriteLine(
                "No test sources defined in manifest. Add (sources (test \"path\")) to your package.zspkg.");
            return 1;
        }

        var testDir = Path.GetFullPath(Path.Combine(manifestDir, manifest.Sources.Test));
        if (!Directory.Exists(testDir))
        {
            Console.Error.WriteLine($"Test directory not found: {testDir}");
            return 1;
        }

        var testFiles = Directory.GetFiles(testDir, "*.zs", SearchOption.TopDirectoryOnly);
        if (testFiles.Length == 0)
        {
            Console.Error.WriteLine($"No .zs test files found in: {testDir}");
            return 1;
        }

        Log.Debug("test: discovered {FileCount} test files in {TestDir}", testFiles.Length, testDir);

        // Resolve ZScheme dependencies from manifest (main + test) for test compilation context
        var allZSchemeDeps = manifest.Dependencies.ZScheme
            .Concat(manifest.TestDependencies.ZScheme).ToList();
        if (allZSchemeDeps.Count > 0)
        {
            var testZsResolver = new ZSchemeDependencyResolver(diagnostics, manifestDir);
            var depPaths = testZsResolver.Resolve(allZSchemeDeps);
            if (diagnostics.HasErrors)
            {
                foreach (var diag in diagnostics.Diagnostics)
                    Console.Error.WriteLine(diag);
                return 1;
            }

            foreach (var depPath in depPaths)
            {
                var resolved = CliHelpers.ResolvePackagePath(depPath);
                if (resolved is not null)
                {
                    moduleSearchPaths.Add(resolved.Value.SourceDir);
                    testPackagePaths.TryAdd(resolved.Value.Prefix, resolved.Value.SourceDir);
                    if (resolved.Value.DefaultModule is { } defMod)
                        testModuleAliases.TryAdd(resolved.Value.Prefix, $"{resolved.Value.Prefix}/{defMod}");
                }
            }

            Log.Debug("test: resolved {Count} ZScheme dependencies for test context", depPaths.Count);
        }

        // Add manifest-level ref paths for CLR assembly resolution
        foreach (var refPath in manifest.Build.RefPaths)
            assemblyRefPaths.Add(Path.GetFullPath(Path.Combine(manifestDir, refPath)));

        // Resolve NuGet dependencies (include deps from module-path packages like ZUnit)
        var assemblySearchPaths = new List<string>(assemblyRefPaths);
        var allNuGetDeps = new List<NuGetDependency>(manifest.Dependencies.NuGet);
        allNuGetDeps.AddRange(manifest.TestDependencies.NuGet);

        // Resolve NuGet deps from module-path packages (e.g., ZUnit needs xunit)
        foreach (var modPath in moduleSearchPaths)
        {
            // Module path points to src/ subdir; manifest is in parent
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

        Log.Debug("test: {NuGetDepCount} total NuGet dependencies (including transitive from module-path packages)", allNuGetDeps.Count);

        if (allNuGetDeps.Count > 0)
        {
            var nugetResolver = new NuGetResolver(diagnostics);
            var nugetOutputDir = nugetResolver.Resolve(allNuGetDeps);
            if (nugetOutputDir is null && diagnostics.HasErrors)
            {
                foreach (var diag in diagnostics.Diagnostics)
                    Console.Error.WriteLine(diag);
                return 1;
            }

            if (nugetOutputDir is not null)
                assemblySearchPaths.Add(nugetOutputDir);
        }

        // 1. Compile main sources as library
        var mainOptions = new CompilerOptions
        {
            AssemblySearchPaths = [..assemblySearchPaths],
            UsePackageCache = false
        };

        var testSw = Stopwatch.StartNew();
        var libraryCompiler = new LibraryCompiler(diagnostics);
        var mainResult = libraryCompiler.Compile(manifestDir, manifest, mainOptions);
        if (mainResult is null)
        {
            foreach (var diag in diagnostics.Diagnostics)
                Console.Error.WriteLine(diag);
            return 1;
        }

        Log.Debug("test: main library compiled in {ElapsedMs}ms, {ModuleCount} modules", testSw.ElapsedMilliseconds, mainResult.Modules.Count);

        // 2. Compile each test file as a program with IL backend
        //    Test files use (module ...) but need prelude — inject main modules
        //    so the prelude finds them in cache, then compile normally.
        var mainSourceDir = manifest.Sources?.Main is not null
            ? Path.GetFullPath(Path.Combine(manifestDir, manifest.Sources.Main))
            : manifestDir;

        var tempDir = Path.Combine(Path.GetTempPath(), $"zscheme-test-{Guid.NewGuid():N}"[..24]);
        Directory.CreateDirectory(tempDir);
        Log.Debug("test: created temp directory {TempDir}", tempDir);
        try
        {
            var testDlls = new List<string>();

            // Copy dependency assemblies to temp dir (NuGet resolved + --ref paths)
            foreach (var searchPath in assemblySearchPaths)
                if (Directory.Exists(searchPath))
                    foreach (var dll in Directory.GetFiles(searchPath, "*.dll"))
                    {
                        var dest = Path.Combine(tempDir, Path.GetFileName(dll));
                        if (!File.Exists(dest))
                            File.Copy(dll, dest);
                    }

            // Copy precompiled dependency assemblies (e.g. stdlib from package cache)
            foreach (var depPath in mainResult.PrecompiledDependencyPaths)
                if (File.Exists(depPath))
                {
                    var dest = Path.Combine(tempDir, Path.GetFileName(depPath));
                    if (!File.Exists(dest))
                        File.Copy(depPath, dest);
                }

            // Copy main library assembly
            if (mainResult.AssemblyBytes.Length > 0)
            {
                var mainDllPath = Path.Combine(tempDir, $"{manifest.Name}.dll");
                File.WriteAllBytes(mainDllPath, mainResult.AssemblyBytes);
            }

            foreach (var testFile in testFiles)
            {
                var testName = Path.GetFileNameWithoutExtension(testFile);
                Log.Debug("test: compiling test file {TestFile}", Path.GetFileName(testFile));
                var testSource = File.ReadAllText(testFile);

                var testOptions = new CompilerOptions
                {
                    OutputMode = OutputMode.Il,
                    AssemblySearchPaths = [tempDir, ..assemblySearchPaths],
                    ModuleSearchPaths = [mainSourceDir, testDir, ..moduleSearchPaths],
                    PackagePaths = new Dictionary<string, string>(testPackagePaths)
                    {
                        [manifest.ImportPrefix ?? ""] = mainSourceDir
                    },
                    ModuleAliases = new Dictionary<string, string>(testModuleAliases),
                    DisablePrelude = false,
                    UsePackageCache = true,
                    Namespace = manifest.Build.Namespace ?? "ZSchemeGenerated"
                };
                var compilation = new Compilation(testOptions);

                // Inject main library modules so they don't get recompiled
                foreach (var (name, mod) in mainResult.Modules)
                    compilation.InjectModule(name, mod);

                CompilationResult result;
                try
                {
                    result = compilation.Compile(testSource, testFile);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Failed to compile: {Path.GetFileName(testFile)}");
                    Console.Error.WriteLine($"  {ex.Message}");
                    continue;
                }

                if (!result.Success)
                {
                    Console.Error.WriteLine($"Failed to compile: {Path.GetFileName(testFile)}");
                    foreach (var diag in result.Diagnostics.Diagnostics
                                 .Where(d => d.Severity == DiagnosticSeverity.Error))
                        Console.Error.WriteLine($"  {diag}");
                    continue;
                }

                if (result is CompilationResult.IlOutputResult ilResult)
                {
                    var testDllPath = Path.Combine(tempDir, $"{testName}.dll");
                    File.WriteAllBytes(testDllPath, ilResult.OutputBytes);
                    Log.Debug("test: wrote test DLL {TestDll} ({Length} bytes)", Path.GetFileName(testDllPath), ilResult.OutputBytes.Length);
                    testDlls.Add(testDllPath);
                }
            }

            if (testDlls.Count == 0)
            {
                Console.Error.WriteLine("No test assemblies produced.");
                return 1;
            }

            // Run tests using xunit runner on each test DLL
            int totalPassed = 0, totalFailed = 0, totalSkipped = 0;
            var allFailures = new List<string>();

            foreach (var testDll in testDlls)
            {
                var (p, f, s, failures) = RunXunitTests(testDll);
                totalPassed += p;
                totalFailed += f;
                totalSkipped += s;
                allFailures.AddRange(failures);
            }

            foreach (var f in allFailures)
                Console.Error.WriteLine(f);

            var total = totalPassed + totalFailed + totalSkipped;
            Log.Debug("test: {Passed} passed, {Failed} failed, {Skipped} skipped ({Total} total)", totalPassed, totalFailed, totalSkipped, total);
            Console.WriteLine(
                $"\nTests: {totalPassed} passed, {totalFailed} failed{(totalSkipped > 0 ? $", {totalSkipped} skipped" : "")} ({total} total)");
            return totalFailed > 0 ? 1 : 0;
        }
        finally
        {
            Log.Debug("test: cleaning up temp directory {TempDir}", tempDir);
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

    private static (int Passed, int Failed, int Skipped, List<string> Failures) RunXunitTests(string testDllPath)
    {
        var loadContext = new AssemblyLoadContext("TestRunner", true);
        var testDir = Path.GetDirectoryName(testDllPath)!;

        // Add resolver for assemblies in the test directory
        loadContext.Resolving += (ctx, name) =>
        {
            var candidate = Path.Combine(testDir, name.Name + ".dll");
            return File.Exists(candidate) ? ctx.LoadFromAssemblyPath(candidate) : null;
        };

        int passed = 0, failed = 0, skipped = 0;
        var failures = new List<string>();

        try
        {
            var asm = loadContext.LoadFromAssemblyPath(testDllPath);
            Log.Debug("xunit: loaded test assembly {Assembly}, {TypeCount} types", Path.GetFileName(testDllPath), asm.GetTypes().Length);

            foreach (var type in asm.GetTypes())
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                // Check for [Fact] attribute by name (avoids needing xunit reference)
                var hasFact = method.GetCustomAttributes(false)
                    .Any(a => a.GetType().FullName == "Xunit.FactAttribute");
                if (!hasFact) continue;

                var testName = $"{type.Name}.{method.Name}";
                Log.Debug("xunit: running test {TestName}", testName);
                try
                {
                    var instance = Activator.CreateInstance(type);
                    method.Invoke(instance, null);
                    passed++;
                    Console.WriteLine($"  PASS: {testName}");
                }
                catch (TargetInvocationException ex)
                {
                    failed++;
                    var inner = ex.InnerException?.Message ?? ex.Message;
                    failures.Add($"  FAIL: {testName}\n        {inner}");
                    Console.Error.WriteLine($"  FAIL: {testName}");
                }
                catch (Exception ex)
                {
                    failed++;
                    failures.Add($"  FAIL: {testName}\n        {ex.Message}");
                    Console.Error.WriteLine($"  FAIL: {testName}");
                }
            }
        }
        finally
        {
            loadContext.Unload();
        }

        return (passed, failed, skipped, failures);
    }
}
