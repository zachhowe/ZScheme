package com.zachhowe.zscheme

import com.intellij.lang.BracePair
import com.intellij.lang.PairedBraceMatcher
import com.intellij.psi.PsiFile
import com.intellij.psi.tree.IElementType

class ZSchemeBraceMatcher : PairedBraceMatcher {
    override fun getPairs(): Array<BracePair> = arrayOf(
        BracePair(ZSchemeTokenTypes.LPAREN, ZSchemeTokenTypes.RPAREN, true),
        BracePair(ZSchemeTokenTypes.LBRACKET, ZSchemeTokenTypes.RBRACKET, true),
    )

    override fun isPairedBracesAllowedBeforeType(lbraceType: IElementType, contextType: IElementType?): Boolean = true

    override fun getCodeConstructStart(file: PsiFile, openingBraceOffset: Int): Int = openingBraceOffset
}
