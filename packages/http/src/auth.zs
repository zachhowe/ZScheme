;; auth.zs — Authentication header helpers
(module auth)

(import stdlib/treelist
        stdlib/mutable/vector)

(import-clr
  [to-base64 System.Convert/ToBase64String : ((Mutable-Vector Byte) -> String)]
  [utf8-get-bytes System.Text.UTF8Encoding.GetBytes
    :instance : (System.Text.UTF8Encoding String -> (Mutable-Vector Byte))])

;; Returns ("Authorization" "Basic <encoded>") header pair
(define (basic-auth [username : String] [password : String]) : (TreeList String)
  (let* ([enc (new System.Text.UTF8Encoding)]
         [creds (string-append (string-append username ":") password)]
         [encoded (to-base64 (utf8-get-bytes enc creds))])
    (treelist "Authorization" (string-append "Basic " encoded))))

;; Returns ("Authorization" "Bearer <token>") header pair
(define (bearer-auth [token : String]) : (TreeList String)
  (treelist "Authorization" (string-append "Bearer " token)))

(export basic-auth bearer-auth)
