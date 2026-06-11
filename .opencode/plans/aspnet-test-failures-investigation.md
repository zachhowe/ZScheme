# ASP.NET Test Failures - Investigation Findings

## Summary

The ASP.NET integration tests are failing with errors like:
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

### Issue 2: `import-clr` Directive Not Processed (UNRESOLVED)

**Location:** AST building pipeline (`AstBuilder.BuildList`, `IrLowering.LowerImportClr`)

**Problem:** When `stdlib/thread` is compiled from source (not from package cache), its `import-clr` directive for `thread-sleep` is NOT being included in `ExportedClrImports`.

**Evidence:**
- Log shows: `Module stdlib/thread: compiled in 0ms (1 exports, 1 types, 0 CLR imports, 0 macros)`
- When compiled directly, `thread.zs` works because cached stdlib includes `thread-sleep`
- When compiled from source as part of aspnet tests, `stdlib/thread` has `0 CLR imports`

**Investigation findings:**
1. Parser outputs 4 s-expressions for `thread.zs` (correct)
2. AST has 1 top-level form (module declaration with absorbed body)
3. `stdlib/thread` is compiled 8 times (once per test file dependency)
4. Each compilation shows `0 CLR imports`
5. `BuildImportClr` is NOT being called
6. `LowerImportClr` is NOT being called

**Possible root causes:**
1. The `ImportClr` node is not in the module body
2. The `ImportClr` node is in the body but not being lowered
3. The `ImportClr` node is being lowered but `LowerImportClr` is not registering the import
4. There's a bug in how `ExportedClrImports` is built

**Key observation:** The `ExportedClrImports` is built from `lowering.ClrImports` filtered by `exportedNames`. Since `stdlib/thread` has no transitive imports with CLR imports, the only source is the current module's own `import-clr` directive, processed by `LowerImportClr`. The fact that `ExportedClrImports` is empty means `thread-sleep` is not in `lowering.ClrImports`, which means `LowerImportClr` is not registering it.

## Impact

- **stdlib tests:** 283 passed (working)
- **http tests:** 3 passed (working)
- **aspnet bridge build:** success (working)
- **aspnet tests:** 8 failed (all failing with compilation errors)

## Files Changed

- `src/ZScheme.Compiler/Pipeline/Compilation.cs` - Fixed precompiled assembly paths collection

## Next Steps

1. Add comprehensive logging to the AST building pipeline to trace the exact path the `import-clr` directive takes
2. Verify the S-expression parser is outputting the correct structure for `import-clr` forms
3. Check if the `import-clr` directive is being processed during a different phase (e.g., pre-parse, macro expansion)
4. Consider adding a fallback mechanism to register CLR imports from transitive modules during IR lowering
