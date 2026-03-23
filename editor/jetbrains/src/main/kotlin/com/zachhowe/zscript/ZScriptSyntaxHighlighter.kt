package com.zachhowe.zscript

import com.intellij.lexer.Lexer
import com.intellij.openapi.editor.DefaultLanguageHighlighterColors
import com.intellij.openapi.editor.colors.TextAttributesKey
import com.intellij.openapi.editor.colors.TextAttributesKey.createTextAttributesKey
import com.intellij.openapi.fileTypes.SyntaxHighlighterBase
import com.intellij.psi.tree.IElementType

class ZScriptSyntaxHighlighter : SyntaxHighlighterBase() {
    companion object {
        val COMMENT = createTextAttributesKey("ZSCRIPT_COMMENT", DefaultLanguageHighlighterColors.LINE_COMMENT)
        val KEYWORD = createTextAttributesKey("ZSCRIPT_KEYWORD", DefaultLanguageHighlighterColors.KEYWORD)
        val OPERATOR = createTextAttributesKey("ZSCRIPT_OPERATOR", DefaultLanguageHighlighterColors.OPERATION_SIGN)
        val NUMBER = createTextAttributesKey("ZSCRIPT_NUMBER", DefaultLanguageHighlighterColors.NUMBER)
        val STRING = createTextAttributesKey("ZSCRIPT_STRING", DefaultLanguageHighlighterColors.STRING)
        val STRING_ESCAPE = createTextAttributesKey("ZSCRIPT_STRING_ESCAPE", DefaultLanguageHighlighterColors.VALID_STRING_ESCAPE)
        val PARENTHESES = createTextAttributesKey("ZSCRIPT_PARENTHESES", DefaultLanguageHighlighterColors.PARENTHESES)
        val BRACKETS = createTextAttributesKey("ZSCRIPT_BRACKETS", DefaultLanguageHighlighterColors.BRACKETS)
        val BUILTIN_TYPE = createTextAttributesKey("ZSCRIPT_TYPE", DefaultLanguageHighlighterColors.CLASS_NAME)
        val VALUE_CONSTRUCTOR = createTextAttributesKey("ZSCRIPT_CONSTRUCTOR", DefaultLanguageHighlighterColors.STATIC_FIELD)
        val TYPE_VARIABLE = createTextAttributesKey("ZSCRIPT_TYPE_VARIABLE", DefaultLanguageHighlighterColors.PARAMETER)
        val CLR_QUALIFIER = createTextAttributesKey("ZSCRIPT_CLR_QUALIFIER", DefaultLanguageHighlighterColors.METADATA)
        val ATTRIBUTE = createTextAttributesKey("ZSCRIPT_ATTRIBUTE", DefaultLanguageHighlighterColors.METADATA)
        val IDENTIFIER = createTextAttributesKey("ZSCRIPT_IDENTIFIER", DefaultLanguageHighlighterColors.IDENTIFIER)
        val WILDCARD = createTextAttributesKey("ZSCRIPT_WILDCARD", DefaultLanguageHighlighterColors.KEYWORD)
        val PUNCTUATION = createTextAttributesKey("ZSCRIPT_PUNCTUATION", DefaultLanguageHighlighterColors.OPERATION_SIGN)

        private val EMPTY = emptyArray<TextAttributesKey>()
    }

    override fun getHighlightingLexer(): Lexer = ZScriptLexerAdapter()

    override fun getTokenHighlights(tokenType: IElementType): Array<TextAttributesKey> = when (tokenType) {
        ZScriptTokenTypes.COMMENT -> arrayOf(COMMENT)

        ZScriptTokenTypes.KEYWORD,
        ZScriptTokenTypes.MODULE_KEYWORD,
        ZScriptTokenTypes.MANIFEST_KEYWORD -> arrayOf(KEYWORD)

        ZScriptTokenTypes.OPERATOR -> arrayOf(OPERATOR)

        ZScriptTokenTypes.INT_LIT,
        ZScriptTokenTypes.FLOAT_LIT -> arrayOf(NUMBER)

        ZScriptTokenTypes.BOOLEAN -> arrayOf(KEYWORD)

        ZScriptTokenTypes.STRING_OPEN,
        ZScriptTokenTypes.STRING_CLOSE,
        ZScriptTokenTypes.STRING_CONTENT -> arrayOf(STRING)

        ZScriptTokenTypes.STRING_ESCAPE -> arrayOf(STRING_ESCAPE)

        ZScriptTokenTypes.LPAREN,
        ZScriptTokenTypes.RPAREN -> arrayOf(PARENTHESES)

        ZScriptTokenTypes.LBRACKET,
        ZScriptTokenTypes.RBRACKET -> arrayOf(BRACKETS)

        ZScriptTokenTypes.BUILTIN_TYPE -> arrayOf(BUILTIN_TYPE)
        ZScriptTokenTypes.VALUE_CONSTRUCTOR -> arrayOf(VALUE_CONSTRUCTOR)
        ZScriptTokenTypes.TYPE_VARIABLE -> arrayOf(TYPE_VARIABLE)

        ZScriptTokenTypes.CLR_QUALIFIER -> arrayOf(CLR_QUALIFIER)
        ZScriptTokenTypes.ATTRIBUTE -> arrayOf(ATTRIBUTE)
        ZScriptTokenTypes.WILDCARD -> arrayOf(WILDCARD)

        ZScriptTokenTypes.COLON,
        ZScriptTokenTypes.DOT,
        ZScriptTokenTypes.ELLIPSIS,
        ZScriptTokenTypes.QUOTE,
        ZScriptTokenTypes.QUASIQUOTE,
        ZScriptTokenTypes.UNQUOTE,
        ZScriptTokenTypes.UNQUOTE_SPLICING -> arrayOf(PUNCTUATION)

        ZScriptTokenTypes.SYMBOL -> arrayOf(IDENTIFIER)

        else -> EMPTY
    }
}
