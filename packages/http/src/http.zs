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
  System.Net.Http

  ;; HttpClient convenience methods
  [client-send-async System.Net.Http.HttpClient.SendAsync
    :instance : (System.Net.Http.HttpClient System.Net.Http.HttpRequestMessage -> (Task System.Net.Http.HttpResponseMessage))]
  [client-post-async System.Net.Http.HttpClient.PostAsync
    :instance : (System.Net.Http.HttpClient String System.Net.Http.HttpContent -> (Task System.Net.Http.HttpResponseMessage))]
  [client-put-async System.Net.Http.HttpClient.PutAsync
    :instance : (System.Net.Http.HttpClient String System.Net.Http.HttpContent -> (Task System.Net.Http.HttpResponseMessage))]

  ;; Response accessors
  [response-status-code System.Net.Http.HttpResponseMessage.StatusCode
    :instance-property : (System.Net.Http.HttpResponseMessage -> Int)]
  [response-is-success System.Net.Http.HttpResponseMessage.IsSuccessStatusCode
    :instance-property : (System.Net.Http.HttpResponseMessage -> Bool)]
  [response-reason System.Net.Http.HttpResponseMessage.ReasonPhrase
    :instance-property : (System.Net.Http.HttpResponseMessage -> String)]
  [response-content System.Net.Http.HttpResponseMessage.Content
    :instance-property : (System.Net.Http.HttpResponseMessage -> System.Net.Http.HttpContent)]

  ;; Read body
  [content-read-string System.Net.Http.HttpContent.ReadAsStringAsync
    :instance : (System.Net.Http.HttpContent -> (Task String))]

  ;; Request headers
  [request-headers System.Net.Http.HttpRequestMessage.Headers
    :instance-property : (System.Net.Http.HttpRequestMessage -> System.Net.Http.Headers.HttpRequestHeaders)]
  [headers-add System.Net.Http.Headers.HttpRequestHeaders.TryAddWithoutValidation
    :instance : (System.Net.Http.Headers.HttpRequestHeaders String String -> Bool)])

;; Shared HttpClient instance
(define http-client (new System.Net.Http.HttpClient))

;; --- Internal helpers ---

;; Convert raw HttpResponseMessage to HttpResponse record
(define-async (raw->response [raw : System.Net.Http.HttpResponseMessage])
  : (Task HttpResponse)
  (let ([body (await (content-read-string (response-content raw)))])
    (HttpResponse
      (response-status-code raw)
      (response-reason raw)
      body
      (response-is-success raw))))

;; Apply headers from a list of pairs to an HttpRequestMessage
(define (apply-headers-loop [hdrs : System.Net.Http.Headers.HttpRequestHeaders]
                            [pairs : (TreeList (TreeList String))]
                            [i : Int] [len : Int]) : Unit
  (if (= i len) ()
    (let ([pair (treelist-ref pairs i)])
      (begin
        (headers-add hdrs (treelist-ref pair 0) (treelist-ref pair 1))
        (apply-headers-loop hdrs pairs (+ i 1) len)))))

(define (apply-headers [msg : System.Net.Http.HttpRequestMessage]
                       [headers : (TreeList (TreeList String))]) : Unit
  (apply-headers-loop (request-headers msg) headers 0 (treelist-length headers)))

;; Send request without body (GET, DELETE, HEAD, OPTIONS)
(define-async (send-no-body [method-str : String]
                            [url : String]
                            [headers : (TreeList (TreeList String))])
  : (Task HttpResponse)
  (let ([msg (new System.Net.Http.HttpRequestMessage (new System.Net.Http.HttpMethod method-str) url)])
    (begin
      (apply-headers msg headers)
      (let ([raw (await (client-send-async http-client msg))])
        (await (raw->response raw))))))

;; --- Public API ---

(define-async (http/get [url : String]
                        [headers : (TreeList (TreeList String))])
  : (Task (Result HttpResponse Error))
  (catch (await (send-no-body "GET" url headers))))

(define-async (http/post [url : String]
                         [body : String]
                         [content-type : String]
                         [headers : (TreeList (TreeList String))])
  : (Task (Result HttpResponse Error))
  (catch
    (let ([content (new System.Net.Http.StringContent body (new System.Text.UTF8Encoding) content-type)])
      (let ([raw (await (client-post-async http-client url content))])
        (await (raw->response raw))))))

(define-async (http/post-json [url : String]
                              [json-body : String]
                              [headers : (TreeList (TreeList String))])
  : (Task (Result HttpResponse Error))
  (catch
    (let ([content (new System.Net.Http.StringContent json-body (new System.Text.UTF8Encoding) "application/json")])
      (let ([raw (await (client-post-async http-client url content))])
        (await (raw->response raw))))))

(define-async (http/put [url : String]
                        [body : String]
                        [content-type : String]
                        [headers : (TreeList (TreeList String))])
  : (Task (Result HttpResponse Error))
  (catch
    (let ([content (new System.Net.Http.StringContent body (new System.Text.UTF8Encoding) content-type)])
      (let ([raw (await (client-put-async http-client url content))])
        (await (raw->response raw))))))

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
    (let ([content (new System.Net.Http.StringContent body (new System.Text.UTF8Encoding) content-type)])
      (let ([msg (new System.Net.Http.HttpRequestMessage (new System.Net.Http.HttpMethod "PATCH") url)])
        (begin
          (apply-headers msg headers)
          ;; Can't use client convenience methods for PATCH, use SendAsync
          ;; TODO: set msg.Content when InstancePropertySet is available
          (let ([raw (await (client-send-async http-client msg))])
            (await (raw->response raw))))))))

(export HttpResponse
        http/get http/post http/post-json http/put http/patch
        http/delete http/head http/options
        basic-auth bearer-auth)
