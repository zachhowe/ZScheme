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
    IReadOnlyDictionary<
        string,
        (
            string TypeName,
            string MethodName,
            int GenericArity,
            ClrImportKind Kind,
            IReadOnlyDictionary<string, GenericConstraintKind>? Constraints
        )
    > ExportedClrImports,
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
    IReadOnlyList<IrNode>? AllIrDefinitions = null,
    /// <summary>
    ///     The .NET namespace the module's generated class lives in (the package's build
    ///     namespace). Set for modules built as part of a package; null otherwise. Consuming
    ///     compilations use this to emit fully-qualified references to precompiled module
    ///     classes (e.g. <c>ZScheme.StdLib.Stdlib_OptionModule</c>).
    /// </summary>
    string? BuildNamespace = null,
    /// <summary>
    ///     Maps a module-level symbol's original ZScheme name to the disambiguated
    ///     identifier it was emitted under, for the symbols whose sanitized name collided
    ///     (see <see cref="Ir.EmitNameResolver"/>). Only renamed symbols appear. Persisted
    ///     in module metadata so a consumer references a precompiled symbol by the same
    ///     name baked into the DLL. Null/empty ⇒ no renames in this module.
    /// </summary>
    IReadOnlyDictionary<string, string>? EmittedNames = null,
    /// <summary>
    ///     Like <see cref="EmittedNames"/> but for renamed <em>type</em> names (records,
    ///     unions + their cases, classes, interfaces). Kept separate because a type and a
    ///     value can share a source name yet need different emitted identifiers. A consumer
    ///     of this precompiled module references a renamed type by the name baked into the
    ///     DLL. Null/empty ⇒ no type renames in this module.
    /// </summary>
    IReadOnlyDictionary<string, string>? TypeEmittedNames = null
);
