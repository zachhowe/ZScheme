# ZS0004 declines on an import-clr member path whose short name is shadowed by a ZScheme type the file never imports

## Symptom

`packages/stdlib/src/mutable/treelist.zs` declares `System.Collections.Generic`
as a namespace hint and then spells all fourteen of its `List<T>` member paths
in full:

```scheme
(import-clr
  System.Collections.Generic
  System.Collections.Immutable
  System.Linq
  [ml-count-raw System.Collections.Generic.List.Count
    :instance-property : ((Mutable-TreeList ^a) -> Int)]
  [ml-item-raw System.Collections.Generic.List.Item
    :instance-indexer : ((Mutable-TreeList ^a) Int -> ^a)]
  …)
```

No ZS0004 hint is offered on any of them, so the greyed-out prefix and its
quick fix never appear.

The shortening is nevertheless valid. Rewriting all fourteen to `List.Count`,
`List.Item`, … compiles and runs clean — 300/300 `packages/stdlib` tests pass
with the short spelling, and every one of those bindings is called from within
the file, so they are genuinely exercised rather than merely accepted.

This is the case 93c3c11 ("Resolve import-clr member paths through the
namespace hints, and hint on them") set out to make hintable. The resolution
half landed; the hint still does not fire here.

## Root cause

The analyzer's decline is at `RedundantTypeQualifierAnalyzer.cs:113`:

```csharp
if (
    canonicalizer.CanonicalImportTypeName(shortName)
    != canonicalizer.CanonicalImportTypeName(typeName)
)
    continue;
```

`CanonicalImportTypeName("List")` returns `List` unchanged, because
`TypeNameCanonicalizer.IsCanonicalizable` refuses any name that
`_isUserDeclaredType` claims — and `List` is stdlib's
`(define-union (List ^a) …)` (`packages/stdlib/src/list.zs:9`).
`CanonicalImportTypeName("System.Collections.Generic.List")` resolves. The two
differ, so no hint.

That is `ImportClrMemberPathShadowedByAZSchemeType_IsNotReported`
(`RedundantTypeQualifierTests.cs:204`) firing — except that test declares the
shadowing record *in the same file*, where declining is right.
`mutable/treelist.zs` imports `stdlib/mutable/vector`, `stdlib/vector` and
`stdlib/option`. It never imports `stdlib/list`, so `List` is not in the
compiler's `_declaredTypeNames` for this module ("own declarations plus
imported ones", `TypeInferer.cs:23-26`) and the short spelling binds to
`System.Collections.Generic.List` exactly as intended.

So the comment justifying that equality test —

```
// The compiler splits and canonicalizes with the same helper, so the two
// cannot disagree.                (RedundantTypeQualifierAnalyzer.cs:112)
```

— does not hold. The helper is the same; the `isUserDeclaredType` set behind
it is not. The analyzer is reading a canonicalizer whose declared-type view is
wider than the one that compiles this module.

The sibling files make the pattern unmistakable. In the same directory, with
the same in-file `System.Collections.Generic` hint,
`packages/stdlib/src/mutable/hash.zs` had every `Dictionary.*` member path
hinted and shortened during the same sweep. `Dictionary` collides with no
ZScheme declaration; `List` does. Nothing else distinguishes the two files.

## Suggested fix

Decide which scope `_isUserDeclaredType` should describe for a member path,
and make the two agree:

- If it should be the module's own import closure (what the compiler uses),
  the analyzer is being handed the wrong canonicalizer and should get the
  per-module one. The shadowing carve-out then keeps working for the
  same-file case the test pins, and starts firing here.
- If a workspace-wide view is deliberate — declining whenever *any* reachable
  module declares the simple name, on the grounds that a later `(import
  stdlib/list)` would silently change what `List.Count` means — then it is
  working as designed, and the gap is that the user is told nothing. A hint
  that says so, or a note in `docs/CLR-TYPE-MAPPING.md`, would beat silence.

The first reading looks right: the resolution the user would get from
shortening is the module's, not the workspace's, and it is the one the
compiler already commits to.

Either way this wants a regression test with the shadowing declaration in an
**unimported sibling module** rather than in the file under test — the current
suite has no case that separates the two scopes, which is why the gap survived
the commit that was meant to close it.

## Notes

- Verified by reverting the shortening and re-checking in-editor: with the
  fully-qualified paths restored, no hint appears.
- `ImportClrMemberPathOnTask_IsNotReported`
  (`RedundantTypeQualifierTests.cs:219`) is a *different* decline and stays
  correct: `Task` is in `NeverCanonicalized`, so neither spelling resolves and
  the short form genuinely does not compile. `packages/stdlib/src/task.zs`
  was migrated to `Task/CompletedTask` during the same sweep and does not
  resolve — `CLR type not found: 'Task'` at any call site. It escaped CI
  because `task-completed-task` has no caller anywhere in the repo. Reverted
  to the qualified path, with a comment.
- The equivalent hint for the `(new (System.Collections.Generic.Dictionary ^k ^v))`
  form in `mutable/hash.zs:47,93` also never fired, but that one goes through
  the `TypeNames` loop (`case "new"` in `TypeNameScanner`), not `ImportMembers`
  — worth confirming whether it is this same cause or a second gap.
