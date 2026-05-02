using System.Text;
using ZScheme.Fuzzer.Generation.Stdlib;

namespace ZScheme.Fuzzer.Generation;

public sealed class ProgramGenerator
{
    private readonly GeneratorContext _ctx;
    private readonly ExprGenerator _exprs;
    private readonly UserFuncGenerator _funcs;
    private readonly UserTypeGenerator _types;
    private readonly StdlibImportGenerator _stdlib;
    private readonly StdlibGenerators _stdlibGens;
    private readonly ConversionExprGenerator _conv;
    private readonly AuxModuleGenerator _aux;
    private readonly SequenceExprGenerator _sequence;
    private readonly TupleExprGenerator _tuple;
    private readonly WithExprGenerator _with;
    private readonly PartialExprGenerator _partial;
    private readonly ExceptionExprGenerator _exception;
    private readonly StringExprGenerator _string;
    private readonly ClassExprGenerator _class;
    private readonly InterfaceGenerator _interface;
    private readonly ObjectExprGenerator _object;
    private readonly ClrInteropExprGenerator _clr;
    private readonly AsyncExprGenerator _async;
    private readonly AsyncUserFuncGenerator _asyncFuncs;

    public ProgramGenerator(Random rng, int maxDepth, int maxFuncs)
    {
        _ctx = new GeneratorContext(rng, maxDepth, maxFuncs);
        _exprs = new ExprGenerator(_ctx);
        _funcs = new UserFuncGenerator(_ctx, _exprs);
        _types = new UserTypeGenerator(_ctx);
        _stdlib = new StdlibImportGenerator(_ctx);
        _stdlibGens = new StdlibGenerators(_ctx, _exprs);
        _conv = new ConversionExprGenerator(_ctx, _exprs);
        _aux = new AuxModuleGenerator(_ctx, _exprs);
        _sequence = new SequenceExprGenerator(_ctx, _exprs);
        _tuple = new TupleExprGenerator(_ctx, _exprs);
        _with = new WithExprGenerator(_ctx, _exprs);
        _partial = new PartialExprGenerator(_ctx, _exprs);
        _exception = new ExceptionExprGenerator(_ctx, _exprs);
        _string = new StringExprGenerator(_ctx, _exprs);
        _class = new ClassExprGenerator(_ctx, _exprs);
        _interface = new InterfaceGenerator(_ctx);
        _object = new ObjectExprGenerator(_ctx, _exprs);
        _clr = new ClrInteropExprGenerator(_ctx, _exprs);
        _async = new AsyncExprGenerator(_ctx, _exprs, _exception);
        _asyncFuncs = new AsyncUserFuncGenerator(_ctx, _exprs, _async, _exception);
        _class.SetAsync(_async);
        _exprs.SetStdlibGenerators(_stdlibGens);
        _exprs.SetConversion(_conv);
        _exprs.SetSequence(_sequence);
        _exprs.SetTuple(_tuple);
        _exprs.SetWith(_with);
        _exprs.SetPartial(_partial);
        _exprs.SetException(_exception);
        _exprs.SetString(_string);
        _exprs.SetClass(_class);
        _exprs.SetObject(_object);
        _exprs.SetClrInterop(_clr);
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
                    StdlibImport.Array => "stdlib/array",
                    StdlibImport.Map => "stdlib/map",
                    StdlibImport.String => "stdlib/string",
                    StdlibImport.Math => "stdlib/math",
                    StdlibImport.Core => "stdlib/core",
                    _ => throw new InvalidOperationException($"Unknown import: {imp}")
                };
                sb.AppendLine($"(import {moduleId})");
            }
            sb.AppendLine();
        }

        _clr.ChooseBindings();
        var clrBlock = _clr.EmitImportBlock();
        if (!string.IsNullOrEmpty(clrBlock))
        {
            sb.AppendLine(clrBlock);
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

        // Interfaces: 0-2. Emitted before classes so a class can implement one.
        var numInterfaces = _ctx.Rng.Next(3);
        for (var i = 0; i < numInterfaces; i++)
        {
            var iface = _interface.GenerateInterface(i);
            _ctx.UserInterfaces.Add(iface);
            sb.AppendLine(iface.Definition);
        }
        if (numInterfaces > 0) sb.AppendLine();

        // Classes: emit a base class with ~35% probability, plus optional
        // interface implementation and optional inheritance pair. Three shapes
        // exercise different OO codegen paths:
        //   * standalone class
        //   * class implementing an interface
        //   * #:open base + derived class with override + super/Method
        var emitClass = _ctx.Rng.NextDouble() < 0.45;
        if (emitClass)
        {
            // ~50% chance to set up for inheritance — requires #:open base and
            // at least one base method to override (always true given our gen).
            var emitDerived = _ctx.Rng.NextDouble() < 0.5;

            // ~40% chance to implement an interface when one exists. Skip when
            // we plan to also emit a derived class, since deriving + implementing
            // simultaneously isn't tested in the current shape.
            UserInterfaceDecl? toImpl = null;
            if (!emitDerived && _ctx.UserInterfaces.Count > 0 && _ctx.Rng.NextDouble() < 0.4)
                toImpl = _ctx.UserInterfaces[_ctx.Rng.Next(_ctx.UserInterfaces.Count)];

            var baseCls = _class.GenerateClass(
                index: 0,
                isOpen: emitDerived,
                interfaceToImplement: toImpl);
            _ctx.UserClasses.Add(baseCls);
            sb.AppendLine(baseCls.Definition);
            sb.AppendLine();

            if (emitDerived)
            {
                var derived = _class.GenerateDerivedClass(index: 1, baseCls);
                _ctx.UserClasses.Add(derived);
                sb.AppendLine(derived.Definition);
                sb.AppendLine();
            }
        }

        // Decide whether this case will use the construct-and-call class
        // reducer. Gate at 30% so the IL backend's known stack-imbalance bug
        // surfaces frequently in the artifact stream without dominating it.
        if (_ctx.UserClasses.Count > 0 && _ctx.Rng.NextDouble() < 0.30)
        {
            _ctx.EnableClassInstanceCalls = true;
            var classImports = _class.EmitInstanceImportClrBlock("ZSchemeFuzzed");
            if (!string.IsNullOrEmpty(classImports))
            {
                sb.AppendLine(classImports);
                sb.AppendLine();
            }
        }

        var numFuncs = _ctx.Rng.Next(_ctx.MaxFuncs + 1);
        for (var i = 0; i < numFuncs; i++)
        {
            var func = _funcs.GenerateUserFunction($"f{i}");
            _ctx.UserFuncs.Add(func);
            sb.AppendLine(func.Definition);
            sb.AppendLine();
        }

        // Decide whether to emit async user funcs / make compute async. computeAsync
        // forces emitAsync because a sync compute can't reach async helpers (no
        // sync-over-async escape in ZScheme), so async helpers without an async
        // entry point would only get compile-time coverage — still kept when
        // emitAsync rolls true on its own, since state-machine codegen is exercised
        // at compile time even for unreachable async funcs.
        var computeAsync = _ctx.Rng.NextDouble() < 0.15;
        var emitAsync = computeAsync || _ctx.Rng.NextDouble() < 0.35;
        _ctx.ComputeIsAsync = computeAsync;
        if (emitAsync)
        {
            var numAsync = 1 + _ctx.Rng.Next(3); // 1, 2, or 3
            for (var i = 0; i < numAsync; i++)
            {
                var asyncFunc = _asyncFuncs.GenerateAsyncFunction($"g{i}");
                _ctx.UserFuncs.Add(asyncFunc);
                sb.AppendLine(asyncFunc.Definition);
                sb.AppendLine();
            }
        }

        var computeScope = new Scope();
        if (computeAsync)
        {
            var computeExpr = _async.GenAsyncBodyInt(computeScope, _ctx.MaxDepth);
            sb.AppendLine("(define-async (compute) : (Task Int)");
            sb.AppendLine($"  {computeExpr})");
        }
        else
        {
            var computeExpr = _exprs.GenInt(computeScope, _ctx.MaxDepth);
            sb.AppendLine("(define (compute) : Int");
            sb.AppendLine($"  {computeExpr})");
        }

        return new GeneratedProgram(sb.ToString(), caseSeed, moduleName, _ctx.AuxModules.ToArray());
    }
}
