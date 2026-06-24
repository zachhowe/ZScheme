using ZScheme.Compiler.Diagnostics;

namespace ZScheme.Compiler.Pipeline;

public abstract record CompilationResult(DiagnosticBag Diagnostics)
{
    public bool Success { get; } = !Diagnostics.HasErrors;

    public sealed record LexerFailure(DiagnosticBag Diagnostics) : CompilationResult(Diagnostics);

    public sealed record SExprParserFailure(DiagnosticBag Diagnostics)
        : CompilationResult(Diagnostics);

    public sealed record MacroExpanderFailure(DiagnosticBag Diagnostics)
        : CompilationResult(Diagnostics);

    public sealed record AstBuilderFailure(DiagnosticBag Diagnostics)
        : CompilationResult(Diagnostics);

    public sealed record TypeInfererFailure(DiagnosticBag Diagnostics)
        : CompilationResult(Diagnostics);

    public sealed record MissingModuleDeclFailure(DiagnosticBag Diagnostics)
        : CompilationResult(Diagnostics);

    public sealed record MissingModuleNameFailure(DiagnosticBag Diagnostics)
        : CompilationResult(Diagnostics);

    public sealed record IrLoweringFailure(DiagnosticBag Diagnostics)
        : CompilationResult(Diagnostics);

    /// <summary>
    ///     Returned when <see cref="CompilerOptions.StopAfterTypeInference" /> is set.
    ///     Codegen was skipped; the typed program is on <see cref="Compilation.TypedProgram" />.
    /// </summary>
    public sealed record TypeAnalysisResult(DiagnosticBag Diagnostics)
        : CompilationResult(Diagnostics);

    public sealed record DependencyResolutionFailure(DiagnosticBag Diagnostics)
        : CompilationResult(Diagnostics);

    public sealed record CSharpOutputResult(
        DiagnosticBag Diagnostics,
        string CsOutput,
        IReadOnlyList<string> PrecompiledAssemblyPaths
    ) : CompilationResult(Diagnostics)
    {
        public string CsOutput { get; set; } = CsOutput;
        public bool IsExecutable { get; set; }
        public IReadOnlyList<string> PrecompiledAssemblyPaths { get; set; } =
            PrecompiledAssemblyPaths;
    }

    public sealed record IlOutputFailure(DiagnosticBag Diagnostics)
        : CompilationResult(Diagnostics);

    public sealed record IlOutputResult(
        DiagnosticBag Diagnostics,
        byte[] OutputBytes,
        IReadOnlyList<string> PrecompiledAssemblyPaths
    ) : CompilationResult(Diagnostics)
    {
        public byte[] OutputBytes { get; set; } = OutputBytes;
        public bool IsExecutable { get; set; }
        public IReadOnlyList<string> PrecompiledAssemblyPaths { get; set; } =
            PrecompiledAssemblyPaths;

        /// <summary>
        ///     Shared-framework ids (e.g. <c>Microsoft.AspNetCore.App</c>) this executable depends
        ///     on, used to write a framework-aware <c>runtimeconfig.json</c>.
        /// </summary>
        public IReadOnlyList<string> FrameworkReferences { get; set; } = [];

        /// <summary>
        ///     Builds the <c>runtimeconfig.json</c> content for this executable from its
        ///     <see cref="FrameworkReferences" />.
        /// </summary>
        public string BuildRuntimeConfigJson() => RuntimeConfig.Generate(FrameworkReferences);
    }
}
