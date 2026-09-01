# ZScheme Syntax Forms Reference

All built-in syntax forms recognized by the ZScheme compiler. These are special forms handled in
`src/ZScheme.Compiler/Ast/AstBuilder.cs` — they cannot be redefined or shadowed.

## Names

Names are case-sensitive, and hyphenated and PascalCase spellings of one word are *different*
names — for functions and for types alike. `HttpResponse` and `http-response` may both be
declared in one module as two unrelated types, each with its own constructor and accessors
(`(HttpResponse-status-code …)` and `(http-response-status-code …)`). They sanitize to the same
.NET identifier, so the compiler gives the second one declared a distinct emitted name; that
happens behind the scenes and never changes how either is written in ZScheme.

Capitalisation is not part of any syntactic rule: a hyphenated or lower-case name may be a base
class, an implemented interface, a type argument, or a union case wherever a PascalCase one may.
The one place spelling still matters is a bare atom in `match` pattern position — see
[`match`](#match--pattern-matching).

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

Top-level definitions are visible to each other regardless of order, so one may call another
declared further down the file — which is how mutual recursion is written at the top level:

```scheme
(define (even? [n : Int]) : Bool (if (= n 0) #t (odd? (- n 1))))
(define (odd?  [n : Int]) : Bool (if (= n 0) #f (even? (- n 1))))
```

A forward reference sees the callee's *declared* signature, not a generalized one, so a generic
function called before it is declared is used at a single type. Declare it first to call it at
several. Two top-level definitions may not share a name: the second does not shadow the first
the way a nested definition would — every call would bind to it, including calls written above
it — so it is an error.

An optional `#:recursive` marker before the signature asserts that the function's
self-recursion is intended even though it will not be compiled as a loop, silencing the
`ZS0005` warning for that definition. Use it where the recursion genuinely cannot be a
loop — a tree fold, a result built around the recursive call — not to quiet a function
that could be rewritten with an accumulator.

```scheme
(define #:recursive (sum [xs : (List Int)]) : Int
  (match xs
    [Nil 0]
    [(Cons h t) (+ h (sum t))]))
```

#### Nested definitions

A `define` may also appear inside a body — a function, `lambda`, `let`, `let*`, `letrec`, `use`,
`use*` or `begin` body. It is visible for the rest of that body only, and it can close over the
enclosing function's parameters instead of taking them as arguments:

```scheme
(define (sum-to [n : Int]) : Int
  (define (loop [i : Int] [acc : Int]) : Int    ;; `n` is captured, not passed
    (if (> i n) acc (loop (+ i 1) (+ acc i))))
  (loop 1 0))
```

A run of *adjacent* definitions forms one group whose members can all see each other, so they may
call each other:

```scheme
(define (classify [n : Int]) : Int
  (define (even? [k : Int]) : Bool (if (= k 0) #t (odd? (- k 1))))
  (define (odd? [k : Int]) : Bool (if (= k 0) #f (even? (- k 1))))
  (if (even? n) 1 0))
```

Definitions do not have to lead the body; each run scopes over whatever follows it, so a definition
placed after an expression is not visible to that expression. A body may not *end* with a
definition, since it would have no result value.

A nested definition is compiled exactly like a `letrec` binding — lifted to a top-level static
function with its captures as leading parameters — so it inherits `letrec`'s guarantees and its one
restriction:

- A tail-recursive nested definition compiles to a loop and runs in constant stack on both
  backends. One that recurses off the tail spine warns (`ZS0005`) exactly as a top-level
  definition would, and takes the same `#:recursive` opt-out.
- It may be generic, including in the enclosing function's type parameters.
- Inside a class or `object` method it may use the instance — read a field, write a `#:mutable`
  one, call a sibling method — and still compiles to a loop. See the `letrec` limitation below
  for the two shapes that remain out of reach.

Only `define` nests. `define-async` and the type-declaration forms (`define-record`,
`define-struct`, `define-union`, `define-class`, `define-interface`, `define-type-alias`) are
top-level only. A `:where` clause is not allowed on a nested definition — the enclosing function's
constraints already apply to its type parameters.

### `define-async` — Define an async function

```scheme
(define-async (name [param : Type] ...) : ReturnType body)
```

Creates an async function that can use `await`. Return type must be `Task` or `(Task T)`.
Accepts the same `#:recursive` marker as `define`, before the signature.

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

### `letrec` — Recursive bindings

```scheme
(letrec ([name1 expr1]
         [name2 expr2]
         ...)
  body)
```

Every name in the group is in scope in *every* binding's value and in the body. That is what
separates it from `let`/`let*`, whose value is evaluated in the enclosing scope — so `letrec` is
the only form that can express a local recursive or mutually-recursive function. Names may not
repeat within a group.

```scheme
;; Mutual recursion
(letrec ([even? (lambda ([n : Int]) : Bool (if (= n 0) #t (odd? (- n 1))))]
         [odd?  (lambda ([n : Int]) : Bool (if (= n 0) #f (even? (- n 1))))])
  (even? 10))

;; Self recursion
(letrec ([sum (lambda ([n : Int]) : Int (if (= n 0) 0 (+ n (sum (- n 1)))))])
  (sum 5))
```

Initialization still runs left to right. A binding whose value is a `lambda` is unconstrained —
building a closure reads nothing, and by the time it can be called the whole group exists. Any
other binding may only use names bound *earlier* in the group, counting anything reachable
through the values it mentions:

```scheme
(letrec ([a 1]
         [f (lambda ([n : Int]) : Int (if (= n 0) a (f (- n 1))))])
  (f 3))                              ;; OK — 'a' is read only when f is called

(letrec ([x (+ y 1)] [y 2]) x)        ;; Error: 'x' uses 'y' before it is initialized
(letrec ([g (lambda () a)] [h (g)] [a 1]) h)
                                      ;; Error: evaluating 'h' calls 'g', which reads 'a'
```

A tail-recursive `letrec` function compiles to a loop, so it runs in constant stack on both
backends.

A group inside a generic function is fine: the lifted functions become generic over the type
variables their own signatures mention, and both backends instantiate the call sites explicitly.

A group written inside a class or `object` method may use the instance. A field that cannot change
after construction is captured by value, like any enclosing local. Anything else that needs a
`this` — reading or writing a `#:mutable` field, calling a sibling method, calling `super/` — makes
the group a private method of that class instead of a top-level function. Either way it still
compiles to a loop, including on an `#:open` class, where the synthesized method is private and so
cannot be overridden.

**Limitation:** a group member may only be *called*, not used as a value, when it is generic (a
generic lifted function cannot be turned into a delegate) or when it reaches the instance (a
private method has no delegate form either). Move it to a top-level `define` if you need to pass
it around. A group in a *constructor* cannot use the instance at all: fields are not in scope
there, and the instance is not complete until the constructor returns. A group that reaches the
instance also cannot be generic, since the method hosting it has nowhere to declare type
parameters — annotate its parameter and return types.

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
- **Literal:** `0`, `1`, `"hello"`, `#t`, `#f`, `'symbol`
- **Variable:** `x` — binds matched value
- **Constructor:** `(Circle r)`, `(Some x)`, `None`

A bare atom is the one place a name's spelling still carries meaning, because `None` and `x` are
written identically. It is read as a constructor pattern when it starts with an upper-case letter
*or* when it names a union case that takes no fields — so `(match c [red 1] [green 2])` matches
the two cases of `(define-union color (red) (green))`, imported ones included. Any other bare
atom binds a variable. Give a binder a name no nullary case in scope uses, or write `_` when the
value is unused.

```scheme
(match s
  [(Circle r) (* r r)]
  [(Rect w h) (* w h)])

(match n
  [0 "zero"]
  [1 "one"]
  [_ "other"])
```

## Operators

Built-in operators are variadic: the AST builder folds an n-ary call down to the
binary form, so `(+ a b c d)` means `(+ (+ (+ a b) c) d)`.

| Operator | Arity | Notes |
|----------|-------|-------|
| `+` | 1+ | Numeric addition **or string concatenation** (see below). `(+ x)` → `x` |
| `*` | 1+ | `(* x)` → `x` |
| `-`, `/` | 1+ | 1-arg is negation / reciprocal, not identity |
| `%` | 2+ | `Int` only; no 1-arg form |
| `<`, `>`, `<=`, `>=` | 2+ | Numeric only. Chained: `(< a b c)` means `(and (< a b) (< b c))` |
| `=`, `!=` | 2+ | Any type. `(!= a b c)` means all three are pairwise distinct |
| `and`, `or` | 1+ | Short-circuiting |
| `not` | 1 | |
| `string-append` | 1+ | String concatenation. `(string-append x)` → `x` |

### String concatenation

`+` is typed over `{Int, Float, String}`, so it concatenates strings as well as
adding numbers. `string-append` is a synonym for the string case; both compile to
the same code.

```scheme
(string-append "Hello, " name "! You have " (int->string n) " messages.")
(+ "Hello, " name "!")          ; same thing
```

Operands must agree — `(+ 1 "a")` is a type error, and `+` is the only operator
widened to `String` (`(- "a" "b")` and `(< "a" "b")` are type errors).

## Quoting

### `'symbol` — Symbol literal

```scheme
(f 'some-symbol)
(quote some-symbol)   ; 'x desugars to (quote x)
```

A quoted identifier is a **symbol** — an interned value of type `Symbol` (backed by
`ZScheme.Runtime.ZSymbol`). Symbols with the same name are the same instance, so `(= 'a 'a)` is
`#t`. Symbols can be compared with `=`, converted with `symbol->string` / `string->symbol`, and
used as `match` literal patterns.

Quoting a self-evaluating literal yields that literal (`'5` → `5`, `'#t` → `#t`, `'"s"` → `"s"`).
Quoting a list (`'(a b c)`) is **not yet supported**.

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

#### Field accessors

Each field defines an accessor named `TypeName-fieldName`, taking the record as its only
argument. The type name keeps the exact spelling it was declared with.

```scheme
(define-record HttpResponse [status-code : Int] [body : String])

(define (ok? [r : HttpResponse]) : Bool
  (= (HttpResponse-status-code r) 200))
```

> **Deprecated:** accessors used to be spelled `TypeName/fieldName`. That form still
> resolves, but reports `ZS0006` and will be removed in a future release. Silence the warning
> for one compilation with `--no-warn-deprecated-accessor-syntax`, or for a package with
> `(build (main (warn-deprecated-accessor-syntax "false")))`. The language server offers a
> quick fix that rewrites the name in place.
>
> Because the separator is now `-`, two different declarations can mint the same accessor —
> `(define-record Foo-bar [baz])` and `(define-record Foo [bar-baz])` both produce
> `Foo-bar-baz`, and the later declaration wins. Rename one of them if that happens.

### `define-struct` — Value type (.NET struct)

```scheme
(define-struct Name [field1 : Type1] [field2 : Type2] ...)
(define-struct (Name ^a ^b) [field : ^a] ...)              ;; generic
```

Defines a .NET value type. Syntax and usage mirror `define-record` — constructor calls, field
accessors (`Name-field`), `(new ...)`, and `with` copy-updates all work the same way —
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
    (string-append name " says " sound)))

(define-class #:open Dog : Animal
  [breed : String]
  (define (Speak) : String
    (string-append name " the " breed)))
```

#### Member accessors

Like records, a class binds `TypeName-fieldName` for each field — including fields inherited
from a base class — and `TypeName-MethodName` for each method, taking the instance as its
first argument. Interfaces bind `InterfaceName-MethodName` the same way.

```scheme
(define-class Animal
  [name : String]
  (define (Speak) : String (string-append name " speaks")))

(define (describe [a : Animal]) : String
  (string-append (Animal-name a) ": " (Animal-Speak a)))
```

The deprecated `TypeName/member` spelling still resolves and reports `ZS0006` — see
[`define-record`](#field-accessors).

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
