;; http.zs — HTTP client library inspired by Racket's http-easy
(module http)

(import stdlib/option)
(import stdlib/result)
(import stdlib/error)
(import stdlib/catch)
(import stdlib/treelist)
(import stdlib/mutable/vector)
(import http/response)
(import http/auth)

(import-clr
  System
  System.Net
  System.Net.Http
  System.Net.Http.Headers
  System.Text

  ;; HttpClient convenience methods
  [client-send-async HttpClient.SendAsync
    :instance : (HttpClient HttpRequestMessage -> (Task HttpResponseMessage))]
  [client-post-async HttpClient.PostAsync
    :instance : (HttpClient String HttpContent -> (Task HttpResponseMessage))]
  [client-put-async HttpClient.PutAsync
    :instance : (HttpClient String HttpContent -> (Task HttpResponseMessage))]

  ;; Response accessors
  ;; StatusCode is the HttpStatusCode enum; response-status-code (below) converts it to Int.
  [response-status-code-raw HttpResponseMessage.StatusCode
    :instance-property : (HttpResponseMessage -> HttpStatusCode)]
  [status-code->int Convert/ToInt32
    : (HttpStatusCode -> Int)]
  [response-is-success HttpResponseMessage.IsSuccessStatusCode
    :instance-property : (HttpResponseMessage -> Bool)]
  [response-reason HttpResponseMessage.ReasonPhrase
    :instance-property : (HttpResponseMessage -> String)]
  [response-content HttpResponseMessage.Content
    :instance-property : (HttpResponseMessage -> HttpContent)]

  ;; Read body
  [content-read-string HttpContent.ReadAsStringAsync
    :instance : (HttpContent -> (Task String))]

  ;; Request headers
  [request-headers HttpRequestMessage.Headers
    :instance-property : (HttpRequestMessage -> HttpRequestHeaders)]
  [headers-add HttpRequestHeaders.TryAddWithoutValidation
    :instance : (HttpRequestHeaders String String -> Bool)])

;; Shared HttpClient instance
(define http-client (new HttpClient))

;; --- Internal helpers ---

;; HttpResponseMessage.StatusCode is the HttpStatusCode enum; expose it as the numeric code.
(define (response-status-code [raw : HttpResponseMessage]) : Int
  (status-code->int (response-status-code-raw raw)))

;; Convert raw HttpResponseMessage to HttpResponse record
(define-async (raw->response [raw : HttpResponseMessage])
  : (Task HttpResponse)
  (let ([body (await (content-read-string (response-content raw)))])
    (HttpResponse
      (response-status-code raw)
      (response-reason raw)
      body
      (response-is-success raw))))

;; Apply headers from a list of pairs to an HttpRequestMessage
(define (apply-headers-loop [hdrs : HttpRequestHeaders]
                            [pairs : (TreeList (TreeList String))]
                            [i : Int] [len : Int]) : Unit
  (if (= i len) ()
    (let ([pair (treelist-ref pairs i)])
      (headers-add hdrs (treelist-ref pair 0) (treelist-ref pair 1))
      (apply-headers-loop hdrs pairs (+ i 1) len))))

(define (apply-headers [msg : HttpRequestMessage]
                       [headers : (TreeList (TreeList String))]) : Unit
  (apply-headers-loop (request-headers msg) headers 0 (treelist-length headers)))

;; Send request without body (GET, DELETE, HEAD, OPTIONS)
(define-async (send-no-body [method-str : String]
                            [url : String]
                            [headers : (TreeList (TreeList String))])
  : (Task HttpResponse)
  (let ([msg (new HttpRequestMessage (new HttpMethod method-str) url)])
    (apply-headers msg headers)
    (let ([raw (await (client-send-async http-client msg))])
      (await (raw->response raw)))))

;; --- Public API ---

(define-async (http/get [url : String]
                        [headers : (TreeList (TreeList String))])
  : (Task (Result HttpResponse Error))
  (catch (await (send-no-body "GET" url headers))))

;; TODO: headers are not applied on POST (client convenience methods can't
;; attach them) — restructure like http/patch to honor them.
(define-async (http/post [url : String]
                         [body : String]
                         [content-type : String]
                         [_headers : (TreeList (TreeList String))])
  : (Task (Result HttpResponse Error))
  (catch
    (let* ([content (new StringContent body (new UTF8Encoding) content-type)]
           [raw (await (client-post-async http-client url content))])
      (await (raw->response raw)))))

;; TODO: headers are not applied on POST (see http/post).
(define-async (http/post-json [url : String]
                              [json-body : String]
                              [_headers : (TreeList (TreeList String))])
  : (Task (Result HttpResponse Error))
  (catch
    (let* ([content (new StringContent json-body (new UTF8Encoding) "application/json")]
           [raw (await (client-post-async http-client url content))])
      (await (raw->response raw)))))

;; TODO: headers are not applied on PUT (see http/post).
(define-async (http/put [url : String]
                        [body : String]
                        [content-type : String]
                        [_headers : (TreeList (TreeList String))])
  : (Task (Result HttpResponse Error))
  (catch
    (let* ([content (new StringContent body (new UTF8Encoding) content-type)]
           [raw (await (client-put-async http-client url content))])
      (await (raw->response raw)))))

(define-async (http/delete [url : String]
                           [headers : (TreeList (TreeList String))])
  : (Task (Result HttpResponse Error))
  (catch (await (send-no-body "DELETE" url headers))))

(define-async (http/head [url : String]
                         [headers : (TreeList (TreeList String))])
  : (Task (Result HttpResponse Error))
  (catch (await (send-no-body "HEAD" url headers))))

(define-async (http/options [url : String]
                            [headers : (TreeList (TreeList String))])
  : (Task (Result HttpResponse Error))
  (catch (await (send-no-body "OPTIONS" url headers))))

(define-async (http/patch [url : String]
                          [body : String]
                          [content-type : String]
                          [headers : (TreeList (TreeList String))])
  : (Task (Result HttpResponse Error))
  (catch
    (let* ([_content (new StringContent body (new UTF8Encoding) content-type)]
           [msg (new HttpRequestMessage (new HttpMethod "PATCH") url)])
      (apply-headers msg headers)
      ;; Can't use client convenience methods for PATCH, use SendAsync
      ;; TODO: set msg.Content when InstancePropertySet is available
      (let ([raw (await (client-send-async http-client msg))])
        (await (raw->response raw))))))

(export HttpResponse
        http/get http/post http/post-json http/put http/patch
        http/delete http/head http/options
        basic-auth bearer-auth)
