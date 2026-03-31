using ZScript.Compiler.Types;

namespace ZScript.Compiler.Ir;

public abstract record IrNode
{
    public ZType Type { get; init; } = ZType.Unit;
    public bool IsTailCall { get; set; }

    // Literals
    public sealed record IntConst(int Value) : IrNode
    {
    }

    public sealed record FloatConst(float Value) : IrNode
    {
    }

    public sealed record BoolConst(bool Value) : IrNode
    {
    }

    public sealed record StringConst(string Value) : IrNode
    {
    }

    public sealed record UnitConst : IrNode
    {
    }

    // Variable reference
    public sealed record Var(string Name) : IrNode
    {
    }

    // Let binding
    public sealed record Let(string VarName, IrNode Value, IrNode Body) : IrNode
    {
    }

    // If expression
    public sealed record If(IrNode Condition, IrNode Then, IrNode Else) : IrNode
    {
    }

    // Function call
    public sealed record Call(IrNode Function, IReadOnlyList<IrNode> Args) : IrNode
    {
    }

    // Binary operation (lowered from built-in operator calls)
    public sealed record BinOp(string Op, IrNode Left, IrNode Right) : IrNode
    {
    }

    // Unary operation
    public sealed record UnaryOp(string Op, IrNode Operand) : IrNode
    {
    }

    // Function definition (top-level or lifted lambda)
    public sealed record FuncDef(
        string Name,
        IReadOnlyList<IrParam> Params,
        ZType ReturnType,
        IrNode Body,
        bool IsSelfRecursive,
        IReadOnlyList<string>? TypeParams = null,
        IReadOnlyList<IrAttribute>? Attributes = null,
        bool IsAsync = false,
        IReadOnlyDictionary<string, GenericConstraintKind>? TypeParamConstraints = null) : IrNode
    {
    }

    // Closure (after lambda lifting)
    public sealed record Closure(
        string LiftedFuncName,
        IReadOnlyList<IrNode> CapturedValues) : IrNode
    {
    }

    // Record construction
    public sealed record RecordNew(
        string TypeName,
        IReadOnlyList<(string FieldName, IrNode Value)> Fields) : IrNode
    {
    }

    // Record field access
    public sealed record FieldGet(IrNode Record, string FieldName) : IrNode
    {
    }

    // Union case construction
    public sealed record UnionCaseNew(
        string UnionName,
        string CaseName,
        IReadOnlyList<IrNode> Args) : IrNode
    {
    }

    // Pattern match (before compilation to decision tree)
    public sealed record Match(IrNode Scrutinee, IReadOnlyList<IrMatchArm> Arms) : IrNode
    {
    }

    // Type test + cast (lowered from pattern match)
    public sealed record TypeTest(IrNode Value, string TypeName, string BindVar) : IrNode
    {
    }

    // Sequence of IR nodes (multiple top-level forms)
    public sealed record Seq(IReadOnlyList<IrNode> Nodes) : IrNode
    {
    }

    // Record type declaration (for codegen)
    public sealed record RecordDecl(
        string Name,
        IReadOnlyList<string> TypeParams,
        IReadOnlyList<IrField> Fields,
        IReadOnlyList<IrAttribute>? Attributes = null,
        IReadOnlyDictionary<string, GenericConstraintKind>? TypeParamConstraints = null) : IrNode
    {
    }

    // Union type declaration (for codegen)
    public sealed record UnionDecl(
        string Name,
        IReadOnlyList<string> TypeParams,
        IReadOnlyList<IrUnionCase> Cases,
        IReadOnlyList<IrAttribute>? Attributes = null,
        IReadOnlyDictionary<string, GenericConstraintKind>? TypeParamConstraints = null) : IrNode
    {
    }

    // List construction
    public sealed record ListNew(IReadOnlyList<IrNode> Elements) : IrNode
    {
    }

    // Array construction
    public sealed record ArrayNew(IReadOnlyList<IrNode> Elements) : IrNode
    {
    }

    // Map construction
    public sealed record MapNew(IReadOnlyList<(IrNode Key, IrNode Value)> Entries) : IrNode
    {
    }

    // CLR constructor call (from new special form)
    public sealed record ClrNew(
        string QualifiedTypeName,
        IReadOnlyList<IrNode> Args) : IrNode
    {
    }

    // CLR method call (from import-clr)
    public sealed record ClrCall(
        string QualifiedTypeName,
        string MethodName,
        IReadOnlyList<IrNode> Args,
        int GenericArity = 0) : IrNode
    {
    }

    // TCO jump (used during tail-call rewriting in C# emitter)
    public sealed record TcoJump(
        IReadOnlyList<string> ParamNames,
        IReadOnlyList<IrNode> NewArgs) : IrNode
    {
    }

    // Error propagation (? expr) — unwraps Ok or early-returns Err
    public sealed record Propagate(IrNode Expr, ZType ResultType) : IrNode
    {
    }

    // Catch .NET exceptions and convert to Result<T, Error>
    public sealed record TryCatch(IrNode Body) : IrNode
    {
    }

    // Throw a .NET exception (from raise special form)
    public sealed record Throw(IrNode Expr) : IrNode
    {
    }

    // Await a Task expression
    public sealed record Await(IrNode Expr) : IrNode
    {
    }

    // Anonymous object implementing .NET interfaces, optionally inheriting from a base class
    public sealed record ObjectExpr(
        IReadOnlyList<string> InterfaceNames,
        IReadOnlyList<IrObjectMethod> Methods,
        string? BaseClassName = null,
        IrConstructor? Constructor = null) : IrNode
    {
    }

    // Class declaration (for codegen)
    public sealed record ClassDecl(
        string Name,
        IReadOnlyList<string> TypeParams,
        IReadOnlyList<string> InterfaceNames,
        IReadOnlyList<IrField> Fields,
        IReadOnlyList<IrObjectMethod> Methods,
        bool IsOpen = false,
        string? BaseClassName = null,
        IrConstructor? Constructor = null,
        IReadOnlyList<IrAttribute>? Attributes = null,
        IReadOnlyDictionary<string, GenericConstraintKind>? TypeParamConstraints = null) : IrNode
    {
    }

    // super/MethodName call (for codegen)
    public sealed record SuperMethodCall(
        string MethodName,
        IReadOnlyList<IrNode> Args) : IrNode
    {
    }

    // Interface declaration (for codegen)
    public sealed record InterfaceDecl(
        string Name,
        IReadOnlyList<string> TypeParams,
        IReadOnlyList<string> BaseInterfaceNames,
        IReadOnlyList<IrInterfaceMethodSignature> Methods,
        IReadOnlyList<IrAttribute>? Attributes = null,
        IReadOnlyDictionary<string, GenericConstraintKind>? TypeParamConstraints = null) : IrNode
    {
    }

    // Collection method call (list/head, vector/map, map/get, etc.)
    public sealed record MethodCall(
        IrNode Receiver,
        string MethodName,
        IReadOnlyList<IrNode> Args,
        bool IsProperty,
        bool IsIndexer,
        bool IsPropertySet = false,
        bool IsIndexerSet = false) : IrNode
    {
    }
}

public sealed record IrObjectMethod(
    string Name,
    IReadOnlyList<IrParam> Params,
    ZType ReturnType,
    IrNode Body,
    IReadOnlyList<IrAttribute>? Attributes = null);

public sealed record IrInterfaceMethodSignature(
    string Name,
    IReadOnlyList<IrParam> Params,
    ZType ReturnType);

public sealed record IrConstructor(
    IReadOnlyList<IrParam> Params,
    IReadOnlyList<IrNode>? SuperArgs,
    IReadOnlyList<(string FieldName, IrNode Value)> FieldSets,
    IReadOnlyList<IrNode> BodyExprs);

public sealed record IrAttribute(
    string Name,
    IReadOnlyList<object> PositionalArgs,
    IReadOnlyList<(string Name, object Value)> NamedArgs);

public sealed record IrParam(string Name, ZType Type, IReadOnlyList<IrAttribute>? Attributes = null);

public sealed record IrField(string Name, ZType Type, IReadOnlyList<IrAttribute>? Attributes = null);

public sealed record IrUnionCase(string Name, IReadOnlyList<IrField> Fields);

public sealed record IrMatchArm(IrPattern Pattern, IrNode Body);

public abstract record IrPattern
{
    public sealed record Wildcard : IrPattern;

    public sealed record Variable(string Name) : IrPattern;

    public sealed record Literal(object Value) : IrPattern;

    public sealed record Constructor(string Name, IReadOnlyList<IrPattern> Fields) : IrPattern;
}
