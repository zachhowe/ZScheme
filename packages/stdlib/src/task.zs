;; task.zs — Task utilities via CLR interop
(module task)

(import-clr
  [task-completed-task System.Threading.Tasks.Task/CompletedTask
    :instance-property : (-> System.Threading.Tasks.Task)])

(export task-completed-task)
