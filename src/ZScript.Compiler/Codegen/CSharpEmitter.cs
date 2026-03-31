using System.Globalization;
using System.Text;
using Serilog;
using ZScript.Compiler.Diagnostics;
using ZScript.Compiler.Ir;
using ZScript.Compiler.Types;

namespace ZScript.Compiler.Codegen;

public sealed class CSharpEmitter(
    DiagnosticBag diagnostics,
    string ns,
    string className,
    IReadOnlyList<string>? clrUsings = null,
    IReadOnlyList<(string ClassName, IReadOnlyList<IrNode> Definitions)>? importedModules = null,
    IReadOnlyList<string>? precompiledAssemblyPaths = null,
    IReadOnlyDictionary<string, string>? precompiledModuleMap = null,
    bool isModule = false)
{
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

    private readonly Dictionary<string, string> _funcToModuleClass =
        BuildFuncToModuleMap(importedModules, precompiledModuleMap);

    private readonly Dictionary<string, string> _typeToModuleClass =
        BuildTypeToModuleMap(importedModules, precompiledModuleMap);

    private readonly List<(string ClassName, IrNode.ObjectExpr Expr, List<string> CapturedVars)> _objectClasses = [];
    private readonly StringBuilder _sb = new();
    private HashSet<string>? _currentClassFields;
    private HashSet<string>? _currentClassLocals;
    private Dictionary<string, string>? _currentObjectCapturedFields;
    private readonly Dictionary<string, EmittedClassInfo> _emittedClassInfos = new();

    private sealed record EmittedClassInfo(
        bool IsOpen,
        string? BaseClassName,
        IReadOnlyList<IrField> Fields,
        IReadOnlyList<string> MethodNames);
    private Dictionary<int, string>? _currentFuncTypeVarMap;
    private HashSet<string>? _currentTypeParams;
    private int _indent;
    private readonly HashSet<string> _currentModuleNames = [];
    private readonly HashSet<string> _localBindings = [];
    private int _objectCounter;
    private int _propagateCounter;
    private IrNode.FuncDef? _userMainFunc;
    public IReadOnlyList<string> PrecompiledAssemblyPaths { get; } = precompiledAssemblyPaths ?? [];


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

    public string Emit(IrNode node)
    {
        Log.Debug("CSharpEmitter: emitting class {ClassName} in namespace {Namespace}", className, ns);
        _sb.Clear();
        var mainStatements = new List<IrNode>();

        EmitLine($"// <auto-generated by ZScript compiler {CompilerInfo.VersionString}>");
        EmitLine("#nullable enable");
        EmitLine();
        if (clrUsings is { Count: > 0 })
        {
            foreach (var usingNs in clrUsings)
                EmitLine($"using {usingNs};");
            EmitLine();
        }

        EmitLine($"namespace {ns};");
        EmitLine();
        EmitTypeDeclarationsInline(node);
        EmitLine();

        if (HasProgramContent(node))
        {
            CollectModuleNames(node);

            EmitLine($"public static class {className}");
            EmitLine("{");
            _indent++;

            if (node is IrNode.Seq seq)
                foreach (var child in seq.Nodes)
                    EmitTopLevel(child, mainStatements);
            else
                EmitTopLevel(node, mainStatements);

            if (mainStatements.Count > 0)
            {
                EmitLine();
                EmitLine($"static {className}()");
                EmitLine("{");
                _indent++;
                foreach (var stmt in mainStatements)
                    EmitLine($"{EmitExpr(stmt)};");
                _indent--;
                EmitLine("}");
            }

            if (_userMainFunc is not null)
            {
                EmitLine();
                EmitLine("public static int Main(string[] args)");
                EmitLine("{");
                _indent++;
                EmitLine($"return {Sanitize("main")}(System.Collections.Immutable.ImmutableList.Create(args));");
                _indent--;
                EmitLine("}");
            }

            // Emit nested classes for object expressions
            if (_objectClasses.Count > 0)
            {
                EmitLine();
                EmitObjectClasses();
            }

            _indent--;
            EmitLine("}");
        }

        Log.Debug("CSharpEmitter: emit complete, {OutputLength} chars", _sb.Length);

        // Emit imported module classes (type declarations nested inside, plus functions/values)
        if (importedModules is { Count: > 0 })
            foreach (var (moduleClassName, defs) in importedModules)
            {
                var hasContent = defs.Any(d =>
                    d is IrNode.FuncDef or IrNode.Let or IrNode.ClrCall or IrNode.Call or IrNode.Throw
                        or IrNode.Await or IrNode.RecordDecl or IrNode.UnionDecl or IrNode.ClassDecl
                        or IrNode.InterfaceDecl);
                if (!hasContent) continue;

                EmitLine();
                EmitLine($"public static class {moduleClassName}");
                EmitLine("{");
                _indent++;

                // Emit type declarations inside the module class
                foreach (var def in defs)
                    switch (def)
                    {
                        case IrNode.RecordDecl rec:
                            EmitLine(EmitRecordDecl(rec));
                            EmitLine();
                            break;
                        case IrNode.UnionDecl union:
                            EmitLine(EmitUnionDecl(union));
                            EmitLine();
                            break;
                        case IrNode.ClassDecl classDecl:
                            EmitClassDecl(classDecl);
                            EmitLine();
                            break;
                        case IrNode.InterfaceDecl ifaceDecl:
                            EmitInterfaceDecl(ifaceDecl);
                            EmitLine();
                            break;
                    }

                var moduleInitStatements = new List<IrNode>();
                foreach (var def in defs)
                    switch (def)
                    {
                        case IrNode.FuncDef func:
                            EmitFuncDef(func);
                            break;
                        case IrNode.Let let:
                            EmitLine(
                                $"public static {TypeToCs(let.Value.Type)} {Sanitize(let.VarName)} = {EmitExpr(let.Value)};");
                            if (let.Body is not IrNode.UnitConst)
                                moduleInitStatements.Add(let.Body);
                            break;
                        case IrNode.ClrCall:
                        case IrNode.Call:
                        case IrNode.Throw:
                        case IrNode.Await:
                            moduleInitStatements.Add(def);
                            break;
                    }

                if (moduleInitStatements.Count > 0)
                {
                    EmitLine();
                    EmitLine($"static {moduleClassName}()");
                    EmitLine("{");
                    _indent++;
                    foreach (var stmt in moduleInitStatements)
                        EmitLine($"{EmitExpr(stmt)};");
                    _indent--;
                    EmitLine("}");
                }

                _indent--;
                EmitLine("}");
            }

        return _sb.ToString();
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
        if (node is IrNode.Seq seq)
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
        else if (node is IrNode.FuncDef func)
            _currentModuleNames.Add(func.Name);
        else if (node is IrNode.Let let)
            _currentModuleNames.Add(let.VarName);
    }

    private void EmitTopLevel(IrNode node, List<IrNode> mainStatements)
    {
        switch (node)
        {
            case IrNode.FuncDef func:
                if (func.Name == "main")
                    _userMainFunc = func;
                EmitFuncDef(func);
                break;
            case IrNode.RecordDecl rec:
                Log.Debug("CSharpEmitter: emitting record {RecordName} inside module class", rec.Name);
                EmitLine(EmitRecordDecl(rec));
                EmitLine();
                break;
            case IrNode.UnionDecl union:
                Log.Debug("CSharpEmitter: emitting union {UnionName} inside module class", union.Name);
                EmitLine(EmitUnionDecl(union));
                EmitLine();
                break;
            case IrNode.ClassDecl classDecl:
                EmitClassDecl(classDecl);
                EmitLine();
                break;
            case IrNode.InterfaceDecl ifaceDecl:
                EmitInterfaceDecl(ifaceDecl);
                EmitLine();
                break;
            case IrNode.Let let:
                EmitLine($"public static {TypeToCs(let.Value.Type)} {Sanitize(let.VarName)} = {EmitExpr(let.Value)};");
                if (let.Body is not IrNode.UnitConst)
                    EmitTopLevel(let.Body, mainStatements);
                break;
            case IrNode.ClrCall:
            case IrNode.Call:
            case IrNode.Throw:
            case IrNode.Await:
                mainStatements.Add(node);
                break;
        }
    }

    private void EmitFuncDef(IrNode.FuncDef func)
    {
        Log.Debug("CSharpEmitter: emitting function {FuncName}, IsAsync={IsAsync}, TypeParams={TypeParamCount}",
            func.Name, func.IsAsync, func.TypeParams?.Count ?? 0);
        var prevTypeParams = _currentTypeParams;
        var prevFuncTypeVarMap = _currentFuncTypeVarMap;

        if (func.TypeParams is { Count: > 0 })
        {
            _currentFuncTypeVarMap = BuildTypeVarMap(func);
            _currentTypeParams = new HashSet<string>(func.TypeParams);
        }

        EmitAttributes(func.Attributes);
        var asyncPrefix = func.IsAsync ? "async " : "";
        var retTypeStr = func.IsAsync
            ? func.ReturnType == ZType.Unit
                ? "System.Threading.Tasks.Task"
                : $"System.Threading.Tasks.Task<{TypeToCs(func.ReturnType)}>"
            : ReturnTypeToCs(func.ReturnType);
        var parms = string.Join(", ",
            func.Params.Select(FormatParam));
        var typeParamStr = func.TypeParams is { Count: > 0 }
            ? $"<{string.Join(", ", func.TypeParams)}>"
            : "";

        var whereClause = FormatWhereConstraints(func.TypeParamConstraints);
        EmitLine($"public static {asyncPrefix}{retTypeStr} {Sanitize(func.Name)}{typeParamStr}({parms}){whereClause}");
        EmitLine("{");
        _indent++;
        _localBindings.Clear();

        if (func.IsSelfRecursive && IsTailRecursive(func.Body, func.Name))
            EmitTailRecursiveLoop(func);
        else if (ContainsPropagate(func.Body))
            EmitStatementsBody(func.Body, func.ReturnType);
        else if (func.IsAsync && ContainsAwait(func.Body))
            EmitAsyncStatementsBody(func.Body, func.ReturnType == ZType.Unit);
        else if (func.Body is IrNode.Throw)
            EmitLine($"{EmitExpr(func.Body)};");
        else if (func.IsAsync && func.ReturnType == ZType.Unit)
            EmitLine($"{EmitExpr(func.Body)};");
        else if (func.ReturnType == ZType.Unit)
            EmitLine($"{EmitExpr(func.Body)};");
        else if (func.Body is IrNode.Let && !HasLetSpineShadowing(func.Body, func.Params))
            EmitStatementsBody(func.Body, func.ReturnType);
        else
            EmitLine($"return {EmitExpr(func.Body)};");

        _indent--;
        EmitLine("}");
        EmitLine();

        _currentTypeParams = prevTypeParams;
        _currentFuncTypeVarMap = prevFuncTypeVarMap;
    }

    private void EmitTailRecursiveLoop(IrNode.FuncDef func)
    {
        EmitLine("while (true)");
        EmitLine("{");
        _indent++;

        EmitTcoBody(func.Body, func.Name, func.Params, func.ReturnType);

        _indent--;
        EmitLine("}");
    }

    private void EmitTcoBody(IrNode body, string funcName, IReadOnlyList<IrParam> parms, ZType returnType)
    {
        switch (body)
        {
            case IrNode.If @if:
                EmitLine($"if ({EmitExpr(@if.Condition)})");
                EmitLine("{");
                _indent++;
                EmitTcoBody(@if.Then, funcName, parms, returnType);
                _indent--;
                EmitLine("}");
                EmitLine("else");
                EmitLine("{");
                _indent++;
                EmitTcoBody(@if.Else, funcName, parms, returnType);
                _indent--;
                EmitLine("}");
                break;

            case IrNode.Let let:
                EmitLine($"var {SanitizeParam(let.VarName)} = {EmitExpr(let.Value)};");
                EmitTcoBody(let.Body, funcName, parms, returnType);
                break;

            case IrNode.Call { Function: IrNode.Var v } call when v.Name == funcName:
                // Tail recursive call — compute new args, then reassign params, then continue
                for (var i = 0; i < call.Args.Count && i < parms.Count; i++)
                    EmitLine($"var __tmp_{i} = {EmitExpr(call.Args[i])};");
                for (var i = 0; i < call.Args.Count && i < parms.Count; i++)
                    EmitLine($"{SanitizeParam(parms[i].Name)} = __tmp_{i};");
                EmitLine("continue;");
                break;

            case IrNode.Throw:
                EmitLine($"{EmitExpr(body)};");
                break;

            case IrNode.Await:
                if (returnType == ZType.Unit)
                    EmitLine($"{EmitExpr(body)};");
                else
                    EmitLine($"return {EmitExpr(body)};");
                break;

            default:
                if (returnType == ZType.Unit)
                    EmitLine($"{EmitExpr(body)};");
                else
                    EmitLine($"return {EmitExpr(body)};");
                break;
        }
    }

    private string EmitExpr(IrNode node)
    {
        return node switch
        {
            IrNode.IntConst n => n.Value.ToString(),
            IrNode.FloatConst n => $"{n.Value.ToString(CultureInfo.InvariantCulture)}f",
            IrNode.BoolConst n => n.Value ? "true" : "false",
            IrNode.StringConst n => $"\"{EscapeString(n.Value)}\"",
            IrNode.UnitConst => "default(System.ValueTuple)",
            IrNode.Var n => EmitVar(n),
            IrNode.Let n => EmitLetExpr(n),
            IrNode.If n => EmitIfExpr(n),
            IrNode.BinOp n => EmitBinOp(n),
            IrNode.UnaryOp n => EmitUnaryOp(n),
            IrNode.Call n => EmitCall(n),
            IrNode.ClrCall n => EmitClrCall(n),
            IrNode.FuncDef n => EmitLambdaExpr(n),
            IrNode.RecordNew n => EmitRecordNew(n),
            IrNode.FieldGet n => $"{EmitExpr(n.Record)}.{Sanitize(n.FieldName)}",
            IrNode.UnionCaseNew n => EmitUnionCaseNew(n),
            IrNode.Match n => EmitMatch(n),
            IrNode.MutableArrayNew n => EmitMutableArrayNew(n),
            IrNode.MapNew n => EmitMapNew(n),
            IrNode.TcoJump j => EmitTcoJump(j),
            IrNode.TryCatch n => EmitTryCatch(n),
            IrNode.MethodCall n => EmitMethodCall(n),
            IrNode.ObjectExpr n => EmitObjectExpr(n),
            IrNode.ClrNew n => EmitClrNew(n),
            IrNode.Throw n => EmitThrow(n),
            IrNode.Await n => $"await {EmitExpr(n.Expr)}",
            IrNode.SuperMethodCall n => EmitSuperMethodCall(n),
            _ => ErrorAndReturn($"C# emission not implemented for {node.GetType().Name}", "default")
        };
    }

    private string EmitLetExpr(IrNode.Let n)
    {
        // Emit as a block expression using a method-local function
        var valExpr = EmitExpr(n.Value);
        var bodyExpr = EmitExpr(n.Body);
        // Use an immediately invoked lambda for let-in-expression, wrapped in Func<> delegate cast
        return
            $"((System.Func<{TypeToCs(n.Value.Type)}, {TypeToCs(n.Body.Type)}>)(({TypeToCs(n.Value.Type)} {SanitizeParam(n.VarName)}) => {bodyExpr}))({valExpr})";
    }

    private string EmitIfExpr(IrNode.If n)
    {
        var cond = EmitExpr(n.Condition);
        var then = EmitExpr(n.Then);
        var @else = EmitExpr(n.Else);
        return $"({cond} ? {then} : {@else})";
    }

    private string EmitBinOp(IrNode.BinOp n)
    {
        var left = EmitExpr(n.Left);
        var right = EmitExpr(n.Right);
        var op = n.Op switch
        {
            "=" => "==",
            "!=" => "!=",
            "and" => "&&",
            "or" => "||",
            _ => n.Op
        };
        return $"({left} {op} {right})";
    }

    private string EmitUnaryOp(IrNode.UnaryOp n)
    {
        var operand = EmitExpr(n.Operand);
        var op = n.Op switch
        {
            "not" => "!",
            _ => n.Op
        };
        return $"({op}{operand})";
    }

    private string EmitCall(IrNode.Call n)
    {
        var func = EmitExpr(n.Function);
        var args = string.Join(", ", n.Args.Select(EmitExpr));
        return $"{func}({args})";
    }

    private string EmitVar(IrNode.Var n)
    {
        if (_currentObjectCapturedFields is not null &&
            _currentObjectCapturedFields.TryGetValue(n.Name, out var fieldAccess))
            return fieldAccess;

        if (_currentClassFields is not null)
        {
            if (_currentClassFields.Contains(n.Name))
                return $"this.{Sanitize(n.Name)}";
            if (_currentClassLocals is null || !_currentClassLocals.Contains(n.Name))
            {
                var qualifyingClass = _funcToModuleClass.TryGetValue(n.Name, out var moduleClass)
                    ? moduleClass
                    : className;
                return $"{qualifyingClass}.{Sanitize(n.Name)}";
            }
        }

        if (_localBindings.Contains(n.Name))
            return SanitizeParam(n.Name);
        if (_funcToModuleClass.TryGetValue(n.Name, out var modClass))
            return $"{modClass}.{Sanitize(n.Name)}";
        if (_currentModuleNames.Contains(n.Name))
            return Sanitize(n.Name);
        return SanitizeParam(n.Name);
    }

    private string EmitClrCall(IrNode.ClrCall n)
    {
        var args = string.Join(", ", n.Args.Select(EmitExpr));
        return $"{n.QualifiedTypeName}.{n.MethodName}({args})";
    }

    private string EmitClrNew(IrNode.ClrNew n)
    {
        var args = string.Join(", ", n.Args.Select(EmitExpr));
        return $"new {n.QualifiedTypeName}({args})";
    }

    private string EmitThrow(IrNode.Throw n)
    {
        return $"throw {EmitExpr(n.Expr)}";
    }

    private string EmitLambdaExpr(IrNode.FuncDef n)
    {
        var parms = string.Join(", ",
            n.Params.Select(p => $"{TypeToCs(p.Type)} {SanitizeParam(p.Name)}"));
        var body = EmitExpr(n.Body);
        if (n.ReturnType is ZType.ZPrimitiveType { Kind: PrimitiveKind.Unit })
            return $"(({parms}) => {{ {body}; }})";
        return $"(({parms}) => {body})";
    }

    private string EmitRecordNew(IrNode.RecordNew n)
    {
        var args = string.Join(", ", n.Fields.Select(f => $"{Sanitize(f.FieldName)}: {EmitExpr(f.Value)}"));
        return $"new {QualifyType(n.TypeName)}({args})";
    }

    private string EmitUnionCaseNew(IrNode.UnionCaseNew n)
    {
        // Extract type args from the node's type (the union's named type with type arguments)
        var typeArgStr = "";
        if (n.Type is ZType.ZNamedType { TypeArgs.Count: > 0 } nt)
            typeArgStr = $"<{string.Join(", ", nt.TypeArgs.Select(TypeToCs))}>";

        var args = string.Join(", ", n.Args.Select(EmitExpr));
        if (n.Args.Count == 0)
            return $"new {QualifyType(n.CaseName)}{typeArgStr}()";
        return $"new {QualifyType(n.CaseName)}{typeArgStr}({args})";
    }

    private string EmitMatch(IrNode.Match n)
    {
        var scrutinee = EmitExpr(n.Scrutinee);
        var scrutineeType = n.Scrutinee.Type;
        var sb = new StringBuilder();
        sb.Append($"{scrutinee} switch {{ ");

        foreach (var arm in n.Arms)
        {
            var pattern = EmitPattern(arm.Pattern, scrutineeType);
            var body = EmitExpr(arm.Body);
            sb.Append($"{pattern} => {body}, ");
        }

        // Only add fallback if the last arm isn't already a catch-all
        var lastPattern = n.Arms[^1].Pattern;
        if (lastPattern is not IrPattern.Wildcard and not IrPattern.Variable)
            sb.Append("_ => throw new System.InvalidOperationException(\"Non-exhaustive match\"), ");
        sb.Append('}');
        return sb.ToString();
    }

    private string EmitPattern(IrPattern p, ZType? scrutineeType)
    {
        return p switch
        {
            IrPattern.Wildcard => "_",
            IrPattern.Variable v => $"var {SanitizeParam(v.Name)}",
            IrPattern.Literal { Value: int i } => i.ToString(),
            IrPattern.Literal { Value: float f } =>
                $"{f.ToString(CultureInfo.InvariantCulture)}f",
            IrPattern.Literal { Value: bool b } => b ? "true" : "false",
            IrPattern.Literal { Value: string s } => $"\"{EscapeString(s)}\"",
            IrPattern.Constructor c => EmitConstructorPattern(c, scrutineeType),
            _ => WarnAndReturn($"Unsupported pattern type for C# emission: {p.GetType().Name}", "_")
        };
    }

    private string EmitConstructorPattern(IrPattern.Constructor c, ZType? scrutineeType)
    {
        var qualifiedName = ResolveConstructorName(c.Name, scrutineeType);

        if (c.Fields.Count == 0)
            return qualifiedName;

        var fields = string.Join(", ", c.Fields.Select((f, i) => EmitPattern(f, null)));
        return $"{qualifiedName}({fields})";
    }

    private string ResolveConstructorName(string ctorName, ZType? scrutineeType)
    {
        var qualified = QualifyType(ctorName);
        // For generic union types, append type arguments to the case name
        if (scrutineeType is ZType.ZNamedType { TypeArgs.Count: > 0 } nt)
            return $"{qualified}<{string.Join(", ", nt.TypeArgs.Select(TypeToCs))}>";
        return qualified;
    }

    private string EmitMutableArrayNew(IrNode.MutableArrayNew n)
    {
        var csType = TypeToCs(n.ElementType);
        if (n.Elements.Count == 0)
            return $"System.Array.Empty<{csType}>()";
        var elems = string.Join(", ", n.Elements.Select(EmitExpr));
        return $"new {csType}[] {{ {elems} }}";
    }

    private string EmitMapNew(IrNode.MapNew n)
    {
        if (n.Entries.Count == 0)
            return "System.Collections.Immutable.ImmutableDictionary.Create<object, object>()";
        var entries = string.Join(", ",
            n.Entries.Select(e =>
                $"new System.Collections.Generic.KeyValuePair<{TypeToCs(e.Key.Type)}, {TypeToCs(e.Value.Type)}>({EmitExpr(e.Key)}, {EmitExpr(e.Value)})"));
        return $"System.Collections.Immutable.ImmutableDictionary.CreateRange(new[] {{ {entries} }})";
    }

    private string EmitTcoJump(IrNode.TcoJump j)
    {
        // This is used in the tail-recursive loop rewrite
        var sb = new StringBuilder();
        sb.Append("{ ");
        for (var i = 0; i < j.NewArgs.Count; i++)
        {
            var tmpName = $"__tmp_{i}";
            sb.Append($"var {tmpName} = {EmitExpr(j.NewArgs[i])}; ");
        }

        for (var i = 0; i < j.NewArgs.Count; i++) sb.Append($"__{SanitizeParam(j.ParamNames[i])} = __tmp_{i}; ");
        sb.Append("continue; }");
        return sb.ToString();
    }

    private string EmitMethodCall(IrNode.MethodCall n)
    {
        var receiver = EmitExpr(n.Receiver);
        var methodName = Sanitize(n.MethodName);
        if (n.IsPropertySet) return $"{receiver}.{n.MethodName} = {EmitExpr(n.Args[0])}";
        if (n.IsProperty) return $"{receiver}.{methodName}";
        if (n.IsIndexerSet) return $"{receiver}[{EmitExpr(n.Args[0])}] = {EmitExpr(n.Args[1])}";
        if (n.IsIndexer) return $"{receiver}[{EmitExpr(n.Args[0])}]";
        var args = string.Join(", ", n.Args.Select(EmitExpr));
        return $"{receiver}.{methodName}({args})";
    }

    private string EmitTryCatch(IrNode.TryCatch n)
    {
        // Extract the Ok/Err types from n.Type which should be Result<T, Error>
        var resultType = TypeToCs(n.Type);
        string okTypeStr, errTypeStr;
        if (n.Type is ZType.ZNamedType { Name: "Result", TypeArgs: [var okT, var errT] })
        {
            okTypeStr = TypeToCs(okT);
            errTypeStr = TypeToCs(errT);
        }
        else
        {
            diagnostics.Warning("Expected Result type for try-catch expression, falling back to object",
                SourceSpan.None);
            okTypeStr = "object";
            errTypeStr = QualifyType("ErrorInfo");
        }

        var body = EmitExpr(n.Body);
        var qOk = QualifyType("Ok");
        var qErr = QualifyType("Err");
        var qErrorInfo = QualifyType("ErrorInfo");
        var qNone = QualifyType("None");
        return
            $"((System.Func<{resultType}>)(() => {{ try {{ return new {qOk}<{okTypeStr}, {errTypeStr}>({body}); }} catch (System.Exception __ex) {{ return new {qErr}<{okTypeStr}, {errTypeStr}>(new {qErrorInfo}(__ex.Message, new {qNone}<{qErrorInfo}>())); }} }}))()";
    }

    private static bool ContainsPropagate(IrNode node)
    {
        return node switch
        {
            IrNode.Propagate => true,
            IrNode.Let let => ContainsPropagate(let.Value) || ContainsPropagate(let.Body),
            IrNode.If @if => ContainsPropagate(@if.Then) || ContainsPropagate(@if.Else),
            IrNode.Match match => match.Arms.Any(a => ContainsPropagate(a.Body)),
            _ => false
        };
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

    private void EmitAsyncStatementsBody(IrNode body, bool isVoidReturn)
    {
        switch (body)
        {
            case IrNode.Let let:
                EmitLine($"var {SanitizeParam(let.VarName)} = {EmitExpr(let.Value)};");
                EmitAsyncStatementsBody(let.Body, isVoidReturn);
                break;
            case IrNode.If @if:
                EmitLine($"if ({EmitExpr(@if.Condition)})");
                EmitLine("{");
                _indent++;
                EmitAsyncStatementsBody(@if.Then, isVoidReturn);
                _indent--;
                EmitLine("}");
                EmitLine("else");
                EmitLine("{");
                _indent++;
                EmitAsyncStatementsBody(@if.Else, isVoidReturn);
                _indent--;
                EmitLine("}");
                break;
            case IrNode.Throw:
                EmitLine($"{EmitExpr(body)};");
                break;
            default:
                if (isVoidReturn)
                    EmitLine($"{EmitExpr(body)};");
                else
                    EmitLine($"return {EmitExpr(body)};");
                break;
        }
    }

    private void EmitStatementsBody(IrNode body, ZType funcReturnType)
    {
        switch (body)
        {
            case IrNode.Let let when ContainsPropagate(let.Value):
            {
                // The value contains a propagate — emit it as statements
                if (let.Value is IrNode.Propagate prop)
                    EmitPropagateBinding(prop, let.VarName, funcReturnType);
                else
                    EmitLine($"var {SanitizeParam(let.VarName)} = {EmitExpr(let.Value)};");
                _localBindings.Add(let.VarName);
                EmitStatementsBody(let.Body, funcReturnType);
                break;
            }
            case IrNode.Let let:
            {
                EmitLine($"var {SanitizeParam(let.VarName)} = {EmitExpr(let.Value)};");
                _localBindings.Add(let.VarName);
                EmitStatementsBody(let.Body, funcReturnType);
                break;
            }
            case IrNode.If @if when ContainsPropagate(@if):
            {
                EmitLine($"if ({EmitExpr(@if.Condition)})");
                EmitLine("{");
                _indent++;
                EmitStatementsBody(@if.Then, funcReturnType);
                _indent--;
                EmitLine("}");
                EmitLine("else");
                EmitLine("{");
                _indent++;
                EmitStatementsBody(@if.Else, funcReturnType);
                _indent--;
                EmitLine("}");
                break;
            }
            case IrNode.Throw:
                EmitLine($"{EmitExpr(body)};");
                break;
            default:
                if (funcReturnType == ZType.Unit)
                    EmitLine($"{EmitExpr(body)};");
                else
                    EmitLine($"return {EmitExpr(body)};");
                break;
        }
    }

    private void EmitPropagateBinding(IrNode.Propagate prop, string varName, ZType funcReturnType)
    {
        var id = _propagateCounter++;
        var innerExpr = EmitExpr(prop.Expr);
        var resultType = prop.ResultType;

        // Extract type args from the inner result type (for casting Ok)
        var resultTypeArgs = "";
        if (resultType is ZType.ZNamedType { Name: "Result", TypeArgs: [var okT, var errT] })
            resultTypeArgs = $"<{TypeToCs(okT)}, {TypeToCs(errT)}>";

        // Extract type args from the function return type (for constructing Err)
        var funcTypeArgs = "";
        if (funcReturnType is ZType.ZNamedType { Name: "Result", TypeArgs: [var fOkT, var fErrT] })
            funcTypeArgs = $"<{TypeToCs(fOkT)}, {TypeToCs(fErrT)}>";

        var qErr = QualifyType("Err");
        var qOk = QualifyType("Ok");
        EmitLine($"var __r{id} = {innerExpr};");
        EmitLine($"if (__r{id} is {qErr}{resultTypeArgs} __err{id})");
        EmitLine($"    return new {qErr}{funcTypeArgs}(__err{id}.{Sanitize("error")});");
        EmitLine($"var {SanitizeParam(varName)} = (({qOk}{resultTypeArgs})__r{id}).{Sanitize("value")};");
        _localBindings.Add(varName);
    }

    private void EmitTypeDeclarationsInline(IrNode node)
    {
        // When in a module context, type declarations are emitted inside the module class
        if (isModule) return;

        if (node is IrNode.Seq seq)
            foreach (var child in seq.Nodes)
                if (child is IrNode.RecordDecl rec)
                {
                    Log.Debug("CSharpEmitter: emitting record {RecordName}", rec.Name);
                    EmitLine(EmitRecordDecl(rec));
                    EmitLine();
                }
                else if (child is IrNode.UnionDecl union)
                {
                    Log.Debug("CSharpEmitter: emitting union {UnionName} with {CaseCount} cases",
                        union.Name, union.Cases.Count);
                    EmitLine(EmitUnionDecl(union));
                    EmitLine();
                }
                else if (child is IrNode.ClassDecl classDecl)
                {
                    EmitClassDecl(classDecl);
                    EmitLine();
                }
                else if (child is IrNode.InterfaceDecl ifaceDecl)
                {
                    EmitInterfaceDecl(ifaceDecl);
                    EmitLine();
                }
    }

    private string EmitRecordDecl(IrNode.RecordDecl rec)
    {
        var sb = new StringBuilder();
        if (rec.Attributes is { Count: > 0 })
            foreach (var attr in rec.Attributes)
                sb.AppendLine(FormatAttribute(attr));
        var typeParams = rec.TypeParams.Count > 0
            ? $"<{string.Join(", ", rec.TypeParams)}>"
            : "";
        var fields = string.Join(", ",
            rec.Fields.Select(f =>
            {
                var fieldAttrs = FormatFieldAttributes(f.Attributes);
                return $"{fieldAttrs}{TypeToCs(f.Type)} {Sanitize(f.Name)}";
            }));
        var whereClause = FormatWhereConstraints(rec.TypeParamConstraints);
        sb.Append($"public sealed record {Sanitize(rec.Name)}{typeParams}({fields}){whereClause};");
        return sb.ToString();
    }

    private string EmitUnionDecl(IrNode.UnionDecl union)
    {
        var typeParams = union.TypeParams.Count > 0
            ? $"<{string.Join(", ", union.TypeParams)}>"
            : "";
        var sb = new StringBuilder();
        if (union.Attributes is { Count: > 0 })
            foreach (var attr in union.Attributes)
                sb.AppendLine(FormatAttribute(attr));
        var whereClause = FormatWhereConstraints(union.TypeParamConstraints);
        sb.AppendLine($"public abstract record {Sanitize(union.Name)}{typeParams}{whereClause};");
        foreach (var c in union.Cases)
        {
            var fields = c.Fields.Count > 0
                ? $"({string.Join(", ", c.Fields.Select(f => $"{TypeToCs(f.Type)} {Sanitize(f.Name)}"))})"
                : "()";
            sb.AppendLine(
                $"public sealed record {Sanitize(c.Name)}{typeParams}{fields} : {Sanitize(union.Name)}{typeParams};");
        }

        return sb.ToString();
    }

    private string EmitObjectExpr(IrNode.ObjectExpr n)
    {
        var className = $"__Object_{_objectCounter++}";

        // Find captured variables: vars referenced in method bodies that aren't method params
        var captured = new List<string>();
        foreach (var method in n.Methods)
        {
            var paramNames = new HashSet<string>(method.Params.Select(p => p.Name));
            CollectCapturedVars(method.Body, paramNames, captured);
        }

        captured = captured.Distinct().ToList();

        _objectClasses.Add((className, n, captured));

        if (captured.Count == 0)
            return $"new {className}()";

        var args = string.Join(", ", captured.Select(SanitizeParam));
        return $"new {className}({args})";
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

    private void EmitClassDecl(IrNode.ClassDecl classDecl)
    {
        Log.Debug("CSharpEmitter: emitting class declaration {ClassName}", classDecl.Name);
        _currentTypeParams = classDecl.TypeParams.Count > 0
            ? new HashSet<string>(classDecl.TypeParams)
            : null;

        if (classDecl.Attributes is { Count: > 0 })
            foreach (var attr in classDecl.Attributes)
                EmitLine(FormatAttribute(attr));

        var typeParams = classDecl.TypeParams.Count > 0
            ? $"<{string.Join(", ", classDecl.TypeParams)}>"
            : "";

        // Build inheritance list: base class first (if any), then interfaces
        var baseList = new List<string>();
        if (classDecl.BaseClassName is not null)
            baseList.Add(Sanitize(classDecl.BaseClassName));
        baseList.AddRange(classDecl.InterfaceNames);
        var inheritance = baseList.Count > 0 ? $" : {string.Join(", ", baseList)}" : "";

        var sealedModifier = classDecl.IsOpen ? "" : "sealed ";
        var whereClause = FormatWhereConstraints(classDecl.TypeParamConstraints);
        EmitLine($"public {sealedModifier}class {Sanitize(classDecl.Name)}{typeParams}{inheritance}{whereClause}");
        EmitLine("{");
        _indent++;

        // Collect inherited fields and method names for override detection
        var inheritedFields = GetEmittedInheritedFields(classDecl.BaseClassName);
        var inheritedMethodNames = GetEmittedInheritedMethodNames(classDecl.BaseClassName);

        // Properties (readonly) — only own fields, not inherited
        foreach (var field in classDecl.Fields)
            EmitLine($"public {TypeToCs(field.Type)} {Sanitize(field.Name)} {{ get; }}");

        EmitLine();

        // Constructor
        if (classDecl.Constructor is { } ctor)
        {
            // Explicit constructor
            var ctorParams = string.Join(", ",
                ctor.Params.Select(p => $"{TypeToCs(p.Type)} {SanitizeParam(p.Name)}"));
            var baseCall = ctor.SuperArgs is not null
                ? $" : base({string.Join(", ", ctor.SuperArgs.Select(EmitExpr))})"
                : "";
            EmitLine($"public {Sanitize(classDecl.Name)}({ctorParams}){baseCall}");
            EmitLine("{");
            _indent++;
            foreach (var expr in ctor.BodyExprs)
                EmitLine($"{EmitExpr(expr)};");
            foreach (var (fieldName, value) in ctor.FieldSets)
                EmitLine($"this.{Sanitize(fieldName)} = {EmitExpr(value)};");
            _indent--;
            EmitLine("}");
        }
        else
        {
            // Auto-generated constructor
            var allParams = new List<string>();
            foreach (var f in inheritedFields)
                allParams.Add($"{TypeToCs(f.Type)} {Sanitize(f.Name)}");
            foreach (var f in classDecl.Fields)
                allParams.Add($"{TypeToCs(f.Type)} {Sanitize(f.Name)}");
            var ctorParams = string.Join(", ", allParams);

            var baseCall = inheritedFields.Count > 0
                ? $" : base({string.Join(", ", inheritedFields.Select(f => Sanitize(f.Name)))})"
                : "";
            EmitLine($"public {Sanitize(classDecl.Name)}({ctorParams}){baseCall}");
            EmitLine("{");
            _indent++;
            foreach (var field in classDecl.Fields)
                EmitLine($"this.{Sanitize(field.Name)} = {Sanitize(field.Name)};");
            _indent--;
            EmitLine("}");
        }

        // Methods
        _currentClassFields = new HashSet<string>(classDecl.Fields.Select(f => f.Name));
        // Include inherited fields so they resolve to this.FieldName
        foreach (var f in inheritedFields)
            _currentClassFields.Add(f.Name);

        foreach (var method in classDecl.Methods)
        {
            _currentClassLocals = new HashSet<string>(method.Params.Select(p => p.Name));
            EmitLine();
            if (method.Attributes is { Count: > 0 })
                foreach (var attr in method.Attributes)
                    EmitLine(FormatAttribute(attr));
            var retTypeStr = ReturnTypeToCs(method.ReturnType);
            var parms = string.Join(", ",
                method.Params.Select(p => $"{TypeToCs(p.Type)} {SanitizeParam(p.Name)}"));

            // Determine virtual/override modifiers
            var isOverride = inheritedMethodNames.Contains(method.Name);
            var methodModifier = isOverride ? "override " : (classDecl.IsOpen ? "virtual " : "");

            EmitLine($"public {methodModifier}{retTypeStr} {Sanitize(method.Name)}({parms})");
            EmitLine("{");
            _indent++;
            if (method.ReturnType == ZType.Unit)
                EmitLine($"{EmitExpr(method.Body)};");
            else
                EmitLine($"return {EmitExpr(method.Body)};");
            _indent--;
            EmitLine("}");
            _currentClassLocals = null;
        }

        _currentClassFields = null;

        _indent--;
        EmitLine("}");
        _currentTypeParams = null;

        // Store class info for future subclasses
        _emittedClassInfos[classDecl.Name] = new EmittedClassInfo(
            classDecl.IsOpen,
            classDecl.BaseClassName,
            classDecl.Fields,
            classDecl.Methods.Select(m => m.Name).ToList());
    }

    private string EmitSuperMethodCall(IrNode.SuperMethodCall n)
    {
        var args = string.Join(", ", n.Args.Select(EmitExpr));
        return n.Args.Count > 0
            ? $"base.{Sanitize(n.MethodName)}({args})"
            : $"base.{Sanitize(n.MethodName)}()";
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


    private void EmitInterfaceDecl(IrNode.InterfaceDecl ifaceDecl)
    {
        Log.Debug("CSharpEmitter: emitting interface {InterfaceName}", ifaceDecl.Name);
        _currentTypeParams = ifaceDecl.TypeParams.Count > 0
            ? new HashSet<string>(ifaceDecl.TypeParams)
            : null;

        if (ifaceDecl.Attributes is { Count: > 0 })
            foreach (var attr in ifaceDecl.Attributes)
                EmitLine(FormatAttribute(attr));

        var typeParams = ifaceDecl.TypeParams.Count > 0
            ? $"<{string.Join(", ", ifaceDecl.TypeParams)}>"
            : "";
        var baseInterfaces = ifaceDecl.BaseInterfaceNames.Count > 0
            ? $" : {string.Join(", ", ifaceDecl.BaseInterfaceNames)}"
            : "";
        var whereClause = FormatWhereConstraints(ifaceDecl.TypeParamConstraints);
        EmitLine($"public interface {Sanitize(ifaceDecl.Name)}{typeParams}{baseInterfaces}{whereClause}");
        EmitLine("{");
        _indent++;

        foreach (var method in ifaceDecl.Methods)
        {
            var retTypeStr = ReturnTypeToCs(method.ReturnType);
            var parms = string.Join(", ",
                method.Params.Select(p => $"{TypeToCs(p.Type)} {SanitizeParam(p.Name)}"));
            EmitLine($"{retTypeStr} {Sanitize(method.Name)}({parms});");
        }

        _indent--;
        EmitLine("}");
        _currentTypeParams = null;
    }


    private void EmitObjectClasses()
    {
        Log.Debug("CSharpEmitter: emitting {ObjectClassCount} object classes", _objectClasses.Count);
        foreach (var (className, expr, captured) in _objectClasses)
        {
            // Build inheritance list: base class first, then interfaces
            var baseList = new List<string>();
            if (expr.BaseClassName is not null)
                baseList.Add(Sanitize(expr.BaseClassName));
            baseList.AddRange(expr.InterfaceNames);
            var inheritance = string.Join(", ", baseList);
            EmitLine($"private sealed class {className} : {inheritance}");
            EmitLine("{");
            _indent++;

            // Fields for captured variables
            foreach (var cap in captured) EmitLine($"private readonly object {Sanitize(cap)}_field;");

            // Determine inherited method names for override detection
            var inheritedMethodNames = GetEmittedInheritedMethodNames(expr.BaseClassName);

            // Constructor
            if (captured.Count > 0 || expr.Constructor is not null)
            {
                var ctorParams = string.Join(", ", captured.Select(c => $"object {SanitizeParam(c)}_param"));

                // Build base call from explicit constructor super args or default parameterless
                var baseCall = "";
                if (expr.Constructor?.SuperArgs is { Count: > 0 } superArgs)
                {
                    var superArgsStr = string.Join(", ", superArgs.Select(EmitExpr));
                    baseCall = $" : base({superArgsStr})";
                }
                else if (expr.BaseClassName is not null)
                {
                    baseCall = " : base()";
                }

                EmitLine($"public {className}({ctorParams}){baseCall}");
                EmitLine("{");
                _indent++;
                foreach (var cap in captured)
                    EmitLine($"this.{Sanitize(cap)}_field = {SanitizeParam(cap)}_param;");
                if (expr.Constructor is { BodyExprs: { Count: > 0 } bodyExprs })
                    foreach (var bodyExpr in bodyExprs)
                        EmitLine($"{EmitExpr(bodyExpr)};");
                _indent--;
                EmitLine("}");
            }
            else if (expr.BaseClassName is not null)
            {
                // No captured vars and no explicit constructor, but has base class — emit parameterless ctor with base()
                EmitLine($"public {className}() : base()");
                EmitLine("{");
                EmitLine("}");
            }

            // Methods
            _currentObjectCapturedFields = new Dictionary<string, string>();
            foreach (var cap in captured)
                _currentObjectCapturedFields[cap] = $"this.{Sanitize(cap)}_field";

            foreach (var method in expr.Methods)
            {
                _currentClassLocals = new HashSet<string>(method.Params.Select(p => p.Name));
                var retTypeStr = TypeToCs(method.ReturnType);
                var parms = string.Join(", ",
                    method.Params.Select(p => $"{TypeToCs(p.Type)} {SanitizeParam(p.Name)}"));
                var isOverride = inheritedMethodNames.Contains(method.Name);
                var modifier = isOverride ? "override " : "";
                EmitLine($"public {modifier}{retTypeStr} {Sanitize(method.Name)}({parms})");
                EmitLine("{");
                _indent++;
                if (method.ReturnType == ZType.Unit)
                    EmitLine($"{EmitExpr(method.Body)};");
                else
                    EmitLine($"return {EmitExpr(method.Body)};");
                _indent--;
                EmitLine("}");
                _currentClassLocals = null;
            }

            _currentObjectCapturedFields = null;

            _indent--;
            EmitLine("}");
            EmitLine();
        }
    }

    private static string FormatAttribute(IrAttribute attr)
    {
        var args = new List<string>();
        foreach (var arg in attr.PositionalArgs)
            args.Add(FormatAttributeValue(arg));
        foreach (var (name, value) in attr.NamedArgs)
            args.Add($"{name} = {FormatAttributeValue(value)}");
        return args.Count > 0 ? $"[{attr.Name}({string.Join(", ", args)})]" : $"[{attr.Name}]";
    }

    private static string FormatAttributeValue(object value)
    {
        return value switch
        {
            string s => $"\"{EscapeString(s)}\"",
            bool b => b ? "true" : "false",
            int i => i.ToString(),
            float f => $"{f.ToString(CultureInfo.InvariantCulture)}f",
            _ => value.ToString() ?? ""
        };
    }

    private void EmitAttributes(IReadOnlyList<IrAttribute>? attrs)
    {
        if (attrs is null) return;
        foreach (var attr in attrs)
            EmitLine(FormatAttribute(attr));
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
            ZType.ZFuncType ft when ft.Return is ZType.ZPrimitiveType { Kind: PrimitiveKind.Unit } &&
                                    ft.Params.Count == 0
                => "System.Action",
            ZType.ZFuncType ft when ft.Return is ZType.ZPrimitiveType { Kind: PrimitiveKind.Unit }
                => $"System.Action<{string.Join(", ", ft.Params.Select(TypeToCs))}>",
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
            ZType.ZNamedType { Name: "Map", TypeArgs: [var k, var v] } =>
                $"System.Collections.Immutable.ImmutableDictionary<{TypeToCs(k)}, {TypeToCs(v)}>",
            ZType.ZNamedType { Name: "Mutable-Map", TypeArgs: [var mmK, var mmV] } =>
                $"System.Collections.Generic.Dictionary<{TypeToCs(mmK)}, {TypeToCs(mmV)}>",
            ZType.ZNamedType { Name: "Task", TypeArgs: [] } =>
                "System.Threading.Tasks.Task",
            ZType.ZNamedType { Name: "Task", TypeArgs: [var taskT] } =>
                $"System.Threading.Tasks.Task<{TypeToCs(taskT)}>",
            ZType.ZNamedType nt when nt.TypeArgs.Count > 0 =>
                $"{QualifyType(nt.Name)}<{string.Join(", ", nt.TypeArgs.Select(TypeToCs))}>",
            ZType.ZNamedType nt when IsUnresolvedTypeVariable(nt.Name) =>
                WarnAndReturn($"Unresolved type variable '{nt.Name}' from annotation, using 'object'", "object"),
            ZType.ZNamedType nt => QualifyType(nt.Name),
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
        if (CSharpKeywords.Contains(sanitized))
            return $"@{sanitized}";
        return sanitized;
    }

    private static string SanitizeParam(string name)
    {
        var sanitized = NameConverter.SanitizeParameter(name);
        if (CSharpKeywords.Contains(sanitized))
            return $"@{sanitized}";
        return sanitized;
    }

    private static string EscapeString(string s)
    {
        return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r")
            .Replace("\t", "\\t");
    }

    private void EmitLine(string line = "")
    {
        if (string.IsNullOrEmpty(line))
        {
            _sb.AppendLine();
        }
        else
        {
            // Handle multiline strings by indenting each line
            var lines = line.Split('\n');
            foreach (var l in lines)
            {
                var trimmed = l.TrimEnd('\r');
                if (string.IsNullOrEmpty(trimmed))
                    _sb.AppendLine();
                else
                {
                    _sb.Append(new string(' ', _indent * 4));
                    _sb.AppendLine(trimmed);
                }
            }
        }
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
}
