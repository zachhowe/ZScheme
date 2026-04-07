package com.zachhowe.zscheme;

import com.intellij.lexer.FlexLexer;
import com.intellij.psi.tree.IElementType;
import com.intellij.psi.TokenType;
import java.util.HashMap;
import java.util.Map;

%%

%class ZSchemeLexer
%implements FlexLexer
%unicode
%function advance
%type IElementType

%{
    private static final Map<String, IElementType> SYMBOL_MAP = new HashMap<>();

    static {
        // Keywords
        for (String kw : new String[]{
            "define", "define-async", "define-syntax", "let", "let*", "fn", "if", "match",
            "record", "union", "try", "catch", "with-handlers", "set!", "begin", "new", "raise", "await",
            "class", "interface", "syntax-rules", "object", "partial",
            "notnull", "struct", "unmanaged", "default", "values"
        }) {
            SYMBOL_MAP.put(kw, ZSchemeTokenTypes.KEYWORD);
        }

        // Module keywords
        for (String kw : new String[]{
            "namespace", "module", "import", "export", "import-clr"
        }) {
            SYMBOL_MAP.put(kw, ZSchemeTokenTypes.MODULE_KEYWORD);
        }

        // Manifest keywords
        for (String kw : new String[]{
            "package", "dependencies", "test-dependencies", "nuget", "build", "output", "backend",
            "stdlib", "ref", "name", "version", "entry"
        }) {
            SYMBOL_MAP.put(kw, ZSchemeTokenTypes.MANIFEST_KEYWORD);
        }

        // Booleans
        SYMBOL_MAP.put("#t", ZSchemeTokenTypes.BOOLEAN);
        SYMBOL_MAP.put("#f", ZSchemeTokenTypes.BOOLEAN);

        // Value constructors
        for (String vc : new String[]{"Some", "None", "Ok", "Err", "Error"}) {
            SYMBOL_MAP.put(vc, ZSchemeTokenTypes.VALUE_CONSTRUCTOR);
        }

        // Built-in types
        for (String bt : new String[]{
            "Int", "Float", "Bool", "String", "Unit", "List", "Vector", "Map",
            "Option", "Result", "Fn", "Task"
        }) {
            SYMBOL_MAP.put(bt, ZSchemeTokenTypes.BUILTIN_TYPE);
        }

        // Operators
        for (String op : new String[]{
            "|>", "<=", ">=", "!=", "<>", "+", "-", "*", "/", "%", "=", "<", ">",
            "and", "or", "not"
        }) {
            SYMBOL_MAP.put(op, ZSchemeTokenTypes.OPERATOR);
        }
    }

    private IElementType classifySymbol(String text) {
        IElementType type = SYMBOL_MAP.get(text);
        if (type != null) return type;

        if (text.equals("_")) return ZSchemeTokenTypes.WILDCARD;
        if (text.equals("@")) return ZSchemeTokenTypes.ATTRIBUTE;
        if (text.equals("?")) return ZSchemeTokenTypes.KEYWORD;
        if (text.equals("...")) return ZSchemeTokenTypes.ELLIPSIS;
        if (text.startsWith("^") && text.length() > 1 && Character.isLowerCase(text.charAt(1))) {
            return ZSchemeTokenTypes.TYPE_VARIABLE;
        }

        return ZSchemeTokenTypes.SYMBOL;
    }
%}

WHITE_SPACE     = [ \t\r\n]+
LINE_COMMENT    = ;[^\r\n]*
DIGIT           = [0-9]
FLOAT           = -?{DIGIT}+\.{DIGIT}+[fF]? | -?{DIGIT}+[fF] | \.{DIGIT}+[fF]?
INTEGER         = -?{DIGIT}+
SYM_START       = [a-zA-Z_+\-*/=<>!?&|%\^~#@]
SYM_CONTINUE    = [a-zA-Z_+\-*/=<>!?&|%\^~#@0-9.\->]
SYMBOL          = {SYM_START}{SYM_CONTINUE}*

%state STRING

%%

<YYINITIAL> {
    {WHITE_SPACE}                   { return TokenType.WHITE_SPACE; }
    {LINE_COMMENT}                  { return ZSchemeTokenTypes.COMMENT; }

    // CLR qualifiers (must precede bare colon)
    ":instance-property"            { return ZSchemeTokenTypes.CLR_QUALIFIER; }
    ":instance-indexer"             { return ZSchemeTokenTypes.CLR_QUALIFIER; }
    ":instance"                     { return ZSchemeTokenTypes.CLR_QUALIFIER; }
    ":where"                        { return ZSchemeTokenTypes.CLR_QUALIFIER; }

    // Structural tokens
    "("                             { return ZSchemeTokenTypes.LPAREN; }
    ")"                             { return ZSchemeTokenTypes.RPAREN; }
    "["                             { return ZSchemeTokenTypes.LBRACKET; }
    "]"                             { return ZSchemeTokenTypes.RBRACKET; }
    ":"                             { return ZSchemeTokenTypes.COLON; }
    "'"                             { return ZSchemeTokenTypes.QUOTE; }
    "`"                             { return ZSchemeTokenTypes.QUASIQUOTE; }
    ",@"                            { return ZSchemeTokenTypes.UNQUOTE_SPLICING; }
    ","                             { return ZSchemeTokenTypes.UNQUOTE; }

    // String start
    \"                              { yybegin(STRING); return ZSchemeTokenTypes.STRING_OPEN; }

    // Numeric literals (floats before integers, before dot)
    {FLOAT}                         { return ZSchemeTokenTypes.FLOAT_LIT; }
    {INTEGER}                       { return ZSchemeTokenTypes.INT_LIT; }

    // Dot (after float rules)
    "."                             { return ZSchemeTokenTypes.DOT; }

    // Symbols — classified by lookup
    {SYMBOL}                        { return classifySymbol(yytext().toString()); }

    // Fallback
    [^]                             { return TokenType.BAD_CHARACTER; }
}

<STRING> {
    \\[ntr\\\"]                     { return ZSchemeTokenTypes.STRING_ESCAPE; }
    \"                              { yybegin(YYINITIAL); return ZSchemeTokenTypes.STRING_CLOSE; }
    [^\\\"]+                        { return ZSchemeTokenTypes.STRING_CONTENT; }
    \\                              { return ZSchemeTokenTypes.STRING_CONTENT; }
}
