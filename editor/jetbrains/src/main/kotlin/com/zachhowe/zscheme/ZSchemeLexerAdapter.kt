package com.zachhowe.zscheme

import com.intellij.lexer.FlexAdapter

class ZSchemeLexerAdapter : FlexAdapter(ZSchemeLexer(null))
