# ASP.NET Test Failures - Investigation Findings

## Summary

The ASP.NET integration tests were failing with errors like:
- `Function 'thread-sleep' not found for AsmResolver IL emission`
- `Variable 'test-support/json-handler' not found for AsmResolver IL emission`

## Root Cause Analysis

### Issue 1: Precompiled Assembly Paths Not Collected (FIXED)

**Location:** `src/ZScheme.Compiler/Pipeline/Compilation.cs`, `CompileLoadModules` method

**Problem:** When stdlib modules were loaded from the package cache, they were only added to `compiledModules` if they were prelude modules. For non-prelude compilations (like the aspnet package), stdlib modules were added to `_moduleCache` but NOT to `compiledModules`.

This meant:
1. `GetPrecompiledAssemblyPaths()` reads from `_moduleCache` - returns stdlib paths
2. BUT `CompileEmit` computes `precompiledAssemblyPaths` from `compiledModules.Where(m => m.PrecompiledAssemblyPath is not null)` - returns **empty** because stdlib isn't in `compiledModules`
3. The IL emitter is constructed with `precompiled=0` assemblies
4. Functions from precompiled modules (like `thread-sleep`) can't be found in `_precompiledMethods`

**Fix:** Modified `CompileLoadModules` to always add modules with `PrecompiledAssemblyPath` to `compiledModules`, regardless of whether they're prelude modules. This ensures their assembly paths are collected for the IL emitter.

### Issue 2: `import-clr` Directive Not Processed (FIXED)

**Location:** `src/ZScheme.Compiler/Ir/IrLowering.cs`, `LowerImportClr` method

**Root Cause:** The `import-clr` syntax in `stdlib/thread.zs` uses dot-separated CLR type names (`System.Threading.Thread.Sleep`), but the code expected slash-separated names (`System.Threading/Thread/Sleep`). The `LastIndexOf('/')` returned -1, causing the import to be silently skipped.

**Fix:** Modified `LowerImportClr` to handle BOTH `/` and `.` as separators when splitting the qualified name into type name and method name:
```csharp
var lastSlash = import.QualifiedName.LastIndexOf('/');
var lastDot = import.QualifiedName.LastIndexOf('.');
var splitIndex = lastSlash >= 0 ? Math.Max(lastSlash, lastDot) : lastDot;
```

### Issue 3: Cross-Module Function Lookup (FIXED)

**Location:** `src/ZScheme.Compiler/Codegen/IlEmitter.Emit.cs`, `EmitCall` method

**Problem:** When a function from another module was called (e.g., `http/get`), the variable name included the module prefix. The sanitized name (`Http_Get`) didn't match the registered key (`Get` or `HttpModule.Get`).

**Fix:** Modified `EmitCall` to use the full variable name for sanitization, preserving the module prefix in the key lookup.

### Issue 4: Main Module Function Values (FIXED)

**Location:** `src/ZScheme.Compiler/Codegen/IlEmitter.Emit.cs`, `EmitLoadVar` method

**Problem:** When a function from the main module was used as a value (e.g., `test-support/json-handler` passed as an argument), the emitter couldn't find it because functions are stored in `_methods`, not `_staticFields`.

**Fix:** Added a fallback lookup in `_methods` when the variable is not found in `_staticFields`, emitting `ldftn` to load the method pointer.

### Issue 5: Modules with Internal Helpers Filtered Out (FIXED)

**Location:** `src/ZScheme.Compiler/Pipeline/Compilation.cs`, `CompileEmit` method

**Problem:** Modules with internal helper functions (not exported) were filtered out of `sourceImportedModules` because `ExportedIrDefinitions` was empty. This caused internal helper functions to be unavailable.

**Fix:** Modified the filter to include modules that have `AllIrDefinitions` with content, even if `ExportedIrDefinitions` is empty.

## Current Status

- **stdlib tests:** 283 passed, 0 failed (ALL PASSING)
- **http tests:** 3 passed, 0 failed (ALL PASSING)
- **aspnet bridge build:** success (working)
- **aspnet tests:** 7 failed (remaining issue: `define-async` in `let` bodies)

## Remaining Issues

### Issue 6: `define-async` Inside `let` Bodies Not Creating Variable Bindings (OPEN)

**Location:** `src/ZScheme.Compiler/Ir/IrLowering.cs`, `LowerDefineAsync` method

**Problem:** When `define-async` appears inside a `let` body (as in the aspnet test files), it creates a `FuncDef` IR node. However, `BuildLet` wraps multiple body expressions into nested `Let` bindings, so the `FuncDef` ends up as the VALUE of a `Let` node rather than a direct child of a `Seq`.

When `EmitLet` processes `Let("_", FuncDef(...), body)`, it calls `EmitNode(FuncDef)` which dispatches to `EmitLambda`. `EmitLambda` creates a method with a generated name (e.g., `__lambda_0_protected-handler`) instead of the original name (`protected-handler`). This means the method is registered in `_methods` under the generated name, but when the code later tries to reference `protected-handler`, it looks for `_methods["ProtectedHandler"]` which doesn't exist.

**Attempted Fix:** Several approaches were tried:
1. Wrapping `FuncDef` in a `Let` at the lowering stage - caused issues with the static constructor
2. Handling `FuncDef` values specially in `EmitLet` - caused IL generation errors (stack imbalance, invalid labels) inside async state machines

The fundamental challenge is that pushing a delegate onto the stack inside an async state machine interferes with the async state machine's stack management, causing IL verification errors.

**Potential Solutions:**
1. Modify `EmitLambda` to register methods with their original names in addition to generated names
2. Create a separate code path for `define-async` that handles async functions differently than lambdas
3. Restructure the test files to avoid `define-async` inside `let` bodies (use top-level `define-async` instead)

## Files Changed

- `src/ZScheme.Compiler/Pipeline/Compilation.cs` - Fixed precompiled assembly paths collection, module filtering, and sourceImportedModules logging
- `src/ZScheme.Compiler/Ir/IrLowering.cs` - Fixed `LowerImportClr` to handle both `/` and `.` separators
- `src/ZScheme.Compiler/Codegen/IlEmitter.cs` - Added debug logging for function registration
- `src/ZScheme.Compiler/Codegen/IlEmitter.Emit.cs` - Fixed `EmitCall` for cross-module function lookup, added `EmitLoadVar` fallback for main module function values

## Debug Logs Added

The following debug logs were added to aid future troubleshooting:
- `LowerImportClr`: Processing imports and registration status
- `BuildImportClr`: Bracket item details
- `BuildModule`: Module body structure
- `RegisterFuncSignature`: Method registration keys
- `EmitCall`: Variable lookup details
- `CompileEmit`: Source imported modules list
