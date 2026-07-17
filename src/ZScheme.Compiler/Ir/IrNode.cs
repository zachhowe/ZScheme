using System.Reflection;
using ZScheme.Compiler.Codegen;
using ZScheme.Compiler.Diagnostics;
using ZScheme.Compiler.Types;

namespace ZScheme.Compiler.Ir;

public abstract record IrNode
{
    public ZType Type { get; init; } = ZType.Unit;
    public SourceSpan Span { get; init; } = SourceSpan.None;

    // Literals
    public sealed record IntConst(int Value) : IrNode;

    public sealed record FloatConst(float Value) : IrNode;

    public sealed record BoolConst(bool Value) : IrNode;

    public sealed record StringConst(string Value) : IrNode;

    /// <summary>A quoted symbol literal; lowered to a <c>ZScheme.Runtime.ZSymbol.Intern</c> call.</summary>
    public sealed record SymbolConst(string Name) : IrNode;

    public sealed record UnitConst : IrNode;

    public sealed record NullConst : IrNode;

    // Variable reference
    public sealed record Var(string Name) : IrNode
    {
        /// <summary>
        ///     When set, codegen routes the lookup to this specific module's class
        ///     (qualifying past the default bare-name resolution). Populated by
        ///     IR lowering for names whose call site was overload-resolved.
        /// </summary>
        public string? ModuleName { get; init; }

        /// <summary>
        ///     When set, this named-function reference is being passed to a CLR
        ///     parameter of the given delegate type. The C# emitter wraps it in an
        ///     adapter lambda cast to this delegate type so the correct overload is
        ///     selected and the function value is coerced into the delegate.
        /// </summary>
        public string? ClrDelegateTypeName { get; init; }

        /// <summary>
        ///     The final emitted identifier this reference resolves to, assigned by
        ///     <see cref="EmitNameResolver" />. When non-null both backends use it
        ///     verbatim (after C# keyword <c>@</c>-escaping) instead of re-sanitizing
        ///     <see cref="Name" />; when null they fall back to sanitizing the raw
        ///     name (synthetic/unresolved references). Lets two source names that
        ///     would sanitize to the same identifier resolve to distinct members.
        /// </summary>
        public string? EmitName { get; init; }
    }

    // Let binding
    public sealed record Let(
        string VarName,
        IrNode Value,
        IrNode Body,
        ZType? VarType = null,
        // Final emitted local/field name, assigned by EmitNameResolver (null => sanitize).
        string? EmitName = null
    ) : IrNode;

    // Use binding — like Let, but the resource is disposed (IDisposable.Dispose) when the
    // body's scope exits, normally or via exception. Emitted as a C# `using` declaration or
    // an IL try/finally. Acts as a try barrier (like WithHandlers) for stack/TCO purposes.
    public sealed record Use(string VarName, IrNode Value, IrNode Body, ZType? VarType = null)
        : IrNode;

    // If expression
    public sealed record If(IrNode Condition, IrNode Then, IrNode Else) : IrNode;

    // Function call
    public sealed record Call(IrNode Function, IReadOnlyList<IrNode> Args) : IrNode;

    // Binary operation (lowered from built-in operator calls)
    public sealed record BinOp(string Op, IrNode Left, IrNode Right) : IrNode;

    // Unary operation
    public sealed record UnaryOp(string Op, IrNode Operand) : IrNode;

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
        IReadOnlyDictionary<string, GenericConstraintKind>? TypeParamConstraints = null,
        string? ClrDelegateTypeName = null,
        // Final emitted method name, assigned by EmitNameResolver (null => emitter
        // sanitizes Name). Disambiguates module-level definitions that would
        // otherwise sanitize to the same identifier.
        string? EmitName = null
    ) : IrNode
    {
        /// <summary>
        ///     Set by TailCallLowering when this function's tail self-calls have been
        ///     rewritten into <see cref="TcoJump" /> nodes. Both backends then emit the
        ///     body as a loop (C# <c>while(true)</c>, IL a branch back to a start label)
        ///     instead of a self-recursive method.
        /// </summary>
        public bool IsTcoLoop { get; init; }
    }

    // Closure (after lambda lifting)
    public sealed record Closure(string LiftedFuncName, IReadOnlyList<IrNode> CapturedValues)
        : IrNode;

    // Record construction
    public sealed record RecordNew(
        string TypeName,
        IReadOnlyList<(string FieldName, IrNode Value)> Fields
    ) : IrNode;

    // Tuple construction
    public sealed record TupleNew(IReadOnlyList<IrNode> Elements) : IrNode;

    // Record field access
    public sealed record FieldGet(IrNode Record, string FieldName) : IrNode;

    // Record copy-with-updates ((with r [field value] ...))
    public sealed record RecordWith(
        string TypeName,
        IrNode Record,
        IReadOnlyList<(string FieldName, IrNode Value)> Updates
    ) : IrNode;

    // Union case construction
    public sealed record UnionCaseNew(string UnionName, string CaseName, IReadOnlyList<IrNode> Args)
        : IrNode;

    // Pattern match (before compilation to decision tree)
    public sealed record Match(IrNode Scrutinee, IReadOnlyList<IrMatchArm> Arms) : IrNode;

    // Sequence of IR nodes (multiple top-level forms)
    public sealed record Seq(IReadOnlyList<IrNode> Nodes) : IrNode;

    // Record type declaration (for codegen). IsValueType = true means emit a .NET struct.
    public sealed record RecordDecl(
        string Name,
        IReadOnlyList<string> TypeParams,
        IReadOnlyList<IrField> Fields,
        IReadOnlyList<IrAttribute>? Attributes = null,
        IReadOnlyDictionary<string, GenericConstraintKind>? TypeParamConstraints = null,
        bool IsValueType = false,
        // Final emitted type name, assigned by EmitNameResolver when this type's
        // sanitized name collided with another type in the module (null => emitters
        // sanitize Name). References resolve via the raw Name, so only the declaration
        // honors this.
        string? EmitName = null
    ) : IrNode;

    // Type alias declaration. Carries the same data as the AST node and is collected into
    // the compilation-wide TypeAliasRegistry during IR collection. Generates no code.
    public sealed record TypeAliasDecl(
        string Name,
        IReadOnlyList<string> TypeParams,
        string ClrTarget,
        string? AssemblyHint,
        bool IsArray
    ) : IrNode;

    // Union type declaration (for codegen)
    public sealed record UnionDecl(
        string Name,
        IReadOnlyList<string> TypeParams,
        IReadOnlyList<IrUnionCase> Cases,
        IReadOnlyList<IrAttribute>? Attributes = null,
        IReadOnlyDictionary<string, GenericConstraintKind>? TypeParamConstraints = null,
        // Final emitted name of the union base type, assigned by EmitNameResolver on a
        // collision (null => emitters sanitize Name). Each case carries its own EmitName.
        string? EmitName = null
    ) : IrNode;

    // Mutable array construction (for varargs packing)
    public sealed record MutableArrayNew(ZType ElementType, IReadOnlyList<IrNode> Elements)
        : IrNode;

    // CLR constructor call (from new special form)
    public sealed record ClrNew(
        string QualifiedTypeName,
        IReadOnlyList<ZType> TypeArgs,
        IReadOnlyList<IrNode> Args
    ) : IrNode;

    // typeof(T) — produces a System.Type value
    public sealed record TypeOf(ZType TypeArg) : IrNode;

    // CLR method call (from import-clr)
    public sealed record ClrCall(
        string QualifiedTypeName,
        string MethodName,
        IReadOnlyList<IrNode> Args,
        int GenericArity = 0,
        IReadOnlyList<ZType>? GenericTypeArgs = null,
        IReadOnlyList<ClrInterop.OutParamInfo>? OutParams = null,
        MethodInfo? ResolvedMethodInfo = null
    ) : IrNode;

    // TCO back-edge: reassign the enclosing loop's parameters to NewArgs and jump
    // back to the top. Produced by TailCallLowering from a tail self-call; consumed
    // by both backends (C# `continue`, IL `Br` to the start label).
    public sealed record TcoJump(IReadOnlyList<string> ParamNames, IReadOnlyList<IrNode> NewArgs)
        : IrNode;

    // Throw a .NET exception (from raise special form)
    public sealed record Throw(IrNode Expr) : IrNode;

    // try { body } catch (ExType1 var1) { handler1 } catch (ExType2 var2) { handler2 } ...
    public sealed record WithHandlers(IrNode Body, IReadOnlyList<IrHandlerClause> Handlers)
        : IrNode;

    // Await a Task expression
    public sealed record Await(IrNode Expr) : IrNode;

    // Anonymous object implementing .NET interfaces, optionally inheriting from a base class
    public sealed record ObjectExpr(
        IReadOnlyList<string> InterfaceNames,
        IReadOnlyList<IrObjectMethod> Methods,
        string? BaseClassName = null,
        IrConstructor? Constructor = null
    ) : IrNode;

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
        IReadOnlyDictionary<string, GenericConstraintKind>? TypeParamConstraints = null,
        // Final emitted type name, assigned by EmitNameResolver on a collision
        // (null => emitters sanitize Name).
        string? EmitName = null,
        // True for synthesized classes lifted from `(object ...)` expressions.
        // Unlike a `define-class`, an object body does not bring its base class's
        // inherited fields into bare-name scope (see TypeInferer.InferObjectExpr),
        // so a bare reference colliding with an inherited field name resolves to a
        // module-level function, not the field. The C# emitter uses this to avoid
        // shadowing such references with `this.<Field>`.
        bool IsObjectLifted = false
    ) : IrNode;

    // super/MethodName call (for codegen)
    public sealed record SuperMethodCall(string MethodName, IReadOnlyList<IrNode> Args) : IrNode;

    // (set! field-name expr) — mutate a mutable field
    public sealed record SetField(string FieldName, IrNode Value) : IrNode;

    // Interface declaration (for codegen)
    public sealed record InterfaceDecl(
        string Name,
        IReadOnlyList<string> TypeParams,
        IReadOnlyList<string> BaseInterfaceNames,
        IReadOnlyList<IrInterfaceMethodSignature> Methods,
        IReadOnlyList<IrAttribute>? Attributes = null,
        IReadOnlyDictionary<string, GenericConstraintKind>? TypeParamConstraints = null,
        // Final emitted type name, assigned by EmitNameResolver on a collision
        // (null => emitters sanitize Name).
        string? EmitName = null
    ) : IrNode;

    // Collection method call (list/head, vector/map, map/get, etc.)
    public sealed record MethodCall(
        IrNode Receiver,
        string MethodName,
        IReadOnlyList<IrNode> Args,
        bool IsProperty,
        bool IsIndexer,
        bool IsPropertySet = false,
        bool IsIndexerSet = false,
        bool IsPropertyInit = false,
        IReadOnlyList<ClrInterop.OutParamInfo>? OutParams = null,
        // Instance-method overload resolved during IR lowering (CLR receivers only;
        // null for user-defined-type receivers, properties, indexers, and generics).
        MethodInfo? ResolvedMethodInfo = null
    ) : IrNode;
}

public sealed record IrObjectMethod(
    string Name,
    IReadOnlyList<IrParam> Params,
    ZType ReturnType,
    IrNode Body,
    IReadOnlyList<IrAttribute>? Attributes = null,
    bool IsAsync = false
);

public sealed record IrInterfaceMethodSignature(
    string Name,
    IReadOnlyList<IrParam> Params,
    ZType ReturnType
);

public sealed record IrConstructor(
    IReadOnlyList<IrParam> Params,
    IReadOnlyList<IrNode>? SuperArgs,
    IReadOnlyList<(string FieldName, IrNode Value)> FieldSets,
    IReadOnlyList<IrNode> BodyExprs
);

public sealed record IrAttribute(
    string Name,
    IReadOnlyList<object> PositionalArgs,
    IReadOnlyList<(string Name, object Value)> NamedArgs
);

public sealed record IrParam(
    string Name,
    ZType Type,
    IReadOnlyList<IrAttribute>? Attributes = null,
    bool IsVariadic = false
);

public sealed record IrField(
    string Name,
    ZType Type,
    IReadOnlyList<IrAttribute>? Attributes = null,
    bool IsMutable = false,
    bool IsInit = false
);

// EmitName: final emitted name of this case's type, assigned by EmitNameResolver on a
// collision (null => emitters sanitize Name).
public sealed record IrUnionCase(
    string Name,
    IReadOnlyList<IrField> Fields,
    string? EmitName = null
);

public sealed record IrMatchArm(IrPattern Pattern, IrNode Body);

public sealed record IrHandlerClause(
    string ExceptionTypeName,
    string BindingVarName,
    IrNode HandlerBody
);

public abstract record IrPattern
{
    public sealed record Wildcard : IrPattern;

    public sealed record Variable(string Name) : IrPattern;

    public sealed record Literal(object Value) : IrPattern;

    public sealed record Constructor(string Name, IReadOnlyList<IrPattern> Fields) : IrPattern
    {
        /// <summary>
        ///     The owning union's name, resolved by <see cref="PatternResolver" /> against the
        ///     scrutinee type and the union registry. Null before resolution (and for a
        ///     constructor pattern whose case could not be resolved).
        /// </summary>
        public string? ResolvedUnion { get; init; }

        /// <summary>
        ///     The concrete <see cref="Types.ZType" /> each field sub-pattern matches against,
        ///     after substituting the scrutinee's type arguments — positionally aligned with
        ///     <see cref="Fields" />. An element is null when that field's type could not be
        ///     resolved. Null (the whole list) before resolution.
        /// </summary>
        public IReadOnlyList<Types.ZType?>? FieldTypes { get; init; }
    }

    public sealed record Tuple(IReadOnlyList<IrPattern> Elements) : IrPattern;

    /// <summary>
    ///     The variable names this pattern binds, in left-to-right order, descending
    ///     through constructor field and tuple element sub-patterns. Both backends scope
    ///     a match arm's bindings to that arm and need this list to do so.
    /// </summary>
    public List<string> BoundNames()
    {
        var names = new List<string>();
        Collect(this, names);
        return names;

        static void Collect(IrPattern pattern, List<string> names)
        {
            switch (pattern)
            {
                case Variable v:
                    names.Add(v.Name);
                    break;
                case Constructor c:
                    foreach (var f in c.Fields)
                        Collect(f, names);
                    break;
                case Tuple t:
                    foreach (var e in t.Elements)
                        Collect(e, names);
                    break;
            }
        }
    }
}
