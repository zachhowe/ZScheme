using ZScheme.Compiler.Ir;

namespace ZScheme.Compiler.Pipeline;

public sealed partial class Compilation
{
    private static void CollectExportedIrDefs(IrNode node, HashSet<string> exportedNames, List<IrNode> result)
    {
        while (true)
        {
            switch (node)
            {
                case IrNode.Seq seq:
                {
                    foreach (var child in seq.Nodes) CollectExportedIrDefs(child, exportedNames, result);
                    break;
                }
                case IrNode.FuncDef funcDef when exportedNames.Contains(funcDef.Name):
                    result.Add(funcDef);
                    break;
                case IrNode.Let let:
                {
                    if (exportedNames.Contains(let.VarName)) result.Add(let);
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
                    foreach (var child in seq.Nodes) CollectAllIrDefs(child, result);
                    break;
                }
                case IrNode.FuncDef or IrNode.UnionDecl or IrNode.RecordDecl or IrNode.ClassDecl:
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
