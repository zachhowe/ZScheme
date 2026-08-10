using System.Reflection;
using System.Runtime.CompilerServices;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using Serilog;
using ZScheme.Compiler.Ir;
using ZScheme.Compiler.Types;
using AsyncMoveNextContext = ZScheme.Compiler.Codegen.IlEmitter.AsyncMoveNextContext;
using EmitContext = ZScheme.Compiler.Codegen.IlEmitter.EmitContext;
using FieldAttributes = AsmResolver.PE.DotNet.Metadata.Tables.FieldAttributes;
using MethodAttributes = AsmResolver.PE.DotNet.Metadata.Tables.MethodAttributes;
using TypeAttributes = AsmResolver.PE.DotNet.Metadata.Tables.TypeAttributes;

namespace ZScheme.Compiler.Codegen;

/// <summary>
///     Generates the async/await state-machine IL for <see cref="IlEmitter" />. This is the
///     cohesive async subsystem (EmitAsyncFuncDef → EmitMoveNextMethod → EmitMoveNextAwait →
///     EmitSetStateMachineMethod, plus the stub body and entry-point shim), extracted from the
///     main emitter (IL_REFACTOR.md issue #4). It holds a back-reference to the host emitter and
///     calls back into its general emission helpers (<c>EmitNode</c>, type mapping, etc.) for the
///     parts that are not async-specific. The <c>_asyncSmCounter</c> monotonic name generator
///     lives here because only async emission uses it.
/// </summary>
internal sealed class IlAsyncEmitter(IlEmitter host)
{
    private static readonly ILogger Log = Serilog.Log.ForContext<IlAsyncEmitter>();

    private readonly IlEmitter _host = host;

    // Counter for state-machine type-name uniquification (<func>d__0, <func>d__1, ...).
    private int _asyncSmCounter;

    // Forwarding aliases so the copied bodies reference host state unchanged.
    private ModuleDefinition _module => _host._module;
    private TypeAliasRegistry _typeAliases => _host._typeAliases;

    internal void EmitAsyncFuncDef(
        IrNode.FuncDef func,
        MethodDefinition stubMethod,
        TypeDefinition parentType,
        EmitContext ctx
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
        var smName = $"<{IlEmitter.Sanitize(func.Name)}>d__{_asyncSmCounter++}";

        // Determine builder and task types
        var isVoid = info.IsVoidReturn;
        Type builderClrType;
        if (isVoid)
            builderClrType = typeof(AsyncTaskMethodBuilder);
        else
            builderClrType = typeof(AsyncTaskMethodBuilder<>).MakeGenericType(
                _host.MapToReflectionClr(func.ReturnType)
            );

        // For generic closed builders (AsyncTaskMethodBuilder<T>), build a
        // GenericInstanceTypeSignature so later code that inspects the builder field
        // signature (e.g. GetAwaitUnsafeOnCompletedRef) can recognize the closed
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
        if (ctx.InstanceArgOffset == 1 && ctx.CurrentTypeDefinition is not null)
        {
            thisField = new FieldDefinition(
                "__this",
                FieldAttributes.Public,
                new FieldSignature(ctx.CurrentTypeDefinition.ToTypeSignature(false))
            );
            smType.Fields.Add(thisField);
        }

        // Parameter fields
        var varFields = new Dictionary<string, FieldDefinition>();
        foreach (var p in func.Params)
        {
            var pField = new FieldDefinition(
                IlEmitter.Sanitize(p.Name),
                FieldAttributes.Public,
                new FieldSignature(_host.MapToClr(p.Type, ctx))
            );
            smType.Fields.Add(pField);
            varFields[p.Name] = pField;
        }

        // Hoisted local fields
        foreach (var local in info.HoistedLocals)
            if (!varFields.ContainsKey(local.Name))
            {
                var lField = new FieldDefinition(
                    $"<{IlEmitter.Sanitize(local.Name)}>5__",
                    FieldAttributes.Public,
                    new FieldSignature(_host.MapToClr(local.Type, ctx))
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
            ctx,
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
            ? _host.ImportClosedGenericMethod(builderClrType, "Create")
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
            ? _host.ImportClosedGenericMethod(builderClrType, "Start")
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
            ? _host.ImportClosedGenericMethod(builderClrType, "Task")
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
        EmitContext ctx,
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
            resultLocal = new CilLocalVariable(_host.MapToClr(func.ReturnType, ctx));
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
            var pLocal = new CilLocalVariable(_host.MapToClr(p.Type, ctx));
            body.LocalVariables.Add(pLocal);
            paramLocals[p.Name] = pLocal;
        }

        // Pre-compute per-try-region trampoline labels. A try region (with-handlers
        // or use) that contains an await needs a trampoline placed immediately before
        // its TryStart so that the parent dispatch can route into it without branching
        // across a try-region boundary.
        var trampolineLabels = new Dictionary<IrNode, CilInstructionLabel>(
            ReferenceEqualityComparer.Instance
        );
        var awaitTryChains = info.AwaitPoints.Select(ap => ap.EnclosingTryBodies).ToList();
        foreach (var chain in awaitTryChains)
        foreach (var tryNode in chain)
            if (!trampolineLabels.ContainsKey(tryNode))
                trampolineLabels[tryNode] = new CilInstructionLabel();

        // Set up MoveNext context
        var moveNextCtx = new AsyncMoveNextContext
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
            moveNextCtx.AllLocals.Add((p.Name, paramLocals[p.Name]));

        var moveNextBodyCtx = ctx with { MoveNextCtx = moveNextCtx };

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
        moveNextCtx.ResumeLabels = resumeLabels;
        var exitLabel = new CilInstructionLabel();
        moveNextCtx.ExitLabel = exitLabel;

        // Declared here rather than after the body because a TCO loop's leaves each Leave to it;
        // its Instruction is still assigned once, after the catch block.
        var afterTryLabel = new CilInstructionLabel();

        // Emit the body using regular EmitNode (outerParams is empty; params come from locals dict)
        var bodyLocals = new Dictionary<string, CilLocalVariable>(paramLocals);

        if (func.IsTcoLoop)
        {
            // TailCallLowering turned this function's awaited tail self-calls into TcoJump
            // back-edges, so the body is a loop rather than a single value expression.
            //
            // The start label sits *after* the field->local parameter reload above, so a
            // back-edge does not re-clobber the parameters the jump just assigned with the
            // values a previous suspension flushed to the fields. Only initial entry and
            // resume-from-suspend read those fields, and both reach the body from above this
            // label — so a back-edge needs only Stloc; a Stfld would be a dead store, because
            // EmitMoveNextAwait flushes every local to its field before it suspends.
            //
            // The Br is backward to a label inside the same protected region (legal CIL) and is
            // reached at stack depth 0, since EmitLoopBody stages the jump arguments into temps.
            // Re-entering a statically-numbered await point on a later iteration is exactly what
            // Roslyn emits for `while` + `await`: the awaiter field is only live between one
            // suspend and its resume.
            //
            // Every leaf terminates itself here, so the straight-line store-result/Leave in the
            // else branch is not emitted — control never falls out of the loop body.
            var startLabel = new CilInstructionLabel { Instruction = il.Add(CilOpCodes.Nop) };
            _host.EmitLoopBody(
                func.Body,
                il,
                [],
                bodyLocals,
                moveNextBodyCtx,
                startLabel,
                new IlEmitter.LoopExit(
                    ParamSlotType: i => paramLocals[func.Params[i].Name].VariableType,
                    StoreParam: (i, tmp) =>
                    {
                        il.Add(CilOpCodes.Ldloc, tmp);
                        il.Add(CilOpCodes.Stloc, paramLocals[func.Params[i].Name]);
                    },
                    EmitLeaf: leaf =>
                    {
                        _host.EmitNode(leaf, il, [], bodyLocals, moveNextBodyCtx);
                        if (!info.IsVoidReturn)
                            il.Add(CilOpCodes.Stloc, resultLocal!);
                        else if (
                            leaf.Type
                            is not null
                                and not ZType.ZPrimitiveType { Kind: PrimitiveKind.Unit }
                        )
                            il.Add(CilOpCodes.Pop);

                        il.Add(CilOpCodes.Leave, afterTryLabel);
                    }
                )
            );
        }
        else
        {
            _host.EmitNode(func.Body, il, [], bodyLocals, moveNextBodyCtx);

            // Store the result
            if (!info.IsVoidReturn)
                il.Add(CilOpCodes.Stloc, resultLocal!);
            else if (
                func.Body.Type is not null and not ZType.ZPrimitiveType { Kind: PrimitiveKind.Unit }
            )
                il.Add(CilOpCodes.Pop);

            // Leave try block
            il.Add(CilOpCodes.Leave, afterTryLabel);
        }

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
                [_host.MapToReflectionClr(func.ReturnType)]
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
    }

    internal void EmitMoveNextAwait(
        IrNode.Await awaitNode,
        CilInstructionCollection il,
        IReadOnlyList<IrParam> outerParams,
        Dictionary<string, CilLocalVariable> locals,
        EmitContext ctx
    )
    {
        var mnCtx = ctx.MoveNextCtx!;
        var stateNum = mnCtx.NextAwaitState++;
        var awaiterField = mnCtx.AwaiterFields[stateNum];
        var resumeLabel = mnCtx.ResumeLabels![stateNum];
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
            awaiterClrType = typeof(TaskAwaiter<>).MakeGenericType(
                _host.MapToReflectionClr(resultType)
            );

        // Declare a local for the awaiter
        var awaiterLocal = new CilLocalVariable(
            _module
                .DefaultImporter.ImportType(awaiterClrType)
                .ToTypeSignature(awaiterClrType.IsValueType)
        );
        il.Owner.LocalVariables.Add(awaiterLocal);

        // Emit the task expression
        _host.EmitNode(awaitNode.Expr, il, outerParams, locals, ctx);

        // Call GetAwaiter()
        var taskClrType = _host.MapToReflectionClr(awaitNode.Expr.Type);
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
        il.Add(CilOpCodes.Stfld, mnCtx.StateField);
        il.Add(CilOpCodes.Ldc_I4, stateNum);
        il.Add(CilOpCodes.Stloc, mnCtx.StateLocal);

        // Store awaiter to field
        il.Add(CilOpCodes.Ldarg_0);
        il.Add(CilOpCodes.Ldloc, awaiterLocal);
        il.Add(CilOpCodes.Stfld, awaiterField);

        // Save all locals to fields
        foreach (var (name, local) in mnCtx.AllLocals)
            if (mnCtx.VarFields.TryGetValue(name, out var field))
            {
                il.Add(CilOpCodes.Ldarg_0);
                il.Add(CilOpCodes.Ldloc, local);
                il.Add(CilOpCodes.Stfld, field);
            }

        // Call __builder.AwaitUnsafeOnCompleted(ref awaiter, ref this)
        var awaitUnsafe = GetAwaitUnsafeOnCompletedRef(awaiterClrType, mnCtx);
        il.Add(CilOpCodes.Ldarg_0);
        il.Add(CilOpCodes.Ldflda, mnCtx.BuilderField);
        il.Add(CilOpCodes.Ldarg_0);
        il.Add(CilOpCodes.Ldflda, awaiterField);
        il.Add(CilOpCodes.Ldarg_0);
        il.Add(CilOpCodes.Call, awaitUnsafe);

        // Leave try block (cannot use ret inside try)
        il.Add(CilOpCodes.Leave, mnCtx.ExitLabel!);

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
        il.Add(CilOpCodes.Stloc, mnCtx.StateLocal);
        il.Add(CilOpCodes.Ldarg_0);
        il.Add(CilOpCodes.Ldc_I4_M1);
        il.Add(CilOpCodes.Stfld, mnCtx.StateField);

        // Restore all locals from fields
        foreach (var (name, local) in mnCtx.AllLocals)
            if (mnCtx.VarFields.TryGetValue(name, out var field))
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

    internal void EmitAwait(
        IrNode.Await awaitNode,
        CilInstructionCollection il,
        IReadOnlyList<IrParam> outerParams,
        Dictionary<string, CilLocalVariable> locals,
        EmitContext ctx
    )
    {
        // Emit the task expression (pushes Task<T> or Task on stack)
        _host.EmitNode(awaitNode.Expr, il, outerParams, locals, ctx);

        // Resolve GetAwaiter() and GetResult() via reflection on the CLR task type
        var taskClrType = _host.MapToReflectionClr(awaitNode.Expr.Type);
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

    /// <summary>
    ///     Emits a synchronous entry-point shim for an async <c>main</c>. The CLR entry point
    ///     must return void/int (ECMA-335 §15.4.1.2), so a Task-returning <c>main</c> cannot be
    ///     the entry point directly. The shim calls the async <c>main</c> and blocks on the
    ///     resulting Task via <c>GetAwaiter().GetResult()</c> (the same wrapper Roslyn generates
    ///     for <c>async Task Main</c>), yielding the awaited Int — or 0 for a Unit/Task main — as
    ///     the process exit code.
    /// </summary>
    internal MethodDefinition EmitAsyncEntryPointShim(
        IrNode.FuncDef mainFuncDef,
        MethodDefinition userMain,
        TypeDefinition typeDef
    )
    {
        var shim = new MethodDefinition(
            "<Main>$",
            MethodAttributes.Public | MethodAttributes.Static,
            MethodSignature.CreateStatic(
                _module.CorLibTypeFactory.Int32,
                [new SzArrayTypeSignature(_module.CorLibTypeFactory.String)]
            )
        );
        shim.ParameterDefinitions.Add(new ParameterDefinition(1, "args", 0));
        typeDef.Methods.Add(shim);

        var body = new CilMethodBody { InitializeLocals = true };
        shim.MethodBody = body;
        var il = body.Instructions;

        // Call the async main → Task<Int> (Int main) or Task (Unit main). The validated param,
        // if present, is the string[] forwarded straight through.
        if (userMain.Parameters.Count > 0)
            il.Add(CilOpCodes.Ldarg_0);
        il.Add(CilOpCodes.Call, userMain);

        // Block on the Task via GetAwaiter().GetResult(), mirroring EmitAwait. main.ReturnType is
        // the awaited inner type: Int -> the user main returns Task<int>; Unit -> non-generic Task.
        var returnsInt = mainFuncDef.ReturnType is ZType.ZPrimitiveType { Kind: PrimitiveKind.Int };
        var taskClrType = returnsInt
            ? typeof(Task<>).MakeGenericType(_host.MapToReflectionClr(mainFuncDef.ReturnType))
            : typeof(Task);
        var getAwaiterMethod = taskClrType.GetMethod("GetAwaiter", Type.EmptyTypes)!;
        var awaiterType = getAwaiterMethod.ReturnType;
        var getResultMethod = awaiterType.GetMethod("GetResult", Type.EmptyTypes)!;

        il.Add(
            CilOpCodes.Call,
            (IMethodDefOrRef)_module.DefaultImporter.ImportMethod(getAwaiterMethod)
        );

        // The awaiter is a struct — store it and call GetResult through its address.
        var awaiterLocal = new CilLocalVariable(
            _module.DefaultImporter.ImportType(awaiterType).ToTypeSignature(awaiterType.IsValueType)
        );
        body.LocalVariables.Add(awaiterLocal);
        il.Add(CilOpCodes.Stloc, awaiterLocal);
        il.Add(CilOpCodes.Ldloca, awaiterLocal);
        il.Add(
            CilOpCodes.Call,
            (IMethodDefOrRef)_module.DefaultImporter.ImportMethod(getResultMethod)
        );

        // Task<int>.GetResult() leaves the Int exit code on the stack; Task.GetResult() is void,
        // so synthesize a 0 exit code.
        if (!returnsInt)
            il.Add(CilOpCodes.Ldc_I4_0);

        il.Add(CilOpCodes.Ret);
        return shim;
    }

    private Type GetAwaiterClrType(AsyncStateMachineAnalyzer.AwaitPointInfo ap)
    {
        if (ap.ResultType is ZType.ZPrimitiveType { Kind: PrimitiveKind.Unit })
            return typeof(TaskAwaiter);
        var innerClr = _host.MapToReflectionClr(ap.ResultType);
        return typeof(TaskAwaiter<>).MakeGenericType(innerClr);
    }

    private MethodSpecification GetAwaitUnsafeOnCompletedRef(
        Type awaiterClrType,
        AsyncMoveNextContext ctx
    )
    {
        // Import the AwaitUnsafeOnCompleted method from the builder type
        var builderType = ctx.BuilderField.Signature!.FieldType;

        // Find the open AwaitUnsafeOnCompleted method on the CLR builder type
        Type builderClrType;
        if (ctx.IsVoidReturn)
        {
            builderClrType = typeof(AsyncTaskMethodBuilder);
        }
        else
        {
            // Reconstruct the closed generic builder CLR type
            // The builder field's signature tells us the type args
            if (builderType is GenericInstanceTypeSignature git)
            {
                var innerClrTypes = git
                    .TypeArguments.Select(ta =>
                    {
                        // Map back from TypeSignature to CLR Type
                        var fullName = ta.FullName;
                        return Type.GetType(fullName) ?? typeof(object);
                    })
                    .ToArray();
                builderClrType = typeof(AsyncTaskMethodBuilder<>).MakeGenericType(innerClrTypes);
            }
            else
            {
                builderClrType = typeof(AsyncTaskMethodBuilder);
            }
        }

        // Use the AsmResolver approach:
        // Import the open generic method, then create a MethodSpecification
        var openAwaitMethod = builderClrType
            .GetMethods()
            .First(m => m is { Name: "AwaitUnsafeOnCompleted", IsGenericMethodDefinition: true });
        var importedMethod = (IMethodDefOrRef)_module.DefaultImporter.ImportMethod(openAwaitMethod);

        // If the builder is a generic instance, we need to reference the method on the closed type
        if (builderType is GenericInstanceTypeSignature gitSig)
        {
            // Create a MemberReference on the closed generic builder type
            var openMethod = typeof(AsyncTaskMethodBuilder<>)
                .GetMethods()
                .First(m => m.Name == "AwaitUnsafeOnCompleted" && m.IsGenericMethodDefinition);
            var openParams = openMethod.GetParameters();

            var sig = MethodSignature.CreateInstance(
                _module.CorLibTypeFactory.Void,
                [
                    new GenericParameterSignature(
                        _module,
                        GenericParameterType.Method,
                        0
                    ).MakeByReferenceType(),
                    new GenericParameterSignature(
                        _module,
                        GenericParameterType.Method,
                        1
                    ).MakeByReferenceType(),
                ]
            );
            sig.GenericParameterCount = 2;
            sig.Attributes |= CallingConventionAttributes.Generic;
            var memberRef = new MemberReference(
                gitSig.ToTypeDefOrRef(),
                "AwaitUnsafeOnCompleted",
                sig
            );
            importedMethod = memberRef;
        }

        var awaiterSig = _module
            .DefaultImporter.ImportType(awaiterClrType)
            .ToTypeSignature(awaiterClrType.IsValueType);
        var smSig = ctx.SmType.ToTypeSignature(true); // state machines are always value types

        return new MethodSpecification(
            importedMethod,
            new GenericInstanceMethodSignature([awaiterSig, smSig])
        );
    }
}
