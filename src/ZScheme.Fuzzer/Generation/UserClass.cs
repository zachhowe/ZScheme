namespace ZScheme.Fuzzer.Generation;

public sealed record UserClassDecl(
    string Name,
    IReadOnlyList<UserClassField> Fields,
    IReadOnlyList<ExprType> ConstructorParamTypes,
    IReadOnlyList<UserClassMethod> Methods,
    bool IsOpen,
    string? BaseName,
    IReadOnlyList<string> ImplementedInterfaces,
    string Definition,
    bool HasExplicitCtor = false,
    IReadOnlyList<UserClassMethod>? BaseChainMethods = null
)
{
    /// <summary>
    ///     Every method callable on an instance: this class's declared methods plus,
    ///     for a derived class, the base chain's effective set (distinct by name — an
    ///     override reuses the inherited name, so the declared entry wins). Derived
    ///     classes list the full set so override picking can reach a method the direct
    ///     base never overrode (a two-level vtable insertion) and the instance-call
    ///     alias emitter can target an inherited member, which the backends must
    ///     resolve against its declaring type.
    /// </summary>
    public IReadOnlyList<UserClassMethod> EffectiveMethods => BaseChainMethods ?? Methods;
}

public sealed record UserClassField(string Name, bool IsMutable, ExprType Type = ExprType.Int);

// All current methods take Int params and return Int — keeps call sites and
// override compatibility trivial. RetType is kept explicit so future generators
// can introduce other return types without touching call sites. IsAsync marks
// methods emitted as `(define-async ... : (Task Int) ...)`; ConstructAndCallToInt
// and EmitInstanceImportClrBlock skip those (sync-context call sites can't await).
public sealed record UserClassMethod(
    string Name,
    IReadOnlyList<ExprType> ParamTypes,
    ExprType RetType,
    bool IsAsync = false
);
