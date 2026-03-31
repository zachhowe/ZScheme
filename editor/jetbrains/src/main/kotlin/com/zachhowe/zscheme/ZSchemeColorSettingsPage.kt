package com.zachhowe.zscheme

import com.intellij.openapi.editor.colors.TextAttributesKey
import com.intellij.openapi.fileTypes.SyntaxHighlighter
import com.intellij.openapi.options.colors.AttributesDescriptor
import com.intellij.openapi.options.colors.ColorDescriptor
import com.intellij.openapi.options.colors.ColorSettingsPage
import javax.swing.Icon

class ZSchemeColorSettingsPage : ColorSettingsPage {
    companion object {
        private val DESCRIPTORS = arrayOf(
            AttributesDescriptor("Comment", ZSchemeSyntaxHighlighter.COMMENT),
            AttributesDescriptor("Keyword", ZSchemeSyntaxHighlighter.KEYWORD),
            AttributesDescriptor("Operator", ZSchemeSyntaxHighlighter.OPERATOR),
            AttributesDescriptor("Number", ZSchemeSyntaxHighlighter.NUMBER),
            AttributesDescriptor("String", ZSchemeSyntaxHighlighter.STRING),
            AttributesDescriptor("String escape", ZSchemeSyntaxHighlighter.STRING_ESCAPE),
            AttributesDescriptor("Parentheses", ZSchemeSyntaxHighlighter.PARENTHESES),
            AttributesDescriptor("Brackets", ZSchemeSyntaxHighlighter.BRACKETS),
            AttributesDescriptor("Built-in type", ZSchemeSyntaxHighlighter.BUILTIN_TYPE),
            AttributesDescriptor("Value constructor", ZSchemeSyntaxHighlighter.VALUE_CONSTRUCTOR),
            AttributesDescriptor("Type variable", ZSchemeSyntaxHighlighter.TYPE_VARIABLE),
            AttributesDescriptor("CLR qualifier", ZSchemeSyntaxHighlighter.CLR_QUALIFIER),
            AttributesDescriptor("Attribute", ZSchemeSyntaxHighlighter.ATTRIBUTE),
            AttributesDescriptor("Identifier", ZSchemeSyntaxHighlighter.IDENTIFIER),
            AttributesDescriptor("Wildcard", ZSchemeSyntaxHighlighter.WILDCARD),
            AttributesDescriptor("Punctuation", ZSchemeSyntaxHighlighter.PUNCTUATION),
        )
    }

    override fun getIcon(): Icon = ZSchemeIcons.FILE
    override fun getHighlighter(): SyntaxHighlighter = ZSchemeSyntaxHighlighter()
    override fun getDemoText(): String = """
; ZScheme example
(namespace ZScheme.Examples)
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
    override fun getDisplayName(): String = "ZScheme"
}
