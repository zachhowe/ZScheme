;; datetime.zs — DateTime and TimeSpan utilities via CLR interop
(module datetime)

(import-clr
  [now System.DateTime/Now
    :instance-property : (-> System.DateTime)]
  [utc-now System.DateTime/UtcNow
    :instance-property : (-> System.DateTime)]
  [millis System.DateTime.Millisecond
    :instance-property : (System.DateTime -> Int)]
  [datetime-subtract System.DateTime.Subtract
    :instance : (System.DateTime System.DateTime -> System.TimeSpan)]
  [timespan-total-seconds System.TimeSpan.TotalSeconds
    :instance-property : (System.TimeSpan -> Double)])

(export now utc-now millis datetime-subtract timespan-total-seconds)
