using System.Diagnostics;
using Serilog;
using ZScheme.Compiler.Codegen;
using ZScheme.Compiler.Diagnostics;
using ZScheme.Compiler.Package;
using ZScheme.Compiler.Pipeline;

namespace ZScheme.Cli;

internal static class BuildCommand
{
    public static int Run(string[] args)
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
                        _ => OutputMode.CSharp,
                    };
                    break;
                case "--ref" when i + 1 < args.Length:
                    overrides.AssemblySearchPaths.Add(Path.GetFullPath(args[++i]));
                    break;
                case "--module-path" when i + 1 < args.Length:
                    overrides.ModuleSearchPaths.Add(Path.GetFullPath(args[++i]));
                    break;
                case "--package-path" when i + 1 < args.Length:
                    var buildResolved = CliHelpers.ResolvePackagePath(args[++i]);
                    if (buildResolved is not null)
                    {
                        overrides.PackagePaths[buildResolved.Value.Prefix] = buildResolved
                            .Value
                            .SourceDir;
                        if (buildResolved.Value.DefaultModule is { } buildDefMod)
                            overrides.ModuleAliases[buildResolved.Value.Prefix] =
                                $"{buildResolved.Value.Prefix}/{buildDefMod}";
                    }

                    break;
                case "--precompiled" when i + 1 < args.Length:
                    overrides.PrecompiledPackagePaths.Add(Path.GetFullPath(args[++i]));
                    break;
            }

        Log.Debug(
            "build: manifest={ManifestPath}, outputOverride={OutputPath}, backendOverride={Backend}",
            manifestPath ?? "(auto-detect)",
            overrides.OutputPath,
            overrides.OutputMode
        );

        // Find manifest if not specified
        if (manifestPath is null)
        {
            var candidates = Directory.GetFiles(Directory.GetCurrentDirectory(), "*.zspkg");
            if (candidates.Length == 0)
            {
                Console.Error.WriteLine(
                    "No .zspkg manifest found in current directory. Use --manifest to specify one."
                );
                return 1;
            }

            if (candidates.Length > 1)
            {
                Console.Error.WriteLine(
                    "Multiple .zspkg files found. Use --manifest to specify one."
                );
                return 1;
            }

            manifestPath = candidates[0];
            Log.Debug("build: auto-detected manifest {ManifestPath}", manifestPath);
        }

        var diagnostics = new DiagnosticBag();
        var buildSw = Stopwatch.StartNew();
        var builder = new PackageBuilder(diagnostics);
        var result = builder.Build(manifestPath, overrides);
        Log.Debug(
            "build: completed in {ElapsedMs}ms, success={Success}",
            buildSw.ElapsedMilliseconds,
            result is not null && result.Success
        );

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
                        AssemblyReferences = csResult.PrecompiledAssemblyPaths,
                    };
                    File.WriteAllText(
                        csprojFile,
                        CSharpProjectGenerator.GenerateCsproj(projectOptions)
                    );
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

                CliHelpers.CopyPrecompiledAssemblies(
                    ilResult.PrecompiledAssemblyPaths,
                    Path.GetDirectoryName(outputFile)!
                );

                if (ilResult.IsExecutable)
                {
                    var runtimeConfigFile = Path.ChangeExtension(outputFile, ".runtimeconfig.json");
                    File.WriteAllText(
                        runtimeConfigFile,
                        BuildRuntimeConfig(ilResult.FrameworkReferences)
                    );
                    Console.WriteLine($"Generated: {runtimeConfigFile}");
                }

                break;
            }
        }

        return 0;
    }

    /// <summary>
    ///     Builds a <c>runtimeconfig.json</c> for an IL executable. With no declared shared
    ///     frameworks this emits a single <c>Microsoft.NETCore.App</c> framework. When the package
    ///     declares frameworks (e.g. <c>Microsoft.AspNetCore.App</c>, which transitively includes
    ///     the base runtime) those are emitted as a <c>frameworks</c> array so the host loads the
    ///     matching shared framework at launch. Versions use the running runtime's major.minor.0
    ///     and rely on roll-forward to the installed patch.
    /// </summary>
    private static string BuildRuntimeConfig(IReadOnlyList<string> frameworkReferences)
    {
        var version = Environment.Version;
        var tfm = $"net{version.Major}.{version.Minor}";
        var runtimeVersion = $"{version.Major}.{version.Minor}.0";

        if (frameworkReferences.Count == 0)
            return $$"""
                {
                  "runtimeOptions": {
                    "tfm": "{{tfm}}",
                    "framework": {
                      "name": "Microsoft.NETCore.App",
                      "version": "{{runtimeVersion}}"
                    }
                  }
                }
                """;

        var entries = string.Join(
            ",\n",
            frameworkReferences
                .Distinct()
                .Select(id =>
                    $$"""
                            {
                              "name": "{{id}}",
                              "version": "{{runtimeVersion}}"
                            }
                        """
                )
        );
        return $$"""
            {
              "runtimeOptions": {
                "tfm": "{{tfm}}",
                "frameworks": [
            {{entries}}
                ]
              }
            }
            """;
    }
}
