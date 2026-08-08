# A CLR primitive's full name is a different type from its ZScheme keyword

## Symptom

Writing a primitive's CLR name in a type annotation is a type error, even
though the two spellings denote the same .NET type:

```
Type mismatch: 'System.String' vs 'String'
Type mismatch: 'System.Int32'  vs 'Int'
Type mismatch: 'System.Int64'  vs 'Long'
Type mismatch: 'System.Boolean' vs 'Bool'
Type mismatch: 'System.Double' vs 'Double'
Type mismatch: 'System.Char'   vs 'Char'
```

Minimal repro — no `import-clr` needed, and adding one changes nothing:

```scheme
(module p)
(define (f [s : System.String]) : String s)   ;; Type mismatch: 'System.String' vs 'String'
```

`System.Object` is the sole exception and works, because `Unifier` has an
explicit boxing arm for it (`Unifier.cs:91`, `:98`, `:191`, `:198`).

The practical effect is that `System.String`, `System.Int32` and friends are
unusable as annotations anywhere: an interop signature that spells one of
them out cannot be written that way, so `import-clr` signatures have to use
the ZScheme keyword even when every neighbouring type in the same signature
is fully qualified.

## Root cause

`AstBuilder.ParseTypeExpr` maps ten bare atoms to a **`ZPrimitiveType`**
rather than a named type (`AstBuilder.cs:3014-3027`):

```csharp
SExpr.Atom a => a.Text switch
{
    "Int" => ZType.Int,          // ZPrimitiveType(PrimitiveKind.Int)
    "String" => ZType.String,    // ZPrimitiveType(PrimitiveKind.String)
    …
    _ => new ZType.ZNamedType(a.Text, []),
},
```

`System.String` misses every case and falls through to
`ZNamedType("System.String")`. `Unifier.UnifyInner` has **no**
`ZPrimitiveType` ↔ `ZNamedType` arm — only the `Object` special case above —
so the two never unify.

`TypeNameCanonicalizer` (added in b7ac726) cannot help here. It reconciles
short and qualified spellings by rewriting a `ZNamedType`'s `Name`, but the
short spelling of a primitive is not a `ZNamedType` at all, so there is
nothing on that side to canonicalize toward. The mismatch is a
*representation* split, one layer below the naming split that commit fixed.

## Suggested fix

Extend the atom switch in `ParseTypeExpr` so each primitive's CLR full name
maps to the same `ZPrimitiveType` its keyword does:

```csharp
"String" or "System.String" => ZType.String,
"Int"    or "System.Int32"  => ZType.Int,
"Long"   or "System.Int64"  => ZType.Long,
"Float"  or "System.Single" => ZType.Float,
"Double" or "System.Double" => ZType.Double,
"Byte"   or "System.Byte"   => ZType.Byte,
"Char"   or "System.Char"   => ZType.Char,
"Bool"   or "System.Boolean" => ZType.Bool,
"Unit"   or "System.Void"   => ZType.Unit,
```

This puts both spellings on one representation at parse time — the same
strategy `TypeNameCanonicalizer` uses for named types — so nothing
downstream needs to know. It also covers the reverse direction for free
(`Unit`/`System.Void` is worth checking separately; `Symbol` has no CLR
counterpart to alias).

A `ZPrimitiveType`↔`ZNamedType` bridge in `Unifier` would also work but is
strictly worse: it leaves two representations alive through IR lowering and
both emitters, which is what made the pre-b7ac726 per-site fallbacks
disagree with each other.

## Follow-up once fixed

`Analysis/RedundantTypeQualifierAnalyzer.cs` carries a `PrimitiveNames`
exclusion set solely because of this bug — ZS0004 must not offer to rewrite
`System.String` to `String` while that rewrite changes the annotation's
`ZType`. Delete the set (and the `PrimitiveShortName_IsNotReported` test)
when the spellings unify; the suggestion becomes correct at that point.
