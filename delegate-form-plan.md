# Plan: `(delegate ...)` Syntax Form

## Problem

ZScheme compiles all `ZFuncType` values to `System.Func<>` / `System.Action<>` at code generation. When ASP.NET Core's `MapGet` expects a `RequestDelegate` (a different delegate type with the same shape as `Func<HttpContext, Task>`), the compiler cannot produce the correct CLR type — there's no way for the ZScheme programmer to say "I want this specific CLR delegate type."

## Design

The `(delegate ...)` form introduces a new type node that carries a **CLR delegate type name** through the pipeline, bypassing the default `Func<>`/`Action<>` mapping at codegen.

### 1. Syntax

```scheme
(delegate Fully.Qualified.DelegateType)
```

This is a **type expression only** (not a value expression), usable wherever type annotations appear:

- **`import-clr` annotation:** `(import-clr handler MyNamespace.MyDelegate : (delegate MyNamespace.MyDelegate) (Int -> Unit))`
- **`let` binding:** `(let [x : (delegate MyDelegate) expr] body)` — casts an expression to a specific delegate type
- **`Param` annotation:** `(lambda ([x : (delegate MyDelegate)]) body)`

### 2. AST Changes

**`AstNode.cs`** — Add new ZType variant:

```csharp
// In ZType.cs
public sealed record ZDelegateType(string ClrTypeName) : ZType
```

**No new AST node needed** — `(delegate ...)` is parsed as a type expression by `ParseTypeExpr` in `AstBuilder.cs`, producing a `ZDelegateType`. It's not a value expression, so no `AstNode` subclass is required.

### 3. Type System Changes

**`AstBuilder.cs`** — Extend `ParseTypeExpr` (line ~2278):

Add a case for detecting the `(delegate TypeName)` pattern:
```
SExpr.SList with first item atom "delegate" -> ZDelegateType(typeName)
```

**`ZType.cs`** — Add `ZDelegateType` record (after `ZNullableType`, ~line 216).

**`ZType.Format()`** — Render `ZDelegateType` as `(delegate ClrTypeName)`.

**`Unifier.cs`** — `ZDelegateType` is a concrete named type. It should unify against other `ZDelegateType` if names match, or fall through to CLR subtype checking.

### 4. Type Inference Changes

**`TypeInferer.cs`** — `ResolveTypeInEnv()`:

`ZDelegateType` passes through unchanged (the CLR type name is already concrete).

**`TypeInferer.cs`** — `InferImportClr()`:

When an `import-clr` has a `TypeAnnotation` of `ZDelegateType`, store it directly so codegen can use it.

### 5. IR Lowering Changes

**`IrNode.cs`** — Extend `IrNode.FuncDef`:

Add an optional `ClrDelegateTypeName` property:

```csharp
public string? ClrDelegateTypeName { get; set; }
```

**`IrLowering.cs`** — `LowerLambda`:

If the lambda's resolved type is `ZDelegateType`, propagate the CLR type name to the IR node.

**`IrLowering.cs`** — `LowerApply` (CLR call site):

When a CLR overload argument is a `ZDelegateType`, the IR node carries the CLR type name so the emitter knows which delegate constructor to emit.

### 6. Code Generation Changes

**`CSharpEmitter.cs`** — `TypeToCs()` (lines 724-773):

Add a case before the `ZFuncType` cases:
```csharp
ZType.ZDelegateType dt => dt.ClrTypeName
```

**`CSharpEmitter.cs`** — `LambdaDelegateType()` (line ~573):

If the lambda has `ClrDelegateTypeName` set, return it instead of computing `Func<>`/`Action<>`.

**`CSharpEmitter.Emit.cs`** — `EmitLambdaExpr()`:

If the lambda has `ClrDelegateTypeName`, wrap the lambda expression in a cast:
```csharp
$"(({ClrDelegateTypeName})(({params}) => {{ {body}; }}))"
```

**`IlTypeMapper.cs`** — `MapToClr()`:

Add a case for `ZDelegateType` that resolves the CLR type by name via `Type.GetType(clrTypeName)`.

**`AsmResolverTypeMapper.cs`** — Same treatment for IL emission.

### 7. CLR Interop Changes

**`ClrInterop.cs`** — `MapClrTypeToZType()` (lines 174-212):

Add a case that maps CLR delegate types to `ZDelegateType` (reverse direction):
```csharp
if (clrType.IsDelegate)
    return new ZType.ZDelegateType(clrType.FullName);
```

This means when a CLR method returns a delegate type, it's represented as `ZDelegateType` in ZScheme.

### 8. Tests

**`tests/ZScheme.Compiler.Tests/`** — Add:

1. **Ast/TypeExpression tests** — Verify `(delegate Fully.Qualified.Type)` parses to `ZDelegateType`
2. **Types/TypeInference tests** — Verify `ZDelegateType` passes through inference unchanged
3. **Codegen/CSharpEmitter tests** — Verify `ZDelegateType` emits the correct C# type name
4. **Integration tests** — A `.zs` file that uses `(delegate ...)` to pass a function to a CLR method expecting a specific delegate type

## Files to Modify

| File | Change |
|------|--------|
| `src/ZScheme.Compiler/Types/ZType.cs` | Add `ZDelegateType` record |
| `src/ZScheme.Compiler/Ast/AstBuilder.cs` | Extend `ParseTypeExpr` to handle `(delegate ...)` |
| `src/ZScheme.Compiler/Types/TypeInferer.cs` | Handle `ZDelegateType` in `ResolveTypeInEnv` |
| `src/ZScheme.Compiler/Ir/IrNode.cs` | Add `ClrDelegateTypeName` property to `FuncDef` |
| `src/ZScheme.Compiler/Ir/IrLowering.cs` | Propagate `ClrDelegateTypeName` from `ZDelegateType` |
| `src/ZScheme.Compiler/Codegen/CSharpEmitter.cs` | Handle `ZDelegateType` in `TypeToCs` and `LambdaDelegateType` |
| `src/ZScheme.Compiler/Codegen/CSharpEmitter.Emit.cs` | Emit delegate cast in `EmitLambdaExpr` |
| `src/ZScheme.Compiler/Codegen/IlTypeMapper.cs` | Handle `ZDelegateType` in `MapToClr` |
| `src/ZScheme.Compiler/Codegen/AsmResolverTypeMapper.cs` | Handle `ZDelegateType` |
| `src/ZScheme.Compiler/Codegen/ClrInterop.cs` | Map CLR delegate types to `ZDelegateType` in `MapClrTypeToZType` |
| `tests/ZScheme.Compiler.Tests/` | Add tests for the new type node |

## Implementation Order

1. **`ZDelegateType` + parsing** — Add the type node and extend `ParseTypeExpr`. Build and verify parsing works.
2. **Type inference pass-through** — Ensure `ZDelegateType` flows through inference unchanged.
3. **IR propagation** — Add `ClrDelegateTypeName` to IR nodes and propagate from AST.
4. **C# codegen** — Handle `ZDelegateType` in `TypeToCs`, `LambdaDelegateType`, and `EmitLambdaExpr`.
5. **IL codegen** — Handle `ZDelegateType` in `IlTypeMapper` and `AsmResolverTypeMapper`.
6. **CLR interop** — Map CLR delegate types to `ZDelegateType` in `MapClrTypeToZType`.
7. **Tests** — Unit tests for each stage, integration test with a CLR delegate.
8. **Update KNOWN_GAPS.md** — Remove the bridge requirement entry.

## Future Enhancements

These are documented as out-of-scope for this implementation:

1. **`(delegate ...)` as a value form** — A future extension could allow `(delegate MyDelegate (fn [x]) body)` to construct a delegate instance directly, rather than requiring a type annotation on an existing expression. This would make the form more ergonomic.

2. **Delegate type aliases via `define-type-alias`** — Users could register delegate type aliases (e.g., `(define-type-alias RequestDelegate Microsoft.AspNetCore.Http.RequestDelegate)`) and then use the alias in type annotations without the fully-qualified name. This would reduce verbosity and make `(delegate ...)` less necessary for common cases.
