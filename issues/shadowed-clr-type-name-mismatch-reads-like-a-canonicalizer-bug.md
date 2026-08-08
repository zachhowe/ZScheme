# A ZScheme type shadowing a CLR simple name produces a mismatch that reads like a canonicalizer bug

## Symptom

With `(import-clr System.Collections.Generic)` in scope:

```scheme
(module p)
(import-clr System.Collections.Generic)
(define (f [xs : (List Int)]) : (System.Collections.Generic.List Int) xs)
```

```
Type mismatch: 'List<Int>' vs 'System.Collections.Generic.List<Int>'
```

Since b7ac726 ("Canonicalize CLR type names so short and qualified spellings
unify") the whole contract of `import-clr`'s namespace hints is that those
two spellings *are* the same type. So this message reads as
"canonicalization failed" — the exact failure mode that commit set out to
eliminate — when in fact the compiler is right and the user is wrong.

`List` here is stdlib's `(define-union (List ^a) …)` (`packages/stdlib/src/list.zs:9`),
in scope through the prelude. It genuinely is a different type from
`System.Collections.Generic.List`, and the annotation means the ZScheme one.
Nothing in the diagnostic says so.

The same trap applies to any CLR type whose simple name collides with a
ZScheme record/union/class/interface/alias — in the file, in an imported
module, or in the prelude. `List` is the one users will hit, because
`System.Collections.Generic` is a common import and `List` is stdlib's most
used type.

## Root cause

Not a bug in the resolution — that part is deliberate and correct.
`TypeNameCanonicalizer.IsCanonicalizable` refuses to canonicalize a name
that `_isUserDeclaredType` claims (`TypeNameCanonicalizer.cs:191`), so bare
`List` stays `ZNamedType("List")` and never gets bound to the CLR type of the
same simple name. Without that guard a namespace hint could silently rebind
a ZScheme `Point` to `System.Drawing.Point`.

The gap is purely in the *reporting*. `Unifier` renders both sides with
`ZType.Format` and stops. It has, at the mismatch site, everything needed to
recognise the case — two `ZNamedType`s, equal simple names, different full
names, one of them known to `_isUserDeclaredType` — and says none of it.

## Suggested fix

At the `ZNamedType`/`ZNamedType` mismatch arm in `Unifier.UnifyInner`
(`Unifier.cs:84`), when the two names' last dot-separated segments are equal
but the full names differ, extend the diagnostic. `Unifier` already takes a
`Func<string,string>? canonicalTypeName` (added in b7ac726) and can be given
the same `isUserDeclaredType` predicate, so the check is local:

```
Type mismatch: 'List<Int>' vs 'System.Collections.Generic.List<Int>'
  note: 'List' is a ZScheme type declared in stdlib/list, not
        'System.Collections.Generic.List' — a namespace hint never rebinds a
        name that a ZScheme declaration already owns. Write the CLR type in
        full, or alias it with (define-type-alias …).
```

A `DiagnosticRelatedInfo` pointing at the shadowing declaration would be
better still where the span is reachable (`RecordDecl`/`UnionDecl`/
`ClassDecl`/`InterfaceDecl` all carry a `NameSpan`); for a declaration that
arrives from a precompiled module only the module name is available.

## Notes

- The LSP's ZS0004 "redundant type qualifier" hint already declines to fire
  here, for the right reason: `Canonical("List", 1)` returns `List`
  unchanged while `Canonical("System.Collections.Generic.List", 1)` resolves,
  so the two are unequal and no suggestion is offered
  (`RedundantTypeQualifierTests.ClrTypeShadowedByAZSchemeOne_IsNotReported`).
  A user who *wanted* the CLR type gets no nudge either way — the mismatch
  message is the only place this can be explained.
- `System.Object`/`Object` and `System.Threading.Tasks.Task`/`Task` are
  unaffected: both are matched in either spelling by `Unifier` and
  `TypeAliasRegistry`, and both are in `TypeNameCanonicalizer.NeverCanonicalized`.
