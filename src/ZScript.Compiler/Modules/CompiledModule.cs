namespace ZScript.Compiler.Modules;

using ZScript.Compiler.Ir;
using ZScript.Compiler.Syntax;
using ZScript.Compiler.Types;

public sealed record CompiledModule(
    string Name,
    string FilePath,
    IReadOnlySet<string> ExportedNames,
    IReadOnlyDictionary<string, ZType> ExportedTypes,
    IReadOnlyDictionary<string, (string TypeName, string MethodName, int GenericArity)> ExportedClrImports,
    IReadOnlyList<IrNode> ExportedIrDefinitions,
    IReadOnlyList<string> ExportedClrNamespaces,
    IReadOnlyDictionary<string, MacroDefinition> ExportedMacros
);
