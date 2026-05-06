;; datetime.zs — DateTime and TimeSpan utilities via CLR interop
(module datetime)

(import-clr
  [utc-now System.DateTime/UtcNow
    :instance-property : (-> System.DateTime)]
  [datetime-subtract System.DateTime.Subtract
    :instance : (System.DateTime System.DateTime -> System.TimeSpan)]
  [timespan-total-seconds System.TimeSpan.TotalSeconds
    :instance-property : (System.TimeSpan -> Double)])

(export utc-now datetime-subtract timespan-total-seconds)
