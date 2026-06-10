;; thread.zs — Thread utilities via CLR interop
(module thread)

(import-clr
  [thread-sleep System.Threading.Thread.Sleep
    :static : (Int -> Unit)])

(thread-sleep 0)

(export thread-sleep)
