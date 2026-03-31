package com.zachhowe.zscheme

import com.intellij.psi.TokenType
import com.intellij.psi.tree.IElementType
import com.intellij.psi.tree.TokenSet

object ZSchemeTokenTypes {
    // Structural
    @JvmField val LPAREN = IElementType("LPAREN", ZSchemeLanguage.INSTANCE)
    @JvmField val RPAREN = IElementType("RPAREN", ZSchemeLanguage.INSTANCE)
    @JvmField val LBRACKET = IElementType("LBRACKET", ZSchemeLanguage.INSTANCE)
    @JvmField val RBRACKET = IElementType("RBRACKET", ZSchemeLanguage.INSTANCE)
    @JvmField val COLON = IElementType("COLON", ZSchemeLanguage.INSTANCE)
    @JvmField val DOT = IElementType("DOT", ZSchemeLanguage.INSTANCE)

    // Reader macros
    @JvmField val QUOTE = IElementType("QUOTE", ZSchemeLanguage.INSTANCE)
    @JvmField val QUASIQUOTE = IElementType("QUASIQUOTE", ZSchemeLanguage.INSTANCE)
    @JvmField val UNQUOTE = IElementType("UNQUOTE", ZSchemeLanguage.INSTANCE)
    @JvmField val UNQUOTE_SPLICING = IElementType("UNQUOTE_SPLICING", ZSchemeLanguage.INSTANCE)

    // Literals
    @JvmField val INT_LIT = IElementType("INT_LIT", ZSchemeLanguage.INSTANCE)
    @JvmField val FLOAT_LIT = IElementType("FLOAT_LIT", ZSchemeLanguage.INSTANCE)
    @JvmField val BOOLEAN = IElementType("BOOLEAN", ZSchemeLanguage.INSTANCE)

    // Strings
    @JvmField val STRING_OPEN = IElementType("STRING_OPEN", ZSchemeLanguage.INSTANCE)
    @JvmField val STRING_CLOSE = IElementType("STRING_CLOSE", ZSchemeLanguage.INSTANCE)
    @JvmField val STRING_CONTENT = IElementType("STRING_CONTENT", ZSchemeLanguage.INSTANCE)
    @JvmField val STRING_ESCAPE = IElementType("STRING_ESCAPE", ZSchemeLanguage.INSTANCE)

    // Comments
    @JvmField val COMMENT = IElementType("COMMENT", ZSchemeLanguage.INSTANCE)

    // Keywords
    @JvmField val KEYWORD = IElementType("KEYWORD", ZSchemeLanguage.INSTANCE)
    @JvmField val MODULE_KEYWORD = IElementType("MODULE_KEYWORD", ZSchemeLanguage.INSTANCE)
    @JvmField val MANIFEST_KEYWORD = IElementType("MANIFEST_KEYWORD", ZSchemeLanguage.INSTANCE)

    // Operators
    @JvmField val OPERATOR = IElementType("OPERATOR", ZSchemeLanguage.INSTANCE)

    // Types and constructors
    @JvmField val BUILTIN_TYPE = IElementType("BUILTIN_TYPE", ZSchemeLanguage.INSTANCE)
    @JvmField val VALUE_CONSTRUCTOR = IElementType("VALUE_CONSTRUCTOR", ZSchemeLanguage.INSTANCE)
    @JvmField val TYPE_VARIABLE = IElementType("TYPE_VARIABLE", ZSchemeLanguage.INSTANCE)

    // Special
    @JvmField val CLR_QUALIFIER = IElementType("CLR_QUALIFIER", ZSchemeLanguage.INSTANCE)
    @JvmField val WILDCARD = IElementType("WILDCARD", ZSchemeLanguage.INSTANCE)
    @JvmField val ATTRIBUTE = IElementType("ATTRIBUTE", ZSchemeLanguage.INSTANCE)
    @JvmField val ELLIPSIS = IElementType("ELLIPSIS", ZSchemeLanguage.INSTANCE)

    // General identifier
    @JvmField val SYMBOL = IElementType("SYMBOL", ZSchemeLanguage.INSTANCE)

    // Token sets
    @JvmField val COMMENTS = TokenSet.create(COMMENT)
    @JvmField val STRINGS = TokenSet.create(STRING_OPEN, STRING_CLOSE, STRING_CONTENT, STRING_ESCAPE)
    @JvmField val WHITE_SPACES = TokenSet.create(TokenType.WHITE_SPACE)
}
