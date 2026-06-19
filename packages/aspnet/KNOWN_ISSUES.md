# Known issues

Compiler / tooling bugs and quirks discovered while building the `zscheme-aspnet`
package. Unlike `KNOWN_GAPS.md` (missing package *surface*, fixed by adding bindings),
these are defects in the surrounding compiler/CLI that the package has to work *around*.
Remove entries as the underlying issues are fixed upstream.

## A record-type annotation on a generic-return binding infers the constructor type

**Affects:** `services/get-required-service`, `services/get-service` (any generic CLR
import whose type parameter appears only in the return position — same shape as
`stdlib/json`'s `json/deserialize`).

**Symptom:** annotating the `let` binding that receives the resolved value with a record
type makes inference fail. This:

```scheme
(let [g : Greeter (services/get-required-service (request/services ctx))]
  (Greeter/prefix g))
```

fails to compile with:

```
Type mismatch: 'Greeter' vs '(String -> Greeter)'
```

The annotation `: Greeter` is resolving the value's expected type to `Greeter`'s
**constructor** type `(String -> Greeter)` instead of the record type `Greeter`, so the
generic `^a` is instantiated to the constructor and the later field access mismatches.

**Workaround:** omit the binding annotation and let the generic instantiation be inferred
from how the value is *used* (this is the pattern `json/deserialize` relies on):

```scheme
(let [g (services/get-required-service (request/services ctx))]
  (Greeter/prefix g))   ;; usage pins ^a = Greeter
```

`packages/aspnet/test/aspnet-di-tests.zs` and the `examples/aspnet-hello` handler both use
the annotation-free form for this reason.

**Likely root cause:** a record name is bound as both a type and a value (its
constructor). When a `let` type annotation names a record whose binding RHS is a generic
call, the annotation appears to be resolved through the value namespace (constructor
function type) rather than the type namespace (the record type). Reproducible outside this
package with any `^a`-return CLR import bound to an annotated `let`.

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
