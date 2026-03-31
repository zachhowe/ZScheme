package com.zachhowe.zscheme

import com.intellij.openapi.fileTypes.LanguageFileType
import javax.swing.Icon

class ZSchemeFileType private constructor() : LanguageFileType(ZSchemeLanguage.INSTANCE) {
    companion object {
        @JvmField
        val INSTANCE = ZSchemeFileType()
    }

    override fun getName(): String = "ZScheme"
    override fun getDescription(): String = "ZScheme language file"
    override fun getDefaultExtension(): String = "zs"
    override fun getIcon(): Icon = ZSchemeIcons.FILE
}
