;; Union types, records, and pattern matching

(namespace ZScheme.Examples)

(import-clr System.Text.Json.Serialization)

(module json)

(record Point 
	[(@ JsonPropertyName "x_coord") x : Int] 
	[(@ JsonPropertyName "y_coord") y : Int])
