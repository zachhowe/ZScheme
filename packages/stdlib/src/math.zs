;; math.zs — Math functions via CLR interop
(module math)

(import-clr
  System
  [sqrt Math/Sqrt]
  [abs Math/Abs]
  [min Math/Min]
  [max Math/Max]
  [floor Math/Floor]
  [ceiling Math/Ceiling]
  [maxf Math/Max : (Float Float -> Float)]
  [minf Math/Min : (Float Float -> Float)])

(export sqrt abs min max floor ceiling maxf minf)
