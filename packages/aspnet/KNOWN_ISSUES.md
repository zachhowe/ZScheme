# Known issues

Compiler / tooling bugs and quirks discovered while building the `zscheme-aspnet`
package. Unlike `KNOWN_GAPS.md` (missing package *surface*, fixed by adding bindings),
these are defects in the surrounding compiler/CLI that the package has to work *around*.
Remove entries as the underlying issues are fixed upstream.

## `build --manifest` can't consume prefixed local-package deps from source

**Affects:** building any standalone consumer package (e.g. `examples/aspnet-hello`) that
depends on this package via a `:local` zscheme dependency.

**Symptom:** `dotnet run --project src/ZScheme.Cli -- build -m <consumer manifest>` reports
`Module not found: 'aspnet/app'` (and siblings), even though the dependency path resolves.
`PackageBuilder` adds the dependency's directory as a bare module search path, but unlike
`PackageTester` (`test -m`) it does not read the dependency's manifest to learn its
`import-prefix` and source dir, so prefixed modules (`aspnet/...`) are never found. Adding
`--package-path` then surfaces a second problem: the dependency's source is *recompiled*
without its framework references, so `app.zs` fails with
`CLR assembly not found for ':from' hint: 'Microsoft.Extensions.Hosting.Abstractions'` and
follow-on `WebApplication vs IDisposable` type mismatches.

**Workaround:** pass the dependency package paths *and* the ASP.NET Core framework runtime
directory explicitly:

```
dotnet run --no-build --project src/ZScheme.Cli -- \
  build --manifest packages/aspnet/examples/aspnet-hello/package.zspkg \
  --package-path packages/aspnet --package-path packages/stdlib \
  --ref ~/.dotnet/shared/Microsoft.AspNetCore.App/<version>
```

(The package's own tests are unaffected — `test -m` resolves local deps prefix-aware and
applies the manifest's framework references.)
