using ZScheme.Compiler.Types;

namespace ZScheme.Compiler.Ir;

/// <summary>
///     Lifts lambdas into top-level functions with explicit capture parameters.
/// </summary>
public sealed class ClosureConverter
{
    private readonly List<IrNode.FuncDef> _liftedFunctions = [];
    private int _closureId;

    public IReadOnlyList<IrNode.FuncDef> LiftedFunctions => _liftedFunctions;

    public IrNode Convert(IrNode node)
    {
        return node switch
        {
            IrNode.Seq seq => new IrNode.Seq(seq.Nodes.Select(Convert).ToList())
            {
                Type = seq.Type,
                Span = seq.Span,
            },
            IrNode.FuncDef func => ConvertFuncDef(func),
            IrNode.Let let => new IrNode.Let(let.VarName, Convert(let.Value), Convert(let.Body))
            {
                Type = let.Type,
                Span = let.Span,
            },
            IrNode.Use use => new IrNode.Use(
                use.VarName,
                Convert(use.Value),
                Convert(use.Body),
                use.VarType
            )
            {
                Type = use.Type,
                Span = use.Span,
            },
            IrNode.If @if => new IrNode.If(
                Convert(@if.Condition),
                Convert(@if.Then),
                Convert(@if.Else)
            )
            {
                Type = @if.Type,
                Span = @if.Span,
            },
            IrNode.Call call => new IrNode.Call(
                Convert(call.Function),
                call.Args.Select(Convert).ToList()
            )
            {
                Type = call.Type,
                IsTailCall = call.IsTailCall,
                Span = call.Span,
            },
            IrNode.BinOp binop => new IrNode.BinOp(
                binop.Op,
                Convert(binop.Left),
                Convert(binop.Right)
            )
            {
                Type = binop.Type,
                Span = binop.Span,
            },
            IrNode.UnaryOp unary => new IrNode.UnaryOp(unary.Op, Convert(unary.Operand))
            {
                Type = unary.Type,
                Span = unary.Span,
            },
            IrNode.Match match => new IrNode.Match(
                Convert(match.Scrutinee),
                match.Arms.Select(a => new IrMatchArm(a.Pattern, Convert(a.Body))).ToList()
            )
            {
                Type = match.Type,
                Span = match.Span,
            },
            IrNode.UnionCaseNew ucn => new IrNode.UnionCaseNew(
                ucn.UnionName,
                ucn.CaseName,
                ucn.Args.Select(Convert).ToList()
            )
            {
                Type = ucn.Type,
                Span = ucn.Span,
            },
            IrNode.MethodCall mc => mc with
            {
                Receiver = Convert(mc.Receiver),
                Args = mc.Args.Select(Convert).ToList(),
            },
            IrNode.ClrNew cn => new IrNode.ClrNew(
                cn.QualifiedTypeName,
                cn.TypeArgs,
                cn.Args.Select(Convert).ToList()
            )
            {
                Type = cn.Type,
                Span = cn.Span,
            },
            IrNode.TupleNew tn => new IrNode.TupleNew(tn.Elements.Select(Convert).ToList())
            {
                Type = tn.Type,
                Span = tn.Span,
            },
            IrNode.SetField sf => new IrNode.SetField(sf.FieldName, Convert(sf.Value))
            {
                Type = sf.Type,
                Span = sf.Span,
            },
            _ => node,
        };
    }

    private IrNode ConvertFuncDef(IrNode.FuncDef func)
    {
        // Find free variables in the function body
        var freeVars = FindFreeVars(func.Body, func.Params.Select(p => p.Name).ToHashSet());

        if (freeVars.Count == 0)
            // No captures needed — just recurse into body
            return func with { Body = Convert(func.Body) };

        // Create a lifted function with capture parameters prepended
        var captureParams = freeVars.Select(v => new IrParam(v, ZType.Unit)).ToList(); // Type will be resolved later
        var allParams = captureParams.Concat(func.Params).ToList();

        var liftedName = $"__closure_{_closureId++}_{func.Name}";
        var liftedBody = Convert(func.Body);
        var liftedFunc = new IrNode.FuncDef(
            liftedName,
            allParams,
            func.ReturnType,
            liftedBody,
            func.IsSelfRecursive,
            func.TypeParams
        )
        {
            Type = func.Type,
            Span = func.Span,
        };
        _liftedFunctions.Add(liftedFunc);

        // Replace the original with a closure node
        var capturedValues = freeVars
            .Select(v => (IrNode)new IrNode.Var(v) { Type = ZType.Unit, Span = func.Span })
            .ToList();
        return new IrNode.Closure(liftedName, capturedValues)
        {
            Type = func.Type,
            Span = func.Span,
        };
    }

    private HashSet<string> FindFreeVars(IrNode node, HashSet<string> bound)
    {
        return node switch
        {
            IrNode.Var v => bound.Contains(v.Name) ? [] : [v.Name],
            IrNode.Let let => Merge(
                FindFreeVars(let.Value, bound),
                FindFreeVars(let.Body, AddToBound(bound, let.VarName))
            ),
            IrNode.Use use => Merge(
                FindFreeVars(use.Value, bound),
                FindFreeVars(use.Body, AddToBound(bound, use.VarName))
            ),
            IrNode.If @if => Merge(
                FindFreeVars(@if.Condition, bound),
                Merge(FindFreeVars(@if.Then, bound), FindFreeVars(@if.Else, bound))
            ),
            IrNode.Call call => Merge(
                FindFreeVars(call.Function, bound),
                call.Args.Aggregate(
                    new HashSet<string>(),
                    (acc, a) => Merge(acc, FindFreeVars(a, bound))
                )
            ),
            IrNode.BinOp binop => Merge(
                FindFreeVars(binop.Left, bound),
                FindFreeVars(binop.Right, bound)
            ),
            IrNode.UnaryOp unary => FindFreeVars(unary.Operand, bound),
            IrNode.FuncDef func => FindFreeVars(
                func.Body,
                new HashSet<string>(bound.Concat(func.Params.Select(p => p.Name)))
            ),
            IrNode.Match match => Merge(
                FindFreeVars(match.Scrutinee, bound),
                match.Arms.Aggregate(
                    new HashSet<string>(),
                    (acc, a) => Merge(acc, FindFreeVars(a.Body, bound))
                )
            ),
            IrNode.UnionCaseNew ucn => ucn.Args.Aggregate(
                new HashSet<string>(),
                (acc, a) => Merge(acc, FindFreeVars(a, bound))
            ),
            IrNode.MethodCall mc => Merge(
                FindFreeVars(mc.Receiver, bound),
                mc.Args.Aggregate(
                    new HashSet<string>(),
                    (acc, a) => Merge(acc, FindFreeVars(a, bound))
                )
            ),
            IrNode.ClrNew cn => cn.Args.Aggregate(
                new HashSet<string>(),
                (acc, a) => Merge(acc, FindFreeVars(a, bound))
            ),
            IrNode.TupleNew tn => tn.Elements.Aggregate(
                new HashSet<string>(),
                (acc, e) => Merge(acc, FindFreeVars(e, bound))
            ),
            IrNode.SetField sf => FindFreeVars(sf.Value, bound),
            _ => [],
        };
    }

    private static HashSet<string> Merge(HashSet<string> a, HashSet<string> b)
    {
        var result = new HashSet<string>(a);
        result.UnionWith(b);
        return result;
    }

    private static HashSet<string> AddToBound(HashSet<string> bound, string name)
    {
        var result = new HashSet<string>(bound) { name };
        return result;
    }
}
