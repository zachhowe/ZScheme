package com.zachhowe.zscheme

import com.intellij.codeInsight.editorActions.SimpleTokenSetQuoteHandler

class ZSchemeQuoteHandler : SimpleTokenSetQuoteHandler(
    ZSchemeTokenTypes.STRING_OPEN,
    ZSchemeTokenTypes.STRING_CLOSE,
    ZSchemeTokenTypes.STRING_CONTENT,
)
