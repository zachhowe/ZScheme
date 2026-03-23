package com.zachhowe.zscript

import com.intellij.codeInsight.editorActions.SimpleTokenSetQuoteHandler

class ZScriptQuoteHandler : SimpleTokenSetQuoteHandler(
    ZScriptTokenTypes.STRING_OPEN,
    ZScriptTokenTypes.STRING_CLOSE,
    ZScriptTokenTypes.STRING_CONTENT,
)
