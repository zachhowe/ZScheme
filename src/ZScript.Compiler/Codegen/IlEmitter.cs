namespace ZScript.Compiler.Codegen;

using System.Reflection;
using System.Reflection.Emit;
using ZScript.Compiler.Diagnostics;
using ZScript.Compiler.Ir;
using ZScript.Compiler.Types;

/// <summary>
/// Emits .NET IL using PersistedAssemblyBuilder (.NET 9+).
/// </summary>
public sealed class IlEmitter
{
    private readonly string _assemblyName;
    private readonly DiagnosticBag _diagnostics;

    public IlEmitter(string assemblyName, DiagnosticBag diagnostics)
    {
        _assemblyName = assemblyName;
        _diagnostics = diagnostics;
    }

    public byte[]? Emit(IrNode node)
    {
        var asmName = new AssemblyName(_assemblyName);
        var asmBuilder = new PersistedAssemblyBuilder(asmName, typeof(object).Assembly);
        var moduleBuilder = asmBuilder.DefineDynamicModule(_assemblyName);
        var typeBuilder = moduleBuilder.DefineType(
            $"{_assemblyName}.Program",
            TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);

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
        }
        else if (node is IrNode.FuncDef singleFunc)
        {
            EmitFuncDef(singleFunc, typeBuilder);
        }

        typeBuilder.CreateType();

        // Save to byte array
        using var ms = new MemoryStream();
        asmBuilder.Save(ms);
        return ms.ToArray();
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
        EmitNode(func.Body, il, func.Params);
        il.Emit(OpCodes.Ret);
    }

    private void EmitNode(IrNode node, ILGenerator il, IReadOnlyList<IrParam> outerParams)
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

            case IrNode.Var v:
                EmitLoadVar(v.Name, il, outerParams);
                break;

            case IrNode.BinOp binop:
                EmitNode(binop.Left, il, outerParams);
                EmitNode(binop.Right, il, outerParams);
                EmitBinaryOp(binop.Op, il);
                break;

            case IrNode.UnaryOp unary:
                EmitNode(unary.Operand, il, outerParams);
                EmitUnaryOp(unary.Op, il);
                break;

            case IrNode.If @if:
                var elseLabel = il.DefineLabel();
                var endLabel = il.DefineLabel();
                EmitNode(@if.Condition, il, outerParams);
                il.Emit(OpCodes.Brfalse, elseLabel);
                EmitNode(@if.Then, il, outerParams);
                il.Emit(OpCodes.Br, endLabel);
                il.MarkLabel(elseLabel);
                EmitNode(@if.Else, il, outerParams);
                il.MarkLabel(endLabel);
                break;

            case IrNode.Let let:
                var local = il.DeclareLocal(IlTypeMapper.MapToClr(let.Value.Type));
                EmitNode(let.Value, il, outerParams);
                il.Emit(OpCodes.Stloc, local);
                // Body can reference this local — need local tracking (simplified for now)
                EmitNode(let.Body, il, outerParams);
                break;

            default:
                _diagnostics.Error($"IL emission not implemented for {node.GetType().Name}", SourceSpan.None);
                il.Emit(OpCodes.Ldc_I4_0); // push something on the stack
                break;
        }
    }

    private void EmitLoadVar(string name, ILGenerator il, IReadOnlyList<IrParam> outerParams)
    {
        for (int i = 0; i < outerParams.Count; i++)
        {
            if (outerParams[i].Name == name)
            {
                il.Emit(OpCodes.Ldarg, i);
                return;
            }
        }

        _diagnostics.Error($"Variable '{name}' not found for IL emission", SourceSpan.None);
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
