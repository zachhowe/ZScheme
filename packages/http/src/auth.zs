;; auth.zs — Authentication header helpers
(module auth)

(import-clr
  [to-base64 System.Convert/ToBase64String : (Fn [(Array Byte)] String)]
  [utf8-get-bytes System.Text.UTF8Encoding.GetBytes
    :instance : (Fn [System.Text.UTF8Encoding String] (Array Byte))])

;; Returns ("Authorization" "Basic <encoded>") header pair
(define (basic-auth [username : String] [password : String]) : (List String)
  (let [enc (new System.Text.UTF8Encoding)]
    (let [creds (string-append (string-append username ":") password)]
      (let [encoded (to-base64 (utf8-get-bytes enc creds))]
        (list "Authorization" (string-append "Basic " encoded))))))

;; Returns ("Authorization" "Bearer <token>") header pair
(define (bearer-auth [token : String]) : (List String)
  (list "Authorization" (string-append "Bearer " token)))

(export basic-auth bearer-auth)
