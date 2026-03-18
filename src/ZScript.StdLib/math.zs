;; math.zs — Math functions via CLR interop
(module math)

(import-clr
  [sqrt System.Math/Sqrt]
  [abs System.Math/Abs]
  [min System.Math/Min]
  [max System.Math/Max]
  [floor System.Math/Floor]
  [ceiling System.Math/Ceiling])

(export sqrt abs min max floor ceiling)
