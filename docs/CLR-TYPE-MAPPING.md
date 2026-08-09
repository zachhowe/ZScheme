# CLR Type Mapping

ZScheme compiles to .NET and can interoperate directly with .NET types and methods. This document describes how CLR types map to ZScheme types and the `import-clr` syntax for binding .NET APIs.

## Primitive Types

| CLR Type | ZScheme Type |
|----------|-------------|
| `int`    | `Int`       |
| `long`   | `Long`      |
| `float`  | `Float`     |
| `double` | `Double`    |
| `byte`   | `Byte`      |
| `char`   | `Char`      |
| `bool`   | `Bool`      |
| `string` | `String`    |
| `void`   | `Unit`      |
| `ZScheme.Runtime.ZSymbol` | `Symbol` |

These mappings are applied automatically when the compiler reflects on CLR method signatures.

They also run in the other direction: a type annotation may spell a primitive with its CLR full name instead of the ZScheme keyword, and the two are the same type. This lets an `import-clr` signature stay fully qualified throughout rather than switching spelling halfway:

```scheme
(import-clr [println System.Console/WriteLine : (System.String -> System.Void)])

(define (f [s : System.String]) : String s)   ;; System.String and String unify
```

The accepted CLR spellings are `System.Int32`, `System.Int64`, `System.Single`, `System.Double`, `System.Byte`, `System.Char`, `System.Boolean`, `System.String` and `System.Void`. `Symbol` is keyword-only — `ZSymbol` is a runtime type, not a CLR primitive.

`Symbol` is the only primitive backed by a non-corlib type: `ZScheme.Runtime.ZSymbol`, from the `ZScheme.Runtime` runtime-support assembly (see [SYNTAX-FORMS.md](SYNTAX-FORMS.md) for the `'symbol` literal syntax). Every compiled program references `ZScheme.Runtime` — the C# backend emits a `<Reference>` to it and the IL backend copies `ZScheme.Runtime.dll` next to the output.

## Collection Types

The six collection type names below are not built into the compiler — they are declared
in stdlib via `(define-type-alias ...)` (see [Syntax Forms](SYNTAX-FORMS.md#define-type-alias--map-a-zscheme-type-name-to-a-clr-type)).
The default prelude pulls in the stdlib modules that own them, so programs see these
aliases without any explicit import. To declare your own type alias for a third-party
CLR type, use the same form:

```scheme
(define-type-alias (BigList ^a)
  System.Collections.Immutable.ImmutableList :from "System.Collections.Immutable")
```

### Immutable Collections

ZScheme's standard library wraps `System.Collections.Immutable` types with idiomatic ZScheme interfaces.

| ZScheme Type     | CLR Type                     | Declared in              |
|------------------|------------------------------|--------------------------|
| `(TreeList ^a)`  | `ImmutableList<T>` (AVL)     | `stdlib/treelist`        |
| `(Vector ^a)`    | `ImmutableArray<T>`          | `stdlib/vector`          |
| `(Hash ^k ^v)`   | `ImmutableDictionary<K,V>`   | `stdlib/hash`            |
| `(List ^a)`      | union (`Nil` \| `Cons`)      | `stdlib/list`            |

### Mutable Collections

When CLR methods return mutable collection types, the compiler automatically maps them:

| ZScheme Type             | CLR Type           | Declared in                  |
|--------------------------|--------------------|------------------------------|
| `(Mutable-TreeList ^a)`  | `List<T>`          | `stdlib/mutable/treelist`    |
| `(Mutable-Vector ^a)`    | `T[]`              | `stdlib/mutable/vector`      |
| `(Mutable-Hash ^k ^v)`   | `Dictionary<K,V>`  | `stdlib/mutable/hash`        |

### Built-in Aliases

A few aliases are registered by the compiler itself (no import needed):

| ZScheme Type | CLR Type          | Notes                                                       |
|--------------|-------------------|-------------------------------------------------------------|
| `(Seq ^a)`   | `IEnumerable<T>`  | The honest type for members that return an enumerable view. |

`Seq` is the right annotation for `import-clr` members that return an enumerable rather
than a concrete collection — e.g. `Dictionary.Keys`/`.Values` (which return
`KeyCollection`/`ICollection<T>`/`IEnumerable<T>`). Materialize a `Seq` into a concrete
collection with `ImmutableList.CreateRange`, which accepts any `IEnumerable<T>`.

## Other CLR Types

CLR types not in the tables above are represented by their fully qualified .NET name. For example, `System.Net.Http.HttpClient` is used directly as a ZScheme type name:

```scheme
(define http-client (new System.Net.Http.HttpClient))

(define (apply-headers [msg : System.Net.Http.HttpRequestMessage]
                       [headers : (TreeList (TreeList String))]) : Unit
  ...)
```

## `import-clr` Syntax

The `import-clr` form binds .NET methods, properties, and indexers to ZScheme function names.

### General Form

```scheme
(import-clr
  NamespaceHint1          ;; helps the runtime locate assemblies
  NamespaceHint2
  [alias QualifiedName flags... : TypeAnnotation])
```

Bare symbols (like `System.Collections.Immutable`) are namespace hints that tell the compiler where to search for assemblies. They are not bindings themselves.

A hint also lets you drop the namespace from a name it covers, exactly like a C# `using` — in a type annotation *and* in the leading type of a binding's `QualifiedName`:

```scheme
(import-clr
  System.Text
  [sb-append StringBuilder.Append          ;; not System.Text.StringBuilder.Append
    :instance : (StringBuilder String -> StringBuilder)])
```

The short spelling resolves only through a namespace declared by an `import-clr` form — in this file or in a module it imports — and only when it is unambiguous; there is deliberately no blanket assembly scan. The language server flags a qualifier a hint makes redundant as `ZS0004`, with a "Simplify name to 'X'" quick fix.

### Static Methods

Syntax: `[alias Type/Method]`

The type and method name are separated by `/`. Type annotations are optional for static imports — the compiler infers types via reflection.

```scheme
(import-clr
  [sqrt System.Math/Sqrt]
  [abs System.Math/Abs]
  [min System.Math/Min]
  [max System.Math/Max])
```

When the compiler cannot pick the right overload automatically, add an explicit type annotation:

```scheme
[to-base64 System.Convert/ToBase64String : ((Mutable-Vector Byte) -> String)]
```

### Instance Methods

Syntax: `[alias Type.Method :instance : (SelfType args... -> ReturnType)]`

The type and member name are separated by `.`. The first parameter in the type annotation is always the receiver object. A type annotation is required.

```scheme
[treelist-add-raw System.Collections.Immutable.ImmutableList.Add
  :instance : ((TreeList ^a) ^a -> (TreeList ^a))]

[client-send-async System.Net.Http.HttpClient.SendAsync
  :instance : (System.Net.Http.HttpClient System.Net.Http.HttpRequestMessage -> (Task System.Net.Http.HttpResponseMessage))]
```

### Instance Properties

Syntax: `[alias Type.Property :instance-property : (SelfType -> PropertyType)]`

```scheme
[treelist-count-raw System.Collections.Immutable.ImmutableList.Count
  :instance-property : ((TreeList ^a) -> Int)]

[response-status-code System.Net.Http.HttpResponseMessage.StatusCode
  :instance-property : (System.Net.Http.HttpResponseMessage -> Int)]
```

### Instance Property Setters

Syntax: `[alias Type.Property :instance-property-set : (SelfType ValueType -> Unit)]`

### Instance Indexers

Syntax: `[alias Type.Item :instance-indexer : (SelfType IndexType -> ElementType)]`

```scheme
[treelist-item-raw System.Collections.Immutable.ImmutableList.Item
  :instance-indexer : ((TreeList ^a) Int -> ^a)]
```

### Instance Indexer Setters

Syntax: `[alias Type.Item :instance-indexer-set : (SelfType IndexType ValueType -> Unit)]`

```scheme
[ml-set-item-raw System.Collections.Generic.List.Item
  :instance-indexer-set : ((Mutable-TreeList ^a) Int ^a -> Unit)]
```

## Generic Type Parameters

Generic CLR methods use type variables prefixed with `^` (e.g., `^a`, `^k`, `^v`). In `import-clr`, generic parameters appear after the qualified name and before the `:` type annotation:

```scheme
[create-treelist-from System.Collections.Immutable.ImmutableList/CreateRange ^a
  : ((TreeList ^a) -> (TreeList ^a))]

[check-equal? Xunit.Assert/Equal ^a]
```

Type variables are also used in type annotations to express polymorphism:

```scheme
((TreeList ^a) ^a -> (TreeList ^a))    ;; ^a is the element type
((Hash ^k ^v) ^k -> ^v)                ;; ^k is the key type, ^v is the value type
```

### Annotation Validation

Annotated `import-clr` declarations are cross-checked against the CLR member they bind, and
a mismatch is a **compile error** at the import site — so a wrong annotation cannot silently
propagate and produce cascading, misleading errors downstream. The check is
**assignability-aware**: parameters are contravariant and the return type is covariant, so it
accepts a declared type that is narrower for a parameter or broader for a return (e.g. passing
a `TreeList` where the CLR wants an `IEnumerable`/`Seq`), tolerates optional/`params`
parameters being omitted, and allows delegate coercion — while still catching arity errors and
concretely mismatched types (including an enum member declared as `Int`).

Validation is unconditional; there is no flag to disable it. When the member cannot be
confidently resolved (or an overloaded method is ambiguous for the declared arity), the check
is skipped rather than guessing, so it never produces a false error.

## Generic Constraints

The `:where` clause constrains generic type parameters. It can be used on both `import-clr` bindings and `define` functions.

```scheme
;; Single constraint
:where (^k notnull)

;; Multiple parameters
:where ((^k notnull) (^v class))
```

Available constraints:

| Constraint   | CLR Equivalent        | Description                      |
|--------------|-----------------------|----------------------------------|
| `notnull`    | `where T : notnull`   | Must be a non-nullable type      |
| `struct`     | `where T : struct`    | Must be a value type             |
| `class`      | `where T : class`     | Must be a reference type         |
| `new`        | `where T : new()`     | Must have a parameterless ctor   |
| `unmanaged`  | `where T : unmanaged` | Must be an unmanaged type        |
| `default`    | `where T : default`   | Default constraint               |

Example from the hash module:

```scheme
(define (hash-set [h : (Hash ^k ^v)] [key : ^k] [val : ^v]) : (Hash ^k ^v)
  :where (^k notnull)
  (hash-set-raw h key val))
```

## Constructors

The `(new TypeName args...)` special form calls .NET constructors:

```scheme
;; No-argument constructor
(define http-client (new System.Net.Http.HttpClient))

;; With arguments
(new System.Net.Http.StringContent body (new System.Text.UTF8Encoding) content-type)

;; Nested constructors
(new System.Net.Http.HttpRequestMessage
  (new System.Net.Http.HttpMethod "PATCH") url)
```

## Calling Conventions

All CLR bindings are called as regular ZScheme functions.

**Static methods** — called with arguments directly:

```scheme
(sqrt 2.0)          ;; System.Math.Sqrt(2.0)
(abs -42)           ;; System.Math.Abs(-42)
```

**Instance methods** — the receiver object is the first argument:

```scheme
(treelist-add-raw xs 42)          ;; xs.Add(42)
(client-send-async client msg)    ;; client.SendAsync(msg)
```

**Instance properties** — called like a single-argument function:

```scheme
(treelist-count-raw xs)           ;; xs.Count
(response-status-code resp)       ;; resp.StatusCode
```

**Instance indexers** — object and index as arguments:

```scheme
(treelist-item-raw xs 0)          ;; xs[0]
(ml-set-item-raw xs 0 99)         ;; xs[0] = 99
```

## Complete Example

This example shows a typical pattern: import CLR bindings as internal helpers, then expose idiomatic ZScheme functions.

```scheme
(module treelist)

;; 1. Import CLR bindings (internal, not exported)
(import-clr
  System.Collections.Immutable
  [treelist-count-raw System.Collections.Immutable.ImmutableList.Count
    :instance-property : ((TreeList ^a) -> Int)]
  [treelist-item-raw System.Collections.Immutable.ImmutableList.Item
    :instance-indexer : ((TreeList ^a) Int -> ^a)]
  [treelist-add-raw System.Collections.Immutable.ImmutableList.Add
    :instance : ((TreeList ^a) ^a -> (TreeList ^a))])

;; 2. Define idiomatic ZScheme wrappers
(define (length [xs : (TreeList ^a)]) : Int
  (treelist-count-raw xs))

(define (list-ref [xs : (TreeList ^a)] [i : Int]) : ^a
  (treelist-item-raw xs i))

(define (append [xs : (TreeList ^a)] [x : ^a]) : (TreeList ^a)
  (treelist-add-raw xs x))

;; 3. Export the public API
(export length list-ref append)
```
