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
