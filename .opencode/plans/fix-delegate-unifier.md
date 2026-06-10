# Plan: Fix ZDelegateType Unification in Unifier

## Problem

`delegate-example.zs` fails to build with errors like:
```
Error: Type mismatch: '(delegate System.Action)' vs '(-> ^a)'
Error: Type mismatch: '(delegate System.Func<int,int>)' vs '(Int -> ^a)'
```

The `ZDelegateType` record exists in ZType.cs and is parsed correctly by AstBuilder, but the `Unifier.UnifyInner` method has no case to handle unification between `ZDelegateType` and `ZFuncType`. When a lambda (which has type `ZFuncType`) is passed to a parameter annotated with `(delegate System.Action)`, the unifier falls through to the error case.

## Root Cause

In `Unifier.UnifyInner` (src/ZScheme.Compiler/Types/Unifier.cs), there is no handling for:
- `ZDelegateType` vs `ZFuncType` unification (bidirectional)
- `ZDelegateType` vs `ZDelegateType` unification
- `ZDelegateType` in `OccursIn`

## Fix

### 1. Unifier.cs — Add ZDelegateType handling in `UnifyInner`

Add three new cases after the `ZNullableType` handling block (before the Object boxing cases):

**Case A: ZDelegateType ↔ ZFuncType (bidirectional)**
When one side is `ZDelegateType` and the other is `ZFuncType`, they unify successfully. A ZScheme lambda (function type) can be passed where a specific CLR delegate type is expected — the CLR will handle the delegate construction at the call site.

```csharp
// ZDelegateType ↔ ZFuncType: delegate types are function types at runtime.
if (ta is ZType.ZDelegateType dt && tb is ZType.ZFuncType ft)
    return true;
if (ta is ZType.ZFuncType ft2 && tb is ZType.ZDelegateType dt2)
    return true;
```

**Case B: ZDelegateType ↔ ZDelegateType**
Two `ZDelegateType` values unify if they have the same CLR type name, or if the CLR types are assignable to each other (for delegate subtype relationships).

```csharp
if (ta is ZType.ZDelegateType dta && tb is ZType.ZDelegateType dtb)
{
    if (dta.ClrTypeName == dtb.ClrTypeName)
        return true;
    // Try CLR subtype check for delegate types
    // ... reflection-based assignability check ...
    // fall through to error if not assignable
}
```

### 2. Unifier.cs — Add ZDelegateType to `OccursIn`

Add a case for `ZDelegateType` in the `OccursIn` switch expression (line ~297):

```csharp
case ZType.ZDelegateType:
    return false;  // ZDelegateType has no recursive type structure
```

### 3. UnifierTests.cs — Add unit tests

Add these tests to `tests/ZScheme.Compiler.Tests/Types/UnifierTests.cs`:

- `UnifyDelegateTypeWithFuncType_Succeeds` — `(delegate System.Action)` unifies with `(-> Unit)`
- `UnifyFuncTypeWithDelegateType_Succeeds` — `(-> Unit)` unifies with `(delegate System.Action)` (bidirectional)
- `UnifyDelegateTypeWithFuncType_DifferentArity_Succeeds` — delegates accept any function signature
- `UnifyTwoDelegateType_SameName_Succeeds` — same CLR delegate type name unifies
- `UnifyTwoDelegateType_DifferentName_Fails` — different delegate type names produce error
- `UnifyTypeVarWithDelegateType_Succeeds` — type var bound to delegate type
- `UnifyDelegateTypeWithTypeVar_Succeeds` — delegate type on left, type var on right

### 4. Verify

Build `delegate-example.zs` and confirm it compiles without errors.

## Files to Modify

| File | Change |
|------|--------|
| `src/ZScheme.Compiler/Types/Unifier.cs` | Add ZDelegateType cases in `UnifyInner` and `OccursIn` |
| `tests/ZScheme.Compiler.Tests/Types/UnifierTests.cs` | Add unit tests for delegate unification |
