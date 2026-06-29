using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using Serilog;
using ZScheme.Compiler.Codegen;
using ZScheme.Compiler.Diagnostics;
using ZScheme.Compiler.Pipeline;

namespace ZScheme.Compiler.Package;

public enum TestOutcome
{
    Passed,
    Failed,
    Skipped,
}

public sealed record TestCaseResult(string TestName, TestOutcome Outcome, string? FailureMessage);

/// <summary>
///     Asks the tester to measure code coverage and write a Cobertura report. The timestamp is
///     supplied by the caller so report generation stays deterministic.
/// </summary>
public sealed record CoverageRequest(string OutputPath, DateTimeOffset Timestamp);

public sealed record PackageTestResult(
    IReadOnlyList<TestCaseResult> Results,
    DiagnosticBag Diagnostics
)
{
    public int Passed => Results.Count(r => r.Outcome == TestOutcome.Passed);
    public int Failed => Results.Count(r => r.Outcome == TestOutcome.Failed);
    public int Skipped => Results.Count(r => r.Outcome == TestOutcome.Skipped);
    public int Total => Results.Count;
    public bool Success => !Diagnostics.HasErrors && Failed == 0;

    /// <summary>Set when a <see cref="CoverageRequest" /> produced a report.</summary>
    public CoverageSummary? Coverage { get; init; }

    /// <summary>Absolute path of the written Cobertura file, when coverage was requested.</summary>
    public string? CoverageOutputPath { get; init; }
}

public sealed class PackageTester(DiagnosticBag diagnostics)
{
    private static readonly ILogger Log = Serilog.Log.ForContext<PackageTester>();

    public async Task<PackageTestResult?> TestAsync(
        string manifestPath,
        IReadOnlyList<string>? additionalModuleSearchPaths = null,
        IReadOnlyList<string>? additionalAssemblyRefPaths = null,
        IReadOnlyDictionary<string, string>? additionalPackagePaths = null,
        IReadOnlyDictionary<string, string>? additionalModuleAliases = null,
        CoverageRequest? coverageRequest = null
    )
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
                manifest.Span
            );
            return null;
        }

        var testDir = Path.GetFullPath(Path.Combine(manifestDir, manifest.Sources.Test));
        if (!Directory.Exists(testDir))
        {
            diagnostics.Error($"Test directory not found: {testDir}", manifest.Span);
            return null;
        }

        var testFiles = Directory.GetFiles(testDir, "*.zs", SearchOption.AllDirectories);
        if (testFiles.Length == 0)
        {
            diagnostics.Error($"No .zs test files found in: {testDir}", manifest.Span);
            return null;
        }

        Log.Debug(
            "PackageTester: discovered {FileCount} test files in {TestDir}",
            testFiles.Length,
            testDir
        );

        // 2. Resolve dependencies (main + test combined)
        var moduleSearchPaths = new List<string>(additionalModuleSearchPaths);
        var packagePaths = new Dictionary<string, string>(additionalPackagePaths);
        var moduleAliases = new Dictionary<string, string>(additionalModuleAliases);

        // Walk the full transitive closure (main + test deps, plus every dep-of-a-dep) so a
        // transitive package's prefixed modules resolve without re-declaring them here.
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

        moduleSearchPaths.AddRange(closure.ModuleSearchPaths);
        foreach (var (prefix, path) in closure.PackagePaths)
            packagePaths.TryAdd(prefix, path);
        foreach (var (prefix, alias) in closure.ModuleAliases)
            moduleAliases.TryAdd(prefix, alias);

        Log.Debug(
            "PackageTester: resolved ZScheme dependencies (transitive), {Count} module search paths",
            closure.ModuleSearchPaths.Count
        );

        // Add manifest-level ref paths (main build config) plus any contributed by transitive deps.
        var assemblyRefPaths = new List<string>(additionalAssemblyRefPaths);
        if (manifest.Build.Main is { } mainBuild)
            foreach (var refPath in mainBuild.RefPaths)
                assemblyRefPaths.Add(Path.GetFullPath(Path.Combine(manifestDir, refPath)));
        assemblyRefPaths.AddRange(closure.RefPaths);

        // Add shared-framework directories (e.g. Microsoft.AspNetCore.App) declared via
        // (framework ...) — by the consumer or any transitive dep — so the ZScheme compiler
        // can resolve framework types when compiling main + test sources.
        assemblyRefPaths.AddRange(
            FrameworkResolver.Resolve(
                manifest.Dependencies.Frameworks.Concat(closure.Frameworks).ToList(),
                diagnostics
            )
        );

        // 3. Resolve NuGet dependencies (main + test + transitive from dependency manifests,
        //    the latter collected during the transitive-closure walk above).
        var assemblySearchPaths = new List<string>(assemblyRefPaths);
        var allNuGetDeps = new List<NuGetDependency>(manifest.Dependencies.NuGet);
        allNuGetDeps.AddRange(manifest.TestDependencies.NuGet);
        allNuGetDeps.AddRange(closure.NuGet);

        Log.Debug(
            "PackageTester: {NuGetDepCount} total NuGet dependencies (including transitive)",
            allNuGetDeps.Count
        );

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
            OutputMode = manifest.Build.Main?.Backend ?? OutputMode.Il,
            Namespace = manifest.Build.Main?.Namespace ?? "ZSchemeGenerated",
            AssemblySearchPaths = [.. assemblySearchPaths],
            PackagePaths = new Dictionary<string, string>(packagePaths),
            ModuleAliases = new Dictionary<string, string>(moduleAliases),
        };

        var testSw = Stopwatch.StartNew();
        var libraryCompiler = new LibraryCompiler(diagnostics);
        var mainResult = libraryCompiler.Compile(manifestDir, manifest, mainOptions);
        if (mainResult is null)
            return null;

        Log.Debug(
            "PackageTester: main library compiled in {ElapsedMs}ms, {ModuleCount} modules",
            testSw.ElapsedMilliseconds,
            mainResult.Modules.Count
        );

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
                    Log.Debug(
                        "PackageTester: failed to pre-load main assembly: {Error}",
                        ex.Message
                    );
                }
            }

            var compilationFailures = new List<TestCaseResult>();

            // Test-only ref paths (from (build (test (ref ...)))) — scoped to test compilation
            // so they do not leak into the already-completed main library build above.
            var testAssemblySearchPaths = new List<string>(assemblySearchPaths);
            if (manifest.Build.Test is { } testBuild)
                foreach (var refPath in testBuild.RefPaths)
                    testAssemblySearchPaths.Add(
                        Path.GetFullPath(Path.Combine(manifestDir, refPath))
                    );

            foreach (var testFile in testFiles)
            {
                var testName = Path.GetFileNameWithoutExtension(testFile);
                var testRel = Path.GetRelativePath(testDir, testFile);
                var testDllName = testRel
                    .Substring(0, testRel.Length - 3)
                    .Replace(Path.DirectorySeparatorChar, '_')
                    .Replace(Path.AltDirectorySeparatorChar, '_');
                Log.Debug(
                    "PackageTester: compiling test file {TestFile}",
                    Path.GetFileName(testFile)
                );
                var testSource = File.ReadAllText(testFile);

                var testOptions = new CompilerOptions
                {
                    OutputMode = OutputMode.Il,
                    AssemblySearchPaths = [tempDir, .. testAssemblySearchPaths],
                    ModuleSearchPaths = [mainSourceDir, testDir, .. moduleSearchPaths],
                    PackagePaths = new Dictionary<string, string>(packagePaths)
                    {
                        [manifest.ImportPrefix ?? ""] = mainSourceDir,
                    },
                    ModuleAliases = new Dictionary<string, string>(moduleAliases),
                    Namespace = manifest.Build.Test?.Namespace ?? "ZSchemeGenerated",
                    PrecompiledPackagePaths = [.. precompiledInTempDir],
                    // Instrument only the package's own main sources (re-emitted into the test DLL
                    // via InjectModule below); test files and precompiled stdlib/deps are excluded.
                    Coverage = coverageRequest is null
                        ? null
                        : new CoverageOptions
                        {
                            Enabled = true,
                            IncludePathPrefixes = [mainSourceDir],
                        },
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
                    diagnostics.Error(
                        $"Failed to compile: {Path.GetFileName(testFile)}: {ex.Message}",
                        SourceSpan.None
                    );
                    compilationFailures.Add(
                        new TestCaseResult(
                            $"{testName} (compilation)",
                            TestOutcome.Failed,
                            ex.Message
                        )
                    );
                    continue;
                }

                if (!result.Success)
                {
                    foreach (
                        var diag in result.Diagnostics.Diagnostics.Where(d =>
                            d.Severity == DiagnosticSeverity.Error
                        )
                    )
                        diagnostics.Error(
                            $"Failed to compile {Path.GetFileName(testFile)}: {diag.Message}",
                            diag.Span
                        );
                    compilationFailures.Add(
                        new TestCaseResult(
                            $"{testName} (compilation)",
                            TestOutcome.Failed,
                            "Test file failed to compile"
                        )
                    );
                    continue;
                }

                if (result is CompilationResult.IlOutputResult ilResult)
                {
                    var testDllPath = Path.Combine(tempDir, $"{testDllName}.dll");
                    File.WriteAllBytes(testDllPath, ilResult.OutputBytes);
                    Log.Debug(
                        "PackageTester: wrote test DLL {TestDll} ({Length} bytes)",
                        Path.GetFileName(testDllPath),
                        ilResult.OutputBytes.Length
                    );
                    testDlls.Add(testDllPath);
                }
            }

            if (testDlls.Count == 0)
            {
                diagnostics.Error("No test assemblies produced.", SourceSpan.None);
                if (compilationFailures.Count > 0)
                    return new PackageTestResult(compilationFailures, diagnostics);
                return null;
            }

            // 6. Run xUnit tests on each DLL
            var allResults = new List<TestCaseResult>(compilationFailures);
            var coverage = coverageRequest is null ? null : new CoverageAggregator();
            foreach (var testDll in testDlls)
                allResults.AddRange(await RunXunitTestsAsync(testDll, coverage));

            // 7. Write the merged coverage report (data was read out of each DLL before unload).
            CoverageSummary? coverageSummary = null;
            string? coverageOutputPath = null;
            if (coverageRequest is not null && coverage is { HasData: true })
            {
                var report = coverage.BuildReport(manifest.Name, coverageRequest.Timestamp);
                coverageOutputPath = Path.GetFullPath(coverageRequest.OutputPath);
                CoberturaWriter.Write(report, coverageOutputPath);
                coverageSummary = coverage.Summarize();
                Log.Debug("PackageTester: wrote coverage report to {Path}", coverageOutputPath);
            }

            var testResult = new PackageTestResult(allResults, diagnostics)
            {
                Coverage = coverageSummary,
                CoverageOutputPath = coverageOutputPath,
            };
            Log.Debug(
                "PackageTester: {Passed} passed, {Failed} failed, {Skipped} skipped ({Total} total)",
                testResult.Passed,
                testResult.Failed,
                testResult.Skipped,
                testResult.Total
            );

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

    private static async Task<List<TestCaseResult>> RunXunitTestsAsync(
        string testDllPath,
        CoverageAggregator? coverage
    )
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
                Log.Debug(
                    "PackageTester: partial type load for {Assembly}, {LoadedCount} of {TotalCount} types loaded",
                    Path.GetFileName(testDllPath),
                    types.Length,
                    ex.Types.Length
                );
            }

            Log.Debug(
                "PackageTester: loaded test assembly {Assembly}, {TypeCount} types",
                Path.GetFileName(testDllPath),
                types.Length
            );

            foreach (var type in types)
            {
                MethodInfo[] methods;
                try
                {
                    // Include Static so top-level (test|theory)-case forms, which compile
                    // to public static methods, are discovered alongside test-suite
                    // classes (which produce instance methods).
                    methods = type.GetMethods(
                        BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static
                    );
                }
                catch
                {
                    continue; // Skip types that fail to load methods (broken IL)
                }

                foreach (var method in methods)
                {
                    IList<CustomAttributeData> attrs;
                    try
                    {
                        // Use CustomAttributeData (metadata-only) rather than GetCustomAttributes,
                        // which instantiates attributes and can fault on transitive xunit.v3 deps.
                        attrs = CustomAttributeData.GetCustomAttributes(method);
                    }
                    catch (Exception ex)
                    {
                        Log.Debug(
                            "PackageTester: attribute scan failed for {Type}.{Method}: {Error}",
                            type.Name,
                            method.Name,
                            ex.Message
                        );
                        continue;
                    }

                    var hasFact = attrs.Any(a => a.AttributeType.FullName == "Xunit.FactAttribute");
                    var hasTheory = attrs.Any(a =>
                        a.AttributeType.FullName == "Xunit.TheoryAttribute"
                    );
                    if (!hasFact && !hasTheory)
                        continue;

                    var testBase = $"{type.Name}.{method.Name}";
                    var invocations = new List<(string Name, object?[]? Args)>();
                    if (hasTheory)
                    {
                        var inlineData = attrs
                            .Where(a => a.AttributeType.FullName == "Xunit.InlineDataAttribute")
                            .ToList();
                        if (inlineData.Count == 0)
                        {
                            results.Add(
                                new TestCaseResult(
                                    testBase,
                                    TestOutcome.Failed,
                                    "theory has no inline data"
                                )
                            );
                            continue;
                        }

                        foreach (var ida in inlineData)
                        {
                            var args = ExtractInlineArgs(ida);
                            var caseName =
                                $"{testBase}({string.Join(", ", args.Select(a => a?.ToString() ?? "null"))})";
                            invocations.Add((caseName, args));
                        }
                    }
                    else
                    {
                        invocations.Add((testBase, null));
                    }

                    foreach (var (name, args) in invocations)
                    {
                        Log.Debug("PackageTester: running test {TestName}", name);
                        try
                        {
                            var instance = method.IsStatic ? null : Activator.CreateInstance(type);
                            var returnValue = method.Invoke(instance, args);

                            // test-case-async / theory-case-async return Task; await
                            // so continuations (and their assertions) actually run.
                            if (returnValue is Task task)
                                await task;

                            results.Add(new TestCaseResult(name, TestOutcome.Passed, null));
                        }
                        catch (TargetInvocationException ex)
                        {
                            // Sync throws from Invoke are wrapped; async throws
                            // come out of `await task` unwrapped (caught below).
                            var inner = ex.InnerException?.Message ?? ex.Message;
                            results.Add(new TestCaseResult(name, TestOutcome.Failed, inner));
                        }
                        catch (Exception ex)
                        {
                            results.Add(new TestCaseResult(name, TestOutcome.Failed, ex.Message));
                        }
                    }
                }
            }

            // Read coverage out of this DLL's self-contained __ZSchemeCoverage class BEFORE the
            // load context is unloaded; copying the values into the aggregator detaches them.
            if (coverage is not null)
                CollectCoverage(types, coverage);
        }
        finally
        {
            loadContext.Unload();
        }

        return results;

        static object?[] ExtractInlineArgs(CustomAttributeData attr)
        {
            if (attr.ConstructorArguments.Count == 0)
                return [];
            // InlineDataAttribute(params object[] data): metadata reifies the array
            // as a nested list of typed arguments whose .Value holds the boxed primitive.
            if (
                attr.ConstructorArguments[0].Value
                is IReadOnlyList<CustomAttributeTypedArgument> list
            )
                return list.Select(a => a.Value).ToArray();
            return attr.ConstructorArguments.Select(a => a.Value).ToArray();
        }
    }

    /// <summary>
    ///     Reflects the <c>__ZSchemeCoverage</c> class baked into a test assembly, reading its
    ///     <c>Hits</c> counters and <c>Meta</c> table, and folds them into the aggregator. Accessing
    ///     the static fields triggers the type's constructor, so even a DLL whose tests touched no
    ///     instrumented code still contributes its full (all-zero) point set — keeping never-run
    ///     lines visible in the merged report.
    /// </summary>
    private static void CollectCoverage(IEnumerable<Type> types, CoverageAggregator aggregator)
    {
        var covType = types.FirstOrDefault(t => t.Name == CoverageContract.TypeName);
        if (covType is null)
            return;

        try
        {
            var hits =
                covType
                    .GetField(CoverageContract.HitsField, BindingFlags.Public | BindingFlags.Static)
                    ?.GetValue(null) as int[];
            var meta =
                covType
                    .GetField(CoverageContract.MetaField, BindingFlags.Public | BindingFlags.Static)
                    ?.GetValue(null) as string;
            aggregator.Add(hits, CoverageContract.ParseMeta(meta));
        }
        catch (Exception ex)
        {
            Log.Debug("PackageTester: failed to read coverage data: {Error}", ex.Message);
        }
    }
}
