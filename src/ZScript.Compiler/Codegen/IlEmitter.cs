namespace ZScript.Compiler.Codegen;

using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using ZScript.Compiler.Diagnostics;
using ZScript.Compiler.Ir;
using ZScript.Compiler.Types;

/// <summary>
/// Emits .NET IL using PersistedAssemblyBuilder (.NET 9+).
/// </summary>
public sealed class IlEmitter(string assemblyName, DiagnosticBag diagnostics, string className = "Program")
{
    public bool HasEntryPoint { get; private set; }

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

        // Name parameters
        for (int i = 0; i < func.Params.Count; i++)
            methodBuilder.DefineParameter(i + 1, ParameterAttributes.None, func.Params[i].Name);

        var il = methodBuilder.GetILGenerator();
        var locals = new Dictionary<string, LocalBuilder>();
        EmitNode(func.Body, il, func.Params, locals);
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
        // For now, handle calls to named functions (Var targets)
        if (call.Function is IrNode.Var v)
        {
            // Emit arguments first
            foreach (var arg in call.Args)
                EmitNode(arg, il, outerParams, locals);

            // The function should be a static method on the same type — emit as a call
            // We can't resolve it at emit time since the type isn't created yet,
            // so we fall through to the error case for now
        }

        diagnostics.Error($"IL emission not implemented for Call with {call.Function.GetType().Name} target", SourceSpan.None);
        il.Emit(OpCodes.Ldc_I4_0);
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
