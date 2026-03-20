namespace ZScript.Compiler.Modules;

using ZScript.Compiler.Ir;
using ZScript.Compiler.Types;

public sealed record CompiledModule(
    string Name,
    string FilePath,
    IReadOnlySet<string> ExportedNames,
    IReadOnlyDictionary<string, ZType> ExportedTypes,
    IReadOnlyDictionary<string, (string TypeName, string MethodName)> ExportedClrImports,
    IReadOnlyList<IrNode> ExportedIrDefinitions,
    IReadOnlyList<string> ExportedClrNamespaces
);
