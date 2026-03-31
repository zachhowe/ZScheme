package com.zachhowe.zscheme

import com.intellij.extapi.psi.PsiFileBase
import com.intellij.openapi.fileTypes.FileType
import com.intellij.psi.FileViewProvider

class ZSchemeFile(viewProvider: FileViewProvider) : PsiFileBase(viewProvider, ZSchemeLanguage.INSTANCE) {
    override fun getFileType(): FileType = ZSchemeFileType.INSTANCE
    override fun toString(): String = "ZScheme File"
}
