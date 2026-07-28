using System.Text;
using ZScheme.Fuzzer.Generation.Stdlib;

namespace ZScheme.Fuzzer.Generation;

public sealed class ProgramGenerator
{
    private readonly AsyncExprGenerator _async;
    private readonly AsyncUserFuncGenerator _asyncFuncs;
    private readonly AttributeAnnotationGenerator _attrs;
    private readonly AuxModuleGenerator _aux;
    private readonly ClassExprGenerator _class;
    private readonly ClrInteropExprGenerator _clr;
    private readonly ConversionExprGenerator _conv;
    private readonly DelegateExprGenerator _delegate;
    private readonly GeneratorContext _ctx;
    private readonly ExceptionExprGenerator _exception;
    private readonly ExprGenerator _exprs;
    private readonly UserFuncGenerator _funcs;
    private readonly InterfaceGenerator _interface;
    private readonly LetStarExprGenerator _letStar;
    private readonly LetrecExprGenerator _letrec;
    private readonly UserMacroGenerator _macros;
    private readonly MatchExprGenerator _match;
    private readonly MatchPatternExtensionsGenerator _matchExt;
    private readonly MutualRecFuncGenerator _mutualRec;
    private readonly ObjectExprGenerator _object;
    private readonly PartialExprGenerator _partial;
    private readonly SequenceExprGenerator _sequence;
    private readonly SetMutationExprGenerator _setMutation;
    private readonly StdlibImportGenerator _stdlib;
    private readonly StdlibGenerators _stdlibGens;
    private readonly StringExprGenerator _string;
    private readonly StructTypeGenerator _structs;
    private readonly SymbolExprGenerator _symbol;
    private readonly TupleExprGenerator _tuple;
    private readonly TypeAliasGenerator _typeAlias;
    private readonly UseExprGenerator _use;
    private readonly UserTypeGenerator _types;
    private readonly VariadicFuncGenerator _variadic;
    private readonly WhereConstraintGenerator _where;
    private readonly WidePrimitiveExprGenerator _widePrim;
    private readonly WithExprGenerator _with;
    private readonly TypeOfExprGenerator _typeOf;

    public ProgramGenerator(Random rng, int maxDepth, int maxFuncs)
    {
        _ctx = new GeneratorContext(rng, maxDepth, maxFuncs);
        _where = new WhereConstraintGenerator(_ctx);
        _attrs = new AttributeAnnotationGenerator(_ctx);
        _exprs = new ExprGenerator(_ctx);
        _funcs = new UserFuncGenerator(_ctx, _exprs, _where);
        _types = new UserTypeGenerator(_ctx, _where);
        _structs = new StructTypeGenerator(_ctx);
        _typeAlias = new TypeAliasGenerator(_ctx);
        _variadic = new VariadicFuncGenerator(_ctx, _exprs);
        _matchExt = new MatchPatternExtensionsGenerator(_ctx, _exprs);
        _widePrim = new WidePrimitiveExprGenerator(_ctx, _exprs);
        _typeOf = new TypeOfExprGenerator(_ctx);
        _macros = new UserMacroGenerator(_ctx);
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
        _delegate = new DelegateExprGenerator(_ctx, _exprs);
        _async = new AsyncExprGenerator(_ctx, _exprs, _exception);
        _asyncFuncs = new AsyncUserFuncGenerator(_ctx, _exprs, _async, _exception);
        _match = new MatchExprGenerator(_ctx, _exprs);
        _match.SetExtensions(_matchExt);
        _letStar = new LetStarExprGenerator(_ctx, _exprs);
        _letrec = new LetrecExprGenerator(_ctx, _exprs);
        _use = new UseExprGenerator(_ctx, _exprs);
        _symbol = new SymbolExprGenerator(_ctx, _exprs);
        _setMutation = new SetMutationExprGenerator(_ctx, _exprs);
        _mutualRec = new MutualRecFuncGenerator(_ctx, _exprs);
        _class.SetAsync(_async);
        _class.SetSetMutation(_setMutation);
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
        _exprs.SetDelegate(_delegate);
        _exprs.SetMatch(_match);
        _exprs.SetLetStar(_letStar);
        _exprs.SetLetrec(_letrec);
        _exprs.SetUse(_use);
        _exprs.SetSymbol(_symbol);
        _exprs.SetWidePrim(_widePrim);
        _exprs.SetTypeOf(_typeOf);
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

        // Per-program low-gated probes. is-null? needs stdlib/core. It is confirmed
        // to surface an IL-backend bug: `is-null?` lowers to ReferenceEquals(x,null),
        // and the IL backend leaves a value-type operand unboxed (ilverify:
        // "StackUnexpected: found Int32, expected ref 'object'"), whereas the C#
        // backend boxes correctly. Gated very low (like the string-indexer path) so
        // the repro shape stays present without dominating the artifact stream.
        if (_ctx.Imports.Contains(StdlibImport.Core) && _ctx.Rng.NextDouble() < 0.08)
            _ctx.EnableNullChecks = true;
        // Unicode string literals — independent probe, oracle-clean in practice.
        if (_ctx.Rng.NextDouble() < 0.20)
            _ctx.EnableUnicodeStrings = true;
        // Non-exhaustive literal matches (runtime fall-through probe) — the
        // omitted-catchall matches are wrapped in with-handlers so the program
        // computes a value whether or not the match throws.
        if (_ctx.Rng.NextDouble() < 0.10)
            _ctx.EnableMatchFallthrough = true;
        // Binder shadowing across let/let*/lambda/match sites.
        if (_ctx.Rng.NextDouble() < 0.25)
            _ctx.EnableShadowing = true;

        if (_ctx.Imports.Count > 0)
        {
            // Stable order for readable / diff-friendly output.
            foreach (var imp in _ctx.Imports.OrderBy(i => (int)i))
            {
                var moduleId = imp switch
                {
                    StdlibImport.Option => "stdlib/option",
                    StdlibImport.TreeList => "stdlib/treelist",
                    StdlibImport.Result => "stdlib/result",
                    StdlibImport.Vector => "stdlib/vector",
                    StdlibImport.Hash => "stdlib/hash",
                    StdlibImport.String => "stdlib/string",
                    StdlibImport.Math => "stdlib/math",
                    StdlibImport.Core => "stdlib/core",
                    StdlibImport.Cond => "stdlib/cond",
                    StdlibImport.Pipe => "stdlib/pipe",
                    StdlibImport.List => "stdlib/list",
                    StdlibImport.ConcurrentQueue => "stdlib/concurrent/queue",
                    StdlibImport.ConcurrentStack => "stdlib/concurrent/stack",
                    StdlibImport.ConcurrentBag => "stdlib/concurrent/bag",
                    StdlibImport.ConcurrentDictionary => "stdlib/concurrent/dictionary",
                    StdlibImport.MutableVector => "stdlib/mutable/vector",
                    StdlibImport.MutableTreeList => "stdlib/mutable/treelist",
                    StdlibImport.MutableHash => "stdlib/mutable/hash",
                    StdlibImport.Error => "stdlib/error",
                    StdlibImport.Control => "stdlib/control",
                    StdlibImport.Catch => "stdlib/catch",
                    _ => throw new InvalidOperationException($"Unknown import: {imp}"),
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
            sb.Append(_attrs.MaybeEmitFor(AttributeTarget.Union));
            sb.AppendLine(u.Definition);
        }

        if (numUnions > 0)
            sb.AppendLine();

        var numRecords = _ctx.Rng.Next(3);
        for (var i = 0; i < numRecords; i++)
        {
            var r = _types.GenerateRecord(i);
            _ctx.UserRecords.Add(r);
            sb.Append(_attrs.MaybeEmitFor(AttributeTarget.Record));
            sb.AppendLine(r.Definition);
        }

        if (numRecords > 0)
            sb.AppendLine();

        // Non-generic structs: 0-2 per program. Added to UserRecords so existing
        // accessor / `with` generators pick them up uniformly.
        var numStructs = _ctx.Rng.Next(3);
        for (var i = 0; i < numStructs; i++)
        {
            var s = _structs.GenerateStruct(i);
            _ctx.UserRecords.Add(s);
            sb.Append(_attrs.MaybeEmitFor(AttributeTarget.Record));
            sb.AppendLine(s.Definition);
        }

        if (numStructs > 0)
            sb.AppendLine();

        // 0-1 type-alias declaration + an uncalled helper that uses it, ~22%.
        // Exercises the alias-resolution codegen path on both backends. Emitted
        // before user functions; not registered in UserFuncs (never called).
        if (_ctx.Rng.NextDouble() < 0.22)
        {
            sb.AppendLine(_typeAlias.EmitAliasAndUser());
            sb.AppendLine();
        }

        // 0-1 user-defined record-producing macro per program. Macro and use
        // site are emitted adjacently; the macro-defined record is registered
        // into UserRecords.
        if (_ctx.Rng.NextDouble() < 0.20)
        {
            var macroBlock = _macros.GenerateMacroAndUse(out var macroRec);
            _ctx.UserRecords.Add(macroRec);
            sb.AppendLine(macroBlock);
            sb.AppendLine();
        }

        // 0-N expression macros (when/let1/min2). Definitions go at the top
        // level; ExprGenerator picks up registered names from _ctx.MacroIntCallables
        // and emits invocations at arbitrary Int positions.
        if (_ctx.Rng.NextDouble() < 0.30)
        {
            var exprMacros = _macros.GenerateExpressionMacros();
            if (!string.IsNullOrEmpty(exprMacros))
            {
                sb.AppendLine(exprMacros);
                sb.AppendLine();
            }
        }

        // Interfaces: 0-2. Emitted before classes so a class can implement one.
        var numInterfaces = _ctx.Rng.Next(3);
        for (var i = 0; i < numInterfaces; i++)
        {
            var iface = _interface.GenerateInterface(i);
            _ctx.UserInterfaces.Add(iface);
            sb.Append(_attrs.MaybeEmitFor(AttributeTarget.Interface));
            sb.AppendLine(iface.Definition);
        }

        if (numInterfaces > 0)
            sb.AppendLine();

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

            var baseCls = _class.GenerateClass(0, emitDerived, toImpl);
            _ctx.UserClasses.Add(baseCls);
            sb.Append(_attrs.MaybeEmitFor(AttributeTarget.Class));
            sb.AppendLine(baseCls.Definition);
            sb.AppendLine();

            if (emitDerived)
            {
                var derived = _class.GenerateDerivedClass(1, baseCls);
                _ctx.UserClasses.Add(derived);
                sb.Append(_attrs.MaybeEmitFor(AttributeTarget.Class));
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
            sb.Append(_attrs.MaybeEmitFor(AttributeTarget.Function));
            sb.AppendLine(func.Definition);
            sb.AppendLine();
        }

        // 0-1 variadic helper per program. Kept rare since variadic codegen
        // is one specific path — over-emitting would crowd out other shapes.
        if (_ctx.Rng.NextDouble() < 0.30)
        {
            var vf = _variadic.Generate($"vf{numFuncs}");
            _ctx.UserFuncs.Add(vf);
            sb.Append(_attrs.MaybeEmitFor(AttributeTarget.Function));
            sb.AppendLine(vf.Definition);
            sb.AppendLine();
        }

        // Delegate-form helpers: emit the `(delegate ...)`-typed helper defines
        // in ~28% of programs and enable the GenInt reducers that call them.
        // Emitted after aux generation so aux modules never reference the
        // helpers (their bodies are built before this flag is set).
        if (_ctx.Rng.NextDouble() < 0.28)
        {
            _ctx.EnableDelegateForms = true;
            sb.AppendLine(_delegate.EmitHelpers());
            sb.AppendLine();
        }

        // Mutual recursion pair — DISABLED until the compiler supports forward
        // references between top-level defines. TypeInferer.InferProgram
        // (TypeInferer.cs:353-358) currently registers each Define's type only
        // after inferring its body, so `(define (mr_a ...) (... (mr_b ...)))`
        // followed by `(define (mr_b ...) ...)` errors with
        // "Undefined variable: 'mr_b'". Re-verified 2026-05-02 — limitation
        // still exists; the MutualRecFuncGenerator file is kept intact so it
        // can be re-wired once the compiler grows a signature pre-pass.
        _ = _mutualRec;

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
