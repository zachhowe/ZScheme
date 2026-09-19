# 0.5.0 (unreleased)

In development since 2026-08-13.

## Changed — packages

- **A package references its dependencies instead of compiling them into itself.** Building a
  package used to recompile every dependency's sources into its own assembly, on both backends:
  `zscheme-aspnet.dll` carried its own copies of fourteen stdlib modules plus di-abstractions,
  logging-abstractions and logging, and `zscheme-http.dll` was 86% dependency code. Each copy was
  a distinct set of CLR types, so an `Option` produced by one package could not be passed to a
  function in another, and each package's metadata advertised its dependencies' modules as its
  own — loading `zscheme-http` and `zscheme-stdlib` together bound `stdlib/option` to whichever
  arrived first. Now every dependency in the closure is resolved to a built artifact and
  referenced. `zscheme-aspnet.dll` drops from 18.4 KB to 10.7 KB and `zscheme-http.dll` from
  38.4 KB to 17.4 KB, both holding only their own modules.
  - `zs build`, `zs install`, `zs test` and the auto-installer all resolve through
    `PackageDependencyWiring`, because they have to agree: one of them referencing stdlib while
    another compiled it in would leave the two disagreeing about which assembly declares `Option`.
    The language server keeps compiling dependency sources — it answers go-to-definition into
    them.
  - An artifact is reused when the package's own sources hash to its recorded `inputFingerprint`
    *and* every dependency it was built against still offers the same version and fingerprint.
    Own-hash alone would call an artifact fresh after stdlib changed, was rebuilt, and became
    current again — while it was still compiled against the previous signatures. Content hashes,
    not timestamps: the cache entry is keyed by package version, and a `git checkout` rewrites
    mtimes on files whose bytes never changed.
  - The `.metadata.json` sidecar gained an optional `dependencies` array and `inputFingerprint`,
    and stops serializing modules whose code lives elsewhere. Both additions are additive and the
    format version stays at 2 — a bump would silently invalidate every cached artifact, including
    the `pkgcache` inside a published toolchain.
  - A cached package assembly is therefore no longer self-contained: `zscheme-http.dll` needs
    `zscheme-stdlib.dll` beside it, and its metadata names what it needs.

- **`generate-project` emits each dependency package as its own project.** A transpiled solution
  used to compile every dependency's sources into the consuming project — aspnet's project held
  `stdlib/option.cs` and five other dependency modules beside its own, and its test project
  inlined stdlib, zunit and http into a 2052-line `test-support.cs`. Now the solution has one
  project per package under a `deps/` folder, wired with `<ProjectReference>`, each in its own
  namespace: aspnet's solution has eight projects, `ZScheme.AspNet/` holds only aspnet's modules,
  `test-support.cs` is 119 lines, and there is exactly one `Option<T>` — in `ZScheme.StdLib`,
  spelled `ZScheme.StdLib.Stdlib_OptionModule.Option<T>` by everything that uses it. Projects
  rather than references to cached `.dll`s, so the tree stays buildable from ZScheme source with
  nothing but `csc`.

## Changed — tooling

- **`run-package-tests.ps1` takes its order from `Get-ZsPackages`.** The sequence of test steps
  and the reinstall between each was written out by hand, and had drifted: `zunit` was never
  installed, and the install before the aspnet tests installed aspnet rather than http. Those
  installs also discarded their output and never checked an exit code, so a dependency that
  failed to install surfaced only as an unexplained downstream test failure. Ordering matters
  more now that a dependency is referenced rather than compiled in — what `zs test` binds against
  is the artifact in the cache.

- **`generate-project` writes the main project as one `.cs` per module.** It already split the
  test project that way, one file per test source; the production half was a single file holding
  every module class — 84 KB for stdlib, and every dependency compiled from source landed in it
  too. Both halves now mirror the tree they came from: the package's own `import-prefix` is
  stripped, so `stdlib/mutable/vector` is written to `ZScheme.StdLib/mutable/vector.cs`, while a
  dependency inlined from source keeps its prefix as a folder (`stdlib/list.cs` inside the http
  project).
  - The split is a slice of one emission, not one emission per module. `CSharpEmitter.EmitUnits`
    returns the shared file header plus one unit per class from a single pass, and `Emit` is that
    same result concatenated — byte for byte, which is what keeps `zs build --backend csharp` and
    `zs compile` unchanged. It has to work this way: the emitter carries state across modules,
    including the counter behind `__match{n}` local names and the emitted-class table a later
    module's base class resolves through, so separate emitters would produce different code.
  - Every generated csproj now names its sources as explicit `<Compile>` items with the SDK's
    default `**/*.cs` glob switched off — `generate-project`, `zs compile --emit-project`, and
    the companion csproj `zs compile`/`zs build` write next to a single `.cs` alike. A stray
    `.cs` in the output directory — a module's file from before it was renamed, a per-module
    tree left where `--emit-project` now writes one file, a hand-written source — is no longer
    compiled into a duplicate definition. The manifest-less `generate-project`, which writes a
    csproj for sources the user adds by hand, keeps the glob.
  - `generate-project` also prunes the generated `.cs` files under its two project directories
    before writing, so a renamed module's old file does not linger in the tree as if it were
    part of the project. Only files whose first line carries the
    `// <auto-generated by ZScheme compiler` marker are removed, so a hand-written source in
    the output directory survives; `bin/` and `obj/` are left alone, and a symlink or junction
    inside the output directory is not followed. `zs compile --emit-project` does not prune:
    it owns only the one file it overwrites, and `-o` can point at a directory holding other
    compiles' output.
## Changed — language
- **`export` is spelled `provide`, and type declarations dropped their `define-` prefix.**
  Both moves follow Racket: it spells the module-export form `provide`, and it treats
  `define-struct` as the legacy spelling of `struct`. So `(export foo)` is now
  `(provide foo)`, and `define-record`, `define-struct`, `define-union`, `define-class` and
  `define-interface` are now `record`, `struct`, `union`, `class` and `interface`. This
  reverses the prefixing done in 0.2 — grouping the type declarations with the
  `define`/`define-async` family read tidily in a list of special forms, but at a declaration
  site the prefix is six characters of ceremony in front of the word that carries the meaning.
  - `define`, `define-async`, `define-syntax` and `define-type-alias` are **unchanged** — they
    declare values, macros and type *names*, not new types.
  - `struct` and `class` remain generic-constraint keywords too (`(^a struct)`). A constraint
    only ever appears inside a `: where` clause, never in head position, so the two uses never
    collide — the same way `new` has always been both a constraint and a special form.
  - The old heads still build, and report the new `ZS0007` warning naming the replacement.
    Normalization happens once, in the AST builder, so module resolution, inference, IR
    lowering and both backends only ever see the modern head — a program using the old heads
    emits byte-identical C# to the same program using the new ones. Disable the warning with
    `--no-warn-deprecated-keyword` or the manifest's
    `(build (main (warn-deprecated-keyword "false")))`; the CLI flag wins. The language server
    offers a quick fix that rewrites the head in place, and marks the head deprecated so
    editors strike it through.
  - The bundled packages and examples still use the old heads and so report `ZS0007` when
    built from source. They keep working; nothing needs to be rewritten to upgrade.

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
- **Type names and case patterns no longer depend on capitalization.** The compiler now uses
  the declaration and pattern context to distinguish type names from constructors, so a type
  can be named with any casing without changing how its cases are resolved. Short CLR type
  names and imported type metadata follow the same rule.

- **Inheritance is resolved transitively.** Derived-class constructors now initialize every
  inherited field, including fields several levels up the hierarchy, and both the C# and IL
  backends implement all methods inherited through an interface chain. Interface and class
  metadata also preserves the complete transitive inheritance information for downstream
  modules.


## Changed — editor support

- **Each ZScheme file extension now has its own syntax definition.** `.zs`, `*.zspkg` and
  `.zsfmt` are three different languages that merely share a lexer, but every editor
  integration had been treating `.zs` and `.zspkg` as one language and `.zsfmt` as nothing at
  all. Each now gets its own language id, scope and grammar in Visual Studio Code, Sublime
  Text and Zed:

  | Extension | Language id | Scope |
  | --- | --- | --- |
  | `.zs` | `zscheme` | `source.zscheme` |
  | `*.zspkg` | `zscheme-package` | `source.zspkg` |
  | `.zsfmt` | `zscheme-fmt` | `source.zsfmt` |

  The manifest and formatter-config grammars are standalone rather than layered over the
  `.zs` one, so `define` and `lambda` no longer highlight as keywords inside a manifest, and
  each highlights its own real vocabulary — `import-prefix`, `sources`, `:local`, `framework`
  entries and dependency names for manifests; settings, both boolean spellings, the
  `space`/`tab` enum and the `-name` removal marker for `.zsfmt`. Unrecognised keys are left
  unhighlighted rather than dressed up as keywords.

  `.zspkg` keeps full language-server support — the client document selectors were widened to
  match, since the server dispatches manifests on the file suffix. `.zsfmt` is deliberately
  not attached to the server, which has no handler for it.

- **Manifest keywords no longer leak into `.zs` files.** `name`, `version`, `build`, `test`,
  `main`, `ref`, `output` and friends were highlighted as keywords in ordinary source; they
  now render as the ordinary function calls they are. Zed was the worst affected, keywordising
  the very common identifiers `main` and `test`.

- **The three `.zs` grammars were reconciled against the compiler**, having drifted apart:
  `use`, `use*`, `quote` and `super/` were missing everywhere; the `import-clr` qualifiers
  `:instance-property-set`, `:instance-property-init`, `:instance-indexer-set` and `:from`
  were unknown to all three; Sublime lacked `null` and the `#:open`/`#:mutable`/`#:init`
  flags; and Zed listed `and`/`or`/`not` as special forms when they are stdlib macros. The
  built-in type list gained `Long`, `Double`, `Byte`, `Char`, `Symbol`, `Hash`, `TreeList` and
  `Mutable-TreeList`, and lost `Map` — a type that does not exist. Generic type constructors
  are written in head position, `(List Int)`, so they are now recognised there instead of
  being claimed as function calls, and user-defined types are scoped apart from built-ins.

- **Zed's tree-sitter grammar mis-lexed the longer `import-clr` qualifiers**:
  `:instance-property-set` matched the `clr_qualifier` token only as far as
  `:instance-property` and left `-set` behind as a separate symbol. The token now matches the
  `-set`/`-init` variants and `:from` whole, with a corpus test pinning it.

## Removed

- **The JetBrains (IntelliJ/Rider) plugin is retired.** It was a JFlex lexer and a hand-written PSI
  layer that had to be kept in step with the compiler by hand, and it lagged. The supported editor
  integrations are now Visual Studio Code, Sublime Text, and Zed — all three of which drive their
  language features from `zs-lsp` rather than a second, divergent implementation of the lexer.
