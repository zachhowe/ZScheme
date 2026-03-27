using ZScript.Compiler.Diagnostics;

namespace ZScript.Compiler.Pipeline;

public sealed record CompilationResult(string? Output, DiagnosticBag Diagnostics)
{
    public byte[]? OutputBytes { get; init; }
    public bool IsExecutable { get; init; }
    public IReadOnlyList<string> PrecompiledAssemblyPaths { get; init; } = [];
    public bool Success => !Diagnostics.HasErrors && (Output is not null || OutputBytes is not null);
}
