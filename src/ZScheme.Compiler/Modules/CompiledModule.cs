using ZScheme.Compiler.Ast;
using ZScheme.Compiler.Ir;
using ZScheme.Compiler.Syntax;
using ZScheme.Compiler.Types;

namespace ZScheme.Compiler.Modules;

public sealed record CompiledModule(
    string Name,
    string FilePath,
    IReadOnlySet<string> ExportedNames,
    IReadOnlyDictionary<string, ZType> ExportedTypes,
    IReadOnlyDictionary<string, (string TypeName, string MethodName, int GenericArity, ClrImportKind Kind,
        IReadOnlyDictionary<string, GenericConstraintKind>? Constraints)> ExportedClrImports,
    IReadOnlyList<IrNode> ExportedIrDefinitions,
    IReadOnlyList<string> ExportedClrNamespaces,
    IReadOnlyDictionary<string, MacroDefinition> ExportedMacros,
    IReadOnlyDictionary<string, string>? ExportedUnionCtors = null,
    IReadOnlyDictionary<string, List<string>>? ExportedRecordCtors = null,
    /// <summary>
    ///     Maps class names to their implemented interface names, so that cross-module
    ///     type checks (e.g., DashAbility implements IAbility) work during unification.
    /// </summary>
    IReadOnlyDictionary<string, IReadOnlyList<string>>? ExportedClassInterfaces = null,
    string? PrecompiledAssemblyPath = null,
    /// <summary>
    ///     All IR definitions in the module, including non-exported internal helpers.
    ///     Used by IL emission so that exported functions can reference internal helpers
    ///     (e.g. an exported <c>http/get</c> calling an internal <c>send-no-body</c>).
    ///     <see cref="ExportedIrDefinitions"/> remains the subset used for cross-module
    ///     visibility — other modules only see exported names/types.
    ///     Null for precompiled modules (their IL is already in the assembly).
    /// </summary>
    IReadOnlyList<IrNode>? AllIrDefinitions = null
);
