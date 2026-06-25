using ZScheme.Compiler.Builtins;

namespace ZScheme.Compiler.Types;

public sealed record BuiltinCtorInfo(string RuntimeType, string? CaseName);

public sealed class TypeEnv(TypeEnv? parent = null)
{
    private readonly Dictionary<string, ZType> _bindings = new();
    private readonly Dictionary<string, BuiltinCtorInfo> _builtinCtors = new();
    private readonly Dictionary<string, OverloadSet> _overloads = new();
    private readonly HashSet<string> _continuationNames = new();

    public static TypeEnv CreateRoot()
    {
        var env = new TypeEnv();

        // Compiler-native built-in functions (arithmetic/comparison/boolean operators and
        // numeric/string/symbol conversions) are defined once in BuiltinRegistry; register
        // their type signatures here.
        foreach (var builtin in BuiltinRegistry.All)
            env.Define(builtin.Name, builtin.Signature);

        // The 6 collection conversion functions (vector->immutable-vector, vector->mutable-vector,
        // mutable-list->list, list->mutable-list, mutable-hash->hash, hash-copy) live in
        // stdlib (see packages/stdlib/src/{vector,list,hash,mutable/{vector,list,hash}}.zs).

        return env;
    }

    public TypeEnv CreateChild()
    {
        return new TypeEnv(this);
    }

    public void Define(string name, ZType type)
    {
        _bindings[name] = type;
        // Re-defining a name in the same scope clears any prior continuation-marker —
        // the new binding is a regular value unless explicitly marked.
        _continuationNames.Remove(name);
    }

    /// <summary>
    /// Define a name and mark it as a *continuation parameter* — the value bound to
    /// <paramref name="name"/> is a continuation captured by call/cc / shift / control /
    /// call/comp. Multi-arg call sites against this binding are auto-bundled into a
    /// single tuple argument by <see cref="TypeInferer.InferApply"/>.
    /// </summary>
    public void DefineContinuation(string name, ZType type)
    {
        _bindings[name] = type;
        _continuationNames.Add(name);
    }

    /// <summary>
    ///     Removes a single binding from this scope. Returns <c>true</c> if one was removed.
    ///     The top-level define path uses this to drop the signature pre-pass's monomorphic
    ///     placeholder before generalization, for the same reason
    ///     <see cref="RemoveOverloadCandidate" /> exists: a placeholder still bound here would
    ///     leave its type variables free in the environment, and <c>Generalize</c> subtracts
    ///     those — which would silently make every annotated generic function monomorphic.
    /// </summary>
    public bool RemoveBinding(string name)
    {
        return _bindings.Remove(name);
    }

    /// <summary>
    ///     Injects an imported binding from <paramref name="moduleName" />. Function-typed
    ///     bindings join an overload set keyed by the bare name so multiple modules can
    ///     export functions with the same name; non-function bindings use the legacy
    ///     single-binding behavior.
    /// </summary>
    public void DefineImportedBinding(string moduleName, string name, ZType type)
    {
        var stripped = type is ZType.ZForAllType fa ? fa.Body : type;
        if (stripped is ZType.ZFuncType)
        {
            DefineOverload(name, $"{moduleName}/{name}", type);
            return;
        }

        Define(name, type);
    }

    /// <summary>
    ///     Adds a candidate to the overload set for <paramref name="name" />. If a single
    ///     non-overloaded binding already exists for the name in this scope, it is folded
    ///     into the overload set (using its qualified name as the existing candidate's key).
    /// </summary>
    public void DefineOverload(string name, string qualifiedName, ZType type)
    {
        if (!_overloads.TryGetValue(name, out var set))
        {
            set = new OverloadSet();
            _overloads[name] = set;
        }

        set.Add(new OverloadCandidate(qualifiedName, type));
    }

    /// <summary>
    ///     Registers an overload candidate, replacing the existing entry with the same qualified
    ///     name if one exists. Used when a local <c>define</c> is registered as an overload twice
    ///     during inference: first with the placeholder <c>selfType</c> (pre-body, to support
    ///     recursive calls when the gate fires) and then with the generalized type post-body.
    /// </summary>
    public void DefineOrReplaceOverload(string name, string qualifiedName, ZType type)
    {
        if (!_overloads.TryGetValue(name, out var set))
        {
            set = new OverloadSet();
            _overloads[name] = set;
        }

        set.AddOrReplace(new OverloadCandidate(qualifiedName, type));
    }

    /// <summary>
    ///     Removes a single overload candidate by qualified name. Returns <c>true</c> if a
    ///     candidate was removed. The local-define inference path uses this to drop the
    ///     pre-body <c>selfType</c> placeholder before generalization, so its free type
    ///     variables are not counted against generalization (which would prevent the function
    ///     from being polymorphic).
    /// </summary>
    public bool RemoveOverloadCandidate(string name, string qualifiedName)
    {
        if (!_overloads.TryGetValue(name, out var set))
            return false;
        var idx = set.Candidates.FindIndex(c => c.QualifiedName == qualifiedName);
        if (idx < 0)
            return false;
        set.Candidates.RemoveAt(idx);
        return true;
    }

    public OverloadSet? LookupOverloads(string name)
    {
        if (_overloads.TryGetValue(name, out var set))
            return set;
        return parent?.LookupOverloads(name);
    }

    public ZType? Lookup(string name)
    {
        if (_bindings.TryGetValue(name, out var type))
            return type;
        return parent?.Lookup(name);
    }

    public bool Contains(string name)
    {
        return Lookup(name) is not null || LookupOverloads(name) is not null;
    }

    /// <summary>
    /// Returns true if the innermost binding for <paramref name="name"/> was introduced
    /// via <see cref="DefineContinuation"/> (or transitively rebound from such a binding).
    /// Walks the parent chain following the lexical-shadowing rule: a normal
    /// <see cref="Define"/> in an inner scope masks any outer continuation marker.
    /// </summary>
    public bool IsContinuation(string name)
    {
        if (_bindings.ContainsKey(name))
            return _continuationNames.Contains(name);
        return parent?.IsContinuation(name) ?? false;
    }

    public void DefineBuiltinCtor(string name, BuiltinCtorInfo info)
    {
        _builtinCtors[name] = info;
    }

    public BuiltinCtorInfo? LookupBuiltinCtor(string name)
    {
        if (_builtinCtors.TryGetValue(name, out var info))
            return info;
        return parent?.LookupBuiltinCtor(name);
    }

    public IEnumerable<ZType> AllBoundTypes()
    {
        foreach (var t in _bindings.Values)
            yield return t;
        foreach (var set in _overloads.Values)
        foreach (var c in set.Candidates)
            yield return c.Type;
        if (parent is not null)
            foreach (var t in parent.AllBoundTypes())
                yield return t;
    }
}
