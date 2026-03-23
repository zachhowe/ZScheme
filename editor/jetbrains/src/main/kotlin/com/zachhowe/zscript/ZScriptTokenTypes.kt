package com.zachhowe.zscript

import com.intellij.psi.TokenType
import com.intellij.psi.tree.IElementType
import com.intellij.psi.tree.TokenSet

object ZScriptTokenTypes {
    // Structural
    @JvmField val LPAREN = IElementType("LPAREN", ZScriptLanguage.INSTANCE)
    @JvmField val RPAREN = IElementType("RPAREN", ZScriptLanguage.INSTANCE)
    @JvmField val LBRACKET = IElementType("LBRACKET", ZScriptLanguage.INSTANCE)
    @JvmField val RBRACKET = IElementType("RBRACKET", ZScriptLanguage.INSTANCE)
    @JvmField val COLON = IElementType("COLON", ZScriptLanguage.INSTANCE)
    @JvmField val DOT = IElementType("DOT", ZScriptLanguage.INSTANCE)

    // Reader macros
    @JvmField val QUOTE = IElementType("QUOTE", ZScriptLanguage.INSTANCE)
    @JvmField val QUASIQUOTE = IElementType("QUASIQUOTE", ZScriptLanguage.INSTANCE)
    @JvmField val UNQUOTE = IElementType("UNQUOTE", ZScriptLanguage.INSTANCE)
    @JvmField val UNQUOTE_SPLICING = IElementType("UNQUOTE_SPLICING", ZScriptLanguage.INSTANCE)

    // Literals
    @JvmField val INT_LIT = IElementType("INT_LIT", ZScriptLanguage.INSTANCE)
    @JvmField val FLOAT_LIT = IElementType("FLOAT_LIT", ZScriptLanguage.INSTANCE)
    @JvmField val BOOLEAN = IElementType("BOOLEAN", ZScriptLanguage.INSTANCE)

    // Strings
    @JvmField val STRING_OPEN = IElementType("STRING_OPEN", ZScriptLanguage.INSTANCE)
    @JvmField val STRING_CLOSE = IElementType("STRING_CLOSE", ZScriptLanguage.INSTANCE)
    @JvmField val STRING_CONTENT = IElementType("STRING_CONTENT", ZScriptLanguage.INSTANCE)
    @JvmField val STRING_ESCAPE = IElementType("STRING_ESCAPE", ZScriptLanguage.INSTANCE)

    // Comments
    @JvmField val COMMENT = IElementType("COMMENT", ZScriptLanguage.INSTANCE)

    // Keywords
    @JvmField val KEYWORD = IElementType("KEYWORD", ZScriptLanguage.INSTANCE)
    @JvmField val MODULE_KEYWORD = IElementType("MODULE_KEYWORD", ZScriptLanguage.INSTANCE)
    @JvmField val MANIFEST_KEYWORD = IElementType("MANIFEST_KEYWORD", ZScriptLanguage.INSTANCE)

    // Operators
    @JvmField val OPERATOR = IElementType("OPERATOR", ZScriptLanguage.INSTANCE)

    // Types and constructors
    @JvmField val BUILTIN_TYPE = IElementType("BUILTIN_TYPE", ZScriptLanguage.INSTANCE)
    @JvmField val VALUE_CONSTRUCTOR = IElementType("VALUE_CONSTRUCTOR", ZScriptLanguage.INSTANCE)
    @JvmField val TYPE_VARIABLE = IElementType("TYPE_VARIABLE", ZScriptLanguage.INSTANCE)

    // Special
    @JvmField val CLR_QUALIFIER = IElementType("CLR_QUALIFIER", ZScriptLanguage.INSTANCE)
    @JvmField val WILDCARD = IElementType("WILDCARD", ZScriptLanguage.INSTANCE)
    @JvmField val ATTRIBUTE = IElementType("ATTRIBUTE", ZScriptLanguage.INSTANCE)
    @JvmField val ELLIPSIS = IElementType("ELLIPSIS", ZScriptLanguage.INSTANCE)

    // General identifier
    @JvmField val SYMBOL = IElementType("SYMBOL", ZScriptLanguage.INSTANCE)

    // Token sets
    @JvmField val COMMENTS = TokenSet.create(COMMENT)
    @JvmField val STRINGS = TokenSet.create(STRING_OPEN, STRING_CLOSE, STRING_CONTENT, STRING_ESCAPE)
    @JvmField val WHITE_SPACES = TokenSet.create(TokenType.WHITE_SPACE)
}
