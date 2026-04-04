using ZScheme.Compiler.Diagnostics;
using ZScheme.Compiler.Types;

namespace ZScheme.Compiler.Ast;

public abstract record AstNode(SourceSpan Span)
{
    public ZType? ResolvedType { get; set; }

    // Literals
    public sealed record IntLit(int Value, SourceSpan Span) : AstNode(Span);

    public sealed record FloatLit(float Value, SourceSpan Span) : AstNode(Span);

    public sealed record BoolLit(bool Value, SourceSpan Span) : AstNode(Span);

    public sealed record StringLit(string Value, SourceSpan Span) : AstNode(Span);

    public sealed record UnitLit(SourceSpan Span) : AstNode(Span);

    public sealed record NullLit(SourceSpan Span) : AstNode(Span);

    // Names
    public sealed record Name(string Value, SourceSpan Span) : AstNode(Span);

    // (let [x expr] body) or (let [x : Type expr] body)
    public sealed record Let(string VarName, AstNode Value, AstNode Body, SourceSpan Span, ZType? TypeAnnotation = null)
        : AstNode(Span);

    // (if cond then else)
    public sealed record If(AstNode Condition, AstNode Then, AstNode Else, SourceSpan Span) : AstNode(Span);

    // (fn [params...] body) — lambda
    public sealed record Lambda(IReadOnlyList<Param> Params, AstNode Body, SourceSpan Span) : AstNode(Span);

    // Function application: (f arg1 arg2 ...)
    public sealed record Apply(AstNode Function, IReadOnlyList<AstNode> Args, SourceSpan Span) : AstNode(Span);

    // (define (name [params...]) : ReturnType :where (^k notnull) body)
    public sealed record Define(
        string FnName,
        IReadOnlyList<Param> Params,
        ZType? ReturnTypeAnnotation,
        AstNode Body,
        SourceSpan Span,
        IReadOnlyList<AttributeDecl>? Attributes = null,
        IReadOnlyDictionary<string, GenericConstraintKind>? TypeParamConstraints = null) : AstNode(Span);

    // (define name expr) — value binding
    public sealed record DefineValue(
        string VarName,
        AstNode Value,
        SourceSpan Span,
        IReadOnlyList<AttributeDecl>? Attributes = null) : AstNode(Span);

    // (record Name [field : Type] ...)
    public sealed record RecordDecl(
        string RecordName,
        IReadOnlyList<string> TypeParams,
        IReadOnlyList<FieldDecl> Fields,
        SourceSpan Span,
        IReadOnlyList<AttributeDecl>? Attributes = null,
        IReadOnlyDictionary<string, GenericConstraintKind>? TypeParamConstraints = null) : AstNode(Span);

    // (union Name (Case1 [field : Type]) ...)
    public sealed record UnionDecl(
        string UnionName,
        IReadOnlyList<string> TypeParams,
        IReadOnlyList<UnionCase> Cases,
        SourceSpan Span,
        IReadOnlyList<AttributeDecl>? Attributes = null,
        IReadOnlyDictionary<string, GenericConstraintKind>? TypeParamConstraints = null) : AstNode(Span);

    // (match expr [pattern body] ...)
    public sealed record Match(
        AstNode Scrutinee,
        IReadOnlyList<MatchArm> Arms,
        SourceSpan Span) : AstNode(Span);

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

    // (new TypeName args...) or (new (GenericType Arg1 Arg2) args...)
    public sealed record ClrNew(
        string TypeName,
        IReadOnlyList<ZType> TypeArgs,
        IReadOnlyList<AstNode> Args,
        SourceSpan Span) : AstNode(Span);

    // (raise expr) — throws a .NET exception
    public sealed record Raise(AstNode Expr, SourceSpan Span) : AstNode(Span);

    // (define-async (name [params...]) : (Task ReturnType) :where (^k notnull) body)
    public sealed record DefineAsync(
        string FnName,
        IReadOnlyList<Param> Params,
        ZType? ReturnTypeAnnotation,
        AstNode Body,
        SourceSpan Span,
        IReadOnlyList<AttributeDecl>? Attributes = null,
        IReadOnlyDictionary<string, GenericConstraintKind>? TypeParamConstraints = null) : AstNode(Span);

    // (await expr) — awaits a Task
    public sealed record Await(AstNode Expr, SourceSpan Span) : AstNode(Span);

    // (namespace name)
    public sealed record NamespaceDecl(string NsName, SourceSpan Span) : AstNode(Span);

    // (module name body...)
    public sealed record ModuleDecl(string ModuleName, IReadOnlyList<AstNode> Body, SourceSpan Span) : AstNode(Span);

    // (import name)
    public sealed record Import(string ModuleName, SourceSpan Span) : AstNode(Span);

    // (export name1 name2 ...)
    public sealed record Export(IReadOnlyList<string> Names, SourceSpan Span) : AstNode(Span);

    // (catch expr) — catches .NET exceptions, returns Result<T, Error>
    public sealed record Catch(AstNode Body, SourceSpan Span) : AstNode(Span);

    // (with-handlers ([ExType var] handler) ... body)
    public sealed record WithHandlers(
        IReadOnlyList<HandlerClause> Handlers,
        AstNode Body,
        SourceSpan Span) : AstNode(Span);

    // (object (IFoo IBar) (Method [params...] : RetType body) ...)
    // (object : BaseClass IFoo (Method [params...] : RetType body) ...)
    // (object : BaseClass (constructor (super args...) ...) (Method ...) ...)
    public sealed record ObjectExpr(
        IReadOnlyList<string> InterfaceNames,
        IReadOnlyList<ObjectMethod> Methods,
        SourceSpan Span,
        string? BaseClassName = null,
        ConstructorDecl? Constructor = null) : AstNode(Span);

    // (class Name [field : Type] ... (Method [params...] : RetType body) ...)
    // (class :open Name ...) — open for subclassing
    // (class Name : BaseClass IFoo [field : Type] ... (Method ...) ...)
    public sealed record ClassDecl(
        string ClassName,
        IReadOnlyList<string> TypeParams,
        IReadOnlyList<string> InterfaceNames,
        IReadOnlyList<FieldDecl> Fields,
        IReadOnlyList<ObjectMethod> Methods,
        SourceSpan Span,
        bool IsOpen = false,
        string? BaseClassName = null,
        ConstructorDecl? Constructor = null,
        IReadOnlyList<AttributeDecl>? Attributes = null,
        IReadOnlyDictionary<string, GenericConstraintKind>? TypeParamConstraints = null) : AstNode(Span);

    // (super/MethodName args...) — call base class method
    public sealed record SuperMethodCall(
        string MethodName,
        IReadOnlyList<AstNode> Args,
        SourceSpan Span) : AstNode(Span);

    // (set! field-name expr) — mutate a mutable field in a method body
    public sealed record SetField(
        string FieldName,
        AstNode Value,
        SourceSpan Span) : AstNode(Span);

    // (interface Name (Method [params...] : RetType) ...)
    public sealed record InterfaceDecl(
        string InterfaceName,
        IReadOnlyList<string> TypeParams,
        IReadOnlyList<string> BaseInterfaceNames,
        IReadOnlyList<InterfaceMethodSignature> Methods,
        SourceSpan Span,
        IReadOnlyList<AttributeDecl>? Attributes = null,
        IReadOnlyDictionary<string, GenericConstraintKind>? TypeParamConstraints = null) : AstNode(Span);

    // A sequence of top-level forms
    public sealed record Program(IReadOnlyList<AstNode> TopLevelForms, SourceSpan Span) : AstNode(Span);
}

public sealed record ObjectMethod(
    string Name,
    IReadOnlyList<Param> Params,
    ZType? ReturnTypeAnnotation,
    AstNode Body,
    SourceSpan Span,
    IReadOnlyList<AttributeDecl>? Attributes = null,
    bool IsAsync = false);

public sealed record InterfaceMethodSignature(
    string Name,
    IReadOnlyList<Param> Params,
    ZType ReturnTypeAnnotation,
    SourceSpan Span);

// (constructor [params...] (super args...) (set! field expr) ...)
public sealed record ConstructorDecl(
    IReadOnlyList<Param> Params,
    IReadOnlyList<AstNode>? SuperArgs,
    IReadOnlyList<(string FieldName, AstNode Value)> FieldSets,
    IReadOnlyList<AstNode> BodyExprs,
    SourceSpan Span);

public sealed record AttributeDecl(
    string Name,
    IReadOnlyList<object> PositionalArgs,
    IReadOnlyList<(string Name, object Value)> NamedArgs,
    SourceSpan Span);

public sealed record Param(
    string Name,
    ZType? TypeAnnotation,
    SourceSpan Span,
    IReadOnlyList<AttributeDecl>? Attributes = null,
    bool IsVariadic = false);

public sealed record FieldDecl(
    string Name,
    ZType TypeAnnotation,
    SourceSpan Span,
    IReadOnlyList<AttributeDecl>? Attributes = null,
    bool IsMutable = false,
    bool IsInit = false);

public sealed record UnionCase(string Name, IReadOnlyList<FieldDecl> Fields, SourceSpan Span);

public sealed record MatchArm(Pattern Pattern, AstNode Body, SourceSpan Span);

public sealed record HandlerClause(
    string ExceptionTypeName,
    string BindingVarName,
    AstNode HandlerBody,
    SourceSpan Span);

public enum ClrImportKind
{
    Static,
    Instance,
    InstanceProperty,
    InstancePropertySet,
    InstanceIndexer,
    InstanceIndexerSet,
    InstancePropertyInit
}

public sealed record ClrImport(
    string Alias,
    string QualifiedName,
    IReadOnlyList<string> TypeParams,
    SourceSpan Span,
    ClrImportKind Kind = ClrImportKind.Static,
    ZType? TypeAnnotation = null,
    IReadOnlyDictionary<string, GenericConstraintKind>? TypeParamConstraints = null);
