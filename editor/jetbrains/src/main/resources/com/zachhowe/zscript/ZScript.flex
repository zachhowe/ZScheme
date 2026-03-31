package com.zachhowe.zscript;

import com.intellij.lexer.FlexLexer;
import com.intellij.psi.tree.IElementType;
import com.intellij.psi.TokenType;
import java.util.HashMap;
import java.util.Map;

%%

%class ZScriptLexer
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
            "record", "union", "try", "catch", "begin", "new", "raise", "await",
            "class", "interface", "syntax-rules", "object", "partial",
            "notnull", "struct", "unmanaged", "default"
        }) {
            SYMBOL_MAP.put(kw, ZScriptTokenTypes.KEYWORD);
        }

        // Module keywords
        for (String kw : new String[]{
            "namespace", "module", "import", "export", "import-clr"
        }) {
            SYMBOL_MAP.put(kw, ZScriptTokenTypes.MODULE_KEYWORD);
        }

        // Manifest keywords
        for (String kw : new String[]{
            "package", "dependencies", "nuget", "build", "output", "backend",
            "stdlib", "ref", "name", "version", "entry"
        }) {
            SYMBOL_MAP.put(kw, ZScriptTokenTypes.MANIFEST_KEYWORD);
        }

        // Booleans
        SYMBOL_MAP.put("true", ZScriptTokenTypes.BOOLEAN);
        SYMBOL_MAP.put("false", ZScriptTokenTypes.BOOLEAN);
        SYMBOL_MAP.put("#t", ZScriptTokenTypes.BOOLEAN);
        SYMBOL_MAP.put("#f", ZScriptTokenTypes.BOOLEAN);

        // Value constructors
        for (String vc : new String[]{"Some", "None", "Ok", "Err", "Error"}) {
            SYMBOL_MAP.put(vc, ZScriptTokenTypes.VALUE_CONSTRUCTOR);
        }

        // Built-in types
        for (String bt : new String[]{
            "Int", "Float", "Bool", "String", "Unit", "List", "Vector", "Map",
            "Option", "Result", "Fn", "Task"
        }) {
            SYMBOL_MAP.put(bt, ZScriptTokenTypes.BUILTIN_TYPE);
        }

        // Operators
        for (String op : new String[]{
            "|>", "<=", ">=", "!=", "<>", "+", "-", "*", "/", "%", "=", "<", ">",
            "and", "or", "not"
        }) {
            SYMBOL_MAP.put(op, ZScriptTokenTypes.OPERATOR);
        }
    }

    private IElementType classifySymbol(String text) {
        IElementType type = SYMBOL_MAP.get(text);
        if (type != null) return type;

        if (text.equals("_")) return ZScriptTokenTypes.WILDCARD;
        if (text.equals("@")) return ZScriptTokenTypes.ATTRIBUTE;
        if (text.equals("?")) return ZScriptTokenTypes.KEYWORD;
        if (text.equals("...")) return ZScriptTokenTypes.ELLIPSIS;
        if (text.startsWith("^") && text.length() > 1 && Character.isLowerCase(text.charAt(1))) {
            return ZScriptTokenTypes.TYPE_VARIABLE;
        }

        return ZScriptTokenTypes.SYMBOL;
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
    {LINE_COMMENT}                  { return ZScriptTokenTypes.COMMENT; }

    // CLR qualifiers (must precede bare colon)
    ":instance-property"            { return ZScriptTokenTypes.CLR_QUALIFIER; }
    ":instance-indexer"             { return ZScriptTokenTypes.CLR_QUALIFIER; }
    ":instance"                     { return ZScriptTokenTypes.CLR_QUALIFIER; }
    ":where"                        { return ZScriptTokenTypes.CLR_QUALIFIER; }

    // Structural tokens
    "("                             { return ZScriptTokenTypes.LPAREN; }
    ")"                             { return ZScriptTokenTypes.RPAREN; }
    "["                             { return ZScriptTokenTypes.LBRACKET; }
    "]"                             { return ZScriptTokenTypes.RBRACKET; }
    ":"                             { return ZScriptTokenTypes.COLON; }
    "'"                             { return ZScriptTokenTypes.QUOTE; }
    "`"                             { return ZScriptTokenTypes.QUASIQUOTE; }
    ",@"                            { return ZScriptTokenTypes.UNQUOTE_SPLICING; }
    ","                             { return ZScriptTokenTypes.UNQUOTE; }

    // String start
    \"                              { yybegin(STRING); return ZScriptTokenTypes.STRING_OPEN; }

    // Numeric literals (floats before integers, before dot)
    {FLOAT}                         { return ZScriptTokenTypes.FLOAT_LIT; }
    {INTEGER}                       { return ZScriptTokenTypes.INT_LIT; }

    // Dot (after float rules)
    "."                             { return ZScriptTokenTypes.DOT; }

    // Symbols — classified by lookup
    {SYMBOL}                        { return classifySymbol(yytext().toString()); }

    // Fallback
    [^]                             { return TokenType.BAD_CHARACTER; }
}

<STRING> {
    \\[ntr\\\"]                     { return ZScriptTokenTypes.STRING_ESCAPE; }
    \"                              { yybegin(YYINITIAL); return ZScriptTokenTypes.STRING_CLOSE; }
    [^\\\"]+                        { return ZScriptTokenTypes.STRING_CONTENT; }
    \\                              { return ZScriptTokenTypes.STRING_CONTENT; }
}
