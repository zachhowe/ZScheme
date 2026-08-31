using System.Diagnostics;
using Serilog;
using ZScheme.Compiler.Codegen;
using ZScheme.Compiler.Diagnostics;
using ZScheme.Compiler.Package;
using ZScheme.Compiler.Pipeline;

namespace ZScheme.Cli;

internal static class CompileCommand
{
    public static int Run(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine(
                "Usage: zs compile <file.zs> [--output <path>] [--backend cs|il] [--ref <dir>] [--module-path <dir>] [--package-path <dir>] [--precompiled <path>] [--emit-project] [--output-type Exe|Library] [--lang-version <ver>] [--nuget <PackageId>:<Version>] [--no-warn-unused-params] [--no-warn-unlooped-recursion] [--no-warn-deprecated-accessor-syntax]"
            );
            return 1;
        }

        var filePath = args[0];
        var outputPath = "output";
        var backend = OutputMode.CSharp;
        var assemblySearchPaths = new List<string>();
        var moduleSearchPaths = new List<string>();
        var packagePaths = new Dictionary<string, string>();
        var moduleAliases = new Dictionary<string, string>();
        var precompiledPaths = new List<string>();
        var emitProject = false;
        string? outputType = null;
        string? langVersion = null;
        var nugetPackages = new List<(string PackageId, string Version)>();
        var warnUnusedParams = true;
        var warnUnloopedRecursion = true;
        var warnDeprecatedAccessorSyntax = true;

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
                        _ => OutputMode.CSharp,
                    };
                    break;
                case "--ref" when i + 1 < args.Length:
                    assemblySearchPaths.Add(Path.GetFullPath(args[++i]));
                    break;
                case "--module-path" when i + 1 < args.Length:
                    moduleSearchPaths.Add(Path.GetFullPath(args[++i]));
                    break;
                case "--package-path" when i + 1 < args.Length:
                    var resolved = CliHelpers.ResolvePackagePath(args[++i]);
                    if (resolved is not null)
                    {
                        packagePaths[resolved.Value.Prefix] = resolved.Value.SourceDir;
                        if (resolved.Value.DefaultModule is { } defMod)
                            moduleAliases[resolved.Value.Prefix] =
                                $"{resolved.Value.Prefix}/{defMod}";
                    }

                    break;
                case "--precompiled" when i + 1 < args.Length:
                    precompiledPaths.Add(Path.GetFullPath(args[++i]));
                    break;
                case "--emit-project":
                    emitProject = true;
                    break;
                case "--no-warn-unused-params":
                    warnUnusedParams = false;
                    break;
                case "--no-warn-deprecated-accessor-syntax":
                    warnDeprecatedAccessorSyntax = false;
                    break;
                case "--no-warn-unlooped-recursion":
                    warnUnloopedRecursion = false;
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
                        Console.Error.WriteLine(
                            $"Invalid --nuget format: {args[i]} (expected PackageId:Version)"
                        );
                    break;
                }
            }

        Log.Debug(
            "compile: file={FilePath}, output={OutputPath}, backend={Backend}, refs={RefCount}, modulePaths={ModulePathCount}, packagePaths={PackagePathCount}, precompiled={PrecompiledCount}",
            filePath,
            outputPath,
            backend,
            assemblySearchPaths.Count,
            moduleSearchPaths.Count,
            packagePaths.Count,
            precompiledPaths.Count
        );

        // Resolve NuGet packages and add to assembly search paths
        if (nugetPackages.Count > 0)
        {
            var nugetDiagnostics = new DiagnosticBag();
            var nugetDeps = nugetPackages
                .Select(p => new NuGetDependency(p.PackageId, p.Version, SourceSpan.None))
                .ToList();
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
            PrecompiledPackagePaths = precompiledPaths,
            WarnUnusedParameters = warnUnusedParams,
            WarnUnloopedRecursion = warnUnloopedRecursion,
            WarnDeprecatedAccessorSyntax = warnDeprecatedAccessorSyntax,
        };
        var sw = Stopwatch.StartNew();
        var compilation = new Compilation(options);
        var result = compilation.Compile(source, filePath);
        Log.Debug(
            "compile: completed in {ElapsedMs}ms, success={Success}",
            sw.ElapsedMilliseconds,
            result.Success
        );

        if (!result.Success)
        {
            foreach (var diag in result.Diagnostics.Diagnostics)
                Console.Error.WriteLine(diag);
            return 1;
        }

        // Successful compiles can still carry warnings (e.g. non-exhaustive matches).
        foreach (var diag in result.Diagnostics.Diagnostics)
            if (!diag.IsError)
                Console.Error.WriteLine(diag);

        switch (result)
        {
            case CompilationResult.CSharpOutputResult csResult:
            {
                if (emitProject)
                {
                    var projectDir = Path.GetFullPath(outputPath);
                    var projectName = Path.GetFileName(projectDir);
                    var resolvedOutputType =
                        outputType ?? (csResult.IsExecutable ? "Exe" : "Library");
                    var projectOptions = new CSharpProjectOptions
                    {
                        OutputType = resolvedOutputType,
                        LangVersion = langVersion,
                        AssemblyReferences = csResult.PrecompiledAssemblyPaths,
                        NuGetPackages = nugetPackages,
                    };
                    var csFileName = $"{projectName}.cs";
                    CSharpProjectGenerator.WriteProjectDirectory(
                        projectDir,
                        projectName,
                        [(csFileName, csResult.CsOutput)],
                        projectOptions
                    );
                    Log.Debug("compile: wrote project to {OutputDir}", projectDir);
                    Console.WriteLine(
                        $"Generated: {Path.Combine(projectDir, $"{projectName}.csproj")}"
                    );
                    Console.WriteLine($"Generated: {Path.Combine(projectDir, csFileName)}");
                }
                else
                {
                    var outputFile = Path.ChangeExtension(outputPath, ".cs");
                    File.WriteAllText(outputFile, csResult.CsOutput);
                    Log.Debug(
                        "compile: wrote C# output to {OutputFile} ({Length} chars)",
                        outputFile,
                        csResult.CsOutput.Length
                    );
                    Console.WriteLine($"Generated: {outputFile}");

                    // Generate companion .csproj if precompiled assemblies are referenced
                    if (csResult.PrecompiledAssemblyPaths.Count > 0)
                    {
                        var csprojFile = Path.ChangeExtension(outputPath, ".csproj");
                        var projectOptions = new CSharpProjectOptions
                        {
                            AssemblyReferences = csResult.PrecompiledAssemblyPaths,
                        };
                        File.WriteAllText(
                            csprojFile,
                            CSharpProjectGenerator.GenerateCsproj(projectOptions)
                        );
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
                Log.Debug(
                    "compile: wrote IL output to {OutputFile} ({Length} bytes)",
                    outputFile,
                    ilResult.OutputBytes.Length
                );
                Console.WriteLine($"Generated: {outputFile}");

                // Copy precompiled assemblies alongside output
                CliHelpers.CopyPrecompiledAssemblies(
                    ilResult.PrecompiledAssemblyPaths,
                    Path.GetDirectoryName(outputFile)!
                );

                if (ilResult.IsExecutable)
                {
                    var runtimeConfigFile = Path.ChangeExtension(outputFile, ".runtimeconfig.json");
                    File.WriteAllText(runtimeConfigFile, ilResult.BuildRuntimeConfigJson());
                    Console.WriteLine($"Generated: {runtimeConfigFile}");
                }

                break;
            }
        }

        return 0;
    }
}
