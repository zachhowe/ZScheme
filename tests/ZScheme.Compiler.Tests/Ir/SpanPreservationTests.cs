using System.Collections;
using System.Reflection;
using Xunit;
using ZScheme.Compiler.Codegen;
using ZScheme.Compiler.Diagnostics;
using ZScheme.Compiler.Ir;
using ZScheme.Compiler.Pipeline;

namespace ZScheme.Compiler.Tests.Ir;

// IrNode.Span is an `init` property with a default, so `new IrNode.Let(...)` silently yields
// SourceSpan.None — unlike AstNode/SExpr/Token, which take their span as a positional record
// parameter the C# compiler forces every construction to supply. The IR is therefore both the
// only layer where a span can be dropped silently and the only layer that gets rewritten
// repeatedly.
//
// That matters because EmitCoverageProbe bails out early when CoverageInScope sees an empty file
// or Line <= 0. A dropped span does not produce a *wrong* coverage point, it produces *no*
// coverage point — instrumented code silently vanishes from the report — and codegen diagnostics
// on the same node degrade to (0:0).
//
// These tests encode the invariant directly rather than asserting per-pass, so a future rewrite
// pass that nobody thought to write a span test for is caught too.
public class SpanPreservationTests
{
    private static string GetStdLibPath()
    {
        var dir = Path.GetDirectoryName(typeof(SpanPreservationTests).Assembly.Location)!;
        while (dir is not null && !File.Exists(Path.Combine(dir, "ZScheme.slnx")))
            dir = Path.GetDirectoryName(dir);
        return Path.Combine(dir!, "packages", "stdlib", "src");
    }

    // Each entry drives a different rewrite path. The name is only there to make a failure
    // message say which shape broke.
    public static TheoryData<string, string> Programs =>
        new()
        {
            {
                "with-handlers as a call argument",
                """
                    (module test)
                    (define (take [a : Int] [b : Int]) : Int (+ a b))
                    (define (compute) : Int
                      (take 1 (with-handlers ([System.DivideByZeroException _] 0) (/ 10 0))))
                    """
            },
            {
                "with-handlers as a binop operand",
                """
                    (module test)
                    (define (compute) : Int
                      (+ 1 (with-handlers ([System.DivideByZeroException _] 0) (/ 10 0))))
                    """
            },
            {
                "await as a binop operand",
                """
                    (module test)
                    (define-async (helper [x : Int]) : (Task Int) x)
                    (define-async (compute) : (Task Int)
                      (+ 1 (await (helper 41))))
                    """
            },
            {
                "use in value position",
                """
                    (module test)
                    (import-clr
                      [ms-can-read System.IO.MemoryStream.CanRead :instance-property : (System.IO.MemoryStream -> Bool)]
                      [ms-flush System.IO.MemoryStream.Flush :instance : (System.IO.MemoryStream -> Unit)])
                    (define (compute) : Int
                      (let ([s (new System.IO.MemoryStream)])
                        (let ([u (use ([m s]) (ms-flush m))])
                          (if (ms-can-read s) 0 1))))
                    """
            },
            {
                "letrec with mutual recursion",
                """
                    (module test)
                    (define (compute) : Int
                      (letrec ([even? (lambda ([n : Int]) : Bool (if (= n 0) #t (odd? (- n 1))))]
                               [odd? (lambda ([n : Int]) : Bool (if (= n 0) #f (even? (- n 1))))])
                        (if (even? 10) 1 0)))
                    """
            },
            {
                "letrec function in value position",
                """
                    (module test)
                    (define (apply-twice [g : (Int -> Int)] [n : Int]) : Int (g (g n)))
                    (define (compute) : Int
                      (letrec ([step (lambda ([k : Int]) : Int (if (= k 0) 0 (+ 1 (step (- k 1)))))])
                        (apply-twice step 4)))
                    """
            },
            {
                "letrec capturing an enclosing local",
                """
                    (module test)
                    (define (compute) : Int
                      (let ([factor 3])
                        (letrec ([scale (lambda ([n : Int]) : Int (if (= n 0) 0 (+ factor (scale (- n 1)))))])
                          (scale 4))))
                    """
            },
            {
                "nested defines",
                """
                    (module test)
                    (define (mid [n : Int]) : Int
                      (define base 10)
                      (define (bump [k : Int]) : Int (+ k base))
                      (bump n))
                    (define (compute) : Int (mid 5))
                    """
            },
            {
                "immediately-invoked lambda",
                """
                    (module test)
                    (define (compute) : Int
                      ((lambda ([n : Int]) : Int (* n 2)) 21))
                    """
            },
            {
                "capturing lambda",
                """
                    (module test)
                    (define (apply-it [g : (Int -> Int)] [n : Int]) : Int (g n))
                    (define (compute) : Int
                      (let ([bump 5])
                        (apply-it (lambda ([n : Int]) : Int (+ n bump)) 37)))
                    """
            },
            {
                "match over a union",
                """
                    (module test)
                    (define-union (Either ^a ^b) (Lt [lv : ^a]) (Rt [rv : ^b]))
                    (define (compute) : Int
                      (match (Lt 99)
                        [(Lt _) 7]
                        [(Rt x) (let ([_ (- x x)]) 42)]))
                    """
            },
            {
                "tail self-recursion",
                """
                    (module test)
                    (define (sum-to [i : Int] [acc : Int]) : Int
                      (if (> i 10) acc (sum-to (+ i 1) (+ acc i))))
                    (define (compute) : Int (sum-to 1 0))
                    """
            },
        };

    [Theory]
    [MemberData(nameof(Programs))]
    public void EveryLineProbeNodeKeepsItsSpan(string shape, string source)
    {
        var ir = LowerAndApplyBackendRewrites(source);

        var spanless = IrWalker
            .DescendantsAndSelf(ir)
            .Where(IlEmitter.IsLineProbeNode)
            .Where(n => IrWalker.HasNoSpan(n.Span))
            .ToList();

        Assert.True(
            spanless.Count == 0,
            $"{shape}: {spanless.Count} line-probe node(s) reached codegen with no span, so the "
                + "IL backend will silently skip their coverage probes:\n"
                + string.Join(
                    "\n",
                    spanless.Select(n => "  " + n.GetType().Name + ": " + Describe(n))
                )
        );
    }

    private static string Describe(IrNode node) =>
        node switch
        {
            IrNode.Let let => $"let {let.VarName}",
            IrNode.Call { Function: IrNode.Var v } => $"call {v.Name}",
            IrNode.BinOp b => $"binop {b.Op}",
            IrNode.UnaryOp u => $"unaryop {u.Op}",
            IrNode.MethodCall mc => $"method {mc.MethodName}",
            IrNode.ClrCall cc => $"clr {cc.QualifiedTypeName}.{cc.MethodName}",
            _ => node.ToString() ?? "",
        };

    // Reproduces exactly what codegen sees: IrLowering's output (which already includes the
    // ObjectLifter / LetrecLifter / IiffeBetaReducer / ClosureConverter / PatternResolver
    // sub-passes) plus the three rewrites IlEmitter.Emit runs at its entry, in that same order.
    private static IrNode LowerAndApplyBackendRewrites(string source)
    {
        var compilation = new Compilation(
            new CompilerOptions
            {
                OutputMode = OutputMode.Il,
                AllowsImplicitModuleName = true,
                DisablePrelude = true,
                PackagePaths = new Dictionary<string, string> { ["stdlib"] = GetStdLibPath() },
            }
        );

        var result = compilation.Compile(source, "spans.zs");
        Assert.True(
            result.Success,
            "Compilation failed:\n" + string.Join("\n", result.Diagnostics.Diagnostics)
        );

        var ir = compilation.LoweredIr;
        Assert.NotNull(ir);

        ir = new WithHandlersHoister().Hoist(ir);
        ir = new AwaitHoister().Hoist(ir);
        ir = new TailCallLowering().Rewrite(ir);
        return ir;
    }
}

/// <summary>
///     Reflection-driven walk over an IR tree. The IR has no shared visitor (see the note on
///     <see cref="IrNode.LetRec" />), so a hand-written walker would silently stop covering any
///     node kind added later — exactly the failure mode these tests exist to prevent. Descending
///     over reflected members instead means a new node or a new child slot is picked up for free.
/// </summary>
internal static class IrWalker
{
    /// <summary>
    ///     Mirrors the rejection test in <c>IlEmitter.Coverage.CoverageInScope</c>, which is what
    ///     actually decides whether a node gets a probe. Note that <c>default(SourceSpan)</c> has
    ///     a null <c>File</c> while <see cref="SourceSpan.None" /> has an empty one, so the two
    ///     are unequal as records — comparing against <c>None</c> alone would miss half the cases.
    /// </summary>
    public static bool HasNoSpan(SourceSpan span) =>
        string.IsNullOrEmpty(span.File) || span.Line <= 0;

    public static IReadOnlyList<IrNode> DescendantsAndSelf(IrNode root)
    {
        var found = new List<IrNode>();
        Visit(root, found, new HashSet<object>(ReferenceEqualityComparer.Instance));
        return found;
    }

    private static void Visit(object? value, List<IrNode> found, HashSet<object> seen)
    {
        switch (value)
        {
            case null or string:
                return;
            case IrNode node:
                if (!seen.Add(node))
                    return;
                found.Add(node);
                VisitMembers(node, found, seen);
                return;
            case IEnumerable items:
                foreach (var item in items)
                    Visit(item, found, seen);
                return;
        }

        // Descend into the IR's own wrapper records — IrMatchArm, IrHandlerClause,
        // IrObjectMethod, IrConstructor — and into the (FieldName, Value) tuples RecordNew and
        // RecordWith hold their fields in. Everything else (ZType, SourceSpan, MethodInfo,
        // primitives) is a leaf.
        var type = value.GetType();
        if (type.Namespace != typeof(IrNode).Namespace && !IsValueTuple(type))
            return;
        if (!type.IsValueType && !seen.Add(value))
            return;
        VisitMembers(value, found, seen);
    }

    private static void VisitMembers(object owner, List<IrNode> found, HashSet<object> seen)
    {
        var type = owner.GetType();

        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            // Type and Span are leaves by definition, and skipping them keeps the walk off the
            // ZType graph entirely.
            if (
                property.GetIndexParameters().Length > 0
                || property.Name is nameof(IrNode.Type) or nameof(IrNode.Span)
            )
                continue;
            Visit(property.GetValue(owner), found, seen);
        }

        // ValueTuple exposes Item1/Item2 as fields, not properties.
        foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
            Visit(field.GetValue(owner), found, seen);
    }

    private static bool IsValueTuple(Type type) =>
        type.IsGenericType
        && type.FullName?.StartsWith("System.ValueTuple`", StringComparison.Ordinal) == true;
}
