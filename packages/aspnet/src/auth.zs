;; auth.zs — authentication / authorization middleware factories
(module auth)

(import stdlib/string)
(import stdlib/mutable/vector)
(import aspnet/request)
(import aspnet/response)

(import-clr
  [from-base64 System.Convert/FromBase64String : (String -> (Mutable-Vector Byte))]
  [utf8-get-string System.Text.UTF8Encoding.GetString
    :instance : (System.Text.UTF8Encoding (Mutable-Vector Byte) -> String)]
  [str-index-of System.String.IndexOf :instance : (String String -> Int)]
  [str-substring-from System.String.Substring :instance : (String Int -> String)]
  [str-substring-range System.String.Substring :instance : (String Int Int -> String)])

;; Validate an HTTP Basic Authorization header value against expected credentials.
;; Returns true only for a well-formed `Basic <base64(user:pass)>` whose decoded
;; username and password match. Any malformed/missing header returns false.
(define (auth/check-basic [auth-header : String] [user : String] [pass : String]) : Bool
  (if (starts-with? auth-header "Basic ")
      ;; Invalid base64 throws FormatException; treat as a failed match.
      (with-handlers ([System.FormatException __e] #f)
        (let* ([enc (new System.Text.UTF8Encoding)]
               [decoded (utf8-get-string enc (from-base64 (str-substring-from auth-header 6)))]
               [sep (str-index-of decoded ":")])
          (if (< sep 0)
              #f
              (if (equals? (str-substring-range decoded 0 sep) user)
                  (equals? (str-substring-from decoded (+ sep 1)) pass)
                  #f))))
      #f))

;; Build a middleware that requires Authorization: Bearer <token>.
;; On match: invokes next. On mismatch: 401 + plain-text body.
(define (auth/require-bearer [token : String])
  : (Microsoft.AspNetCore.Http.HttpContext (-> Task) -> Task)
  (let ([expected (string-append "Bearer " token)])
    (lambda ([ctx : Microsoft.AspNetCore.Http.HttpContext]
             [next : (-> Task)])
      (let ([provided (request/header ctx "Authorization" "")])
        (if (equals? provided expected)
            (next)
            (begin
              (response/status-set ctx 401)
              (response/write-string ctx "unauthorized")))))))

;; Build a middleware that requires Authorization: Basic <base64(user:pass)>.
;; On match: invokes next. On mismatch: 401 + plain-text body.
(define (auth/require-basic [user : String] [pass : String])
  : (Microsoft.AspNetCore.Http.HttpContext (-> Task) -> Task)
  (lambda ([ctx : Microsoft.AspNetCore.Http.HttpContext]
           [next : (-> Task)])
    (let ([provided (request/header ctx "Authorization" "")])
      (if (auth/check-basic provided user pass)
          (next)
          (begin
            (response/status-set ctx 401)
            (response/write-string ctx "unauthorized"))))))

(export auth/require-bearer auth/require-basic)
