;; thread.zs — Thread utilities via CLR interop
(module thread)

(import-clr
  System.Threading
  [thread-sleep Thread.Sleep
    :static : (Int -> Unit)])

(thread-sleep 0)

(export thread-sleep)
