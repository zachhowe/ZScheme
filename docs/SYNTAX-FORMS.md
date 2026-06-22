# ZScheme Syntax Forms Reference

All built-in syntax forms recognized by the ZScheme compiler. These are special forms handled in
`src/ZScheme.Compiler/Ast/AstBuilder.cs` — they cannot be redefined or shadowed.

## Definitions

### `define` — Define a function or value

```scheme
;; Function definition
(define (name [param : Type] ...) : ReturnType body)

;; Value definition
(define name expr)
```

Parameters can have type annotations `[x : Int]` or be bare `[x]`. Return type annotation is
optional. Supports variadic parameters `[xs : Type ...]` (one allowed, must be last) and generic
type constraints via `: where`.

```scheme
(define (factorial [n : Int] [acc : Int]) : Int
  (if (= n 0) acc (factorial (- n 1) (* n acc))))

(define add5 (partial add 5))
```

### `define-async` — Define an async function

```scheme
(define-async (name [param : Type] ...) : ReturnType body)
```

Creates an async function that can use `await`. Return type must be `Task` or `(Task T)`.

```scheme
(define-async (compute-async [x : Int]) : (Task Int)
  (+ x 1))

(define-async (fetch-and-add [x : Int]) : (Task Int)
  (let ([result (await (compute-async x))])
    (+ result 10)))
```

## Bindings

### `let` — Single binding

```scheme
(let ([name expr]) body)
```

Binds `name` to the result of `expr`, then evaluates `body` with the binding in scope.

```scheme
(let ([x (+ 1 2)]) (* x x))
```

### `let*` — Sequential bindings

```scheme
(let* ([name1 expr1]
       [name2 expr2]
       ...)
  body)
```

Each binding can reference all previous bindings. Desugars to nested `let` forms.

```scheme
(let* ([x 5]
       [y (* x 2)]
       [z (+ x y)])
  z)
```

### `lambda` — Anonymous function

```scheme
(lambda (param1 param2 ...) body)
```

Creates a closure. Parameters can have type annotations.

```scheme
(lambda (x) (+ x 1))
(lambda (x y) (+ x y))
```

## Control Flow

### `if` — Conditional

```scheme
(if condition then-expr else-expr)
```

Evaluates `condition`; if true returns `then-expr`, otherwise `else-expr`. Both branches required.

```scheme
(if (= n 0) acc (factorial (- n 1) (* n acc)))
```

### `begin` — Sequential evaluation

```scheme
(begin expr1 expr2 ... exprN)
```

Evaluates all expressions in order, returns the last value. Desugars to nested `let` bindings
with `_` as the ignored name.

### `match` — Pattern matching

```scheme
(match scrutinee
  [pattern1 body1]
  [pattern2 body2]
  ...)
```

Matches `scrutinee` against patterns and evaluates the first matching arm. The compiler checks
for exhaustiveness. Pattern types:

- **Wildcard:** `_` — matches anything
- **Literal:** `0`, `1`, `"hello"`, `#t`, `#f`
- **Variable:** `x` — binds matched value
- **Constructor:** `(Circle r)`, `(Some x)`, `None`

```scheme
(match s
  [(Circle r) (* r r)]
  [(Rect w h) (* w h)])

(match n
  [0 "zero"]
  [1 "one"]
  [_ "other"])
```

## Function Composition

### `|>` — Pipe operator (stdlib macro)

Available via `(import stdlib/pipe)`.

```scheme
(|> value (f1 arg1) (f2 arg2) ...)
```

Threads `value` through successive function calls. Each step receives the previous result as
its first argument.

```scheme
(import stdlib/pipe)
(|> x (add 1) (mul 3) (sub 2))
(|> x add5 double)
```

### `partial` — Partial application

```scheme
(partial function arg1 arg2 ...)
```

Returns a new function with some arguments pre-filled.

```scheme
(define add5 (partial add 5))
(define double (partial mul 2))
```

## Type Definitions

### `define-record` — Product type (immutable record)

```scheme
(define-record Name [field1 : Type1] [field2 : Type2] ...)
(define-record (Name ^a ^b) [field : ^a] ...)              ;; generic
```

Defines an immutable record type with named fields. The record name is also its constructor.
Supports generic type parameters (prefixed with `^`) and `: where` constraints.

```scheme
(define-record Point [x : Int] [y : Int])

;; Usage: (Point 3 4)
```

### `define-struct` — Value type (.NET struct)

```scheme
(define-struct Name [field1 : Type1] [field2 : Type2] ...)
(define-struct (Name ^a ^b) [field : ^a] ...)              ;; generic
```

Defines a .NET value type. Syntax and usage mirror `define-record` — constructor calls, field
accessors (`Name/field`), `(new ...)`, and `with` copy-updates all work the same way —
but the emitted type is a `readonly record struct` with value semantics (assignment and
parameter passing copy the value). Supports generic type parameters and `: where`
constraints.

```scheme
(define-struct Point [x : Int] [y : Int])

;; Usage: (Point 3 4)
```

### `define-union` — Sum type (discriminated union)

```scheme
(define-union Name
  (Case1 [field1 : Type1] ...)
  (Case2 [field1 : Type1] ...)
  ...)
(define-union (Name ^a) ...)                                ;; generic
```

Defines a tagged union. Each case is a constructor. Supports generic type parameters and
`: where` constraints.

```scheme
(define-union Shape
  (Circle [radius : Int])
  (Rect [w : Int] [h : Int]))
```

### `define-type-alias` — Map a ZScheme type name to a CLR type

```scheme
(define-type-alias (Name ^a ^b ...) Fully.Qualified.OpenGenericType)
(define-type-alias (Name ^a ^b ...) Fully.Qualified.OpenGenericType :from "AssemblyName")
(define-type-alias (Name ^a) :array)
(define-type-alias Name Fully.Qualified.NonGenericType)               ;; arity 0
```

Declares that the ZScheme type name `Name` (with the given arity) should be rendered as
the specified CLR type at code generation time. The alias is purely a codegen mapping —
it does not affect type inference, which still treats `Name[Args...]` as an opaque named
type. The optional `:from "AssemblyName"` keyword specifies the assembly that contains
the CLR target when it is not in the default assembly probing path. The `:array`
sentinel is a special form that maps to a single-dimension CLR array (`T[]`); it
requires exactly one type parameter.

Aliases are visible across the entire compilation: a declaration in one module is
available to any other module in the same compilation that transitively depends on it.
Standard library aliases (`Hash`, `TreeList`, `Vector`, `Mutable-Hash`,
`Mutable-TreeList`, `Mutable-Vector`) live in their respective stdlib modules and
are pulled in by the default prelude — programs do not need to reference them
explicitly. The pure linked-list type `(List ^a)` (with `Nil`/`Cons`
constructors) lives in `stdlib/list` and is also part of the prelude.

```scheme
(define-type-alias (Hash ^k ^v)
  System.Collections.Immutable.ImmutableDictionary :from "System.Collections.Immutable")

(define-type-alias (Mutable-Hash ^k ^v) System.Collections.Generic.Dictionary)

(define-type-alias (Mutable-Vector ^a) :array)

;; User code can declare new aliases the same way:
(define-type-alias (BigList ^a)
  System.Collections.Immutable.ImmutableList :from "System.Collections.Immutable")
```

Redeclaring an existing alias with a different target is an error. Redeclaring with the
identical target (e.g. when stdlib's declaration is loaded twice through different
import paths) is silently idempotent.

## Record Operations

### `with` — Record copy-with-updates

```scheme
(with record-expr [field1 value1] [field2 value2] ...)
```

Returns a new record (or struct) value with the listed fields replaced; the original is
untouched. Works on any `define-record` or `define-struct` type and compiles to C#'s `with` expression.
Chained `with` expressions evaluate inner-first.

```scheme
(define-record Point [x : Int] [y : Int])

(define (shift-x [p : Point] [new-x : Int]) : Point
  (with p [x new-x]))

(define (move-to [p : Point] [nx : Int] [ny : Int]) : Point
  (with p [x nx] [y ny]))
```

## Object-Oriented Programming

### `define-class` — Define a mutable class

```scheme
(define-class Name [field : Type] ... (define (Method [params]) : RetType body) ...)
(define-class Name : BaseClass IFace1 IFace2 ...)           ;; inheritance
(define-class #:open Name ...)                               ;; allow subclassing
(define-class (Name ^a) ...)                                 ;; generic
```

Defines a class with fields, methods, and optional inheritance. Classes are sealed by default;
use `#:open` to allow subclassing. Supports `constructor` blocks with `super` and `set!`.
Instance methods must be defined with `define` or `define-async`; the bare
`(Name [params] ...)` form is not accepted inside class bodies.

```scheme
(define-class #:open Animal
  [name : String]
  [sound : String]
  (define (Speak) : String
    (string-append (string-append name " says ") sound)))

(define-class #:open Dog : Animal
  [breed : String]
  (define (Speak) : String
    (string-append (string-append name " the ") breed)))
```

#### Field flags: `#:mutable` and `#:init`

Fields default to immutable, read-only properties. Append a flag after the
type annotation to change that:

- `#:mutable` — field may be reassigned via `set!`.
- `#:init` — field emits an init-only property setter, usable from C# object
  initializers. Mutually exclusive with `#:mutable`.

```scheme
(define-class Counter
  [count : Int #:mutable])                              ;; reassignable

(define-record Point [x : Int #:init] [y : Int #:init])        ;; init-only setters
```

### `define-interface` — Define an interface

```scheme
(define-interface Name
  (Method1 [params] : RetType)
  (Method2 [params] : RetType)
  ...)
(define-interface Name : IBase1 IBase2 ...)                  ;; inheritance
(define-interface (Name ^a) ...)                             ;; generic
```

Defines method signatures without implementations.

```scheme
(define-interface IGreeter
  (Greet [] : String))

(define-interface IAdvancedCalculator : ICalculator
  (Multiply [a : Int] [b : Int] : Int))
```

### `object` — Anonymous object expression

```scheme
(object InterfaceName (define (Method [params]) : RetType body) ...)
(object (IFace1 IFace2) ...)                          ;; multiple interfaces
(object : BaseClass ...)                              ;; inherit from class
(object : BaseClass IFace1 ...)                       ;; class + interfaces
```

Creates an anonymous object implementing interfaces or inheriting from a class. Can capture
variables from the enclosing scope. Supports `constructor` blocks with `super`. Instance
methods must be defined with `define` or `define-async`.

```scheme
(define greeter
  (object IGreeter
    (define (Greet [name : String]) : String
      (string-append "Hello, " name))))

(define loud-dog
  (object : Animal
    (constructor (super "Dog" "woof"))
    (define (Speak) : String
      (string-append (super/Speak) "!!!"))))
```

### `super/Method` — Call base class method

```scheme
(super/MethodName arg1 arg2 ...)
```

Invokes the base class implementation of a method. Valid in class and object method bodies.

```scheme
(define (Speak) : String
  (string-append (super/Speak) "!!!"))
```

### `super` — Call base class constructor

```scheme
(constructor [params]
  (super arg1 arg2 ...)
  ...)
```

Calls the base class constructor. Only valid inside `constructor` blocks.

```scheme
(constructor [display-name : String]
  (set! name display-name)
  (set! sound "..."))
```

### `set!` — Mutate a field

```scheme
(set! field-name expr)
```

Assigns a value to a field. Only valid inside `constructor` blocks.

```scheme
(set! name "Alice")
```

## Error Handling

### `catch` — Catch .NET exceptions as Result

```scheme
(catch expr)
```

Evaluates `expr`. If it throws a .NET exception, wraps it in `Err`; otherwise wraps the
result in `Ok`.

```scheme
(define (safe-parse [s : String]) : (Result Int Error)
  (catch (parse-int s)))

(define (safe-divide [a : Int] [b : Int]) : (Result Int Error)
  (catch (divide a b)))
```

### `raise` — Throw an exception

```scheme
(raise expr)
```

Throws `expr` as a .NET exception. The return type unifies with any type, so `raise` can
appear in either branch of an `if`.

```scheme
(define (divide [a : Int] [b : Int]) : Int
  (if (= b 0)
    (raise (new System.ArgumentException "divisor cannot be zero"))
    (/ a b)))
```

### `with-handlers` — Catch specific .NET exception types

```scheme
(with-handlers
  ([ExceptionType var] handler-body)
  ...
  body-expr)
```

Evaluates `body-expr` inside a try block. If an exception matching one of the handler types
is thrown, the corresponding handler body is evaluated with the exception bound to `var`.
Handlers are checked in order, so you must list them most-specific first: a handler whose
exception type is a subtype of (or equal to) an earlier handler's type is unreachable and
rejected at compile time. Unrelated exception types (neither is a subtype of the other) may
appear in any order. All handler bodies must return the same type as `body-expr`. Use `_`
as the binding variable to discard the exception.

```scheme
(define (safe-divide [a : Int] [b : Int]) : Int
  (with-handlers
    ([System.DivideByZeroException _] 0)
    (/ a b)))

(define (describe-error [a : Int] [b : Int]) : String
  (with-handlers
    ([System.DivideByZeroException _] "division by zero")
    ([System.Exception e] (. e Message))
    (begin (/ a b) "ok")))
```

## Async

### `await` — Await a Task

```scheme
(await task-expr)
```

Waits for an async `Task` to complete and unwraps the result. Only valid inside
`define-async` functions.

```scheme
(define-async (double-compute [x : Int]) : (Task Int)
  (let ([a (await (compute-async x))])
    (let ([b (await (compute-async a))])
      (+ a b))))
```

## CLR Interop

### `import-clr` — Import .NET types and members

```scheme
(import-clr
  [alias Namespace.Type/Member]
  [alias Namespace.Type/Member :kind]
  [alias Namespace.Type/Member :kind : (ArgTypes -> RetType)]
  [alias Namespace.Type/Member ^a ^b :kind : (ArgTypes -> RetType)]
  Namespace1 Namespace2 ...)
```

Imports .NET methods, properties, and namespaces. The `:kind` specifier controls how the
member is bound:

| Kind | Description |
|------|-------------|
| *(default)* | Static method |
| `:instance` | Instance method |
| `:instance-property` | Property getter |
| `:instance-property-set` | Property setter |
| `:instance-indexer` | Indexer getter |
| `:instance-indexer-set` | Indexer setter |

Bare atoms import namespaces. Type parameters and type annotations are optional.

A `:from "AssemblyName"` hint (mirroring `define-type-alias`'s `:from`) loads the named
assembly so types whose namespace does not match their assembly file name can be resolved
— e.g. `Microsoft.AspNetCore.Builder.EndpointRouteBuilderExtensions` ships in
`Microsoft.AspNetCore.Routing.dll`:

```scheme
(import-clr
  [map-get Microsoft.AspNetCore.Builder.EndpointRouteBuilderExtensions/MapGet
    :from "Microsoft.AspNetCore.Routing"
    : (Microsoft.AspNetCore.Routing.IEndpointRouteBuilder String
       (delegate Microsoft.AspNetCore.Http.RequestDelegate)
       -> Microsoft.AspNetCore.Builder.IEndpointConventionBuilder)])
```

```scheme
(import-clr
  [writeln System.Console/WriteLine]
  [parse-int System.Int32/Parse])

(import-clr
  System.Collections.Immutable
  [list-count System.Collections.Immutable.ImmutableList.Count
    :instance-property : ((List ^a) -> Int)]
  [list-add System.Collections.Immutable.ImmutableList.Add
    :instance : ((List ^a) ^a -> (List ^a))])
```

### `new` — Construct a .NET object

```scheme
(new TypeName arg1 arg2 ...)
```

Calls a .NET constructor.

```scheme
(new System.Object)
(new System.Text.StringBuilder "Hello, ZScheme!")
(new System.ArgumentException "invalid argument")
```

### `typeof` — Reflect a type as a `System.Type` value

```scheme
(typeof TypeExpr)
```

Produces the `System.Type` for a compile-time-known type, mirroring C# `typeof(T)`. The
argument is any ZScheme type expression — a primitive name, a user-defined type, a generic
instantiation, a nullable, or a type alias.

```scheme
(typeof Int)                 ; → typeof(int)
(typeof String)              ; → typeof(string)
(typeof MyRecord)            ; → typeof(MyRecord)
(typeof (List Int))          ; → typeof(ImmutableList<int>)        ; through stdlib alias
(typeof (Result Int String)) ; → typeof(Result<int, string>)
(typeof Int?)                ; → typeof(int?)
```

Use `typeof` to pass a `System.Type` to CLR APIs that take one — e.g. typed JSON
serialization (`JsonSerializer.Serialize(value, typeof(MyRecord))`), service resolution,
or attribute lookup.

### `delegate` — Specific .NET delegate type

```scheme
(delegate Fully.Qualified.DelegateType)
```

This is a **type expression only** (not a value expression). It specifies that a value should
be treated as a particular .NET delegate type, bypassing the compiler's default mapping of
function types to `System.Func<>` / `System.Action<>`.

Usable wherever type annotations appear:

- **Parameter annotation:** `(lambda ([f : (delegate System.Action)]) body)`
- **`let` binding:** `(let ([x : (delegate System.Action) expr]) body)`
- **Function parameter:** `(define (handle [h : (delegate System.Action)]) body)`
- **`import-clr` annotation:** `(import-clr handler MyDelegate : (delegate MyDelegate) (Unit -> Unit))`

This form is needed when a CLR API expects a specific delegate type (e.g. ASP.NET Core's
`RequestDelegate`) rather than a generic `Func<>` or `Action<>`.

```scheme
(import-clr [map-get Microsoft.AspNetCore.Routing.IEndpointRouteBuilder.MapGet
             :instance : ((IEndpointRouteBuilder) String (delegate RequestDelegate))])

(define (start [app : WebApplication]) : Unit
  (map-get app "/"
    (lambda [ctx : HttpContext] : Unit
      (. ctx Response WriteTextAsync "Hello"))))
```

## Modules

### `namespace` — Set the .NET namespace

```scheme
(namespace NamespaceName)
```

Sets the namespace for all subsequent definitions in the file.

```scheme
(namespace ZScheme.Examples)
```

### `module` — Declare a module

```scheme
(module Name)             ;; implicit body: absorbs remaining forms in file
(module Name form1 ...)   ;; explicit body
```

Groups definitions into a named module for the module system.

```scheme
(module factorial)
```

### `import` — Import a module

```scheme
(import module-path)
```

Makes definitions from another module available. Module paths use `/` separators.

```scheme
(import stdlib/option)
(import stdlib/result)
(import stdlib/list)
```

### `export` — Export definitions

```scheme
(export name1 name2 ...)
```

Marks names as public exports from the current module.

## Attributes

### `@` — Apply .NET attributes

```scheme
(@ AttributeName positional-args... [NamedKey value] ...)
```

Applies a .NET attribute to the following declaration (`define`, `define-record`, `define-union`, `define-class`,
or `define-interface`). Supports positional and named arguments.

```scheme
(@ System.Obsolete "Use new-function instead")
(define (old-function [x : Int]) : Int x)
```

## Type System Notation

These are not syntax forms but appear within them:

| Notation | Meaning |
|----------|---------|
| `^a`, `^b` | Type parameters (generic) |
| `(ArgTypes -> RetType)` | Function type |
| `(Option Int)` | Parameterized type |
| `: where (^a notnull)` | Generic constraint |
| `[x : Type ...]` | Variadic parameter |
| `#t`, `#f` | Boolean literals |
| `()` | Unit literal (empty list) |

Constraint kinds: `notnull`, `struct`, `class`, `new`, `unmanaged`, `default`.
