# ZScheme

ZScheme is a Scheme-like functional programming language that compiles to .NET. It combines S-expression syntax with static type inference, immutable data structures, and full CLR interoperability.

## Features

- **Static type inference** — Hindley-Milner type system with unification; no type annotations required (but supported)
- **Immutable by default** — Lists, maps, and arrays backed by .NET immutable collections
- **Pattern matching** — Destructuring with exhaustiveness checking
- **Algebraic data types** — Records, discriminated unions, and tuples
- **First-class functions** — Closures, higher-order functions, partial application, and composition
- **Tail call optimization** — Recursive functions optimized automatically
- **Result and Option types** — Functional error handling built into the standard library
- **Macros** — `define-syntax` with `syntax-rules` for compile-time code generation
- **Async/await** — Async functions backed by .NET Tasks
- **CLR interop** — Call .NET methods, construct objects, implement interfaces
- **Object system** — Classes with inheritance, interfaces, method dispatch, and anonymous classes
- **Two code generation backends** — Emit C# source or IL directly (via AsmResolver)
- **Package system** — Declare dependencies, build, test, and install packages
- **NuGet integration** — Reference NuGet packages directly from package manifests
- **Built-in test framework** — xUnit-based ZUnit with rackunit-style assertions

## Quick Start

### Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [PowerShell 7.6.0+](https://github.com/PowerShell/PowerShell)

### Build the compiler

```bash
dotnet build
```

### Compile and run a program

```bash
# Compile a .zs file to an executable
zs compile examples/factorial.zs -o out

# Compile and run in one step
zs run examples/factorial.zs

# Start the REPL
zs repl
```

Or via `dotnet run` without installing:

```bash
dotnet run --project src/ZScheme.Cli -- compile examples/factorial.zs -o out
```

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
;; Immutable list
(define nums (list 1 2 3 4 5))
(list/map (lambda (x) (* x 2)) nums)
(list/filter (lambda (x) (> x 2)) nums)
(list/fold + 0 nums)

;; Immutable map
(define scores (map-of (pair "alice" 95) (pair "bob" 87)))
```

### Error Handling

```scheme
(define (safe-div [a : Int] [b : Int]) : (Result Int ErrorInfo)
  (if (= b 0)
    (Err (Error "division by zero"))
    (Ok (/ a b))))
```

### Async/Await

```scheme
(define-async (fetch-and-add [x : Int]) : (Task Int)
  (let [result (await (compute-async x))]
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

(let [sb (new System.Text.StringBuilder "hello")]
  (sb-tostring sb))
```

### Classes and Inheritance

```scheme
(define-class : open Animal
  [name : String]
  [sound : String]
  (define (Speak) : String
    (string/format "{0} says {1}" name sound)))

(define-class : open Dog : Animal
  [breed : String]
  (define (Speak) : String
    (string/format "{0} the {1}" name breed)))
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
| `stdlib/error` | `ErrorInfo` type for structured errors |
| `stdlib/list` | Immutable list operations (`map`, `filter`, `fold`, ...) |
| `stdlib/array` | Immutable array operations |
| `stdlib/map` | Immutable dictionary operations |
| `stdlib/string` | String utilities |
| `stdlib/math` | Math functions |
| `stdlib/datetime` | Date and time utilities |
| `stdlib/task` | Async task helpers |
| `stdlib/slist` | Pure singly linked list (`SList`, `SCons`, `SNil`) |
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
