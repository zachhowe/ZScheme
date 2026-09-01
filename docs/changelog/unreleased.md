# 0.5.0 (unreleased)

In development since 2026-08-13.

## Changed — language

- **Member accessors are spelled `Type-member`, not `Type/member`.** Field access read as a
  namespace qualification rather than a field selection, and it did not match Racket, where a
  struct accessor is a plain hyphenated identifier. `/` was also the most overloaded character
  in the language — module path (`stdlib/option`), CLR member path
  (`System.Console/WriteLine`), base call (`super/Speak`), tuple index (`value/0`) — and field
  access was the one use carrying the least distinct meaning. So `(HttpResponse/status-code r)`
  is now `(HttpResponse-status-code r)`.
  - Applies to every type-derived binding: record and struct fields, class fields (own and
    inherited), class methods, and interface methods. The type name keeps the exact spelling it
    was declared with — no case transformation. Every other `/` convention is unchanged.
  - The old spelling still resolves for now, and reports the new `ZS0006` warning naming its
    replacement. Resolution happens once, in the type inferer, which rewrites the name onto the
    node so IR lowering and both backends only ever see the modern spelling — a program using
    the old syntax emits byte-identical C# to the same program using the new one. Disable the
    warning with `--no-warn-deprecated-accessor-syntax` or the manifest's
    `(build (main (warn-deprecated-accessor-syntax "false")))`; the CLI flag wins. The language
    server offers a quick fix that rewrites the name in place.
  - The fallback only fires for a genuine accessor — a function whose first parameter is the
    type that names it — so an undefined `foo/bar` still reports `ZS0001` rather than being
    silently redirected to an unrelated `foo-bar`.
  - One consequence of hyphen-joining: two declarations can now mint the same accessor, since
    `(define-record Foo-bar [baz])` and `(define-record Foo [bar-baz])` both produce
    `Foo-bar-baz`. The later declaration wins. Slash-joining could not collide this way.
  - Internally the member name is no longer recovered by splitting the accessor string. A type
    name never contains `/` but very much can contain `-` (a struct named `s-v` yields
    `s-v-a`), so `IrLowering` now carries the member name alongside the accessor name instead
    of splitting at the first separator.
