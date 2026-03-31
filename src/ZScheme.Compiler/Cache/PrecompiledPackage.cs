using ZScheme.Compiler.Ast;
using ZScheme.Compiler.Ir;
using ZScheme.Compiler.Syntax;
using ZScheme.Compiler.Types;

namespace ZScheme.Compiler.Cache;

public sealed record PrecompiledPackage(
    string PackageName,
    string Version,
    string AssemblyPath,
    IReadOnlyDictionary<string, PrecompiledModuleInfo> Modules,
    string? ImportPrefix = null,
    string? DefaultModule = null);

public sealed record PrecompiledModuleInfo(
    string Name,
    IReadOnlySet<string> ExportedNames,
    IReadOnlyDictionary<string, ZType> ExportedTypes,
    IReadOnlyDictionary<string, (string TypeName, string MethodName, int GenericArity, ClrImportKind Kind,
        IReadOnlyDictionary<string, GenericConstraintKind>? Constraints)> ExportedClrImports,
    IReadOnlyList<string> ExportedClrNamespaces,
    IReadOnlyDictionary<string, string>? ExportedUnionCtors,
    IReadOnlyDictionary<string, List<string>>? ExportedRecordCtors,
    IReadOnlyDictionary<string, MacroDefinition>? ExportedMacros = null,
    IReadOnlyList<IrNode>? TypeDeclarations = null);
