using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using Serilog;
using Serilog.Events;
using ZScheme.Compiler;
using ZScheme.Compiler.Cache;
using ZScheme.Compiler.Diagnostics;
using ZScheme.Compiler.Package;
using ZScheme.Compiler.Codegen;
using ZScheme.Compiler.Pipeline;
using ZScheme.Compiler.Repl;

namespace ZScheme.Cli;

public static class Program
{
    public static int Main(string[] args)
    {
        var debug = args.Contains("--debug");
        if (debug)
        {
            args = args.Where(a => a != "--debug").ToArray();
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .WriteTo.Console(
                    outputTemplate: "[{Timestamp:HH:mm:ss.fff} {Level:u3}] {Message:lj}{NewLine}",
                    standardErrorFromLevel: LogEventLevel.Verbose)
                .CreateLogger();
        }

        try
        {
            if (args.Length == 0)
            {
                PrintUsage();
                return 0;
            }

            var command = args[0];
            Log.Debug("CLI: command={Command}, args={Args}", command, string.Join(" ", args[1..]));
            return command switch
            {
                "compile" => RunCompile(args[1..]),
                "build" => RunBuild(args[1..]),
                "install" => RunInstall(args[1..]),
                "test" => RunTest(args[1..]),
                "run" => RunExecute(args[1..]),
                "repl" => RunRepl(),
                "generate-project" => RunGenerateProject(args[1..]),
                "--version" or "-v" => PrintVersion(),
                "--help" or "-h" => PrintUsage(),
                _ => Error($"Unknown command: {command}")
            };
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    private static int RunCompile(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine(
                "Usage: zs compile <file.zs> [--output <path>] [--backend cs|il] [--ref <dir>] [--module-path <dir>] [--package-path <dir>] [--no-cache] [--precompiled <path>] [--emit-project] [--output-type Exe|Library] [--lang-version <ver>] [--nuget <PackageId>:<Version>]");
            return 1;
        }

        var filePath = args[0];
        var outputPath = "output";
        var backend = OutputMode.CSharp;
        var assemblySearchPaths = new List<string>();
        var moduleSearchPaths = new List<string>();
        var packagePaths = new Dictionary<string, string>();
        var moduleAliases = new Dictionary<string, string>();
        var useCache = true;
        var precompiledPaths = new List<string>();
        var emitProject = false;
        string? outputType = null;
        string? langVersion = null;
        var nugetPackages = new List<(string PackageId, string Version)>();

        for (var i = 1; i < args.Length; i++)
            switch (args[i])
            {
                case "--output" or "-o" when i + 1 < args.Length:
                    outputPath = args[++i];
                    break;
                case "--backend" or "-b" when i + 1 < args.Length:
                    backend = args[++i] switch
                    {
                        "il" => OutputMode.Il,
                        _ => OutputMode.CSharp
                    };
                    break;
                case "--ref" when i + 1 < args.Length:
                    assemblySearchPaths.Add(Path.GetFullPath(args[++i]));
                    break;
                case "--no-cache":
                    useCache = false;
                    break;
                case "--module-path" when i + 1 < args.Length:
                    moduleSearchPaths.Add(Path.GetFullPath(args[++i]));
                    break;
                case "--package-path" when i + 1 < args.Length:
                    var resolved = ResolvePackagePath(args[++i]);
                    if (resolved is not null)
                    {
                        packagePaths[resolved.Value.Prefix] = resolved.Value.SourceDir;
                        if (resolved.Value.DefaultModule is { } defMod)
                            moduleAliases[resolved.Value.Prefix] = $"{resolved.Value.Prefix}/{defMod}";
                    }

                    break;
                case "--precompiled" when i + 1 < args.Length:
                    precompiledPaths.Add(Path.GetFullPath(args[++i]));
                    break;
                case "--emit-project":
                    emitProject = true;
                    break;
                case "--output-type" when i + 1 < args.Length:
                    outputType = args[++i];
                    break;
                case "--lang-version" when i + 1 < args.Length:
                    langVersion = args[++i];
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

        Log.Debug("compile: file={FilePath}, output={OutputPath}, backend={Backend}, refs={RefCount}, modulePaths={ModulePathCount}, packagePaths={PackagePathCount}, cache={UseCache}, precompiled={PrecompiledCount}",
            filePath, outputPath, backend, assemblySearchPaths.Count, moduleSearchPaths.Count, packagePaths.Count, useCache, precompiledPaths.Count);

        // Resolve NuGet packages and add to assembly search paths
        if (nugetPackages.Count > 0)
        {
            var nugetDiagnostics = new DiagnosticBag();
            var nugetDeps = nugetPackages.Select(p =>
                new NuGetDependency(p.PackageId, p.Version, SourceSpan.None)).ToList();
            var resolver = new NuGetResolver(nugetDiagnostics);
            var nugetDir = resolver.Resolve(nugetDeps);
            if (nugetDir is not null)
                assemblySearchPaths.Add(nugetDir);
            if (nugetDiagnostics.HasErrors)
            {
                foreach (var diag in nugetDiagnostics.Diagnostics)
                    Console.Error.WriteLine(diag);
                return 1;
            }
        }

        if (!File.Exists(filePath))
        {
            Console.Error.WriteLine($"File not found: {filePath}");
            return 1;
        }

        var source = File.ReadAllText(filePath);
        Log.Debug("compile: read {SourceLength} chars from {FilePath}", source.Length, filePath);
        var options = new CompilerOptions
        {
            OutputMode = backend,
            OutputPath = outputPath,
            AssemblySearchPaths = assemblySearchPaths,
            ModuleSearchPaths = moduleSearchPaths,
            PackagePaths = packagePaths,
            ModuleAliases = moduleAliases,
            UsePackageCache = useCache,
            PrecompiledPackagePaths = precompiledPaths
        };
        var sw = Stopwatch.StartNew();
        var compilation = new Compilation(options);
        var result = compilation.Compile(source, filePath);
        Log.Debug("compile: completed in {ElapsedMs}ms, success={Success}", sw.ElapsedMilliseconds, result.Success);

        if (!result.Success)
        {
            foreach (var diag in result.Diagnostics.Diagnostics)
                Console.Error.WriteLine(diag);
            return 1;
        }

        switch (result)
        {
            case CompilationResult.CSharpOutputResult csResult:
            {
                if (emitProject)
                {
                    var projectDir = Path.GetFullPath(outputPath);
                    var projectName = Path.GetFileName(projectDir);
                    var resolvedOutputType = outputType ?? (csResult.IsExecutable ? "Exe" : "Library");
                    var projectOptions = new CSharpProjectOptions
                    {
                        OutputType = resolvedOutputType,
                        LangVersion = langVersion,
                        AssemblyReferences = csResult.PrecompiledAssemblyPaths,
                        NuGetPackages = nugetPackages
                    };
                    var csFileName = $"{projectName}.cs";
                    CSharpProjectGenerator.WriteProjectDirectory(
                        projectDir,
                        projectName,
                        [(csFileName, csResult.CsOutput)],
                        projectOptions);
                    Log.Debug("compile: wrote project to {OutputDir}", projectDir);
                    Console.WriteLine($"Generated: {Path.Combine(projectDir, $"{projectName}.csproj")}");
                    Console.WriteLine($"Generated: {Path.Combine(projectDir, csFileName)}");
                }
                else
                {
                    var outputFile = Path.ChangeExtension(outputPath, ".cs");
                    File.WriteAllText(outputFile, csResult.CsOutput);
                    Log.Debug("compile: wrote C# output to {OutputFile} ({Length} chars)", outputFile, csResult.CsOutput.Length);
                    Console.WriteLine($"Generated: {outputFile}");

                    // Generate companion .csproj if precompiled assemblies are referenced
                    if (csResult.PrecompiledAssemblyPaths.Count > 0)
                    {
                        var csprojFile = Path.ChangeExtension(outputPath, ".csproj");
                        var projectOptions = new CSharpProjectOptions
                        {
                            AssemblyReferences = csResult.PrecompiledAssemblyPaths
                        };
                        File.WriteAllText(csprojFile, CSharpProjectGenerator.GenerateCsproj(projectOptions));
                        Console.WriteLine($"Generated: {csprojFile}");
                    }
                }
                break;
            }
            case CompilationResult.IlOutputResult ilResult:
            {
                var extension = ilResult.IsExecutable ? ".exe" : ".dll";
                var outputFile = Path.ChangeExtension(outputPath, extension);
                File.WriteAllBytes(outputFile, ilResult.OutputBytes);
                Log.Debug("compile: wrote IL output to {OutputFile} ({Length} bytes)", outputFile, ilResult.OutputBytes.Length);
                Console.WriteLine($"Generated: {outputFile}");

                // Copy precompiled assemblies alongside output
                CopyPrecompiledAssemblies(ilResult.PrecompiledAssemblyPaths, Path.GetDirectoryName(outputFile)!);

                if (ilResult.IsExecutable)
                {
                    var runtimeConfigFile = Path.ChangeExtension(outputFile, ".runtimeconfig.json");
                    var version = Environment.Version;
                    var runtimeConfig = $$"""
                                          {
                                            "runtimeOptions": {
                                              "tfm": "net{{version.Major}}.{{version.Minor}}",
                                              "framework": {
                                                "name": "Microsoft.NETCore.App",
                                                "version": "{{version.Major}}.{{version.Minor}}.0"
                                              }
                                            }
                                          }
                                          """;
                    File.WriteAllText(runtimeConfigFile, runtimeConfig);
                    Console.WriteLine($"Generated: {runtimeConfigFile}");
                }
                break;
            }
        }

        return 0;
    }

    private static int RunBuild(string[] args)
    {
        string? manifestPath = null;
        var overrides = new CompilerOptions();

        for (var i = 0; i < args.Length; i++)
            switch (args[i])
            {
                case "--manifest" or "-m" when i + 1 < args.Length:
                    manifestPath = args[++i];
                    break;
                case "--output" or "-o" when i + 1 < args.Length:
                    overrides.OutputPath = args[++i];
                    break;
                case "--backend" or "-b" when i + 1 < args.Length:
                    overrides.OutputMode = args[++i] switch
                    {
                        "il" => OutputMode.Il,
                        _ => OutputMode.CSharp
                    };
                    break;
                case "--ref" when i + 1 < args.Length:
                    overrides.AssemblySearchPaths.Add(Path.GetFullPath(args[++i]));
                    break;
                case "--module-path" when i + 1 < args.Length:
                    overrides.ModuleSearchPaths.Add(Path.GetFullPath(args[++i]));
                    break;
                case "--package-path" when i + 1 < args.Length:
                    var buildResolved = ResolvePackagePath(args[++i]);
                    if (buildResolved is not null)
                    {
                        overrides.PackagePaths[buildResolved.Value.Prefix] = buildResolved.Value.SourceDir;
                        if (buildResolved.Value.DefaultModule is { } buildDefMod)
                            overrides.ModuleAliases[buildResolved.Value.Prefix] =
                                $"{buildResolved.Value.Prefix}/{buildDefMod}";
                    }

                    break;
                case "--no-cache":
                    overrides.UsePackageCache = false;
                    break;
                case "--precompiled" when i + 1 < args.Length:
                    overrides.PrecompiledPackagePaths.Add(Path.GetFullPath(args[++i]));
                    break;
            }

        Log.Debug("build: manifest={ManifestPath}, outputOverride={OutputPath}, backendOverride={Backend}",
            manifestPath ?? "(auto-detect)", overrides.OutputPath, overrides.OutputMode);

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
            Log.Debug("build: auto-detected manifest {ManifestPath}", manifestPath);
        }

        var diagnostics = new DiagnosticBag();
        var buildSw = Stopwatch.StartNew();
        var builder = new PackageBuilder(diagnostics);
        var result = builder.Build(manifestPath, overrides);
        Log.Debug("build: completed in {ElapsedMs}ms, success={Success}", buildSw.ElapsedMilliseconds, result is not null && result.Success);

        if (result is null || !result.Success)
        {
            var diags = result?.Diagnostics ?? diagnostics;
            foreach (var diag in diags.Diagnostics)
                Console.Error.WriteLine(diag);
            return 1;
        }

        var outputPath = overrides.OutputPath != "output" ? overrides.OutputPath : "output";
        var backend = overrides.OutputMode;

        switch (result)
        {
            case CompilationResult.CSharpOutputResult csResult:
            {
                var outputFile = Path.ChangeExtension(outputPath, ".cs");
                File.WriteAllText(outputFile, csResult.CsOutput);
                Console.WriteLine($"Generated: {outputFile}");

                if (csResult.PrecompiledAssemblyPaths.Count > 0)
                {
                    var csprojFile = Path.ChangeExtension(outputPath, ".csproj");
                    var projectOptions = new CSharpProjectOptions
                    {
                        AssemblyReferences = csResult.PrecompiledAssemblyPaths
                    };
                    File.WriteAllText(csprojFile, CSharpProjectGenerator.GenerateCsproj(projectOptions));
                    Console.WriteLine($"Generated: {csprojFile}");
                }
                break;
            }
            case CompilationResult.IlOutputResult ilResult:
            {
                var extension = ilResult.IsExecutable ? ".exe" : ".dll";
                var outputFile = Path.ChangeExtension(outputPath, extension);
                File.WriteAllBytes(outputFile, ilResult.OutputBytes);
                Console.WriteLine($"Generated: {outputFile}");

                CopyPrecompiledAssemblies(ilResult.PrecompiledAssemblyPaths, Path.GetDirectoryName(outputFile)!);

                if (ilResult.IsExecutable)
                {
                    var runtimeConfigFile = Path.ChangeExtension(outputFile, ".runtimeconfig.json");
                    var version = Environment.Version;
                    var runtimeConfig = $$"""
                                          {
                                            "runtimeOptions": {
                                              "tfm": "net{{version.Major}}.{{version.Minor}}",
                                              "framework": {
                                                "name": "Microsoft.NETCore.App",
                                                "version": "{{version.Major}}.{{version.Minor}}.0"
                                              }
                                            }
                                          }
                                          """;
                    File.WriteAllText(runtimeConfigFile, runtimeConfig);
                    Console.WriteLine($"Generated: {runtimeConfigFile}");
                }
                break;
            }
        }

        return 0;
    }

    private static int RunInstall(string[] args)
    {
        string? manifestPath = null;
        var packPackagePaths = new Dictionary<string, string>();
        var packModuleAliases = new Dictionary<string, string>();

        for (var i = 0; i < args.Length; i++)
            switch (args[i])
            {
                case "--manifest" or "-m" when i + 1 < args.Length:
                    manifestPath = args[++i];
                    break;
                case "--package-path" when i + 1 < args.Length:
                    var packResolved = ResolvePackagePath(args[++i]);
                    if (packResolved is not null)
                    {
                        packPackagePaths[packResolved.Value.Prefix] = packResolved.Value.SourceDir;
                        if (packResolved.Value.DefaultModule is { } packDefMod)
                            packModuleAliases[packResolved.Value.Prefix] =
                                $"{packResolved.Value.Prefix}/{packDefMod}";
                    }

                    break;
            }

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

        Log.Debug("install: manifest={ManifestPath}", manifestPath);

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

        Log.Debug("install: parsed manifest name={Name}, version={Version}, nugetDeps={NuGetCount}",
            manifest.Name, manifest.Version, manifest.Dependencies.NuGet.Count);

        // Resolve NuGet dependencies
        var assemblySearchPaths = new List<string>();
        if (manifest.Dependencies.NuGet.Count > 0)
        {
            var nugetResolver = new NuGetResolver(diagnostics);
            var nugetOutputDir = nugetResolver.Resolve(manifest.Dependencies.NuGet);
            if (nugetOutputDir is null && diagnostics.HasErrors)
            {
                foreach (var diag in diagnostics.Diagnostics)
                    Console.Error.WriteLine(diag);
                return 1;
            }

            if (nugetOutputDir is not null)
            {
                assemblySearchPaths.Add(nugetOutputDir);
                Log.Debug("install: resolved NuGet dependencies to {OutputDir}", nugetOutputDir);
            }
        }

        // Resolve ZScheme dependencies from manifest
        foreach (var dep in manifest.Dependencies.ZScheme)
        {
            if (dep.Source is ZSchemeDependencySource.Local local)
            {
                var depDir = Path.GetFullPath(Path.Combine(manifestDir, local.Path));
                var depResolved = ResolvePackagePath(depDir);
                if (depResolved is not null)
                {
                    packPackagePaths.TryAdd(depResolved.Value.Prefix, depResolved.Value.SourceDir);
                    if (depResolved.Value.DefaultModule is { } defMod)
                        packModuleAliases.TryAdd(depResolved.Value.Prefix,
                            $"{depResolved.Value.Prefix}/{defMod}");
                }
            }
        }

        var options = new CompilerOptions
        {
            AssemblySearchPaths = assemblySearchPaths,
            PackagePaths = packPackagePaths,
            ModuleAliases = packModuleAliases,
            UsePackageCache = false // We're building the cache, don't read from it
        };

        // Compile as library
        var installSw = Stopwatch.StartNew();
        var libraryCompiler = new LibraryCompiler(diagnostics);
        var result = libraryCompiler.Compile(manifestDir, manifest, options);
        Log.Debug("install: library compilation completed in {ElapsedMs}ms, success={Success}", installSw.ElapsedMilliseconds, result is not null);
        if (result is null)
        {
            foreach (var diag in diagnostics.Diagnostics)
                Console.Error.WriteLine(diag);
            return 1;
        }

        // Store in cache
        var cacheManager = new PackageCacheManager();
        cacheManager.Store(manifest.Name, manifest.Version, result.AssemblyBytes, result.Modules,
            manifest.ImportPrefix, manifest.DefaultModule);

        var cachePath = Path.Combine(ZSchemePaths.GetPackageCacheRoot(), manifest.Name, manifest.Version);
        Log.Debug("install: stored package {Name}@{Version} in cache at {CachePath}", manifest.Name, manifest.Version, cachePath);
        Console.WriteLine($"Package '{manifest.Name}' v{manifest.Version} cached at: {cachePath}");
        return 0;
    }

    private static int RunTest(string[] args)
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
                    var testResolved = ResolvePackagePath(args[++i]);
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
                var resolved = ResolvePackagePath(depPath);
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

    private static int RunExecute(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("Usage: zs run <file.zs>");
            return 1;
        }

        Console.Error.WriteLine("Direct execution not yet implemented. Use 'compile' + dotnet run.");
        return 1;
    }

    private static int RunRepl()
    {
        var repl = new Repl();
        repl.Run();
        return 0;
    }

    private static int RunGenerateProject(string[] args)
    {
        var outputDir = "output";
        string? projectOutputType = null;
        string? langVersion = null;
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

    private static void CopyPrecompiledAssemblies(IReadOnlyList<string> assemblyPaths, string outputDir)
    {
        foreach (var path in assemblyPaths)
        {
            var destPath = Path.Combine(outputDir, Path.GetFileName(path));
            if (path != destPath && File.Exists(path))
            {
                File.Copy(path, destPath, true);
                Console.WriteLine($"Copied: {Path.GetFileName(path)}");
            }
        }
    }

    private static int PrintVersion()
    {
        Console.WriteLine($"ZScheme Compiler {CompilerInfo.VersionString}");
        return 0;
    }

    private static int PrintUsage()
    {
        Console.WriteLine($"ZScheme Compiler {CompilerInfo.VersionString}");
        Console.WriteLine();
        Console.WriteLine("Usage: zs <command> [options]");
        Console.WriteLine();
        Console.WriteLine("Global options:");
        Console.WriteLine("  --debug                Enable debug logging (output to stderr)");
        Console.WriteLine();
        Console.WriteLine("Commands:");
        Console.WriteLine("  compile <file.zs>   Compile a ZScheme file");
        Console.WriteLine("  build               Build from a .zspkg package manifest");
        Console.WriteLine("  install             Compile a library package and cache it");
        Console.WriteLine("  test                Run package tests defined in manifest");
        Console.WriteLine("  run <file.zs>       Compile and run a ZScheme file");
        Console.WriteLine("  repl                Start interactive REPL");
        Console.WriteLine("  generate-project    Generate a .csproj project directory");
        Console.WriteLine();
        Console.WriteLine("Options (compile):");
        Console.WriteLine("  --output, -o <path>    Output path (default: output)");
        Console.WriteLine("  --backend, -b cs|il  Backend (default: cs)");
        Console.WriteLine("  --ref <dir>            Directory containing CLR assemblies (repeatable)");
        Console.WriteLine("  --module-path <dir>    Additional module search directory (repeatable)");
        Console.WriteLine("  --package-path <dir>    Register a package for qualified imports (repeatable)");
        Console.WriteLine("  --no-cache             Skip package cache lookup");
        Console.WriteLine("  --precompiled <path>   Reference a precompiled .dll (repeatable)");
        Console.WriteLine();
        Console.WriteLine("Options (build):");
        Console.WriteLine("  --manifest, -m <path>  Path to .zspkg manifest (default: auto-detect)");
        Console.WriteLine("  --output, -o <path>    Output path (overrides manifest)");
        Console.WriteLine("  --backend, -b cs|il  Backend (overrides manifest)");
        Console.WriteLine("  --ref <dir>            Assembly search directory (repeatable)");
        Console.WriteLine("  --module-path <dir>    Additional module search directory (repeatable)");
        Console.WriteLine("  --package-path <dir>    Register a package for qualified imports (repeatable)");
        Console.WriteLine("  --no-cache             Skip package cache lookup");
        Console.WriteLine("  --precompiled <path>   Reference a precompiled .dll (repeatable)");
        Console.WriteLine();
        Console.WriteLine("Options (install):");
        Console.WriteLine("  --manifest, -m <path>  Path to .zspkg manifest (default: auto-detect)");
        Console.WriteLine("  --package-path <dir>    Register a package for qualified imports (repeatable)");
        Console.WriteLine();
        Console.WriteLine("Options (test):");
        Console.WriteLine("  --manifest, -m <path>  Path to .zspkg manifest (default: auto-detect)");
        Console.WriteLine("  --module-path <dir>    Additional module search directory (repeatable)");
        Console.WriteLine("  --package-path <dir>    Register a package for qualified imports (repeatable)");
        return 0;
    }

    /// <summary>
    ///     Reads a package manifest from a directory to resolve the import prefix and source path.
    ///     Returns (importPrefix, sourceDir) or null if the manifest is missing or invalid.
    /// </summary>
    private static (string Prefix, string SourceDir, string? DefaultModule)? ResolvePackagePath(string packageDir)
    {
        Log.Debug("ResolvePackagePath: resolving {PackageDir}", packageDir);
        var fullDir = Path.GetFullPath(packageDir);
        var manifestPath = Path.Combine(fullDir, "package.zspkg");
        if (!File.Exists(manifestPath))
        {
            Console.Error.WriteLine($"No package.zspkg found in: {fullDir}");
            return null;
        }

        var diag = new DiagnosticBag();
        var parser = new ManifestParser(diag);
        var manifest = parser.Parse(File.ReadAllText(manifestPath), manifestPath);
        if (manifest is null || diag.HasErrors)
        {
            foreach (var d in diag.Diagnostics)
                Console.Error.WriteLine(d);
            return null;
        }

        if (manifest.ImportPrefix is null)
        {
            Console.Error.WriteLine($"Package at '{fullDir}' has no (import-prefix ...) defined");
            return null;
        }

        var sourceDir = manifest.Sources?.Main is not null
            ? Path.GetFullPath(Path.Combine(fullDir, manifest.Sources.Main))
            : fullDir;

        Log.Debug("ResolvePackagePath: resolved prefix={Prefix}, sourceDir={SourceDir}, defaultModule={DefaultModule}",
            manifest.ImportPrefix, sourceDir, manifest.DefaultModule);
        return (manifest.ImportPrefix, sourceDir, manifest.DefaultModule);
    }

    private static int Error(string message)
    {
        Console.Error.WriteLine(message);
        return 1;
    }
}
