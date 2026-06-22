# ZScheme HTTP

HTTP client library for ZScheme, inspired by Racket's http-easy.

## Installation

Add to your `package.zspkg` dependencies:

```scheme
(dependencies
  (zscheme
    [http :local "../http"]))
```

## Import

```scheme
(import http/http)
```

The `http/auth` module is re-exported through `http/http`, so `basic-auth` and `bearer-auth` are available after importing `http/http`.

## API Reference

### HttpResponse Record

| Field | Type | Description |
|-------|------|-------------|
| `status` | `Int` | HTTP status code |
| `reason` | `String` | Reason phrase |
| `body` | `String` | Response body |
| `success` | `Bool` | True if status is 2xx |

### HTTP Functions

All functions are async and return `(Task (Result HttpResponse Error))`.

| Function | Parameters |
|----------|------------|
| `http/get` | `url headers` |
| `http/post` | `url body content-type headers` |
| `http/post-json` | `url json-body headers` |
| `http/put` | `url body content-type headers` |
| `http/patch` | `url body content-type headers` |
| `http/delete` | `url headers` |
| `http/head` | `url headers` |
| `http/options` | `url headers` |

Headers are passed as `(List (List String))` — a list of two-element string lists (name, value).

### Auth Helpers

| Function | Signature | Returns |
|----------|-----------|---------|
| `basic-auth` | `(basic-auth username password)` | `("Authorization" "Basic <encoded>")` header pair |
| `bearer-auth` | `(bearer-auth token)` | `("Authorization" "Bearer <token>")` header pair |

## Usage

### GET Request

```scheme
(import http/http)
(import stdlib/list)
(import stdlib/result)

(define-async (fetch-page) : (Task Unit)
  (let ([result (await (http/get "https://example.com" (list)))])
    (match result
      [(Ok resp) (println (HttpResponse/body resp))]
      [(Err e)   (println (Error/message e))])))
```

### POST with JSON

```scheme
(import http/http)
(import stdlib/list)

(define-async (create-user) : (Task (Result HttpResponse Error))
  (http/post-json
    "https://api.example.com/users"
    "{\"name\": \"Alice\"}"
    (list (bearer-auth "my-token"))))
```

### Authenticated GET

```scheme
(import http/http)
(import stdlib/list)

(define-async (fetch-protected) : (Task (Result HttpResponse Error))
  (http/get
    "https://api.example.com/me"
    (list (basic-auth "user" "pass"))))
```

## Dependencies

- **ZScheme** — `stdlib`
