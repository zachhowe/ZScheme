using ZScheme.Compiler.Ast;
using ZScheme.Compiler.Ir;
using ZScheme.Compiler.Types;

namespace ZScheme.Compiler.Pipeline;

public sealed partial class Compilation
{
    /// <summary>
    ///     Walks an AST subtree and registers every <see cref="AstNode.TypeAliasDecl" /> in the
    ///     compilation-wide registry. Used as a pre-pass before type inference so the registry
    ///     is populated when CLR-new constructions need alias-aware type-arg mapping.
    /// </summary>
    private void CollectTypeAliasesFromAst(AstNode node)
    {
        switch (node)
        {
            case AstNode.Program p:
                foreach (var f in p.TopLevelForms)
                    CollectTypeAliasesFromAst(f);
                break;
            case AstNode.ModuleDecl m:
                foreach (var f in m.Body)
                    CollectTypeAliasesFromAst(f);
                break;
            case AstNode.TypeAliasDecl alias:
                var info = new TypeAliasInfo(
                    alias.AliasName,
                    alias.TypeParams,
                    alias.ClrTarget,
                    alias.AssemblyHint,
                    alias.IsArray ? TypeAliasKind.SzArray : TypeAliasKind.GenericClrType,
                    alias.Span
                );
                if (
                    !TypeAliases.TryAdd(info, out var existing)
                    && existing is not null
                    && (
                        existing.ClrTarget != info.ClrTarget
                        || existing.AssemblyHint != info.AssemblyHint
                        || existing.Kind != info.Kind
                        || existing.TypeParams.Count != info.TypeParams.Count
                    )
                )
                    _diagnostics.Error(
                        $"Type alias '{alias.AliasName}' is already declared with a different target ({existing.ClrTarget}); cannot redefine to '{info.ClrTarget}'",
                        alias.Span
                    );
                break;
        }
    }

    /// <summary>
    ///     Walks the IR tree and registers every <see cref="IrNode.TypeAliasDecl" /> node in the
    ///     compilation-wide <see cref="TypeAliases" /> registry. Duplicate alias names emit a
    ///     diagnostic but do not stop compilation (the first declaration wins).
    /// </summary>
    private void CollectTypeAliases(IrNode node)
    {
        switch (node)
        {
            case IrNode.Seq seq:
            {
                foreach (var child in seq.Nodes)
                    CollectTypeAliases(child);
                break;
            }
            case IrNode.Let let:
                CollectTypeAliases(let.Body);
                break;
            case IrNode.TypeAliasDecl alias:
            {
                var info = new TypeAliasInfo(
                    alias.Name,
                    alias.TypeParams,
                    alias.ClrTarget,
                    alias.AssemblyHint,
                    alias.IsArray ? TypeAliasKind.SzArray : TypeAliasKind.GenericClrType,
                    alias.Span
                );
                if (
                    !TypeAliases.TryAdd(info, out var existing)
                    && existing is not null
                    && (
                        existing.ClrTarget != info.ClrTarget
                        || existing.AssemblyHint != info.AssemblyHint
                        || existing.Kind != info.Kind
                        || existing.TypeParams.Count != info.TypeParams.Count
                    )
                )
                    _diagnostics.Error(
                        $"Type alias '{alias.Name}' is already declared with a different target ({existing.ClrTarget}); cannot redefine to '{info.ClrTarget}'",
                        alias.Span
                    );
                break;
            }
        }
    }

    private static void CollectExportedIrDefs(
        IrNode node,
        HashSet<string> exportedNames,
        List<IrNode> result
    )
    {
        while (true)
        {
            switch (node)
            {
                case IrNode.Seq seq:
                {
                    foreach (var child in seq.Nodes)
                        CollectExportedIrDefs(child, exportedNames, result);
                    break;
                }
                case IrNode.FuncDef funcDef when exportedNames.Contains(funcDef.Name):
                    result.Add(funcDef);
                    break;
                case IrNode.Let let:
                {
                    if (exportedNames.Contains(let.VarName))
                        result.Add(let);
                    // Always recurse into Let.Body — exported definitions can be nested
                    // inside non-exported Let bindings (e.g. module-level defines)
                    node = let.Body;
                    continue;
                }
                case IrNode.UnionDecl unionDecl when exportedNames.Contains(unionDecl.Name):
                    result.Add(unionDecl);
                    break;
                case IrNode.RecordDecl recordDecl when exportedNames.Contains(recordDecl.Name):
                    result.Add(recordDecl);
                    break;
                case IrNode.ClassDecl classDecl when exportedNames.Contains(classDecl.Name):
                    result.Add(classDecl);
                    break;
                case IrNode.InterfaceDecl ifaceDecl when exportedNames.Contains(ifaceDecl.Name):
                    result.Add(ifaceDecl);
                    break;
                case IrNode.TypeAliasDecl typeAlias:
                    // Type aliases are always "exported" for codegen visibility — they don't
                    // generate code but the compilation-wide registry needs to see them.
                    result.Add(typeAlias);
                    break;
            }

            break;
        }
    }

    private static void CollectAllIrDefs(IrNode node, List<IrNode> result)
    {
        while (true)
        {
            switch (node)
            {
                case IrNode.Seq seq:
                {
                    foreach (var child in seq.Nodes)
                        CollectAllIrDefs(child, result);
                    break;
                }
                case IrNode.FuncDef
                or IrNode.UnionDecl
                or IrNode.RecordDecl
                or IrNode.ClassDecl
                or IrNode.InterfaceDecl
                or IrNode.TypeAliasDecl:
                    result.Add(node);
                    break;
                case IrNode.Let let:
                    result.Add(let);
                    // Recurse into Let.Body to find definitions nested inside
                    // module-level Let bindings (e.g. functions defined after a top-level define)
                    node = let.Body;
                    continue;
            }

            break;
        }
    }
}
