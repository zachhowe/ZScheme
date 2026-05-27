namespace ZScheme.Fuzzer.Generation;

public enum UserFuncKind
{
    Regular,
    Recursive,
    HigherOrder,
    Generic
}

// ParamTypes describes each param's "shape" at the call site: typically ExprType.Int
// (a ground-type arg), but ExprType.IntFn for function-typed params. For generic
// functions, IsGenericParam[i] is true at positions typed `^a` — those positions
// may receive any type from AllowedGrounds, and the call-site return is then
// reduced back to Int by the caller.
// ReturnIsGeneric distinguishes `(id : ^a -> ^a)` (return varies with instantiation)
// from `(apply : (^a -> Int) ^a -> Int)` (return is always Int regardless).
public sealed record UserFunc(
    string Name,
    UserFuncKind Kind,
    List<ExprType> ParamTypes,
    string Definition,
    IReadOnlySet<ExprType> AllowedGrounds,
    IReadOnlyList<bool> IsGenericParam,
    bool ReturnIsGeneric,
    bool IsAsync = false,
    ExprType ReturnType = ExprType.Int,
    // When true, the LAST entry in ParamTypes is variadic — call sites pass 0-N
    // args of that element type instead of exactly one. The element type today
    // is always Int.
    bool IsVariadic = false);
