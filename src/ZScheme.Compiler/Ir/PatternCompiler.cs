using ZScheme.Compiler.Diagnostics;
using ZScheme.Compiler.Types;

namespace ZScheme.Compiler.Ir;

/// <summary>
///     Compiles pattern match expressions into decision trees (if/typetest/cast chains).
/// </summary>
public sealed class PatternCompiler
{
    public IrNode Compile(IrNode.Match match)
    {
        return CompileArms(match.Scrutinee, match.Arms.ToList(), match.Type, match.Span);
    }

    private IrNode CompileArms(
        IrNode scrutinee,
        List<IrMatchArm> arms,
        ZType resultType,
        SourceSpan matchSpan
    )
    {
        if (arms.Count == 0)
            // No arms — should be caught by exhaustiveness checker
            return new IrNode.Call(
                new IrNode.Var("__match_failure") { Type = ZType.Unit, Span = matchSpan },
                []
            )
            {
                Type = resultType,
                Span = matchSpan,
            };

        var arm = arms[0];
        var remaining = arms.Skip(1).ToList();
        var armSpan = arm.Body.Span;

        var (condition, bindings) = CompilePattern(scrutinee, arm.Pattern, armSpan);

        var body = WrapWithBindings(arm.Body, bindings, armSpan);

        if (remaining.Count == 0 && condition is null)
            return body;

        if (condition is null)
            return body; // Irrefutable pattern

        var elseBody = CompileArms(scrutinee, remaining, resultType, matchSpan);
        return new IrNode.If(condition, body, elseBody) { Type = resultType, Span = armSpan };
    }

    private (IrNode? Condition, List<(string Name, IrNode Value)> Bindings) CompilePattern(
        IrNode scrutinee,
        IrPattern pattern,
        SourceSpan span
    )
    {
        switch (pattern)
        {
            case IrPattern.Wildcard:
                return (null, []);

            case IrPattern.Variable v:
                return (null, [(v.Name, scrutinee)]);

            case IrPattern.Literal lit:
                var litNode = lit.Value switch
                {
                    int i => (IrNode)new IrNode.IntConst(i) { Type = ZType.Int, Span = span },
                    float f => new IrNode.FloatConst(f) { Type = ZType.Float, Span = span },
                    bool b => new IrNode.BoolConst(b) { Type = ZType.Bool, Span = span },
                    string s => new IrNode.StringConst(s) { Type = ZType.String, Span = span },
                    global::ZScheme.Runtime.ZSymbol sym => new IrNode.SymbolConst(sym.Name)
                    {
                        Type = ZType.Symbol,
                        Span = span,
                    },
                    _ => new IrNode.IntConst(0) { Type = ZType.Int, Span = span },
                };
                var cond = new IrNode.BinOp("=", scrutinee, litNode)
                {
                    Type = ZType.Bool,
                    Span = span,
                };
                return (cond, []);

            case IrPattern.Tuple tup:
                var tupleBindings = new List<(string Name, IrNode Value)>();
                IrNode? tupleCond = null;
                for (var i = 0; i < tup.Elements.Count; i++)
                {
                    var fieldAccess = new IrNode.FieldGet(scrutinee, $"Item{i + 1}")
                    {
                        Type = ZType.Unit,
                        Span = span,
                    };
                    var (subCond, subBindings) = CompilePattern(fieldAccess, tup.Elements[i], span);
                    tupleBindings.AddRange(subBindings);
                    if (subCond is not null)
                        tupleCond = tupleCond is null
                            ? subCond
                            : new IrNode.BinOp("and", tupleCond, subCond)
                            {
                                Type = ZType.Bool,
                                Span = span,
                            };
                }

                return (tupleCond, tupleBindings);

            case IrPattern.Constructor ctor:
                var typeTest = new IrNode.TypeTest(scrutinee, ctor.Name, $"__{ctor.Name}_val")
                {
                    Type = ZType.Bool,
                    Span = span,
                };

                var bindings = new List<(string Name, IrNode Value)>();
                // For each field in the constructor pattern, create a field access and recurse
                for (var i = 0; i < ctor.Fields.Count; i++)
                {
                    var fieldAccess = new IrNode.FieldGet(
                        new IrNode.Var($"__{ctor.Name}_val") { Type = scrutinee.Type, Span = span },
                        $"Item{i + 1}"
                    )
                    {
                        Type = ZType.Unit,
                        Span = span,
                    };

                    var (subCond, subBindings) = CompilePattern(fieldAccess, ctor.Fields[i], span);
                    bindings.AddRange(subBindings);
                    // sub-conditions would need to be ANDed together (simplified here)
                }

                return (typeTest, bindings);

            default:
                return (null, []);
        }
    }

    private IrNode WrapWithBindings(
        IrNode body,
        List<(string Name, IrNode Value)> bindings,
        SourceSpan span
    )
    {
        var result = body;
        for (var i = bindings.Count - 1; i >= 0; i--)
            result = new IrNode.Let(bindings[i].Name, bindings[i].Value, result)
            {
                Type = body.Type,
                Span = span,
            };
        return result;
    }
}
