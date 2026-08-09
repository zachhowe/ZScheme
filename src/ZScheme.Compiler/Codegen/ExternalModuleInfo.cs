using ZScheme.Compiler.Ir;

namespace ZScheme.Compiler.Codegen;

/// <summary>
///     A module whose code another project emits, described well enough for this compilation
///     to reference it correctly without re-emitting it.
/// </summary>
/// <param name="ClassName">
///     The module's generated class name, unqualified. Keys the per-module lookups that a
///     call site reaches by module name (see <c>CSharpEmitter.TryLookupGenericFunc</c>).
/// </param>
/// <param name="QualifiedClassName">
///     The same class prefixed with the module's build namespace when it has one — how the
///     emitted source must spell it. Equal to <paramref name="ClassName" /> for a module that
///     ends up in the consumer's own namespace (a sibling emitted into the same assembly).
/// </param>
/// <param name="Definitions">
///     The module's IR definitions, read for signatures only — never emitted.
/// </param>
public sealed record ExternalModuleInfo(
    string ClassName,
    string QualifiedClassName,
    IReadOnlyList<IrNode> Definitions
);
