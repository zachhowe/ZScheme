;; task.zs — Task utilities via CLR interop
(module task)

;; The member path stays fully qualified: `Task` is on TypeNameCanonicalizer's
;; NeverCanonicalized list (with Object/ValueTuple/Clr-Array), so a namespace hint can
;; never complete it and ClrInterop.FindType would fail on the bare name.
(import-clr
  [task-completed-task System.Threading.Tasks.Task/CompletedTask
    :instance-property : (-> System.Threading.Tasks.Task)])

(export task-completed-task)
