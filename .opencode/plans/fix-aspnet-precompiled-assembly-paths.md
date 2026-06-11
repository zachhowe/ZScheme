# Fix: aspnet test failures - thread-sleep not found for AsmResolver IL emission

## Root Cause Analysis

### Issue 1 (Fixed): Precompiled assembly paths not collected
In `Compilation.CompileLoadModules`, stdlib modules loaded from the package cache were only added to `compiledModules` if they were prelude modules. This was fixed by adding all modules with `PrecompiledAssemblyPath` to `compiledModules`.

### Issue 2 (Root cause of remaining failures): `stdlib/thread` has 0 CLR imports
When `stdlib/thread` is compiled from source (not from package cache), its `import-clr` directive for `thread-sleep` is NOT being included in `ExportedClrImports`.

Evidence:
- Log shows: `Module stdlib/thread: compiled in 0ms (1 exports, 1 types, 0 CLR imports, 0 macros)`
- When compiled directly, `thread.zs` works because cached stdlib includes `thread-sleep`
- When compiled from source as part of aspnet tests, `stdlib/thread` has `0 CLR imports`

The `ExportedClrImports` is built from `lowering.ClrImports` filtered by `exportedNames`. Since `stdlib/thread` has no transitive imports with CLR imports, the only source is the current module's `import-clr` directive, processed by `LowerImportClr`.

The fact that `ExportedClrImports` is empty means `thread-sleep` is not in `lowering.ClrImports`, which means `LowerImportClr` is not registering it.

### Investigation findings:
1. Parser outputs 4 s-expressions for `thread.zs` (correct)
2. AST has 1 top-level form (module declaration with absorbed body)
3. `stdlib/thread` is compiled 8 times (once per test file dependency)
4. Each compilation shows `0 CLR imports`
5. `LowerImportClr` should process the `import-clr` directive and register `thread-sleep` in `_clrImports`
6. But `ExportedClrImports` is empty, meaning `LowerImportClr` is not working as expected

## Current Status
- Fix 1 applied (precompiled assembly paths)
- Fix 2 needed: `stdlib/thread` needs to properly register its `import-clr` in `ExportedClrImports`

## Next Steps
1. Add debug logging to `LowerImportClr` to verify it's being called
2. Check if the `ImportClr` node is in the AST for `stdlib/thread`
3. Verify the qualified name parsing in `LowerImportClr`
