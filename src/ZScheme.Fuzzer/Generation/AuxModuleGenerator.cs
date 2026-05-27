using System.Globalization;
using System.Text;

namespace ZScheme.Fuzzer.Generation;

// Name of a function exported by an auxiliary module — including its module prefix,
// so the main fuzz module can call it as `helper_xy/double` after importing helper_xy.
public sealed record AuxExport(string QualifiedName, IReadOnlyList<ExprType> ParamTypes);

// Emits small auxiliary `.zs` files that the main fuzz module imports.
// Invariants:
//   - Aux modules never define `compute` (avoids confusing the diffexec oracle).
//   - Aux modules share the same namespace (ZSchemeFuzzed) so they compile into
//     the same output assembly as the main module.
//   - Default topology is a star graph rooted at main. With small probability
//     module B (index >= 1) imports module A (a lower index) and calls one of
//     its helpers — exercising the module-graph topological-sort path. Never
//     emits a cycle (B->A only, never A<->B).
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

        // Track exports of already-emitted aux modules so a later aux module
        // can optionally import them. Exports are registered with the context
        // only at the end so the main module's call-site logic only ever sees
        // a complete export list.
        var pendingExports = new List<AuxExport>();
        for (var i = 0; i < n; i++)
        {
            var auxName = $"aux_{(uint)caseSeed:x8}_{i}";

            // For modules past the first, 30% chance to import a prior aux
            // module. This injects a dependency edge into the module graph.
            var visibleExports = new List<AuxExport>();
            var importedAuxNames = new List<string>();
            if (i > 0 && _ctx.Rng.NextDouble() < 0.30)
            {
                // Pick one prior module to depend on.
                var dep = _ctx.AuxModules[_ctx.Rng.Next(_ctx.AuxModules.Count)];
                importedAuxNames.Add(dep.ModuleName);
                visibleExports.AddRange(pendingExports.Where(e =>
                    e.QualifiedName.StartsWith(dep.ModuleName + "/", StringComparison.Ordinal)));
            }

            var module = GenerateOneModule(auxName, pendingExports, importedAuxNames, visibleExports);
            _ctx.AuxModules.Add(module);
        }

        foreach (var e in pendingExports)
            _ctx.AuxExports.Add(e);
    }

    private AuxModule GenerateOneModule(
        string moduleName,
        List<AuxExport> pendingExports,
        IReadOnlyList<string> importedAuxNames,
        IReadOnlyList<AuxExport> visibleExports)
    {
        var sb = new StringBuilder();
        sb.AppendLine("(namespace ZSchemeFuzzed)");
        sb.AppendLine();
        sb.AppendLine($"(module {moduleName})");
        sb.AppendLine();

        foreach (var imp in importedAuxNames)
            sb.AppendLine($"(import {imp})");
        if (importedAuxNames.Count > 0) sb.AppendLine();

        // 1-3 small Int-typed helpers. Qualified name uses module-prefix convention
        // (matches the `list/map`, `option/unwrap-or` style used throughout the stdlib).
        var numFns = 1 + _ctx.Rng.Next(3);
        var exportNames = new List<string>();
        for (var fi = 0; fi < numFns; fi++)
        {
            var localName = $"h{fi}";
            var qualified = $"{moduleName}/{localName}";
            var paramTypes = GenerateHelperFunction(sb, qualified, visibleExports);
            pendingExports.Add(new AuxExport(qualified, paramTypes));
            exportNames.Add(qualified);
            sb.AppendLine();
        }

        sb.AppendLine($"(export {string.Join(" ", exportNames)})");

        return new AuxModule(moduleName, sb.ToString());
    }

    // Writes a `(define ...)` block for an Int-returning helper and returns its
    // param types so the main module can build a valid call site. When
    // visibleExports is non-empty (the cross-aux-import path), the body may
    // call into those imported helpers.
    private IReadOnlyList<ExprType> GenerateHelperFunction(
        StringBuilder sb,
        string qualifiedName,
        IReadOnlyList<AuxExport> visibleExports)
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

        // 60% chance to call into an imported aux helper when one is visible —
        // this is what actually exercises the cross-module dependency edge at
        // codegen time.
        string body;
        if (visibleExports.Count > 0 && _ctx.Rng.NextDouble() < 0.60)
        {
            var dep = visibleExports[_ctx.Rng.Next(visibleExports.Count)];
            // Wrap the imported call's result with one of our params so the
            // helper isn't a pure passthrough (improves coverage of mixed
            // expression paths).
            var firstParam = paramNames[0];
            var depArgs = string.Join(" ", dep.ParamTypes.Select(_ =>
                _ctx.Rng.Next(0, 100).ToString(CultureInfo.InvariantCulture)));
            body = $"(+ {firstParam} ({dep.QualifiedName} {depArgs}))";
        }
        else
        {
            body = _exprs.GenInt(scope, bodyDepth);
        }

        var paramStr = string.Join(" ", paramNames.Select(p => $"[{p} : Int]"));
        sb.AppendLine($"(define ({qualifiedName} {paramStr}) : Int");
        sb.AppendLine($"  {body})");
        return paramTypes;
    }
}
