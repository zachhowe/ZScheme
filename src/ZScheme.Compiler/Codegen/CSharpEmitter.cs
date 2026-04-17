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
    bool suppressVersionPreamble = false)
{
    private static readonly ILogger Log = Serilog.Log.ForContext<CSharpEmitter>();

    private static readonly HashSet<string> CSharpKeywords =
    [
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char",
        "checked", "class", "const", "continue", "decimal", "default", "delegate",
        "do", "double", "else", "enum", "event", "explicit", "extern", "false",
        "finally", "fixed", "float", "for", "foreach", "goto", "if", "implicit",
        "in", "int", "interface", "internal", "is", "lock", "long", "namespace",
        "new", "null", "object", "operator", "out", "override", "params", "private",
        "protected", "public", "readonly", "ref", "return", "sbyte", "sealed",
        "short", "sizeof", "stackalloc", "static", "string", "struct", "switch",
        "this", "throw", "true", "try", "typeof", "uint", "ulong", "unchecked",
        "unsafe", "ushort", "using", "virtual", "void", "volatile", "while"
    ];

    private readonly HashSet<string> _currentModuleNames = [];
    private readonly Dictionary<string, EmittedClassInfo> _emittedClassInfos = new();

    private readonly Dictionary<string, string> _funcToModuleClass =
        BuildFuncToModuleMap(importedModules, precompiledModuleMap);

    private readonly HashSet<string> _localBindings = [];

    private readonly List<(string ClassName, IrNode.ObjectExpr Expr, List<string> CapturedVars)> _objectClasses = [];
    private readonly StringBuilder _sb = new();

    private readonly Dictionary<string, string> _typeToModuleClass =
        BuildTypeToModuleMap(importedModules, precompiledModuleMap);

    private HashSet<string>? _currentClassFields;
    private HashSet<string>? _currentClassLocals;
    private Dictionary<int, string>? _currentFuncTypeVarMap;
    private Dictionary<string, string>? _currentObjectCapturedFields;
    private HashSet<string>? _currentTypeParams;
    private int _indent;
    private int _objectCounter;
    private IrNode.FuncDef? _userMainFunc;

    private static Dictionary<string, string> BuildFuncToModuleMap(
        IReadOnlyList<(string ClassName, IReadOnlyList<IrNode> Definitions)>? modules,
        IReadOnlyDictionary<string, string>? precompiledMap = null)
    {
        var map = new Dictionary<string, string>();

        // Add precompiled module mappings first
        if (precompiledMap is not null)
            foreach (var (name, moduleClass) in precompiledMap)
                map[name] = moduleClass;

        if (modules is null) return map;
        foreach (var (moduleClassName, defs) in modules)
        foreach (var def in defs)
        {
            var name = def switch
            {
                IrNode.FuncDef f => f.Name,
                IrNode.Let l => l.VarName,
                _ => null
            };
            if (name is not null)
                map[name] = moduleClassName;
        }

        return map;
    }

    private static Dictionary<string, string> BuildTypeToModuleMap(
        IReadOnlyList<(string ClassName, IReadOnlyList<IrNode> Definitions)>? modules,
        IReadOnlyDictionary<string, string>? precompiledMap = null)
    {
        var map = new Dictionary<string, string>();

        // Add precompiled module mappings first
        if (precompiledMap is not null)
            foreach (var (name, moduleClass) in precompiledMap)
                map[name] = moduleClass;

        if (modules is null) return map;
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
                    if (isModule) return true;
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
                            break;
                        case IrNode.Let let:
                            _currentModuleNames.Add(let.VarName);
                            break;
                    }

                break;
            }
            case IrNode.FuncDef func:
                _currentModuleNames.Add(func.Name);
                break;
            case IrNode.Let let:
                _currentModuleNames.Add(let.VarName);
                break;
        }
    }

    private string BuildOutParamArgList(IReadOnlyList<IrNode> visibleArgs,
        IReadOnlyList<ClrInterop.OutParamInfo> outParams)
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
            return $"{qualified}<{string.Join(", ", nt.TypeArgs.Select(TypeToCs))}>";
        return qualified;
    }

    private static bool ContainsAwait(IrNode node)
    {
        return node switch
        {
            IrNode.Await => true,
            IrNode.Let let => ContainsAwait(let.Value) || ContainsAwait(let.Body),
            IrNode.If @if => ContainsAwait(@if.Condition) || ContainsAwait(@if.Then) || ContainsAwait(@if.Else),
            IrNode.Match match => ContainsAwait(match.Scrutinee) || match.Arms.Any(a => ContainsAwait(a.Body)),
            IrNode.Call call => ContainsAwait(call.Function) || call.Args.Any(ContainsAwait),
            _ => false
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

    private static void CollectCapturedVars(IrNode node, HashSet<string> localNames, List<string> captured)
    {
        switch (node)
        {
            case IrNode.Var v:
                if (!localNames.Contains(v.Name))
                    captured.Add(v.Name);
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
            case IrNode.MethodCall mc:
                CollectCapturedVars(mc.Receiver, localNames, captured);
                foreach (var arg in mc.Args)
                    CollectCapturedVars(arg, localNames, captured);
                break;
        }
    }

    private List<IrField> GetEmittedInheritedFields(string? baseClassName)
    {
        var result = new List<IrField>();
        if (baseClassName is not null && _emittedClassInfos.TryGetValue(baseClassName, out var info))
        {
            result.AddRange(GetEmittedInheritedFields(info.BaseClassName));
            result.AddRange(info.Fields);
        }

        return result;
    }

    private HashSet<string> GetEmittedInheritedMethodNames(string? baseClassName)
    {
        var result = new HashSet<string>();
        if (baseClassName is not null && _emittedClassInfos.TryGetValue(baseClassName, out var info))
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
            _ => value.ToString() ?? ""
        };
    }

    private static string FormatFieldAttributes(IReadOnlyList<IrAttribute>? attrs)
    {
        if (attrs is null or { Count: 0 }) return "";
        var parts = attrs.Select(a => FormatAttribute(a).Insert(1, "property: "));
        return string.Join(" ", parts) + " ";
    }

    private static string FormatWhereConstraints(IReadOnlyDictionary<string, GenericConstraintKind>? constraints)
    {
        if (constraints is not { Count: > 0 }) return "";
        var sb = new StringBuilder();
        foreach (var (param, kind) in constraints)
        {
            var parts = new List<string>();
            if (kind.HasFlag(GenericConstraintKind.Class)) parts.Add("class");
            if (kind.HasFlag(GenericConstraintKind.Struct)) parts.Add("struct");
            if (kind.HasFlag(GenericConstraintKind.Unmanaged)) parts.Add("unmanaged");
            if (kind.HasFlag(GenericConstraintKind.NotNull)) parts.Add("notnull");
            if (kind.HasFlag(GenericConstraintKind.Default)) parts.Add("default");
            if (kind.HasFlag(GenericConstraintKind.New)) parts.Add("new()");
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
        return type switch
        {
            ZType.ZPrimitiveType { Kind: PrimitiveKind.Int } => "int",
            ZType.ZPrimitiveType { Kind: PrimitiveKind.Long } => "long",
            ZType.ZPrimitiveType { Kind: PrimitiveKind.Float } => "float",
            ZType.ZPrimitiveType { Kind: PrimitiveKind.Double } => "double",
            ZType.ZPrimitiveType { Kind: PrimitiveKind.Byte } => "byte",
            ZType.ZPrimitiveType { Kind: PrimitiveKind.Char } => "char",
            ZType.ZPrimitiveType { Kind: PrimitiveKind.Bool } => "bool",
            ZType.ZPrimitiveType { Kind: PrimitiveKind.String } => "string",
            ZType.ZPrimitiveType { Kind: PrimitiveKind.Unit } => "System.ValueTuple",
            ZType.ZFuncType { Return: ZType.ZPrimitiveType { Kind: PrimitiveKind.Unit }, Params.Count: 0 } =>
                "System.Action",
            ZType.ZFuncType { Return: ZType.ZPrimitiveType { Kind: PrimitiveKind.Unit } } ft =>
                $"System.Action<{string.Join(", ", ft.Params.Select(TypeToCs))}>",
            ZType.ZFuncType ft =>
                $"System.Func<{string.Join(", ", ft.Params.Select(TypeToCs).Append(TypeToCs(ft.Return)))}>",
            ZType.ZNamedType { Name: "List", TypeArgs: [var elem] } =>
                $"System.Collections.Immutable.ImmutableList<{TypeToCs(elem)}>",
            ZType.ZNamedType { Name: "Array", TypeArgs: [var elem] } =>
                $"System.Collections.Immutable.ImmutableArray<{TypeToCs(elem)}>",
            ZType.ZNamedType { Name: "Mutable-Array", TypeArgs: [var arrElem] } =>
                $"{TypeToCs(arrElem)}[]",
            ZType.ZNamedType { Name: "Mutable-List", TypeArgs: [var mlElem] } =>
                $"System.Collections.Generic.List<{TypeToCs(mlElem)}>",
            ZType.ZNamedType { Name: "Pair", TypeArgs: [var pk, var pv] } =>
                $"System.Collections.Generic.KeyValuePair<{TypeToCs(pk)}, {TypeToCs(pv)}>",
            ZType.ZNamedType { Name: "Map", TypeArgs: [var k, var v] } =>
                $"System.Collections.Immutable.ImmutableDictionary<{TypeToCs(k)}, {TypeToCs(v)}>",
            ZType.ZNamedType { Name: "Mutable-Map", TypeArgs: [var mmK, var mmV] } =>
                $"System.Collections.Generic.Dictionary<{TypeToCs(mmK)}, {TypeToCs(mmV)}>",
            ZType.ZNamedType { Name: "Concurrent-Bag", TypeArgs: [var cbElem] } =>
                $"System.Collections.Concurrent.ConcurrentBag<{TypeToCs(cbElem)}>",
            ZType.ZNamedType { Name: "Concurrent-Queue", TypeArgs: [var cqElem] } =>
                $"System.Collections.Concurrent.ConcurrentQueue<{TypeToCs(cqElem)}>",
            ZType.ZNamedType { Name: "Concurrent-Stack", TypeArgs: [var csElem] } =>
                $"System.Collections.Concurrent.ConcurrentStack<{TypeToCs(csElem)}>",
            ZType.ZNamedType { Name: "Concurrent-Dictionary", TypeArgs: [var cdK, var cdV] } =>
                $"System.Collections.Concurrent.ConcurrentDictionary<{TypeToCs(cdK)}, {TypeToCs(cdV)}>",
            ZType.ZNamedType { Name: "Task", TypeArgs: [] } =>
                "System.Threading.Tasks.Task",
            ZType.ZNamedType { Name: "Task", TypeArgs: [var taskT] } =>
                $"System.Threading.Tasks.Task<{TypeToCs(taskT)}>",
            ZType.ZNamedType { Name: "ValueTuple" } vt when vt.TypeArgs.Count > 0 =>
                $"({string.Join(", ", vt.TypeArgs.Select(TypeToCs))})",
            ZType.ZNamedType { TypeArgs.Count: > 0 } nt =>
                $"{QualifyType(nt.Name)}<{string.Join(", ", nt.TypeArgs.Select(TypeToCs))}>",
            ZType.ZNamedType nt when IsUnresolvedTypeVariable(nt.Name) =>
                WarnAndReturn($"Unresolved type variable '{nt.Name}' from annotation, using 'object'", "object"),
            ZType.ZNamedType nt => QualifyType(nt.Name),
            ZType.ZNullableType { Inner: var inner } => $"{TypeToCs(inner)}?",
            ZType.ZTypeVar tv when _currentFuncTypeVarMap is not null
                                   && _currentFuncTypeVarMap.TryGetValue(tv.Id, out var tpName) => tpName,
            ZType.ZTypeVar => WarnAndReturn("Unresolved type variable in C# emission, using 'object'", "object"),
            _ => WarnAndReturn($"Unmapped type in C# emission: {type.GetType().Name}, using 'object'", "object")
        };
    }

    private string QualifyType(string name)
    {
        return _typeToModuleClass.TryGetValue(name, out var moduleClass)
            ? $"{moduleClass}.{Sanitize(name)}"
            : Sanitize(name);
    }

    private bool IsUnresolvedTypeVariable(string name)
    {
        return name.Length == 1 && char.IsLower(name[0])
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

    private string WarnAndReturn(string message, string fallback)
    {
        Log.Debug("CSharpEmitter: type mapping fallback - {Message}, using '{Fallback}'", message, fallback);
        diagnostics.Warning(message, SourceSpan.None);
        return fallback;
    }

    private string ErrorAndReturn(string message, string fallback)
    {
        diagnostics.Error(message, SourceSpan.None);
        return fallback;
    }

    private static string Sanitize(string name)
    {
        var sanitized = NameConverter.SanitizeIdentifier(name);
        return CSharpKeywords.Contains(sanitized) ? $"@{sanitized}" : sanitized;
    }

    private static string SanitizeParam(string name)
    {
        var sanitized = NameConverter.SanitizeParameter(name);
        return CSharpKeywords.Contains(sanitized) ? $"@{sanitized}" : sanitized;
    }

    private static string EscapeString(string s)
    {
        return s.Replace("\\", @"\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r")
            .Replace("\t", "\\t");
    }

    // --- Tail Call Optimization helpers ---

    private static bool IsTailRecursive(IrNode body, string funcName)
    {
        return body switch
        {
            IrNode.If @if =>
                IsTailRecursive(@if.Then, funcName) || IsTailRecursive(@if.Else, funcName),
            IrNode.Let let => IsTailRecursive(let.Body, funcName),
            IrNode.Call { Function: IrNode.Var v } when v.Name == funcName => true,
            IrNode.BinOp { Op: var op, Left: IrNode.Call { Function: IrNode.Var v } }
                when v.Name == funcName => false, // not tail if result is used in binop
            _ => false
        };
    }

    private sealed record EmittedClassInfo(
        bool IsOpen,
        string? BaseClassName,
        IReadOnlyList<IrField> Fields,
        IReadOnlyList<string> MethodNames);
}
