namespace ZScript.Compiler.Codegen;

using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using ZScript.Compiler.Diagnostics;
using ZScript.Compiler.Ir;
using ZScript.Compiler.Types;
using ZScript.Runtime;

/// <summary>
/// Emits .NET IL using PersistedAssemblyBuilder (.NET 9+).
/// </summary>
public sealed class IlEmitter(string assemblyName, DiagnosticBag diagnostics, string className = "Program", IReadOnlyList<string>? clrUsings = null)
{
    public bool HasEntryPoint { get; private set; }
    public IReadOnlyList<string> ClrUsings { get; } = clrUsings ?? [];

    private readonly Dictionary<string, MethodBuilder> _methods = new();
    private ZType? _currentFuncReturnType;

    public byte[]? Emit(IrNode node)
    {
        var asmName = new AssemblyName(assemblyName);
        var coreAssembly = Assembly.Load("System.Runtime");
        var asmBuilder = new PersistedAssemblyBuilder(asmName, coreAssembly);
        var moduleBuilder = asmBuilder.DefineDynamicModule(assemblyName);
        var typeBuilder = moduleBuilder.DefineType(
            $"{assemblyName}.{className}",
            TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);

        var mainStatements = new List<IrNode>();

        if (node is IrNode.Seq seq)
        {
            // First pass: define type declarations
            foreach (var child in seq.Nodes)
            {
                if (child is IrNode.RecordDecl or IrNode.UnionDecl)
                    DefineTypeDecl(child, moduleBuilder);
            }

            // Second pass: emit functions
            foreach (var child in seq.Nodes)
            {
                if (child is IrNode.FuncDef func)
                    EmitFuncDef(func, typeBuilder);
            }

            // Third pass: collect top-level statements for Main()
            foreach (var child in seq.Nodes)
                CollectTopLevel(child, mainStatements);
        }
        else if (node is IrNode.FuncDef singleFunc)
        {
            EmitFuncDef(singleFunc, typeBuilder);
        }
        else
        {
            CollectTopLevel(node, mainStatements);
        }

        // Emit Main() if there are top-level statements
        MethodBuilder? mainMethod = null;
        if (mainStatements.Count > 0)
        {
            mainMethod = typeBuilder.DefineMethod("Main",
                MethodAttributes.Public | MethodAttributes.Static,
                typeof(void), Type.EmptyTypes);
            var mainIl = mainMethod.GetILGenerator();
            var locals = new Dictionary<string, LocalBuilder>();
            foreach (var stmt in mainStatements)
            {
                EmitNode(stmt, mainIl, [], locals);
                // Pop return value if non-void
                if (stmt.Type is not null and not ZType.ZPrimitiveType { Kind: PrimitiveKind.Unit })
                    mainIl.Emit(OpCodes.Pop);
            }
            mainIl.Emit(OpCodes.Ret);
            HasEntryPoint = true;
        }

        typeBuilder.CreateType();

        if (mainMethod is not null)
        {
            // Build exe with entry point
            var metadataBuilder = asmBuilder.GenerateMetadata(out var ilStream, out var fieldData);
            int rowNumber = mainMethod.MetadataToken & 0x00FFFFFF;
            var entryPointHandle = MetadataTokens.MethodDefinitionHandle(rowNumber);
            var peBuilder = new ManagedPEBuilder(
                new PEHeaderBuilder(imageCharacteristics: Characteristics.ExecutableImage),
                new MetadataRootBuilder(metadataBuilder),
                ilStream,
                entryPoint: entryPointHandle);
            var blobBuilder = new BlobBuilder();
            peBuilder.Serialize(blobBuilder);
            using var ms = new MemoryStream();
            blobBuilder.WriteContentTo(ms);
            return ms.ToArray();
        }
        else
        {
            // Save as dll (no entry point)
            using var ms = new MemoryStream();
            asmBuilder.Save(ms);
            return ms.ToArray();
        }
    }

    private static void CollectTopLevel(IrNode node, List<IrNode> mainStatements)
    {
        switch (node)
        {
            case IrNode.FuncDef:
            case IrNode.RecordDecl:
            case IrNode.UnionDecl:
                break;
            case IrNode.Let let:
                // The entire let (binding + body) becomes a main statement
                if (let.Body is not IrNode.UnitConst)
                    mainStatements.Add(let);
                break;
            case IrNode.ClrCall:
            case IrNode.Call:
                mainStatements.Add(node);
                break;
        }
    }

    private void DefineTypeDecl(IrNode node, ModuleBuilder module)
    {
        // Type declarations will be implemented in Phase 8
        // For now, just skip
    }

    private void EmitFuncDef(IrNode.FuncDef func, TypeBuilder typeBuilder)
    {
        var paramTypes = func.Params.Select(p => IlTypeMapper.MapToClr(p.Type)).ToArray();
        var returnType = IlTypeMapper.MapToClr(func.ReturnType);

        var methodBuilder = typeBuilder.DefineMethod(
            func.Name,
            MethodAttributes.Public | MethodAttributes.Static,
            returnType,
            paramTypes);

        _methods[func.Name] = methodBuilder;

        // Name parameters
        for (int i = 0; i < func.Params.Count; i++)
            methodBuilder.DefineParameter(i + 1, ParameterAttributes.None, func.Params[i].Name);

        var il = methodBuilder.GetILGenerator();
        var locals = new Dictionary<string, LocalBuilder>();

        _currentFuncReturnType = func.ReturnType;
        EmitNode(func.Body, il, func.Params, locals);
        _currentFuncReturnType = null;

        il.Emit(OpCodes.Ret);
    }

    private void EmitNode(IrNode node, ILGenerator il, IReadOnlyList<IrParam> outerParams,
        Dictionary<string, LocalBuilder> locals)
    {
        switch (node)
        {
            case IrNode.IntConst n:
                il.Emit(OpCodes.Ldc_I4, n.Value);
                break;

            case IrNode.FloatConst n:
                il.Emit(OpCodes.Ldc_R4, n.Value);
                break;

            case IrNode.BoolConst n:
                il.Emit(n.Value ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0);
                break;

            case IrNode.StringConst n:
                il.Emit(OpCodes.Ldstr, n.Value);
                break;

            case IrNode.UnitConst:
                break;

            case IrNode.Var v:
                EmitLoadVar(v.Name, il, outerParams, locals);
                break;

            case IrNode.BinOp binop:
                EmitNode(binop.Left, il, outerParams, locals);
                EmitNode(binop.Right, il, outerParams, locals);
                EmitBinaryOp(binop.Op, il);
                break;

            case IrNode.UnaryOp unary:
                EmitNode(unary.Operand, il, outerParams, locals);
                EmitUnaryOp(unary.Op, il);
                break;

            case IrNode.If @if:
                var elseLabel = il.DefineLabel();
                var endLabel = il.DefineLabel();
                EmitNode(@if.Condition, il, outerParams, locals);
                il.Emit(OpCodes.Brfalse, elseLabel);
                EmitNode(@if.Then, il, outerParams, locals);
                il.Emit(OpCodes.Br, endLabel);
                il.MarkLabel(elseLabel);
                EmitNode(@if.Else, il, outerParams, locals);
                il.MarkLabel(endLabel);
                break;

            case IrNode.Let let:
                var local = il.DeclareLocal(IlTypeMapper.MapToClr(let.Value.Type));
                EmitNode(let.Value, il, outerParams, locals);
                il.Emit(OpCodes.Stloc, local);
                locals[let.VarName] = local;
                EmitNode(let.Body, il, outerParams, locals);
                break;

            case IrNode.ClrCall clrCall:
                EmitClrCall(clrCall, il, outerParams, locals);
                break;

            case IrNode.Call call:
                EmitCall(call, il, outerParams, locals);
                break;

            case IrNode.BuiltinCtorCall ctorCall:
                EmitBuiltinCtorCall(ctorCall, il, outerParams, locals);
                break;

            case IrNode.Match match:
                EmitMatch(match, il, outerParams, locals);
                break;

            case IrNode.TryCatch tryCatch:
                EmitTryCatch(tryCatch, il, outerParams, locals);
                break;

            case IrNode.Propagate propagate:
                EmitPropagate(propagate, il, outerParams, locals);
                break;

            default:
                diagnostics.Error($"IL emission not implemented for {node.GetType().Name}", SourceSpan.None);
                il.Emit(OpCodes.Ldc_I4_0); // push something on the stack
                break;
        }
    }

    private void EmitClrCall(IrNode.ClrCall clrCall, ILGenerator il, IReadOnlyList<IrParam> outerParams,
        Dictionary<string, LocalBuilder> locals)
    {
        // Emit arguments
        foreach (var arg in clrCall.Args)
            EmitNode(arg, il, outerParams, locals);

        // Resolve the CLR method — search loaded assemblies since Type.GetType
        // only finds types in the calling assembly or System.Private.CoreLib
        var type = ResolveClrType(clrCall.QualifiedTypeName);
        if (type is null)
        {
            diagnostics.Error($"CLR type '{clrCall.QualifiedTypeName}' not found", SourceSpan.None);
            il.Emit(OpCodes.Ldc_I4_0);
            return;
        }

        var argTypes = clrCall.Args.Select(a => IlTypeMapper.MapToClr(a.Type)).ToArray();
        var method = type.GetMethod(clrCall.MethodName, argTypes);
        if (method is null)
        {
            diagnostics.Error($"CLR method '{clrCall.QualifiedTypeName}.{clrCall.MethodName}' not found", SourceSpan.None);
            il.Emit(OpCodes.Ldc_I4_0);
            return;
        }

        il.Emit(OpCodes.Call, method);
    }

    private static Type? ResolveClrType(string qualifiedTypeName)
    {
        // Try direct lookup first (works for assembly-qualified names and System.Private.CoreLib types)
        var type = Type.GetType(qualifiedTypeName);
        if (type is not null)
            return type;

        // Search all loaded assemblies
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            type = asm.GetType(qualifiedTypeName);
            if (type is not null)
                return type;
        }

        return null;
    }

    private void EmitCall(IrNode.Call call, ILGenerator il, IReadOnlyList<IrParam> outerParams,
        Dictionary<string, LocalBuilder> locals)
    {
        if (call.Function is IrNode.Var v)
        {
            // Emit arguments first
            foreach (var arg in call.Args)
                EmitNode(arg, il, outerParams, locals);

            if (_methods.TryGetValue(v.Name, out var methodBuilder))
            {
                il.Emit(OpCodes.Call, methodBuilder);
                return;
            }

            diagnostics.Error($"Function '{v.Name}' not found for IL emission", SourceSpan.None);
            il.Emit(OpCodes.Ldc_I4_0);
            return;
        }

        diagnostics.Error($"IL emission not implemented for Call with {call.Function.GetType().Name} target", SourceSpan.None);
        il.Emit(OpCodes.Ldc_I4_0);
    }

    private void EmitBuiltinCtorCall(IrNode.BuiltinCtorCall node, ILGenerator il,
        IReadOnlyList<IrParam> outerParams, Dictionary<string, LocalBuilder> locals)
    {
        if (node.RuntimeTypeName == "ZsError")
        {
            // (Error "msg") -> new ZsError(string)
            foreach (var arg in node.Args)
                EmitNode(arg, il, outerParams, locals);

            var ctor = typeof(ZsError).GetConstructor([typeof(string)])!;
            il.Emit(OpCodes.Newobj, ctor);
            return;
        }

        // Ok, Err, Some, None — nested types inside ZsResult<,> or ZsOption<>
        var typeArgs = node.TypeArgs.Select(IlTypeMapper.MapToClr).ToArray();
        var nestedType = ResolveNestedRuntimeType(node.RuntimeTypeName, node.CaseName!, typeArgs);

        if (nestedType is null)
        {
            diagnostics.Error($"Cannot resolve runtime type {node.RuntimeTypeName}.{node.CaseName}", SourceSpan.None);
            il.Emit(OpCodes.Ldc_I4_0);
            return;
        }

        // Emit constructor arguments
        foreach (var arg in node.Args)
            EmitNode(arg, il, outerParams, locals);

        // Find the constructor
        var argTypes = node.Args.Select(a => IlTypeMapper.MapToClr(a.Type)).ToArray();
        var ctorInfo = nestedType.GetConstructors().FirstOrDefault(c =>
        {
            var p = c.GetParameters();
            return p.Length == argTypes.Length;
        });

        if (ctorInfo is null)
        {
            diagnostics.Error($"Constructor not found for {nestedType}", SourceSpan.None);
            il.Emit(OpCodes.Ldc_I4_0);
            return;
        }

        il.Emit(OpCodes.Newobj, ctorInfo);
    }

    private static Type? ResolveNestedRuntimeType(string runtimeTypeName, string caseName, Type[] typeArgs)
    {
        // Map runtime type name to the open generic type
        Type? openParent = runtimeTypeName switch
        {
            "ZsResult" => typeof(ZsResult<,>),
            "ZsOption" => typeof(ZsOption<>),
            _ => null
        };

        if (openParent is null)
            return null;

        // Close the parent generic type
        var closedParent = openParent.MakeGenericType(typeArgs);

        // Get the nested type (Ok, Err, Some, None)
        var nestedType = closedParent.GetNestedType(caseName);
        return nestedType;
    }

    private void EmitMatch(IrNode.Match match, ILGenerator il, IReadOnlyList<IrParam> outerParams,
        Dictionary<string, LocalBuilder> locals)
    {
        // Store scrutinee in a local
        var scrutineeType = IlTypeMapper.MapToClr(match.Scrutinee.Type);
        var scrutineeLocal = il.DeclareLocal(scrutineeType);
        EmitNode(match.Scrutinee, il, outerParams, locals);
        il.Emit(OpCodes.Stloc, scrutineeLocal);

        var endLabel = il.DefineLabel();
        var armLabels = new Label[match.Arms.Count];
        for (int i = 0; i < match.Arms.Count; i++)
            armLabels[i] = il.DefineLabel();

        // Create a "next arm" label for each arm (the label of the following arm, or a fail label)
        var failLabel = il.DefineLabel();

        for (int i = 0; i < match.Arms.Count; i++)
        {
            il.MarkLabel(armLabels[i]);
            var arm = match.Arms[i];
            var nextLabel = i + 1 < match.Arms.Count ? armLabels[i + 1] : failLabel;

            EmitPatternTest(arm.Pattern, scrutineeLocal, match.Scrutinee.Type, nextLabel, il, outerParams, locals);
            EmitNode(arm.Body, il, outerParams, locals);
            il.Emit(OpCodes.Br, endLabel);
        }

        // Fail: throw InvalidOperationException
        il.MarkLabel(failLabel);
        il.Emit(OpCodes.Ldstr, "Non-exhaustive match");
        var exCtor = typeof(InvalidOperationException).GetConstructor([typeof(string)])!;
        il.Emit(OpCodes.Newobj, exCtor);
        il.Emit(OpCodes.Throw);

        il.MarkLabel(endLabel);
    }

    private void EmitPatternTest(IrPattern pattern, LocalBuilder scrutineeLocal, ZType scrutineeType,
        Label failLabel, ILGenerator il, IReadOnlyList<IrParam> outerParams, Dictionary<string, LocalBuilder> locals)
    {
        switch (pattern)
        {
            case IrPattern.Wildcard:
                // Always matches — no test needed
                break;

            case IrPattern.Variable v:
                // Bind scrutinee to a new local
                var bindLocal = il.DeclareLocal(scrutineeLocal.LocalType);
                il.Emit(OpCodes.Ldloc, scrutineeLocal);
                il.Emit(OpCodes.Stloc, bindLocal);
                locals[v.Name] = bindLocal;
                break;

            case IrPattern.Literal { Value: string s }:
                il.Emit(OpCodes.Ldloc, scrutineeLocal);
                il.Emit(OpCodes.Ldstr, s);
                var strEquals = typeof(string).GetMethod("Equals", BindingFlags.Public | BindingFlags.Static,
                    [typeof(string), typeof(string)])!;
                il.Emit(OpCodes.Call, strEquals);
                il.Emit(OpCodes.Brfalse, failLabel);
                break;

            case IrPattern.Literal { Value: int i }:
                il.Emit(OpCodes.Ldloc, scrutineeLocal);
                il.Emit(OpCodes.Ldc_I4, i);
                il.Emit(OpCodes.Ceq);
                il.Emit(OpCodes.Brfalse, failLabel);
                break;

            case IrPattern.Literal { Value: bool b }:
                il.Emit(OpCodes.Ldloc, scrutineeLocal);
                il.Emit(b ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0);
                il.Emit(OpCodes.Ceq);
                il.Emit(OpCodes.Brfalse, failLabel);
                break;

            case IrPattern.Constructor c:
                EmitConstructorPatternTest(c, scrutineeLocal, scrutineeType, failLabel, il, outerParams, locals);
                break;
        }
    }

    private void EmitConstructorPatternTest(IrPattern.Constructor ctor, LocalBuilder scrutineeLocal,
        ZType scrutineeType, Label failLabel, ILGenerator il, IReadOnlyList<IrParam> outerParams,
        Dictionary<string, LocalBuilder> locals)
    {
        // Resolve the CLR type for this constructor case
        var caseType = ResolveConstructorCaseType(ctor.Name, scrutineeType);
        if (caseType is null)
        {
            diagnostics.Error($"Cannot resolve constructor type '{ctor.Name}' for pattern match", SourceSpan.None);
            return;
        }

        // isinst type test
        il.Emit(OpCodes.Ldloc, scrutineeLocal);
        il.Emit(OpCodes.Isinst, caseType);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Brfalse, failLabel);

        // Store the cast result
        var castLocal = il.DeclareLocal(caseType);
        il.Emit(OpCodes.Stloc, castLocal);

        // Extract fields
        if (ctor.Fields.Count > 0)
        {
            // Get the property name for this case
            var propertyName = ctor.Name switch
            {
                "Ok" or "Some" => "Value",
                "Err" => "Error",
                _ => "Value"
            };

            for (int i = 0; i < ctor.Fields.Count; i++)
            {
                var field = ctor.Fields[i];
                if (field is IrPattern.Variable v)
                {
                    var prop = caseType.GetProperty(propertyName);
                    if (prop is not null)
                    {
                        var getter = prop.GetGetMethod()!;
                        var fieldLocal = il.DeclareLocal(prop.PropertyType);
                        il.Emit(OpCodes.Ldloc, castLocal);
                        il.Emit(OpCodes.Callvirt, getter);
                        il.Emit(OpCodes.Stloc, fieldLocal);
                        locals[v.Name] = fieldLocal;
                    }
                }
                else if (field is IrPattern.Wildcard)
                {
                    // Ignore
                }
            }
        }
        else
        {
            // No fields to extract — pop the dup'd reference we left on stack
            il.Emit(OpCodes.Pop);
        }
    }

    private static Type? ResolveConstructorCaseType(string caseName, ZType scrutineeType)
    {
        return scrutineeType switch
        {
            ZType.ZNamedType { Name: "Result", TypeArgs: [var okT, var errT] } =>
                ResolveNestedRuntimeType("ZsResult", caseName,
                    [IlTypeMapper.MapToClr(okT), IlTypeMapper.MapToClr(errT)]),

            ZType.ZNamedType { Name: "Option", TypeArgs: [var t] } =>
                ResolveNestedRuntimeType("ZsOption", caseName,
                    [IlTypeMapper.MapToClr(t)]),

            _ => null
        };
    }

    private void EmitTryCatch(IrNode.TryCatch node, ILGenerator il, IReadOnlyList<IrParam> outerParams,
        Dictionary<string, LocalBuilder> locals)
    {
        // Extract Ok/Err types from the Result type
        Type okClrType, errClrType, resultClrType;
        if (node.Type is ZType.ZNamedType { Name: "Result", TypeArgs: [var okT, var errT] })
        {
            okClrType = IlTypeMapper.MapToClr(okT);
            errClrType = IlTypeMapper.MapToClr(errT);
            resultClrType = IlTypeMapper.MapToClr(node.Type);
        }
        else
        {
            diagnostics.Error("TryCatch node type is not a Result type", SourceSpan.None);
            il.Emit(OpCodes.Ldc_I4_0);
            return;
        }

        // Declare a local to hold the result (can't leave values on stack across exception boundaries)
        var resultLocal = il.DeclareLocal(resultClrType);

        // Resolve Ok and Err nested types
        var okType = ResolveNestedRuntimeType("ZsResult", "Ok", [okClrType, errClrType]);
        var errType = ResolveNestedRuntimeType("ZsResult", "Err", [okClrType, errClrType]);
        if (okType is null || errType is null)
        {
            diagnostics.Error("Cannot resolve Ok/Err types for TryCatch", SourceSpan.None);
            il.Emit(OpCodes.Ldc_I4_0);
            return;
        }

        var okCtor = okType.GetConstructors().First(c => c.GetParameters().Length == 1);
        var errCtor = errType.GetConstructors().First(c => c.GetParameters().Length == 1);

        // begin try
        il.BeginExceptionBlock();

        // Emit body — evaluates to the "ok" value
        EmitNode(node.Body, il, outerParams, locals);

        // Wrap in Ok
        il.Emit(OpCodes.Newobj, okCtor);
        il.Emit(OpCodes.Stloc, resultLocal);

        // begin catch (Exception)
        il.BeginCatchBlock(typeof(Exception));

        // Stack has the Exception; get its Message
        var getMessage = typeof(Exception).GetProperty("Message")!.GetGetMethod()!;
        il.Emit(OpCodes.Callvirt, getMessage);

        // new ZsError(message)
        var zsErrorCtor = typeof(ZsError).GetConstructor([typeof(string)])!;
        il.Emit(OpCodes.Newobj, zsErrorCtor);

        // new Err(zsError)
        il.Emit(OpCodes.Newobj, errCtor);
        il.Emit(OpCodes.Stloc, resultLocal);

        // end
        il.EndExceptionBlock();

        // Load the result
        il.Emit(OpCodes.Ldloc, resultLocal);
    }

    private void EmitPropagate(IrNode.Propagate node, ILGenerator il, IReadOnlyList<IrParam> outerParams,
        Dictionary<string, LocalBuilder> locals)
    {
        // Emit inner expression (should evaluate to a Result value)
        EmitNode(node.Expr, il, outerParams, locals);

        var resultClrType = IlTypeMapper.MapToClr(node.ResultType);
        var tempLocal = il.DeclareLocal(resultClrType);
        il.Emit(OpCodes.Stloc, tempLocal);

        // Resolve the Err type for the inner result
        Type innerOkClrType, innerErrClrType;
        if (node.ResultType is ZType.ZNamedType { Name: "Result", TypeArgs: [var okT, var errT] })
        {
            innerOkClrType = IlTypeMapper.MapToClr(okT);
            innerErrClrType = IlTypeMapper.MapToClr(errT);
        }
        else
        {
            diagnostics.Error("Propagate expression is not a Result type", SourceSpan.None);
            il.Emit(OpCodes.Ldc_I4_0);
            return;
        }

        var innerErrType = ResolveNestedRuntimeType("ZsResult", "Err", [innerOkClrType, innerErrClrType])!;
        var innerOkType = ResolveNestedRuntimeType("ZsResult", "Ok", [innerOkClrType, innerErrClrType])!;

        // Test: is it Err?
        var okLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, tempLocal);
        il.Emit(OpCodes.Isinst, innerErrType);
        il.Emit(OpCodes.Brfalse, okLabel);

        // It's Err — extract the error and wrap in the function's return Err type, then early return
        il.Emit(OpCodes.Ldloc, tempLocal);
        il.Emit(OpCodes.Castclass, innerErrType);

        // Get .Error property
        var errProp = innerErrType.GetProperty("Error")!.GetGetMethod()!;
        il.Emit(OpCodes.Callvirt, errProp);

        // Wrap in the function's return Err type
        if (_currentFuncReturnType is ZType.ZNamedType { Name: "Result", TypeArgs: [var fOkT, var fErrT] })
        {
            var funcOkClr = IlTypeMapper.MapToClr(fOkT);
            var funcErrClr = IlTypeMapper.MapToClr(fErrT);
            var funcErrType = ResolveNestedRuntimeType("ZsResult", "Err", [funcOkClr, funcErrClr])!;
            var funcErrCtor = funcErrType.GetConstructors().First(c => c.GetParameters().Length == 1);
            il.Emit(OpCodes.Newobj, funcErrCtor);
        }

        il.Emit(OpCodes.Ret); // Early return

        // Ok path — extract Value
        il.MarkLabel(okLabel);
        il.Emit(OpCodes.Ldloc, tempLocal);
        il.Emit(OpCodes.Castclass, innerOkType);
        var valueProp = innerOkType.GetProperty("Value")!.GetGetMethod()!;
        il.Emit(OpCodes.Callvirt, valueProp);
        // Unwrapped value is now on the stack
    }

    private void EmitLoadVar(string name, ILGenerator il, IReadOnlyList<IrParam> outerParams,
        Dictionary<string, LocalBuilder> locals)
    {
        // Check locals first
        if (locals.TryGetValue(name, out var local))
        {
            il.Emit(OpCodes.Ldloc, local);
            return;
        }

        // Then check parameters
        for (int i = 0; i < outerParams.Count; i++)
        {
            if (outerParams[i].Name == name)
            {
                il.Emit(OpCodes.Ldarg, i);
                return;
            }
        }

        diagnostics.Error($"Variable '{name}' not found for IL emission", SourceSpan.None);
        il.Emit(OpCodes.Ldc_I4_0);
    }

    private static void EmitBinaryOp(string op, ILGenerator il)
    {
        switch (op)
        {
            case "+": il.Emit(OpCodes.Add); break;
            case "-": il.Emit(OpCodes.Sub); break;
            case "*": il.Emit(OpCodes.Mul); break;
            case "/": il.Emit(OpCodes.Div); break;
            case "%": il.Emit(OpCodes.Rem); break;
            case "=": il.Emit(OpCodes.Ceq); break;
            case "<": il.Emit(OpCodes.Clt); break;
            case ">": il.Emit(OpCodes.Cgt); break;
            case "!=":
                il.Emit(OpCodes.Ceq);
                il.Emit(OpCodes.Ldc_I4_0);
                il.Emit(OpCodes.Ceq);
                break;
            case "<=":
                il.Emit(OpCodes.Cgt);
                il.Emit(OpCodes.Ldc_I4_0);
                il.Emit(OpCodes.Ceq);
                break;
            case ">=":
                il.Emit(OpCodes.Clt);
                il.Emit(OpCodes.Ldc_I4_0);
                il.Emit(OpCodes.Ceq);
                break;
            case "and": il.Emit(OpCodes.And); break;
            case "or": il.Emit(OpCodes.Or); break;
        }
    }

    private static void EmitUnaryOp(string op, ILGenerator il)
    {
        switch (op)
        {
            case "not":
                il.Emit(OpCodes.Ldc_I4_0);
                il.Emit(OpCodes.Ceq);
                break;
        }
    }
}
