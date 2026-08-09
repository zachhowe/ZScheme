# ZScheme

ZScheme is a Scheme-like functional programming language that compiles to .NET. It combines S-expression syntax with static type inference, immutable data structures, and full CLR interoperability.

## Goals

- Implement data structures using existing .NET types
- Provide a usable Scheme-like language that compiles to efficient .NET code
- Provide APIs that are similar to Racket
- Polished editor support using language server protocol

## Features

- **Static type inference** — Hindley-Milner type system with unification; no type annotations required (but supported)
- **Immutable by default** — Lists, hashes, and vectors backed by .NET immutable collections
- **Pattern matching** — Destructuring with exhaustiveness checking
- **Algebraic data types** — Records, discriminated unions, and tuples
- **First-class functions** — Closures, higher-order functions, partial application, and composition
- **Tail call optimization** — Recursive functions unrolled automatically
- **Result and Option types** — Functional error handling built into the standard library
- **Macros** — `define-syntax` with `syntax-rules` for compile-time code generation
- **Async/await** — Async functions backed by .NET Tasks
- **CLR interop** — Call .NET methods, construct objects, implement interfaces
- **Object system** — Classes with inheritance, interfaces, method dispatch, and anonymous classes
- **Two code generation backends** — Emit C# source or IL directly (via AsmResolver)
- **Package system** — Declare dependencies, build, test, and install packages
- **NuGet integration** — Reference NuGet packages directly from package manifests
- **Built-in test framework** — xUnit-based ZUnit with rackunit-style assertions

## Features still in progress

- [ ] Continuations support (e.g. `call/cc`)
- [ ] Editor support: Sublime Text, Visual Studio Code, Zed

## Features planned

- [ ] Mutable properties on record types
- [ ] Documentation: Showing doc comments in the LSP, HTML doc generation

## Quick Start

### Install

Linux and macOS:

```bash
curl -fsSL https://raw.githubusercontent.com/zachhowe/ZScheme/master/install.sh | sh
```

Windows (PowerShell):

```powershell
irm https://raw.githubusercontent.com/zachhowe/ZScheme/master/install.ps1 | iex
```

This installs `zsup`, the toolchain manager, along with the latest `zs` and `zs-lsp`. Running
programs needs the [.NET 10 runtime](https://dotnet.microsoft.com/download).

### Compile and run a program

```bash
# Compile a .zs file to an executable
zs compile examples/factorial.zs -o out

# Start the REPL
zs repl
```

### Manage versions

Several ZScheme versions can be installed at once, and `zs` follows whichever one is selected:

```bash
zsup install 0.4.0        # install a version
zsup use 0.4.0            # make it the default
zsup list                 # see what is installed

echo 0.3.0 > .zscheme-version   # pin this project to a different version
```

See [docs/TOOLCHAIN.md](docs/TOOLCHAIN.md) for the full command set, the selection rules, and how
to link a locally built compiler.

### Build from source

Requirements: [.NET 10 SDK](https://dotnet.microsoft.com/download) and
[PowerShell 7.6.0+](https://github.com/PowerShell/PowerShell).

```bash
dotnet build
```

Run the compiler without installing it:

```bash
dotnet run --project src/ZScheme.Cli -- compile examples/factorial.zs -o out
```

Or register your build as a toolchain, so `zs` runs it everywhere:

```bash
pwsh ./build-dev-toolchain.ps1 -Use
```

That builds both `zs` and `zs-lsp` — they always ship together, so neither project's `bin/`
directory is a toolchain on its own — links the result as `dev`, and makes it the default.

## Language Overview

### Functions

```scheme
(define (add [x : Int] [y : Int]) : Int
  (+ x y))

;; Type annotations are optional — the compiler infers them
(define (double x)
  (* x 2))

;; Lambdas
(define (make-adder [n : Int]) : (Int -> Int)
  (lambda (x) (+ n x)))
```

### Records and Unions

```scheme
(define-record Point [x : Int] [y : Int])

(define-union Shape
  (Circle [radius : Int])
  (Rect [w : Int] [h : Int]))
```

### Pattern Matching

```scheme
(define (area [s : Shape]) : Int
  (match s
    [(Circle r) (* r r)]
    [(Rect w h) (* w h)]))
```

The compiler checks that all cases are covered and reports unmatched patterns.

### Collections

```scheme
(import stdlib/treelist)
(import stdlib/hash)

;; Immutable AVL-backed tree list
(define nums (treelist 1 2 3 4 5))
(treelist-map nums (lambda (x) (* x 2)))
(treelist-filter nums (lambda (x) (> x 2)))
(treelist-fold nums 0 (lambda (acc x) (+ acc x)))

;; Immutable hash table
(define scores (hash (pair "alice" 95) (pair "bob" 87)))
```

### Error Handling

```scheme
(import stdlib/result)
(import stdlib/error)

(define (safe-div [a : Int] [b : Int]) : (Result Int Error)
  (if (= b 0)
    (Err (make-error "division by zero"))
    (Ok (/ a b))))
```

### Async/Await

```scheme
(define-async (fetch-and-add [x : Int]) : (Task Int)
  (let ([result (await (compute-async x))])
    (+ result 10)))
```

### Macros

```scheme
(define-syntax define-dto
  (syntax-rules ()
    [(define-dto name field ...)
     (define-record name field ...)]))

(define-dto UserInfo [name : String] [age : Int])
```

### CLR Interop

```scheme
;; Static methods: Type/Method with slash separator
(import-clr
  [writeln System.Console/WriteLine])

(writeln "Hello from .NET!")

;; Instance methods: Type.Method with :instance flag and type annotation
(import-clr
  [sb-tostring System.Text.StringBuilder.ToString
    :instance : (System.Text.StringBuilder -> String)])

(let ([sb (new System.Text.StringBuilder "hello")])
  (sb-tostring sb))
```

### Classes and Inheritance

```scheme
(import stdlib/string)

;; Base class must be marked #:open to allow subclassing
(define-class #:open Animal
  [name : String]
  [sound : String]
  (define (Speak) : String
    (format "{0} says {1}" name sound)))

(define-class #:open Dog : Animal
  [breed : String]
  (define (Speak) : String
    (format "{0} the {1}" name breed)))
```

### Testing with ZUnit

```scheme
(import zunit)

(test-suite MathTests
  (test-case addition
    (check-equal? (+ 1 2) 3))
  (test-case subtraction
    (check-equal? (- 3 1) 2)))
```

## CLI Reference

| Command | Description |
|---------|-------------|
| `compile <file.zs>` | Compile a ZScheme source file |
| `build` | Build a package from its manifest |
| `test` | Run package tests |
| `run <file.zs>` | Compile and execute a file |
| `lint [paths...]` | Report style diagnostics; `--fix` rewrites the files in place |
| `install` | Compile and cache a library package |
| `repl` | Start the interactive REPL |
| `package init` | Initialize a new package |
| `generate-project` | Generate a .csproj from a package |

### Common Options

| Option | Description |
|--------|-------------|
| `-o, --output <dir>` | Output directory |
| `-b, --backend cs\|il` | Code generation backend (C# source or IL) |
| `--ref <dir>` | CLR assembly reference directory (repeatable) |
| `--module-path <dir>` | Additional module search path (repeatable) |
| `--package-path <dir>` | Register a package for qualified imports (repeatable) |
| `--precompiled <path>` | Reference a precompiled .dll (repeatable) |
| `--debug` | Enable compiler debug logging |

## Standard Library

The standard library (`stdlib`) provides modules imported with qualified names:

| Module | Description |
|--------|-------------|
| `stdlib/option` | `Option` type — `Some` and `None` |
| `stdlib/result` | `Result` type — `Ok` and `Err` |
| `stdlib/error` | `Error` record and `make-error` constructor for structured errors |
| `stdlib/list` | Pure singly linked list (`List`, `Cons`, `Nil`) with `map`, `filter`, `fold`, ... |
| `stdlib/treelist` | AVL-tree-backed immutable list (`treelist-map`, `treelist-filter`, `treelist-fold`, ...) |
| `stdlib/vector` | Immutable vector operations |
| `stdlib/hash` | Immutable hash table operations |
| `stdlib/string` | String utilities |
| `stdlib/math` | Math functions |
| `stdlib/datetime` | Date and time utilities |
| `stdlib/task` | Async task helpers |
| `stdlib/catch` | Exception-to-Result conversion |
| `stdlib/mutable/*` | Mutable collection variants |
| `stdlib/concurrent/*` | Thread-safe concurrent collections |

## Package Format

Packages are defined with a `.zspkg` manifest:

```scheme
(package
  (name "my-package")
  (version "0.1.0")
  (import-prefix "mylib")
  (sources
    (main "src")
    (test "test"))
  (dependencies
    (nuget
      [System.Collections.Immutable "9.0.0"]))
  (test-dependencies
    (zscheme
      [zunit :local "../zunit"]))
  (build
    (main
      (namespace "MyLib"))))
```

## Project Structure

```
src/ZScheme.Cli/          CLI entry point and REPL
src/ZScheme.Compiler/     Core compiler (lexer, parser, type checker, IR, codegen)
packages/stdlib/          Standard library
packages/zunit/           Testing framework
packages/http/            HTTP client library
examples/                 Example programs
tests/                    Compiler test suite
```

## Running Tests

```bash
# Run compiler tests
dotnet test

# Run package tests (stdlib, http)
pwsh ./run-package-tests.ps1

# Build all examples
pwsh ./build-examples.ps1
```

## Inspired by

- [F#](https://fsharp.org/) — ML-family language on .NET with algebraic data types, pattern matching, and type inference
- [Racket](https://racket-lang.org/) — Scheme descendant with a rich macro system and language-oriented programming
- [Typed Racket](https://docs.racket-lang.org/ts-guide/) — Racket’s gradually-typed sister language which allows the incremental addition of statically-checked type annotations
- [Plait](https://docs.racket-lang.org/plait/) — Statically typed teaching language built in Racket
