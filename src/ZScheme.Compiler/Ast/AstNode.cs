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

    /// <summary>A quoted symbol literal, e.g. <c>'some-symbol</c>. Backed at runtime by
    /// <c>ZScheme.Runtime.ZSymbol</c>.</summary>
    public sealed record SymbolLit(string Value, SourceSpan Span) : AstNode(Span);

    public sealed record UnitLit(SourceSpan Span) : AstNode(Span);

    public sealed record NullLit(SourceSpan Span) : AstNode(Span);

    // Names
    public sealed record Name(string Value, SourceSpan Span) : AstNode(Span)
    {
        /// <summary>
        ///     Populated by the type inferer when the name resolves to multiple
        ///     imported function definitions (overload set). The application
        ///     site is responsible for picking a candidate; using the bare name
        ///     outside a call is a diagnostic.
        /// </summary>
        public OverloadSet? OverloadCandidates { get; set; }

        /// <summary>
        ///     Populated by overload resolution after a candidate is selected.
        ///     Format: "moduleName/funcName" (e.g. "slist/cons"). Codegen routes
        ///     the call to the specified module's class instead of the default
        ///     (last-write-wins) bare-name lookup.
        /// </summary>
        public string? ResolvedQualifiedName { get; set; }

        /// <summary>
        ///     Populated by the type inferer when <see cref="Value" /> used the deprecated
        ///     <c>Type/member</c> accessor spelling and resolved to the modern
        ///     <c>Type-member</c> binding. Later passes consult this in preference to
        ///     <see cref="Value" /> so only the modern spelling reaches IR lowering.
        /// </summary>
        public string? ResolvedAccessorName { get; set; }
    }

    // (let [x expr] body) or (let [x : Type expr] body)
    // NameSpan points at the bound-name atom; default for desugared/synthesized lets
    // (multi-body wrappers), which unused-binding analysis skips.
    public sealed record Let(
        string VarName,
        AstNode Value,
        AstNode Body,
        SourceSpan Span,
        ZType? TypeAnnotation = null,
        SourceSpan NameSpan = default
    ) : AstNode(Span);

    // (letrec ([f expr] [g expr] ...) body) — a recursive binding group. Every name is in
    // scope in every binding's value and in the body, which is what makes local self- and
    // mutual recursion expressible; `let`/`let*` bind their value in the enclosing scope.
    // Initialization still runs left to right, so AstBuilder rejects a group where a
    // non-lambda value could read a binding that has not been assigned yet.
    public sealed record Letrec(
        IReadOnlyList<LetrecBinding> Bindings,
        AstNode Body,
        SourceSpan Span
    ) : AstNode(Span);

    // NameSpan points at the bound-name atom, so unused-binding analysis can underline it.
    // AllowsUnloopedRecursion carries the `#:recursive` marker of the nested `define` this
    // binding came from: the binding is lifted to a top-level function and looped on the same
    // rules, so it takes the same ZS0005 opt-out. See Define.
    public sealed record LetrecBinding(
        string Name,
        AstNode Value,
        ZType? TypeAnnotation = null,
        SourceSpan NameSpan = default,
        bool AllowsUnloopedRecursion = false
    );

    // (use ([x expr]) body) — binds a disposable resource, disposed when the body's scope exits.
    public sealed record Use(
        string VarName,
        AstNode Value,
        AstNode Body,
        SourceSpan Span,
        ZType? TypeAnnotation = null,
        SourceSpan NameSpan = default
    ) : AstNode(Span);

    // (if cond then else)
    public sealed record If(AstNode Condition, AstNode Then, AstNode Else, SourceSpan Span)
        : AstNode(Span);

    // (lambda (params...) body) or (lambda (params...) : ReturnType body)
    public sealed record Lambda(
        IReadOnlyList<Param> Params,
        ZType? ReturnTypeAnnotation,
        AstNode Body,
        SourceSpan Span
    ) : AstNode(Span);

    // Function application: (f arg1 arg2 ...)
    public sealed record Apply(AstNode Function, IReadOnlyList<AstNode> Args, SourceSpan Span)
        : AstNode(Span);

    // (values expr1 expr2 ...) — tuple construction
    public sealed record TupleNew(IReadOnlyList<AstNode> Elements, SourceSpan Span) : AstNode(Span);

    // (define (name [params...]) : ReturnType :where (^k notnull) body)
    // NameSpan, when non-empty, points at the bare function-name token so the LSP can
    // resolve hovers/go-to-definition precisely (the outer Span is single-line and
    // unreliable for multi-line forms).
    // AllowsUnloopedRecursion is set by the `#:recursive` marker: an assertion that this
    // definition's self-recursion is intended, silencing ZS0005. It never leaves the AST.
    public sealed record Define(
        string FnName,
        IReadOnlyList<Param> Params,
        ZType? ReturnTypeAnnotation,
        AstNode Body,
        SourceSpan Span,
        IReadOnlyList<AttributeDecl>? Attributes = null,
        IReadOnlyDictionary<string, GenericConstraintKind>? TypeParamConstraints = null,
        SourceSpan NameSpan = default,
        bool AllowsUnloopedRecursion = false
    ) : AstNode(Span);

    // (define name expr) — value binding
    public sealed record DefineValue(
        string VarName,
        AstNode Value,
        SourceSpan Span,
        IReadOnlyList<AttributeDecl>? Attributes = null,
        SourceSpan NameSpan = default
    ) : AstNode(Span);

    // (define-record Name [field : Type] ...) or (define-struct Name [field : Type] ...)
    // IsValueType distinguishes `record` (class) from `struct` (value type); every other aspect is identical.
    // NameSpan, when non-empty, points at the declaration-name atom (LSP rename/navigation).
    public sealed record RecordDecl(
        string RecordName,
        IReadOnlyList<string> TypeParams,
        IReadOnlyList<FieldDecl> Fields,
        SourceSpan Span,
        IReadOnlyList<AttributeDecl>? Attributes = null,
        IReadOnlyDictionary<string, GenericConstraintKind>? TypeParamConstraints = null,
        bool IsValueType = false,
        SourceSpan NameSpan = default
    ) : AstNode(Span);

    // (define-union Name (Case1 [field : Type]) ...)
    public sealed record UnionDecl(
        string UnionName,
        IReadOnlyList<string> TypeParams,
        IReadOnlyList<UnionCase> Cases,
        SourceSpan Span,
        IReadOnlyList<AttributeDecl>? Attributes = null,
        IReadOnlyDictionary<string, GenericConstraintKind>? TypeParamConstraints = null,
        SourceSpan NameSpan = default
    ) : AstNode(Span);

    // (match expr [pattern body] ...)
    public sealed record Match(AstNode Scrutinee, IReadOnlyList<MatchArm> Arms, SourceSpan Span)
        : AstNode(Span);

    // (partial f arg1 arg2 ...)
    public sealed record Partial(AstNode Function, IReadOnlyList<AstNode> Args, SourceSpan Span)
        : AstNode(Span);

    // (import-clr [alias Type/Method] ... Namespace ...)
    public sealed record ImportClr(
        IReadOnlyList<ClrImport> Imports,
        IReadOnlyList<string> Namespaces,
        SourceSpan Span
    ) : AstNode(Span);

    // (define-type-alias (Name ^a ^b ...) Fully.Qualified.Clr.OpenGenericType :from "AssemblyName")
    // (define-type-alias (Name ^a) :array)
    // Declares that the ZScheme type name `Name` (of the given arity) maps to the specified
    // CLR type at codegen. Type checking is unaffected — the alias only changes how the
    // named type is rendered to a CLR type.
    public sealed record TypeAliasDecl(
        string AliasName,
        IReadOnlyList<string> TypeParams,
        string ClrTarget,
        string? AssemblyHint,
        bool IsArray,
        SourceSpan NameSpan,
        SourceSpan Span
    ) : AstNode(Span);

    // (new TypeName args...) or (new (GenericType Arg1 Arg2) args...)
    public sealed record ClrNew(
        string TypeName,
        IReadOnlyList<ZType> TypeArgs,
        IReadOnlyList<AstNode> Args,
        SourceSpan Span
    ) : AstNode(Span);

    // (typeof TypeExpr) — produces a System.Type value (mirrors C# typeof)
    public sealed record TypeOf(ZType TypeArg, SourceSpan Span) : AstNode(Span);

    // (raise expr) — throws a .NET exception
    public sealed record Raise(AstNode Expr, SourceSpan Span) : AstNode(Span);

    // (define-async (name [params...]) : (Task ReturnType) :where (^k notnull) body)
    // AllowsUnloopedRecursion: see Define.
    public sealed record DefineAsync(
        string FnName,
        IReadOnlyList<Param> Params,
        ZType? ReturnTypeAnnotation,
        AstNode Body,
        SourceSpan Span,
        IReadOnlyList<AttributeDecl>? Attributes = null,
        IReadOnlyDictionary<string, GenericConstraintKind>? TypeParamConstraints = null,
        SourceSpan NameSpan = default,
        bool AllowsUnloopedRecursion = false
    ) : AstNode(Span);

    // (await expr) — awaits a Task
    public sealed record Await(AstNode Expr, SourceSpan Span) : AstNode(Span);

    // (namespace name)
    public sealed record NamespaceDecl(string NsName, SourceSpan Span) : AstNode(Span);

    // (module name body...)
    public sealed record ModuleDecl(string ModuleName, IReadOnlyList<AstNode> Body, SourceSpan Span)
        : AstNode(Span);

    // (import name)
    public sealed record Import(string ModuleName, SourceSpan Span) : AstNode(Span);

    // (export name1 name2 ...)
    public sealed record Export(IReadOnlyList<string> Names, SourceSpan Span) : AstNode(Span);

    // (with-handlers ([ExType var] handler) ... body)
    public sealed record WithHandlers(
        IReadOnlyList<HandlerClause> Handlers,
        AstNode Body,
        SourceSpan Span
    ) : AstNode(Span);

    // (with record-expr [field value] ...) — produce a copy of a record with updated fields
    public sealed record With(
        AstNode Record,
        IReadOnlyList<(string FieldName, AstNode Value)> Updates,
        SourceSpan Span
    ) : AstNode(Span);

    // (object (IFoo IBar) (Method [params...] : RetType body) ...)
    // (object : BaseClass IFoo (Method [params...] : RetType body) ...)
    // (object : BaseClass (constructor (super args...) ...) (Method ...) ...)
    public sealed record ObjectExpr(
        IReadOnlyList<string> InterfaceNames,
        IReadOnlyList<ObjectMethod> Methods,
        SourceSpan Span,
        string? BaseClassName = null,
        ConstructorDecl? Constructor = null
    ) : AstNode(Span);

    // (define-class Name [field : Type] ... (Method [params...] : RetType body) ...)
    // (define-class #:open Name ...) — open for subclassing
    // (define-class Name : BaseClass IFoo [field : Type] ... (Method ...) ...)
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
        IReadOnlyDictionary<string, GenericConstraintKind>? TypeParamConstraints = null,
        SourceSpan NameSpan = default
    ) : AstNode(Span);

    // (super/MethodName args...) — call base class method
    public sealed record SuperMethodCall(
        string MethodName,
        IReadOnlyList<AstNode> Args,
        SourceSpan Span
    ) : AstNode(Span);

    // (set! field-name expr) — mutate a mutable field in a method body
    public sealed record SetField(string FieldName, AstNode Value, SourceSpan Span) : AstNode(Span);

    // (define-interface Name (Method [params...] : RetType) ...)
    public sealed record InterfaceDecl(
        string InterfaceName,
        IReadOnlyList<string> TypeParams,
        IReadOnlyList<string> BaseInterfaceNames,
        IReadOnlyList<InterfaceMethodSignature> Methods,
        SourceSpan Span,
        IReadOnlyList<AttributeDecl>? Attributes = null,
        IReadOnlyDictionary<string, GenericConstraintKind>? TypeParamConstraints = null,
        SourceSpan NameSpan = default
    ) : AstNode(Span);

    // A sequence of top-level forms
    public sealed record Program(IReadOnlyList<AstNode> TopLevelForms, SourceSpan Span)
        : AstNode(Span);
}

// AllowsUnloopedRecursion / NameSpan: see Define. A method of a sealed class is a loop
// candidate in its own right (TailCallLowering rewrites its tail self-calls), so it takes
// the same `#:recursive` opt-out and the same name anchor for the diagnostic.
public sealed record ObjectMethod(
    string Name,
    IReadOnlyList<Param> Params,
    ZType? ReturnTypeAnnotation,
    AstNode Body,
    SourceSpan Span,
    IReadOnlyList<AttributeDecl>? Attributes = null,
    bool IsAsync = false,
    bool AllowsUnloopedRecursion = false,
    SourceSpan NameSpan = default
);

public sealed record InterfaceMethodSignature(
    string Name,
    IReadOnlyList<Param> Params,
    ZType ReturnTypeAnnotation,
    SourceSpan Span
);

// (constructor [params...] (super args...) (set! field expr) ...)
public sealed record ConstructorDecl(
    IReadOnlyList<Param> Params,
    IReadOnlyList<AstNode>? SuperArgs,
    IReadOnlyList<(string FieldName, AstNode Value)> FieldSets,
    IReadOnlyList<AstNode> BodyExprs,
    SourceSpan Span
);

/// <summary>Represents a bare symbol reference in an attribute argument (emitted unquoted, e.g. enum values).</summary>
public sealed record SymbolRef(string Name);

public sealed record AttributeDecl(
    string Name,
    IReadOnlyList<object> PositionalArgs,
    IReadOnlyList<(string Name, object Value)> NamedArgs,
    SourceSpan Span
);

// NameSpan, when non-empty, points at the bare parameter-name atom; Span covers the
// whole [name : Type] bracket for annotated parameters, so precise consumers (rename,
// occurrence matching) must prefer NameSpan.
public sealed record Param(
    string Name,
    ZType? TypeAnnotation,
    SourceSpan Span,
    IReadOnlyList<AttributeDecl>? Attributes = null,
    bool IsVariadic = false,
    SourceSpan NameSpan = default
)
{
    /// <summary>
    ///     The inferred parameter type, populated during type inference. Mutable so the
    ///     same <see cref="Param" /> instance carries its inferred type back to the LSP for
    ///     hover, without subclassing <see cref="AstNode" /> (which would conflict with the
    ///     nested <c>AstNode.Name</c> record).
    /// </summary>
    public ZType? ResolvedType { get; set; }
}

public sealed record FieldDecl(
    string Name,
    ZType TypeAnnotation,
    SourceSpan Span,
    IReadOnlyList<AttributeDecl>? Attributes = null,
    bool IsMutable = false,
    bool IsInit = false
);

// NameSpan, when non-empty, points at the case-name atom (Span covers the whole case form).
public sealed record UnionCase(
    string Name,
    IReadOnlyList<FieldDecl> Fields,
    SourceSpan Span,
    SourceSpan NameSpan = default
);

public sealed record MatchArm(Pattern Pattern, AstNode Body, SourceSpan Span)
{
    /// <summary>
    ///     The arm's pattern. Settable because type inference resolves a bare atom that names a
    ///     nullary union case into the constructor pattern it was meant to be — see
    ///     <c>TypeInferer.ResolveBareCasePatterns</c>. Nothing else rewrites it.
    /// </summary>
    public Pattern Pattern { get; set; } = Pattern;
}

public sealed record HandlerClause(
    string ExceptionTypeName,
    string BindingVarName,
    AstNode HandlerBody,
    SourceSpan Span
);

public enum ClrImportKind
{
    Static,
    Instance,
    InstanceProperty,
    InstancePropertySet,
    InstanceIndexer,
    InstanceIndexerSet,
    InstancePropertyInit,
}

// AliasSpan, when non-empty, points at the bare alias atom so the LSP can resolve
// hovers/go-to-definition precisely (Span covers the whole [alias Type/Method] bracket).
public sealed record ClrImport(
    string Alias,
    string QualifiedName,
    IReadOnlyList<string> TypeParams,
    SourceSpan Span,
    ClrImportKind Kind = ClrImportKind.Static,
    ZType? TypeAnnotation = null,
    IReadOnlyDictionary<string, GenericConstraintKind>? TypeParamConstraints = null,
    string? AssemblyHint = null,
    SourceSpan AliasSpan = default
);
