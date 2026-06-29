namespace ZScheme.Compiler.Pipeline;

public enum OutputMode
{
    CSharp,
    Il,
}

/// <summary>
///     Opt-in code-coverage instrumentation for the IL backend. When
///     <see cref="Enabled" /> is set, the emitter weaves stack-neutral probes into the
///     generated method bodies and bakes a self-contained <c>__ZSchemeCoverage</c> class
///     (hit counters + a coverage-point→source metadata table) directly into the output
///     assembly — no external runtime library is referenced. The <c>zs</c> toolchain reads
///     that state back out via reflection to produce a report.
/// </summary>
public sealed class CoverageOptions
{
    public bool Enabled { get; set; }

    /// <summary>
    ///     Absolute path prefixes of source files to instrument (typically the package's main
    ///     source directory). A coverage point is only emitted when its <c>SourceSpan.File</c>
    ///     lives under one of these prefixes, so test files and precompiled stdlib/deps are
    ///     excluded. An empty list instruments every file with a real span.
    /// </summary>
    public List<string> IncludePathPrefixes { get; set; } = [];
}

public sealed class CompilerOptions
{
    public OutputMode OutputMode { get; set; } = OutputMode.CSharp;
    public string OutputPath { get; set; } = "output";
    public string Namespace { get; set; } = "ZSchemeGenerated";
    public List<string> AssemblySearchPaths { get; set; } = [];
    public List<string> ModuleSearchPaths { get; set; } = [];
    public Dictionary<string, string> PackagePaths { get; set; } = new();
    public Dictionary<string, string> ModuleAliases { get; set; } = new();

    public List<string> PreludeModules { get; set; } =
    [
        "stdlib/option",
        "stdlib/result",
        "stdlib/error",
        "stdlib/core",
        "stdlib/list",
        "stdlib/treelist",
        "stdlib/vector",
        "stdlib/hash",
        "stdlib/catch",
        // Mutable variants are part of the prelude so the type aliases for Mutable-Vector,
        // Mutable-TreeList, and Mutable-Hash are visible to programs that don't explicitly
        // import the mutable submodules. The variadic rest-parameter syntax in particular
        // depends on Clr-Array (or Mutable-Vector if loaded) being known.
        "stdlib/mutable/vector",
        "stdlib/mutable/treelist",
        "stdlib/mutable/hash",
    ];

    public bool DisablePrelude { get; set; } = false;

    /// <summary>
    ///     When <c>true</c>, files without a <c>(module ...)</c> declaration compile using
    ///     "UnnamedModule" as the class name instead of failing. Intended for REPL and unit
    ///     test scenarios where there is no actual source file. Defaults to <c>false</c>.
    /// </summary>
    public bool AllowsImplicitModuleName { get; set; }

    public List<string> PrecompiledPackagePaths { get; set; } = [];
    public bool SuppressVersionPreamble { get; set; }

    /// <summary>
    ///     Shared-framework ids the package declares (e.g. <c>Microsoft.AspNetCore.App</c>).
    ///     Carried onto <see cref="CompilationResult.IlOutputResult" /> so the build command can
    ///     emit a framework-aware <c>runtimeconfig.json</c> for an executable that depends on a
    ///     shared framework beyond <c>Microsoft.NETCore.App</c>.
    /// </summary>
    public IReadOnlyList<string> FrameworkReferences { get; set; } = [];

    /// <summary>
    ///     Externally-supplied module name used as the qualifying prefix for locally-defined
    ///     functions when registering them as overload candidates (e.g. <c>"stdlib/vector"</c>).
    ///     When set, this overrides the file's <c>(module ...)</c> declaration. The package
    ///     compiler sets this so that locals inside <c>packages/stdlib/src/vector.zs</c> are
    ///     registered under <c>"stdlib/vector/..."</c>, matching how the same module's exports
    ///     are seen when imported as a prelude. Without it, prelude self-import would create
    ///     two candidates for the same function under different qualified names. Null falls
    ///     back to the file's declared module name.
    /// </summary>
    public string? PrimaryModuleName { get; set; }

    /// <summary>
    ///     When <c>true</c>, <see cref="Compilation.Compile" /> stops after type inference and
    ///     skips IR lowering and codegen. The typed program is exposed via
    ///     <see cref="Compilation.TypedProgram" />. Used by the language server to type-check
    ///     without producing artifacts.
    /// </summary>
    public bool StopAfterTypeInference { get; set; }

    /// <summary>
    ///     Overrides the base directory for ZScheme caches (compiled packages under
    ///     <c>pkg/{Version}/</c> and git-cloned ZScheme dependencies under <c>git/</c>) for this
    ///     compilation. When <c>null</c>, falls back to the process-wide default (which the CLI
    ///     populates from the <c>ZSCHEME_CACHE_DIR</c> environment variable), and ultimately to
    ///     <c>~/.zscheme/cache</c>. An explicit value here takes precedence over the env var.
    ///     NuGet caches are intentionally unaffected and remain at
    ///     <c>~/.zscheme/cache/nuget</c>.
    /// </summary>
    public string? CacheDirectory { get; set; }

    /// <summary>
    ///     When non-null and <see cref="CoverageOptions.Enabled" />, the IL backend instruments
    ///     the emitted assembly for code coverage. Null (the default) leaves every other code
    ///     path untouched.
    /// </summary>
    public CoverageOptions? Coverage { get; set; }
}
