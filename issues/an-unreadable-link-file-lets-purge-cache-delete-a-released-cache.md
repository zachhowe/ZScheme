# An unreadable `.link` file lets `--purge-cache` delete a released toolchain's cache

**Found by:** code review of the `version-selector` branch (the zsup toolchain
manager). Traced from source; no live repro was attempted.

**Affects:** `UninstallCommand.Run` (`src/ZScheme.Zsup/Commands/UninstallCommand.cs:48-54`),
reached by `zsup uninstall <name> --purge-cache`.

## Symptom

`zsup uninstall dev --purge-cache` removes a *linked* developer toolchain and, on
the way out, deletes `cache/pkg/dev` — which the link never wrote to. If a real
toolchain named `dev` was ever installed from a release, that is its compiled
package cache, and rebuilding it needs the SDK and the network.

The command reports nothing unusual. The next compile with that toolchain does a
from-source stdlib rebuild.

## Root cause

The linked-ness of the toolchain is read from the registry entry:

```csharp
var isLinked = existing?.IsLinked ?? false;
var compilerVersion = isLinked
    ? null
    : PackageCacheSeeder.ResolveCompilerVersion(
        existing?.Dir ?? ZSchemeHome.GetToolchainDir(name, home),
        name
    );
```

`ToolchainRegistry.TryGet` returns `null` — not a linked entry — when the link
file cannot be parsed (`ToolchainRegistry.cs:120-125`):

```csharp
var target = ReadLinkTarget(linkFile);
return target is null ? null : Linked(name, target);
```

`ReadLinkTarget` answers `null` for a link file that is empty, comment-only, or
unreadable. So for such a file:

- `existing` is `null`, therefore `isLinked` is **`false`**;
- `ResolveCompilerVersion` is called against `toolchains/<name>`, which does not
  exist for a link, so it falls through to its last line —
  `return FindCompilerVersion(toolchainDir) ?? installedAs;` — and yields **the
  name**;
- `registry.Remove(name)` still deletes the link file and returns success;
- the purge guard at `:98` sees a non-null `compilerVersion` and proceeds.

That guard's own comment states the exact outcome this bypasses:

> Only for a real installation. A link's name is not a compiler version, and
> `zsup link 0.4.0 ./build` is legal whenever toolchains/0.4.0 is free — treating
> it as one would delete cache/pkg/0.4.0, the released payload's cache, which the
> link never wrote to.

The `UsingCompilerVersion` check at `:103` does not save it either: it excludes
linked toolchains, so a cache shared only with the (now removed) link looks
unshared.

## Suggested fix direction

Decide linked-ness from the *file*, not from a successful parse — the link file
is what makes a toolchain a link, and its contents are what the command does not
need here:

```csharp
var isLinked =
    existing?.IsLinked ?? File.Exists(ZSchemeHome.GetToolchainLinkFile(name, home));
```

An unparseable link file then keeps `compilerVersion` null, and the purge is
skipped exactly as it is for a readable one. This also matches what `Remove`
already does: it deletes the link file on existence, without reading it.

Worth a test in `UninstallCommandTests` (or `ToolchainRegistryTests`, wherever the
link fixtures live): write a `dev.link` containing only a comment, seed
`cache/pkg/dev`, run the uninstall with `--purge-cache`, and assert the cache
survives.

## Priority note

Low frequency — it needs a corrupted or hand-edited `.link` file, which is not
something zsup produces. But the failure is silent, destroys expensive state, and
the collision it needs (`cache/pkg/<name>` existing for a link's name) is exactly
the one the surrounding comment says is legal and expected. The fix is one line.
