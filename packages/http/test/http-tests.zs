;; http-tests.zs — Tests for the HTTP client library
(namespace ZScheme.Http.Tests)
(module http-tests)

(import zunit)
(import stdlib/treelist)
(import stdlib/result)
(import http/auth)
(import http/response)

(test-suite AuthTests
  (test-case bearer_auth_creates_header
    (let [header (bearer-auth "my-token")]
      (begin
        (check-equal? "Authorization" (list-ref header 0))
        (check-equal? "Bearer my-token" (list-ref header 1)))))

  (test-case basic_auth_creates_header
    (let [header (basic-auth "user" "pass")]
      (begin
        (check-equal? "Authorization" (list-ref header 0))
        ;; "user:pass" in base64 is "dXNlcjpwYXNz"
        (check-equal? "Basic dXNlcjpwYXNz" (list-ref header 1))))))

(test-suite ResponseTests
  (test-case response_fields_accessible
    (let [resp (HttpResponse 200 "OK" "hello" #t)]
      (begin
        (check-equal? 200 (HttpResponse/status resp))
        (check-equal? "OK" (HttpResponse/reason resp))
        (check-equal? "hello" (HttpResponse/body resp))
        (check-true (HttpResponse/success resp))))))
