using System.Text;

namespace ZScheme.Fuzzer.Generation;

public sealed class ProgramGenerator
{
    private readonly GeneratorContext _ctx;
    private readonly ExprGenerator _exprs;
    private readonly UserFuncGenerator _funcs;
    private readonly UserTypeGenerator _types;
    private readonly StdlibImportGenerator _stdlib;
    private readonly AuxModuleGenerator _aux;
    private readonly SequenceExprGenerator _sequence;
    private readonly TupleExprGenerator _tuple;
    private readonly WithExprGenerator _with;
    private readonly PartialExprGenerator _partial;
    private readonly ExceptionExprGenerator _exception;
    private readonly StringExprGenerator _string;
    private readonly ClassExprGenerator _class;

    public ProgramGenerator(Random rng, int maxDepth, int maxFuncs)
    {
        _ctx = new GeneratorContext(rng, maxDepth, maxFuncs);
        _exprs = new ExprGenerator(_ctx);
        _funcs = new UserFuncGenerator(_ctx, _exprs);
        _types = new UserTypeGenerator(_ctx);
        _stdlib = new StdlibImportGenerator(_ctx, _exprs);
        _aux = new AuxModuleGenerator(_ctx, _exprs);
        _sequence = new SequenceExprGenerator(_ctx, _exprs);
        _tuple = new TupleExprGenerator(_ctx, _exprs);
        _with = new WithExprGenerator(_ctx, _exprs);
        _partial = new PartialExprGenerator(_ctx, _exprs);
        _exception = new ExceptionExprGenerator(_ctx, _exprs);
        _string = new StringExprGenerator(_ctx, _exprs);
        _class = new ClassExprGenerator(_ctx, _exprs);
        _exprs.SetStdlib(_stdlib);
        _exprs.SetSequence(_sequence);
        _exprs.SetTuple(_tuple);
        _exprs.SetWith(_with);
        _exprs.SetPartial(_partial);
        _exprs.SetException(_exception);
        _exprs.SetString(_string);
        _exprs.SetClass(_class);
    }

    public GeneratedProgram Generate(long caseSeed)
    {
        _ctx.ResetPerCase();

        var moduleName = $"fuzz_{(uint)caseSeed:x8}";

        // Generate aux modules first so main body can call into them.
        _aux.GenerateModules(caseSeed);

        var sb = new StringBuilder();
        sb.AppendLine("(namespace ZSchemeFuzzed)");
        sb.AppendLine();
        sb.AppendLine($"(module {moduleName})");
        sb.AppendLine();

        foreach (var auxModule in _ctx.AuxModules)
            sb.AppendLine($"(import {auxModule.ModuleName})");
        if (_ctx.AuxModules.Count > 0)
            sb.AppendLine();

        _stdlib.ChooseImports();
        if (_ctx.Imports.Count > 0)
        {
            // Stable order for readable / diff-friendly output.
            foreach (var imp in _ctx.Imports.OrderBy(i => (int)i))
            {
                var moduleId = imp switch
                {
                    StdlibImport.Option => "stdlib/option",
                    StdlibImport.List => "stdlib/list",
                    StdlibImport.Result => "stdlib/result",
                    _ => throw new InvalidOperationException($"Unknown import: {imp}")
                };
                sb.AppendLine($"(import {moduleId})");
            }
            sb.AppendLine();
        }

        // 0-2 generic unions and 0-2 generic records per program.
        // Union or record first is arbitrary — emit unions first for readability.
        var numUnions = _ctx.Rng.Next(3);
        for (var i = 0; i < numUnions; i++)
        {
            var u = _types.GenerateUnion(i);
            _ctx.UserUnions.Add(u);
            sb.AppendLine(u.Definition);
        }
        if (numUnions > 0) sb.AppendLine();

        var numRecords = _ctx.Rng.Next(3);
        for (var i = 0; i < numRecords; i++)
        {
            var r = _types.GenerateRecord(i);
            _ctx.UserRecords.Add(r);
            sb.AppendLine(r.Definition);
        }
        if (numRecords > 0) sb.AppendLine();

        // 0-1 user classes. Low probability keeps most cases single-file and lets
        // the class-specific codegen paths get exercised on a sizable fraction
        // without swamping the generator output.
        if (_ctx.Rng.NextDouble() < 0.35)
        {
            var cls = _class.GenerateClass(0);
            _ctx.UserClasses.Add(cls);
            sb.AppendLine(cls.Definition);
            sb.AppendLine();
        }

        var numFuncs = _ctx.Rng.Next(_ctx.MaxFuncs + 1);
        for (var i = 0; i < numFuncs; i++)
        {
            var func = _funcs.GenerateUserFunction($"f{i}");
            _ctx.UserFuncs.Add(func);
            sb.AppendLine(func.Definition);
            sb.AppendLine();
        }

        var computeScope = new Scope();
        var computeExpr = _exprs.GenInt(computeScope, _ctx.MaxDepth);
        sb.AppendLine("(define (compute) : Int");
        sb.AppendLine($"  {computeExpr})");

        return new GeneratedProgram(sb.ToString(), caseSeed, moduleName, _ctx.AuxModules.ToArray());
    }
}
