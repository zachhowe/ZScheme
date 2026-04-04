using ZScheme.Compiler.Diagnostics;

namespace ZScheme.Compiler.Pipeline;

public abstract record CompilationResult(DiagnosticBag Diagnostics)
{
    public bool Success { get; } = !Diagnostics.HasErrors;

    public sealed record LexerFailure(DiagnosticBag Diagnostics) : CompilationResult(Diagnostics);

    public sealed record SExprParserFailure(DiagnosticBag Diagnostics) : CompilationResult(Diagnostics);

    public sealed record MacroExpanderFailure(DiagnosticBag Diagnostics) : CompilationResult(Diagnostics);

    public sealed record AstBuilderFailure(DiagnosticBag Diagnostics) : CompilationResult(Diagnostics);

    public sealed record TypeInfererFailure(DiagnosticBag Diagnostics) : CompilationResult(Diagnostics);

    public sealed record MissingModuleDeclFailure(DiagnosticBag Diagnostics) : CompilationResult(Diagnostics);

    public sealed record MissingModuleNameFailure(DiagnosticBag Diagnostics) : CompilationResult(Diagnostics);

    public sealed record IrLoweringFailure(DiagnosticBag Diagnostics) : CompilationResult(Diagnostics);

    public sealed record DependencyResolutionFailure(DiagnosticBag Diagnostics) : CompilationResult(Diagnostics);

    public sealed record CSharpOutputResult(
        DiagnosticBag Diagnostics,
        string CsOutput,
        IReadOnlyList<string> PrecompiledAssemblyPaths)
        : CompilationResult(Diagnostics)
    {
        public string CsOutput { get; set; } = CsOutput;
        public bool IsExecutable { get; set; }
        public IReadOnlyList<string> PrecompiledAssemblyPaths { get; set; } = PrecompiledAssemblyPaths;
    }

    public sealed record IlOutputFailure(DiagnosticBag Diagnostics) : CompilationResult(Diagnostics);

    public sealed record IlOutputResult(
        DiagnosticBag Diagnostics,
        byte[] OutputBytes,
        IReadOnlyList<string> PrecompiledAssemblyPaths)
        : CompilationResult(Diagnostics)
    {
        public byte[] OutputBytes { get; set; } = OutputBytes;
        public bool IsExecutable { get; set; }
        public IReadOnlyList<string> PrecompiledAssemblyPaths { get; set; } = PrecompiledAssemblyPaths;
    }
}
