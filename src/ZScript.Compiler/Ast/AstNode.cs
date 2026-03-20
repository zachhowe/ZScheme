namespace ZScript.Compiler.Ast;

using ZScript.Compiler.Diagnostics;
using ZScript.Compiler.Types;

public abstract record AstNode(SourceSpan Span)
{
    public ZType? ResolvedType { get; set; }

    // Literals
    public sealed record IntLit(int Value, SourceSpan Span) : AstNode(Span);
    public sealed record FloatLit(float Value, SourceSpan Span) : AstNode(Span);
    public sealed record BoolLit(bool Value, SourceSpan Span) : AstNode(Span);
    public sealed record StringLit(string Value, SourceSpan Span) : AstNode(Span);
    public sealed record UnitLit(SourceSpan Span) : AstNode(Span);

    // Names
    public sealed record Name(string Value, SourceSpan Span) : AstNode(Span);

    // (let [x expr] body)
    public sealed record Let(string VarName, AstNode Value, AstNode Body, SourceSpan Span) : AstNode(Span);

    // (if cond then else)
    public sealed record If(AstNode Condition, AstNode Then, AstNode Else, SourceSpan Span) : AstNode(Span);

    // (fn [params...] body) — lambda
    public sealed record Lambda(IReadOnlyList<Param> Params, AstNode Body, SourceSpan Span) : AstNode(Span);

    // Function application: (f arg1 arg2 ...)
    public sealed record Apply(AstNode Function, IReadOnlyList<AstNode> Args, SourceSpan Span) : AstNode(Span);

    // (define (name [params...]) : ReturnType body)
    public sealed record Define(
        string FnName,
        IReadOnlyList<Param> Params,
        ZType? ReturnTypeAnnotation,
        AstNode Body,
        SourceSpan Span,
        IReadOnlyList<AttributeDecl>? Attributes = null) : AstNode(Span);

    // (define name expr) — value binding
    public sealed record DefineValue(string VarName, AstNode Value, SourceSpan Span,
        IReadOnlyList<AttributeDecl>? Attributes = null) : AstNode(Span);

    // (record Name [field : Type] ...)
    public sealed record RecordDecl(
        string RecordName,
        IReadOnlyList<string> TypeParams,
        IReadOnlyList<FieldDecl> Fields,
        SourceSpan Span,
        IReadOnlyList<AttributeDecl>? Attributes = null) : AstNode(Span);

    // (union Name (Case1 [field : Type]) ...)
    public sealed record UnionDecl(
        string UnionName,
        IReadOnlyList<string> TypeParams,
        IReadOnlyList<UnionCase> Cases,
        SourceSpan Span,
        IReadOnlyList<AttributeDecl>? Attributes = null) : AstNode(Span);

    // (match expr [pattern body] ...)
    public sealed record Match(
        AstNode Scrutinee,
        IReadOnlyList<MatchArm> Arms,
        SourceSpan Span) : AstNode(Span);

    // (|> x (f a) (g b)) — pipe
    public sealed record Pipe(AstNode Initial, IReadOnlyList<AstNode> Steps, SourceSpan Span) : AstNode(Span);

    // (partial f arg1 arg2 ...)
    public sealed record Partial(AstNode Function, IReadOnlyList<AstNode> Args, SourceSpan Span) : AstNode(Span);

    // (try body) with (? expr) inside
    public sealed record Try(AstNode Body, SourceSpan Span) : AstNode(Span);

    // (? expr) — error propagation
    public sealed record Propagate(AstNode Expr, SourceSpan Span) : AstNode(Span);

    // (import-clr [alias Type/Method] ... Namespace ...)
    public sealed record ImportClr(
        IReadOnlyList<ClrImport> Imports,
        IReadOnlyList<string> Namespaces,
        SourceSpan Span) : AstNode(Span);

    // (new TypeName args...)
    public sealed record ClrNew(string TypeName, IReadOnlyList<AstNode> Args, SourceSpan Span) : AstNode(Span);

    // (raise expr) — throws a .NET exception
    public sealed record Raise(AstNode Expr, SourceSpan Span) : AstNode(Span);

    // (namespace name)
    public sealed record NamespaceDecl(string NsName, SourceSpan Span) : AstNode(Span);

    // (module name)
    public sealed record ModuleDecl(string ModuleName, SourceSpan Span) : AstNode(Span);

    // (import name)
    public sealed record Import(string ModuleName, SourceSpan Span) : AstNode(Span);

    // (export name1 name2 ...)
    public sealed record Export(IReadOnlyList<string> Names, SourceSpan Span) : AstNode(Span);

    // (catch expr) — catches .NET exceptions, returns Result<T, Error>
    public sealed record Catch(AstNode Body, SourceSpan Span) : AstNode(Span);

    // (list expr ...)
    public sealed record ListExpr(IReadOnlyList<AstNode> Elements, SourceSpan Span) : AstNode(Span);

    // (vector expr ...)
    public sealed record VectorExpr(IReadOnlyList<AstNode> Elements, SourceSpan Span) : AstNode(Span);

    // (map-of (k v) ...)
    public sealed record MapExpr(IReadOnlyList<(AstNode Key, AstNode Value)> Entries, SourceSpan Span) : AstNode(Span);

    // (object (IFoo IBar) (Method [params...] : RetType body) ...)
    public sealed record ObjectExpr(
        IReadOnlyList<string> InterfaceNames,
        IReadOnlyList<ObjectMethod> Methods,
        SourceSpan Span) : AstNode(Span);

    // (test-case "name" body ...)
    public sealed record TestCase(
        string TestName,
        IReadOnlyList<AstNode> Body,
        SourceSpan Span) : AstNode(Span);

    // A sequence of top-level forms
    public sealed record Program(IReadOnlyList<AstNode> TopLevelForms, SourceSpan Span) : AstNode(Span);
}

public sealed record ObjectMethod(
    string Name,
    IReadOnlyList<Param> Params,
    ZType? ReturnTypeAnnotation,
    AstNode Body,
    SourceSpan Span);

public sealed record AttributeDecl(
    string Name,
    IReadOnlyList<object> PositionalArgs,
    IReadOnlyList<(string Name, object Value)> NamedArgs,
    SourceSpan Span);

public sealed record Param(string Name, ZType? TypeAnnotation, SourceSpan Span,
    IReadOnlyList<AttributeDecl>? Attributes = null);

public sealed record FieldDecl(string Name, ZType TypeAnnotation, SourceSpan Span,
    IReadOnlyList<AttributeDecl>? Attributes = null);

public sealed record UnionCase(string Name, IReadOnlyList<FieldDecl> Fields, SourceSpan Span);

public sealed record MatchArm(Pattern Pattern, AstNode Body, SourceSpan Span);

public sealed record ClrImport(string Alias, string QualifiedName, SourceSpan Span);
