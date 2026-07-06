using ZScheme.Compiler.Types;

namespace ZScheme.Compiler.Builtins;

/// <summary>
///     How <c>AstBuilder</c> normalizes a variable-arity call to a built-in operator
///     into the arity-1/arity-2 shape the rest of the pipeline expects.
/// </summary>
public enum FoldKind
{
    /// <summary>Not a variadic operator; the call passes through unchanged.</summary>
    None,

    /// <summary><c>+</c>, <c>*</c>: left-associative fold; a single arg returns unchanged.</summary>
    ArithIdentity,

    /// <summary>
    ///     <c>-</c>, <c>/</c>: left-associative fold; a single arg passes through so the
    ///     inferer/IR can lower it to unary negation / reciprocal.
    /// </summary>
    ArithUnary,

    /// <summary>
    ///     <c>%</c>: left-associative fold with no single-argument form (a lone arg is an
    ///     arity error). Distinct from <see cref="ArithUnary" /> because modulo has no unary shape.
    /// </summary>
    ArithStrict,

    /// <summary><c>=</c>, <c>&lt;</c>, <c>&gt;</c>, <c>&lt;=</c>, <c>&gt;=</c>: expand to an AND chain of adjacent comparisons.</summary>
    CmpChain,

    /// <summary><c>!=</c>: expand to an AND of every pairwise <c>!=</c> (all-distinct).</summary>
    NeqAllDistinct,

    /// <summary><c>and</c>, <c>or</c>: right-associative fold preserving short-circuit shape.</summary>
    BoolFold,
}

/// <summary>
///     Neutral description of how <c>IrLowering</c> should turn a built-in call into an
///     <c>IrNode</c>. Kept free of any <c>Ir</c> dependency so this registry depends only
///     on <c>Types</c>; <c>IrLowering</c> interprets these cases.
/// </summary>
public abstract record BuiltinLowering
{
    /// <summary>
    ///     Lowered by arity to <c>IrNode.BinOp</c> (arity 2) and/or <c>IrNode.UnaryOp</c>
    ///     (arity 1), using <see cref="OpOverride" /> ?? the built-in's name as the op string.
    ///     <c>-</c> is both binary and unary; <c>not</c> is unary-only; <c>string-append</c>
    ///     is binary with an <see cref="OpOverride" /> of <c>"+"</c>.
    /// </summary>
    public sealed record Operator(bool Binary, bool Unary, string? OpOverride = null) : BuiltinLowering;

    /// <summary>Arity-1 call lowered to <c>arg.ToString()</c> (e.g. <c>int-&gt;string</c>).</summary>
    public sealed record ToStringCall : BuiltinLowering;

    /// <summary>Arity-1 call lowered to a static CLR method call (e.g. <c>System.Convert.ToInt32</c>).</summary>
    public sealed record ClrStaticCall(string TypeName, string MethodName) : BuiltinLowering;
}

/// <summary>
///     One compiler-native built-in function: its name, type signature, IR-lowering spec,
///     and variadic-fold category.
/// </summary>
public sealed record BuiltinFn(
    string Name,
    ZType Signature,
    BuiltinLowering Lowering,
    FoldKind Fold
);

/// <summary>
///     The single source of truth for ZScheme's compiler-native built-in functions
///     (arithmetic/comparison/boolean operators and numeric/string/symbol conversions).
///     Consumed by <c>TypeEnv</c> (signatures), <c>IrLowering</c> (lowering), and
///     <c>AstBuilder</c> (variadic folding) so the vocabulary is defined exactly once.
///     <para>
///         Collection conversions (<c>vector-&gt;immutable-vector</c>, etc.) and Scheme
///         primitives like <c>cons</c>/<c>car</c>/<c>cdr</c> are NOT built-ins — they live in
///         stdlib <c>.zs</c> and flow through the normal call path.
///     </para>
/// </summary>
public static class BuiltinRegistry
{
    static BuiltinRegistry()
    {
        var list = new List<BuiltinFn>();

        // Numeric constraint {Int, Float} shared by arithmetic and ordered comparisons.
        IReadOnlySet<PrimitiveKind> numericKinds = new HashSet<PrimitiveKind>
        {
            PrimitiveKind.Int,
            PrimitiveKind.Float,
        };

        // Arithmetic operators: forall a:{Int,Float}. (a, a) -> a
        // Fixed bound-var ids (9200+) avoid colliding with inference-fresh vars.
        var arithOps = new[] { "+", "-", "*", "/" };
        for (var i = 0; i < arithOps.Length; i++)
        {
            var op = arithOps[i];
            var numVar = new ZType.ZConstrainedVar(9200 + i, numericKinds);
            list.Add(
                new BuiltinFn(
                    op,
                    new ZType.ZForAllType([numVar.Id], new ZType.ZFuncType([numVar, numVar], numVar)),
                    new BuiltinLowering.Operator(Binary: true, Unary: op == "-"),
                    op is "+" or "*" ? FoldKind.ArithIdentity : FoldKind.ArithUnary
                )
            );
        }

        // Modulo: (Int, Int) -> Int
        list.Add(
            new BuiltinFn(
                "%",
                new ZType.ZFuncType([ZType.Int, ZType.Int], ZType.Int),
                new BuiltinLowering.Operator(Binary: true, Unary: false),
                FoldKind.ArithStrict
            )
        );

        // Ordered comparison operators: forall a:{Int,Float}. (a, a) -> Bool
        var ordOps = new[] { "<", ">", "<=", ">=" };
        for (var i = 0; i < ordOps.Length; i++)
        {
            var cmpVar = new ZType.ZConstrainedVar(9210 + i, numericKinds);
            list.Add(
                new BuiltinFn(
                    ordOps[i],
                    new ZType.ZForAllType(
                        [cmpVar.Id],
                        new ZType.ZFuncType([cmpVar, cmpVar], ZType.Bool)
                    ),
                    new BuiltinLowering.Operator(Binary: true, Unary: false),
                    FoldKind.CmpChain
                )
            );
        }

        // Equality operators: forall a. (a, a) -> Bool
        var eqVar1 = new ZType.ZTypeVar(9220);
        list.Add(
            new BuiltinFn(
                "=",
                new ZType.ZForAllType([eqVar1.Id], new ZType.ZFuncType([eqVar1, eqVar1], ZType.Bool)),
                new BuiltinLowering.Operator(Binary: true, Unary: false),
                FoldKind.CmpChain
            )
        );
        var eqVar2 = new ZType.ZTypeVar(9221);
        list.Add(
            new BuiltinFn(
                "!=",
                new ZType.ZForAllType([eqVar2.Id], new ZType.ZFuncType([eqVar2, eqVar2], ZType.Bool)),
                new BuiltinLowering.Operator(Binary: true, Unary: false),
                FoldKind.NeqAllDistinct
            )
        );

        // Boolean operators
        var boolBinOp = new ZType.ZFuncType([ZType.Bool, ZType.Bool], ZType.Bool);
        list.Add(
            new BuiltinFn(
                "and",
                boolBinOp,
                new BuiltinLowering.Operator(Binary: true, Unary: false),
                FoldKind.BoolFold
            )
        );
        list.Add(
            new BuiltinFn(
                "or",
                boolBinOp,
                new BuiltinLowering.Operator(Binary: true, Unary: false),
                FoldKind.BoolFold
            )
        );
        list.Add(
            new BuiltinFn(
                "not",
                new ZType.ZFuncType([ZType.Bool], ZType.Bool),
                new BuiltinLowering.Operator(Binary: false, Unary: true),
                FoldKind.None
            )
        );

        // String concatenation: (String, String) -> String, lowered to BinOp("+").
        list.Add(
            new BuiltinFn(
                "string-append",
                new ZType.ZFuncType([ZType.String, ZType.String], ZType.String),
                new BuiltinLowering.Operator(Binary: true, Unary: false, OpOverride: "+"),
                FoldKind.None
            )
        );

        // Conversion functions (all arity-1).
        list.Add(
            new BuiltinFn(
                "int->float",
                new ZType.ZFuncType([ZType.Int], ZType.Float),
                new BuiltinLowering.ClrStaticCall("System.Convert", "ToSingle"),
                FoldKind.None
            )
        );
        list.Add(
            new BuiltinFn(
                "float->int",
                new ZType.ZFuncType([ZType.Float], ZType.Int),
                new BuiltinLowering.ClrStaticCall("System.Convert", "ToInt32"),
                FoldKind.None
            )
        );
        list.Add(
            new BuiltinFn(
                "int->string",
                new ZType.ZFuncType([ZType.Int], ZType.String),
                new BuiltinLowering.ToStringCall(),
                FoldKind.None
            )
        );
        list.Add(
            new BuiltinFn(
                "string->int",
                new ZType.ZFuncType([ZType.String], ZType.Int),
                new BuiltinLowering.ClrStaticCall("System.Int32", "Parse"),
                FoldKind.None
            )
        );
        list.Add(
            new BuiltinFn(
                "symbol->string",
                new ZType.ZFuncType([ZType.Symbol], ZType.String),
                // ZSymbol.ToString() returns the symbol name.
                new BuiltinLowering.ToStringCall(),
                FoldKind.None
            )
        );
        list.Add(
            new BuiltinFn(
                "string->symbol",
                new ZType.ZFuncType([ZType.String], ZType.Symbol),
                new BuiltinLowering.ClrStaticCall("ZScheme.Runtime.ZSymbol", "Intern"),
                FoldKind.None
            )
        );
        list.Add(
            new BuiltinFn(
                "double->float",
                new ZType.ZFuncType([ZType.Double], ZType.Float),
                new BuiltinLowering.ClrStaticCall("System.Convert", "ToSingle"),
                FoldKind.None
            )
        );
        list.Add(
            new BuiltinFn(
                "float->double",
                new ZType.ZFuncType([ZType.Float], ZType.Double),
                new BuiltinLowering.ClrStaticCall("System.Convert", "ToDouble"),
                FoldKind.None
            )
        );

        All = list;
        ByName = list.ToDictionary(b => b.Name);
        BinaryOps = list.Where(b => b.Lowering is BuiltinLowering.Operator { Binary: true })
            .Select(b => b.Name)
            .ToHashSet();
        UnaryOps = list.Where(b => b.Lowering is BuiltinLowering.Operator { Unary: true })
            .Select(b => b.Name)
            .ToHashSet();
    }

    /// <summary>All built-in functions, in declaration order.</summary>
    public static IReadOnlyList<BuiltinFn> All { get; }

    /// <summary>Built-ins keyed by their ZScheme name.</summary>
    public static IReadOnlyDictionary<string, BuiltinFn> ByName { get; }

    /// <summary>Names of built-ins that lower to a binary <c>IrNode.BinOp</c> at arity 2.</summary>
    public static IReadOnlySet<string> BinaryOps { get; }

    /// <summary>Names of built-ins that lower to a unary <c>IrNode.UnaryOp</c> at arity 1.</summary>
    public static IReadOnlySet<string> UnaryOps { get; }

    /// <summary>
    ///     The concrete return type of a non-generic built-in signature, or <c>Unit</c> for
    ///     polymorphic (<c>ZForAllType</c>) signatures. Used only as a fallback IR node type
    ///     when a call somehow reaches lowering without an inferred type.
    /// </summary>
    public static ZType ConcreteReturnOrUnit(BuiltinFn builtin)
    {
        return builtin.Signature is ZType.ZFuncType f ? f.Return : ZType.Unit;
    }
}
