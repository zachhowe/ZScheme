# Toolchain management (`zsup`)

`zsup` installs ZScheme toolchains side by side and decides which one the `zs` and `zs-lsp` on your
PATH resolve to. It works the same way on Linux, macOS, and Windows.

## Installing

Linux and macOS:

```sh
curl -fsSL https://raw.githubusercontent.com/zachhowe/ZScheme/master/install.sh | sh
```

Windows (PowerShell):

```powershell
irm https://raw.githubusercontent.com/zachhowe/ZScheme/master/install.ps1 | iex
```

Both scripts download `zsup`, verify it against the release's `SHA256SUMS`, install the latest
toolchain, and put `~/.zscheme/bin` on your PATH. Pass `--no-modify-path` / `-NoModifyPath` to skip
the PATH change, and `--version <X.Y.Z>` (or `ZSCHEME_INSTALL_VERSION`) to install something other
than the latest release; a leading `v` is accepted there, matching the release tags. That is
deliberately not `ZSCHEME_VERSION`, which selects the installed toolchain `zs` runs and is commonly
exported to a name like `dev`.

`zs` and `zs-lsp` are framework-dependent and need the [.NET 10 runtime](https://dotnet.microsoft.com/download).
`zsup` itself is a native binary with no such dependency, so it can tell you when the runtime is
missing. It warns rather than failing, so you can install the runtime afterwards.

## Commands

| Command | Purpose |
| --- | --- |
| `zsup install <version>` | Install a toolchain (`latest` resolves the newest release) |
| `zsup install <v> --from <archive\|dir>` | Install from a local archive or build directory |
| `zsup use <toolchain>` | Set the global default |
| `zsup use <toolchain> --local` | Pin the toolchain in `./.zscheme-version` |
| `zsup list [--verbose]` | List toolchains, marking the default and the active one |
| `zsup uninstall <toolchain> [--purge-cache]` | Remove a toolchain |
| `zsup link <name> <dir>` | Register a locally built tree as a toolchain |
| `zsup unlink <name>` | Remove a linked toolchain |
| `zsup which [zs\|zs-lsp]` | Print the resolved path (stdout) and where the choice came from (stderr) |
| `zsup self update` | Replace `zsup` with the latest release |
| `zsup self uninstall --yes` | Delete `~/.zscheme` |

`install` also takes `--force` (replace an existing installation, or a linked toolchain of the same
name) and `--no-default` (install without changing the default). Without `--force` it refuses both,
so a name never ends up with an installed directory and a link at once. The refusal happens as soon
as the version is known and before any asset is fetched, so `zsup install latest` on an up-to-date
installation costs one API call rather than a full release download.

A payload without both `zs` and `zs-lsp` is refused whatever the name: they ship together, and one
without the other installs cleanly and then fails from inside an editor, far from the install that
caused it. This is reachable in practice only through `--from`, pointed at a single project's build
output — see [Developing on the compiler itself](#developing-on-the-compiler-itself).

`link` refuses `~/.zscheme/bin`, and the home directory above it, because the `zs` there is the shim
itself: a toolchain pointing at it would make `zs` hand off to `zs` for as long as the machine held
out. The shim refuses the same handoff when it is asked to make it, and gives up after eight
handoffs in a row that never reach a compiler, so a link made by hand — or one pointing at a copy of
the bin directory somewhere else — ends in an error rather than a runaway.

## How a toolchain is selected

`~/.zscheme/bin/zs` and `zs-lsp` are the `zsup` binary under another name; it recognises how it was
invoked and hands off to the real executable. `zsup install` and `zsup self update` re-stamp both
names so they can never lag behind the `zsup` beside them. On Windows a name that is locked by a
process still running it cannot be replaced; the other names are stamped anyway, and each one that
was skipped is named in a warning telling you to close what is holding it and re-run the command
that stamped them — `zsup install <version> --force` after an install, or
`zsup self update <version>` after a self update, which names the version because a bare
`zsup self update` would find itself already current and stamp nothing. A skipped shim keeps
working — it is just the previous version.

Selection order, highest first:

1. **`ZSCHEME_VERSION`** — for one command or one shell.
2. **`.zscheme-version`** — the nearest one at or above the current directory, walking up to the
   filesystem root. One line holding a toolchain name; `#` comments and blank lines are skipped.
3. **The global default** — whatever `zsup use` last set, in `~/.zscheme/settings.json`.

```sh
echo 0.3.0 > .zscheme-version     # this project builds with 0.3.0
zsup use 0.4.0                    # everything else uses 0.4.0
ZSCHEME_VERSION=0.3.0 zs --version  # just this command
```

`zsup which` reports which rule applied, which is the quickest way to explain a surprising version.

Note that `package.zspkg` is deliberately *not* a pin source. The manifest parser rejects unknown
fields, so a `(toolchain ...)` entry would make the manifest unreadable by every already-released
compiler — the opposite of what a pin is for.

## Layout

```
~/.zscheme/                        # override with ZSCHEME_HOME
├── bin/                           # the only directory that goes on PATH
│   ├── zsup                       # the real binary
│   ├── zs                         # hardlink (Unix) / copy (Windows) of zsup
│   └── zs-lsp
├── toolchains/
│   ├── 0.4.0/
│   │   ├── bin/                   # zs, zs-lsp and their assemblies
│   │   ├── packages/              # stdlib sources
│   │   ├── pkgcache/<version>/    # prebuilt packages, seeded on install
│   │   └── toolchain.json
│   └── dev.link                   # a linked toolchain: one line, an absolute path
├── settings.json                  # the global default
├── downloads/                      # transient download and staging space
├── env, env.fish                  # PATH snippets sourced by your shell profile (Unix)
└── cache/
    ├── pkg/<version>/             # compiled packages, keyed by compiler version
    ├── git/
    └── nuget/
```

The package cache being keyed by compiler version is what lets toolchains coexist: two versions
never share compiled packages, so installing one cannot corrupt another.

Installs are atomic. Everything is unpacked into `downloads/.staging-*` and committed with a single
directory rename, so an interrupted install leaves nothing half-written.

## The standard library

Each toolchain ships both the `packages/` sources and a prebuilt `pkgcache/`, which `zsup install`
copies into `cache/pkg/<version>/`. That makes the first compile instant and entirely offline —
building stdlib from source would otherwise need a NuGet restore and the .NET SDK.

The prebuilt cache is stored as `pkgcache/<compiler version>/…`, and it is that version — not the
name the toolchain was installed under — that decides where it is seeded. The two differ whenever a
toolchain is installed under another name (`zsup install dev --from …`), and the compiler only ever
looks in `cache/pkg/<its own version>/`.

`ZSCHEME_CACHE_DIR` moves that directory, and seeding follows it: `zsup` resolves the destination
exactly the way a compile resolves its cache, so the two cannot end up pointing at different
directories. Seeding the home's copy for someone who has the variable exported would leave the first
compile building stdlib from source anyway, which is the whole thing this avoids.

The sources are still worth shipping: the cache rebuilds from them if it is ever cleared, and the
language server uses them for go-to-definition into stdlib.

Because the key is the compiler version, one `cache/pkg/<version>/` can back several installed
toolchains. `zsup uninstall --purge-cache` therefore keeps it when any of the others still reports
that version, and says so. A linked toolchain is exempt from that half entirely — it has no entry in
the shared cache to remove, and its *name* is not a version, so `zsup link 0.4.0 ./build` must not be
read as a claim on `cache/pkg/0.4.0/`. Only its own `cache-dev/<name>/`, which is per-name and can
never be shared, is removed.

## Developing on the compiler itself

```sh
pwsh ./build-dev-toolchain.ps1 -Use
```

That builds both `zs` and `zs-lsp` into `dist/toolchain-dev/bin/`, links it as `dev`, and makes it
the default. `-Name` picks another name, `-Configuration Release` another configuration, and
`-NoLink` just builds the tree. Rerun it after a change: the link is a pointer, not a copy, so
nothing has to be reinstalled — though an editor has to restart the language server to pick up a
new `zs-lsp`, and on Windows a running one locks its own binary and has to be stopped before the
build can replace it.

Both projects, because **`zs` and `zs-lsp` always ship together**. They are separate projects with
separate output directories, so neither `bin/Debug/net10.0` is a toolchain by itself: linking the
CLI's gives a working `zs` and an editor that cannot start a language server, and linking the
language server's gives a tree with no `zs` in it at all. `zsup install` refuses a payload missing
either one, and `zsup link` warns about it — the tree may simply not have been built yet.

The dev tree lives inside the checkout on purpose. The compiler finds the standard library by
scanning up from its own location for a `packages/` directory, so `dist/toolchain-dev/bin/` resolves
to the repository's `packages/`: stdlib edits are live too, and nothing is copied.

A linked toolchain gets its own package cache at `~/.zscheme/cache-dev/<name>/`. Without that it
would report the same compiler version as the released toolchain and write to the same
`cache/pkg/<version>/`, so a work-in-progress metadata change could silently corrupt a working
installation. The trade-off is that a linked toolchain builds the standard library once on first
use.

## Editors

Editors find the language server as bare `zs-lsp` on PATH, which is the shim — so an editor follows
whichever toolchain is selected, including a per-project `.zscheme-version`, with no configuration.

Two settings bypass the shim by design, for pointing an editor at a specific build:

- `zscheme.languageServer.path` (VS Code)
- `ZSCHEME_LSP_PATH` (Zed)

## Environment variables

| Variable | Effect |
| --- | --- |
| `ZSCHEME_HOME` | Root for toolchains and caches (default `~/.zscheme`) |
| `ZSCHEME_VERSION` | Toolchain to use, outranking every other rule |
| `ZSCHEME_CACHE_DIR` | Package/git cache root; outranks `ZSCHEME_HOME` for those caches |
| `ZSCHEME_TOOLCHAIN` | Set *by* the shim for the child process; not an input |
| `ZSCHEME_GITHUB_REPO` | Repository releases are fetched from |
| `ZSCHEME_DIST_BASE_URL` | Base URL for downloads, for mirrors and offline testing |
| `ZSCHEME_GITHUB_API_URL` | API base URL `latest` is resolved from (default `https://api.github.com`) |
| `ZSCHEME_INSTALL_VERSION` | Version the bootstrap scripts install; read by them only, not by `zsup` |

A mirrored or airgapped setup needs both URL variables: `ZSCHEME_DIST_BASE_URL` covers asset
downloads, while resolving `latest` is an API call and follows `ZSCHEME_GITHUB_API_URL`. Naming an
explicit version avoids the API call entirely.

## Release assets

`zsup install` and both bootstrap scripts construct these names, so they must stay stable:

```
zscheme-<version>-<rid>.zip      # win-x64, win-arm64
zscheme-<version>-<rid>.tar.gz   # linux-x64, linux-arm64, osx-x64, osx-arm64
zsup-<version>-<rid>.zip|.tar.gz
SHA256SUMS                       # GNU coreutils format, covering every asset
```

Tags are bare versions (`0.4.0`, no `v` prefix), and `.github/workflows/release.yml` fails the build
if a tag disagrees with `Directory.Build.props`.

The `.tar.gz` archives are built on a Linux runner on purpose: tar entries written on Windows carry
mode 0644, which would leave a hand-extracted `zs` non-executable. `zsup` forces mode 0755 on
install regardless, but anyone unpacking a release by hand depends on the archive being right.
