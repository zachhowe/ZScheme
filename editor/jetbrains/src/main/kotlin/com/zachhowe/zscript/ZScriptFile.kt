package com.zachhowe.zscript

import com.intellij.extapi.psi.PsiFileBase
import com.intellij.openapi.fileTypes.FileType
import com.intellij.psi.FileViewProvider

class ZScriptFile(viewProvider: FileViewProvider) : PsiFileBase(viewProvider, ZScriptLanguage.INSTANCE) {
    override fun getFileType(): FileType = ZScriptFileType.INSTANCE
    override fun toString(): String = "ZScript File"
}
