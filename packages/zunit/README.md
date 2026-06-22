# ZUnit

xUnit-based testing framework for ZScheme with rackunit-style assertions.

## Installation

Add to your `package.zspkg` test dependencies:

```scheme
(test-dependencies
  (zscheme
    [zunit :local "../zunit"]))
```

## Import

```scheme
(import zunit)
```

ZUnit uses a default module, so no prefix is needed on assertions and macros.

## API Reference

### Assertions

| Function | Signature | Description |
|----------|-----------|-------------|
| `check-equal?` | `(check-equal? expected actual)` | Assert two values are equal |
| `check-not-equal?` | `(check-not-equal? expected actual)` | Assert two values are not equal |
| `check-true` | `(check-true expr)` | Assert expression is true |
| `check-false` | `(check-false expr)` | Assert expression is false |
| `check-pred` | `(check-pred pred value)` | Assert predicate holds for value |
| `check-not-false` | `(check-not-false expr)` | Assert expression is not false |
| `fail` | `(fail message)` | Unconditionally fail the test |

### Test Macros

| Macro | Description |
|-------|-------------|
| `test-case` | Define a single test (xUnit `[Fact]`) |
| `test-suite` | Group test cases into a class |
| `theory-case` | Parameterized test with `inline-data` (xUnit `[Theory]`) |
| `test-case-async` | Async test returning `Task` |
| `test-suite-async` | Group async test cases into a class |
| `theory-case-async` | Async parameterized test |

## Usage

### Basic Test Suite

```scheme
(import zunit)

(test-suite MathTests
  (test-case addition
    (check-equal? 3 (+ 1 2)))
  (test-case negative-result
    (check-true (< (- 1 5) 0))))
```

### Parameterized Tests

```scheme
(import zunit)

(theory-case double-is-correct ([x : Int] [expected : Int])
  (inline-data 1 2)
  (inline-data 3 6)
  (inline-data 5 10)
  (check-equal? expected (* x 2)))
```

### Async Tests

```scheme
(import zunit)

(test-suite-async AsyncTests
  (test-case-async fetch-completes
    (let ([result (await (some-async-op))])
      (check-true (result/ok? result)))))
```

## Dependencies

- **NuGet** — `xunit.v3.extensibility.core 3.2.2`, `xunit.v3.assert 3.2.2`
