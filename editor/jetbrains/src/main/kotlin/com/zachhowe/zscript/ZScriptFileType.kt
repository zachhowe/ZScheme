package com.zachhowe.zscript

import com.intellij.openapi.fileTypes.LanguageFileType
import javax.swing.Icon

class ZScriptFileType private constructor() : LanguageFileType(ZScriptLanguage.INSTANCE) {
    companion object {
        @JvmField
        val INSTANCE = ZScriptFileType()
    }

    override fun getName(): String = "ZScript"
    override fun getDescription(): String = "ZScript language file"
    override fun getDefaultExtension(): String = "zs"
    override fun getIcon(): Icon = ZScriptIcons.FILE
}
