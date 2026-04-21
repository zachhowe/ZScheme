using System.Text;

namespace ZScheme.Fuzzer.Generation;

// Name of a function exported by an auxiliary module — including its module prefix,
// so the main fuzz module can call it as `helper_xy/double` after importing helper_xy.
public sealed record AuxExport(string QualifiedName, IReadOnlyList<ExprType> ParamTypes);

// Emits small auxiliary `.zs` files that the main fuzz module imports.
// Invariants:
//   - Aux modules never define `compute` (avoids confusing the diffexec oracle).
//   - Aux modules never import each other (star graph rooted at main) — keeps
//     topological-sort risk low until we deliberately expand coverage.
//   - Aux modules share the same namespace (ZSchemeFuzzed) so they compile into
//     the same output assembly as the main module.
public sealed class AuxModuleGenerator
{
    private readonly GeneratorContext _ctx;
    private readonly ExprGenerator _exprs;

    public AuxModuleGenerator(GeneratorContext ctx, ExprGenerator exprs)
    {
        _ctx = ctx;
        _exprs = exprs;
    }

    public void GenerateModules(long caseSeed)
    {
        // 0-2 aux modules, weighted so most cases stay single-file.
        var n = _ctx.Rng.NextDouble() < 0.4 ? 1 + _ctx.Rng.Next(2) : 0;

        // Accumulate exports locally and only register them with the context AFTER
        // every aux module has been generated. This enforces the star-graph
        // invariant: aux bodies cannot call functions exported by other aux
        // modules (those haven't been imported). Once registered, the main
        // module's body can freely reference any of them.
        var pendingExports = new List<AuxExport>();
        for (var i = 0; i < n; i++)
        {
            var auxName = $"aux_{(uint)caseSeed:x8}_{i}";
            var module = GenerateOneModule(auxName, pendingExports);
            _ctx.AuxModules.Add(module);
        }

        foreach (var e in pendingExports)
            _ctx.AuxExports.Add(e);
    }

    private AuxModule GenerateOneModule(string moduleName, List<AuxExport> pendingExports)
    {
        var sb = new StringBuilder();
        sb.AppendLine("(namespace ZSchemeFuzzed)");
        sb.AppendLine();
        sb.AppendLine($"(module {moduleName})");
        sb.AppendLine();

        // 1-3 small Int-typed helpers. Qualified name uses module-prefix convention
        // (matches the `list/map`, `option/unwrap-or` style used throughout the stdlib).
        var numFns = 1 + _ctx.Rng.Next(3);
        var exportNames = new List<string>();
        for (var fi = 0; fi < numFns; fi++)
        {
            var localName = $"h{fi}";
            var qualified = $"{moduleName}/{localName}";
            var paramTypes = GenerateHelperFunction(sb, qualified);
            pendingExports.Add(new AuxExport(qualified, paramTypes));
            exportNames.Add(qualified);
            sb.AppendLine();
        }

        sb.AppendLine($"(export {string.Join(" ", exportNames)})");

        return new AuxModule(moduleName, sb.ToString());
    }

    // Writes a `(define ...)` block for an Int-returning helper and returns its
    // param types so the main module can build a valid call site.
    private IReadOnlyList<ExprType> GenerateHelperFunction(StringBuilder sb, string qualifiedName)
    {
        var arity = 1 + _ctx.Rng.Next(2);
        var scope = new Scope();
        var paramNames = new List<string>();
        var paramTypes = new List<ExprType>();
        for (var i = 0; i < arity; i++)
        {
            var pname = _ctx.Fresh();
            paramNames.Add(pname);
            paramTypes.Add(ExprType.Int);
            scope = scope.Extend(pname, ExprType.Int);
        }

        // Restrict body depth to avoid oversized aux modules (main module already
        // explodes with depth - the aux helpers should stay simple and call-friendly).
        var bodyDepth = Math.Min(_ctx.MaxDepth, 3);
        var body = _exprs.GenInt(scope, bodyDepth);
        var paramStr = string.Join(" ", paramNames.Select(p => $"[{p} : Int]"));
        sb.AppendLine($"(define ({qualifiedName} {paramStr}) : Int");
        sb.AppendLine($"  {body})");
        return paramTypes;
    }
}
