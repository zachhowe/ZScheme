# Known issues

Compiler / tooling bugs and quirks discovered while building the `zscheme-aspnet`
package. Unlike `KNOWN_GAPS.md` (missing package *surface*, fixed by adding bindings),
these are defects in the surrounding compiler/CLI that the package has to work *around*.
Remove entries as the underlying issues are fixed upstream.

## `build --backend il` produces a stack-imbalanced `Main` for the aspnet-hello example

**Affects:** building `examples/aspnet-hello` with the IL backend
(`build -m … --backend il`). The C# backend (the default) is unaffected.

**Symptom:** IL emission aborts with
`AsmResolver.DotNet.Code.Cil.StackImbalanceException: Stack imbalance was detected at
offset IL_0005 in method body of System.Int32 AspNetHello.MainModule::Main(System.String[])`.
The failure is at the *emit* stage — module/type/framework resolution all succeed — and
is deterministic. The synthesized `Main` wrapper (which returns the process exit code as
`Int32`) is emitted with an unbalanced stack for this entry; the trigger has not yet been
isolated (the entry's `main` returns `Unit` and ends in a blocking `app/run` call). Plain
examples with a `Unit`-returning `main` emit fine under the IL backend, so it is specific
to this program shape rather than the backend in general.

**Workaround:** build with the C# backend (the default — omit `--backend il`):

```
dotnet run --no-build --project src/ZScheme.Cli -- \
  build --manifest packages/aspnet/examples/aspnet-hello/package.zspkg
```

## `build --manifest` does not resolve transitive (deps-of-deps) zscheme prefixes

**Affects:** building a consumer package that relies on a *transitive* zscheme
dependency's prefixed modules — e.g. depending only on `aspnet` and expecting `stdlib/...`
imports to resolve because `aspnet` itself depends on `stdlib`.

**Symptom:** `Module not found: 'stdlib/...'`. `PackageBuilder` (like `PackageTester`)
reads only the consumer's *direct* dependency manifests for prefix/source-dir/framework
info; it does not recurse into a dependency's own zscheme dependencies. So a dep-of-a-dep's
import prefix is never registered on the module search path.

**Workaround:** declare the transitive zscheme package as a *direct* dependency in the
consumer manifest. `examples/aspnet-hello` already lists both `aspnet` and `stdlib` in its
`(dependencies (zscheme …))` for this reason.
