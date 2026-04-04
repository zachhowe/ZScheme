;; datetime.zs — DateTime and TimeSpan utilities via CLR interop
(module datetime)

(import-clr
  [utc-now System.DateTime/UtcNow
    :instance-property : (Fn [] System.DateTime)]
  [datetime-subtract System.DateTime.Subtract
    :instance : (Fn [System.DateTime System.DateTime] System.TimeSpan)]
  [timespan-total-seconds System.TimeSpan.TotalSeconds
    :instance-property : (Fn [System.TimeSpan] Double)])

(export utc-now datetime-subtract timespan-total-seconds)
