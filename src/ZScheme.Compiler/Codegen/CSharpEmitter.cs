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
    IReadOnlyDictionary<string, string>? precompiledModuleNamespaces = null
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

    // Maps "<moduleClass>.<rawFuncName>" -> disambiguated C# method name for
    // functions (or module-level values) whose sanitized name collides with a
    // nested type declared in the same module class. C# rejects a class that
    // contains both a nested type and a method with the same name (CS0102),
    // even though the CLR (and thus the IL backend) permits it. This only
    // applies to modules emitted from source in the current compilation;
    // precompiled modules are referenced, not redefined, so they keep their
    // original names. Populated in BuildFuncRenames at the start of Emit.
    private readonly Dictionary<string, string> _funcRenames = new();

    // The module class currently being emitted, so function/value definitions
    // can consult _funcRenames for their disambiguated name.
    private string _emittingModuleClass = "";

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

    private readonly List<(
        string ClassName,
        IrNode.ObjectExpr Expr,
        List<CapturedVar> CapturedVars
    )> _objectClasses = [];

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
    private Dictionary<string, string>? _currentObjectCapturedFields;
    private HashSet<string>? _currentTypeParams;
    private int _indent;
    private int _objectCounter;
    private IrNode.FuncDef? _userMainFunc;

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
        while (current is IrNode.Let let)
        {
            if (!seen.Add(let.VarName))
                return true;
            current = let.Body;
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
            IrNode.WithHandlers => true,
            IrNode.If i => WantsStatementForm(i.Then) || WantsStatementForm(i.Else),
            _ => false,
        };

    private void CollectCapturedVars(
        IrNode node,
        HashSet<string> localNames,
        List<CapturedVar> captured
    )
    {
        switch (node)
        {
            case IrNode.Var v:
                if (localNames.Contains(v.Name))
                    break;
                // Class fields take precedence over module-scope names in
                // EmitVar (the field check runs before the module-name check),
                // so when the enclosing class has a field with this name we
                // must capture it even if a module function shadows the name.
                // Without this, the inner object class falls through to the
                // module-name path and emits a bare `F0` reference that
                // resolves to the static function (CS0428 method group) rather
                // than the captured field.
                //
                // The same precedence applies when this object expression is
                // nested inside another object expression. Object-class bodies
                // are emitted by EmitObjectClasses *after* EmitClassDecl
                // returns (so _currentClassFields is null at that point), but
                // the enclosing object class's captures are exposed via
                // _currentObjectCapturedFields and represent fields the inner
                // object can capture from. Without this branch the inner
                // object would skip the name as a "module function" and emit
                // a bare reference to the static function.
                var shadowsModuleName =
                    (_currentClassFields is not null && _currentClassFields.Contains(v.Name))
                    || (
                        _currentObjectCapturedFields is not null
                        && _currentObjectCapturedFields.ContainsKey(v.Name)
                    );
                // Module-scope functions and bindings resolve to a qualified
                // static member in EmitVar — emitting the bare name as a ctor
                // argument (and boxing it into an `object` field) would compile
                // to `new __Object_N(bareName)` where `bareName` is undefined
                // in the enclosing scope, and the field could not be invoked
                // anyway.
                if (
                    !shadowsModuleName
                    && (
                        v.ModuleName is not null
                        || _funcToModuleClass.ContainsKey(v.Name)
                        || _currentModuleNames.Contains(v.Name)
                    )
                )
                    break;
                captured.Add(new CapturedVar(v.Name, v.Type));
                break;
            case IrNode.Let let:
                CollectCapturedVars(let.Value, localNames, captured);
                var withBinding = new HashSet<string>(localNames) { let.VarName };
                CollectCapturedVars(let.Body, withBinding, captured);
                break;
            case IrNode.If @if:
                CollectCapturedVars(@if.Condition, localNames, captured);
                CollectCapturedVars(@if.Then, localNames, captured);
                CollectCapturedVars(@if.Else, localNames, captured);
                break;
            case IrNode.BinOp bin:
                CollectCapturedVars(bin.Left, localNames, captured);
                CollectCapturedVars(bin.Right, localNames, captured);
                break;
            case IrNode.UnaryOp un:
                CollectCapturedVars(un.Operand, localNames, captured);
                break;
            case IrNode.Call call:
                CollectCapturedVars(call.Function, localNames, captured);
                foreach (var arg in call.Args)
                    CollectCapturedVars(arg, localNames, captured);
                break;
            case IrNode.ClrCall clr:
                foreach (var arg in clr.Args)
                    CollectCapturedVars(arg, localNames, captured);
                break;
            case IrNode.ClrNew cn:
                foreach (var arg in cn.Args)
                    CollectCapturedVars(arg, localNames, captured);
                break;
            case IrNode.MethodCall mc:
                CollectCapturedVars(mc.Receiver, localNames, captured);
                foreach (var arg in mc.Args)
                    CollectCapturedVars(arg, localNames, captured);
                break;
            case IrNode.Match m:
                CollectCapturedVars(m.Scrutinee, localNames, captured);
                foreach (var arm in m.Arms)
                    CollectCapturedVars(
                        arm.Body,
                        CollectPatternBindings(arm.Pattern, localNames),
                        captured
                    );
                break;
            case IrNode.Seq seq:
                foreach (var n in seq.Nodes)
                    CollectCapturedVars(n, localNames, captured);
                break;
            case IrNode.FuncDef fd:
                CollectCapturedVars(
                    fd.Body,
                    new HashSet<string>(localNames.Concat(fd.Params.Select(p => p.Name))),
                    captured
                );
                break;
            case IrNode.WithHandlers wh:
                CollectCapturedVars(wh.Body, localNames, captured);
                foreach (var h in wh.Handlers)
                {
                    var withHandlerBinding = new HashSet<string>(localNames) { h.BindingVarName };
                    CollectCapturedVars(h.HandlerBody, withHandlerBinding, captured);
                }

                break;
            case IrNode.Throw th:
                CollectCapturedVars(th.Expr, localNames, captured);
                break;
            case IrNode.Await aw:
                CollectCapturedVars(aw.Expr, localNames, captured);
                break;
            case IrNode.SetField sf:
                CollectCapturedVars(sf.Value, localNames, captured);
                break;
            case IrNode.FieldGet fg:
                CollectCapturedVars(fg.Record, localNames, captured);
                break;
            case IrNode.TypeTest tt:
                CollectCapturedVars(tt.Value, localNames, captured);
                break;
            case IrNode.SuperMethodCall smc:
                foreach (var arg in smc.Args)
                    CollectCapturedVars(arg, localNames, captured);
                break;
            case IrNode.TupleNew tn:
                foreach (var e in tn.Elements)
                    CollectCapturedVars(e, localNames, captured);
                break;
            case IrNode.RecordNew rn:
                foreach (var (_, v) in rn.Fields)
                    CollectCapturedVars(v, localNames, captured);
                break;
            case IrNode.RecordWith rw:
                CollectCapturedVars(rw.Record, localNames, captured);
                foreach (var (_, v) in rw.Updates)
                    CollectCapturedVars(v, localNames, captured);
                break;
            case IrNode.UnionCaseNew ucn:
                foreach (var arg in ucn.Args)
                    CollectCapturedVars(arg, localNames, captured);
                break;
            case IrNode.MutableArrayNew man:
                foreach (var e in man.Elements)
                    CollectCapturedVars(e, localNames, captured);
                break;
            case IrNode.TcoJump tj:
                foreach (var arg in tj.NewArgs)
                    CollectCapturedVars(arg, localNames, captured);
                break;
            case IrNode.Closure cl:
                foreach (var v in cl.CapturedValues)
                    CollectCapturedVars(v, localNames, captured);
                break;
            case IrNode.ObjectExpr oe:
                foreach (var om in oe.Methods)
                {
                    var paramSet = new HashSet<string>(
                        localNames.Concat(om.Params.Select(p => p.Name))
                    );
                    CollectCapturedVars(om.Body, paramSet, captured);
                }

                if (oe.Constructor is { } c)
                {
                    var ctorScope = new HashSet<string>(
                        localNames.Concat(c.Params.Select(p => p.Name))
                    );
                    if (c.SuperArgs is not null)
                        foreach (var a in c.SuperArgs)
                            CollectCapturedVars(a, ctorScope, captured);
                    foreach (var e in c.BodyExprs)
                        CollectCapturedVars(e, ctorScope, captured);
                    foreach (var (_, v) in c.FieldSets)
                        CollectCapturedVars(v, ctorScope, captured);
                }

                break;
        }
    }

    private static HashSet<string> CollectPatternBindings(
        IrPattern pattern,
        HashSet<string> existing
    )
    {
        var result = new HashSet<string>(existing);
        AddPatternBindings(pattern, result);
        return result;
    }

    private static void AddPatternBindings(IrPattern pattern, HashSet<string> bindings)
    {
        switch (pattern)
        {
            case IrPattern.Variable v:
                bindings.Add(v.Name);
                break;
            case IrPattern.Constructor c:
                foreach (var f in c.Fields)
                    AddPatternBindings(f, bindings);
                break;
            case IrPattern.Tuple t:
                foreach (var e in t.Elements)
                    AddPatternBindings(e, bindings);
                break;
        }
    }

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
        return _typeToModuleClass.TryGetValue(name, out var moduleClass)
            ? $"{moduleClass}.{Sanitize(name)}"
            : Sanitize(name);
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

    // Detect functions/module-level values whose sanitized name collides with a
    // nested type (record/union/union-case/class/interface) declared in the same
    // source module, and record a disambiguated method name for each. See
    // _funcRenames for why this is necessary. Considers only modules emitted from
    // source in this compilation (the current module plus importedModules);
    // precompiled modules are referenced by their original names.
    private void BuildFuncRenames(IrNode currentNode)
    {
        var currentDefs = currentNode is IrNode.Seq seq ? seq.Nodes : [currentNode];
        var sourceModules = new List<(string ClassName, IReadOnlyList<IrNode> Defs)>
        {
            (className, currentDefs),
        };
        if (importedModules is not null)
            foreach (var m in importedModules)
                sourceModules.Add((m.ClassName, m.Definitions));

        foreach (var (moduleClass, defs) in sourceModules)
        {
            var typeNames = new HashSet<string>();
            foreach (var def in defs)
                switch (def)
                {
                    case IrNode.RecordDecl rec:
                        typeNames.Add(Sanitize(rec.Name));
                        break;
                    case IrNode.UnionDecl union:
                        typeNames.Add(Sanitize(union.Name));
                        foreach (var c in union.Cases)
                            typeNames.Add(Sanitize(c.Name));
                        break;
                    case IrNode.ClassDecl cls:
                        typeNames.Add(Sanitize(cls.Name));
                        break;
                    case IrNode.InterfaceDecl iface:
                        typeNames.Add(Sanitize(iface.Name));
                        break;
                }

            if (typeNames.Count == 0)
                continue;

            // Track every method/value name already in use in this class so a
            // generated rename never collides with an unrelated member.
            var usedNames = new HashSet<string>(typeNames);
            foreach (var def in defs)
            {
                var existing = def switch
                {
                    IrNode.FuncDef f => Sanitize(f.Name),
                    IrNode.Let l => Sanitize(l.VarName),
                    _ => null,
                };
                if (existing is not null)
                    usedNames.Add(existing);
            }

            foreach (var def in defs)
            {
                var rawName = def switch
                {
                    IrNode.FuncDef f => f.Name,
                    IrNode.Let l => l.VarName,
                    _ => null,
                };
                // `main` is referenced verbatim by the generated entry point, so
                // it must keep its sanitized name.
                if (rawName is null or "main")
                    continue;

                var csName = Sanitize(rawName);
                if (!typeNames.Contains(csName))
                    continue;

                var renamed = $"{csName}_fn";
                var suffix = 2;
                while (usedNames.Contains(renamed))
                    renamed = $"{csName}_fn{suffix++}";
                usedNames.Add(renamed);
                _funcRenames[$"{moduleClass}.{rawName}"] = renamed;
            }
        }
    }

    // Sanitize a reference/definition of a module-level function or value,
    // applying any collision rename recorded for (moduleClass, rawName).
    private string SanitizeFunc(string moduleClass, string rawName)
    {
        return _funcRenames.TryGetValue($"{moduleClass}.{rawName}", out var renamed)
            ? renamed
            : Sanitize(rawName);
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

    private readonly record struct CapturedVar(string Name, ZType Type);

    private sealed record EmittedClassInfo(
        bool IsOpen,
        string? BaseClassName,
        IReadOnlyList<IrField> Fields,
        IReadOnlyList<string> MethodNames
    );
}
