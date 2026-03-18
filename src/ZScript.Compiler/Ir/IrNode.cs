namespace ZScript.Compiler.Ir;

using ZScript.Compiler.Types;

public abstract record IrNode
{
    public ZType Type { get; init; } = ZType.Unit;
    public bool IsTailCall { get; set; }

    // Literals
    public sealed record IntConst(int Value) : IrNode { }
    public sealed record FloatConst(float Value) : IrNode { }
    public sealed record BoolConst(bool Value) : IrNode { }
    public sealed record StringConst(string Value) : IrNode { }
    public sealed record UnitConst() : IrNode { }

    // Variable reference
    public sealed record Var(string Name) : IrNode { }

    // Let binding
    public sealed record Let(string VarName, IrNode Value, IrNode Body) : IrNode { }

    // If expression
    public sealed record If(IrNode Condition, IrNode Then, IrNode Else) : IrNode { }

    // Function call
    public sealed record Call(IrNode Function, IReadOnlyList<IrNode> Args) : IrNode { }

    // Binary operation (lowered from built-in operator calls)
    public sealed record BinOp(string Op, IrNode Left, IrNode Right) : IrNode { }

    // Unary operation
    public sealed record UnaryOp(string Op, IrNode Operand) : IrNode { }

    // Function definition (top-level or lifted lambda)
    public sealed record FuncDef(
        string Name,
        IReadOnlyList<IrParam> Params,
        ZType ReturnType,
        IrNode Body,
        bool IsSelfRecursive) : IrNode { }

    // Closure (after lambda lifting)
    public sealed record Closure(
        string LiftedFuncName,
        IReadOnlyList<IrNode> CapturedValues) : IrNode { }

    // Record construction
    public sealed record RecordNew(
        string TypeName,
        IReadOnlyList<(string FieldName, IrNode Value)> Fields) : IrNode { }

    // Record field access
    public sealed record FieldGet(IrNode Record, string FieldName) : IrNode { }

    // Union case construction
    public sealed record UnionCaseNew(
        string UnionName,
        string CaseName,
        IReadOnlyList<IrNode> Args) : IrNode { }

    // Pattern match (before compilation to decision tree)
    public sealed record Match(IrNode Scrutinee, IReadOnlyList<IrMatchArm> Arms) : IrNode { }

    // Type test + cast (lowered from pattern match)
    public sealed record TypeTest(IrNode Value, string TypeName, string BindVar) : IrNode { }

    // Sequence of IR nodes (multiple top-level forms)
    public sealed record Seq(IReadOnlyList<IrNode> Nodes) : IrNode { }

    // Record type declaration (for codegen)
    public sealed record RecordDecl(
        string Name,
        IReadOnlyList<string> TypeParams,
        IReadOnlyList<IrField> Fields) : IrNode { }

    // Union type declaration (for codegen)
    public sealed record UnionDecl(
        string Name,
        IReadOnlyList<string> TypeParams,
        IReadOnlyList<IrUnionCase> Cases) : IrNode { }

    // List construction
    public sealed record ListNew(IReadOnlyList<IrNode> Elements) : IrNode { }

    // Vector construction
    public sealed record VectorNew(IReadOnlyList<IrNode> Elements) : IrNode { }

    // Map construction
    public sealed record MapNew(IReadOnlyList<(IrNode Key, IrNode Value)> Entries) : IrNode { }

    // CLR method call (from import-clr)
    public sealed record ClrCall(
        string QualifiedTypeName,
        string MethodName,
        IReadOnlyList<IrNode> Args) : IrNode { }

    // TCO jump (used during tail-call rewriting in C# emitter)
    public sealed record TcoJump(
        IReadOnlyList<string> ParamNames,
        IReadOnlyList<IrNode> NewArgs) : IrNode { }

    // Built-in constructor call (Ok, Err, Some, None, Error)
    public sealed record BuiltinCtorCall(
        string RuntimeTypeName,
        string? CaseName,
        IReadOnlyList<IrNode> Args,
        IReadOnlyList<ZType> TypeArgs) : IrNode { }

    // Error propagation (? expr) — unwraps Ok or early-returns Err
    public sealed record Propagate(IrNode Expr, ZType ResultType) : IrNode { }

    // Catch .NET exceptions and convert to Result<T, Error>
    public sealed record TryCatch(IrNode Body) : IrNode { }

    // Collection method call (list/head, vector/map, map/get, etc.)
    public sealed record MethodCall(
        IrNode Receiver,
        string MethodName,
        IReadOnlyList<IrNode> Args,
        bool IsProperty,
        bool IsIndexer) : IrNode { }
}

public sealed record IrParam(string Name, ZType Type);

public sealed record IrField(string Name, ZType Type);

public sealed record IrUnionCase(string Name, IReadOnlyList<IrField> Fields);

public sealed record IrMatchArm(IrPattern Pattern, IrNode Body);

public abstract record IrPattern
{
    public sealed record Wildcard() : IrPattern;
    public sealed record Variable(string Name) : IrPattern;
    public sealed record Literal(object Value) : IrPattern;
    public sealed record Constructor(string Name, IReadOnlyList<IrPattern> Fields) : IrPattern;
}
