;; math.zs — Math functions via CLR interop
(module math)

(import-clr
  [sqrt System.Math/Sqrt]
  [abs System.Math/Abs]
  [min System.Math/Min]
  [max System.Math/Max]
  [floor System.Math/Floor]
  [ceiling System.Math/Ceiling]
  [maxf System.Math/Max : (Float Float -> Float)]
  [minf System.Math/Min : (Float Float -> Float)])

(export sqrt abs min max floor ceiling maxf minf)
