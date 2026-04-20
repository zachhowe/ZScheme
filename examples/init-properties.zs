;; Init-only property setters on ZScheme-defined types
;;
;; Use the #:init modifier on fields to emit init-only property
;; setters, allowing object initializer syntax from consuming
;; C# code.

(namespace ZScheme.Examples)

(module init-properties)

;; Record with init-only property setters
(record Point [x : Int #:init] [y : Int #:init])

;; Class with init-only setters on immutable fields
(class Config
  [host : String #:init]
  [port : Int #:init])

(define (make-point) : Point
  (Point 10 20))

(define (make-config) : Config
  (Config "localhost" 8080))
