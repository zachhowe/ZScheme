# CLR Type Mapping

ZScript compiles to .NET and can interoperate directly with .NET types and methods. This document describes how CLR types map to ZScript types and the `import-clr` syntax for binding .NET APIs.

## Primitive Types

| CLR Type | ZScript Type |
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

These mappings are applied automatically when the compiler reflects on CLR method signatures.

## Collection Types

### Immutable Collections

ZScript's standard library wraps `System.Collections.Immutable` types with idiomatic ZScript interfaces.

| ZScript Type     | CLR Type                     | Module          |
|------------------|------------------------------|-----------------|
| `(List ^a)`      | `ImmutableList<T>`           | `stdlib/list`   |
| `(Array ^a)`     | `ImmutableArray<T>`          | `stdlib/array`  |
| `(Map ^k ^v)`    | `ImmutableDictionary<K,V>`   | `stdlib/map`    |

### Mutable Collections

When CLR methods return mutable collection types, the compiler automatically maps them:

| ZScript Type            | CLR Type           | Module                  |
|-------------------------|--------------------|-------------------------|
| `(Mutable-List ^a)`     | `List<T>`          | `stdlib/mutable-list`   |
| `(Mutable-Array ^a)`    | `T[]`              | `stdlib/mutable-array`  |
| `(Mutable-Map ^k ^v)`   | `Dictionary<K,V>`  | `stdlib/mutable-map`    |

## Other CLR Types

CLR types not in the tables above are represented by their fully qualified .NET name. For example, `System.Net.Http.HttpClient` is used directly as a ZScript type name:

```scheme
(define http-client (new System.Net.Http.HttpClient))

(define (apply-headers [msg : System.Net.Http.HttpRequestMessage]
                       [headers : (List (List String))]) : Unit
  ...)
```

## `import-clr` Syntax

The `import-clr` form binds .NET methods, properties, and indexers to ZScript function names.

### General Form

```scheme
(import-clr
  NamespaceHint1          ;; helps the runtime locate assemblies
  NamespaceHint2
  [alias QualifiedName flags... : TypeAnnotation])
```

Bare symbols (like `System.Collections.Immutable`) are namespace hints that tell the compiler where to search for assemblies. They are not bindings themselves.

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
[to-base64 System.Convert/ToBase64String : (Fn [(Mutable-Array Byte)] String)]
```

### Instance Methods

Syntax: `[alias Type.Method :instance : (Fn [SelfType args...] ReturnType)]`

The type and member name are separated by `.`. The first parameter in the type annotation is always the receiver object. A type annotation is required.

```scheme
[list-add-raw System.Collections.Immutable.ImmutableList.Add
  :instance : (Fn [(List ^a) ^a] (List ^a))]

[client-send-async System.Net.Http.HttpClient.SendAsync
  :instance : (Fn [System.Net.Http.HttpClient System.Net.Http.HttpRequestMessage]
                   (Task System.Net.Http.HttpResponseMessage))]
```

### Instance Properties

Syntax: `[alias Type.Property :instance-property : (Fn [SelfType] PropertyType)]`

```scheme
[list-count-raw System.Collections.Immutable.ImmutableList.Count
  :instance-property : (Fn [(List ^a)] Int)]

[response-status-code System.Net.Http.HttpResponseMessage.StatusCode
  :instance-property : (Fn [System.Net.Http.HttpResponseMessage] Int)]
```

### Instance Property Setters

Syntax: `[alias Type.Property :instance-property-set : (Fn [SelfType ValueType] Unit)]`

### Instance Indexers

Syntax: `[alias Type.Item :instance-indexer : (Fn [SelfType IndexType] ElementType)]`

```scheme
[list-item-raw System.Collections.Immutable.ImmutableList.Item
  :instance-indexer : (Fn [(List ^a) Int] ^a)]
```

### Instance Indexer Setters

Syntax: `[alias Type.Item :instance-indexer-set : (Fn [SelfType IndexType ValueType] Unit)]`

```scheme
[ml-set-item-raw System.Collections.Generic.List.Item
  :instance-indexer-set : (Fn [(Mutable-List ^a) Int ^a] Unit)]
```

## Generic Type Parameters

Generic CLR methods use type variables prefixed with `^` (e.g., `^a`, `^k`, `^v`). In `import-clr`, generic parameters appear after the qualified name and before the `:` type annotation:

```scheme
[create-list-from System.Collections.Immutable.ImmutableList/CreateRange ^a
  : (Fn [(List ^a)] (List ^a))]

[check-equal? Xunit.Assert/Equal ^a]
```

Type variables are also used in type annotations to express polymorphism:

```scheme
(Fn [(List ^a) ^a] (List ^a))    ;; ^a is the element type
(Fn [(Map ^k ^v) ^k] ^v)         ;; ^k is the key type, ^v is the value type
```

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

Example from the map module:

```scheme
(define (map/put [m : (Map ^k ^v)] [key : ^k] [val : ^v]) : (Map ^k ^v)
  :where (^k notnull)
  (map-set-raw m key val))
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

All CLR bindings are called as regular ZScript functions.

**Static methods** — called with arguments directly:

```scheme
(sqrt 2.0)          ;; System.Math.Sqrt(2.0)
(abs -42)           ;; System.Math.Abs(-42)
```

**Instance methods** — the receiver object is the first argument:

```scheme
(list-add-raw xs 42)              ;; xs.Add(42)
(client-send-async client msg)    ;; client.SendAsync(msg)
```

**Instance properties** — called like a single-argument function:

```scheme
(list-count-raw xs)               ;; xs.Count
(response-status-code resp)       ;; resp.StatusCode
```

**Instance indexers** — object and index as arguments:

```scheme
(list-item-raw xs 0)              ;; xs[0]
(ml-set-item-raw xs 0 99)         ;; xs[0] = 99
```

## Complete Example

This example shows a typical pattern: import CLR bindings as internal helpers, then expose idiomatic ZScript functions.

```scheme
(module list)

;; 1. Import CLR bindings (internal, not exported)
(import-clr
  System.Collections.Immutable
  [list-count-raw System.Collections.Immutable.ImmutableList.Count
    :instance-property : (Fn [(List ^a)] Int)]
  [list-item-raw System.Collections.Immutable.ImmutableList.Item
    :instance-indexer : (Fn [(List ^a) Int] ^a)]
  [list-add-raw System.Collections.Immutable.ImmutableList.Add
    :instance : (Fn [(List ^a) ^a] (List ^a))])

;; 2. Define idiomatic ZScript wrappers
(define (list/count [xs : (List ^a)]) : Int
  (list-count-raw xs))

(define (list/nth [xs : (List ^a)] [i : Int]) : ^a
  (list-item-raw xs i))

(define (list/append [xs : (List ^a)] [x : ^a]) : (List ^a)
  (list-add-raw xs x))

;; 3. Export the public API
(export list/count list/nth list/append)
```
