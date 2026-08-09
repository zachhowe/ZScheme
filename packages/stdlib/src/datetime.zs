;; datetime.zs — DateTime and TimeSpan utilities via CLR interop
(module datetime)

(import-clr
  System
  [now DateTime/Now
    :instance-property : (-> DateTime)]
  [utc-now DateTime/UtcNow
    :instance-property : (-> DateTime)]
  [millis DateTime.Millisecond
    :instance-property : (DateTime -> Int)]
  [datetime-subtract DateTime.Subtract
    :instance : (DateTime DateTime -> TimeSpan)]
  [timespan-total-seconds TimeSpan.TotalSeconds
    :instance-property : (TimeSpan -> Double)])

(export now utc-now millis datetime-subtract timespan-total-seconds)
