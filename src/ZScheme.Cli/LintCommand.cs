using Serilog;
using ZScheme.Compiler.Analysis;
using ZScheme.Compiler.Diagnostics;
using ZScheme.Compiler.Package;
using ZScheme.Compiler.Pipeline;

namespace ZScheme.Cli;

/// <summary>
///     <c>zs lint</c> — style analysis that no compile path runs, reported (and optionally
///     applied) over a whole package rather than one editor buffer at a time. Currently one
///     rule: ZS0004, the redundant namespace qualifier
///     (<see cref="RedundantTypeQualifierAnalyzer" />).
///     <para>
///         Each file is type-checked on its own with <c>StopAfterTypeInference</c>, the way the
///         language server checks the open document — the analyzer needs the compilation's
///         <c>TypeNameCanonicalizer</c> to prove a short spelling resolves to the same type, and
///         that only exists once stage 4 has run.
///     </para>
/// </summary>
internal static class LintCommand
{
    public static int Run(string[] args)
    {
        string? manifestPath = null;
        var fix = false;
        var paths = new List<string>();
        var extra = new ExtraInputs();

        for (var i = 0; i < args.Length; i++)
            switch (args[i])
            {
                case "--manifest" or "-m" when i + 1 < args.Length:
                    manifestPath = args[++i];
                    break;
                case "--fix":
                    fix = true;
                    break;
                case "--module-path" when i + 1 < args.Length:
                    extra.ModuleSearchPaths.Add(Path.GetFullPath(args[++i]));
                    break;
                case "--ref" when i + 1 < args.Length:
                    extra.AssemblySearchPaths.Add(Path.GetFullPath(args[++i]));
                    break;
                case "--package-path" when i + 1 < args.Length:
                    var resolved = CliHelpers.ResolvePackagePath(args[++i]);
                    if (resolved is null)
                        return 1;
                    extra.PackagePaths[resolved.Value.Prefix] = resolved.Value.SourceDir;
                    if (resolved.Value.DefaultModule is { } defaultModule)
                        extra.ModuleAliases[resolved.Value.Prefix] =
                            $"{resolved.Value.Prefix}/{defaultModule}";
                    break;
                default:
                    if (args[i].StartsWith('-'))
                        return CliHelpers.Error($"Unknown option: {args[i]}");
                    paths.Add(args[i]);
                    break;
            }

        Log.Debug(
            "lint: manifest={ManifestPath}, paths={PathCount}, fix={Fix}",
            manifestPath ?? "(auto-detect)",
            paths.Count,
            fix
        );

        if (manifestPath is not null && !File.Exists(manifestPath))
            return CliHelpers.Error($"Manifest not found: {manifestPath}");

        var groups = ResolveGroups(paths, manifestPath);
        if (groups is null)
            return 1;
        if (groups.Count == 0)
            return CliHelpers.Error("No .zs files to lint.");

        var totalIssues = 0;
        var totalFixed = 0;
        var filesWithIssues = 0;
        var failed = 0;
        var declined = 0;

        foreach (var group in groups)
        {
            var context = LintContext.Create(group.ManifestPath, extra);
            if (context is null)
            {
                // The whole group is skipped, so count every file in it — one failure would
                // understate how much of the run did not happen.
                failed += group.Files.Count;
                continue;
            }

            foreach (var file in group.Files)
            {
                var source = File.ReadAllText(file);
                var hints = Analyze(context, file, source);
                if (hints is null)
                {
                    failed++;
                    continue;
                }

                if (hints.Count == 0)
                    continue;

                filesWithIssues++;
                totalIssues += hints.Count;

                if (!fix)
                {
                    foreach (var hint in hints)
                        Console.WriteLine(Format(hint));
                    continue;
                }

                var (text, applied) = RedundantTypeQualifierFixer.Apply(source, hints);
                if (applied > 0)
                {
                    File.WriteAllText(file, text);
                    totalFixed += applied;
                    Console.WriteLine(
                        $"{Display(file)}: {applied} fix{(applied == 1 ? "" : "es")} applied"
                    );
                }

                // The fixer only declines a span it cannot place in the source, which should not
                // happen — say so rather than let a partial sweep read as a clean one.
                if (applied < hints.Count)
                {
                    declined += hints.Count - applied;
                    Console.Error.WriteLine(
                        $"{Display(file)}: {hints.Count - applied} hint(s) could not be applied"
                    );
                }
            }
        }

        if (fix)
            Console.WriteLine(
                totalFixed == 0
                    ? "No fixes applied."
                    : $"{totalFixed} fix{(totalFixed == 1 ? "" : "es")} applied in {filesWithIssues} file{(filesWithIssues == 1 ? "" : "s")}."
            );
        else if (totalIssues == 0)
            Console.WriteLine("No issues found.");
        else
            Console.WriteLine(
                $"{totalIssues} issue{(totalIssues == 1 ? "" : "s")} in {filesWithIssues} file{(filesWithIssues == 1 ? "" : "s")} (run with --fix to apply)"
            );

        if (failed > 0)
            Console.Error.WriteLine(
                $"{failed} file{(failed == 1 ? "" : "s")} could not be analyzed."
            );

        if (failed > 0 || declined > 0)
            return 1;
        return !fix && totalIssues > 0 ? 1 : 0;
    }

    /// <summary>Runs the analyzer over one file, or returns null when the file did not
    ///     type-check far enough to analyze (its errors are printed).</summary>
    private static IReadOnlyList<Diagnostic>? Analyze(
        LintContext context,
        string file,
        string source
    )
    {
        var compilation = new Compilation(context.OptionsFor(file));
        compilation.Compile(source, file);

        // Stage 4 is where the canonicalizer is built; without it there is nothing to prove a
        // short spelling against, and a file that failed earlier must not be rewritten.
        if (compilation.Canonicalizer is not { } canonicalizer)
        {
            Console.Error.WriteLine($"{Display(file)}: could not be analyzed");
            foreach (var diag in compilation.GetDiagnostics().Diagnostics.Where(d => d.IsError))
                Console.Error.WriteLine($"  {diag}");
            return null;
        }

        // A bag of its own: the compilation's own warnings are not lint's output.
        var hints = new DiagnosticBag();
        new RedundantTypeQualifierAnalyzer(hints).Analyze(source, file, canonicalizer);
        return
        [
            .. hints
                .Diagnostics.Where(d => d.Code == DiagnosticCodes.RedundantTypeQualifier)
                .OrderBy(d => d.Span.Line)
                .ThenBy(d => d.Span.Column),
        ];
    }

    private static string Format(Diagnostic hint)
    {
        return $"{Display(hint.Span.File)}({hint.Span.Line}:{hint.Span.Column}): "
            + $"hint {hint.Code}: {hint.Message}";
    }

    private static string Display(string path)
    {
        var relative = Path.GetRelativePath(Directory.GetCurrentDirectory(), path);
        return relative.StartsWith("..", StringComparison.Ordinal) ? path : relative;
    }

    private sealed record LintGroup(string? ManifestPath, IReadOnlyList<string> Files);

    /// <summary>Resolution inputs from the command line, layered over whatever a group's
    ///     manifest resolves to. The only way to give a file outside any package the context it
    ///     needs, and the way to point ZS0004 at an assembly the manifest does not name — the
    ///     canonicalizer can only confirm a shortening for a namespace it can actually load.</summary>
    private sealed class ExtraInputs
    {
        public List<string> ModuleSearchPaths { get; } = [];
        public List<string> AssemblySearchPaths { get; } = [];
        public Dictionary<string, string> PackagePaths { get; } = new();
        public Dictionary<string, string> ModuleAliases { get; } = new();
    }

    /// <summary>
    ///     Groups the files to lint by the package that owns them, so each is checked with its
    ///     own manifest's dependencies and module names. An explicit <c>--manifest</c> puts every
    ///     file in that one group; otherwise the owning manifest is found by walking up from each
    ///     file (a file under no package lints standalone, in a null group). Returns null on a
    ///     usage error, which has already been reported.
    /// </summary>
    private static List<LintGroup>? ResolveGroups(List<string> paths, string? manifestPath)
    {
        if (paths.Count == 0)
        {
            // No paths: lint the package in the current directory, the way build/test/install
            // pick up their manifest.
            if (manifestPath is null)
            {
                var candidates = Directory.GetFiles(Directory.GetCurrentDirectory(), "*.zspkg");
                if (candidates.Length == 0)
                {
                    Console.Error.WriteLine(
                        "No .zspkg manifest found in current directory. Use --manifest or pass paths to lint."
                    );
                    return null;
                }

                if (candidates.Length > 1)
                {
                    Console.Error.WriteLine(
                        "Multiple .zspkg files found. Use --manifest to specify one."
                    );
                    return null;
                }

                manifestPath = candidates[0];
            }

            var sourceDirs = PackageSourceDirs(manifestPath);
            if (sourceDirs is null)
                return null;

            return
            [
                new LintGroup(manifestPath, [.. sourceDirs.SelectMany(ZsFilesUnder).Distinct()]),
            ];
        }

        var files = new List<string>();
        foreach (var path in paths)
        {
            var full = Path.GetFullPath(path);
            if (Directory.Exists(full))
                files.AddRange(ZsFilesUnder(full));
            else if (File.Exists(full))
                files.Add(full);
            else
            {
                Console.Error.WriteLine($"Path not found: {path}");
                return null;
            }
        }

        return
        [
            .. files
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .GroupBy(f => manifestPath ?? FindOwningManifest(Path.GetDirectoryName(f)))
                .Select(g => new LintGroup(g.Key, g.ToList())),
        ];
    }

    private static List<string> ZsFilesUnder(string dir)
    {
        return [.. Directory.GetFiles(dir, "*.zs", SearchOption.AllDirectories).Order()];
    }

    /// <summary>The nearest <c>.zspkg</c> at or above <paramref name="dir" />, or null when the
    ///     file lives outside any package. An ambiguous directory (more than one manifest) is
    ///     skipped rather than guessed at.</summary>
    private static string? FindOwningManifest(string? dir)
    {
        for (var current = dir; current is not null; current = Path.GetDirectoryName(current))
        {
            var candidates = Directory.GetFiles(current, "*.zspkg");
            if (candidates.Length == 1)
                return candidates[0];
        }

        return null;
    }

    /// <summary>Every directory a package's own sources live in — <c>sources.main</c> (or the
    ///     package root) plus <c>sources.test</c> when declared. Test sources are lint's business
    ///     too, and the context below resolves the test dependency closure so they check.</summary>
    private static List<string>? PackageSourceDirs(string manifestPath)
    {
        var manifest = ParseManifest(manifestPath);
        if (manifest is null)
            return null;

        var packageDir = Path.GetDirectoryName(Path.GetFullPath(manifestPath))!;
        var dirs = new List<string>
        {
            manifest.Sources?.Main is { } main
                ? Path.GetFullPath(Path.Combine(packageDir, main))
                : packageDir,
        };

        if (manifest.Sources?.Test is { } test)
        {
            var testDir = Path.GetFullPath(Path.Combine(packageDir, test));
            if (Directory.Exists(testDir))
                dirs.Add(testDir);
        }

        return dirs;
    }

    private static PackageManifest? ParseManifest(string manifestPath)
    {
        var diagnostics = new DiagnosticBag();
        var manifest = new ManifestParser(diagnostics).Parse(
            File.ReadAllText(manifestPath),
            manifestPath
        );
        if (manifest is null)
        {
            Console.Error.WriteLine($"Failed to parse manifest: {manifestPath}");
            foreach (var diag in diagnostics.Diagnostics.Where(d => d.IsError))
                Console.Error.WriteLine($"  {diag}");
        }

        return manifest;
    }

    /// <summary>
    ///     The per-package compilation setup, resolved once and reused for every file in it —
    ///     the dependency closure walk and NuGet restore behind
    ///     <see cref="PackageOptionsBuilder.Resolve" /> are far too expensive to repeat per file.
    /// </summary>
    private sealed class LintContext
    {
        private ResolvedPackageInputs? _inputs;
        private ExtraInputs _extra = new();
        private Dictionary<string, string> _aliases = new();
        private Dictionary<string, string> _packagePaths = new();
        private string? _sourceDir;
        private string? _prefix;

        /// <summary>Null when the package's manifest could not be parsed or resolved (already
        ///     reported). A null <paramref name="manifestPath" /> gives a standalone context, for
        ///     files that belong to no package.</summary>
        public static LintContext? Create(string? manifestPath, ExtraInputs extra)
        {
            var context = new LintContext { _extra = extra };
            foreach (var (prefix, path) in extra.PackagePaths)
                context._packagePaths[prefix] = path;
            foreach (var (alias, target) in extra.ModuleAliases)
                context._aliases[alias] = target;
            if (manifestPath is null)
                return context;

            var manifest = ParseManifest(manifestPath);
            if (manifest is null)
                return null;

            var manifestDir = Path.GetDirectoryName(Path.GetFullPath(manifestPath))!;
            var diagnostics = new DiagnosticBag();
            var inputs = PackageOptionsBuilder.Resolve(
                manifestDir,
                WithTestDependencies(manifest),
                diagnostics
            );
            if (inputs is null)
            {
                Console.Error.WriteLine($"Failed to resolve package inputs: {manifestPath}");
                foreach (var diag in diagnostics.Diagnostics.Where(d => d.IsError))
                    Console.Error.WriteLine($"  {diag}");
                return null;
            }

            // TryAdd throughout: an explicit command-line --package-path wins over what the
            // manifest resolves to, the same way it does for build and test.
            context._inputs = inputs;
            foreach (var (prefix, path) in inputs.PackagePaths)
                context._packagePaths.TryAdd(prefix, path);
            foreach (var (alias, target) in inputs.ModuleAliases)
                context._aliases.TryAdd(alias, target);
            context._sourceDir = manifest.Sources?.Main is { } main
                ? Path.GetFullPath(Path.Combine(manifestDir, main))
                : manifestDir;
            context._prefix = manifest.ImportPrefix;

            if (context._prefix is not null)
            {
                // Resolve() describes this package's *dependencies*; linting a file inside the
                // package also has to resolve the package's own prefixed imports, and the bare
                // spelling of a sibling module ("helper" for "mypkg/helper") that
                // LibraryCompiler aliases when it compiles them together.
                context._packagePaths.TryAdd(context._prefix, context._sourceDir);
                if (manifest.DefaultModule is { } defaultModule)
                    context._aliases.TryAdd(context._prefix, $"{context._prefix}/{defaultModule}");

                foreach (var file in ZsFilesUnder(context._sourceDir))
                {
                    var modulePart = ModulePart(context._sourceDir, file);
                    if (modulePart is not null)
                        context._aliases.TryAdd(modulePart, $"{context._prefix}/{modulePart}");
                }
            }

            return context;
        }

        /// <summary>
        ///     The manifest with its test dependencies folded into its main ones — the same
        ///     "main + test" closure <see cref="PackageTester" /> walks. Lint checks a package's
        ///     test sources alongside its main ones, and those import the test deps (zunit);
        ///     without this every test file fails to resolve. Nothing is lost on the main
        ///     sources: an extra resolvable package only matters to a file that imports it.
        /// </summary>
        private static PackageManifest WithTestDependencies(PackageManifest manifest)
        {
            var test = manifest.TestDependencies;
            if (test.ZScheme.Count == 0 && test.NuGet.Count == 0 && test.Frameworks.Count == 0)
                return manifest;

            var main = manifest.Dependencies;
            return manifest with
            {
                Dependencies = new PackageDependencies(
                    [.. main.ZScheme, .. test.ZScheme],
                    [.. main.NuGet, .. test.NuGet],
                    [.. main.Frameworks, .. test.Frameworks]
                ),
            };
        }

        /// <summary>
        ///     A fresh options object per file: <see cref="Compilation" /> writes back to
        ///     <see cref="CompilerOptions.Namespace" /> when the source declares one, so a shared
        ///     instance would leak one file's namespace into the next.
        /// </summary>
        public CompilerOptions OptionsFor(string file)
        {
            var options = new CompilerOptions
            {
                StopAfterTypeInference = true,
                AllowsImplicitModuleName = true,
                PackagePaths = new Dictionary<string, string>(_packagePaths),
                ModuleAliases = new Dictionary<string, string>(_aliases),
            };

            // Command-line paths first, so an explicit --ref/--module-path is searched ahead of
            // whatever the manifest resolved to.
            options.ModuleSearchPaths = [.. _extra.ModuleSearchPaths];
            options.AssemblySearchPaths = [.. _extra.AssemblySearchPaths];
            if (_inputs is { } inputs)
            {
                options.ModuleSearchPaths.AddRange(inputs.ModuleSearchPaths);
                options.AssemblySearchPaths.AddRange(inputs.AssemblySearchPaths);
                options.FrameworkReferences = [.. inputs.FrameworkIds];
            }

            // Same reason LibraryCompiler sets it: without the qualified name, a module that the
            // prelude also imports registers its locals twice under two spellings.
            if (_sourceDir is not null && _prefix is not null)
                if (ModulePart(_sourceDir, file) is { } modulePart)
                    options.PrimaryModuleName = $"{_prefix}/{modulePart}";

            return options;
        }

        /// <summary>The module name a file contributes under its package's source root, or null
        ///     when the file lies outside it (a test file, or a path lint was pointed at
        ///     directly).</summary>
        private static string? ModulePart(string sourceDir, string file)
        {
            var relative = Path.GetRelativePath(sourceDir, file);
            if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
                return null;
            return Path.ChangeExtension(relative, null).Replace(Path.DirectorySeparatorChar, '/');
        }
    }
}
