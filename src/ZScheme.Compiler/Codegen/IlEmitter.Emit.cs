using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.CompilerServices;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using ZScheme.Compiler.Diagnostics;
using ZScheme.Compiler.Ir;
using ZScheme.Compiler.Types;
using FieldAttributes = AsmResolver.PE.DotNet.Metadata.Tables.FieldAttributes;
using MethodAttributes = AsmResolver.PE.DotNet.Metadata.Tables.MethodAttributes;
using MethodSemanticsAttributes = AsmResolver.PE.DotNet.Metadata.Tables.MethodSemanticsAttributes;
using ParameterAttributes = AsmResolver.PE.DotNet.Metadata.Tables.ParameterAttributes;
using TypeAttributes = AsmResolver.PE.DotNet.Metadata.Tables.TypeAttributes;

namespace ZScheme.Compiler.Codegen;

public sealed partial class IlEmitter
{
    public byte[]? Emit(IrNode node)
    {
        // IL requires stack depth 0 at try-block entry. Hoist any with-handlers nested inside
        // compound expressions (binops, calls, etc.) up into let bindings so each try starts
        // with an empty stack.
        node = new WithHandlersHoister().Hoist(node);

        // Async state-machine resume labels also require stack depth 0, otherwise the
        // IsCompleted-fall-through and resume-from-suspend paths arrive at GetResult with
        // mismatched stack heights. Hoist awaits the same way.
        node = new AwaitHoister().Hoist(node);

        Log.Debug(
            "IlEmitter: emitting assembly {AssemblyName}, usings={UsingCount}, searchPaths={SearchPathCount}, importedModules={ImportedModuleCount}",
            assemblyName,
            ClrUsings.Count,
            assemblySearchPaths?.Count ?? 0,
            importedModules?.Count ?? 0
        );
        if (assemblySearchPaths is { Count: > 0 })
            foreach (var sp in assemblySearchPaths)
                Log.Debug("IlEmitter: assembly search path: {Path}", sp);

        var sysRuntimeAsm = Assembly.Load("System.Runtime");
        var corLib = new AssemblyReference("System.Runtime", sysRuntimeAsm.GetName().Version!)
        {
            PublicKeyOrToken = sysRuntimeAsm.GetName().GetPublicKeyToken(),
        };
        _module = new ModuleDefinition(assemblyName + ".dll", corLib);
        var asmDef = new AssemblyDefinition(assemblyName, new Version(1, 0, 0, 0));
        asmDef.Modules.Add(_module);

        _valueTupleType = _module
            .DefaultImporter.ImportType(typeof(ValueTuple))
            .ToTypeSignature(true);

        const TypeAttributes typeAttrs =
            TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed;
        var typeDef = new TypeDefinition(_ilNamespace, className, typeAttrs)
        {
            BaseType = _module.CorLibTypeFactory.Object.ToTypeDefOrRef(),
        };
        _module.TopLevelTypes.Add(typeDef);
        _currentTypeDefinition = typeDef;

        var mainStatements = new List<IrNode>();

        // Load precompiled assemblies
        if (precompiledAssemblyPaths is { Count: > 0 })
            foreach (var path in precompiledAssemblyPaths)
                LoadPrecompiledAssembly(path);

        // Pass 0: define types and functions from imported modules
        if (importedModules is { Count: > 0 })
        {
            var moduleState =
                new List<(
                    TypeDefinition ModuleType,
                    List<IrNode.Let> LetBindings,
                    IReadOnlyList<IrNode> Defs,
                    List<(IrNode.FuncDef Func, MethodDefinition Method)> Funcs
                )>();

            // Pass 0a: define all types, static fields, and function signatures.
            // Order matters: types and FuncDef signatures must be registered BEFORE
            // class declarations are emitted, so class method bodies can resolve
            // module-level functions and other classes.
            foreach (var (moduleClassName, defs) in importedModules)
            {
                var moduleType = new TypeDefinition(
                    _ilNamespace,
                    moduleClassName,
                    TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed
                )
                {
                    BaseType = _module.CorLibTypeFactory.Object.ToTypeDefOrRef(),
                };
                _module.TopLevelTypes.Add(moduleType);

                // Sub-pass 1: type declarations
                foreach (var def in defs)
                    if (def is IrNode.RecordDecl or IrNode.UnionDecl or IrNode.InterfaceDecl)
                        DefineTypeDecl(def, moduleType);

                // Sub-pass 2: static field bindings (let)
                var moduleLetBindings = new List<IrNode.Let>();
                foreach (var def in defs)
                    if (def is IrNode.Let let)
                    {
                        var fieldType = MapToClr(let.Value.Type);
                        var fd = new FieldDefinition(
                            let.VarName,
                            FieldAttributes.Public | FieldAttributes.Static,
                            new FieldSignature(fieldType)
                        );
                        moduleType.Fields.Add(fd);
                        _staticFields[let.VarName] = fd;
                        moduleLetBindings.Add(let);
                    }

                // Sub-pass 3: register function signatures (so class method bodies can call them).
                // Collect (func, methodDef) pairs: the per-module methodDef is needed in Pass 0b
                // because _methods is keyed only by sanitized name and collides across modules
                // when multiple modules define a function with the same name (e.g. `clampf`).
                var moduleFuncs = new List<(IrNode.FuncDef Func, MethodDefinition Method)>();
                foreach (var def in defs)
                    if (def is IrNode.FuncDef func)
                    {
                        var methodDef = RegisterFuncSignature(func, moduleType);
                        moduleFuncs.Add((func, methodDef));
                    }

                // Sub-pass 4: emit class declarations (their method bodies can now resolve all
                // module-level functions and static fields)
                foreach (var def in defs)
                    if (def is IrNode.ClassDecl classDecl)
                        EmitClassDecl(classDecl);

                Log.Debug(
                    "IlEmitter: Pass 0a complete for {ModuleClassName}: {LetCount} let bindings, {FuncCount} functions",
                    moduleClassName,
                    moduleLetBindings.Count,
                    defs.Count(d => d is IrNode.FuncDef)
                );
                moduleState.Add((moduleType, moduleLetBindings, defs, moduleFuncs));
            }

            // Pass 0b: emit all function bodies and .cctor bodies.
            // Set _currentTypeDefinition to each imported module's type so that lambdas
            // and closure types lifted out of those bodies are nested inside the right
            // module class. Otherwise they end up nested in the main module type, and
            // the imported function's call site sees a NestedPrivate member from a
            // different declaring type — which fails IL verification ("Method/Field
            // is not visible") and trips InvalidProgramException at runtime.
            var savedMainTypeDef = _currentTypeDefinition;
            foreach (var (moduleType, moduleLetBindings, defs, moduleFuncs) in moduleState)
            {
                _currentTypeDefinition = moduleType;
                foreach (var (func, methodDef) in moduleFuncs)
                    EmitFuncBody(func, methodDef);

                if (moduleLetBindings.Count <= 0)
                    continue;
                var cctor = new MethodDefinition(
                    ".cctor",
                    MethodAttributes.Static
                        | MethodAttributes.Private
                        | MethodAttributes.HideBySig
                        | MethodAttributes.SpecialName
                        | MethodAttributes.RuntimeSpecialName,
                    MethodSignature.CreateStatic(_module.CorLibTypeFactory.Void)
                );
                moduleType.Methods.Add(cctor);
                var body = new CilMethodBody { InitializeLocals = true };
                cctor.MethodBody = body;
                var il = body.Instructions;
                var locals = new Dictionary<string, CilLocalVariable>();
                foreach (var let in moduleLetBindings)
                {
                    EmitNode(let.Value, il, [], locals);
                    il.Add(CilOpCodes.Stsfld, _staticFields[let.VarName]);
                    var local = new CilLocalVariable(MapToClr(let.Value.Type));
                    body.LocalVariables.Add(local);
                    il.Add(CilOpCodes.Ldsfld, _staticFields[let.VarName]);
                    il.Add(CilOpCodes.Stloc, local);
                    locals[let.VarName] = local;
                    if (let.Body is IrNode.UnitConst)
                        continue;

                    EmitNode(let.Body, il, [], locals);
                    if (
                        let.Body.Type
                        is not null
                            and not ZType.ZPrimitiveType { Kind: PrimitiveKind.Unit }
                    )
                        il.Add(CilOpCodes.Pop);
                }

                il.Add(CilOpCodes.Ret);
            }

            _currentTypeDefinition = savedMainTypeDef;
            Log.Debug(
                "IlEmitter: Pass 0b complete, {ModuleCount} imported module bodies emitted",
                moduleState.Count
            );
        }

        switch (node)
        {
            case IrNode.Seq seq:
            {
                foreach (var child in seq.Nodes)
                    if (child is IrNode.RecordDecl or IrNode.UnionDecl or IrNode.InterfaceDecl)
                        DefineTypeDecl(child, isModule ? typeDef : null);

                foreach (var child in seq.Nodes)
                    if (child is IrNode.Let let)
                    {
                        var fieldType = MapToClr(let.Value.Type);
                        var fd = new FieldDefinition(
                            let.VarName,
                            FieldAttributes.Public | FieldAttributes.Static,
                            new FieldSignature(fieldType)
                        );
                        typeDef.Fields.Add(fd);
                        _staticFields[let.VarName] = fd;
                    }

                MethodDefinition? userMainMethod = null;
                foreach (var child in seq.Nodes)
                    if (child is IrNode.FuncDef func)
                    {
                        EmitFuncDef(func, typeDef);
                        if (func.Name == "main")
                            userMainMethod = _methods[Sanitize("main")];
                    }
                    else if (child is IrNode.ClassDecl classDecl)
                    {
                        EmitClassDecl(classDecl);
                    }

                foreach (var child in seq.Nodes)
                    CollectTopLevel(child, mainStatements);
                break;
            }
            case IrNode.FuncDef singleFunc:
                EmitFuncDef(singleFunc, typeDef);
                break;
            default:
                CollectTopLevel(node, mainStatements);
                break;
        }

        Log.Debug(
            "IlEmitter: main type processing complete, {MethodCount} methods registered, {FieldCount} static fields, {MainStatementCount} main statements",
            _methods.Count,
            _staticFields.Count,
            mainStatements.Count
        );

        // Emit static constructor (.cctor)
        if (mainStatements.Count > 0)
        {
            var cctor = new MethodDefinition(
                ".cctor",
                MethodAttributes.Static
                    | MethodAttributes.Private
                    | MethodAttributes.HideBySig
                    | MethodAttributes.SpecialName
                    | MethodAttributes.RuntimeSpecialName,
                MethodSignature.CreateStatic(_module.CorLibTypeFactory.Void)
            );
            typeDef.Methods.Add(cctor);

            var body = new CilMethodBody { InitializeLocals = true };
            cctor.MethodBody = body;
            var il = body.Instructions;
            var locals = new Dictionary<string, CilLocalVariable>();

            foreach (var stmt in mainStatements)
                if (stmt is IrNode.Let let)
                {
                    EmitNode(let.Value, il, [], locals);
                    il.Add(CilOpCodes.Stsfld, _staticFields[let.VarName]);
                    var local = new CilLocalVariable(MapToClr(let.Value.Type));
                    body.LocalVariables.Add(local);
                    il.Add(CilOpCodes.Ldsfld, _staticFields[let.VarName]);
                    il.Add(CilOpCodes.Stloc, local);
                    locals[let.VarName] = local;
                    if (let.Body is IrNode.UnitConst)
                        continue;
                    EmitNode(let.Body, il, [], locals);
                    if (
                        let.Body.Type
                        is not null
                            and not ZType.ZPrimitiveType { Kind: PrimitiveKind.Unit }
                    )
                        il.Add(CilOpCodes.Pop);
                }
                else
                {
                    EmitNode(stmt, il, [], locals);
                    if (
                        stmt.Type
                        is not null
                            and not ZType.ZPrimitiveType { Kind: PrimitiveKind.Unit }
                    )
                        il.Add(CilOpCodes.Pop);
                }

            il.Add(CilOpCodes.Ret);
        }

        // Emit Main(string[] args) wrapper
        if (node is IrNode.Seq seq2)
        {
            MethodDefinition? userMain = null;
            foreach (var child in seq2.Nodes)
                if (child is IrNode.FuncDef { Name: "main" })
                {
                    userMain = _methods[Sanitize("main")];
                    break;
                }

            if (userMain is not null)
            {
                var mainMethod = new MethodDefinition(
                    "Main",
                    MethodAttributes.Public | MethodAttributes.Static,
                    MethodSignature.CreateStatic(
                        _module.CorLibTypeFactory.Int32,
                        [new SzArrayTypeSignature(_module.CorLibTypeFactory.String)]
                    )
                );
                mainMethod.ParameterDefinitions.Add(new ParameterDefinition(1, "args", 0));
                typeDef.Methods.Add(mainMethod);

                var mainBody = new CilMethodBody { InitializeLocals = true };
                mainMethod.MethodBody = mainBody;
                var mainIl = mainBody.Instructions;

                // Only pass args if the user's main function expects a parameter
                if (userMain.Parameters.Count > 0)
                {
                    var createMethod = typeof(ImmutableList)
                        .GetMethods()
                        .First(m =>
                            m.Name == "Create"
                            && m.IsGenericMethodDefinition
                            && m.GetParameters() is [{ ParameterType.IsArray: true }]
                        )
                        .MakeGenericMethod(typeof(string));
                    mainIl.Add(CilOpCodes.Ldarg_0);
                    mainIl.Add(CilOpCodes.Call, _module.DefaultImporter.ImportMethod(createMethod));
                }

                mainIl.Add(CilOpCodes.Call, userMain);
                mainIl.Add(CilOpCodes.Ret);

                HasEntryPoint = true;
                _module.ManagedEntryPointMethod = mainMethod;
                Log.Debug("IlEmitter: entry point Main() emitted");
            }
        }

        if (diagnostics.HasErrors)
            return null;

        // For library compilation with imported module classes, set a generous maxStack
        // instead of computing it (the IL for pattern matching, async, and nullable types
        // in class methods may have stack calculation issues in AsmResolver)
        if (importedModules?.Any(m => m.Definitions.Any(d => d is IrNode.ClassDecl)) == true)
        {
            void SetMaxStack(TypeDefinition td)
            {
                foreach (var md in td.Methods)
                    if (md.CilMethodBody is { } body)
                    {
                        body.ComputeMaxStackOnBuild = false;
                        body.MaxStack = 16;
                    }

                foreach (var nested in td.NestedTypes)
                    SetMaxStack(nested);
            }

            foreach (var td in _module.TopLevelTypes)
                SetMaxStack(td);
        }

        // Workaround for AsmResolver issue where method bodies with > 20 exception handlers
        // serialize a corrupted EH section. The CIL "tiny" exception section header has a
        // 1-byte size field (max 255), and 4 + 12*N <= 255 caps it at N = 20 entries.
        // AsmResolver picks tiny vs fat based on whether any single handler has fat-sized
        // offsets, ignoring the cumulative section size — so when a method has 21+ handlers
        // it still emits a tiny section, the size byte wraps modulo 256, and the runtime reads
        // back zero EH clauses. With no exception handlers registered, `leave` instructions
        // become unprotected and exceptions escape with-handlers as if no try/catch existed.
        //
        // We force fat format by widening one handler's protected region past the IsFat
        // threshold (TryEnd - TryStart >= 255). Once any handler reports IsFat, AsmResolver
        // emits the whole section in fat format and the size overflow disappears.
        ForceFatExceptionSectionsForLargeBodies();

        using var ms = new MemoryStream();
        _module.Write(ms);
        var bytes = ms.ToArray();
        Log.Debug("IlEmitter: emit complete, {ByteCount} bytes", bytes.Length);
        return bytes;
    }

    private void ForceFatExceptionSectionsForLargeBodies()
    {
        const int TinyEhSectionMaxClauses = 20;

        void Visit(TypeDefinition td)
        {
            foreach (var md in td.Methods)
                if (
                    md.CilMethodBody is { } body
                    && body.ExceptionHandlers.Count > TinyEhSectionMaxClauses
                    && !body.ExceptionHandlers.Any(eh => eh.IsFat)
                )
                    PromoteToFatExceptionSection(body);

            foreach (var nested in td.NestedTypes)
                Visit(nested);
        }

        foreach (var td in _module.TopLevelTypes)
            Visit(td);
    }

    private void PromoteToFatExceptionSection(CilMethodBody body)
    {
        // Pad the protected region of an existing handler with reachable, harmless nops so
        // that TryEnd - TryStart >= 255 (the threshold for CilExceptionHandler.IsFat). Once
        // any handler reports IsFat, AsmResolver emits the entire EH section in fat format
        // and the count overflow bug is avoided.
        //
        // We insert padding immediately after TryStart (which is itself a nop emitted by
        // EmitWithHandlers). The padding executes at runtime but is a no-op, so no semantic
        // change. We deliberately do NOT pad the catch handler region: the JIT validates
        // unreachable code inside handlers in ways that can reject the body even when
        // ilverify accepts it.
        const int PaddingBytes = 256;
        var instructions = body.Instructions;
        if (instructions.Count == 0 || body.ExceptionHandlers.Count == 0)
            return;

        instructions.CalculateOffsets();

        CilExceptionHandler? anchorHandler = null;
        CilInstruction? tryStartInstruction = null;
        foreach (var eh in body.ExceptionHandlers)
        {
            if (eh.TryStart is null || eh.TryEnd is null)
                continue;
            var startIns = instructions.FirstOrDefault(i => i.Offset == eh.TryStart.Offset);
            if (startIns is null)
                continue;
            anchorHandler = eh;
            tryStartInstruction = startIns;
            break;
        }

        if (anchorHandler is null || tryStartInstruction is null)
            return;

        var insertIndex = instructions.IndexOf(tryStartInstruction) + 1;
        if (insertIndex <= 0)
            return;

        for (var i = 0; i < PaddingBytes; i++)
            instructions.Insert(insertIndex, new CilInstruction(CilOpCodes.Nop));

        instructions.CalculateOffsets();

        Log.Debug(
            "IlEmitter: padded try region by {Bytes} bytes in method {Method} to force fat EH section",
            PaddingBytes,
            body.Owner?.Name
        );
    }

    private void EmitUnionCaseEquals(TypeDefinition caseType, List<FieldDefinition> fields)
    {
        var method = new MethodDefinition(
            "Equals",
            MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig,
            MethodSignature.CreateInstance(
                _module.CorLibTypeFactory.Boolean,
                [_module.CorLibTypeFactory.Object]
            )
        );
        method.ParameterDefinitions.Add(new ParameterDefinition(1, "obj", 0));
        caseType.Methods.Add(method);

        var body = new CilMethodBody { InitializeLocals = true };
        method.MethodBody = body;
        var il = body.Instructions;

        var getType = _module.DefaultImporter.ImportMethod(typeof(object).GetMethod("GetType")!);
        var typeEquality = _module.DefaultImporter.ImportMethod(
            typeof(Type).GetMethod("op_Equality", [typeof(Type), typeof(Type)])!
        );
        var returnFalse = new CilInstructionLabel();

        // Check: obj != null && this.GetType() == obj.GetType()
        il.Add(CilOpCodes.Ldarg_1);
        il.Add(CilOpCodes.Brfalse, returnFalse);
        il.Add(CilOpCodes.Ldarg_0);
        il.Add(CilOpCodes.Call, (IMethodDefOrRef)getType);
        il.Add(CilOpCodes.Ldarg_1);
        il.Add(CilOpCodes.Callvirt, (IMethodDefOrRef)getType);
        il.Add(CilOpCodes.Call, (IMethodDefOrRef)typeEquality);
        il.Add(CilOpCodes.Brfalse, returnFalse);

        if (fields.Count == 0)
        {
            il.Add(CilOpCodes.Ldc_I4_1);
            il.Add(CilOpCodes.Ret);
            var falseLdc0 = new CilInstruction(CilOpCodes.Ldc_I4_0);
            returnFalse.Instruction = falseLdc0;
            il.Add(falseLdc0);
            il.Add(CilOpCodes.Ret);
            return;
        }

        body.InitializeLocals = true;
        var otherLocal = new CilLocalVariable(_module.CorLibTypeFactory.Object);
        body.LocalVariables.Add(otherLocal);
        il.Add(CilOpCodes.Ldarg_1);
        il.Add(CilOpCodes.Stloc, otherLocal);

        // Compare each field using object.Equals(object, object). `other` is still an
        // object reference in a local; we need to isinst/castclass it to the case type
        // (closed on its own generic params if the case is generic) before ldfld, so
        // the emitted IL passes verification.
        var objEquals = _module.DefaultImporter.ImportMethod(
            typeof(object).GetMethod("Equals", [typeof(object), typeof(object)])!
        );
        var caseSelfSig = MakeSelfGenericInstance(caseType);
        var caseSelfRef = caseSelfSig is null ? caseType : caseSelfSig.ToTypeDefOrRef();
        foreach (var field in fields)
        {
            var fieldRef = ResolveSelfField(caseType, field);
            il.Add(CilOpCodes.Ldarg_0);
            il.Add(CilOpCodes.Ldfld, fieldRef);
            il.Add(CilOpCodes.Box, field.Signature!.FieldType.ToTypeDefOrRef());
            il.Add(CilOpCodes.Ldloc, otherLocal);
            il.Add(CilOpCodes.Castclass, caseSelfRef);
            il.Add(CilOpCodes.Ldfld, fieldRef);
            il.Add(CilOpCodes.Box, field.Signature!.FieldType.ToTypeDefOrRef());
            il.Add(CilOpCodes.Call, (IMethodDefOrRef)objEquals);
            il.Add(CilOpCodes.Brfalse, returnFalse);
        }

        // All fields matched
        il.Add(CilOpCodes.Ldc_I4_1);
        il.Add(CilOpCodes.Ret);

        // Return false
        var falseLdc = new CilInstruction(CilOpCodes.Ldc_I4_0);
        returnFalse.Instruction = falseLdc;
        il.Add(falseLdc);
        il.Add(CilOpCodes.Ret);
    }

    private void EmitUnionCaseGetHashCode(TypeDefinition caseType, List<FieldDefinition> fields)
    {
        var method = new MethodDefinition(
            "GetHashCode",
            MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig,
            MethodSignature.CreateInstance(_module.CorLibTypeFactory.Int32)
        );
        caseType.Methods.Add(method);

        var body = new CilMethodBody { InitializeLocals = true };
        method.MethodBody = body;
        var il = body.Instructions;

        if (fields.Count == 0)
        {
            il.Add(CilOpCodes.Ldstr, caseType.Name ?? "");
            il.Add(
                CilOpCodes.Callvirt,
                (IMethodDefOrRef)
                    _module.DefaultImporter.ImportMethod(
                        typeof(string).GetMethod("GetHashCode", Type.EmptyTypes)!
                    )
            );
            il.Add(CilOpCodes.Ret);
            return;
        }

        body.InitializeLocals = true;
        var hashCodeType = _module.DefaultImporter.ImportType(typeof(HashCode));
        var hashCodeLocal = new CilLocalVariable(hashCodeType.ToTypeSignature(true));
        body.LocalVariables.Add(hashCodeLocal);

        // Initialize HashCode struct
        il.Add(CilOpCodes.Ldloca, hashCodeLocal);
        il.Add(CilOpCodes.Initobj, hashCodeType);

        // Add type name
        var addGenericMethod = typeof(HashCode)
            .GetMethods()
            .First(m => m.Name == "Add" && m.IsGenericMethod && m.GetParameters().Length == 1);
        var addString = _module.DefaultImporter.ImportMethod(
            addGenericMethod.MakeGenericMethod(typeof(string))
        );
        il.Add(CilOpCodes.Ldloca, hashCodeLocal);
        il.Add(CilOpCodes.Ldstr, caseType.Name ?? "");
        if (addString is MethodSpecification addStringSpec)
            il.Add(CilOpCodes.Call, addStringSpec);
        else
            il.Add(CilOpCodes.Call, (IMethodDefOrRef)addString);

        // Add each field value (boxed to object)
        var addObject = _module.DefaultImporter.ImportMethod(
            addGenericMethod.MakeGenericMethod(typeof(object))
        );
        foreach (var field in fields)
        {
            il.Add(CilOpCodes.Ldloca, hashCodeLocal);
            il.Add(CilOpCodes.Ldarg_0);
            il.Add(CilOpCodes.Ldfld, ResolveSelfField(caseType, field));
            il.Add(CilOpCodes.Box, field.Signature!.FieldType.ToTypeDefOrRef());
            if (addObject is MethodSpecification addObjSpec)
                il.Add(CilOpCodes.Call, addObjSpec);
            else
                il.Add(CilOpCodes.Call, (IMethodDefOrRef)addObject);
        }

        // Return hash code
        var toHashCode = _module.DefaultImporter.ImportMethod(
            typeof(HashCode).GetMethod("ToHashCode")!
        );
        il.Add(CilOpCodes.Ldloca, hashCodeLocal);
        il.Add(CilOpCodes.Call, (IMethodDefOrRef)toHashCode);
        il.Add(CilOpCodes.Ret);
    }

    private void EmitDeconstruct(TypeDefinition type, List<FieldDefinition> fields)
    {
        if (fields.Count == 0)
            return;

        var outParamTypes = fields
            .Select(TypeSignature (f) => new ByReferenceTypeSignature(f.Signature!.FieldType))
            .ToArray();

        var method = new MethodDefinition(
            "Deconstruct",
            MethodAttributes.Public | MethodAttributes.HideBySig,
            MethodSignature.CreateInstance(_module.CorLibTypeFactory.Void, outParamTypes)
        );

        for (var i = 0; i < fields.Count; i++)
            method.ParameterDefinitions.Add(
                new ParameterDefinition((ushort)(i + 1), $"p{i}", ParameterAttributes.Out)
            );

        type.Methods.Add(method);

        var body = new CilMethodBody { InitializeLocals = true };
        method.MethodBody = body;
        var il = body.Instructions;

        for (var i = 0; i < fields.Count; i++)
        {
            il.Add(CilOpCodes.Ldarg, method.Parameters[i]);
            il.Add(CilOpCodes.Ldarg_0);
            il.Add(CilOpCodes.Ldfld, ResolveSelfField(type, fields[i]));
            il.Add(CilOpCodes.Stobj, fields[i].Signature!.FieldType.ToTypeDefOrRef());
        }

        il.Add(CilOpCodes.Ret);
    }

    private void EmitFuncDef(IrNode.FuncDef func, TypeDefinition typeDefinition)
    {
        var methodDef = RegisterFuncSignature(func, typeDefinition);
        EmitFuncBody(func, methodDef);
    }

    private void EmitFuncBody(IrNode.FuncDef func, MethodDefinition methodDef)
    {
        Log.Debug(
            "IlEmitter: emitting function {FuncName}, IsAsync={IsAsync}, IsGeneric={IsGeneric}",
            func.Name,
            func.IsAsync,
            func.TypeParams is { Count: > 0 }
        );
        var isGeneric = func.TypeParams is { Count: > 0 };
        var typeDefinition = methodDef.DeclaringType!;

        var savedTypeVarMap = _currentTypeVarMap;
        var savedTypeParamMap = _currentTypeParamMap;

        if (isGeneric)
        {
            var varNameMap = BuildTypeVarMap(func);
            _currentTypeVarMap = new Dictionary<int, TypeSignature>();
            _currentTypeParamMap = new Dictionary<string, TypeSignature>();
            foreach (var (varId, paramName) in varNameMap)
            {
                var idx = func.TypeParams!.ToList().IndexOf(paramName);
                if (idx < 0)
                    continue;
                var gpSig = new GenericParameterSignature(
                    _module,
                    GenericParameterType.Method,
                    idx
                );
                _currentTypeVarMap[varId] = gpSig;
                _currentTypeParamMap[paramName] = gpSig;
            }
        }

        Log.Debug(
            "IlEmitter: function {FuncName} emission path: {Path}",
            func.Name,
            func.IsAsync && AsyncStateMachineAnalyzer.ContainsAwait(func.Body)
                ? "async-state-machine"
                : "synchronous"
        );

        if (func.IsAsync && AsyncStateMachineAnalyzer.ContainsAwait(func.Body))
        {
            var savedOffset = _instanceArgOffset;
            var savedReturnType = _currentFuncReturnType;
            _instanceArgOffset = 0;
            _currentFuncReturnType = func.ReturnType;
            EmitAsyncFuncDef(func, methodDef, typeDefinition);
            _currentFuncReturnType = savedReturnType;
            _instanceArgOffset = savedOffset;
        }
        else
        {
            var body = new CilMethodBody { InitializeLocals = true };
            methodDef.MethodBody = body;
            var il = body.Instructions;
            var locals = new Dictionary<string, CilLocalVariable>();

            var savedOffset = _instanceArgOffset;
            var savedReturnType = _currentFuncReturnType;
            _instanceArgOffset = 0;
            _currentFuncReturnType = func.ReturnType;
            EmitNode(func.Body, il, func.Params, locals);
            _currentFuncReturnType = savedReturnType;
            _instanceArgOffset = savedOffset;

            if (func.IsAsync)
            {
                // For async funcs without awaits we still need to wrap the body into a Task.
                // Three cases: (a) Unit return, (b) non-generic Task return (treat like Unit
                // wrapped in CompletedTask), (c) Task<T> return — extract T and FromResult<T>.
                var isUnit = func.ReturnType is ZType.ZPrimitiveType { Kind: PrimitiveKind.Unit };
                var isVoidTask =
                    func.ReturnType is ZType.ZNamedType { TypeArgs: [] } voidTask
                    && _typeAliases.IsTaskName(voidTask.Name);

                if (isUnit || isVoidTask)
                {
                    if (
                        func.Body.Type
                        is not null
                            and not ZType.ZPrimitiveType { Kind: PrimitiveKind.Unit }
                    )
                        il.Add(CilOpCodes.Pop);
                    var completedTaskGetter = typeof(Task)
                        .GetProperty("CompletedTask")!
                        .GetGetMethod()!;
                    il.Add(
                        CilOpCodes.Call,
                        _module.DefaultImporter.ImportMethod(completedTaskGetter)
                    );
                }
                else
                {
                    var inner =
                        func.ReturnType is ZType.ZNamedType { TypeArgs: [var t] } taskNt
                        && _typeAliases.IsTaskName(taskNt.Name)
                            ? t
                            : func.ReturnType;
                    var fromResult = typeof(Task)
                        .GetMethod("FromResult")!
                        .MakeGenericMethod(MapToReflectionClr(inner));
                    il.Add(CilOpCodes.Call, _module.DefaultImporter.ImportMethod(fromResult));
                }
            }

            il.Add(CilOpCodes.Ret);
        }

        if (!isGeneric)
            return;
        _currentTypeVarMap = savedTypeVarMap;
        _currentTypeParamMap = savedTypeParamMap;
    }

    private void EmitNode(
        IrNode node,
        CilInstructionCollection il,
        IReadOnlyList<IrParam> outerParams,
        Dictionary<string, CilLocalVariable> locals
    )
    {
        switch (node)
        {
            case IrNode.Match
            or IrNode.ObjectExpr
            or IrNode.FuncDef
            or IrNode.ClrCall
            or IrNode.MethodCall
            or IrNode.Await
            or IrNode.WithHandlers:
                Log.Debug("IlEmitter.EmitNode: dispatching {NodeType}", node.GetType().Name);
                break;
        }

        switch (node)
        {
            case IrNode.IntConst n:
                il.Add(CilOpCodes.Ldc_I4, n.Value);
                break;

            case IrNode.FloatConst n:
                il.Add(CilOpCodes.Ldc_R4, n.Value);
                break;

            case IrNode.BoolConst n:
                il.Add(n.Value ? CilOpCodes.Ldc_I4_1 : CilOpCodes.Ldc_I4_0);
                break;

            case IrNode.StringConst n:
                il.Add(CilOpCodes.Ldstr, n.Value);
                break;

            case IrNode.UnitConst:
                break;

            case IrNode.NullConst nullConst:
                // null with Unit type means it was used in a void context (e.g., match arm
                // alongside set!) — don't push anything onto the stack
                if (nullConst.Type is ZType.ZPrimitiveType { Kind: PrimitiveKind.Unit })
                    break;
                if (nullConst.Type is ZType.ZNullableType)
                {
                    var nullableClrType = MapToClr(nullConst.Type);
                    if (nullableClrType.IsValueType)
                    {
                        // Nullable<T> where T is value type — use initobj
                        var nullableLocal = new CilLocalVariable(nullableClrType);
                        il.Owner.LocalVariables.Add(nullableLocal);
                        il.Owner.InitializeLocals = true;
                        il.Add(CilOpCodes.Ldloca, nullableLocal);
                        il.Add(CilOpCodes.Initobj, nullableClrType.ToTypeDefOrRef());
                        il.Add(CilOpCodes.Ldloc, nullableLocal);
                    }
                    else
                    {
                        // Nullable reference type — just ldnull
                        il.Add(CilOpCodes.Ldnull);
                    }
                }
                else
                {
                    il.Add(CilOpCodes.Ldnull);
                }

                break;

            case IrNode.Var v:
                EmitLoadVar(v.Name, v.Span, il, outerParams, locals, v.Type);
                break;

            case IrNode.BinOp binop:
                if (binop.Op is "and" or "or")
                {
                    EmitShortCircuit(binop, il, outerParams, locals);
                }
                else
                {
                    EmitNode(binop.Left, il, outerParams, locals);
                    EmitNode(binop.Right, il, outerParams, locals);
                    EmitBinaryOp(binop.Op, binop.Left.Type, il);
                }

                break;

            case IrNode.UnaryOp unary:
                EmitNode(unary.Operand, il, outerParams, locals);
                EmitUnaryOp(unary.Op, il);
                break;

            case IrNode.If @if:
                EmitIf(@if, il, outerParams, locals);
                break;

            case IrNode.Let let:
                EmitLet(let, il, outerParams, locals);
                break;

            case IrNode.ClrCall clrCall:
                EmitClrCall(clrCall, il, outerParams, locals);
                break;

            case IrNode.Call call:
                EmitCall(call, il, outerParams, locals);
                break;

            case IrNode.Match match:
                EmitMatch(match, il, outerParams, locals);
                break;

            case IrNode.ClrNew clrNew:
                EmitClrNew(clrNew, il, outerParams, locals);
                break;

            case IrNode.TypeOf typeOf:
                EmitTypeOf(typeOf, il);
                break;

            case IrNode.Throw @throw:
                EmitNode(@throw.Expr, il, outerParams, locals);
                il.Add(CilOpCodes.Throw);
                break;

            case IrNode.MethodCall methodCall:
                EmitMethodCall(methodCall, il, outerParams, locals);
                break;

            case IrNode.MutableArrayNew mutableArrayNew:
                EmitMutableArrayNew(mutableArrayNew, il, outerParams, locals);
                break;

            case IrNode.FuncDef funcDef:
                EmitLambda(funcDef, il, outerParams, locals);
                break;

            case IrNode.TupleNew tupleNew:
                EmitTupleNew(tupleNew, il, outerParams, locals);
                break;

            case IrNode.RecordNew recordNew:
                EmitRecordNew(recordNew, il, outerParams, locals);
                break;

            case IrNode.RecordWith recordWith:
                EmitRecordWith(recordWith, il, outerParams, locals);
                break;

            case IrNode.FieldGet fieldGet:
                EmitFieldGet(fieldGet, il, outerParams, locals);
                break;

            case IrNode.UnionCaseNew unionCaseNew:
                EmitUnionCaseNew(unionCaseNew, il, outerParams, locals);
                break;

            case IrNode.Seq seq:
                for (var i = 0; i < seq.Nodes.Count; i++)
                {
                    EmitNode(seq.Nodes[i], il, outerParams, locals);
                    if (
                        i < seq.Nodes.Count - 1
                        && seq.Nodes[i].Type
                            is not null
                                and not ZType.ZPrimitiveType { Kind: PrimitiveKind.Unit }
                    )
                        il.Add(CilOpCodes.Pop);
                }

                break;

            case IrNode.Await awaitNode:
                if (_moveNextCtx != null)
                    EmitMoveNextAwait(awaitNode, il, outerParams, locals);
                else
                    EmitAwait(awaitNode, il, outerParams, locals);
                break;

            case IrNode.WithHandlers withHandlers:
                EmitWithHandlers(withHandlers, il, outerParams, locals);
                break;

            case IrNode.SuperMethodCall superCall:
                EmitSuperMethodCall(superCall, il, outerParams, locals);
                break;

            case IrNode.ObjectExpr objectExpr:
                EmitObjectExpr(objectExpr, il, outerParams, locals);
                break;

            case IrNode.SetField setField:
                EmitLoadClassThis(il);
                EmitNode(setField.Value, il, outerParams, locals);
                EmitNullableWrapIfNeeded(
                    setField.Value,
                    _currentClassFields![setField.FieldName].Signature!.FieldType,
                    il
                );
                il.Add(CilOpCodes.Stfld, _currentClassFields![setField.FieldName]);
                break;

            case IrNode.Closure closure:
            {
                var sanitizedName = Sanitize(closure.LiftedFuncName);
                if (!_methods.TryGetValue(sanitizedName, out var closureMethodDef))
                {
                    diagnostics.Error(
                        $"Lifted closure method '{closure.LiftedFuncName}' not found",
                        closure.Span
                    );
                    il.Add(CilOpCodes.Ldc_I4_0);
                    break;
                }

                var closureType = closureMethodDef.DeclaringType;
                if (closureType is null)
                {
                    diagnostics.Error(
                        $"Lifted closure method '{closure.LiftedFuncName}' has no declaring type",
                        closure.Span
                    );
                    il.Add(CilOpCodes.Ldc_I4_0);
                    break;
                }

                // Get capture fields from the closure type (fields excluding synthetic fields starting with <>)
                var captureFields = closureType
                    .Fields.Where(f => f.Name is not null && !f.Name.Value.StartsWith("<>"))
                    .ToList();

                // Resolve the closure type to a CLR Type for constructor lookup
                var closureClrType = ResolveClrTypeForTypeRef(closureType);
                if (closureClrType is null)
                {
                    diagnostics.Error(
                        $"Cannot resolve closure type for '{closure.LiftedFuncName}'",
                        closure.Span
                    );
                    il.Add(CilOpCodes.Ldc_I4_0);
                    break;
                }

                il.Add(
                    CilOpCodes.Newobj,
                    _module.DefaultImporter.ImportMethod(closureClrType.GetConstructors()[0])
                );
                for (var j = 0; j < closure.CapturedValues.Count && j < captureFields.Count; j++)
                {
                    il.Add(CilOpCodes.Dup);
                    EmitNode(closure.CapturedValues[j], il, outerParams, locals);
                    il.Add(CilOpCodes.Stfld, captureFields[j]);
                }

                // Load method pointer and create delegate
                il.Add(CilOpCodes.Ldftn, closureMethodDef);
                var returnType = closureMethodDef.Signature!.ReturnType;
                Type delegateCtorType;
                if (returnType == _module.CorLibTypeFactory.Void)
                    delegateCtorType = typeof(Action);
                else
                    delegateCtorType = typeof(Func<>);
                var closureDelegateCtor = _module.DefaultImporter.ImportMethod(
                    delegateCtorType.GetConstructors()[0]
                );
                il.Add(CilOpCodes.Newobj, closureDelegateCtor);
                break;
            }

            default:
                diagnostics.Error(
                    $"AsmResolver IL emission not implemented for {node.GetType().Name}",
                    node.Span
                );
                il.Add(CilOpCodes.Ldc_I4_0);
                break;
        }
    }

    /// <summary>
    ///     If the target type is Nullable&lt;T&gt; and the value type is non-nullable T,
    ///     emit a newobj call to wrap the value on the stack into Nullable&lt;T&gt;.
    /// </summary>
    /// <summary>
    ///     If the target type is Nullable&lt;T&gt; and the value type is non-nullable T,
    ///     emit a newobj call to wrap the value on the stack into Nullable&lt;T&gt;.
    ///     For null constants targeting nullable fields, replaces the ldnull with initobj.
    /// </summary>
    private void EmitNullableWrapIfNeeded(
        IrNode valueNode,
        TypeSignature targetClrType,
        CilInstructionCollection il
    )
    {
        if (targetClrType is not GenericInstanceTypeSignature git)
            return;

        if (git.GenericType.FullName != "System.Nullable`1")
            return;

        // Handle null constant → nullable: replace ldnull with initobj Nullable<T>
        if (valueNode is IrNode.NullConst)
        {
            // The null constant handler may have emitted ldnull (for unresolved type vars)
            // or initobj (for known nullable types). If the last instruction is ldnull, fix it.
            if (il.Count <= 0 || il[^1].OpCode != CilOpCodes.Ldnull)
                return;
            il.RemoveAt(il.Count - 1);
            var nullableLocal = new CilLocalVariable(git);
            il.Owner.LocalVariables.Add(nullableLocal);
            il.Owner.InitializeLocals = true;
            il.Add(CilOpCodes.Ldloca, nullableLocal);
            il.Add(CilOpCodes.Initobj, git.ToTypeDefOrRef());
            il.Add(CilOpCodes.Ldloc, nullableLocal);

            return;
        }

        // Skip if value is already nullable
        if (valueNode.Type is ZType.ZNullableType)
            return;

        // Target is Nullable<T>, value is T — wrap via Nullable<T>(T value) constructor
        var nullableOpenType = typeof(Nullable<>);
        var openCtor = nullableOpenType.GetConstructors()[0];
        var importedCtor = (IMethodDefOrRef)_module.DefaultImporter.ImportMethod(openCtor);
        var ctorRef = new MemberReference(
            git.ToTypeDefOrRef(),
            importedCtor.Name!,
            importedCtor.Signature
        );
        il.Add(CilOpCodes.Newobj, ctorRef);
    }

    private void EmitIf(
        IrNode.If @if,
        CilInstructionCollection il,
        IReadOnlyList<IrParam> outerParams,
        Dictionary<string, CilLocalVariable> locals
    )
    {
        var elseLabel = new CilInstructionLabel();
        var endLabel = new CilInstructionLabel();
        var ifIsUnit = @if.Type is ZType.ZPrimitiveType { Kind: PrimitiveKind.Unit };
        EmitNode(@if.Condition, il, outerParams, locals);
        il.Add(CilOpCodes.Brfalse, elseLabel);
        EmitNode(@if.Then, il, outerParams, locals);
        ReconcileBranchStack(@if.Then.Type, ifIsUnit, il);
        il.Add(CilOpCodes.Br, endLabel);
        elseLabel.Instruction = il.Add(CilOpCodes.Nop);
        EmitNode(@if.Else, il, outerParams, locals);
        ReconcileBranchStack(@if.Else.Type, ifIsUnit, il);
        endLabel.Instruction = il.Add(CilOpCodes.Nop);
    }

    private void EmitLet(
        IrNode.Let let,
        CilInstructionCollection il,
        IReadOnlyList<IrParam> outerParams,
        Dictionary<string, CilLocalVariable> locals
    )
    {
        EmitNode(let.Value, il, outerParams, locals);
        if (let.Value.Type is not ZType.ZPrimitiveType { Kind: PrimitiveKind.Unit })
        {
            var local = new CilLocalVariable(MapToClr(let.Value.Type));
            il.Owner.LocalVariables.Add(local);
            il.Add(CilOpCodes.Stloc, local);
            locals[let.VarName] = local;

            // Also save to state machine field if we're inside MoveNext
            if (
                _moveNextCtx != null
                && _moveNextCtx.VarFields.TryGetValue(let.VarName, out var smField)
            )
            {
                il.Add(CilOpCodes.Ldarg_0);
                il.Add(CilOpCodes.Ldloc, local);
                il.Add(CilOpCodes.Stfld, smField);
                _moveNextCtx.AllLocals.Add((let.VarName, local));
            }
        }

        EmitNode(let.Body, il, outerParams, locals);
    }

    private void EmitClrNew(
        IrNode.ClrNew clrNew,
        CilInstructionCollection il,
        IReadOnlyList<IrParam> outerParams,
        Dictionary<string, CilLocalVariable> locals
    )
    {
        foreach (var arg in clrNew.Args)
            EmitNode(arg, il, outerParams, locals);

        // (new FCls_0 ...) for user-defined ZScheme classes: the type lives in
        // the module we're currently emitting, so CLR reflection can't see it.
        // Look it up in _userTypes and use the matching constructor directly.
        if (
            _userTypes.TryGetValue(clrNew.QualifiedTypeName, out var userTypeRef)
            && userTypeRef is TypeDefinition userTypeDef
        )
        {
            var userCtor = userTypeDef.Methods.FirstOrDefault(m =>
                m is { IsConstructor: true, IsStatic: false }
                && m.Parameters.Count == clrNew.Args.Count
            );
            if (userCtor is not null)
            {
                il.Add(CilOpCodes.Newobj, userCtor);
                return;
            }

            diagnostics.Error(
                $"No constructor on '{clrNew.QualifiedTypeName}' matches the given arguments",
                SourceSpan.None
            );
            il.Add(CilOpCodes.Ldc_I4_0);
            return;
        }

        var type = _clrInterop.FindType(clrNew.QualifiedTypeName);

        // If not found, try as a generic type definition by appending arity suffix
        if (type is null && clrNew.TypeArgs.Count > 0)
        {
            type = _clrInterop.FindType($"{clrNew.QualifiedTypeName}`{clrNew.TypeArgs.Count}");
            type = type?.MakeGenericType(
                clrNew.TypeArgs.Select(t => MapToReflectionClr(t)).ToArray()
            );
        }

        // Fallback: use inferred type info
        if (type is null && clrNew.Type is ZType.ZNamedType { TypeArgs: { Count: > 0 } typeArgs })
        {
            type = _clrInterop.FindType($"{clrNew.QualifiedTypeName}`{typeArgs.Count}");
            type = type?.MakeGenericType(typeArgs.Select(t => MapToReflectionClr(t)).ToArray());
        }

        if (type is null)
        {
            diagnostics.Error($"CLR type '{clrNew.QualifiedTypeName}' not found", clrNew.Span);
            il.Add(CilOpCodes.Ldc_I4_0);
            return;
        }

        var argTypes = clrNew.Args.Select(a => ResolveClrType(a.Type)).ToArray();
        var ctor =
            type.GetConstructor(argTypes)
            ?? type.GetConstructors()
                .FirstOrDefault(c => c.GetParameters().Length == argTypes.Length);

        if (ctor is null)
        {
            diagnostics.Error(
                $"No constructor on '{clrNew.QualifiedTypeName}' matches the given arguments",
                clrNew.Span
            );
            il.Add(CilOpCodes.Ldc_I4_0);
            return;
        }

        // When inside a generic function, construct the newobj on the properly parameterized type
        // (e.g., ConcurrentDictionary<!!0, !!1> instead of ConcurrentDictionary<object, object>)
        if (
            _currentTypeVarMap is { Count: > 0 }
            && clrNew.Type is ZType.ZNamedType { TypeArgs.Count: > 0 } clrNewNt
        )
        {
            var asmType = MapToClr(clrNewNt);
            if (asmType is GenericInstanceTypeSignature git)
            {
                var importedCtor = (IMethodDefOrRef)_module.DefaultImporter.ImportMethod(ctor);
                var closedCtor = new MemberReference(
                    git.ToTypeDefOrRef(),
                    ".ctor",
                    importedCtor.Signature!
                );
                il.Add(CilOpCodes.Newobj, closedCtor);
                return;
            }
        }

        il.Add(CilOpCodes.Newobj, _module.DefaultImporter.ImportMethod(ctor));
    }

    private void EmitTypeOf(IrNode.TypeOf typeOf, CilInstructionCollection il)
    {
        // (typeof T) lowers to `ldtoken T; call System.Type.GetTypeFromHandle(RuntimeTypeHandle)`,
        // which is the IL pattern the C# compiler emits for `typeof(T)`.
        var typeSig = MapToClr(typeOf.TypeArg);
        var getTypeFromHandle = _module.DefaultImporter.ImportMethod(
            typeof(Type).GetMethod(nameof(Type.GetTypeFromHandle), [typeof(RuntimeTypeHandle)])!
        );

        il.Add(CilOpCodes.Ldtoken, typeSig.ToTypeDefOrRef());
        il.Add(CilOpCodes.Call, (IMethodDefOrRef)getTypeFromHandle);
    }

    private void EmitClrCall(
        IrNode.ClrCall clrCall,
        CilInstructionCollection il,
        IReadOnlyList<IrParam> outerParams,
        Dictionary<string, CilLocalVariable> locals
    )
    {
        var type = _clrInterop.FindType(clrCall.QualifiedTypeName);
        if (type is null)
        {
            diagnostics.Error($"CLR type '{clrCall.QualifiedTypeName}' not found", clrCall.Span);
            il.Add(CilOpCodes.Ldc_I4_0);
            return;
        }

        if (clrCall.OutParams is { Count: > 0 })
        {
            EmitOutParamStaticCall(clrCall, type, il, outerParams, locals);
            return;
        }

        var argTypes = clrCall.Args.Select(a => ResolveClrType(a.Type)).ToArray();

        MethodInfo? method;
        MethodInfo? openGeneric = null;
        var useAsmGenericPath = false;
        if (clrCall.GenericArity > 0)
        {
            var candidates = type.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(m =>
                    m.Name == clrCall.MethodName
                    && m.IsGenericMethodDefinition
                    && m.GetGenericArguments().Length == clrCall.GenericArity
                    && ParamsMatchWithOptionals(m, argTypes.Length)
                )
                .ToList();

            // Prefer the smallest parameter count (fewest optional defaults to
            // synthesize), tie-breaking by overload specificity.
            openGeneric = candidates
                .OrderBy(m => m.GetParameters().Length)
                .ThenByDescending(m => ScoreGenericOverload(m, argTypes))
                .FirstOrDefault();

            // Close the generic via an AsmResolver MethodSpecification (honoring the
            // resolved GenericTypeArgs) when a type arg is a type variable in a generic
            // context, or a user-defined record/union. Neither can be closed by reflection
            // MakeGenericMethod, since they are not loaded System.Types (a record is a
            // TypeDefinition being built in the current module).
            var hasTypeVarArgs =
                clrCall.GenericTypeArgs is { Count: > 0 }
                && clrCall.GenericTypeArgs.Any(t => t is ZType.ZTypeVar or ZType.ZConstrainedVar);
            var hasUserTypeArgs =
                clrCall.GenericTypeArgs is { Count: > 0 }
                && clrCall.GenericTypeArgs.Any(t =>
                    t is ZType.ZNamedType nt && _userTypes.ContainsKey(nt.Name)
                );
            useAsmGenericPath =
                openGeneric is not null
                && ((hasTypeVarArgs && _currentTypeVarMap is { Count: > 0 }) || hasUserTypeArgs);

            method = useAsmGenericPath
                ? openGeneric // closed below via MethodSpecification; keep the open def for param shapes
                : openGeneric?.MakeGenericMethod(InferGenericTypeArgs(openGeneric, argTypes));
        }
        else if (clrCall.ResolvedMethodInfo is { } preResolved)
        {
            // Signature-directed resolution (incl. concrete-delegate-over-base-Delegate)
            // already chose the overload during IR lowering; honor it rather than
            // re-running the reflection fallback, which rejects RequestDelegate because
            // it is not IsAssignableFrom(Func<...>) and would pick the base Delegate.
            Log.Debug("EmitClrCall: using pre-resolved overload {Method}", preResolved);
            method = preResolved;
        }
        else
        {
            method = type.GetMethod(clrCall.MethodName, argTypes);

            // Fallback: exact type matching can fail when nullable types are unwrapped
            // (e.g. float? → float) or when assignable types don't match exactly.
            // Search by name + parameter count, then verify assignability.
            if (method is null)
            {
                var candidates = type.GetMethods(
                        BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance
                    )
                    .Where(m =>
                        m.Name == clrCall.MethodName && ParamsMatchWithOptionals(m, argTypes.Length)
                    )
                    .OrderBy(m => m.GetParameters().Length)
                    .ToList();

                // Delegate-shape preference: when an argument is a ZScheme function, prefer
                // a candidate whose parameter is a concrete delegate matching the function
                // shape over one taking the abstract System.Delegate base (whose ctor cannot
                // be constructed). Parallels ClrInterop.ResolveOverloadCallSite's tie-break.
                var delegateArgs = clrCall
                    .Args.Select((a, i) => (a.Type as ZType.ZFuncType, i))
                    .Where(t => t.Item1 is not null)
                    .ToList();
                if (candidates.Count > 1 && delegateArgs.Count > 0)
                {
                    var specific = candidates
                        .Where(m =>
                        {
                            var ps = m.GetParameters();
                            return delegateArgs.All(d =>
                                d.i < ps.Length
                                && _clrInterop.FuncTypeMatchesDelegate(
                                    d.Item1!,
                                    ps[d.i].ParameterType,
                                    clrCall.Span
                                )
                            );
                        })
                        .ToList();
                    if (specific.Count > 0)
                        candidates = specific;
                }

                method = candidates.Count switch
                {
                    1 => candidates[0],
                    // Pick the best match: prefer exact matches, then assignable matches.
                    // Only the supplied arguments are checked; trailing optional params
                    // are filled with defaults below.
                    > 1 => candidates.FirstOrDefault(m =>
                    {
                        var ps = m.GetParameters();
                        for (var i = 0; i < argTypes.Length; i++)
                            if (
                                !ps[i].ParameterType.IsAssignableFrom(argTypes[i])
                                && !(Nullable.GetUnderlyingType(ps[i].ParameterType) == argTypes[i])
                            )
                                return false;
                        return true;
                    }) ?? candidates[0],
                    _ => method,
                };
            }
        }

        if (method is null)
        {
            // Fallback: check for static properties
            var prop = type.GetProperty(
                clrCall.MethodName,
                BindingFlags.Public | BindingFlags.Static
            );
            if (prop?.GetGetMethod() is { } getter)
            {
                il.Add(CilOpCodes.Call, _module.DefaultImporter.ImportMethod(getter));
                return;
            }

            // Fallback: check for static fields (enum values, static readonly fields)
            var field = type.GetField(
                clrCall.MethodName,
                BindingFlags.Public | BindingFlags.Static
            );
            if (field is not null)
            {
                if (field.IsLiteral)
                {
                    // Enum/const value — emit as integer constant
                    var constVal = field.GetRawConstantValue();
                    switch (constVal)
                    {
                        case int i:
                            il.Add(CilOpCodes.Ldc_I4, i);
                            break;
                        case long l:
                            il.Add(CilOpCodes.Ldc_I8, l);
                            break;
                        default:
                            il.Add(CilOpCodes.Ldc_I4, Convert.ToInt32(constVal));
                            break;
                    }
                }
                else
                {
                    // Static field — emit ldsfld
                    il.Add(
                        CilOpCodes.Ldsfld,
                        (IFieldDescriptor)_module.DefaultImporter.ImportField(field)
                    );
                }

                return;
            }

            diagnostics.Error(
                $"CLR method '{clrCall.QualifiedTypeName}.{clrCall.MethodName}' not found",
                clrCall.Span
            );
            il.Add(CilOpCodes.Ldc_I4_0);
            return;
        }

        // Emit arguments with boxing/nullable wrapping where needed
        var methodParams = method.GetParameters();
        for (var i = 0; i < clrCall.Args.Count; i++)
        {
            var isVarArg = clrCall.Args[i] is IrNode.Var;
            var needsDelegate = false;

            // A Var argument passed to a concrete delegate parameter must be turned into
            // that delegate; pushing a raw method pointer or a differently-typed delegate
            // value would fail verification (e.g. a Func<HttpContext,Task> where a
            // RequestDelegate is expected). newobj on a delegate ctor takes (object target,
            // IntPtr method).
            if (
                isVarArg
                && i < methodParams.Length
                && typeof(Delegate).IsAssignableFrom(methodParams[i].ParameterType)
                && methodParams[i].ParameterType != typeof(Delegate)
                && methodParams[i].ParameterType != typeof(MulticastDelegate)
            )
            {
                var pType = methodParams[i].ParameterType;
                var delegateCtor = _module.DefaultImporter.ImportMethod(pType.GetConstructors()[0]);
                var sanitizedName = Sanitize(((IrNode.Var)clrCall.Args[i]).Name);
                if (_methods.TryGetValue(sanitizedName, out var methodDef))
                {
                    // Top-level ZScheme functions are static, so the target is null.
                    il.Add(CilOpCodes.Ldnull);
                    il.Add(CilOpCodes.Ldftn, methodDef);
                    il.Add(CilOpCodes.Newobj, delegateCtor);
                    needsDelegate = true;
                }
                else if (
                    typeof(Delegate).IsAssignableFrom(argTypes[i])
                    && argTypes[i] != pType
                    && argTypes[i].GetMethod("Invoke") is { } srcInvoke
                )
                {
                    // A closure value (e.g. a Func<...> parameter) whose delegate type differs
                    // from the target: build a new delegate over the source's Invoke method.
                    EmitNode(clrCall.Args[i], il, outerParams, locals); // [src]
                    il.Add(CilOpCodes.Dup); // [src, src]
                    il.Add(CilOpCodes.Ldvirtftn, _module.DefaultImporter.ImportMethod(srcInvoke)); // [src, ftn]
                    il.Add(CilOpCodes.Newobj, delegateCtor); // [delegate]
                    needsDelegate = true;
                }
            }

            if (!needsDelegate)
                EmitNode(clrCall.Args[i], il, outerParams, locals);

            if (i >= methodParams.Length)
                continue;
            var paramType = methodParams[i].ParameterType;

            // Skip boxing/nullable checks for Var arguments that were converted to delegates
            if (needsDelegate)
                continue;

            if (argTypes[i].IsValueType && !paramType.IsValueType)
            {
                // In a generic context, use AsmResolver types for boxing so type variables
                // resolve to IL generic parameters (!!0) instead of being erased to object.
                if (useAsmGenericPath)
                    il.Add(CilOpCodes.Box, MapToClr(clrCall.Args[i].Type).ToTypeDefOrRef());
                else
                    il.Add(CilOpCodes.Box, _module.DefaultImporter.ImportType(argTypes[i]));
            }

            // Wrap T → Nullable<T> when parameter is Nullable<T> and argument is T
            if (
                !paramType.IsGenericType
                || paramType.GetGenericTypeDefinition() != typeof(Nullable<>)
                || clrCall.Args[i].Type is ZType.ZNullableType
            )
                continue;
            var targetSig = _module
                .DefaultImporter.ImportType(paramType)
                .ToTypeSignature(paramType.IsValueType);
            EmitNullableWrapIfNeeded(clrCall.Args[i], targetSig, il);
        }

        // Supply defaults for any trailing optional parameters the call omitted
        // (e.g. JsonSerializerOptions? options = null on JsonSerializer.Serialize<T>).
        for (var i = clrCall.Args.Count; i < methodParams.Length; i++)
            EmitDefaultArgument(methodParams[i], il);

        if (useAsmGenericPath)
        {
            var openMethodRef = _module.DefaultImporter.ImportMethod(openGeneric!);
            var genericArgSigs = clrCall.GenericTypeArgs!.Select(t => MapToClr(t)).ToArray();
            Log.Debug(
                "EmitClrCall: generic path for {Type}.{Method}, typeArgs=[{TypeArgs}], sigs=[{Sigs}]",
                clrCall.QualifiedTypeName,
                clrCall.MethodName,
                string.Join(", ", clrCall.GenericTypeArgs!),
                string.Join(", ", genericArgSigs.Select(s => s.ToString()))
            );
            var gim = new MethodSpecification(
                (IMethodDefOrRef)openMethodRef,
                new GenericInstanceMethodSignature(genericArgSigs)
            );
            il.Add(CilOpCodes.Call, gim);
        }
        else
        {
            Log.Debug(
                "EmitClrCall: reflection path for {Type}.{Method}, resolved={ResolvedMethod}",
                clrCall.QualifiedTypeName,
                clrCall.MethodName,
                method
            );
            il.Add(CilOpCodes.Call, _module.DefaultImporter.ImportMethod(method));
        }
    }

    // Whether method `m` can be called with `suppliedCount` leading arguments, i.e. it
    // has at least that many parameters and every parameter beyond them is optional.
    private static bool ParamsMatchWithOptionals(MethodInfo m, int suppliedCount)
    {
        var ps = m.GetParameters();
        if (ps.Length < suppliedCount)
            return false;
        for (var i = suppliedCount; i < ps.Length; i++)
            if (!ps[i].IsOptional)
                return false;
        return true;
    }

    // Push the default value for an omitted optional parameter onto the IL stack.
    private void EmitDefaultArgument(ParameterInfo p, CilInstructionCollection il)
    {
        var pt = p.ParameterType;
        if (!pt.IsValueType)
        {
            il.Add(CilOpCodes.Ldnull);
            return;
        }

        // Value-type parameter with an explicit non-null default constant.
        if (p is { HasDefaultValue: true, DefaultValue: { } dv })
            switch (dv)
            {
                case bool b:
                    il.Add(CilOpCodes.Ldc_I4, b ? 1 : 0);
                    return;
                case char c:
                    il.Add(CilOpCodes.Ldc_I4, c);
                    return;
                case float f:
                    il.Add(CilOpCodes.Ldc_R4, f);
                    return;
                case double d:
                    il.Add(CilOpCodes.Ldc_R8, d);
                    return;
                case long or ulong:
                    il.Add(CilOpCodes.Ldc_I8, Convert.ToInt64(dv));
                    return;
                case sbyte or byte or short or ushort or int or uint or Enum:
                    il.Add(CilOpCodes.Ldc_I4, Convert.ToInt32(dv));
                    return;
            }

        // Fallback: default(valueType) via a temp local + initobj.
        var sig = _module.DefaultImporter.ImportType(pt).ToTypeSignature(true);
        var tmp = new CilLocalVariable(sig);
        il.Owner.LocalVariables.Add(tmp);
        il.Add(CilOpCodes.Ldloca, tmp);
        il.Add(CilOpCodes.Initobj, sig.ToTypeDefOrRef());
        il.Add(CilOpCodes.Ldloc, tmp);
    }

    private void EmitOutParamStaticCall(
        IrNode.ClrCall clrCall,
        Type type,
        CilInstructionCollection il,
        IReadOnlyList<IrParam> outerParams,
        Dictionary<string, CilLocalVariable> locals
    )
    {
        var outParams = clrCall.OutParams!;

        // Find the method using the full parameter count (including out params)
        var method = FindMethodWithOutParams(
            type,
            clrCall.MethodName,
            clrCall.Args,
            outParams,
            BindingFlags.Public | BindingFlags.Static
        );
        if (method is null)
        {
            diagnostics.Error(
                $"CLR method '{clrCall.QualifiedTypeName}.{clrCall.MethodName}' with out parameters not found",
                clrCall.Span
            );
            il.Add(CilOpCodes.Ldc_I4_0);
            return;
        }

        // Allocate locals for each out parameter
        var outLocals = new List<CilLocalVariable>();
        foreach (var op in outParams)
        {
            var outLocal = new CilLocalVariable(MapToClr(op.ElementType));
            il.Owner.LocalVariables.Add(outLocal);
            outLocals.Add(outLocal);
        }

        // Emit arguments interleaved with ldloca for out params
        var outParamSet = outParams.ToDictionary(op => op.OriginalIndex);
        var totalParams = clrCall.Args.Count + outParams.Count;
        var visibleIdx = 0;
        var outIdx = 0;
        for (var i = 0; i < totalParams; i++)
            if (outParamSet.ContainsKey(i))
            {
                il.Add(CilOpCodes.Ldloca, outLocals[outIdx++]);
            }
            else
            {
                EmitNode(clrCall.Args[visibleIdx], il, outerParams, locals);
                var methodParam = method.GetParameters()[i];
                if (
                    methodParam.ParameterType == typeof(object)
                    && clrCall.Args[visibleIdx].Type is ZType.ZPrimitiveType
                )
                    il.Add(
                        CilOpCodes.Box,
                        _module.DefaultImporter.ImportType(
                            MapToReflectionClr(clrCall.Args[visibleIdx].Type)
                        )
                    );
                visibleIdx++;
            }

        // Call the method
        il.Add(CilOpCodes.Call, _module.DefaultImporter.ImportMethod(method));

        // Store the return value, then construct ValueTuple
        var retClrType = MapToClr(_clrInterop.MapClrTypeToZType(method.ReturnType));
        var retLocal = new CilLocalVariable(retClrType);
        il.Owner.LocalVariables.Add(retLocal);
        il.Add(CilOpCodes.Stloc, retLocal);

        il.Add(CilOpCodes.Ldloc, retLocal);
        foreach (var outLocal in outLocals)
            il.Add(CilOpCodes.Ldloc, outLocal);

        // Construct the ValueTuple
        EmitValueTupleNewobj(clrCall.Type, il, clrCall.Span);
    }

    private void EmitCall(
        IrNode.Call call,
        CilInstructionCollection il,
        IReadOnlyList<IrParam> outerParams,
        Dictionary<string, CilLocalVariable> locals
    )
    {
        if (call.Function is IrNode.Var v)
        {
            // Sanitize the full variable name (which may include module prefix like "http/get")
            var sanitized = Sanitize(v.Name);
            // For overload-resolved calls, prefer the module-qualified key so we
            // route to the correct module's method even when another imported
            // module has overwritten the bare-name entry in our maps.
            var qualifiedKey = v.ModuleName is not null
                ? $"{NameConverter.ClassNameFromModuleName(v.ModuleName)}.{sanitized}"
                : null;

            Log.Debug(
                "EmitCall: looking up variable '{Name}' (ModuleName={ModuleName}, sanitized={Sanitized}, qualifiedKey={QualifiedKey})",
                v.Name,
                v.ModuleName,
                sanitized,
                qualifiedKey
            );

            // Check defined methods — try qualified key first, then bare name
            if (
                (qualifiedKey is not null && _methods.TryGetValue(qualifiedKey, out var methodDef))
                || _methods.TryGetValue(sanitized, out methodDef)
            )
            {
                Log.Debug(
                    "EmitCall: resolved {FuncName} as user-defined method, isGeneric={IsGeneric}",
                    v.Name,
                    methodDef.GenericParameters.Count > 0
                );
                if (methodDef.GenericParameters.Count > 0)
                {
                    // Prefer the qualified key when present so overload-resolved calls
                    // pick up the correct module's funcType (the bare-name entry can be
                    // overwritten when another imported module exports the same name).
                    var lookupKey =
                        qualifiedKey is not null && _genericMethodTypes.ContainsKey(qualifiedKey)
                            ? qualifiedKey
                            : sanitized;
                    var typeArgs = InferTypeArgsForCall(lookupKey, methodDef, call.Args, call.Type);
                    var gim = new MethodSpecification(
                        methodDef,
                        new GenericInstanceMethodSignature(typeArgs)
                    );

                    // Emit arguments with boxing where value types are passed as reference type params
                    var sig = methodDef.Signature!;
                    for (var i = 0; i < call.Args.Count; i++)
                    {
                        EmitNode(call.Args[i], il, outerParams, locals);
                        if (i < sig.ParameterTypes.Count)
                        {
                            var paramSig = sig.ParameterTypes[i];
                            // Resolve generic parameter signatures to concrete types,
                            // including nested generics like SList<!0>
                            var resolvedParam = ResolveGenericParam(paramSig, typeArgs);
                            var argClrType = MapToReflectionClr(call.Args[i].Type);
                            if (argClrType.IsValueType && !resolvedParam.IsValueType)
                                il.Add(
                                    CilOpCodes.Box,
                                    _module.DefaultImporter.ImportType(argClrType)
                                );
                        }
                    }

                    il.Add(CilOpCodes.Call, gim);
                }
                else
                {
                    foreach (var arg in call.Args)
                        EmitNode(arg, il, outerParams, locals);
                    il.Add(CilOpCodes.Call, methodDef);
                }

                return;
            }

            // Check precompiled methods (prefer qualified key for overload-resolved calls)
            var precompiledMethod =
                qualifiedKey is not null
                && _precompiledMethods.TryGetValue(qualifiedKey, out var qualPm)
                    ? qualPm
                    : _precompiledMethods.GetValueOrDefault(sanitized);
            if (precompiledMethod is not null)
            {
                Log.Debug("EmitCall: resolved {FuncName} as precompiled method", v.Name);
                var reflectionMethod = qualifiedKey is not null
                    ? _precompiledReflectionMethods.GetValueOrDefault(qualifiedKey)
                        ?? _precompiledReflectionMethods.GetValueOrDefault(sanitized)
                    : _precompiledReflectionMethods.GetValueOrDefault(sanitized);
                if (reflectionMethod is { IsGenericMethodDefinition: true })
                {
                    var argClrTypes = call.Args.Select(a => MapToReflectionClr(a.Type)).ToArray();
                    var callRetClrType = call.Type is not null
                        ? MapToReflectionClr(call.Type)
                        : null;
                    var instantiated = reflectionMethod.MakeGenericMethod(
                        InferGenericTypeArgs(reflectionMethod, argClrTypes, callRetClrType)
                    );

                    // Emit arguments with boxing where value types are passed as reference types
                    var instParams = instantiated.GetParameters();
                    for (var i = 0; i < call.Args.Count; i++)
                    {
                        EmitNode(call.Args[i], il, outerParams, locals);
                        if (
                            i < instParams.Length
                            && argClrTypes[i].IsValueType
                            && !instParams[i].ParameterType.IsValueType
                        )
                            il.Add(
                                CilOpCodes.Box,
                                _module.DefaultImporter.ImportType(argClrTypes[i])
                            );
                    }

                    var importedGeneric = _module.DefaultImporter.ImportMethod(instantiated);
                    if (importedGeneric is MethodSpecification methodSpec)
                        il.Add(CilOpCodes.Call, methodSpec);
                    else
                        il.Add(CilOpCodes.Call, (IMethodDefOrRef)importedGeneric);
                }
                else
                {
                    // Non-generic precompiled method: emit args with boxing where needed
                    var preParams = reflectionMethod?.GetParameters();
                    for (var i = 0; i < call.Args.Count; i++)
                    {
                        EmitNode(call.Args[i], il, outerParams, locals);
                        if (preParams is null || i >= preParams.Length)
                            continue;
                        var argClrType = MapToReflectionClr(call.Args[i].Type);
                        if (argClrType.IsValueType && !preParams[i].ParameterType.IsValueType)
                            il.Add(CilOpCodes.Box, _module.DefaultImporter.ImportType(argClrType));
                    }

                    il.Add(CilOpCodes.Call, (IMethodDefOrRef)precompiledMethod);
                }

                return;
            }

            // Check locals (delegate invocation)
            if (locals.TryGetValue(v.Name, out var delegateLocal))
            {
                Log.Debug("EmitCall: resolved {FuncName} as local delegate invocation", v.Name);
                il.Add(CilOpCodes.Ldloc, delegateLocal);
                foreach (var arg in call.Args)
                    EmitNode(arg, il, outerParams, locals);
                EmitDelegateInvoke(call.Function.Type, il);
                return;
            }

            // Check parameters (delegate). AsmResolver's Parameters collection
            // excludes `this` and is always 0-indexed, so the i from outerParams
            // maps directly without adding _instanceArgOffset.
            for (var i = 0; i < outerParams.Count; i++)
                if (
                    outerParams[i].Name == v.Name
                    && outerParams[i].Type is ZType.ZFuncType or ZType.ZDelegateType
                )
                {
                    var method = il.Owner!.Owner!;
                    il.Add(CilOpCodes.Ldarg, method.Parameters[i]);
                    foreach (var arg in call.Args)
                        EmitNode(arg, il, outerParams, locals);
                    EmitDelegateInvoke(outerParams[i].Type, il);
                    return;
                }

            // Check current class fields (delegate-typed capture). Object
            // expressions thread captured delegate-typed outer bindings through
            // the synthesized class as fields; without this branch a method
            // body that invokes such a capture by name (e.g. `(f x)` where
            // `f` came from the enclosing `define`'s parameters) falls through
            // to the "Function not found" error since `f` is no longer in
            // outerParams once we descend into the object's method.
            if (
                _currentClassFields is not null
                && _currentClassFields.TryGetValue(v.Name, out var classFieldDelegate)
                && call.Function.Type is ZType.ZFuncType or ZType.ZDelegateType
            )
            {
                Log.Debug("EmitCall: resolved {FuncName} as captured class field delegate", v.Name);
                EmitLoadClassThis(il);
                il.Add(CilOpCodes.Ldfld, classFieldDelegate);
                foreach (var arg in call.Args)
                    EmitNode(arg, il, outerParams, locals);
                EmitDelegateInvoke(call.Function.Type, il);
                return;
            }

            // Check static fields
            if (_staticFields.TryGetValue(v.Name, out var staticField))
                if (call.Function.Type is ZType.ZFuncType or ZType.ZDelegateType)
                {
                    il.Add(CilOpCodes.Ldsfld, staticField);
                    foreach (var arg in call.Args)
                        EmitNode(arg, il, outerParams, locals);
                    EmitDelegateInvoke(call.Function.Type, il);
                    return;
                }

            // Check sibling instance methods (calls within the same class)
            if (
                _currentClassMethods is not null
                && _currentClassMethods.TryGetValue(v.Name, out var siblingMethod)
            )
            {
                Log.Debug("EmitCall: resolved {FuncName} as sibling instance method", v.Name);

                // Load 'this' — from __this field if inside async state machine, else Ldarg_0
                if (_moveNextCtx?.ThisField is { } siblingThisField)
                {
                    il.Add(CilOpCodes.Ldarg_0);
                    il.Add(CilOpCodes.Ldfld, siblingThisField);
                }
                else
                {
                    il.Add(CilOpCodes.Ldarg_0);
                }

                foreach (var arg in call.Args)
                    EmitNode(arg, il, outerParams, locals);

                il.Add(CilOpCodes.Callvirt, siblingMethod);
                return;
            }

            diagnostics.Error(
                $"Function '{v.Name}' not found for AsmResolver IL emission",
                call.Span
            );
            il.Add(CilOpCodes.Ldc_I4_0);
            return;
        }

        // Non-Var target: emit expression, then invoke
        EmitNode(call.Function, il, outerParams, locals);
        foreach (var arg in call.Args)
            EmitNode(arg, il, outerParams, locals);
        if (call.Function.Type is ZType.ZFuncType or ZType.ZDelegateType)
        {
            EmitDelegateInvoke(call.Function.Type, il);
            return;
        }

        diagnostics.Error(
            $"AsmResolver IL emission not implemented for Call with {call.Function.GetType().Name} target",
            call.Span
        );
        il.Add(CilOpCodes.Ldc_I4_0);
    }

    private void EmitMatch(
        IrNode.Match match,
        CilInstructionCollection il,
        IReadOnlyList<IrParam> outerParams,
        Dictionary<string, CilLocalVariable> locals
    )
    {
        Log.Debug(
            "IlEmitter.EmitMatch: {ArmCount} arms, scrutinee type={ScrutineeType}",
            match.Arms.Count,
            match.Scrutinee.Type
        );
        var scrutineeType = MapToClr(match.Scrutinee.Type);
        var scrutineeLocal = new CilLocalVariable(scrutineeType);
        il.Owner.LocalVariables.Add(scrutineeLocal);
        EmitNode(match.Scrutinee, il, outerParams, locals);
        il.Add(CilOpCodes.Stloc, scrutineeLocal);

        var endLabel = new CilInstructionLabel();
        var armLabels = new CilInstructionLabel[match.Arms.Count];
        for (var i = 0; i < match.Arms.Count; i++)
            armLabels[i] = new CilInstructionLabel();

        var failLabel = new CilInstructionLabel();
        var matchIsUnit = match.Type is ZType.ZPrimitiveType { Kind: PrimitiveKind.Unit };

        for (var i = 0; i < match.Arms.Count; i++)
        {
            armLabels[i].Instruction = il.Add(CilOpCodes.Nop);
            var arm = match.Arms[i];
            var nextLabel = i + 1 < match.Arms.Count ? armLabels[i + 1] : failLabel;

            EmitPatternTest(
                arm.Pattern,
                scrutineeLocal,
                match.Scrutinee.Type,
                nextLabel,
                il,
                outerParams,
                locals
            );
            EmitNode(arm.Body, il, outerParams, locals);
            ReconcileBranchStack(arm.Body.Type, matchIsUnit, il);
            il.Add(CilOpCodes.Br, endLabel);
        }

        failLabel.Instruction = il.Add(CilOpCodes.Nop);
        il.Add(CilOpCodes.Ldstr, "Non-exhaustive match");
        var exCtor = typeof(InvalidOperationException).GetConstructor([typeof(string)])!;
        il.Add(CilOpCodes.Newobj, _module.DefaultImporter.ImportMethod(exCtor));
        il.Add(CilOpCodes.Throw);

        endLabel.Instruction = il.Add(CilOpCodes.Nop);
    }

    private void EmitPatternTest(
        IrPattern pattern,
        CilLocalVariable scrutineeLocal,
        ZType scrutineeType,
        ICilLabel failLabel,
        CilInstructionCollection il,
        IReadOnlyList<IrParam> outerParams,
        Dictionary<string, CilLocalVariable> locals
    )
    {
        switch (pattern)
        {
            case IrPattern.Wildcard:
                break;

            case IrPattern.Variable v:
                var bindLocal = new CilLocalVariable(scrutineeLocal.VariableType);
                il.Owner.LocalVariables.Add(bindLocal);
                il.Add(CilOpCodes.Ldloc, scrutineeLocal);
                il.Add(CilOpCodes.Stloc, bindLocal);
                locals[v.Name] = bindLocal;
                break;

            case IrPattern.Literal { Value: string s }:
                il.Add(CilOpCodes.Ldloc, scrutineeLocal);
                il.Add(CilOpCodes.Ldstr, s);
                var strEquals = typeof(string).GetMethod(
                    "Equals",
                    BindingFlags.Public | BindingFlags.Static,
                    [typeof(string), typeof(string)]
                )!;
                il.Add(CilOpCodes.Call, _module.DefaultImporter.ImportMethod(strEquals));
                il.Add(CilOpCodes.Brfalse, failLabel);
                break;

            case IrPattern.Literal { Value: int n }:
                il.Add(CilOpCodes.Ldloc, scrutineeLocal);
                il.Add(CilOpCodes.Ldc_I4, n);
                il.Add(CilOpCodes.Ceq);
                il.Add(CilOpCodes.Brfalse, failLabel);
                break;

            // Without this case, EmitPatternTest fell through to a no-op for float
            // literal patterns, so the first arm body *always* ran — see fuzzer
            // seed 0xf0ab7e8f. Ceq on floats follows IEEE 754: -0.0 equals 0.0
            // and NaN equals nothing, matching C# `==` semantics.
            case IrPattern.Literal { Value: float f }:
                il.Add(CilOpCodes.Ldloc, scrutineeLocal);
                il.Add(CilOpCodes.Ldc_R4, f);
                il.Add(CilOpCodes.Ceq);
                il.Add(CilOpCodes.Brfalse, failLabel);
                break;

            case IrPattern.Literal { Value: bool b }:
                il.Add(CilOpCodes.Ldloc, scrutineeLocal);
                il.Add(b ? CilOpCodes.Ldc_I4_1 : CilOpCodes.Ldc_I4_0);
                il.Add(CilOpCodes.Ceq);
                il.Add(CilOpCodes.Brfalse, failLabel);
                break;

            case IrPattern.Constructor c:
                EmitConstructorPatternTest(
                    c,
                    scrutineeLocal,
                    scrutineeType,
                    failLabel,
                    il,
                    outerParams,
                    locals
                );
                break;

            case IrPattern.Tuple tup:
                EmitTuplePatternTest(
                    tup,
                    scrutineeLocal,
                    scrutineeType,
                    failLabel,
                    il,
                    outerParams,
                    locals
                );
                break;
        }
    }

    private void EmitTuplePatternTest(
        IrPattern.Tuple tup,
        CilLocalVariable scrutineeLocal,
        ZType scrutineeType,
        ICilLabel failLabel,
        CilInstructionCollection il,
        IReadOnlyList<IrParam> outerParams,
        Dictionary<string, CilLocalVariable> locals
    )
    {
        // AsmResolver-side signature of the scrutinee type — for tuples, a closed
        // GenericInstanceTypeSignature like ValueTuple`2<int32,int32>. The ldflda/ldloca
        // instruction loads the address of this closed type, so any ldfld immediately
        // after must use a field reference whose declaring type is also closed.
        // ImportField on a reflection FieldInfo anchors the declaring type at the open
        // ValueTuple`2, which ilverify rejects with a StackUnexpected error.
        var scrutineeSig = MapToClr(scrutineeType);
        var tupleGit = scrutineeSig as GenericInstanceTypeSignature;
        var tupleClrType = MapToReflectionClr(scrutineeType);
        var tupleZArgs =
            scrutineeType is ZType.ZNamedType namedTuple
            && _typeAliases.IsValueTupleName(namedTuple.Name)
                ? namedTuple.TypeArgs
                : null;
        for (var i = 0; i < tup.Elements.Count; i++)
        {
            var element = tup.Elements[i];
            if (element is IrPattern.Wildcard)
                continue;

            IFieldDescriptor fieldRef;
            TypeSignature fieldType;
            if (tupleGit is not null && i < tupleGit.TypeArguments.Count)
            {
                // Build a MemberReference on the closed tuple. The field signature
                // carries an open `!i` placeholder; the TypeSpec substitutes it with
                // the concrete type argument at runtime (matching what csc emits).
                var openParamSig = new GenericParameterSignature(
                    _module,
                    GenericParameterType.Type,
                    i
                );
                fieldRef = new MemberReference(
                    tupleGit.ToTypeDefOrRef(),
                    $"Item{i + 1}",
                    new FieldSignature(openParamSig)
                );
                fieldType = tupleGit.TypeArguments[i];
            }
            else
            {
                var field = tupleClrType.GetField($"Item{i + 1}");
                if (field is null)
                    continue;
                var importedField = _module.DefaultImporter.ImportField(field);
                fieldRef = importedField;
                fieldType = (importedField.Signature as FieldSignature)!.FieldType;
            }

            var fieldLocal = new CilLocalVariable(fieldType);
            il.Owner.LocalVariables.Add(fieldLocal);
            il.Add(CilOpCodes.Ldloca, scrutineeLocal);
            il.Add(CilOpCodes.Ldfld, fieldRef);
            il.Add(CilOpCodes.Stloc, fieldLocal);

            if (element is IrPattern.Variable v)
            {
                locals[v.Name] = fieldLocal;
                continue;
            }

            // Recurse for Literal/Constructor/nested-Tuple sub-patterns. Without this,
            // tuple patterns silently ignored every non-Variable/Wildcard element, so
            // (values 1 x) matched (5,10) — see fuzzer seed 0x32b37a3c.
            var elementZType =
                tupleZArgs is not null && i < tupleZArgs.Count ? tupleZArgs[i] : ZType.Unit;
            EmitPatternTest(element, fieldLocal, elementZType, failLabel, il, outerParams, locals);
        }
    }

    private void EmitConstructorPatternTest(
        IrPattern.Constructor ctor,
        CilLocalVariable scrutineeLocal,
        ZType scrutineeType,
        ICilLabel failLabel,
        CilInstructionCollection il,
        IReadOnlyList<IrParam> outerParams,
        Dictionary<string, CilLocalVariable> locals
    )
    {
        var caseTypeDefOrRef = ResolveConstructorCaseType(ctor.Name, scrutineeType);
        if (caseTypeDefOrRef is null)
        {
            diagnostics.Error(
                $"Cannot resolve constructor type '{ctor.Name}' for pattern match",
                SourceSpan.None
            );
            return;
        }

        // For records/structs the constructor name matches the scrutinee type itself, so the
        // type test is statically already true — skip the isinst, which also avoids
        // generating an unverifiable isinst against a value-type local for structs.
        var isSameType = scrutineeType is ZType.ZNamedType ssNamed && ssNamed.Name == ctor.Name;
        var isValueType = (caseTypeDefOrRef as TypeDefinition)?.IsValueType == true;
        // For generic user types, ResolveConstructorCaseType returns a TypeSpecification (not a
        // TypeDefinition), so the cast above misses generic structs. Recover IsValueType from the
        // underlying TypeDefinition recorded in _unionCaseTypes — without this, struct field
        // extraction emits `ldloc + callvirt` instead of `ldloca + call`, which fails ilverify
        // ("Callvirt on a value type method"). See fuzzer seed 0xe5d6b11a.
        if (
            !isValueType
            && scrutineeType is ZType.ZNamedType sNamedForVt
            && _unionCaseTypes.TryGetValue($"{sNamedForVt.Name}.{ctor.Name}", out var caseTd)
            && caseTd is TypeDefinition { IsValueType: true }
        )
            isValueType = true;
        var caseTypeSig = caseTypeDefOrRef.ToTypeSignature(isValueType);
        CilLocalVariable castLocal;
        if (isSameType)
        {
            castLocal = scrutineeLocal;
        }
        else
        {
            // Stloc-then-Ldloc pattern leaves stack empty on both success and fail paths
            // (avoids a Dup that would leak an extra value into the next-arm label).
            castLocal = new CilLocalVariable(caseTypeSig);
            il.Owner.LocalVariables.Add(castLocal);
            il.Add(CilOpCodes.Ldloc, scrutineeLocal);
            il.Add(CilOpCodes.Isinst, caseTypeDefOrRef);
            il.Add(CilOpCodes.Stloc, castLocal);
            il.Add(CilOpCodes.Ldloc, castLocal);
            il.Add(CilOpCodes.Brfalse, failLabel);
        }

        if (ctor.Fields.Count <= 0)
            return;
        string? caseKey = null;
        if (scrutineeType is ZType.ZNamedType named)
            caseKey = $"{named.Name}.{ctor.Name}";

        List<string> propertyNames;
        if (
            caseKey is not null
            && _unionCasePropertyNames.TryGetValue(caseKey, out var storedNames)
        )
            propertyNames = storedNames.ToList();
        else
            propertyNames = Enumerable.Range(0, ctor.Fields.Count).Select(_ => "Value").ToList();

        for (var i = 0; i < ctor.Fields.Count; i++)
        {
            var field = ctor.Fields[i];
            if (field is IrPattern.Wildcard)
                continue;
            var propName = i < propertyNames.Count ? propertyNames[i] : "Value";
            var getterKey = caseKey is not null ? $"{caseKey}.{propName}" : null;

            if (getterKey is null || !_unionCaseGetters.TryGetValue(getterKey, out var getter))
                continue;

            CilLocalVariable fieldLocal;
            if (caseTypeSig is GenericInstanceTypeSignature git)
            {
                // For generic union cases, create a MemberReference on the closed TypeSpec
                // keeping the original !0-based signature from the MethodDefinition
                IMethodDefOrRef resolvedGetter;
                TypeSignature fieldType;
                if (getter is MethodDefinition getterDef)
                {
                    resolvedGetter = new MemberReference(
                        git.ToTypeDefOrRef(),
                        getterDef.Name!,
                        getterDef.Signature!
                    );
                    // Resolve the local variable type: substitute !0 → actual type arg,
                    // including nested generics like SList<!0> → SList<actual>
                    fieldType = ResolveGenericParam(
                        getterDef.Signature!.ReturnType,
                        git.TypeArguments
                    );
                }
                else
                {
                    // Precompiled getter: create a MemberReference on the closed TypeSpec
                    // so the CLR can resolve the method on the concrete generic instance
                    var importedGetter = (IMethodDefOrRef)getter;
                    resolvedGetter = new MemberReference(
                        git.ToTypeDefOrRef(),
                        importedGetter.Name!,
                        importedGetter.Signature!
                    );
                    // Resolve the return type: substitute generic params with actual type args,
                    // including nested generics
                    fieldType = ResolveGenericParam(
                        importedGetter.Signature!.ReturnType,
                        git.TypeArguments
                    );
                }

                fieldLocal = new CilLocalVariable(fieldType);
                il.Owner.LocalVariables.Add(fieldLocal);
                if (isValueType)
                {
                    il.Add(CilOpCodes.Ldloca, castLocal);
                    il.Add(CilOpCodes.Call, resolvedGetter);
                }
                else
                {
                    il.Add(CilOpCodes.Ldloc, castLocal);
                    il.Add(CilOpCodes.Callvirt, resolvedGetter);
                }

                il.Add(CilOpCodes.Stloc, fieldLocal);
            }
            else
            {
                // Non-generic: use getter directly
                TypeSignature fieldType;
                if (getter is MethodDefinition getterDef2)
                    fieldType = getterDef2.Signature!.ReturnType;
                else
                    fieldType = MapToClr(scrutineeType);

                fieldLocal = new CilLocalVariable(fieldType);
                il.Owner.LocalVariables.Add(fieldLocal);
                if (isValueType)
                {
                    il.Add(CilOpCodes.Ldloca, castLocal);
                    il.Add(CilOpCodes.Call, (IMethodDefOrRef)getter);
                }
                else
                {
                    il.Add(CilOpCodes.Ldloc, castLocal);
                    il.Add(CilOpCodes.Callvirt, (IMethodDefOrRef)getter);
                }

                il.Add(CilOpCodes.Stloc, fieldLocal);
            }

            // Dispatch on sub-pattern kind: bind a Variable directly, or recurse for
            // nested Constructor/Tuple/Literal patterns with the extracted field as scrutinee.
            if (field is IrPattern.Variable v)
            {
                locals[v.Name] = fieldLocal;
            }
            else
            {
                var fieldZType = ComputeUnionFieldZType(scrutineeType, ctor.Name, i);
                if (fieldZType is not null)
                    EmitPatternTest(
                        field,
                        fieldLocal,
                        fieldZType,
                        failLabel,
                        il,
                        outerParams,
                        locals
                    );
            }
        }
    }

    private ZType? ComputeUnionFieldZType(ZType scrutineeType, string caseName, int fieldIdx)
    {
        if (scrutineeType is not ZType.ZNamedType named)
            return null;
        var key = $"{named.Name}.{caseName}";
        if (!_unionCaseFieldTypes.TryGetValue(key, out var entry))
            return null;
        if (fieldIdx >= entry.FieldTypes.Count)
            return null;

        var fieldTemplate = entry.FieldTypes[fieldIdx];
        if (entry.TypeParams.Count == 0)
            return fieldTemplate;

        var subst = new Dictionary<string, ZType>();
        for (var i = 0; i < entry.TypeParams.Count && i < named.TypeArgs.Count; i++)
            subst[entry.TypeParams[i]] = named.TypeArgs[i];

        return SubstituteTypeParams(fieldTemplate, subst);
    }

    private static ZType SubstituteTypeParams(ZType type, IReadOnlyDictionary<string, ZType> map)
    {
        return type switch
        {
            ZType.ZNamedType { TypeArgs.Count: 0 } nt
                when map.TryGetValue(nt.Name, out var mapped) => mapped,
            ZType.ZNamedType nt => new ZType.ZNamedType(
                nt.Name,
                nt.TypeArgs.Select(a => SubstituteTypeParams(a, map)).ToList()
            ),
            ZType.ZFuncType ft => new ZType.ZFuncType(
                ft.Params.Select(p => SubstituteTypeParams(p, map)).ToList(),
                SubstituteTypeParams(ft.Return, map),
                ft.IsVariadic
            ),
            ZType.ZNullableType nn => new ZType.ZNullableType(SubstituteTypeParams(nn.Inner, map)),
            _ => type,
        };
    }

    private void EmitMethodCall(
        IrNode.MethodCall node,
        CilInstructionCollection il,
        IReadOnlyList<IrParam> outerParams,
        Dictionary<string, CilLocalVariable> locals
    )
    {
        Log.Debug(
            "IlEmitter.EmitMethodCall: .{MethodName} on {ReceiverType}, isProperty={IsProperty}, isIndexer={IsIndexer}, argCount={ArgCount}",
            node.MethodName,
            node.Receiver.Type,
            node.IsProperty,
            node.IsIndexer,
            node.Args.Count
        );
        var receiverClrType = ResolveClrType(node.Receiver.Type);
        var isValueType = receiverClrType.IsValueType;

        // User-defined types being compiled in this module aren't loaded into the AppDomain yet,
        // so ResolveClrType falls back to System.Object. Consult the AsmResolver TypeDefinition
        // we registered for this name to recover the value-type flag.
        if (
            !isValueType
            && node.Receiver.Type is ZType.ZNamedType receiverNamedForVt
            && _userTypes.TryGetValue(receiverNamedForVt.Name, out var receiverUserTypeForVt)
            && receiverUserTypeForVt is TypeDefinition { IsValueType: true }
        )
            isValueType = true;

        EmitNode(node.Receiver, il, outerParams, locals);

        if (isValueType)
        {
            var receiverLocal = new CilLocalVariable(MapToClr(node.Receiver.Type));
            il.Owner.LocalVariables.Add(receiverLocal);
            il.Add(CilOpCodes.Stloc, receiverLocal);
            il.Add(CilOpCodes.Ldloca, receiverLocal);
        }

        if (node.IsProperty)
        {
            // Arrays: use ldlen for Length property
            if (receiverClrType.IsArray && node.MethodName == "Length")
            {
                il.Add(CilOpCodes.Ldlen);
                il.Add(CilOpCodes.Conv_I4);
                return;
            }

            // Try TypeDefinition first (for types defined in this compilation)
            if (
                node.Receiver.Type is ZType.ZNamedType named
                && _userTypes.TryGetValue(named.Name, out var typeRef)
                && typeRef is TypeDefinition td
            )
            {
                var sanitizedMethodName = Sanitize(node.MethodName);
                var asmProp = td.Properties.FirstOrDefault(p => p.Name == sanitizedMethodName);
                var asmGetter = asmProp
                    ?.Semantics.FirstOrDefault(s =>
                        s.Attributes == MethodSemanticsAttributes.Getter
                    )
                    ?.Method;
                if (asmGetter is not null)
                {
                    // For generic types, create a MemberReference on the closed generic instance.
                    // The IsValueType flag must match the underlying type so the metadata token
                    // is encoded as ELEMENT_TYPE_VALUETYPE vs ELEMENT_TYPE_CLASS correctly.
                    if (td.GenericParameters.Count > 0 && named.TypeArgs.Count > 0)
                    {
                        var typeArgs = named.TypeArgs.Select(ta => MapToClr(ta)).ToArray();
                        var closedSig = td.MakeGenericInstanceType(td.IsValueType, typeArgs);
                        var getterRef = new MemberReference(
                            closedSig.ToTypeDefOrRef(),
                            asmGetter.Name!,
                            asmGetter.Signature!
                        );
                        il.Add(isValueType ? CilOpCodes.Call : CilOpCodes.Callvirt, getterRef);
                    }
                    else
                    {
                        il.Add(isValueType ? CilOpCodes.Call : CilOpCodes.Callvirt, asmGetter);
                    }

                    return;
                }
            }

            // Resolve using the raw CLR type for proper generic instantiation
            var rawClrType = receiverClrType;
            var ilMappedType = MapToReflectionClr(node.Receiver.Type);
            if (ilMappedType != typeof(object))
                rawClrType = ilMappedType;
            // Record field accessors lower to MethodCall with the ZScheme field name
            // (e.g. "message"), but precompiled record types expose PascalCase CLR
            // properties (e.g. "Message"). Try the raw name first (CLR interop property
            // access already supplies the exact CLR name) then the sanitized form.
            var sanitizedPropName = Sanitize(node.MethodName);
            var prop =
                rawClrType.GetProperty(node.MethodName)
                ?? rawClrType.GetProperty(sanitizedPropName);
            if (prop is null && rawClrType.IsGenericType)
                prop =
                    rawClrType.GetGenericTypeDefinition().GetProperty(node.MethodName)
                    ?? rawClrType.GetGenericTypeDefinition().GetProperty(sanitizedPropName);
            if (prop is not null)
            {
                var getter = prop.GetGetMethod()!;
                il.Add(
                    isValueType ? CilOpCodes.Call : CilOpCodes.Callvirt,
                    ImportMethodWithGenericDeclaringType(getter, node.Receiver.Type)
                );
                return;
            }

            diagnostics.Warning(
                $"Property '{node.MethodName}' not found on {receiverClrType}",
                node.Span
            );
            il.Add(CilOpCodes.Ldc_I4_0);
            return;
        }

        if (node.IsPropertySet)
        {
            EmitNode(node.Args[0], il, outerParams, locals);
            var rawClrType = receiverClrType;
            var ilMappedType = MapToReflectionClr(node.Receiver.Type);
            if (ilMappedType != typeof(object))
                rawClrType = ilMappedType;
            var prop = rawClrType.GetProperty(node.MethodName);
            if (prop is null && rawClrType.IsGenericType)
                prop = rawClrType.GetGenericTypeDefinition().GetProperty(node.MethodName);
            if (prop is not null)
            {
                var setter = prop.GetSetMethod()!;
                il.Add(
                    isValueType ? CilOpCodes.Call : CilOpCodes.Callvirt,
                    ImportMethodWithGenericDeclaringType(setter, node.Receiver.Type)
                );
                return;
            }

            diagnostics.Error(
                $"Property setter '{node.MethodName}' not found on {receiverClrType}",
                node.Span
            );
            return;
        }

        if (node.IsIndexer)
        {
            EmitNode(node.Args[0], il, outerParams, locals);
            if (receiverClrType.IsArray)
            {
                var elemType = MapToClr(((ZType.ZNamedType)node.Receiver.Type).TypeArgs[0]);
                il.Add(CilOpCodes.Ldelem, elemType.ToTypeDefOrRef());
                return;
            }

            var indexer = ResolveIndexerAccessor(receiverClrType, "get_");
            if (indexer is not null)
            {
                il.Add(
                    isValueType ? CilOpCodes.Call : CilOpCodes.Callvirt,
                    ImportMethodWithGenericDeclaringType(indexer, node.Receiver.Type)
                );
                return;
            }

            diagnostics.Error($"Indexer not found on {receiverClrType}", node.Span);
            il.Add(CilOpCodes.Ldc_I4_0);
            return;
        }

        if (node.IsIndexerSet)
        {
            EmitNode(node.Args[0], il, outerParams, locals);
            EmitNode(node.Args[1], il, outerParams, locals);
            if (receiverClrType.IsArray)
            {
                var elemType = MapToClr(((ZType.ZNamedType)node.Receiver.Type).TypeArgs[0]);
                il.Add(CilOpCodes.Stelem, elemType.ToTypeDefOrRef());
                return;
            }

            var setter = ResolveIndexerAccessor(receiverClrType, "set_");
            if (setter is not null)
            {
                il.Add(
                    isValueType ? CilOpCodes.Call : CilOpCodes.Callvirt,
                    ImportMethodWithGenericDeclaringType(setter, node.Receiver.Type)
                );
                return;
            }

            diagnostics.Error($"Indexer setter not found on {receiverClrType}", node.Span);
            return;
        }

        if (node.OutParams is { Count: > 0 })
        {
            EmitOutParamMethodCall(node, receiverClrType, isValueType, il, outerParams, locals);
            return;
        }

        // User-defined class/struct in this compilation: resolve method via its TypeDefinition,
        // since the type isn't yet loaded into the current AppDomain for reflection.
        if (
            node.Receiver.Type is ZType.ZNamedType namedRecv
            && _userTypes.TryGetValue(namedRecv.Name, out var userTypeRef)
            && userTypeRef is TypeDefinition userTd
        )
        {
            var sanitizedName = Sanitize(node.MethodName);
            var mdef = userTd.Methods.FirstOrDefault(m =>
                !m.IsConstructor
                && !m.IsStatic
                && m.Name == sanitizedName
                && m.Parameters.Count == node.Args.Count
            );
            if (mdef is not null)
            {
                // ResolveClrType returned object for this type (it isn't loaded yet), so the
                // value-type address dance at the top of this method was skipped. Do it here.
                if (userTd.IsValueType && !isValueType)
                {
                    var receiverLocal = new CilLocalVariable(userTd.ToTypeSignature(true));
                    il.Owner.LocalVariables.Add(receiverLocal);
                    il.Add(CilOpCodes.Stloc, receiverLocal);
                    il.Add(CilOpCodes.Ldloca, receiverLocal);
                }

                foreach (var arg in node.Args)
                    EmitNode(arg, il, outerParams, locals);

                IMethodDescriptor methodRef = mdef;
                if (userTd.GenericParameters.Count > 0 && namedRecv.TypeArgs.Count > 0)
                {
                    var typeArgs = namedRecv.TypeArgs.Select(ta => MapToClr(ta)).ToArray();
                    var closedSig = userTd.MakeGenericInstanceType(false, typeArgs);
                    methodRef = new MemberReference(
                        closedSig.ToTypeDefOrRef(),
                        mdef.Name!,
                        mdef.Signature!
                    );
                }

                il.Add(userTd.IsValueType ? CilOpCodes.Call : CilOpCodes.Callvirt, methodRef);
                return;
            }
        }

        var argTypes = node.Args.Select(a => ResolveClrType(a.Type)).ToArray();
        MethodInfo? methodInfo;
        try
        {
            methodInfo =
                receiverClrType.GetMethod(node.MethodName, argTypes)
                ?? receiverClrType.GetMethod(
                    node.MethodName,
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy,
                    null,
                    argTypes,
                    null
                );
        }
        catch (AmbiguousMatchException)
        {
            // Fall back to matching by arg count when multiple overloads exist
            methodInfo = receiverClrType
                .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(m =>
                    m.Name == node.MethodName && m.GetParameters().Length == argTypes.Length
                );
        }

        // Fallback: match by arg count if exact type match failed
        methodInfo ??= receiverClrType
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m =>
                m.Name == node.MethodName && m.GetParameters().Length == argTypes.Length
            );

        // Optional-aware fallback: the supplied args are a prefix and every remaining
        // parameter is optional (e.g. WebApplication.RunAsync(string? url = null)).
        methodInfo ??= receiverClrType
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy)
            .FirstOrDefault(m =>
                m.Name == node.MethodName && ParamsMatchWithOptionals(m, argTypes.Length)
            );

        // Emit arguments with boxing/nullable wrapping where needed
        var methodParams = methodInfo?.GetParameters();
        for (var i = 0; i < node.Args.Count; i++)
        {
            EmitNode(node.Args[i], il, outerParams, locals);
            if (methodParams is null || i >= methodParams.Length)
                continue;
            var paramType = methodParams[i].ParameterType;
            if (argTypes[i].IsValueType && !paramType.IsValueType)
                il.Add(CilOpCodes.Box, _module.DefaultImporter.ImportType(argTypes[i]));

            // Wrap T → Nullable<T> when parameter is Nullable<T> and argument is T
            if (
                !paramType.IsGenericType
                || paramType.GetGenericTypeDefinition() != typeof(Nullable<>)
                || node.Args[i].Type is ZType.ZNullableType
            )
                continue;
            var targetSig = _module
                .DefaultImporter.ImportType(paramType)
                .ToTypeSignature(paramType.IsValueType);
            EmitNullableWrapIfNeeded(node.Args[i], targetSig, il);
        }

        // Supply defaults for any omitted trailing optional parameters.
        if (methodParams is not null)
            for (var i = node.Args.Count; i < methodParams.Length; i++)
                EmitDefaultArgument(methodParams[i], il);

        if (methodInfo is not null && ParamsMatchWithOptionals(methodInfo, argTypes.Length))
        {
            il.Add(
                isValueType ? CilOpCodes.Call : CilOpCodes.Callvirt,
                ImportMethodWithGenericDeclaringType(methodInfo, node.Receiver.Type)
            );
            return;
        }

        // Fallback: check for instance properties
        var instanceProp = receiverClrType.GetProperty(
            node.MethodName,
            BindingFlags.Public | BindingFlags.Instance
        );
        if (instanceProp?.GetGetMethod() is { } propGetter && node.Args.Count == 0)
        {
            il.Add(
                isValueType ? CilOpCodes.Call : CilOpCodes.Callvirt,
                _module.DefaultImporter.ImportMethod(propGetter)
            );
            return;
        }

        diagnostics.Warning(
            $"Property '{node.MethodName}' not found on {receiverClrType}",
            node.Span
        );
        il.Add(CilOpCodes.Ldc_I4_0);
    }

    private void EmitOutParamMethodCall(
        IrNode.MethodCall node,
        Type receiverClrType,
        bool isValueType,
        CilInstructionCollection il,
        IReadOnlyList<IrParam> outerParams,
        Dictionary<string, CilLocalVariable> locals
    )
    {
        var outParams = node.OutParams!;

        // Resolve the method using the full parameter list (including out params)
        var method = FindMethodWithOutParams(
            receiverClrType,
            node.MethodName,
            node.Args,
            outParams,
            BindingFlags.Public | BindingFlags.Instance
        );
        if (method is null)
        {
            diagnostics.Error(
                $"Method '{node.MethodName}' with out parameters not found on {receiverClrType}",
                node.Span
            );
            il.Add(CilOpCodes.Ldc_I4_0);
            return;
        }

        // Allocate locals for each out parameter
        // Derive out-param types from the node's ValueTuple return type (which has properly resolved
        // type vars) rather than from CLR reflection (which uses CLR generic param names like "T"
        // that don't match the function's generated type params "T0", "T1")
        var outLocals = new List<CilLocalVariable>();
        var tupleTypeArgs =
            node.Type is ZType.ZNamedType vtType && _typeAliases.IsValueTupleName(vtType.Name)
                ? vtType.TypeArgs
                : null;
        for (var opIdx = 0; opIdx < outParams.Count; opIdx++)
        {
            // The tuple type is (returnValue, outParam0, outParam1, ...) — out params start at index 1
            var elemType =
                tupleTypeArgs is not null && opIdx + 1 < tupleTypeArgs.Count
                    ? tupleTypeArgs[opIdx + 1]
                    : outParams[opIdx].ElementType;
            var outLocal = new CilLocalVariable(MapToClr(elemType));
            il.Owner.LocalVariables.Add(outLocal);
            outLocals.Add(outLocal);
        }

        // Emit arguments interleaved with ldloca for out params
        var outParamSet = outParams.ToDictionary(op => op.OriginalIndex);
        var totalParams = node.Args.Count + outParams.Count;
        var visibleIdx = 0;
        var outIdx = 0;
        for (var i = 0; i < totalParams; i++)
            if (outParamSet.ContainsKey(i))
            {
                il.Add(CilOpCodes.Ldloca, outLocals[outIdx++]);
            }
            else
            {
                EmitNode(node.Args[visibleIdx++], il, outerParams, locals);
                // Box value types if needed for object parameters
                var methodParam = method.GetParameters()[i];
                if (
                    methodParam.ParameterType == typeof(object)
                    && node.Args[visibleIdx - 1].Type is ZType.ZPrimitiveType
                )
                    il.Add(
                        CilOpCodes.Box,
                        MapToClr(node.Args[visibleIdx - 1].Type).ToTypeDefOrRef()
                    );
            }

        // Call the method
        il.Add(
            isValueType ? CilOpCodes.Call : CilOpCodes.Callvirt,
            ImportMethodWithGenericDeclaringType(method, node.Receiver.Type)
        );

        // Store the return value in a local
        var retClrType = MapToClr(_clrInterop.MapClrTypeToZType(method.ReturnType));
        var retLocal = new CilLocalVariable(retClrType);
        il.Owner.LocalVariables.Add(retLocal);
        il.Add(CilOpCodes.Stloc, retLocal);

        // Construct the ValueTuple: load ret, out0, out1, ...
        il.Add(CilOpCodes.Ldloc, retLocal);
        foreach (var outLocal in outLocals)
            il.Add(CilOpCodes.Ldloc, outLocal);

        // Build the tuple type from the method's actual return type + out param element types
        // (node.Type may not be a ValueTuple if overload resolution changed after IR lowering)
        var tupleType = node.Type;
        if (tupleType is not ZType.ZNamedType { Name: "ValueTuple" })
        {
            var tupleElements = new List<ZType>
            {
                _clrInterop.MapClrTypeToZType(method.ReturnType),
            };
            for (var oi = 0; oi < outParams.Count; oi++)
                // Derive element type from node's ValueTuple type if available, else from out-param info
                tupleElements.Add(
                    tupleTypeArgs is not null && oi + 1 < tupleTypeArgs.Count
                        ? tupleTypeArgs[oi + 1]
                        : outParams[oi].ElementType
                );

            tupleType = new ZType.ZNamedType("ValueTuple", tupleElements);
        }

        EmitValueTupleNewobj(tupleType, il, node.Span);
    }

    /// <summary>
    ///     Creates a closed ValueTuple GenericInstanceTypeSignature from ZType args,
    ///     using AsmResolver-aware type mapping (preserves generic type parameters).
    ///     Uses the same import path as AsmResolverTypeMapper for consistency.
    /// </summary>
    private GenericInstanceTypeSignature MakeValueTupleSig(IReadOnlyList<ZType> typeArgs)
    {
        // MapToClr delegates to AsmResolverTypeMapper which now handles ValueTuple
        var sig = MapToClr(new ZType.ZNamedType("ValueTuple", typeArgs.ToList()));
        return (GenericInstanceTypeSignature)sig;
    }

    /// <summary>
    ///     Emits a newobj instruction for a ValueTuple, using AsmResolver-aware type mapping
    ///     to preserve generic type parameters (e.g., ValueTuple&lt;bool, !!0&gt;).
    ///     Expects the tuple element values to already be on the stack.
    /// </summary>
    private void EmitValueTupleNewobj(ZType tupleType, CilInstructionCollection il, SourceSpan span)
    {
        if (
            tupleType is ZType.ZNamedType { TypeArgs.Count: > 0 } vtNt
            && _typeAliases.IsValueTupleName(vtNt.Name)
        )
        {
            var tupleGit = MakeValueTupleSig(vtNt.TypeArgs);
            // Use !0, !1, etc. for the ctor parameter types — the TypeSpec provides actual types
            var ctorParamTypes = Enumerable
                .Range(0, vtNt.TypeArgs.Count)
                .Select(i =>
                    (TypeSignature)
                        new GenericParameterSignature(_module, GenericParameterType.Type, i)
                )
                .ToArray();
            var ctorSig = MethodSignature.CreateInstance(
                _module.CorLibTypeFactory.Void,
                ctorParamTypes
            );
            var closedCtor = new MemberReference(tupleGit.ToTypeDefOrRef(), ".ctor", ctorSig);
            il.Add(CilOpCodes.Newobj, closedCtor);
        }
        else
        {
            var tupleClrType = MapToReflectionClr(tupleType);
            var tupleCtor = tupleClrType.GetConstructors().FirstOrDefault();
            if (tupleCtor is not null)
            {
                il.Add(CilOpCodes.Newobj, _module.DefaultImporter.ImportMethod(tupleCtor));
            }
            else
            {
                diagnostics.Error(
                    $"Could not find ValueTuple constructor for type {tupleType}",
                    span
                );
                il.Add(CilOpCodes.Ldc_I4_0);
            }
        }
    }

    private void EmitMutableArrayNew(
        IrNode.MutableArrayNew node,
        CilInstructionCollection il,
        IReadOnlyList<IrParam> outerParams,
        Dictionary<string, CilLocalVariable> locals
    )
    {
        var elementSigType = MapToClr(node.ElementType);

        il.Add(CilOpCodes.Ldc_I4, node.Elements.Count);
        il.Add(CilOpCodes.Newarr, elementSigType.ToTypeDefOrRef());

        for (var i = 0; i < node.Elements.Count; i++)
        {
            il.Add(CilOpCodes.Dup);
            il.Add(CilOpCodes.Ldc_I4, i);
            EmitNode(node.Elements[i], il, outerParams, locals);
            // Box value types when element type is object
            if (elementSigType == _module.CorLibTypeFactory.Object)
            {
                var elemClrType = MapToClr(node.Elements[i].Type);
                if (elemClrType.IsValueType)
                    il.Add(CilOpCodes.Box, elemClrType.ToTypeDefOrRef());
            }

            il.Add(CilOpCodes.Stelem, elementSigType.ToTypeDefOrRef());
        }
    }

    private void EmitLambda(
        IrNode.FuncDef funcDef,
        CilInstructionCollection il,
        IReadOnlyList<IrParam> outerParams,
        Dictionary<string, CilLocalVariable> locals
    )
    {
        var lambdaName = $"__lambda_{_lambdaId++}_{funcDef.Name}";
        Log.Debug(
            "IlEmitter.EmitLambda: {LambdaName}, {ParamCount} params",
            lambdaName,
            funcDef.Params.Count
        );
        var paramNames = funcDef.Params.Select(p => p.Name).ToHashSet();
        var freeVars = FindFreeVars(funcDef.Body, paramNames);

        var captures = new List<(string Name, TypeSignature SigType, Type ClrType)>();
        var capturedNames = new HashSet<string>();
        foreach (var fv in freeVars)
            if (locals.TryGetValue(fv, out var loc))
            {
                captures.Add(
                    (
                        fv,
                        loc.VariableType,
                        MapToReflectionClr(GetVarType(fv, outerParams, locals) ?? ZType.Unit)
                    )
                );
                capturedNames.Add(fv);
            }
            else
            {
                foreach (var t in outerParams)
                    if (t.Name == fv)
                    {
                        captures.Add((fv, MapToClr(t.Type), MapToReflectionClr(t.Type)));
                        capturedNames.Add(fv);
                        break;
                    }
            }

        // A lambda inside a class instance method that reads or writes a class field
        // cannot be a plain static method: the field access needs the enclosing class's
        // `this`, but the static lambda's `ldarg.0` is its first parameter. Forcing the
        // closure path and capturing `this` as a synthetic field fixes this — subsequent
        // class-field emission inside the body routes through the captured local
        // (see EmitLoadClassThis).
        //
        // FindFreeVars traverses nested FuncDefs, so any class field referenced by a
        // nested lambda shows up in `freeVars`. A free var that also names an outer
        // local/param binds there (shadowing the field) and is already in
        // `capturedNames`, so we only flag unresolved free vars that happen to match a
        // class field. Writes via SetField aren't in freeVars, so we also scan for those.
        const string thisCaptureName = "<>this";
        var needsThisCapture = false;
        if (_currentClassFields is { Count: > 0 } && _currentTypeDefinition is not null)
        {
            foreach (var fv in freeVars)
            {
                if (capturedNames.Contains(fv) || !_currentClassFields.ContainsKey(fv))
                    continue;
                // A free var that names a class field but also names a top-level
                // function resolves to the function at the call site (EmitCall
                // checks `_methods` first), so the lambda doesn't actually need
                // `<>this` for it. Capturing `<>this` here is wasteful and, when
                // this lambda lives inside a nested object's ctor where `this`
                // refers to a different type than the enclosing class, produces
                // a stack-unexpected (the wrong-typed `ldarg.0` flows into the
                // `<>this` field of the enclosing-class type).
                if (_methods.ContainsKey(Sanitize(fv)) || _staticFields.ContainsKey(fv))
                    continue;
                needsThisCapture = true;
                break;
            }

            if (!needsThisCapture)
                needsThisCapture = BodyContainsClassFieldSet(funcDef.Body, _currentClassFields);
        }

        if (needsThisCapture)
        {
            var thisSig = _currentTypeDefinition!.ToTypeSignature();
            captures.Add((thisCaptureName, thisSig, typeof(object)));
        }

        if (captures.Count == 0)
        {
            // When a lambda inside a generic function references the outer function's type
            // variables, we must propagate those type parameters to the lambda method.
            // Otherwise the method signature contains !!0 references without defining
            // any generic parameters, producing invalid IL (BadImageFormatException).
            IReadOnlyList<string>? inheritedTypeParams = null;
            TypeSignature[]? outerTypeArgs = null;

            if (_currentTypeVarMap is { Count: > 0 } && funcDef.Type is ZType.ZFuncType lambdaFt)
            {
                var lambdaFreeVars = Substitution.FreeVars(lambdaFt).OrderBy(id => id).ToList();
                if (lambdaFreeVars.Count > 0)
                {
                    // Build reverse map: type var ID → param name
                    var varIdToName = new Dictionary<int, string>();
                    if (_currentTypeParamMap is not null)
                        foreach (var (name, sig) in _currentTypeParamMap)
                        foreach (var (varId, varSig) in _currentTypeVarMap)
                            if (sig == varSig)
                                varIdToName[varId] = name;

                    var referencedParams = new List<string>();
                    var typeArgsList = new List<TypeSignature>();
                    foreach (var varId in lambdaFreeVars)
                        if (
                            varIdToName.TryGetValue(varId, out var paramName)
                            && _currentTypeVarMap.TryGetValue(varId, out var sig)
                        )
                        {
                            referencedParams.Add(paramName);
                            typeArgsList.Add(sig);
                        }

                    if (referencedParams.Count > 0)
                    {
                        inheritedTypeParams = referencedParams;
                        outerTypeArgs = typeArgsList.ToArray();
                    }
                }
            }

            var emitFunc = inheritedTypeParams is { Count: > 0 }
                ? funcDef with
                {
                    Name = lambdaName,
                    TypeParams = inheritedTypeParams,
                }
                : funcDef with
                {
                    Name = lambdaName,
                };

            // A capture-less lambda is emitted as its own static method. Its body
            // must not inherit the enclosing async method's MoveNext context: that
            // context drives state-machine field stores (e.g. EmitLet's `ldarg.0;
            // stfld <SM field>`), but inside the lambda `ldarg.0` is the lambda's
            // first parameter, not the state-machine `this`. Leaving it set lets a
            // lambda-local `let` whose name collides with a hoisted async local emit
            // `stfld` against an int argument, which fails ilverify (StackUnexpected).
            // The closure path below clears it for the same reason.
            var savedCapturelessMoveNextCtx = _moveNextCtx;
            _moveNextCtx = null;
            EmitFuncDef(emitFunc, _currentTypeDefinition!);
            _moveNextCtx = savedCapturelessMoveNextCtx;
            var lambdaMethod = _methods[Sanitize(lambdaName)];
            il.Add(CilOpCodes.Ldnull);

            if (outerTypeArgs is { Length: > 0 })
            {
                var gim = new MethodSpecification(
                    lambdaMethod,
                    new GenericInstanceMethodSignature(outerTypeArgs)
                );
                il.Add(CilOpCodes.Ldftn, gim);
            }
            else
            {
                il.Add(CilOpCodes.Ldftn, lambdaMethod);
            }
        }
        else
        {
            var closureType = new TypeDefinition(
                "",
                $"<>c__{lambdaName}",
                TypeAttributes.NestedPrivate | TypeAttributes.Sealed | TypeAttributes.Class
            )
            {
                BaseType = _module.CorLibTypeFactory.Object.ToTypeDefOrRef(),
            };
            _currentTypeDefinition!.NestedTypes.Add(closureType);

            // When a closure is created inside a generic method, its capture fields
            // and `Invoke` signature can mention the method's generic parameters
            // (e.g. `compose`'s `Func<!!0,!!1>` captures). A nested type may not
            // reference the enclosing method's generic parameters, so mirror them
            // onto the closure type as its own type parameters (same index order)
            // and rewrite every `!!i` reference to the type parameter `!i`. The
            // construction site below then instantiates the closure over the
            // method's parameters. Without this the emitted IL fails verification
            // and throws InvalidProgramException at JIT time.
            var methodTypeParams = _currentTypeParamMap is { Count: > 0 }
                ? _currentTypeParamMap
                    .Where(kv =>
                        kv.Value
                            is GenericParameterSignature
                            {
                                ParameterType: GenericParameterType.Method
                            }
                    )
                    .OrderBy(kv => ((GenericParameterSignature)kv.Value).Index)
                    .Select(kv => kv.Key)
                    .ToList()
                : [];
            var closureIsGeneric = methodTypeParams.Count > 0;
            foreach (var tp in methodTypeParams)
                closureType.GenericParameters.Add(new GenericParameter(tp));

            var captureFields = new List<FieldDefinition>();
            foreach (var (name, sigType, _) in captures)
            {
                var fieldSig = closureIsGeneric ? MethodGpToTypeGp(sigType) : sigType;
                var fb = new FieldDefinition(
                    name,
                    FieldAttributes.Public,
                    new FieldSignature(fieldSig)
                );
                closureType.Fields.Add(fb);
                captureFields.Add(fb);
            }

            var closureCtor = new MethodDefinition(
                ".ctor",
                MethodAttributes.Public
                    | MethodAttributes.HideBySig
                    | MethodAttributes.SpecialName
                    | MethodAttributes.RuntimeSpecialName,
                MethodSignature.CreateInstance(_module.CorLibTypeFactory.Void)
            );
            closureType.Methods.Add(closureCtor);
            var closureCtorBody = new CilMethodBody { InitializeLocals = true };
            closureCtor.MethodBody = closureCtorBody;
            var closureCtorIl = closureCtorBody.Instructions;
            closureCtorIl.Add(CilOpCodes.Ldarg_0);
            closureCtorIl.Add(
                CilOpCodes.Call,
                _module.DefaultImporter.ImportMethod(
                    typeof(object).GetConstructor(Type.EmptyTypes)!
                )
            );
            closureCtorIl.Add(CilOpCodes.Ret);

            // Within the closure type's own members, generic references must be
            // type-kind (`!i`), not the method-kind (`!!i`) that MapToClr emits in
            // the enclosing generic method. Swap the type-variable maps to their
            // type-kind equivalents for the duration of signature and body emission,
            // then restore them so the construction site (which runs in the
            // enclosing method's context) keeps using method-kind references.
            var savedClosureTypeVarMap = _currentTypeVarMap;
            var savedClosureTypeParamMap = _currentTypeParamMap;
            if (closureIsGeneric)
            {
                _currentTypeVarMap = _currentTypeVarMap!.ToDictionary(
                    kv => kv.Key,
                    kv => MethodGpToTypeGp(kv.Value)
                );
                _currentTypeParamMap = _currentTypeParamMap!.ToDictionary(
                    kv => kv.Key,
                    kv => MethodGpToTypeGp(kv.Value)
                );
            }

            var lambdaReturnType = MapReturnTypeToClr(funcDef.ReturnType);
            var lambdaParamTypes = funcDef.Params.Select(p => MapToClr(p.Type)).ToArray();
            var lambdaMethod = new MethodDefinition(
                "Invoke",
                MethodAttributes.Public,
                MethodSignature.CreateInstance(lambdaReturnType, lambdaParamTypes)
            );
            for (var i = 0; i < funcDef.Params.Count; i++)
                lambdaMethod.ParameterDefinitions.Add(
                    new ParameterDefinition((ushort)(i + 1), funcDef.Params[i].Name, 0)
                );
            closureType.Methods.Add(lambdaMethod);

            var lambdaBody = new CilMethodBody { InitializeLocals = true };
            lambdaMethod.MethodBody = lambdaBody;
            var lambdaIl = lambdaBody.Instructions;
            var lambdaLocals = new Dictionary<string, CilLocalVariable>();

            CilLocalVariable? thisCaptureLocal = null;
            for (var i = 0; i < captures.Count; i++)
            {
                var captureLocal = new CilLocalVariable(captureFields[i].Signature!.FieldType);
                lambdaBody.LocalVariables.Add(captureLocal);
                lambdaIl.Add(CilOpCodes.Ldarg_0);
                lambdaIl.Add(CilOpCodes.Ldfld, ResolveSelfField(closureType, captureFields[i]));
                lambdaIl.Add(CilOpCodes.Stloc, captureLocal);
                if (captures[i].Name == thisCaptureName)
                    thisCaptureLocal = captureLocal;
                else
                    lambdaLocals[captures[i].Name] = captureLocal;
            }

            var savedOffset = _instanceArgOffset;
            var savedReturnType = _currentFuncReturnType;
            var savedThisLocal = _currentClassThisLocal;
            var savedMoveNextCtx = _moveNextCtx;
            _instanceArgOffset = 1;
            _currentFuncReturnType = funcDef.ReturnType;
            // Inside the lambda's own method we are no longer in the enclosing
            // async state machine's MoveNext — field access must go through the
            // captured `this` local (if any), not `ldarg.0; ldfld __this`.
            _moveNextCtx = null;
            // BodyReferencesClassFields traverses nested FuncDefs, so thisCaptureLocal
            // is non-null iff any descendant references class fields. When null, no
            // descendant needs a this-holder and leaving it null is safe.
            _currentClassThisLocal = thisCaptureLocal;
            EmitNode(funcDef.Body, lambdaIl, funcDef.Params, lambdaLocals);
            _currentFuncReturnType = savedReturnType;
            _instanceArgOffset = savedOffset;
            _currentClassThisLocal = savedThisLocal;
            _moveNextCtx = savedMoveNextCtx;
            lambdaIl.Add(CilOpCodes.Ret);

            // Restore the enclosing method's generic context before emitting the
            // construction site (which loads captured method args/locals).
            _currentTypeVarMap = savedClosureTypeVarMap;
            _currentTypeParamMap = savedClosureTypeParamMap;

            // When the closure type is generic, every reference to its members from
            // the enclosing method must be anchored on the closure instantiated over
            // the method's generic parameters (`closure<!!0,!!1,...>`); the member
            // signatures themselves use the type-kind placeholders the runtime then
            // substitutes. For a non-generic closure the bare definitions are correct.
            IMethodDefOrRef ctorRef = closureCtor;
            IMethodDefOrRef invokeRef = lambdaMethod;
            var fieldRefs = captureFields.Cast<IFieldDescriptor>().ToList();
            if (closureIsGeneric)
            {
                var methodArgs = Enumerable
                    .Range(0, methodTypeParams.Count)
                    .Select(i =>
                        (TypeSignature)
                            new GenericParameterSignature(_module, GenericParameterType.Method, i)
                    )
                    .ToArray();
                var closedClosure = new GenericInstanceTypeSignature(
                    closureType,
                    false,
                    methodArgs
                ).ToTypeDefOrRef();
                ctorRef = new MemberReference(closedClosure, ".ctor", closureCtor.Signature);
                invokeRef = new MemberReference(closedClosure, "Invoke", lambdaMethod.Signature);
                fieldRefs = captureFields
                    .Select(f =>
                        (IFieldDescriptor)new MemberReference(closedClosure, f.Name!, f.Signature!)
                    )
                    .ToList();
            }

            // Emit closure instantiation
            il.Add(CilOpCodes.Newobj, ctorRef);
            for (var i = 0; i < captures.Count; i++)
            {
                il.Add(CilOpCodes.Dup);
                if (captures[i].Name == thisCaptureName)
                    EmitLoadClassThis(il);
                else
                    EmitLoadVar(captures[i].Name, funcDef.Span, il, outerParams, locals);
                il.Add(CilOpCodes.Stfld, fieldRefs[i]);
            }

            il.Add(CilOpCodes.Ldftn, invokeRef);
        }

        il.Add(CilOpCodes.Newobj, ImportDelegateConstructor(funcDef.Type));
    }

    /// <summary>
    ///     Emits the prelude for a base-constructor call: pushes <c>this</c> and the
    ///     evaluated super args. If any super arg contains a <c>with-handlers</c>
    ///     (try/catch), all super args are spilled into locals first so the try block
    ///     is entered with an empty evaluation stack — required by the IL verifier.
    /// </summary>
    private void EmitSuperArgsWithThis(
        IReadOnlyList<IrNode> superArgs,
        CilMethodBody body,
        CilInstructionCollection il,
        IReadOnlyList<IrParam> outerParams,
        Dictionary<string, CilLocalVariable> argLocals
    )
    {
        var needsSpill = superArgs.Any(WithHandlersHoister.ContainsWithHandlers);
        if (needsSpill)
        {
            var spilled = new List<CilLocalVariable>(superArgs.Count);
            foreach (var arg in superArgs)
            {
                EmitNode(arg, il, outerParams, argLocals);
                var local = new CilLocalVariable(MapToClr(arg.Type ?? ZType.Unit));
                body.LocalVariables.Add(local);
                il.Add(CilOpCodes.Stloc, local);
                spilled.Add(local);
            }

            il.Add(CilOpCodes.Ldarg_0);
            foreach (var local in spilled)
                il.Add(CilOpCodes.Ldloc, local);
        }
        else
        {
            il.Add(CilOpCodes.Ldarg_0);
            foreach (var arg in superArgs)
                EmitNode(arg, il, outerParams, argLocals);
        }
    }

    private void EmitObjectExpr(
        IrNode.ObjectExpr objectExpr,
        CilInstructionCollection il,
        IReadOnlyList<IrParam> outerParams,
        Dictionary<string, CilLocalVariable> locals
    )
    {
        Log.Debug(
            "IlEmitter.EmitObjectExpr: {InterfaceCount} interfaces, {MethodCount} methods, baseClass={BaseClass}",
            objectExpr.InterfaceNames.Count,
            objectExpr.Methods.Count,
            objectExpr.BaseClassName ?? "(none)"
        );
        // Capture analysis: collect free vars across all methods AND the
        // explicit constructor's super args and body exprs. Super args and
        // body exprs are emitted inside the anonymous class's constructor,
        // so any reference to an outer-scope variable must be captured.
        var allFreeVars = new HashSet<string>();
        foreach (var method in objectExpr.Methods)
        {
            var paramNames = method.Params.Select(p => p.Name).ToHashSet();
            allFreeVars.UnionWith(FindFreeVars(method.Body, paramNames));
        }

        if (objectExpr.Constructor is { } ctorDecl)
        {
            var ctorParamNames = ctorDecl.Params.Select(p => p.Name).ToHashSet();
            if (ctorDecl.SuperArgs is not null)
                foreach (var arg in ctorDecl.SuperArgs)
                    allFreeVars.UnionWith(FindFreeVars(arg, ctorParamNames));
            foreach (var bodyExpr in ctorDecl.BodyExprs)
                allFreeVars.UnionWith(FindFreeVars(bodyExpr, ctorParamNames));
            foreach (var (_, value) in ctorDecl.FieldSets)
                allFreeVars.UnionWith(FindFreeVars(value, ctorParamNames));
        }

        // Track the original ZType alongside the TypeSignature so nested
        // captures (a lambda/object inside the constructor that re-captures
        // one of our captures) can preserve the original type for delegate
        // detection in EmitCall. Locals and class fields don't carry a
        // ZType in the emitter, so recover one by walking the object's IR
        // for a Var node with the same name. Without this, a captured
        // function-typed local (e.g. an outer lambda's delegate-typed
        // closure capture) would lose its ZType.ZFuncType, and EmitCall in
        // the inner ctor would reject the call site as "function not found".
        var captures = new List<(string Name, TypeSignature SigType, ZType ZType)>();

        ZType? RecoverZType(string fv)
        {
            ZType? t = null;
            if (objectExpr.Constructor is { } c)
            {
                if (c.SuperArgs is not null)
                    foreach (var a in c.SuperArgs)
                    {
                        t = FindVarType(a, fv);
                        if (t is not null)
                            return t;
                    }

                foreach (var (_, v) in c.FieldSets)
                {
                    t = FindVarType(v, fv);
                    if (t is not null)
                        return t;
                }

                foreach (var b in c.BodyExprs)
                {
                    t = FindVarType(b, fv);
                    if (t is not null)
                        return t;
                }
            }

            foreach (var m in objectExpr.Methods)
            {
                t = FindVarType(m.Body, fv);
                if (t is not null)
                    return t;
            }

            return null;
        }

        foreach (var fv in allFreeVars)
        {
            if (locals.TryGetValue(fv, out var loc))
            {
                captures.Add((fv, loc.VariableType, RecoverZType(fv) ?? ZType.Unit));
                continue;
            }

            var foundParam = false;
            foreach (var t in outerParams)
                if (t.Name == fv)
                {
                    captures.Add((fv, MapToClr(t.Type), t.Type));
                    foundParam = true;
                    break;
                }

            if (foundParam)
                continue;

            // If the name resolves to a top-level function (or a static field
            // holding a delegate) at the enclosing scope, it's statically
            // reachable from inside the anonymous class — EmitCall/EmitLoadVar
            // will route through `_methods` / `_staticFields`, so no capture is
            // needed. Skip even when a class field with the same name exists,
            // since the IR Var node was already resolved to the function by
            // type inference (the recovered ZType is a function type that
            // wouldn't fit the class field's slot anyway). Without this, the
            // top-level function gets shadowed and captured at the field's
            // type, which produces stack-imbalance IL when the value flows
            // into a partial-closure field expecting the function type.
            var recoveredZType = RecoverZType(fv);
            if (
                recoveredZType is ZType.ZFuncType
                && (_methods.ContainsKey(Sanitize(fv)) || _staticFields.ContainsKey(fv))
            )
                continue;

            // A free var that resolves to an enclosing-scope class field
            // (e.g. when this ObjectExpr is nested inside another object's
            // method body, or inside a class instance method) must also be
            // captured. The call-site EmitLoadVar can read the field via
            // `this`, but inside our anonymous class's methods `Ldarg.0` is
            // *our* instance, not the enclosing one — without a capture, the
            // var would be unresolved and emission would fall through to the
            // "Variable 'X' not found" error path.
            if (
                _currentClassFields is not null
                && _currentClassFields.TryGetValue(fv, out var classField)
            )
                captures.Add((fv, classField.Signature!.FieldType, recoveredZType ?? ZType.Unit));
        }

        // Create anonymous class type
        var objClassName = $"<>__Object_{_objectExprId++}";
        var objType = new TypeDefinition(
            "",
            objClassName,
            TypeAttributes.NestedPrivate | TypeAttributes.Sealed | TypeAttributes.Class
        );

        // Resolve base type
        var baseTypeRef = _module.CorLibTypeFactory.Object.ToTypeDefOrRef();
        TypeDefinition? baseTypeDef = null;
        var inheritedMethodNames = new HashSet<string>();
        if (
            objectExpr.BaseClassName is not null
            && _asmClassInfos.TryGetValue(objectExpr.BaseClassName, out var baseClassInfo)
        )
        {
            baseTypeRef = baseClassInfo.TypeDef;
            baseTypeDef = baseClassInfo.TypeDef;
            inheritedMethodNames = GetAsmInheritedMethodNames(objectExpr.BaseClassName);
        }

        objType.BaseType = baseTypeRef;

        var containerType =
            _currentTypeDefinition
            ?? _module.TopLevelTypes.First(t => t.Name == Sanitize(className));
        containerType.NestedTypes.Add(objType);

        // Add interface implementations
        foreach (var ifaceName in objectExpr.InterfaceNames)
        {
            var ifaceRef = ResolveInterfaceType(ifaceName);
            if (ifaceRef is not null)
                objType.Interfaces.Add(new InterfaceImplementation(ifaceRef));
            else
                diagnostics.Error(
                    $"Interface '{ifaceName}' not found for object expression",
                    objectExpr.Span
                );
        }

        // Add capture fields
        var captureFields = new List<FieldDefinition>();
        foreach (var (name, sigType, _) in captures)
        {
            var fb = new FieldDefinition(name, FieldAttributes.Public, new FieldSignature(sigType));
            objType.Fields.Add(fb);
            captureFields.Add(fb);
        }

        // Emit constructor. Captures are threaded through as constructor
        // parameters so super args and body exprs can reference them
        // directly through EmitLoadVar's outerParams path — before the
        // previous implementation, super args were emitted into the ctor
        // body but indexed against the enclosing method's parameters, which
        // caused ArgumentOutOfRangeException when the ctor was zero-arg.
        var ctorParamTypes = captures.Select(c => c.SigType).ToArray();
        var ctor = new MethodDefinition(
            ".ctor",
            MethodAttributes.Public
                | MethodAttributes.HideBySig
                | MethodAttributes.SpecialName
                | MethodAttributes.RuntimeSpecialName,
            MethodSignature.CreateInstance(_module.CorLibTypeFactory.Void, ctorParamTypes)
        );
        for (var pi = 0; pi < captures.Count; pi++)
            ctor.ParameterDefinitions.Add(
                new ParameterDefinition((ushort)(pi + 1), captures[pi].Name, 0)
            );
        objType.Methods.Add(ctor);
        var ctorBody = new CilMethodBody { InitializeLocals = true };
        ctor.MethodBody = ctorBody;
        var ctorIl = ctorBody.Instructions;

        // Build the IrParam list that mirrors the ctor's parameter order
        // so EmitLoadVar / EmitCall resolve capture references against the
        // ctor's parameters (positional index matches captureFields[i]).
        var ctorOuterParams = captures.Select(c => new IrParam(c.Name, c.ZType)).ToList();

        // The constructor body runs in a fresh method frame: `ldarg.0` is the
        // object's `this`, not the enclosing async MoveNext's state machine. As
        // with the object's methods below, clear the MoveNext context (and any
        // captured-this local) so super args and ctor body expressions don't emit
        // state-machine field stores against the object's `this` — a let in the
        // ctor body whose name collides with a hoisted async local would otherwise
        // `stfld` the wrong receiver and fail ilverify (StackUnexpected).
        var savedCtorMoveNextCtx = _moveNextCtx;
        var savedCtorThisLocal = _currentClassThisLocal;
        _moveNextCtx = null;
        _currentClassThisLocal = null;

        // Call base constructor — super args reference the ctor params via
        // the synthesized outerParams list above.
        if (objectExpr.Constructor?.SuperArgs is { Count: > 0 } superArgs)
        {
            var savedCtorOffset = _instanceArgOffset;
            _instanceArgOffset = 1;
            var ctorArgLocals = new Dictionary<string, CilLocalVariable>();
            EmitSuperArgsWithThis(superArgs, ctorBody, ctorIl, ctorOuterParams, ctorArgLocals);
            _instanceArgOffset = savedCtorOffset;
            var baseCtorRef = ResolveAsmBaseConstructor(baseTypeDef, superArgs.Count);
            ctorIl.Add(CilOpCodes.Call, (IMethodDefOrRef)baseCtorRef);
        }
        else
        {
            ctorIl.Add(CilOpCodes.Ldarg_0);
            var baseCtorRef = ResolveAsmBaseConstructor(baseTypeDef, 0);
            ctorIl.Add(CilOpCodes.Call, (IMethodDefOrRef)baseCtorRef);
        }

        // Store capture params into capture fields so method bodies can
        // read them via this.<field>.
        for (var i = 0; i < captures.Count; i++)
        {
            ctorIl.Add(CilOpCodes.Ldarg_0);
            ctorIl.Add(CilOpCodes.Ldarg, ctor.Parameters[i]);
            ctorIl.Add(CilOpCodes.Stfld, captureFields[i]);
        }

        // Emit constructor body expressions if present. Captures are now
        // reachable both as ctor parameters (via ctorOuterParams) and as
        // fields (via the field map below) — EmitLoadVar prefers the
        // parameter path, which is fine.
        if (objectExpr.Constructor is { BodyExprs: { Count: > 0 } ctorBodyExprs })
        {
            var savedCtorOffset = _instanceArgOffset;
            _instanceArgOffset = 1;
            var ctorLocals2 = new Dictionary<string, CilLocalVariable>();
            foreach (var bodyExpr in ctorBodyExprs)
            {
                EmitNode(bodyExpr, ctorIl, ctorOuterParams, ctorLocals2);
                if (
                    bodyExpr.Type
                    is not null
                        and not ZType.ZPrimitiveType { Kind: PrimitiveKind.Unit }
                )
                    ctorIl.Add(CilOpCodes.Pop);
            }

            _instanceArgOffset = savedCtorOffset;
        }

        _moveNextCtx = savedCtorMoveNextCtx;
        _currentClassThisLocal = savedCtorThisLocal;

        ctorIl.Add(CilOpCodes.Ret);

        // Build field map for method bodies (captures accessible via this.field)
        var fieldMap = new Dictionary<string, FieldDefinition>();
        for (var i = 0; i < captures.Count; i++)
            fieldMap[captures[i].Name] = captureFields[i];

        // Also include inherited fields from base class
        if (baseTypeDef is not null)
            AddAsmInheritedFieldsToMap(baseTypeDef, fieldMap);

        // Emit methods
        foreach (var method in objectExpr.Methods)
        {
            var retType =
                method.ReturnType == ZType.Unit
                    ? _module.CorLibTypeFactory.Void
                    : MapToClr(method.ReturnType);
            var methodParamTypes = method.Params.Select(p => MapToClr(p.Type)).ToArray();

            // Determine method attributes based on whether this overrides a base method
            var isOverride = inheritedMethodNames.Contains(method.Name);
            var methodAttrs =
                MethodAttributes.Public
                | MethodAttributes.Virtual
                | MethodAttributes.HideBySig
                | MethodAttributes.Final;
            if (!isOverride)
                methodAttrs |= MethodAttributes.NewSlot;

            var mb = new MethodDefinition(
                Sanitize(method.Name),
                methodAttrs,
                MethodSignature.CreateInstance(retType, methodParamTypes)
            );
            for (var pi = 0; pi < method.Params.Count; pi++)
                mb.ParameterDefinitions.Add(
                    new ParameterDefinition((ushort)(pi + 1), method.Params[pi].Name, 0)
                );
            objType.Methods.Add(mb);

            var methodBody = new CilMethodBody { InitializeLocals = true };
            mb.MethodBody = methodBody;
            var methodIl = methodBody.Instructions;
            var methodLocals = new Dictionary<string, CilLocalVariable>();

            var savedOffset = _instanceArgOffset;
            var savedReturnType = _currentFuncReturnType;
            var savedClassFields = _currentClassFields;
            var savedTypeDef = _currentTypeDefinition;
            var savedBaseTypeDef = _currentBaseTypeDefinition;
            // Inside the anonymous class's own method we are in a fresh frame:
            // `ldarg.0` is the object's `this`, and there is no enclosing async
            // MoveNext to read fields from. Leaving these set would carry over a
            // captured-this local (or `<MoveNext>` state-machine field) from the
            // outer scope, so an `EmitLoadClassThis` for the object's own field
            // would emit `ldloc N` referencing a local in a different method body
            // — producing an UnrecognizedLocalNumber at offset 0.
            var savedThisLocal = _currentClassThisLocal;
            var savedMoveNextCtx = _moveNextCtx;
            _instanceArgOffset = 1;
            _currentFuncReturnType = method.ReturnType;
            _currentClassFields = fieldMap;
            _currentTypeDefinition = objType;
            _currentBaseTypeDefinition = baseTypeDef;
            _currentClassThisLocal = null;
            _moveNextCtx = null;

            EmitNode(method.Body, methodIl, method.Params, methodLocals);

            _currentClassFields = savedClassFields;
            _instanceArgOffset = savedOffset;
            _currentFuncReturnType = savedReturnType;
            _currentTypeDefinition = savedTypeDef;
            _currentBaseTypeDefinition = savedBaseTypeDef;
            _currentClassThisLocal = savedThisLocal;
            _moveNextCtx = savedMoveNextCtx;

            if (method.ReturnType is ZType.ZPrimitiveType { Kind: PrimitiveKind.Unit })
                if (
                    method.Body.Type
                    is not null
                        and not ZType.ZPrimitiveType { Kind: PrimitiveKind.Unit }
                )
                    methodIl.Add(CilOpCodes.Pop);
            methodIl.Add(CilOpCodes.Ret);
        }

        // Emit instantiation: evaluate captures in the outer context and
        // pass them to the constructor. The constructor stores them into
        // fields, so no follow-up Dup+Stfld is needed.
        for (var i = 0; i < captures.Count; i++)
            EmitLoadVar(captures[i].Name, objectExpr.Span, il, outerParams, locals);
        il.Add(CilOpCodes.Newobj, ctor);
    }

    private void EmitTupleNew(
        IrNode.TupleNew node,
        CilInstructionCollection il,
        IReadOnlyList<IrParam> outerParams,
        Dictionary<string, CilLocalVariable> locals
    )
    {
        foreach (var element in node.Elements)
            EmitNode(element, il, outerParams, locals);

        EmitValueTupleNewobj(node.Type, il, node.Span);
    }

    private void EmitRecordNew(
        IrNode.RecordNew node,
        CilInstructionCollection il,
        IReadOnlyList<IrParam> outerParams,
        Dictionary<string, CilLocalVariable> locals
    )
    {
        foreach (var (_, value) in node.Fields)
            EmitNode(value, il, outerParams, locals);

        if (_userTypes.TryGetValue(node.TypeName, out var typeRef))
        {
            if (typeRef is TypeDefinition td)
            {
                var ctor = td.Methods.FirstOrDefault(m =>
                    m is { IsConstructor: true, IsStatic: false }
                    && m.Parameters.Count == node.Fields.Count
                );
                if (ctor is not null)
                {
                    // For generic records, the open ctor MethodDefinition has signature
                    // `Foo::.ctor(!0, !0)`, but `newobj` needs a MemberReference on the
                    // closed instance — `Foo<int32>::.ctor(!0, !0)` — or ilverify rejects
                    // the result as a bare `Foo` reference where `Foo<int32>` is expected.
                    if (
                        td.GenericParameters.Count > 0
                        && node.Type is ZType.ZNamedType nt
                        && nt.TypeArgs.Count == td.GenericParameters.Count
                    )
                    {
                        var typeArgs = nt.TypeArgs.Select(ta => MapToClr(ta)).ToArray();
                        var closedSig = td.MakeGenericInstanceType(td.IsValueType, typeArgs);
                        var ctorRef = new MemberReference(
                            closedSig.ToTypeDefOrRef(),
                            ".ctor",
                            ctor.Signature!
                        );
                        il.Add(CilOpCodes.Newobj, ctorRef);
                    }
                    else
                    {
                        il.Add(CilOpCodes.Newobj, ctor);
                    }

                    return;
                }
            }
            else
            {
                // Precompiled type — resolve constructor via reflection
                var clrType = ResolveClrTypeForTypeRef(typeRef);
                var ctorInfo = clrType
                    ?.GetConstructors()
                    .FirstOrDefault(c => c.GetParameters().Length == node.Fields.Count);
                if (ctorInfo is not null)
                {
                    il.Add(
                        CilOpCodes.Newobj,
                        (IMethodDefOrRef)_module.DefaultImporter.ImportMethod(ctorInfo)
                    );
                    return;
                }
            }
        }

        diagnostics.Error(
            $"Type '{node.TypeName}' not found or has no matching constructor for AsmResolver IL emission",
            node.Span
        );
        il.Add(CilOpCodes.Ldc_I4_0);
    }

    private void EmitRecordWith(
        IrNode.RecordWith node,
        CilInstructionCollection il,
        IReadOnlyList<IrParam> outerParams,
        Dictionary<string, CilLocalVariable> locals
    )
    {
        // Resolve the record type — prefer node.Type (set from inference), fall back to
        // node.Record.Type, then to the declared TypeName.
        var resolvedName = node.TypeName;
        IReadOnlyList<ZType> typeArgs = [];
        if (node.Type is ZType.ZNamedType nt1)
        {
            resolvedName = nt1.Name;
            typeArgs = nt1.TypeArgs;
        }
        else if (node.Record.Type is ZType.ZNamedType nt2)
        {
            resolvedName = nt2.Name;
            typeArgs = nt2.TypeArgs;
        }

        if (
            !_userTypes.TryGetValue(resolvedName, out var typeRef)
            || typeRef is not TypeDefinition td
        )
        {
            diagnostics.Error(
                $"'with' expression: type '{resolvedName}' not found or is not a user-defined record",
                node.Span
            );
            il.Add(CilOpCodes.Ldnull);
            return;
        }

        // For generic records, resolve all members against the closed generic instance.
        // Pass td.IsValueType so the generic instance signature is correctly tagged.
        GenericInstanceTypeSignature? closedSig = null;
        if (td.GenericParameters.Count > 0 && typeArgs.Count == td.GenericParameters.Count)
        {
            var mapped = typeArgs.Select(ta => MapToClr(ta)).ToArray();
            closedSig = td.MakeGenericInstanceType(td.IsValueType, mapped);
        }

        IMethodDefOrRef ResolveMethod(MethodDefinition m)
        {
            return closedSig is null
                ? m
                : new MemberReference(closedSig.ToTypeDefOrRef(), m.Name!, m.Signature!);
        }

        if (td.IsValueType)
        {
            // Struct path: copy source onto stack into a local, mutate via init setters by address,
            // then load the local. Value-type instance methods must be invoked with `call`, never
            // `callvirt`, and need a managed pointer (ldloca) as the receiver.
            var localSig = closedSig is not null ? closedSig : td.ToTypeSignature();
            var tmp = new CilLocalVariable(localSig);
            il.Owner.LocalVariables.Add(tmp);

            EmitNode(node.Record, il, outerParams, locals);
            il.Add(CilOpCodes.Stloc, tmp);

            foreach (var (fieldName, value) in node.Updates)
            {
                var sanitized = Sanitize(fieldName);
                var prop = td.Properties.FirstOrDefault(p => p.Name == sanitized);
                var setter = prop
                    ?.Semantics.FirstOrDefault(s =>
                        s.Attributes == MethodSemanticsAttributes.Setter
                    )
                    ?.Method;
                if (setter is null)
                {
                    diagnostics.Error(
                        $"'with' expression: struct '{resolvedName}' has no init setter for field '{fieldName}'",
                        node.Span
                    );
                    continue;
                }

                il.Add(CilOpCodes.Ldloca, tmp);
                EmitNode(value, il, outerParams, locals);
                il.Add(CilOpCodes.Call, ResolveMethod(setter));
            }

            il.Add(CilOpCodes.Ldloc, tmp);
            return;
        }

        var cloneMethod = td.Methods.FirstOrDefault(m => m.Name == "<Clone>$");
        if (cloneMethod is null)
        {
            diagnostics.Error(
                $"'with' expression: type '{resolvedName}' has no <Clone>$ method",
                node.Span
            );
            il.Add(CilOpCodes.Ldnull);
            return;
        }

        // 1) Push the base record and call <Clone>$.
        EmitNode(node.Record, il, outerParams, locals);
        il.Add(CilOpCodes.Callvirt, ResolveMethod(cloneMethod));

        // 2) For each update: dup the cloned reference, push the value, call the init setter.
        foreach (var (fieldName, value) in node.Updates)
        {
            var sanitized = Sanitize(fieldName);
            var prop = td.Properties.FirstOrDefault(p => p.Name == sanitized);
            var setter = prop
                ?.Semantics.FirstOrDefault(s => s.Attributes == MethodSemanticsAttributes.Setter)
                ?.Method;
            if (setter is null)
            {
                diagnostics.Error(
                    $"'with' expression: record '{resolvedName}' has no init setter for field '{fieldName}'",
                    node.Span
                );
                continue;
            }

            il.Add(CilOpCodes.Dup);
            EmitNode(value, il, outerParams, locals);
            il.Add(CilOpCodes.Callvirt, ResolveMethod(setter));
        }
    }

    private void EmitFieldGet(
        IrNode.FieldGet node,
        CilInstructionCollection il,
        IReadOnlyList<IrParam> outerParams,
        Dictionary<string, CilLocalVariable> locals
    )
    {
        var recordType = node.Record.Type;

        // ValueTuple field access — Item1, Item2, etc. are public fields (not properties)
        if (
            recordType is ZType.ZNamedType vtNt
            && _typeAliases.IsValueTupleName(vtNt.Name)
            && node.FieldName.StartsWith("Item")
        )
        {
            EmitNode(node.Record, il, outerParams, locals);
            var tupleGit = MakeValueTupleSig(vtNt.TypeArgs);
            var tupleLocal = new CilLocalVariable(tupleGit);
            il.Owner.LocalVariables.Add(tupleLocal);
            il.Owner.InitializeLocals = true;
            il.Add(CilOpCodes.Stloc, tupleLocal);
            il.Add(CilOpCodes.Ldloca, tupleLocal);

            // Construct field reference with generic parameter type (!0, !1, etc.)
            var fieldIdx = int.Parse(node.FieldName["Item".Length..]) - 1;
            if (fieldIdx >= 0 && fieldIdx < vtNt.TypeArgs.Count)
            {
                // Use !N (type generic param) for the field type — the TypeSpec provides the actual types
                var gpSig = new GenericParameterSignature(
                    _module,
                    GenericParameterType.Type,
                    fieldIdx
                );
                var fieldRef = new MemberReference(
                    tupleGit.ToTypeDefOrRef(),
                    node.FieldName,
                    new FieldSignature(gpSig)
                );
                il.Add(CilOpCodes.Ldfld, fieldRef);
                return;
            }
        }

        EmitNode(node.Record, il, outerParams, locals);
        if (
            recordType is ZType.ZNamedType named
            && _userTypes.TryGetValue(named.Name, out var typeRef)
        )
        {
            if (typeRef is TypeDefinition td)
            {
                var prop = td.Properties.FirstOrDefault(p => p.Name == Sanitize(node.FieldName));
                var getter = prop
                    ?.Semantics.FirstOrDefault(s =>
                        s.Attributes == MethodSemanticsAttributes.Getter
                    )
                    ?.Method;
                if (getter is not null)
                {
                    // For generic types, create a MemberReference on the closed generic instance
                    if (td.GenericParameters.Count > 0 && named.TypeArgs.Count > 0)
                    {
                        var typeArgs = named.TypeArgs.Select(ta => MapToClr(ta)).ToArray();
                        var closedSig = td.MakeGenericInstanceType(false, typeArgs);
                        var getterRef = new MemberReference(
                            closedSig.ToTypeDefOrRef(),
                            getter.Name!,
                            getter.Signature!
                        );
                        il.Add(CilOpCodes.Callvirt, getterRef);
                    }
                    else
                    {
                        il.Add(CilOpCodes.Callvirt, getter);
                    }

                    return;
                }
            }
            else
            {
                // Precompiled type — resolve via reflection
                var clrType = ResolveClrTypeForTypeRef(typeRef);
                var prop = clrType?.GetProperty(Sanitize(node.FieldName));
                if (prop?.GetGetMethod() != null)
                {
                    il.Add(
                        CilOpCodes.Callvirt,
                        (IMethodDefOrRef)_module.DefaultImporter.ImportMethod(prop.GetGetMethod()!)
                    );
                    return;
                }
            }
        }

        diagnostics.Error(
            $"Field '{node.FieldName}' not found for AsmResolver IL emission",
            node.Span
        );
        il.Add(CilOpCodes.Ldc_I4_0);
    }

    private void EmitUnionCaseNew(
        IrNode.UnionCaseNew node,
        CilInstructionCollection il,
        IReadOnlyList<IrParam> outerParams,
        Dictionary<string, CilLocalVariable> locals
    )
    {
        foreach (var arg in node.Args)
            EmitNode(arg, il, outerParams, locals);

        var caseKey = $"{node.UnionName}.{node.CaseName}";
        Log.Debug("EmitUnionCaseNew: caseKey={CaseKey}, nodeType={NodeType}", caseKey, node.Type);
        if (_unionCaseTypes.TryGetValue(caseKey, out var caseTypeRef))
        {
            Log.Debug(
                "EmitUnionCaseNew: found caseTypeRef type={RefType}, fullName={FullName}",
                caseTypeRef.GetType().Name,
                caseTypeRef.FullName
            );
            if (
                node.Type is ZType.ZNamedType { TypeArgs.Count: > 0 } nt
                && caseTypeRef is TypeDefinition { GenericParameters.Count: > 0 } caseTd
            )
            {
                var typeArgs = nt.TypeArgs.Select(ta => MapToClr(ta)).ToArray();
                var closedSig = caseTd.MakeGenericInstanceType(false, typeArgs);

                var openCtor = caseTd.Methods.First(m =>
                    m.IsConstructor && !m.IsStatic && m.Parameters.Count == node.Args.Count
                );
                // Keep open ctor parameter types as !0, !1 etc. — the TypeSpec provides the actual types
                var openCtorParamTypes = openCtor.Parameters.Select(p => p.ParameterType).ToArray();
                var closedCtor = new MemberReference(
                    closedSig.ToTypeDefOrRef(),
                    ".ctor",
                    MethodSignature.CreateInstance(
                        _module.CorLibTypeFactory.Void,
                        openCtorParamTypes
                    )
                );
                il.Add(CilOpCodes.Newobj, closedCtor);
                return;
            }

            // Non-generic or imported non-TypeDefinition
            if (caseTypeRef is TypeDefinition caseTd2)
            {
                var ctor = caseTd2.Methods.FirstOrDefault(m =>
                    m is { IsConstructor: true, IsStatic: false }
                    && m.Parameters.Count == node.Args.Count
                );
                if (ctor is not null)
                {
                    il.Add(CilOpCodes.Newobj, ctor);
                    return;
                }
            }
            else
            {
                // Precompiled: construct newobj using the imported type reference directly
                if (node.Type is ZType.ZNamedType { TypeArgs.Count: > 0 } ntPre)
                {
                    // Generic case: create closed generic instance (e.g., Some<int>)
                    var typeArgs = ntPre.TypeArgs.Select(ta => MapToClr(ta)).ToArray();
                    var closedSig = new GenericInstanceTypeSignature(caseTypeRef, false, typeArgs);

                    // Resolve the open constructor via reflection to get correct generic param indices
                    var clrCaseType = ResolveClrTypeForTypeRef(caseTypeRef);
                    var openCtor = clrCaseType
                        ?.GetConstructors()
                        .FirstOrDefault(c => c.GetParameters().Length == node.Args.Count);
                    if (openCtor != null)
                    {
                        var importedCtor = (IMethodDefOrRef)
                            _module.DefaultImporter.ImportMethod(openCtor);
                        var closedCtor = new MemberReference(
                            closedSig.ToTypeDefOrRef(),
                            ".ctor",
                            importedCtor.Signature!
                        );
                        il.Add(CilOpCodes.Newobj, closedCtor);
                        return;
                    }
                }

                // Non-generic: resolve constructor via reflection
                var clrType = ResolveClrTypeForTypeRef(caseTypeRef);
                if (clrType is not null)
                {
                    var argTypes = node.Args.Select(a => ResolveClrType(a.Type)).ToArray();
                    var ctor =
                        clrType.GetConstructor(argTypes)
                        ?? clrType
                            .GetConstructors()
                            .FirstOrDefault(c => c.GetParameters().Length == node.Args.Count);
                    if (ctor is not null)
                    {
                        il.Add(CilOpCodes.Newobj, _module.DefaultImporter.ImportMethod(ctor));
                        return;
                    }
                }
            }
        }

        diagnostics.Error(
            $"Union case '{caseKey}' not found for AsmResolver IL emission",
            node.Span
        );
        il.Add(CilOpCodes.Ldc_I4_0);
    }

    private void EmitAwait(
        IrNode.Await awaitNode,
        CilInstructionCollection il,
        IReadOnlyList<IrParam> outerParams,
        Dictionary<string, CilLocalVariable> locals
    )
    {
        // Emit the task expression (pushes Task<T> or Task on stack)
        EmitNode(awaitNode.Expr, il, outerParams, locals);

        // Resolve GetAwaiter() and GetResult() via reflection on the CLR task type
        var taskClrType = MapToReflectionClr(awaitNode.Expr.Type);
        var getAwaiterMethod = taskClrType.GetMethod("GetAwaiter", Type.EmptyTypes)!;
        var awaiterType = getAwaiterMethod.ReturnType;
        var getResultMethod = awaiterType.GetMethod("GetResult", Type.EmptyTypes)!;

        // Call GetAwaiter() on the Task
        il.Add(
            CilOpCodes.Call,
            (IMethodDefOrRef)_module.DefaultImporter.ImportMethod(getAwaiterMethod)
        );

        // TaskAwaiter is a struct — store in local and load address for instance method call
        var awaiterLocal = new CilLocalVariable(
            _module.DefaultImporter.ImportType(awaiterType).ToTypeSignature(awaiterType.IsValueType)
        );
        il.Owner.LocalVariables.Add(awaiterLocal);
        il.Add(CilOpCodes.Stloc, awaiterLocal);
        il.Add(CilOpCodes.Ldloca, awaiterLocal);

        // Call GetResult() — returns T for Task<T>, void for non-generic Task
        il.Add(
            CilOpCodes.Call,
            (IMethodDefOrRef)_module.DefaultImporter.ImportMethod(getResultMethod)
        );
    }

    private void EmitWithHandlers(
        IrNode.WithHandlers node,
        CilInstructionCollection il,
        IReadOnlyList<IrParam> outerParams,
        Dictionary<string, CilLocalVariable> locals
    )
    {
        // CIL forbids branching into a catch handler from outside, so if any
        // handler body contains an `await`, the resume label would land inside
        // a protected handler region and the top-of-MoveNext dispatch would
        // produce a BranchIntoHandler verifier error. Lower such with-handlers
        // by lifting the handler bodies *out* of the catch: the catch only
        // captures the exception and tags it, then the body runs after the
        // try region in regular (reachable) code.
        if (
            _moveNextCtx is not null
            && node.Handlers.Any(h => AsyncStateMachineAnalyzer.ContainsAwait(h.HandlerBody))
        )
        {
            EmitWithHandlersLiftedCatch(node, il, outerParams, locals);
            return;
        }

        var resultSigType = MapToClr(node.Type);
        var resultLocal = new CilLocalVariable(resultSigType);
        il.Owner.LocalVariables.Add(resultLocal);

        var endLabel = new CilInstructionLabel();

        // If this with-handlers is part of an async state machine and contains
        // await points, emit a trampoline label *before* the TryStart. The
        // outer (or enclosing-WH) dispatch jumps to this label, then execution
        // falls through into the try region, where a per-WH dispatch routes to
        // the actual resume label inside this region. This avoids the illegal
        // pattern of branching across a try-region boundary.
        var tramp =
            _moveNextCtx?.TrampolineLabels is { } trampolines
            && trampolines.TryGetValue(node, out var t)
                ? t
                : null;
        if (tramp is not null)
            tramp.Instruction = il.Add(CilOpCodes.Nop);

        // Try block
        var tryStartLabel = new CilInstructionLabel { Instruction = il.Add(CilOpCodes.Nop) };

        EmitWithHandlersBodyDispatch(node, il, tramp);

        EmitNode(node.Body, il, outerParams, locals);
        il.Add(CilOpCodes.Stloc, resultLocal);
        il.Add(CilOpCodes.Leave, endLabel);

        // Emit each catch handler. The CLR requires that catch handlers for the
        // same try block be contiguous in the exception table: handler N's
        // HandlerEnd must equal handler N+1's HandlerStart, with no gap of
        // unprotected code between them. We enforce that by reusing the next
        // handler's opening Nop (or the final endLabel Nop) as the previous
        // handler's end.
        var handlerBoundaries =
            new List<(CilInstructionLabel Start, CilInstructionLabel End, Type ClrType)>();
        CilInstructionLabel? previousHandlerEnd = null;
        foreach (var handler in node.Handlers)
        {
            var exClrType = _clrInterop.FindType(handler.ExceptionTypeName);
            if (exClrType is null)
            {
                diagnostics.Error(
                    $"Cannot resolve exception type '{handler.ExceptionTypeName}' for IL emission",
                    node.Span
                );
                continue;
            }

            var handlerStart = new CilInstructionLabel();
            var handlerEnd = new CilInstructionLabel();

            // Handler start: exception object is on the stack. This Nop also
            // serves as the previous handler's HandlerEnd (exclusive), so
            // consecutive handlers abut with no orphan bytes between them.
            handlerStart.Instruction = il.Add(CilOpCodes.Nop);
            if (previousHandlerEnd is not null)
                previousHandlerEnd.Instruction = handlerStart.Instruction;

            if (handler.BindingVarName != "_")
            {
                // Store exception in a local variable
                var exLocal = new CilLocalVariable(
                    _module.DefaultImporter.ImportType(exClrType).ToTypeSignature(false)
                );
                il.Owner.LocalVariables.Add(exLocal);

                il.Add(CilOpCodes.Stloc, exLocal);

                // Add binding to locals dict
                var hadPrevious = locals.TryGetValue(handler.BindingVarName, out var previousLocal);
                locals[handler.BindingVarName] = exLocal;

                EmitNode(handler.HandlerBody, il, outerParams, locals);

                // Restore locals
                if (hadPrevious)
                    locals[handler.BindingVarName] = previousLocal!;
                else
                    locals.Remove(handler.BindingVarName);
            }
            else
            {
                // Discard the exception from the stack
                il.Add(CilOpCodes.Pop);
                EmitNode(handler.HandlerBody, il, outerParams, locals);
            }

            il.Add(CilOpCodes.Stloc, resultLocal);
            il.Add(CilOpCodes.Leave, endLabel);

            previousHandlerEnd = handlerEnd;
            handlerBoundaries.Add((handlerStart, handlerEnd, exClrType));
        }

        // End label. Also doubles as the last handler's HandlerEnd so its
        // region ends exactly where the surrounding code resumes.
        endLabel.Instruction = il.Add(CilOpCodes.Nop);
        if (previousHandlerEnd is not null)
            previousHandlerEnd.Instruction = endLabel.Instruction;

        // Register exception handlers (all share the same try region)
        foreach (var (start, end, clrType) in handlerBoundaries)
            il.Owner.ExceptionHandlers.Add(
                new CilExceptionHandler
                {
                    HandlerType = CilExceptionHandlerType.Exception,
                    TryStart = tryStartLabel,
                    TryEnd = handlerBoundaries[0].Start,
                    HandlerStart = start,
                    HandlerEnd = end,
                    ExceptionType = _module.DefaultImporter.ImportType(clrType).ToTypeDefOrRef(),
                }
            );

        // Load the result
        il.Add(CilOpCodes.Ldloc, resultLocal);
    }

    /// <summary>
    ///     Emits the per-with-handlers state dispatch table that routes resume points
    ///     for awaits located inside this WH's body (or a descendant's body) to the
    ///     correct resume label or further-nested trampoline. No-op when there is no
    ///     trampoline (i.e. no body await needs routing here).
    /// </summary>
    private void EmitWithHandlersBodyDispatch(
        IrNode.WithHandlers node,
        CilInstructionCollection il,
        CilInstructionLabel? tramp
    )
    {
        if (
            tramp is null
            || _moveNextCtx is not { } ctx
            || ctx.AwaitTryChains is not { } chains
            || ctx.ResumeLabels is not { } resumes
        )
            return;

        var entries = new List<(int State, ICilLabel Target)>();
        for (var i = 0; i < chains.Count; i++)
        {
            var chain = chains[i];
            var idx = -1;
            for (var k = 0; k < chain.Count; k++)
                if (ReferenceEquals(chain[k], node))
                {
                    idx = k;
                    break;
                }

            if (idx < 0)
                continue;

            ICilLabel target =
                idx == chain.Count - 1 ? resumes[i] : ctx.TrampolineLabels![chain[idx + 1]];
            entries.Add((i, target));
        }

        if (entries.Count == 0)
            return;

        var maxState = entries.Max(e => e.State);
        var fallthrough = new CilInstructionLabel();
        var defaults = new ICilLabel[maxState + 1];
        for (var i = 0; i <= maxState; i++)
            defaults[i] = fallthrough;
        foreach (var (state, target) in entries)
            defaults[state] = target;

        il.Add(CilOpCodes.Ldloc, ctx.StateLocal);
        il.Add(CilOpCodes.Switch, defaults);
        fallthrough.Instruction = il.Add(CilOpCodes.Nop);
    }

    /// <summary>
    ///     Emits a with-handlers whose handler body contains an <c>await</c> by
    ///     lifting the handler bodies out of the catch region. The catch handler
    ///     only stores the caught exception (or pops it) and writes a tag local;
    ///     after the try region the tag is dispatched to run the chosen handler
    ///     body in normal code. This way the await's resume label is *not* inside
    ///     a protected handler, so the top-of-MoveNext state dispatch can reach
    ///     it without violating CIL's BranchIntoHandler rule.
    /// </summary>
    private void EmitWithHandlersLiftedCatch(
        IrNode.WithHandlers node,
        CilInstructionCollection il,
        IReadOnlyList<IrParam> outerParams,
        Dictionary<string, CilLocalVariable> locals
    )
    {
        var ctx = _moveNextCtx!;

        var resultSigType = MapToClr(node.Type);
        var resultLocal = new CilLocalVariable(resultSigType);
        il.Owner.LocalVariables.Add(resultLocal);

        var tagLocal = new CilLocalVariable(_module.CorLibTypeFactory.Int32);
        il.Owner.LocalVariables.Add(tagLocal);

        var endLabel = new CilInstructionLabel();
        var skipLabel = new CilInstructionLabel();

        var tramp =
            ctx.TrampolineLabels is { } trampolines && trampolines.TryGetValue(node, out var t)
                ? t
                : null;
        if (tramp is not null)
            tramp.Instruction = il.Add(CilOpCodes.Nop);

        var tryStartLabel = new CilInstructionLabel { Instruction = il.Add(CilOpCodes.Nop) };

        // Per-WH dispatch (for body-awaits — handler-body awaits route via the
        // top-of-MoveNext dispatch directly to their resume labels in the
        // normal code that follows the try region).
        EmitWithHandlersBodyDispatch(node, il, tramp);

        // Body: store result, leave to skip with tag=0 (no exception).
        EmitNode(node.Body, il, outerParams, locals);
        il.Add(CilOpCodes.Stloc, resultLocal);
        il.Add(CilOpCodes.Ldc_I4_0);
        il.Add(CilOpCodes.Stloc, tagLocal);
        il.Add(CilOpCodes.Leave, skipLabel);

        // Catch handlers: each captures the exception (or discards it for `_`)
        // and writes a 1-based tag, then leaves the try region. The handler
        // body itself is *not* emitted here — it runs after the try.
        var captured = new List<(IrHandlerClause Handler, CilLocalVariable? VarLocal)>();
        var handlerBoundaries =
            new List<(CilInstructionLabel Start, CilInstructionLabel End, Type ClrType)>();
        CilInstructionLabel? previousHandlerEnd = null;
        var tagCounter = 0;
        foreach (var handler in node.Handlers)
        {
            tagCounter++;
            var exClrType = _clrInterop.FindType(handler.ExceptionTypeName);
            if (exClrType is null)
            {
                diagnostics.Error(
                    $"Cannot resolve exception type '{handler.ExceptionTypeName}' for IL emission",
                    node.Span
                );
                continue;
            }

            var handlerStart = new CilInstructionLabel();
            var handlerEnd = new CilInstructionLabel();

            handlerStart.Instruction = il.Add(CilOpCodes.Nop);
            if (previousHandlerEnd is not null)
                previousHandlerEnd.Instruction = handlerStart.Instruction;

            CilLocalVariable? varLocal = null;
            if (handler.BindingVarName != "_")
            {
                varLocal = new CilLocalVariable(
                    _module.DefaultImporter.ImportType(exClrType).ToTypeSignature(false)
                );
                il.Owner.LocalVariables.Add(varLocal);
                il.Add(CilOpCodes.Stloc, varLocal);

                // If the handler body crosses an await, the analyzer hoisted
                // this binding to a state-machine field; persist it now so
                // the await save/restore picks it up.
                if (ctx.VarFields.TryGetValue(handler.BindingVarName, out var smField))
                {
                    il.Add(CilOpCodes.Ldarg_0);
                    il.Add(CilOpCodes.Ldloc, varLocal);
                    il.Add(CilOpCodes.Stfld, smField);
                    ctx.AllLocals.Add((handler.BindingVarName, varLocal));
                }
            }
            else
            {
                il.Add(CilOpCodes.Pop);
            }

            il.Add(CilOpCodes.Ldc_I4, tagCounter);
            il.Add(CilOpCodes.Stloc, tagLocal);
            il.Add(CilOpCodes.Leave, skipLabel);

            previousHandlerEnd = handlerEnd;
            handlerBoundaries.Add((handlerStart, handlerEnd, exClrType));
            captured.Add((handler, varLocal));
        }

        // skipLabel doubles as the last handler's HandlerEnd (the CLR requires
        // contiguous catch regions with no gap of unprotected code between
        // them, and HandlerEnd is exclusive).
        skipLabel.Instruction = il.Add(CilOpCodes.Nop);
        if (previousHandlerEnd is not null)
            previousHandlerEnd.Instruction = skipLabel.Instruction;

        foreach (var (start, end, clrType) in handlerBoundaries)
            il.Owner.ExceptionHandlers.Add(
                new CilExceptionHandler
                {
                    HandlerType = CilExceptionHandlerType.Exception,
                    TryStart = tryStartLabel,
                    TryEnd = handlerBoundaries[0].Start,
                    HandlerStart = start,
                    HandlerEnd = end,
                    ExceptionType = _module.DefaultImporter.ImportType(clrType).ToTypeDefOrRef(),
                }
            );

        // Tag dispatch: tag == 0 means no exception; jump straight to end with
        // the body's result already in resultLocal.
        il.Add(CilOpCodes.Ldloc, tagLocal);
        il.Add(CilOpCodes.Brfalse, endLabel);

        // For each handler in declaration order, run its body if its tag matches.
        var handlerIdx = 0;
        foreach (var (handler, varLocal) in captured)
        {
            handlerIdx++;
            var nextCheckLabel = new CilInstructionLabel();
            il.Add(CilOpCodes.Ldloc, tagLocal);
            il.Add(CilOpCodes.Ldc_I4, handlerIdx);
            il.Add(CilOpCodes.Bne_Un, nextCheckLabel);

            CilLocalVariable? previousLocal = null;
            var hadPrevious = false;
            if (varLocal is not null)
            {
                hadPrevious = locals.TryGetValue(handler.BindingVarName, out previousLocal);
                locals[handler.BindingVarName] = varLocal;
            }

            EmitNode(handler.HandlerBody, il, outerParams, locals);
            il.Add(CilOpCodes.Stloc, resultLocal);
            il.Add(CilOpCodes.Br, endLabel);

            if (varLocal is not null)
            {
                if (hadPrevious)
                    locals[handler.BindingVarName] = previousLocal!;
                else
                    locals.Remove(handler.BindingVarName);
            }

            nextCheckLabel.Instruction = il.Add(CilOpCodes.Nop);
        }

        endLabel.Instruction = il.Add(CilOpCodes.Nop);
        il.Add(CilOpCodes.Ldloc, resultLocal);
    }

    private void EmitLoadVar(
        string name,
        SourceSpan span,
        CilInstructionCollection il,
        IReadOnlyList<IrParam> outerParams,
        Dictionary<string, CilLocalVariable> locals,
        ZType? varType = null
    )
    {
        if (locals.TryGetValue(name, out var local))
        {
            il.Add(CilOpCodes.Ldloc, local);
            return;
        }

        for (var i = 0; i < outerParams.Count; i++)
            if (outerParams[i].Name == name)
            {
                var method = il.Owner!.Owner!;
                // In AsmResolver, Parameters collection excludes 'this', so for instance methods
                // (where _instanceArgOffset=1), we still index by i into Parameters.
                il.Add(CilOpCodes.Ldarg, method.Parameters[i]);
                return;
            }

        if (
            _currentClassFields is not null
            && _currentClassFields.TryGetValue(name, out var classField)
        )
        {
            EmitLoadClassThis(il);
            il.Add(CilOpCodes.Ldfld, classField);
            return;
        }

        if (_staticFields.TryGetValue(name, out var field))
        {
            il.Add(CilOpCodes.Ldsfld, field);
            return;
        }

        // Check if the name is a function in _methods (for main module function values)
        var sanitizedName = Sanitize(name);
        Log.Debug(
            "EmitLoadVar: trying to load '{Name}', sanitizedName={Sanitized}, inStaticFields={InStatic}, inMethods={InMethods}",
            name,
            sanitizedName,
            _staticFields.ContainsKey(name),
            _methods.ContainsKey(sanitizedName)
        );
        if (_methods.TryGetValue(sanitizedName, out var methodDef))
        {
            // A top-level function used as a value must become a delegate, not a bare
            // method pointer. When we know the function type, construct the matching
            // Func/Action delegate (ldnull target since the method is static); otherwise
            // fall back to the raw pointer for callers that handle it themselves.
            if (varType is ZType.ZFuncType)
            {
                il.Add(CilOpCodes.Ldnull);
                il.Add(CilOpCodes.Ldftn, methodDef);
                il.Add(CilOpCodes.Newobj, ImportDelegateConstructor(varType));
            }
            else
            {
                il.Add(CilOpCodes.Ldftn, methodDef);
            }

            return;
        }

        diagnostics.Error($"Variable '{name}' not found for AsmResolver IL emission", span);
        il.Add(CilOpCodes.Ldc_I4_0);
    }

    private void EmitBinaryOp(string op, ZType? leftType, CilInstructionCollection il)
    {
        switch (op)
        {
            case "+" when leftType is ZType.ZPrimitiveType { Kind: PrimitiveKind.String }:
                var concatMethod = typeof(string).GetMethod(
                    "Concat",
                    [typeof(string), typeof(string)]
                )!;
                il.Add(CilOpCodes.Call, _module.DefaultImporter.ImportMethod(concatMethod));
                break;
            case "+":
                il.Add(CilOpCodes.Add);
                break;
            case "-":
                il.Add(CilOpCodes.Sub);
                break;
            case "*":
                il.Add(CilOpCodes.Mul);
                break;
            case "/":
                il.Add(CilOpCodes.Div);
                break;
            case "%":
                il.Add(CilOpCodes.Rem);
                break;
            case "=":
                il.Add(CilOpCodes.Ceq);
                break;
            case "<":
                il.Add(CilOpCodes.Clt);
                break;
            case ">":
                il.Add(CilOpCodes.Cgt);
                break;
            case "!=":
                il.Add(CilOpCodes.Ceq);
                il.Add(CilOpCodes.Ldc_I4_0);
                il.Add(CilOpCodes.Ceq);
                break;
            case "<=":
                // For floats/doubles, use the unordered variant so NaN => false
                // (matches IEEE 754 and the C# <= operator). !(a > b) is wrong for NaN.
                il.Add(IsFloatLike(leftType) ? CilOpCodes.Cgt_Un : CilOpCodes.Cgt);
                il.Add(CilOpCodes.Ldc_I4_0);
                il.Add(CilOpCodes.Ceq);
                break;
            case ">=":
                il.Add(IsFloatLike(leftType) ? CilOpCodes.Clt_Un : CilOpCodes.Clt);
                il.Add(CilOpCodes.Ldc_I4_0);
                il.Add(CilOpCodes.Ceq);
                break;
            // "and" and "or" are short-circuited: handled before operand emission
            // in the IrNode.BinOp dispatch (see EmitShortCircuit). They never
            // reach this switch.
        }
    }

    private void EmitShortCircuit(
        IrNode.BinOp binop,
        CilInstructionCollection il,
        IReadOnlyList<IrParam> outerParams,
        Dictionary<string, CilLocalVariable> locals
    )
    {
        // Lower (and a b) to: a ? b : false
        // Lower (or  a b) to: a ? true : b
        // Matches the C# emitter's `&&` / `||` semantics, which do not evaluate
        // the right operand when the left determines the result. Without this,
        // an expression like `(and #f (some-throwing-call))` would throw under
        // the IL backend but not under the C# backend.
        var shortLabel = new CilInstructionLabel();
        var endLabel = new CilInstructionLabel();
        EmitNode(binop.Left, il, outerParams, locals);
        if (binop.Op == "and")
        {
            il.Add(CilOpCodes.Brfalse, shortLabel);
            EmitNode(binop.Right, il, outerParams, locals);
            il.Add(CilOpCodes.Br, endLabel);
            shortLabel.Instruction = il.Add(CilOpCodes.Ldc_I4_0);
        }
        else
        {
            il.Add(CilOpCodes.Brtrue, shortLabel);
            EmitNode(binop.Right, il, outerParams, locals);
            il.Add(CilOpCodes.Br, endLabel);
            shortLabel.Instruction = il.Add(CilOpCodes.Ldc_I4_1);
        }

        endLabel.Instruction = il.Add(CilOpCodes.Nop);
    }

    private static void EmitUnaryOp(string op, CilInstructionCollection il)
    {
        switch (op)
        {
            case "not":
                il.Add(CilOpCodes.Ldc_I4_0);
                il.Add(CilOpCodes.Ceq);
                break;
            case "-":
                il.Add(CilOpCodes.Neg);
                break;
        }
    }

    private static bool IsFloatLike(ZType? t)
    {
        return t is ZType.ZPrimitiveType { Kind: PrimitiveKind.Float or PrimitiveKind.Double };
    }

    // Resolves an indexer accessor (getter or setter) on a CLR type. Most types use
    // the C# default name "Item" — but some types (notably System.String) declare the
    // indexer under a different name via [DefaultMember]; for those we must look up
    // get_/set_<DefaultMember> instead of get_/set_Item.
    private static MethodInfo? ResolveIndexerAccessor(Type receiver, string accessorPrefix)
    {
        var hit = receiver.GetMethod(accessorPrefix + "Item");
        if (hit is null && receiver.IsGenericType)
            hit = receiver.GetGenericTypeDefinition().GetMethod(accessorPrefix + "Item");
        if (hit is not null)
            return hit;

        var dm = (DefaultMemberAttribute?)
            Attribute.GetCustomAttribute(receiver, typeof(DefaultMemberAttribute));
        if (dm is not null && dm.MemberName != "Item")
        {
            hit = receiver.GetMethod(accessorPrefix + dm.MemberName);
            if (hit is null && receiver.IsGenericType)
                hit = receiver.GetGenericTypeDefinition().GetMethod(accessorPrefix + dm.MemberName);
        }

        return hit;
    }

    /// <summary>
    ///     Emits a Callvirt to delegate.Invoke() using the AsmResolver-aware type for the delegate.
    /// </summary>
    private void EmitDelegateInvoke(ZType funcType, CilInstructionCollection il)
    {
        var clrDelegateType = MapToReflectionClr(funcType);
        var invokeMethod = clrDelegateType.GetMethod("Invoke");
        if (invokeMethod is null)
        {
            diagnostics.Error(
                $"Cannot find Invoke method on delegate type '{clrDelegateType}' for IL emission",
                SourceSpan.None
            );
            il.Add(CilOpCodes.Ldc_I4_0);
            return;
        }
        il.Add(CilOpCodes.Callvirt, ImportMethodWithGenericDeclaringType(invokeMethod, funcType));
    }

    private void EmitCustomAttributes(IReadOnlyList<IrAttribute>? attrs, MethodDefinition target)
    {
        if (attrs is null)
            return;
        foreach (var attr in attrs)
        {
            var attrType =
                _clrInterop.FindType(attr.Name) ?? _clrInterop.FindType(attr.Name + "Attribute");
            if (attrType is null)
                continue;
            var customAttr = BuildCustomAttribute(attrType, attr);
            if (customAttr is not null)
                target.CustomAttributes.Add(customAttr);
        }
    }

    private void EmitCustomAttributes(IReadOnlyList<IrAttribute>? attrs, TypeDefinition target)
    {
        if (attrs is null)
            return;
        foreach (var attr in attrs)
        {
            var attrType =
                _clrInterop.FindType(attr.Name) ?? _clrInterop.FindType(attr.Name + "Attribute");
            if (attrType is null)
                continue;
            var customAttr = BuildCustomAttribute(attrType, attr);
            if (customAttr is not null)
                target.CustomAttributes.Add(customAttr);
        }
    }

    private CustomAttribute? BuildCustomAttribute(Type attrType, IrAttribute attr)
    {
        if (attr.PositionalArgs.Count == 0 && attr.NamedArgs.Count == 0)
        {
            var ctorInfo = attrType.GetConstructor(Type.EmptyTypes);
            if (ctorInfo is not null)
            {
                var ctorRef = (ICustomAttributeType)_module.DefaultImporter.ImportMethod(ctorInfo);
                return new CustomAttribute(ctorRef);
            }

            // Fall through: attributes like xunit v3 FactAttribute have only a
            // ctor with all-optional parameters (e.g. [CallerFilePath] string? = null).
            var defaultedCtor = attrType
                .GetConstructors(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(c => c.GetParameters().All(p => p.HasDefaultValue));
            if (defaultedCtor is null)
                return null;

            var defaultedCtorRef = (ICustomAttributeType)
                _module.DefaultImporter.ImportMethod(defaultedCtor);
            var defaultedAttr = new CustomAttribute(defaultedCtorRef);
            var defaultedSig = new CustomAttributeSignature();
            foreach (var p in defaultedCtor.GetParameters())
            {
                var typeSig = _module
                    .DefaultImporter.ImportType(p.ParameterType)
                    .ToTypeSignature(false);
                defaultedSig.FixedArguments.Add(
                    new CustomAttributeArgument(typeSig, p.DefaultValue)
                );
            }

            defaultedAttr.Signature = defaultedSig;
            return defaultedAttr;
        }

        var ctor = FindAttributeConstructor(attrType, attr.PositionalArgs);
        if (ctor is null)
            return null;

        var ctorReference = (ICustomAttributeType)_module.DefaultImporter.ImportMethod(ctor);
        var customAttr = new CustomAttribute(ctorReference);
        var ctorParams = ctor.GetParameters();

        var signature = new CustomAttributeSignature();

        if (ctorParams.Length == 1 && ctorParams[0].ParameterType == typeof(object[]))
        {
            // params object[] — pack all positional args as boxed elements in an array argument
            var objectTypeSig = _module
                .DefaultImporter.ImportType(typeof(object))
                .ToTypeSignature(false);
            var arrayTypeSig = objectTypeSig.MakeSzArrayType();

            var elements = new object[attr.PositionalArgs.Count];
            for (var i = 0; i < attr.PositionalArgs.Count; i++)
            {
                var (clrType, value) = ResolveAttributeArgValue(attr.PositionalArgs[i]);
                var elemTypeSig = _module
                    .DefaultImporter.ImportType(clrType)
                    .ToTypeSignature(false);
                elements[i] = new BoxedArgument(elemTypeSig, value);
            }

            signature.FixedArguments.Add(new CustomAttributeArgument(arrayTypeSig, elements));
        }
        else
        {
            // Positional args match constructor parameters 1:1
            for (var i = 0; i < attr.PositionalArgs.Count && i < ctorParams.Length; i++)
            {
                var (_, value) = ResolveAttributeArgValue(attr.PositionalArgs[i]);
                var typeSig = _module
                    .DefaultImporter.ImportType(ctorParams[i].ParameterType)
                    .ToTypeSignature(false);
                signature.FixedArguments.Add(new CustomAttributeArgument(typeSig, value));
            }
        }

        customAttr.Signature = signature;
        return customAttr;
    }

    private void EmitClassDecl(IrNode.ClassDecl classDecl)
    {
        Log.Debug(
            "IlEmitter: emitting class declaration {ClassName}, {FieldCount} fields, {MethodCount} methods, isOpen={IsOpen}, base={BaseClass}, interfaces=[{Interfaces}]",
            classDecl.Name,
            classDecl.Fields.Count,
            classDecl.Methods.Count,
            classDecl.IsOpen,
            classDecl.BaseClassName ?? "(object)",
            string.Join(", ", classDecl.InterfaceNames)
        );

        // Resolve base type
        var baseTypeRef = _module.CorLibTypeFactory.Object.ToTypeDefOrRef();
        TypeDefinition? baseTypeDef = null;
        var inheritedFields = new List<IrField>();
        var inheritedMethodNames = new HashSet<string>();

        // The parser puts the first name after ':' in BaseClassName (position-based).
        // If it's not a known ZScheme class, it may actually be a CLR interface.
        string? baseClassAsInterface = null;

        if (
            classDecl.BaseClassName is not null
            && _asmClassInfos.TryGetValue(classDecl.BaseClassName, out var baseInfo)
        )
        {
            // Known ZScheme class — use as base type
            baseTypeRef = baseInfo.TypeDef;
            baseTypeDef = baseInfo.TypeDef;
            inheritedFields.AddRange(GetAsmInheritedFields(classDecl.BaseClassName));
            inheritedMethodNames = GetAsmInheritedMethodNames(classDecl.BaseClassName);
        }
        else if (classDecl.BaseClassName is not null)
        {
            // Not a ZScheme class — could be a ZScheme interface or a CLR type.
            // Check _userTypes first (ZScheme interfaces are registered there but not in _asmClassInfos)
            if (_userTypes.ContainsKey(classDecl.BaseClassName))
            {
                // ZScheme-defined interface
                baseClassAsInterface = classDecl.BaseClassName;
            }
            else
            {
                // Try resolving as CLR type
                var clrType = _clrInterop.FindType(classDecl.BaseClassName);
                if (clrType is null)
                    foreach (var ns in ClrUsings)
                    {
                        clrType = _clrInterop.FindType(ns + "." + classDecl.BaseClassName);
                        if (clrType is not null)
                            break;
                    }

                if (clrType is not null)
                {
                    if (clrType.IsInterface)
                        baseClassAsInterface = classDecl.BaseClassName;
                    else
                        baseTypeRef = _module.DefaultImporter.ImportType(clrType);
                }
            }
        }

        var typeAttrs = TypeAttributes.Public | TypeAttributes.Class;
        if (!classDecl.IsOpen)
            typeAttrs |= TypeAttributes.Sealed;

        var classType = new TypeDefinition(_ilNamespace, Sanitize(classDecl.Name), typeAttrs)
        {
            BaseType = baseTypeRef,
        };
        _module.TopLevelTypes.Add(classType);
        RegisterUserType(classDecl.Name, classType);

        EmitCustomAttributes(classDecl.Attributes, classType);

        // Add interface implementations and collect interface method names
        var interfaceMethodNames = new HashSet<string>();

        // Handle BaseClassName that was actually a CLR interface
        if (baseClassAsInterface is not null)
        {
            var ifaceRef = ResolveInterfaceType(baseClassAsInterface);
            if (ifaceRef is not null)
                classType.Interfaces.Add(new InterfaceImplementation(ifaceRef));
            CollectInterfaceMethodNames(baseClassAsInterface, interfaceMethodNames);
        }

        foreach (var ifaceName in classDecl.InterfaceNames)
        {
            var ifaceRef = ResolveInterfaceType(ifaceName);
            if (ifaceRef is not null)
                classType.Interfaces.Add(new InterfaceImplementation(ifaceRef));

            // Collect method names from CLR interfaces for Virtual flag marking
            CollectInterfaceMethodNames(ifaceName, interfaceMethodNames);
        }

        // Define own fields as properties with backing fields
        var fieldDefs = new List<(FieldDefinition Field, PropertyDefinition Prop)>();
        foreach (var field in classDecl.Fields)
        {
            var fieldType = MapToClr(field.Type);
            // Use Family (protected) so subclass methods — including those generated
            // for `(class Sub : Base ...)` and `(object : Base ...)` expressions —
            // can read/write the inherited backing field directly via ldfld/stfld.
            // Private would force the IL verifier to reject those accesses.
            var fieldAttrs = FieldAttributes.Family;
            if (!field.IsMutable)
                fieldAttrs |= FieldAttributes.InitOnly;
            var fb = new FieldDefinition(
                $"<{Sanitize(field.Name)}>k__BackingField",
                fieldAttrs,
                new FieldSignature(fieldType)
            );
            classType.Fields.Add(fb);

            var getterName = $"get_{Sanitize(field.Name)}";
            var isGetterIfaceImpl = interfaceMethodNames.Contains(getterName);
            var getterAttrs =
                MethodAttributes.Public
                | MethodAttributes.Virtual
                | MethodAttributes.SpecialName
                | MethodAttributes.HideBySig;
            if (isGetterIfaceImpl)
                getterAttrs |= MethodAttributes.NewSlot | MethodAttributes.Final;

            var getter = new MethodDefinition(
                getterName,
                getterAttrs,
                MethodSignature.CreateInstance(fieldType)
            );
            classType.Methods.Add(getter);
            var getBody = new CilMethodBody { InitializeLocals = true };
            getter.MethodBody = getBody;
            var getIl = getBody.Instructions;
            getIl.Add(CilOpCodes.Ldarg_0);
            getIl.Add(CilOpCodes.Ldfld, fb);
            getIl.Add(CilOpCodes.Ret);

            var pb = new PropertyDefinition(
                Sanitize(field.Name),
                0,
                PropertySignature.CreateInstance(fieldType)
            );
            pb.Semantics.Add(new MethodSemantics(getter, MethodSemanticsAttributes.Getter));

            if (field.IsMutable)
            {
                var setterName = $"set_{Sanitize(field.Name)}";
                var isSetterIfaceImpl = interfaceMethodNames.Contains(setterName);
                var setterAttrs =
                    MethodAttributes.Public
                    | MethodAttributes.Virtual
                    | MethodAttributes.SpecialName
                    | MethodAttributes.HideBySig;
                if (isSetterIfaceImpl)
                    setterAttrs |= MethodAttributes.NewSlot | MethodAttributes.Final;

                var setter = new MethodDefinition(
                    setterName,
                    setterAttrs,
                    MethodSignature.CreateInstance(_module.CorLibTypeFactory.Void, [fieldType])
                );
                setter.ParameterDefinitions.Add(new ParameterDefinition(1, "value", 0));
                classType.Methods.Add(setter);
                var setBody = new CilMethodBody { InitializeLocals = true };
                setter.MethodBody = setBody;
                var setIl = setBody.Instructions;
                setIl.Add(CilOpCodes.Ldarg_0);
                setIl.Add(CilOpCodes.Ldarg_1);
                setIl.Add(CilOpCodes.Stfld, fb);
                setIl.Add(CilOpCodes.Ret);
                pb.Semantics.Add(new MethodSemantics(setter, MethodSemanticsAttributes.Setter));
            }
            else if (field.IsInit)
            {
                var initSetter = CreateInitSetter(classType, Sanitize(field.Name), fieldType, fb);
                classType.Methods.Add(initSetter);
                pb.Semantics.Add(new MethodSemantics(initSetter, MethodSemanticsAttributes.Setter));
            }

            classType.Properties.Add(pb);
            fieldDefs.Add((fb, pb));
        }

        // Constructor
        if (classDecl.Constructor is { } irCtor)
        {
            // Explicit constructor
            var baseCtorRef = ResolveAsmBaseConstructor(baseTypeDef, irCtor.SuperArgs?.Count ?? 0);
            var ctorParamTypes = irCtor.Params.Select(p => MapToClr(p.Type)).ToArray();
            var ctor = new MethodDefinition(
                ".ctor",
                MethodAttributes.Public
                    | MethodAttributes.HideBySig
                    | MethodAttributes.SpecialName
                    | MethodAttributes.RuntimeSpecialName,
                MethodSignature.CreateInstance(_module.CorLibTypeFactory.Void, ctorParamTypes)
            );
            for (var i = 0; i < irCtor.Params.Count; i++)
                ctor.ParameterDefinitions.Add(
                    new ParameterDefinition((ushort)(i + 1), Sanitize(irCtor.Params[i].Name), 0)
                );
            classType.Methods.Add(ctor);

            var ctorBody = new CilMethodBody { InitializeLocals = true };
            ctor.MethodBody = ctorBody;
            var ctorIl = ctorBody.Instructions;

            // Set up instance context for EmitNode calls within constructor
            var savedCtorOffset = _instanceArgOffset;
            _instanceArgOffset = 1;

            // Call base constructor
            if (irCtor.SuperArgs is { Count: > 0 } classSuperArgs)
            {
                var ctorLocals = new Dictionary<string, CilLocalVariable>();
                EmitSuperArgsWithThis(classSuperArgs, ctorBody, ctorIl, irCtor.Params, ctorLocals);
            }
            else
            {
                ctorIl.Add(CilOpCodes.Ldarg_0);
            }

            ctorIl.Add(CilOpCodes.Call, (IMethodDefOrRef)baseCtorRef);

            // Body expressions
            var bodyLocals = new Dictionary<string, CilLocalVariable>();
            foreach (var expr in irCtor.BodyExprs)
            {
                EmitNode(expr, ctorIl, irCtor.Params, bodyLocals);
                if (expr.Type is not null and not ZType.ZPrimitiveType { Kind: PrimitiveKind.Unit })
                    ctorIl.Add(CilOpCodes.Pop);
            }

            // Field assignments from set!
            foreach (var (fieldName, value) in irCtor.FieldSets)
            {
                var fieldIdx = classDecl.Fields.ToList().FindIndex(f => f.Name == fieldName);
                if (fieldIdx < 0)
                    continue;
                var fieldType = fieldDefs[fieldIdx].Field.Signature!.FieldType;
                if (WithHandlersHoister.ContainsWithHandlers(value))
                {
                    // Stack must be empty at try-block entry; spill the value to a
                    // local before pushing `this`.
                    EmitNode(value, ctorIl, irCtor.Params, bodyLocals);
                    EmitNullableWrapIfNeeded(value, fieldType, ctorIl);
                    var tmp = new CilLocalVariable(fieldType);
                    ctorBody.LocalVariables.Add(tmp);
                    ctorIl.Add(CilOpCodes.Stloc, tmp);
                    ctorIl.Add(CilOpCodes.Ldarg_0);
                    ctorIl.Add(CilOpCodes.Ldloc, tmp);
                }
                else
                {
                    ctorIl.Add(CilOpCodes.Ldarg_0);
                    EmitNode(value, ctorIl, irCtor.Params, bodyLocals);
                    EmitNullableWrapIfNeeded(value, fieldType, ctorIl);
                }

                ctorIl.Add(CilOpCodes.Stfld, fieldDefs[fieldIdx].Field);
            }

            _instanceArgOffset = savedCtorOffset;
            ctorIl.Add(CilOpCodes.Ret);
        }
        else
        {
            // Auto-generated constructor: inherited fields + own fields
            var baseCtorRef = ResolveAsmBaseConstructor(baseTypeDef, inheritedFields.Count);
            var allParamTypes = inheritedFields
                .Select(f => MapToClr(f.Type))
                .Concat(classDecl.Fields.Select(f => MapToClr(f.Type)))
                .ToArray();
            var ctor = new MethodDefinition(
                ".ctor",
                MethodAttributes.Public
                    | MethodAttributes.HideBySig
                    | MethodAttributes.SpecialName
                    | MethodAttributes.RuntimeSpecialName,
                MethodSignature.CreateInstance(_module.CorLibTypeFactory.Void, allParamTypes)
            );
            var paramIdx = 1;
            foreach (var f in inheritedFields)
                ctor.ParameterDefinitions.Add(
                    new ParameterDefinition((ushort)paramIdx++, Sanitize(f.Name), 0)
                );
            foreach (var f in classDecl.Fields)
                ctor.ParameterDefinitions.Add(
                    new ParameterDefinition((ushort)paramIdx++, Sanitize(f.Name), 0)
                );
            classType.Methods.Add(ctor);

            var ctorBody = new CilMethodBody { InitializeLocals = true };
            ctor.MethodBody = ctorBody;
            var ctorIl = ctorBody.Instructions;

            // Call base constructor with inherited field args
            ctorIl.Add(CilOpCodes.Ldarg_0);
            for (var i = 0; i < inheritedFields.Count; i++)
                ctorIl.Add(CilOpCodes.Ldarg, ctor.Parameters[i]);
            ctorIl.Add(CilOpCodes.Call, (IMethodDefOrRef)baseCtorRef);

            // Store own fields
            for (var i = 0; i < fieldDefs.Count; i++)
            {
                ctorIl.Add(CilOpCodes.Ldarg_0);
                ctorIl.Add(CilOpCodes.Ldarg, ctor.Parameters[inheritedFields.Count + i]);
                ctorIl.Add(CilOpCodes.Stfld, fieldDefs[i].Field);
            }

            ctorIl.Add(CilOpCodes.Ret);

            // Parameterless constructor for test frameworks
            if (classDecl.Fields.Count > 0 || inheritedFields.Count > 0)
            {
                var defaultCtorBaseRef = ResolveAsmBaseConstructor(baseTypeDef, 0);
                var defaultCtor = new MethodDefinition(
                    ".ctor",
                    MethodAttributes.Public
                        | MethodAttributes.HideBySig
                        | MethodAttributes.SpecialName
                        | MethodAttributes.RuntimeSpecialName,
                    MethodSignature.CreateInstance(_module.CorLibTypeFactory.Void)
                );
                classType.Methods.Add(defaultCtor);
                var defaultCtorBody = new CilMethodBody { InitializeLocals = true };
                defaultCtor.MethodBody = defaultCtorBody;
                var defaultCtorIl = defaultCtorBody.Instructions;
                defaultCtorIl.Add(CilOpCodes.Ldarg_0);
                defaultCtorIl.Add(CilOpCodes.Call, (IMethodDefOrRef)defaultCtorBaseRef);
                defaultCtorIl.Add(CilOpCodes.Ret);
            }
        }

        // Build field lookup for method bodies (own fields + inherited)
        var classFieldMap = new Dictionary<string, FieldDefinition>();
        for (var i = 0; i < classDecl.Fields.Count; i++)
            classFieldMap[classDecl.Fields[i].Name] = fieldDefs[i].Field;
        if (baseTypeDef is not null)
            AddAsmInheritedFieldsToMap(baseTypeDef, classFieldMap);

        // Emit methods in two phases so a method body can resolve calls to sibling methods
        // (and to itself for recursion).

        // Phase 1: define MethodDefinition shells, register in _currentClassMethods, but defer body emission.
        var methodShells = new List<MethodDefinition>();
        var classMethodMap = new Dictionary<string, MethodDefinition>();
        foreach (var method in classDecl.Methods)
        {
            var retType =
                method.ReturnType == ZType.Unit
                    ? _module.CorLibTypeFactory.Void
                    : MapToClr(method.ReturnType);
            var methodParamTypes = method.Params.Select(p => MapToClr(p.Type)).ToArray();

            var isOverride = inheritedMethodNames.Contains(method.Name);
            var isInterfaceImpl = interfaceMethodNames.Contains(method.Name);
            var methodAttrs = MethodAttributes.Public;
            if (isOverride)
                methodAttrs |= MethodAttributes.Virtual | MethodAttributes.HideBySig;
            else if (isInterfaceImpl)
                methodAttrs |=
                    MethodAttributes.Virtual
                    | MethodAttributes.NewSlot
                    | MethodAttributes.HideBySig
                    | MethodAttributes.Final;
            else if (classDecl.IsOpen)
                methodAttrs |=
                    MethodAttributes.Virtual
                    | MethodAttributes.NewSlot
                    | MethodAttributes.HideBySig;

            var mb = new MethodDefinition(
                Sanitize(method.Name),
                methodAttrs,
                MethodSignature.CreateInstance(retType, methodParamTypes)
            );
            for (var pi = 0; pi < method.Params.Count; pi++)
                mb.ParameterDefinitions.Add(
                    new ParameterDefinition((ushort)(pi + 1), method.Params[pi].Name, 0)
                );
            classType.Methods.Add(mb);
            EmitCustomAttributes(method.Attributes, mb);

            methodShells.Add(mb);
            classMethodMap[method.Name] = mb;
        }

        // Phase 2: emit method bodies. _currentClassMethods is set so EmitCall can resolve siblings.
        var savedClassMethods = _currentClassMethods;
        _currentClassMethods = classMethodMap;

        for (var methodIdx = 0; methodIdx < classDecl.Methods.Count; methodIdx++)
        {
            var method = classDecl.Methods[methodIdx];
            var mb = methodShells[methodIdx];

            if (method.IsAsync && AsyncStateMachineAnalyzer.ContainsAwait(method.Body))
            {
                // Async class method: create synthetic FuncDef and delegate to async emitter
                var savedOffset = _instanceArgOffset;
                var savedReturnType = _currentFuncReturnType;
                var savedClassFields = _currentClassFields;
                var savedTypeDef = _currentTypeDefinition;
                var savedBaseTypeDef = _currentBaseTypeDefinition;
                _instanceArgOffset = 1;
                _currentFuncReturnType = method.ReturnType;
                _currentClassFields = classFieldMap;
                _currentTypeDefinition = classType;
                _currentBaseTypeDefinition = baseTypeDef;

                var syntheticFunc = new IrNode.FuncDef(
                    method.Name,
                    method.Params,
                    method.ReturnType,
                    method.Body,
                    false
                )
                {
                    Span = method.Body.Span,
                };
                EmitAsyncFuncDef(syntheticFunc, mb, classType);

                _currentClassFields = savedClassFields;
                _instanceArgOffset = savedOffset;
                _currentFuncReturnType = savedReturnType;
                _currentTypeDefinition = savedTypeDef;
                _currentBaseTypeDefinition = savedBaseTypeDef;
            }
            else
            {
                var methodBody = new CilMethodBody { InitializeLocals = true };
                mb.MethodBody = methodBody;
                var methodIl = methodBody.Instructions;
                var methodLocals = new Dictionary<string, CilLocalVariable>();

                var savedOffset = _instanceArgOffset;
                var savedReturnType = _currentFuncReturnType;
                var savedClassFields = _currentClassFields;
                var savedTypeDef = _currentTypeDefinition;
                var savedBaseTypeDef = _currentBaseTypeDefinition;
                _instanceArgOffset = 1;
                _currentFuncReturnType = method.ReturnType;
                _currentClassFields = classFieldMap;
                _currentTypeDefinition = classType;
                _currentBaseTypeDefinition = baseTypeDef;

                EmitNode(method.Body, methodIl, method.Params, methodLocals);

                _currentClassFields = savedClassFields;
                _instanceArgOffset = savedOffset;
                _currentFuncReturnType = savedReturnType;
                _currentTypeDefinition = savedTypeDef;
                _currentBaseTypeDefinition = savedBaseTypeDef;

                if (
                    method is
                    {
                        ReturnType: ZType.ZPrimitiveType { Kind: PrimitiveKind.Unit },
                        Body.Type: not null
                            and not ZType.ZPrimitiveType { Kind: PrimitiveKind.Unit }
                    }
                )
                    methodIl.Add(CilOpCodes.Pop);

                // Async class methods without any await still need their body wrapped into a Task
                // before returning (the async state machine path is skipped when there are no awaits).
                if (method.IsAsync)
                {
                    var isVoidTask =
                        method.ReturnType is ZType.ZNamedType { TypeArgs: [] } voidTask2
                        && _typeAliases.IsTaskName(voidTask2.Name);
                    var isUnitMethod =
                        method.ReturnType is ZType.ZPrimitiveType { Kind: PrimitiveKind.Unit };

                    if (isUnitMethod || isVoidTask)
                    {
                        if (
                            !isUnitMethod
                            && method.Body.Type
                                is not null
                                    and not ZType.ZPrimitiveType { Kind: PrimitiveKind.Unit }
                        )
                            methodIl.Add(CilOpCodes.Pop);
                        var completedTaskGetter = typeof(Task)
                            .GetProperty("CompletedTask")!
                            .GetGetMethod()!;
                        methodIl.Add(
                            CilOpCodes.Call,
                            _module.DefaultImporter.ImportMethod(completedTaskGetter)
                        );
                    }
                    else
                    {
                        var inner =
                            method.ReturnType is ZType.ZNamedType { TypeArgs: [var t] } taskNt2
                            && _typeAliases.IsTaskName(taskNt2.Name)
                                ? t
                                : method.ReturnType;
                        var fromResult = typeof(Task)
                            .GetMethod("FromResult")!
                            .MakeGenericMethod(MapToReflectionClr(inner));
                        methodIl.Add(
                            CilOpCodes.Call,
                            _module.DefaultImporter.ImportMethod(fromResult)
                        );
                    }
                }

                methodIl.Add(CilOpCodes.Ret);
            }
        }

        _currentClassMethods = savedClassMethods;

        // Store class info for future subclasses
        _asmClassInfos[classDecl.Name] = new AsmClassInfo(
            classType,
            classDecl.IsOpen,
            classDecl.BaseClassName,
            classDecl.Fields,
            classDecl.Methods.Select(m => m.Name).ToList()
        );
    }

    private void EmitSuperMethodCall(
        IrNode.SuperMethodCall superCall,
        CilInstructionCollection il,
        IReadOnlyList<IrParam> outerParams,
        Dictionary<string, CilLocalVariable> locals
    )
    {
        if (_currentBaseTypeDefinition is null)
        {
            diagnostics.Error(
                "super/ can only be used in a class with a base class",
                superCall.Span
            );
            il.Add(CilOpCodes.Ldc_I4_0);
            return;
        }

        var baseMethod = _currentBaseTypeDefinition.Methods.FirstOrDefault(m =>
            !m.IsConstructor && m.Name == Sanitize(superCall.MethodName)
        );
        if (baseMethod is null)
        {
            diagnostics.Error($"Base class has no method '{superCall.MethodName}'", superCall.Span);
            il.Add(CilOpCodes.Ldc_I4_0);
            return;
        }

        il.Add(CilOpCodes.Ldarg_0);
        foreach (var arg in superCall.Args)
            EmitNode(arg, il, outerParams, locals);
        il.Add(CilOpCodes.Call, baseMethod);
    }

    private void EmitAsyncFuncDef(
        IrNode.FuncDef func,
        MethodDefinition stubMethod,
        TypeDefinition parentType
    )
    {
        Log.Debug("IlEmitter: emitting async state machine for {FuncName}", func.Name);
        var info = AsyncStateMachineAnalyzer.Analyze(func, _typeAliases);
        Log.Debug(
            "IlEmitter: async SM for {FuncName}: {AwaitCount} await points, {HoistedCount} hoisted locals, isVoid={IsVoid}",
            func.Name,
            info.AwaitPoints.Count,
            info.HoistedLocals.Count,
            info.IsVoidReturn
        );
        var smName = $"<{Sanitize(func.Name)}>d__{_asyncSmCounter++}";

        // Determine builder and task types
        var isVoid = info.IsVoidReturn;
        Type builderClrType;
        if (isVoid)
            builderClrType = typeof(AsyncTaskMethodBuilder);
        else
            builderClrType = typeof(AsyncTaskMethodBuilder<>).MakeGenericType(
                MapToReflectionClr(func.ReturnType)
            );

        // For generic closed builders (AsyncTaskMethodBuilder<T>), build a
        // GenericInstanceTypeSignature so later code that inspects the builder field
        // signature (e.g. GetAwaitUnsafeOnCompletedRef) can recognise the closed
        // generic and emit method references on the closed type.
        TypeSignature builderTypeSig;
        if (builderClrType.IsGenericType && !builderClrType.IsGenericTypeDefinition)
        {
            var openBuilder = builderClrType.GetGenericTypeDefinition();
            var builderArgs = builderClrType
                .GetGenericArguments()
                .Select(a => _module.DefaultImporter.ImportType(a).ToTypeSignature(a.IsValueType))
                .ToArray();
            builderTypeSig = new GenericInstanceTypeSignature(
                _module.DefaultImporter.ImportType(openBuilder),
                openBuilder.IsValueType,
                builderArgs
            );
        }
        else
        {
            builderTypeSig = _module
                .DefaultImporter.ImportType(builderClrType)
                .ToTypeSignature(builderClrType.IsValueType);
        }

        // --- Define state machine struct ---
        var smType = new TypeDefinition(
            "",
            smName,
            TypeAttributes.Sealed | TypeAttributes.NestedPrivate | TypeAttributes.SequentialLayout,
            _module.DefaultImporter.ImportType(typeof(ValueType))
        );
        smType.Interfaces.Add(
            new InterfaceImplementation(
                _module.DefaultImporter.ImportType(typeof(IAsyncStateMachine))
            )
        );
        // [CompilerGenerated]
        var compGenCtor = typeof(CompilerGeneratedAttribute).GetConstructor(Type.EmptyTypes)!;
        smType.CustomAttributes.Add(
            new CustomAttribute(
                (ICustomAttributeType)_module.DefaultImporter.ImportMethod(compGenCtor)
            )
        );
        parentType.NestedTypes.Add(smType);

        // --- Define fields ---
        var stateField = new FieldDefinition(
            "__state",
            FieldAttributes.Public,
            new FieldSignature(_module.CorLibTypeFactory.Int32)
        );
        smType.Fields.Add(stateField);

        var builderField = new FieldDefinition(
            "__builder",
            FieldAttributes.Public,
            new FieldSignature(builderTypeSig)
        );
        smType.Fields.Add(builderField);

        // __this field for instance method async state machines
        FieldDefinition? thisField = null;
        if (_instanceArgOffset == 1 && _currentTypeDefinition is not null)
        {
            thisField = new FieldDefinition(
                "__this",
                FieldAttributes.Public,
                new FieldSignature(_currentTypeDefinition.ToTypeSignature(false))
            );
            smType.Fields.Add(thisField);
        }

        // Parameter fields
        var varFields = new Dictionary<string, FieldDefinition>();
        foreach (var p in func.Params)
        {
            var pField = new FieldDefinition(
                Sanitize(p.Name),
                FieldAttributes.Public,
                new FieldSignature(MapToClr(p.Type))
            );
            smType.Fields.Add(pField);
            varFields[p.Name] = pField;
        }

        // Hoisted local fields
        foreach (var local in info.HoistedLocals)
            if (!varFields.ContainsKey(local.Name))
            {
                var lField = new FieldDefinition(
                    $"<{Sanitize(local.Name)}>5__",
                    FieldAttributes.Public,
                    new FieldSignature(MapToClr(local.Type))
                );
                smType.Fields.Add(lField);
                varFields[local.Name] = lField;
            }

        // Awaiter fields
        var awaiterFields = new Dictionary<int, FieldDefinition>();
        foreach (var ap in info.AwaitPoints)
        {
            var awaiterClrType = GetAwaiterClrType(ap);
            var awaiterField = new FieldDefinition(
                $"__awaiter{ap.StateNumber}",
                FieldAttributes.Private,
                new FieldSignature(
                    _module
                        .DefaultImporter.ImportType(awaiterClrType)
                        .ToTypeSignature(awaiterClrType.IsValueType)
                )
            );
            smType.Fields.Add(awaiterField);
            awaiterFields[ap.StateNumber] = awaiterField;
        }

        // --- Emit MoveNext method ---
        EmitMoveNextMethod(
            func,
            smType,
            stateField,
            builderField,
            builderClrType,
            varFields,
            awaiterFields,
            info,
            thisField
        );

        // --- Emit SetStateMachine method ---
        EmitSetStateMachineMethod(smType, builderField, builderClrType);

        // --- Emit stub method body ---
        EmitAsyncStubBody(
            func,
            stubMethod,
            smType,
            stateField,
            builderField,
            builderClrType,
            varFields,
            thisField
        );

        // --- Add [AsyncStateMachine] attribute to stub ---
        var asmAttrCtor = typeof(AsyncStateMachineAttribute).GetConstructor([typeof(Type)])!;
        var asmAttr = new CustomAttribute(
            (ICustomAttributeType)_module.DefaultImporter.ImportMethod(asmAttrCtor)
        )
        {
            Signature = new CustomAttributeSignature(
                new CustomAttributeArgument(
                    _module.DefaultImporter.ImportType(typeof(Type)).ToTypeSignature(false),
                    smType.ToTypeSignature(true)
                )
            ),
        };
        stubMethod.CustomAttributes.Add(asmAttr);
    }

    private void EmitAsyncStubBody(
        IrNode.FuncDef func,
        MethodDefinition stubMethod,
        TypeDefinition smType,
        FieldDefinition stateField,
        FieldDefinition builderField,
        Type builderClrType,
        Dictionary<string, FieldDefinition> varFields,
        FieldDefinition? thisField = null
    )
    {
        var body = new CilMethodBody { InitializeLocals = true };
        stubMethod.MethodBody = body;
        var il = body.Instructions;

        // Local 0: the state machine struct
        var smLocal = new CilLocalVariable(smType.ToTypeSignature(true));
        body.LocalVariables.Add(smLocal);

        // initobj smType
        il.Add(CilOpCodes.Ldloca, smLocal);
        il.Add(CilOpCodes.Initobj, smType.ToTypeSignature(true).ToTypeDefOrRef());

        // Copy 'this' into __this field for instance method async state machines
        if (thisField is not null)
        {
            il.Add(CilOpCodes.Ldloca, smLocal);
            il.Add(CilOpCodes.Ldarg_0); // this
            il.Add(CilOpCodes.Stfld, thisField);
        }

        // Copy parameters into state machine fields
        for (var i = 0; i < func.Params.Count; i++)
        {
            il.Add(CilOpCodes.Ldloca, smLocal);
            il.Add(CilOpCodes.Ldarg, stubMethod.Parameters[i]);
            il.Add(CilOpCodes.Stfld, varFields[func.Params[i].Name]);
        }

        // sm.__builder = AsyncTaskMethodBuilder<T>.Create()
        var createMethod = builderClrType.IsGenericType
            ? ImportClosedGenericMethod(builderClrType, "Create")
            : (IMethodDefOrRef)
                _module.DefaultImporter.ImportMethod(builderClrType.GetMethod("Create")!);
        il.Add(CilOpCodes.Ldloca, smLocal);
        il.Add(CilOpCodes.Call, createMethod);
        il.Add(CilOpCodes.Stfld, builderField);

        // sm.__state = -1
        il.Add(CilOpCodes.Ldloca, smLocal);
        il.Add(CilOpCodes.Ldc_I4_M1);
        il.Add(CilOpCodes.Stfld, stateField);

        // sm.__builder.Start<SM>(ref sm)
        var startMethodRef = builderClrType.IsGenericType
            ? ImportClosedGenericMethod(builderClrType, "Start")
            : (IMethodDefOrRef)
                _module.DefaultImporter.ImportMethod(builderClrType.GetMethod("Start")!);
        var startSpec = new MethodSpecification(
            startMethodRef,
            new GenericInstanceMethodSignature([smType.ToTypeSignature(true)])
        );

        il.Add(CilOpCodes.Ldloca, smLocal);
        il.Add(CilOpCodes.Ldflda, builderField);
        il.Add(CilOpCodes.Ldloca, smLocal);
        il.Add(CilOpCodes.Call, startSpec);

        // return sm.__builder.Task
        var taskPropGetter = builderClrType.IsGenericType
            ? ImportClosedGenericMethod(builderClrType, "Task")
            : (IMethodDefOrRef)
                _module.DefaultImporter.ImportMethod(
                    builderClrType.GetProperty("Task")!.GetGetMethod()!
                );
        il.Add(CilOpCodes.Ldloca, smLocal);
        il.Add(CilOpCodes.Ldflda, builderField);
        il.Add(CilOpCodes.Call, taskPropGetter);
        il.Add(CilOpCodes.Ret);
    }

    private void EmitMoveNextMethod(
        IrNode.FuncDef func,
        TypeDefinition smType,
        FieldDefinition stateField,
        FieldDefinition builderField,
        Type builderClrType,
        Dictionary<string, FieldDefinition> varFields,
        Dictionary<int, FieldDefinition> awaiterFields,
        AsyncStateMachineAnalyzer.AsyncMethodInfo info,
        FieldDefinition? thisField = null
    )
    {
        var moveNext = new MethodDefinition(
            "MoveNext",
            MethodAttributes.Private
                | MethodAttributes.Final
                | MethodAttributes.HideBySig
                | MethodAttributes.NewSlot
                | MethodAttributes.Virtual,
            MethodSignature.CreateInstance(_module.CorLibTypeFactory.Void)
        );
        smType.Methods.Add(moveNext);

        // Override IAsyncStateMachine.MoveNext
        var moveNextIntf = _module.DefaultImporter.ImportMethod(
            typeof(IAsyncStateMachine).GetMethod("MoveNext")!
        );
        smType.MethodImplementations.Add(
            new MethodImplementation((IMethodDefOrRef)moveNextIntf, moveNext)
        );

        var body = new CilMethodBody { InitializeLocals = true };
        moveNext.MethodBody = body;
        var il = body.Instructions;

        // Declare locals
        var stateLocal = new CilLocalVariable(_module.CorLibTypeFactory.Int32);
        body.LocalVariables.Add(stateLocal);

        // Local for final result (if non-void)
        CilLocalVariable? resultLocal = null;
        if (!info.IsVoidReturn)
        {
            resultLocal = new CilLocalVariable(MapToClr(func.ReturnType));
            body.LocalVariables.Add(resultLocal);
        }

        // Exception local for catch block
        var exLocal = new CilLocalVariable(
            _module.DefaultImporter.ImportType(typeof(Exception)).ToTypeSignature(false)
        );
        body.LocalVariables.Add(exLocal);

        // Declare locals for each param (load from fields at resume points)
        var paramLocals = new Dictionary<string, CilLocalVariable>();
        foreach (var p in func.Params)
        {
            var pLocal = new CilLocalVariable(MapToClr(p.Type));
            body.LocalVariables.Add(pLocal);
            paramLocals[p.Name] = pLocal;
        }

        // Pre-compute per-with-handlers trampoline labels. A with-handlers that
        // contains an await needs a trampoline placed immediately before its
        // TryStart so that the parent dispatch can route into it without
        // branching across a try-region boundary.
        var trampolineLabels = new Dictionary<IrNode.WithHandlers, CilInstructionLabel>(
            ReferenceEqualityComparer.Instance
        );
        var awaitTryChains = info.AwaitPoints.Select(ap => ap.EnclosingTryBodies).ToList();
        foreach (var chain in awaitTryChains)
        foreach (var wh in chain)
            if (!trampolineLabels.ContainsKey(wh))
                trampolineLabels[wh] = new CilInstructionLabel();

        // Set up MoveNext context
        _moveNextCtx = new AsyncMoveNextContext
        {
            SmType = smType,
            StateField = stateField,
            BuilderField = builderField,
            StateLocal = stateLocal,
            VarFields = varFields,
            AwaiterFields = awaiterFields,
            AllLocals = [],
            IsVoidReturn = info.IsVoidReturn,
            ThisField = thisField,
            NextAwaitState = 0,
            AwaitTryChains = awaitTryChains,
            TrampolineLabels = trampolineLabels,
        };

        // Add param locals to the AllLocals tracking
        foreach (var p in func.Params)
            _moveNextCtx.AllLocals.Add((p.Name, paramLocals[p.Name]));

        // Load __state into local
        il.Add(CilOpCodes.Ldarg_0);
        il.Add(CilOpCodes.Ldfld, stateField);
        il.Add(CilOpCodes.Stloc, stateLocal);

        // --- Try block ---
        var tryStartLabel = new CilInstructionLabel { Instruction = il.Add(CilOpCodes.Nop) };

        // Jump table: create resume labels for each await point
        var resumeLabels = new CilInstructionLabel[info.AwaitPoints.Count];
        for (var i = 0; i < info.AwaitPoints.Count; i++)
            resumeLabels[i] = new CilInstructionLabel();

        // Outer dispatch: for each await, jump to the resume label if the
        // await is at the top level, or to the outermost enclosing
        // with-handlers' trampoline if it's inside a nested try. Branching
        // directly into a nested try region is illegal CIL (BranchIntoTry).
        if (resumeLabels.Length > 0)
        {
            var dispatchTargets = new ICilLabel[resumeLabels.Length];
            for (var i = 0; i < resumeLabels.Length; i++)
            {
                var chain = awaitTryChains[i];
                dispatchTargets[i] =
                    chain.Count == 0 ? resumeLabels[i] : trampolineLabels[chain[0]];
            }

            il.Add(CilOpCodes.Ldloc, stateLocal);
            il.Add(CilOpCodes.Switch, dispatchTargets);
        }

        // Initial state: load params from fields into locals
        foreach (var p in func.Params)
        {
            il.Add(CilOpCodes.Ldarg_0);
            il.Add(CilOpCodes.Ldfld, varFields[p.Name]);
            il.Add(CilOpCodes.Stloc, paramLocals[p.Name]);
        }

        // Store resume labels and exit label for EmitMoveNextAwait to use
        _moveNextCtx.ResumeLabels = resumeLabels;
        var exitLabel = new CilInstructionLabel();
        _moveNextCtx.ExitLabel = exitLabel;

        // Emit the body using regular EmitNode (outerParams is empty; params come from locals dict)
        var bodyLocals = new Dictionary<string, CilLocalVariable>(paramLocals);
        EmitNode(func.Body, il, [], bodyLocals);

        // Store the result
        if (!info.IsVoidReturn)
            il.Add(CilOpCodes.Stloc, resultLocal!);
        else if (
            func.Body.Type is not null and not ZType.ZPrimitiveType { Kind: PrimitiveKind.Unit }
        )
            il.Add(CilOpCodes.Pop);

        // Leave try block
        var afterTryLabel = new CilInstructionLabel();
        il.Add(CilOpCodes.Leave, afterTryLabel);

        // --- Catch block ---
        var catchStartLabel = new CilInstructionLabel { Instruction = il.Add(CilOpCodes.Nop) };
        il.Add(CilOpCodes.Stloc, exLocal);

        // __state = -2
        il.Add(CilOpCodes.Ldarg_0);
        il.Add(CilOpCodes.Ldc_I4, -2);
        il.Add(CilOpCodes.Stfld, stateField);

        // __builder.SetException(ex)
        var setException = _module.DefaultImporter.ImportMethod(
            builderClrType.GetMethod("SetException", [typeof(Exception)])!
        );
        il.Add(CilOpCodes.Ldarg_0);
        il.Add(CilOpCodes.Ldflda, builderField);
        il.Add(CilOpCodes.Ldloc, exLocal);
        il.Add(CilOpCodes.Call, setException);

        il.Add(CilOpCodes.Leave, exitLabel);

        // --- After try/catch ---
        afterTryLabel.Instruction = il.Add(CilOpCodes.Nop);

        // __state = -2
        il.Add(CilOpCodes.Ldarg_0);
        il.Add(CilOpCodes.Ldc_I4, -2);
        il.Add(CilOpCodes.Stfld, stateField);

        // __builder.SetResult(result)
        // __builder.SetResult(result)
        if (info.IsVoidReturn)
        {
            var setResult = _module.DefaultImporter.ImportMethod(
                builderClrType.GetMethod("SetResult", Type.EmptyTypes)!
            );
            il.Add(CilOpCodes.Ldarg_0);
            il.Add(CilOpCodes.Ldflda, builderField);
            il.Add(CilOpCodes.Call, setResult);
        }
        else
        {
            var setResultMethod = builderClrType.GetMethod(
                "SetResult",
                [MapToReflectionClr(func.ReturnType)]
            )!;
            var setResult = _module.DefaultImporter.ImportMethod(setResultMethod);
            il.Add(CilOpCodes.Ldarg_0);
            il.Add(CilOpCodes.Ldflda, builderField);
            il.Add(CilOpCodes.Ldloc, resultLocal!);
            il.Add(CilOpCodes.Call, setResult);
        }

        exitLabel.Instruction = il.Add(CilOpCodes.Nop);
        il.Add(CilOpCodes.Ret);

        // Register exception handler
        il.Owner.ExceptionHandlers.Add(
            new CilExceptionHandler
            {
                HandlerType = CilExceptionHandlerType.Exception,
                TryStart = tryStartLabel,
                TryEnd = catchStartLabel,
                HandlerStart = catchStartLabel,
                HandlerEnd = afterTryLabel,
                ExceptionType = _module
                    .DefaultImporter.ImportType(typeof(Exception))
                    .ToTypeDefOrRef(),
            }
        );

        _moveNextCtx = null;
    }

    private void EmitMoveNextAwait(
        IrNode.Await awaitNode,
        CilInstructionCollection il,
        IReadOnlyList<IrParam> outerParams,
        Dictionary<string, CilLocalVariable> locals
    )
    {
        var ctx = _moveNextCtx!;
        var stateNum = ctx.NextAwaitState++;
        var awaiterField = ctx.AwaiterFields[stateNum];
        var resumeLabel = ctx.ResumeLabels![stateNum];
        var resultType = AsyncStateMachineAnalyzer.GetAwaitResultType(
            awaitNode.Expr.Type,
            _typeAliases
        );
        var isVoidAwait = resultType is ZType.ZPrimitiveType { Kind: PrimitiveKind.Unit };

        // Determine awaiter CLR type
        Type awaiterClrType;
        if (isVoidAwait)
            awaiterClrType = typeof(TaskAwaiter);
        else
            awaiterClrType = typeof(TaskAwaiter<>).MakeGenericType(MapToReflectionClr(resultType));

        // Declare a local for the awaiter
        var awaiterLocal = new CilLocalVariable(
            _module
                .DefaultImporter.ImportType(awaiterClrType)
                .ToTypeSignature(awaiterClrType.IsValueType)
        );
        il.Owner.LocalVariables.Add(awaiterLocal);

        // Emit the task expression
        EmitNode(awaitNode.Expr, il, outerParams, locals);

        // Call GetAwaiter()
        var taskClrType = MapToReflectionClr(awaitNode.Expr.Type);
        var getAwaiterMethod = taskClrType.GetMethod("GetAwaiter", Type.EmptyTypes)!;
        il.Add(CilOpCodes.Call, _module.DefaultImporter.ImportMethod(getAwaiterMethod));
        il.Add(CilOpCodes.Stloc, awaiterLocal);

        // Check IsCompleted
        var isCompletedGetter = awaiterClrType.GetProperty("IsCompleted")!.GetGetMethod()!;
        var completedLabel = new CilInstructionLabel();

        il.Add(CilOpCodes.Ldloca, awaiterLocal);
        il.Add(CilOpCodes.Call, _module.DefaultImporter.ImportMethod(isCompletedGetter));
        il.Add(CilOpCodes.Brtrue, completedLabel);

        // --- Not completed: suspend ---

        // Set state
        il.Add(CilOpCodes.Ldarg_0);
        il.Add(CilOpCodes.Ldc_I4, stateNum);
        il.Add(CilOpCodes.Stfld, ctx.StateField);
        il.Add(CilOpCodes.Ldc_I4, stateNum);
        il.Add(CilOpCodes.Stloc, ctx.StateLocal);

        // Store awaiter to field
        il.Add(CilOpCodes.Ldarg_0);
        il.Add(CilOpCodes.Ldloc, awaiterLocal);
        il.Add(CilOpCodes.Stfld, awaiterField);

        // Save all locals to fields
        foreach (var (name, local) in ctx.AllLocals)
            if (ctx.VarFields.TryGetValue(name, out var field))
            {
                il.Add(CilOpCodes.Ldarg_0);
                il.Add(CilOpCodes.Ldloc, local);
                il.Add(CilOpCodes.Stfld, field);
            }

        // Call __builder.AwaitUnsafeOnCompleted(ref awaiter, ref this)
        var awaitUnsafe = GetAwaitUnsafeOnCompletedRef(awaiterClrType, ctx);
        il.Add(CilOpCodes.Ldarg_0);
        il.Add(CilOpCodes.Ldflda, ctx.BuilderField);
        il.Add(CilOpCodes.Ldarg_0);
        il.Add(CilOpCodes.Ldflda, awaiterField);
        il.Add(CilOpCodes.Ldarg_0);
        il.Add(CilOpCodes.Call, awaitUnsafe);

        // Leave try block (cannot use ret inside try)
        il.Add(CilOpCodes.Leave, ctx.ExitLabel!);

        // --- Resume label (jump table target) ---
        resumeLabel.Instruction = il.Add(CilOpCodes.Nop);

        // Restore awaiter from field
        il.Add(CilOpCodes.Ldarg_0);
        il.Add(CilOpCodes.Ldfld, awaiterField);
        il.Add(CilOpCodes.Stloc, awaiterLocal);

        // Clear awaiter field
        il.Add(CilOpCodes.Ldarg_0);
        il.Add(CilOpCodes.Ldflda, awaiterField);
        il.Add(
            CilOpCodes.Initobj,
            _module
                .DefaultImporter.ImportType(awaiterClrType)
                .ToTypeSignature(awaiterClrType.IsValueType)
                .ToTypeDefOrRef()
        );

        // Reset state to -1
        il.Add(CilOpCodes.Ldc_I4_M1);
        il.Add(CilOpCodes.Stloc, ctx.StateLocal);
        il.Add(CilOpCodes.Ldarg_0);
        il.Add(CilOpCodes.Ldc_I4_M1);
        il.Add(CilOpCodes.Stfld, ctx.StateField);

        // Restore all locals from fields
        foreach (var (name, local) in ctx.AllLocals)
            if (ctx.VarFields.TryGetValue(name, out var field))
            {
                il.Add(CilOpCodes.Ldarg_0);
                il.Add(CilOpCodes.Ldfld, field);
                il.Add(CilOpCodes.Stloc, local);
            }

        // --- Completed label (fast path + resume path converge) ---
        completedLabel.Instruction = il.Add(CilOpCodes.Nop);

        // Call GetResult()
        var getResultMethod = awaiterClrType.GetMethod("GetResult", Type.EmptyTypes)!;
        il.Add(CilOpCodes.Ldloca, awaiterLocal);
        il.Add(CilOpCodes.Call, _module.DefaultImporter.ImportMethod(getResultMethod));

        // Result (T or void) is now on the stack
    }

    private void EmitSetStateMachineMethod(
        TypeDefinition smType,
        FieldDefinition builderField,
        Type builderClrType
    )
    {
        var setSmMethod = new MethodDefinition(
            "SetStateMachine",
            MethodAttributes.Private
                | MethodAttributes.Final
                | MethodAttributes.HideBySig
                | MethodAttributes.NewSlot
                | MethodAttributes.Virtual,
            MethodSignature.CreateInstance(
                _module.CorLibTypeFactory.Void,
                [
                    _module
                        .DefaultImporter.ImportType(typeof(IAsyncStateMachine))
                        .ToTypeSignature(false),
                ]
            )
        );
        setSmMethod.ParameterDefinitions.Add(new ParameterDefinition(1, "stateMachine", 0));
        smType.Methods.Add(setSmMethod);

        // Override IAsyncStateMachine.SetStateMachine
        var setSmIntf = _module.DefaultImporter.ImportMethod(
            typeof(IAsyncStateMachine).GetMethod("SetStateMachine")!
        );
        smType.MethodImplementations.Add(
            new MethodImplementation((IMethodDefOrRef)setSmIntf, setSmMethod)
        );

        var body = new CilMethodBody { InitializeLocals = true };
        setSmMethod.MethodBody = body;
        body.Instructions.Add(CilOpCodes.Ret);
    }
}
