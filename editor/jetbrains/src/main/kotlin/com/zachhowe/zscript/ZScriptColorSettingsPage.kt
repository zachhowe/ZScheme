package com.zachhowe.zscript

import com.intellij.openapi.editor.colors.TextAttributesKey
import com.intellij.openapi.fileTypes.SyntaxHighlighter
import com.intellij.openapi.options.colors.AttributesDescriptor
import com.intellij.openapi.options.colors.ColorDescriptor
import com.intellij.openapi.options.colors.ColorSettingsPage
import javax.swing.Icon

class ZScriptColorSettingsPage : ColorSettingsPage {
    companion object {
        private val DESCRIPTORS = arrayOf(
            AttributesDescriptor("Comment", ZScriptSyntaxHighlighter.COMMENT),
            AttributesDescriptor("Keyword", ZScriptSyntaxHighlighter.KEYWORD),
            AttributesDescriptor("Operator", ZScriptSyntaxHighlighter.OPERATOR),
            AttributesDescriptor("Number", ZScriptSyntaxHighlighter.NUMBER),
            AttributesDescriptor("String", ZScriptSyntaxHighlighter.STRING),
            AttributesDescriptor("String escape", ZScriptSyntaxHighlighter.STRING_ESCAPE),
            AttributesDescriptor("Parentheses", ZScriptSyntaxHighlighter.PARENTHESES),
            AttributesDescriptor("Brackets", ZScriptSyntaxHighlighter.BRACKETS),
            AttributesDescriptor("Built-in type", ZScriptSyntaxHighlighter.BUILTIN_TYPE),
            AttributesDescriptor("Value constructor", ZScriptSyntaxHighlighter.VALUE_CONSTRUCTOR),
            AttributesDescriptor("Type variable", ZScriptSyntaxHighlighter.TYPE_VARIABLE),
            AttributesDescriptor("CLR qualifier", ZScriptSyntaxHighlighter.CLR_QUALIFIER),
            AttributesDescriptor("Attribute", ZScriptSyntaxHighlighter.ATTRIBUTE),
            AttributesDescriptor("Identifier", ZScriptSyntaxHighlighter.IDENTIFIER),
            AttributesDescriptor("Wildcard", ZScriptSyntaxHighlighter.WILDCARD),
            AttributesDescriptor("Punctuation", ZScriptSyntaxHighlighter.PUNCTUATION),
        )
    }

    override fun getIcon(): Icon = ZScriptIcons.FILE
    override fun getHighlighter(): SyntaxHighlighter = ZScriptSyntaxHighlighter()
    override fun getDemoText(): String = """
; ZScript example
(namespace ZScript.Examples)
(module shapes)

(import list)

; Define a union type
(union Shape
  (Circle [radius : Float])
  (Rect [w : Int] [h : Int]))

; Define a function with type annotation
(define (describe [shape : Shape]) : String
  (match shape
    [(Circle r) (string-append "Circle with radius " (int->string r))]
    [(Rect w h) (string-append "Rectangle " (int->string w) "x" (int->string h))]
    [_ "Unknown shape"]))

; Use value constructors and pipe operator
(define (main)
  (let* [shapes (list (Circle 5.0) (Rect 3 4))]
    (|> shapes
        (list/map describe)
        (list/map println))))

; Option and Result types
(define (safe-divide [a : Int] [b : Int]) : (Result Int String)
  (if (= b 0)
    (Err "Division by zero")
    (Ok (/ a b))))

; CLR interop
(import-clr :instance "System.String" "ToUpper" string-upper)

; Type variable example
(define (identity [x : ^a]) : ^a x)

; Generic constraint with where clause
(define (ensure-value [x : ^a]) : ^a :where (^a notnull) x)

; Boolean and numeric literals
(define pi 3.14159f)
(define enabled #t)
(define count 42)
""".trimIndent()

    override fun getAdditionalHighlightingTagToDescriptorMap(): Map<String, TextAttributesKey>? = null
    override fun getAttributeDescriptors(): Array<AttributesDescriptor> = DESCRIPTORS
    override fun getColorDescriptors(): Array<ColorDescriptor> = ColorDescriptor.EMPTY_ARRAY
    override fun getDisplayName(): String = "ZScript"
}
