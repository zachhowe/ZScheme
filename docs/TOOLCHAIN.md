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
the PATH change.

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

`install` also takes `--force` (replace an existing installation) and `--no-default` (install
without changing the default).

## How a toolchain is selected

`~/.zscheme/bin/zs` and `zs-lsp` are the `zsup` binary under another name; it recognises how it was
invoked and hands off to the real executable. Selection order, highest first:

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

The sources are still worth shipping: the cache rebuilds from them if it is ever cleared, and the
language server uses them for go-to-definition into stdlib.

## Developing on the compiler itself

```sh
dotnet build
zsup link dev src/ZScheme.Cli/bin/Debug/net10.0
zsup use dev
```

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
