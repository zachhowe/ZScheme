using ZScript.Compiler.Ast;
using ZScript.Compiler.Ir;
using ZScript.Compiler.Syntax;
using ZScript.Compiler.Types;

namespace ZScript.Compiler.Modules;

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
    string? PrecompiledAssemblyPath = null
);
