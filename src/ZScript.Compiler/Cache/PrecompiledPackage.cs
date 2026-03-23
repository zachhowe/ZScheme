namespace ZScript.Compiler.Cache;

using ZScript.Compiler.Ast;
using ZScript.Compiler.Ir;
using ZScript.Compiler.Syntax;
using ZScript.Compiler.Types;

public sealed record PrecompiledPackage(
    string PackageName,
    string Version,
    string AssemblyPath,
    IReadOnlyDictionary<string, PrecompiledModuleInfo> Modules,
    string? Namespace = null);

public sealed record PrecompiledModuleInfo(
    string Name,
    IReadOnlySet<string> ExportedNames,
    IReadOnlyDictionary<string, ZType> ExportedTypes,
    IReadOnlyDictionary<string, (string TypeName, string MethodName, int GenericArity, ClrImportKind Kind)> ExportedClrImports,
    IReadOnlyList<string> ExportedClrNamespaces,
    IReadOnlyDictionary<string, string>? ExportedUnionCtors,
    IReadOnlyDictionary<string, List<string>>? ExportedRecordCtors,
    IReadOnlyDictionary<string, MacroDefinition>? ExportedMacros = null,
    IReadOnlyList<IrNode>? TypeDeclarations = null);
