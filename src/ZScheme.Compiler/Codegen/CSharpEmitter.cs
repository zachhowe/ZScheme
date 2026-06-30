using System.Globalization;
using System.Text;
using Serilog;
using ZScheme.Compiler.Ast;
using ZScheme.Compiler.Diagnostics;
using ZScheme.Compiler.Ir;
using ZScheme.Compiler.Types;

namespace ZScheme.Compiler.Codegen;

public sealed partial class CSharpEmitter(
    DiagnosticBag diagnostics,
    string ns,
    string className,
    IReadOnlyList<string>? clrUsings = null,
    IReadOnlyList<(string ClassName, IReadOnlyList<IrNode> Definitions)>? importedModules = null,
    IReadOnlyDictionary<string, string>? precompiledModuleMap = null,
    bool isModule = false,
    bool suppressVersionPreamble = false,
    TypeAliasRegistry? typeAliases = null,
    IReadOnlyDictionary<string, string>? precompiledModuleNamespaces = null,
    // rawTypeName -> emitted name for renamed types exported by precompiled modules this
    // compilation consumes, loaded from their metadata. Seeds _typeEmitNames so references
    // to a renamed precompiled type resolve to the name baked into the DLL.
    IReadOnlyDictionary<string, string>? precompiledTypeRenames = null
)
{
    private static readonly ILogger Log = Serilog.Log.ForContext<CSharpEmitter>();

    private static readonly HashSet<string> CSharpKeywords =
    [
        "abstract",
        "as",
        "base",
        "bool",
        "break",
        "byte",
        "case",
        "catch",
        "char",
        "checked",
        "class",
        "const",
        "continue",
        "decimal",
        "default",
        "delegate",
        "do",
        "double",
        "else",
        "enum",
        "event",
        "explicit",
        "extern",
        "false",
        "finally",
        "fixed",
        "float",
        "for",
        "foreach",
        "goto",
        "if",
        "implicit",
        "in",
        "int",
        "interface",
        "internal",
        "is",
        "lock",
        "long",
        "namespace",
        "new",
        "null",
        "object",
        "operator",
        "out",
        "override",
        "params",
        "private",
        "protected",
        "public",
        "readonly",
        "ref",
        "return",
        "sbyte",
        "sealed",
        "short",
        "sizeof",
        "stackalloc",
        "static",
        "string",
        "struct",
        "switch",
        "this",
        "throw",
        "true",
        "try",
        "typeof",
        "uint",
        "ulong",
        "unchecked",
        "unsafe",
        "ushort",
        "using",
        "virtual",
        "void",
        "volatile",
        "while",
    ];

    // Maps case name -> owning union name, used to look up an entry in
    // _unionCaseFieldTypes when only the case name is known (e.g., from a
    // pattern) and the scrutinee type is a bare type variable.
    private readonly Dictionary<string, string> _caseToUnion = BuildCaseToUnion(importedModules);

    private readonly HashSet<string> _currentModuleNames = [];
    private readonly Dictionary<string, EmittedClassInfo> _emittedClassInfos = new();

    private readonly Dictionary<string, string> _funcToModuleClass = BuildFuncToModuleMap(
        importedModules,
        precompiledModuleMap
    );

    // Maps function name -> (declared C# generic param names, polymorphic ZFuncType
    // with the same type variables that occur in the inferred signature). Populated
    // from imported-module FuncDefs at construction and from the current module in
    // CollectModuleNames. EmitCall uses this to instantiate generic calls explicitly
    // (`F<T1, T2>(args)`) so Roslyn doesn't trip on CS0411 when method-type-argument
    // inference can't see through the surrounding lambda/delegate cast.
    private readonly Dictionary<
        string,
        (IReadOnlyList<string> TypeParams, ZType.ZFuncType FuncType)
    > _genericFuncs = BuildGenericFuncs(importedModules);

    private readonly HashSet<string> _localBindings = [];

    // Match-arm pattern bindings that had to be renamed because they shadow an
    // enclosing local (a C# switch-expression pattern variable that collides with
    // an in-scope local is rejected with CS0136). Maps the original ZScheme name to
    // a fresh C# identifier; only *renamed* (colliding) bindings appear here, so
    // non-shadowing pattern variables resolve exactly as before. Scoped per arm by
    // EmitMatch's save/restore.
    private readonly Dictionary<string, string> _patternRenames = new();

    // Names bound by any *enclosing* match arm (renamed or not), used purely to
    // detect collisions for nested matches that rebind the same name.
    private readonly HashSet<string> _boundPatternVars = [];

    // Monotonic counter for generating fresh pattern-binding identifiers.
    private int _matchBindCounter;

    // Names of declared record types (single-case structs/records). A constructor
    // pattern `Name(p1, ..., pN)` against one of these is irrefutable when every
    // sub-pattern is irrefutable, since there is only one case to match. Populated
    // from imported modules at construction and current-module RecordDecl nodes
    // during emission. Used to suppress redundant `_ =>` fallbacks in switch
    // expressions, which Roslyn rejects with CS8510.
    private readonly HashSet<string> _recordTypeNames = BuildRecordTypeNames(importedModules);
    private readonly StringBuilder _sb = new();
    private readonly TypeAliasRegistry _typeAliases = typeAliases ?? new TypeAliasRegistry();

    // Maps user type name -> (ordered type-param names, constraint dict). Used to
    // pick a default substitution for free type variables that satisfies the
    // declared constraint (e.g., `unmanaged`/`struct` cannot be `object`).
    private readonly Dictionary<
        string,
        (
            IReadOnlyList<string> TypeParams,
            IReadOnlyDictionary<string, GenericConstraintKind> Constraints
        )
    > _typeParamConstraints = BuildTypeParamConstraints(importedModules);

    private readonly Dictionary<string, string> _typeToModuleClass = BuildTypeToModuleMap(
        importedModules,
        precompiledModuleMap
    );

    // Maps a user type's raw source name -> the disambiguated identifier it is emitted
    // under, for types whose sanitized name collided (see EmitNameResolver). Seeded from
    // precompiled modules' persisted type renames and source-imported modules' stamped
    // EmitName, then extended with the current module's renames by
    // RegisterCurrentTypeEmitNames at the start of Emit. QualifyType (references) and
    // SanitizeType (declarations) consult it; an absent entry => plain sanitization.
    private readonly Dictionary<string, string> _typeEmitNames = BuildTypeEmitNames(
        importedModules,
        precompiledTypeRenames
    );

    // Maps "<union>.<case>" -> (define-union type params, field types) so nested pattern
    // matches can recover each field's scrutinee ZType after substituting the
    // outer type arguments. Populated from imported modules at construction time
    // and from current-module UnionDecl nodes during emission.
    private readonly Dictionary<
        string,
        (IReadOnlyList<string> TypeParams, IReadOnlyList<ZType> FieldTypes)
    > _unionCaseFieldTypes = BuildUnionCaseFieldTypes(importedModules);

    private HashSet<string>? _currentClassFields;
    private HashSet<string>? _currentClassMethods;
    private Dictionary<int, string>? _currentFuncTypeVarMap;
    private HashSet<string>? _currentTypeParams;
    private int _indent;

    /// <summary>
    ///     True when the emitted module defines a top-level <c>main</c>, which Roslyn discovers
    ///     as the program entry point. Mirrors <see cref="IlEmitter.HasEntryPoint" /> and drives
    ///     <see cref="Pipeline.CompilationResult.CSharpOutputResult.IsExecutable" />.
    /// </summary>
    public bool HasEntryPoint { get; private set; }

    private static Dictionary<string, string> BuildFuncToModuleMap(
        IReadOnlyList<(string ClassName, IReadOnlyList<IrNode> Definitions)>? modules,
        IReadOnlyDictionary<string, string>? precompiledMap = null
    )
    {
        var map = new Dictionary<string, string>();

        // Add precompiled module mappings first
        if (precompiledMap is not null)
            foreach (var (name, moduleClass) in precompiledMap)
                map[name] = moduleClass;

        if (modules is null)
            return map;
        foreach (var (moduleClassName, defs) in modules)
        foreach (var def in defs)
        {
            var name = def switch
            {
                IrNode.FuncDef f => f.Name,
                IrNode.Let l => l.VarName,
                _ => null,
            };
            if (name is not null)
                map[name] = moduleClassName;
        }

        return map;
    }

    private static Dictionary<
        string,
        (IReadOnlyList<string> TypeParams, IReadOnlyList<ZType> FieldTypes)
    > BuildUnionCaseFieldTypes(
        IReadOnlyList<(string ClassName, IReadOnlyList<IrNode> Definitions)>? modules
    )
    {
        var map = new Dictionary<string, (IReadOnlyList<string>, IReadOnlyList<ZType>)>();
        if (modules is null)
            return map;
        foreach (var (_, defs) in modules)
        foreach (var def in defs)
            if (def is IrNode.UnionDecl union)
                foreach (var c in union.Cases)
                    map[$"{union.Name}.{c.Name}"] = (
                        union.TypeParams,
                        c.Fields.Select(f => f.Type).ToList()
                    );
        return map;
    }

    private static Dictionary<
        string,
        (IReadOnlyList<string>, IReadOnlyDictionary<string, GenericConstraintKind>)
    > BuildTypeParamConstraints(
        IReadOnlyList<(string ClassName, IReadOnlyList<IrNode> Definitions)>? modules
    )
    {
        var map =
            new Dictionary<
                string,
                (IReadOnlyList<string>, IReadOnlyDictionary<string, GenericConstraintKind>)
            >();
        if (modules is null)
            return map;
        foreach (var (_, defs) in modules)
        foreach (var def in defs)
            switch (def)
            {
                case IrNode.UnionDecl u when u.TypeParamConstraints is { Count: > 0 }:
                    map[u.Name] = (u.TypeParams, u.TypeParamConstraints);
                    break;
                case IrNode.RecordDecl r when r.TypeParamConstraints is { Count: > 0 }:
                    map[r.Name] = (r.TypeParams, r.TypeParamConstraints);
                    break;
                case IrNode.ClassDecl c when c.TypeParamConstraints is { Count: > 0 }:
                    map[c.Name] = (c.TypeParams, c.TypeParamConstraints);
                    break;
                case IrNode.InterfaceDecl i when i.TypeParamConstraints is { Count: > 0 }:
                    map[i.Name] = (i.TypeParams, i.TypeParamConstraints);
                    break;
            }

        return map;
    }

    private static HashSet<string> BuildRecordTypeNames(
        IReadOnlyList<(string ClassName, IReadOnlyList<IrNode> Definitions)>? modules
    )
    {
        var names = new HashSet<string>();
        if (modules is null)
            return names;
        foreach (var (_, defs) in modules)
        foreach (var def in defs)
            if (def is IrNode.RecordDecl rec)
                names.Add(rec.Name);
        return names;
    }

    private static Dictionary<string, string> BuildCaseToUnion(
        IReadOnlyList<(string ClassName, IReadOnlyList<IrNode> Definitions)>? modules
    )
    {
        var map = new Dictionary<string, string>();
        if (modules is null)
            return map;
        foreach (var (_, defs) in modules)
        foreach (var def in defs)
            if (def is IrNode.UnionDecl union)
                foreach (var c in union.Cases)
                    map[c.Name] = union.Name;
        return map;
    }

    private static Dictionary<string, string> BuildTypeToModuleMap(
        IReadOnlyList<(string ClassName, IReadOnlyList<IrNode> Definitions)>? modules,
        IReadOnlyDictionary<string, string>? precompiledMap = null
    )
    {
        var map = new Dictionary<string, string>();

        // Add precompiled module mappings first
        if (precompiledMap is not null)
            foreach (var (name, moduleClass) in precompiledMap)
                map[name] = moduleClass;

        if (modules is null)
            return map;
        foreach (var (moduleClassName, defs) in modules)
        foreach (var def in defs)
            switch (def)
            {
                case IrNode.RecordDecl rec:
                    map[rec.Name] = moduleClassName;
                    break;
                case IrNode.UnionDecl union:
                    map[union.Name] = moduleClassName;
                    foreach (var c in union.Cases)
                        map[c.Name] = moduleClassName;
                    break;
                case IrNode.ClassDecl cls:
                    map[cls.Name] = moduleClassName;
                    break;
                case IrNode.InterfaceDecl iface:
                    map[iface.Name] = moduleClassName;
                    break;
            }

        return map;
    }

    // Seeds _typeEmitNames (raw type name -> emitted name) from precompiled-module type
    // renames and the stamped EmitName on source-imported type declarations. Only renamed
    // types appear; an absent entry means QualifyType/SanitizeType sanitize the raw name.
    private static Dictionary<string, string> BuildTypeEmitNames(
        IReadOnlyList<(string ClassName, IReadOnlyList<IrNode> Definitions)>? modules,
        IReadOnlyDictionary<string, string>? precompiledTypeRenames
    )
    {
        var map = new Dictionary<string, string>();

        if (precompiledTypeRenames is not null)
            foreach (var (name, emitted) in precompiledTypeRenames)
                map[name] = emitted;

        if (modules is null)
            return map;
        foreach (var (_, defs) in modules)
        foreach (var def in defs)
            switch (def)
            {
                case IrNode.RecordDecl { EmitName: { } e } r:
                    map[r.Name] = e;
                    break;
                case IrNode.ClassDecl { EmitName: { } e } c:
                    map[c.Name] = e;
                    break;
                case IrNode.InterfaceDecl { EmitName: { } e } i:
                    map[i.Name] = e;
                    break;
                case IrNode.UnionDecl u:
                    if (u.EmitName is { } ue)
                        map[u.Name] = ue;
                    foreach (var uc in u.Cases)
                        if (uc.EmitName is { } ce)
                            map[uc.Name] = ce;
                    break;
            }

        return map;
    }

    // Extends _typeEmitNames with the current module's renamed types before any emission,
    // so both type declarations and references resolve to the stamped name. Walks Seq/Let
    // chains to mirror EmitNameResolver's top-level collection.
    private void RegisterCurrentTypeEmitNames(IrNode node)
    {
        switch (node)
        {
            case IrNode.Seq seq:
                foreach (var child in seq.Nodes)
                    RegisterCurrentTypeEmitNames(child);
                break;
            case IrNode.Let let:
                RegisterCurrentTypeEmitNames(let.Body);
                break;
            case IrNode.RecordDecl { EmitName: { } e } r:
                _typeEmitNames[r.Name] = e;
                break;
            case IrNode.ClassDecl { EmitName: { } e } c:
                _typeEmitNames[c.Name] = e;
                break;
            case IrNode.InterfaceDecl { EmitName: { } e } i:
                _typeEmitNames[i.Name] = e;
                break;
            case IrNode.UnionDecl u:
                if (u.EmitName is { } ue)
                    _typeEmitNames[u.Name] = ue;
                foreach (var uc in u.Cases)
                    if (uc.EmitName is { } ce)
                        _typeEmitNames[uc.Name] = ce;
                break;
        }
    }

    private bool HasProgramContent(IrNode node)
    {
        var nodes = node is IrNode.Seq seq ? seq.Nodes : [node];
        foreach (var child in nodes)
            switch (child)
            {
                case IrNode.FuncDef:
                case IrNode.Let:
                case IrNode.Call:
                case IrNode.ClrCall:
                case IrNode.Throw:
                case IrNode.Await:
                    return true;
                case IrNode.RecordDecl:
                case IrNode.UnionDecl:
                case IrNode.ClassDecl:
                case IrNode.InterfaceDecl:
                    if (isModule)
                        return true;
                    break;
            }

        return false;
    }

    private void CollectModuleNames(IrNode node)
    {
        switch (node)
        {
            case IrNode.Seq seq:
            {
                foreach (var child in seq.Nodes)
                    switch (child)
                    {
                        case IrNode.FuncDef func:
                            _currentModuleNames.Add(func.Name);
                            RegisterGenericFunc(func);
                            break;
                        case IrNode.Let let:
                            _currentModuleNames.Add(let.VarName);
                            // EmitTopLevel turns a nested top-level let's body into further
                            // static fields, so recurse to register those binding names too.
                            CollectModuleNames(let.Body);
                            break;
                        case IrNode.RecordDecl rec:
                            _recordTypeNames.Add(rec.Name);
                            break;
                    }

                break;
            }
            case IrNode.FuncDef func:
                _currentModuleNames.Add(func.Name);
                RegisterGenericFunc(func);
                break;
            case IrNode.Let let:
                _currentModuleNames.Add(let.VarName);
                CollectModuleNames(let.Body);
                break;
        }
    }

    private void RegisterGenericFunc(IrNode.FuncDef func)
    {
        if (func.TypeParams is { Count: > 0 } tps && func.Type is ZType.ZFuncType ft)
        {
            _genericFuncs[func.Name] = (tps, ft);
            // Also key by the current module's qualified name. Same-module calls
            // can be overload-resolved (Var carries this module's ModuleName), in
            // which case TryLookupGenericFunc consults only the qualified entry —
            // mirroring how imported modules are registered in BuildGenericFuncs.
            _genericFuncs[$"{className}.{func.Name}"] = (tps, ft);
        }
    }

    private static Dictionary<
        string,
        (IReadOnlyList<string> TypeParams, ZType.ZFuncType FuncType)
    > BuildGenericFuncs(
        IReadOnlyList<(string ClassName, IReadOnlyList<IrNode> Definitions)>? modules
    )
    {
        var map = new Dictionary<string, (IReadOnlyList<string>, ZType.ZFuncType)>();
        if (modules is null)
            return map;
        foreach (var (className, defs) in modules)
        foreach (var def in defs)
            if (
                def is IrNode.FuncDef { TypeParams: { Count: > 0 } tps } f
                && f.Type is ZType.ZFuncType ft
            )
            {
                map[f.Name] = (tps, ft);
                // Also key by "ClassName.FuncName" so overload-resolved call sites
                // (which know the originating module) can fetch the correct entry
                // even when another imported module exports a function with the
                // same bare name and overwrites the bare-key entry above.
                map[$"{className}.{f.Name}"] = (tps, ft);
            }

        return map;
    }

    private bool TryLookupGenericFunc(
        IrNode.Var v,
        out (IReadOnlyList<string> TypeParams, ZType.ZFuncType FuncType) info
    )
    {
        if (v.ModuleName is not null)
        {
            // Overload-resolved to a specific module: consult ONLY the qualified
            // entry. Falling back to the bare-name entry here is unsound — when
            // two imported modules export the same name (e.g. the generic list
            // `empty?` and the non-generic string `empty?`), the bare key holds
            // whichever was registered last, so a non-generic call would be
            // emitted with the other module's type arguments (CS0308).
            var className = NameConverter.ClassNameFromModuleName(v.ModuleName);
            return _genericFuncs.TryGetValue($"{className}.{v.Name}", out info);
        }

        return _genericFuncs.TryGetValue(v.Name, out info);
    }

    private string BuildOutParamArgList(
        IReadOnlyList<IrNode> visibleArgs,
        IReadOnlyList<ClrInterop.OutParamInfo> outParams
    )
    {
        // Reconstruct the full argument list by interleaving visible args and out params
        // at their original positions
        var outParamSet = outParams.ToDictionary(op => op.OriginalIndex);
        var totalParams = visibleArgs.Count + outParams.Count;
        var argStrings = new string[totalParams];
        var visibleIdx = 0;
        var outIdx = 0;
        for (var i = 0; i < totalParams; i++)
            if (outParamSet.ContainsKey(i))
                argStrings[i] = $"out __out{outIdx++}";
            else
                argStrings[i] = EmitExpr(visibleArgs[visibleIdx++]);

        return string.Join(", ", argStrings);
    }

    private string ResolveConstructorName(string ctorName, ZType? scrutineeType)
    {
        var qualified = QualifyType(ctorName);
        // For generic union types, append type arguments to the case name
        if (scrutineeType is ZType.ZNamedType { TypeArgs.Count: > 0 } nt)
            return $"{qualified}<{string.Join(", ", FormatTypeArgs(GetUnionForCase(ctorName) ?? nt.Name, nt.TypeArgs))}>";
        return qualified;
    }

    private string? GetUnionForCase(string caseName)
    {
        return _caseToUnion.TryGetValue(caseName, out var u) ? u : null;
    }

    // Render a generic type's args, substituting `int` for any free type
    // variable that would otherwise emit as `object`. A free var means no
    // concrete value flows at that position, so the substitution does not
    // change runtime behaviour — but `int` satisfies every constraint we
    // emit (`unmanaged`, `struct`, `notnull`), whereas `object` violates
    // value-type constraints both on the declaring type itself and on any
    // downstream callsite that consumes a pattern variable typed as the
    // free param (e.g. `f<T> where T : unmanaged` applied to a payload
    // bound from a union case whose declaring param is free).
    private IEnumerable<string> FormatTypeArgs(string typeName, IReadOnlyList<ZType> args)
    {
        return args.Select(arg => IsFreeTypeVar(arg) ? "int" : TypeToCs(arg));
    }

    private bool IsFreeTypeVar(ZType t)
    {
        return t switch
        {
            ZType.ZTypeVar tv => _currentFuncTypeVarMap is null
                || !_currentFuncTypeVarMap.ContainsKey(tv.Id),
            ZType.ZNamedType nt when IsUnresolvedTypeVariable(nt.Name) => true,
            _ => false,
        };
    }

    // Mirror of the `FormatTypeArgs` defaulting, but operating on a `ZType`
    // rather than emission strings. Replaces every free `ZTypeVar` (top-level
    // or nested) with `Int` so downstream `TypeToCs` calls produce `int`
    // instead of `object`. Used where a free param's emission must agree with
    // a sibling emission that already went through `FormatTypeArgs`.
    private ZType DefaultFreeTypeVars(ZType t)
    {
        return t switch
        {
            ZType.ZTypeVar when IsFreeTypeVar(t) => ZType.Int,
            ZType.ZNamedType nt when IsUnresolvedTypeVariable(nt.Name) => ZType.Int,
            ZType.ZNamedType nt => new ZType.ZNamedType(
                nt.Name,
                nt.TypeArgs.Select(DefaultFreeTypeVars).ToList()
            ),
            ZType.ZFuncType ft => new ZType.ZFuncType(
                ft.Params.Select(DefaultFreeTypeVars).ToList(),
                DefaultFreeTypeVars(ft.Return),
                ft.IsVariadic
            ),
            ZType.ZNullableType { Inner: var inner } => new ZType.ZNullableType(
                DefaultFreeTypeVars(inner)
            ),
            _ => t,
        };
    }

    private static bool ContainsAwait(IrNode node)
    {
        return node switch
        {
            IrNode.Await => true,
            IrNode.Let let => ContainsAwait(let.Value) || ContainsAwait(let.Body),
            IrNode.Use use => ContainsAwait(use.Value) || ContainsAwait(use.Body),
            IrNode.If @if => ContainsAwait(@if.Condition)
                || ContainsAwait(@if.Then)
                || ContainsAwait(@if.Else),
            IrNode.Match match => ContainsAwait(match.Scrutinee)
                || match.Arms.Any(a => ContainsAwait(a.Body)),
            IrNode.Call call => ContainsAwait(call.Function) || call.Args.Any(ContainsAwait),
            IrNode.WithHandlers wh => ContainsAwait(wh.Body)
                || wh.Handlers.Any(h => ContainsAwait(h.HandlerBody)),
            IrNode.Throw th => ContainsAwait(th.Expr),
            IrNode.Seq seq => seq.Nodes.Any(ContainsAwait),
            _ => false,
        };
    }

    private static bool HasLetSpineShadowing(IrNode body, IReadOnlyList<IrParam> funcParams)
    {
        var seen = new HashSet<string>(funcParams.Select(p => p.Name));
        var current = body;
        while (true)
        {
            // 'use' has the same binding/spine shape as 'let' for shadowing purposes.
            if (current is IrNode.Let let)
            {
                if (!seen.Add(let.VarName))
                    return true;
                current = let.Body;
            }
            else if (current is IrNode.Use use)
            {
                if (!seen.Add(use.VarName))
                    return true;
                current = use.Body;
            }
            else
            {
                break;
            }
        }

        return false;
    }

    /// True when emitting <paramref name="node"/> as a single C# expression would
    /// produce an immediately-invoked lambda (IIFE) at its top level — i.e. a
    /// <c>let</c> (which <see cref="EmitLetExpr"/> wraps in <c>((Func&lt;…&gt;)(…))()</c>),
    /// a <c>with-handlers</c> (which <see cref="EmitWithHandlers"/> wraps in a
    /// try/catch IIFE), or an <c>if</c> whose taken branch would itself need one.
    /// Such nodes, when they appear in statement position (a function/method body
    /// or a statement-form <c>if</c> branch), are flattened into plain locals,
    /// <c>if/else</c> blocks, and bare <c>try/catch</c> statements instead of an
    /// IIFE. Anything else emits cleanly as an expression already.
    private static bool WantsStatementForm(IrNode node) =>
        node switch
        {
            IrNode.Let => true,
            IrNode.Use => true,
            IrNode.WithHandlers => true,
            IrNode.If i => WantsStatementForm(i.Then) || WantsStatementForm(i.Else),
            _ => false,
        };

    private List<IrField> GetEmittedInheritedFields(string? baseClassName)
    {
        var result = new List<IrField>();
        if (
            baseClassName is not null
            && _emittedClassInfos.TryGetValue(baseClassName, out var info)
        )
        {
            result.AddRange(GetEmittedInheritedFields(info.BaseClassName));
            result.AddRange(info.Fields);
        }

        return result;
    }

    private HashSet<string> GetEmittedInheritedMethodNames(string? baseClassName)
    {
        var result = new HashSet<string>();
        if (
            baseClassName is not null
            && _emittedClassInfos.TryGetValue(baseClassName, out var info)
        )
        {
            foreach (var m in GetEmittedInheritedMethodNames(info.BaseClassName))
                result.Add(m);
            foreach (var m in info.MethodNames)
                result.Add(m);
        }

        return result;
    }

    private static string FormatAttribute(IrAttribute attr)
    {
        var args = attr.PositionalArgs.Select(FormatAttributeValue).ToList();
        foreach (var (name, value) in attr.NamedArgs)
            args.Add($"{name} = {FormatAttributeValue(value)}");
        return args.Count > 0 ? $"[{attr.Name}({string.Join(", ", args)})]" : $"[{attr.Name}]";
    }

    private static string FormatAttributeValue(object value)
    {
        return value switch
        {
            SymbolRef sym => sym.Name,
            string s => $"\"{EscapeString(s)}\"",
            bool b => b ? "true" : "false",
            int i => i.ToString(),
            float f => $"{f.ToString(CultureInfo.InvariantCulture)}f",
            _ => value.ToString() ?? "",
        };
    }

    private static string FormatFieldAttributes(IReadOnlyList<IrAttribute>? attrs)
    {
        if (attrs is null or { Count: 0 })
            return "";
        var parts = attrs.Select(a => FormatAttribute(a).Insert(1, "property: "));
        return string.Join(" ", parts) + " ";
    }

    private static string FormatWhereConstraints(
        IReadOnlyDictionary<string, GenericConstraintKind>? constraints
    )
    {
        if (constraints is not { Count: > 0 })
            return "";
        var sb = new StringBuilder();
        foreach (var (param, kind) in constraints)
        {
            var parts = new List<string>();
            if (kind.HasFlag(GenericConstraintKind.Class))
                parts.Add("class");
            if (kind.HasFlag(GenericConstraintKind.Struct))
                parts.Add("struct");
            if (kind.HasFlag(GenericConstraintKind.Unmanaged))
                parts.Add("unmanaged");
            if (kind.HasFlag(GenericConstraintKind.NotNull))
                parts.Add("notnull");
            // `default` is only valid on override / explicit-interface-implementation methods
            // in C# (CS8823). Absence of constraints already denotes "either ref or value type",
            // so we skip emitting it on non-override declarations — which is every site we emit
            // a where-clause from (static functions, records, unions, classes, interfaces).
            if (kind.HasFlag(GenericConstraintKind.New))
                parts.Add("new()");
            if (parts.Count > 0)
                sb.Append($" where {param} : {string.Join(", ", parts)}");
        }

        return sb.ToString();
    }

    private string FormatParam(IrParam p)
    {
        var prefix = "";
        if (p.Attributes is { Count: > 0 })
            prefix = string.Join(" ", p.Attributes.Select(FormatAttribute)) + " ";
        if (p.IsVariadic)
            prefix += "params ";
        return $"{prefix}{TypeToCs(p.Type)} {SanitizeParam(p.Name)}";
    }

    private string ReturnTypeToCs(ZType type)
    {
        return type == ZType.Unit ? "void" : TypeToCs(type);
    }

    private static ZType LetVarType(IrNode.Let let)
    {
        return let.VarType ?? let.Value.Type;
    }

    private string TypeToCs(ZType type)
    {
        // Default any free type variable (one not bound by an enclosing generic
        // function's type-param map) to `int` before dispatching. Without this,
        // the special-cased named-type patterns below (e.g. `Concurrent-Dictionary`,
        // `List`, `Map`) recurse into `TypeToCs` directly and a free `ZTypeVar`
        // arg falls through to the `object` fallback — producing emissions like
        // `ConcurrentDictionary<int, object>` whose generic param disagrees with
        // a sibling argument expression that already went through `FormatTypeArgs`
        // and picked `int`. The mismatch lands as a Roslyn type-conversion error.
        // The generic fallback at `ZNamedType { TypeArgs.Count: > 0 }` calls
        // `FormatTypeArgs`, which applies the same defaulting; doing it here
        // unifies the special-cased and generic paths.
        type = DefaultFreeTypeVars(type);
        return type switch
        {
            ZType.ZDelegateType dt => dt.ClrTypeName,
            ZType.ZPrimitiveType { Kind: PrimitiveKind.Int } => "int",
            ZType.ZPrimitiveType { Kind: PrimitiveKind.Long } => "long",
            ZType.ZPrimitiveType { Kind: PrimitiveKind.Float } => "float",
            ZType.ZPrimitiveType { Kind: PrimitiveKind.Double } => "double",
            ZType.ZPrimitiveType { Kind: PrimitiveKind.Byte } => "byte",
            ZType.ZPrimitiveType { Kind: PrimitiveKind.Char } => "char",
            ZType.ZPrimitiveType { Kind: PrimitiveKind.Bool } => "bool",
            ZType.ZPrimitiveType { Kind: PrimitiveKind.String } => "string",
            ZType.ZPrimitiveType { Kind: PrimitiveKind.Unit } => "System.ValueTuple",
            ZType.ZFuncType
            {
                Return: ZType.ZPrimitiveType { Kind: PrimitiveKind.Unit },
                Params.Count: 0
            } => "System.Action",
            ZType.ZFuncType { Return: ZType.ZPrimitiveType { Kind: PrimitiveKind.Unit } } ft =>
                $"System.Action<{string.Join(", ", ft.Params.Select(TypeToCs))}>",
            ZType.ZFuncType ft =>
                $"System.Func<{string.Join(", ", ft.Params.Select(TypeToCs).Append(TypeToCs(ft.Return)))}>",
            ZType.ZNamedType { TypeArgs: [] } task when _typeAliases.IsTaskName(task.Name) =>
                "System.Threading.Tasks.Task",
            ZType.ZNamedType { TypeArgs: [var taskT] } task2
                when _typeAliases.IsTaskName(task2.Name) =>
                $"System.Threading.Tasks.Task<{TypeToCs(taskT)}>",
            ZType.ZNamedType { TypeArgs.Count: > 0 } vt
                when _typeAliases.IsValueTupleName(vt.Name) =>
                $"({string.Join(", ", vt.TypeArgs.Select(TypeToCs))})",
            ZType.ZNamedType nt
                when _typeAliases.TryGet(nt.Name, out var alias) && alias is not null =>
                ApplyAliasCs(alias, nt),
            ZType.ZNamedType { TypeArgs.Count: > 0 } nt =>
                $"{QualifyType(nt.Name)}<{string.Join(", ", FormatTypeArgs(nt.Name, nt.TypeArgs))}>",
            ZType.ZNamedType nt when IsUnresolvedTypeVariable(nt.Name) => WarnAndReturn(
                $"Unresolved type variable '{nt.Name}' from annotation, using 'object'",
                "object"
            ),
            ZType.ZNamedType nt => QualifyType(nt.Name),
            ZType.ZNullableType { Inner: var inner } => $"{TypeToCs(inner)}?",
            ZType.ZTypeVar tv
                when _currentFuncTypeVarMap is not null
                    && _currentFuncTypeVarMap.TryGetValue(tv.Id, out var tpName) => tpName,
            ZType.ZTypeVar => WarnAndReturn(
                "Unresolved type variable in C# emission, using 'object'",
                "object"
            ),
            _ => WarnAndReturn(
                $"Unmapped type in C# emission: {type.GetType().Name}, using 'object'",
                "object"
            ),
        };
    }

    private string ApplyAliasCs(TypeAliasInfo alias, ZType.ZNamedType nt)
    {
        if (nt.TypeArgs.Count != alias.TypeParams.Count)
            return WarnAndReturn(
                $"Type alias '{alias.Name}' expects {alias.TypeParams.Count} type arguments, got {nt.TypeArgs.Count}",
                "object",
                alias.Span
            );
        if (alias.Kind == TypeAliasKind.SzArray)
            return $"{TypeToCs(nt.TypeArgs[0])}[]";
        if (nt.TypeArgs.Count == 0)
            return alias.ClrTarget;
        var args = string.Join(", ", nt.TypeArgs.Select(TypeToCs));
        return $"{alias.ClrTarget}<{args}>";
    }

    private string QualifyType(string name)
    {
        // A renamed type (collision-disambiguated by EmitNameResolver) is emitted under its
        // stamped name; otherwise the raw name is sanitized as usual.
        var emitted = _typeEmitNames.TryGetValue(name, out var e)
            ? (CSharpKeywords.Contains(e) ? $"@{e}" : e)
            : Sanitize(name);
        return _typeToModuleClass.TryGetValue(name, out var moduleClass)
            ? $"{moduleClass}.{emitted}"
            : emitted;
    }

    private bool IsUnresolvedTypeVariable(string name)
    {
        return name.Length == 1
            && char.IsLower(name[0])
            && (_currentTypeParams is null || !_currentTypeParams.Contains(name));
    }

    private static Dictionary<int, string> BuildTypeVarMap(IrNode.FuncDef func)
    {
        if (func.TypeParams is not { Count: > 0 } || func.Type is not ZType.ZFuncType ft)
            return new Dictionary<int, string>();
        var freeVars = Substitution.FreeVars(ft).OrderBy(id => id).ToList();
        var map = new Dictionary<int, string>();
        for (var i = 0; i < freeVars.Count && i < func.TypeParams.Count; i++)
            map[freeVars[i]] = func.TypeParams[i];
        return map;
    }

    private string WarnAndReturn(string message, string fallback, SourceSpan span = default)
    {
        Log.Debug(
            "CSharpEmitter: type mapping fallback - {Message}, using '{Fallback}'",
            message,
            fallback
        );
        diagnostics.Warning(message, span);
        return fallback;
    }

    private string ErrorAndReturn(string message, string fallback, SourceSpan span = default)
    {
        diagnostics.Error(message, span);
        return fallback;
    }

    private static string Sanitize(string name)
    {
        var sanitized = NameConverter.SanitizeIdentifier(name);
        return CSharpKeywords.Contains(sanitized) ? $"@{sanitized}" : sanitized;
    }

    // Resolve a module-level function/value definition or reference to its emitted
    // identifier. EmitNameResolver stamps EmitName on the IR node when the name had to
    // be disambiguated to avoid a collision (with another member or a nested type);
    // otherwise the original name is sanitized as usual. Keyword `@`-escaping is applied
    // on top of a stamped name, which the resolver produces in bare-identifier space.
    private string SanitizeFunc(string? emitName, string rawName)
    {
        return emitName is { } e ? (CSharpKeywords.Contains(e) ? $"@{e}" : e) : Sanitize(rawName);
    }

    // Resolve a type declaration's emitted name. EmitNameResolver stamps EmitName on the
    // type decl when its sanitized name collided with another type in the module; otherwise
    // the raw name is sanitized as usual. Mirrors SanitizeFunc for the type-declaration side
    // (QualifyType handles the reference side via _typeEmitNames).
    private string SanitizeType(string? emitName, string rawName)
    {
        return emitName is { } e ? (CSharpKeywords.Contains(e) ? $"@{e}" : e) : Sanitize(rawName);
    }

    private static string SanitizeParam(string name)
    {
        var sanitized = NameConverter.SanitizeParameter(name);
        return CSharpKeywords.Contains(sanitized) ? $"@{sanitized}" : sanitized;
    }

    // C# parses `-2147483648` as unary `-` applied to `2147483648`. The literal
    // `2147483648` doesn't fit in `int`, so it widens to `long` — and
    // `Math.Abs(-2147483648)` then resolves to `Math.Abs(long)` instead of
    // `Math.Abs(int)`, producing different results from the IL backend. Emit
    // `int.MinValue` to keep the literal an `int`.
    private static string FormatIntLiteral(int value)
    {
        return value == int.MinValue
            ? "int.MinValue"
            : value.ToString(CultureInfo.InvariantCulture);
    }

    // Member access (`.`) and indexing (`[]`) bind tighter than unary `-` in C#,
    // so an emitted receiver like `-52468` would parse as `-(52468.M())` — for
    // a numeric receiver this becomes CS0023 ("Operator '-' cannot be applied
    // to operand of type 'string'"). Wrap in parens whenever the receiver
    // begins with a leading `-` so the access binds to the negated value.
    //
    // C# also forbids member access directly on a `switch` expression or an
    // `await` expression — the parser doesn't recognize `<expr> switch { ... }
    // .M()` or `await x.M()` as `(<switch>).M()` / `(await x).M()` and instead
    // produces a CS1003 "',' expected" once it walks past the `}` (or splices
    // the `.M()` into the awaited expression). Wrap those receivers eagerly.
    private static string ParenthesizeReceiver(IrNode node, string receiver)
    {
        if (node is IrNode.Match or IrNode.Await)
            return $"({receiver})";
        return receiver.Length > 0 && receiver[0] == '-' ? $"({receiver})" : receiver;
    }

    private static string EscapeString(string s)
    {
        return s.Replace("\\", @"\\")
            .Replace("\"", "\\\"")
            .Replace("\n", "\\n")
            .Replace("\r", "\\r")
            .Replace("\t", "\\t");
    }

    // --- Tail Call Optimization helpers ---

    private static bool IsTailRecursive(IrNode body, string funcName)
    {
        return body switch
        {
            IrNode.If @if => IsTailRecursive(@if.Then, funcName)
                || IsTailRecursive(@if.Else, funcName),
            IrNode.Let let => IsTailRecursive(let.Body, funcName),
            IrNode.Call { Function: IrNode.Var v } when v.Name == funcName => true,
            IrNode.BinOp { Op: var op, Left: IrNode.Call { Function: IrNode.Var v } }
                when v.Name == funcName => false, // not tail if result is used in binop
            _ => false,
        };
    }

    private sealed record EmittedClassInfo(
        bool IsOpen,
        string? BaseClassName,
        IReadOnlyList<IrField> Fields,
        IReadOnlyList<string> MethodNames
    );
}
