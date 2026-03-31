package com.zachhowe.zscheme

import com.intellij.lexer.Lexer
import com.intellij.openapi.editor.DefaultLanguageHighlighterColors
import com.intellij.openapi.editor.colors.TextAttributesKey
import com.intellij.openapi.editor.colors.TextAttributesKey.createTextAttributesKey
import com.intellij.openapi.fileTypes.SyntaxHighlighterBase
import com.intellij.psi.tree.IElementType

class ZSchemeSyntaxHighlighter : SyntaxHighlighterBase() {
    companion object {
        val COMMENT = createTextAttributesKey("ZSCHEME_COMMENT", DefaultLanguageHighlighterColors.LINE_COMMENT)
        val KEYWORD = createTextAttributesKey("ZSCHEME_KEYWORD", DefaultLanguageHighlighterColors.KEYWORD)
        val OPERATOR = createTextAttributesKey("ZSCHEME_OPERATOR", DefaultLanguageHighlighterColors.OPERATION_SIGN)
        val NUMBER = createTextAttributesKey("ZSCHEME_NUMBER", DefaultLanguageHighlighterColors.NUMBER)
        val STRING = createTextAttributesKey("ZSCHEME_STRING", DefaultLanguageHighlighterColors.STRING)
        val STRING_ESCAPE = createTextAttributesKey("ZSCHEME_STRING_ESCAPE", DefaultLanguageHighlighterColors.VALID_STRING_ESCAPE)
        val PARENTHESES = createTextAttributesKey("ZSCHEME_PARENTHESES", DefaultLanguageHighlighterColors.PARENTHESES)
        val BRACKETS = createTextAttributesKey("ZSCHEME_BRACKETS", DefaultLanguageHighlighterColors.BRACKETS)
        val BUILTIN_TYPE = createTextAttributesKey("ZSCHEME_TYPE", DefaultLanguageHighlighterColors.CLASS_NAME)
        val VALUE_CONSTRUCTOR = createTextAttributesKey("ZSCHEME_CONSTRUCTOR", DefaultLanguageHighlighterColors.STATIC_FIELD)
        val TYPE_VARIABLE = createTextAttributesKey("ZSCHEME_TYPE_VARIABLE", DefaultLanguageHighlighterColors.PARAMETER)
        val CLR_QUALIFIER = createTextAttributesKey("ZSCHEME_CLR_QUALIFIER", DefaultLanguageHighlighterColors.METADATA)
        val ATTRIBUTE = createTextAttributesKey("ZSCHEME_ATTRIBUTE", DefaultLanguageHighlighterColors.METADATA)
        val IDENTIFIER = createTextAttributesKey("ZSCHEME_IDENTIFIER", DefaultLanguageHighlighterColors.IDENTIFIER)
        val WILDCARD = createTextAttributesKey("ZSCHEME_WILDCARD", DefaultLanguageHighlighterColors.KEYWORD)
        val PUNCTUATION = createTextAttributesKey("ZSCHEME_PUNCTUATION", DefaultLanguageHighlighterColors.OPERATION_SIGN)

        private val EMPTY = emptyArray<TextAttributesKey>()
    }

    override fun getHighlightingLexer(): Lexer = ZSchemeLexerAdapter()

    override fun getTokenHighlights(tokenType: IElementType): Array<TextAttributesKey> = when (tokenType) {
        ZSchemeTokenTypes.COMMENT -> arrayOf(COMMENT)

        ZSchemeTokenTypes.KEYWORD,
        ZSchemeTokenTypes.MODULE_KEYWORD,
        ZSchemeTokenTypes.MANIFEST_KEYWORD -> arrayOf(KEYWORD)

        ZSchemeTokenTypes.OPERATOR -> arrayOf(OPERATOR)

        ZSchemeTokenTypes.INT_LIT,
        ZSchemeTokenTypes.FLOAT_LIT -> arrayOf(NUMBER)

        ZSchemeTokenTypes.BOOLEAN -> arrayOf(KEYWORD)

        ZSchemeTokenTypes.STRING_OPEN,
        ZSchemeTokenTypes.STRING_CLOSE,
        ZSchemeTokenTypes.STRING_CONTENT -> arrayOf(STRING)

        ZSchemeTokenTypes.STRING_ESCAPE -> arrayOf(STRING_ESCAPE)

        ZSchemeTokenTypes.LPAREN,
        ZSchemeTokenTypes.RPAREN -> arrayOf(PARENTHESES)

        ZSchemeTokenTypes.LBRACKET,
        ZSchemeTokenTypes.RBRACKET -> arrayOf(BRACKETS)

        ZSchemeTokenTypes.BUILTIN_TYPE -> arrayOf(BUILTIN_TYPE)
        ZSchemeTokenTypes.VALUE_CONSTRUCTOR -> arrayOf(VALUE_CONSTRUCTOR)
        ZSchemeTokenTypes.TYPE_VARIABLE -> arrayOf(TYPE_VARIABLE)

        ZSchemeTokenTypes.CLR_QUALIFIER -> arrayOf(CLR_QUALIFIER)
        ZSchemeTokenTypes.ATTRIBUTE -> arrayOf(ATTRIBUTE)
        ZSchemeTokenTypes.WILDCARD -> arrayOf(WILDCARD)

        ZSchemeTokenTypes.COLON,
        ZSchemeTokenTypes.DOT,
        ZSchemeTokenTypes.ELLIPSIS,
        ZSchemeTokenTypes.QUOTE,
        ZSchemeTokenTypes.QUASIQUOTE,
        ZSchemeTokenTypes.UNQUOTE,
        ZSchemeTokenTypes.UNQUOTE_SPLICING -> arrayOf(PUNCTUATION)

        ZSchemeTokenTypes.SYMBOL -> arrayOf(IDENTIFIER)

        else -> EMPTY
    }
}
