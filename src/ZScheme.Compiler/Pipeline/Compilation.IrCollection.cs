using ZScheme.Compiler.Ir;

namespace ZScheme.Compiler.Pipeline;

public sealed partial class Compilation
{
    private static void CollectExportedIrDefs(IrNode node, HashSet<string> exportedNames, List<IrNode> result)
    {
        if (node is IrNode.Seq seq)
            foreach (var child in seq.Nodes)
                CollectExportedIrDefs(child, exportedNames, result);
        else if (node is IrNode.FuncDef funcDef && exportedNames.Contains(funcDef.Name))
            result.Add(funcDef);
        else if (node is IrNode.Let let)
        {
            if (exportedNames.Contains(let.VarName))
                result.Add(let);
            // Always recurse into Let.Body — exported definitions can be nested
            // inside non-exported Let bindings (e.g. module-level defines)
            CollectExportedIrDefs(let.Body, exportedNames, result);
        }
        else if (node is IrNode.UnionDecl unionDecl && exportedNames.Contains(unionDecl.Name))
            result.Add(unionDecl);
        else if (node is IrNode.RecordDecl recordDecl && exportedNames.Contains(recordDecl.Name))
            result.Add(recordDecl);
        else if (node is IrNode.ClassDecl classDecl && exportedNames.Contains(classDecl.Name))
            result.Add(classDecl);
    }

    private static void CollectAllIrDefs(IrNode node, List<IrNode> result)
    {
        if (node is IrNode.Seq seq)
            foreach (var child in seq.Nodes)
                CollectAllIrDefs(child, result);
        else if (node is IrNode.FuncDef or IrNode.UnionDecl or IrNode.RecordDecl or IrNode.ClassDecl)
            result.Add(node);
        else if (node is IrNode.Let let)
        {
            result.Add(let);
            // Recurse into Let.Body to find definitions nested inside
            // module-level Let bindings (e.g. functions defined after a top-level define)
            CollectAllIrDefs(let.Body, result);
        }
    }
}
