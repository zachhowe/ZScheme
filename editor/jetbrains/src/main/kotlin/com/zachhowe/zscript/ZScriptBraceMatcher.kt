package com.zachhowe.zscript

import com.intellij.lang.BracePair
import com.intellij.lang.PairedBraceMatcher
import com.intellij.psi.PsiFile
import com.intellij.psi.tree.IElementType

class ZScriptBraceMatcher : PairedBraceMatcher {
    override fun getPairs(): Array<BracePair> = arrayOf(
        BracePair(ZScriptTokenTypes.LPAREN, ZScriptTokenTypes.RPAREN, true),
        BracePair(ZScriptTokenTypes.LBRACKET, ZScriptTokenTypes.RBRACKET, true),
    )

    override fun isPairedBracesAllowedBeforeType(lbraceType: IElementType, contextType: IElementType?): Boolean = true

    override fun getCodeConstructStart(file: PsiFile, openingBraceOffset: Int): Int = openingBraceOffset
}
