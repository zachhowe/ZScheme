package com.zachhowe.zscript

import com.intellij.lang.Language

class ZScriptLanguage private constructor() : Language("ZScript") {
    companion object {
        @JvmField
        val INSTANCE = ZScriptLanguage()
    }
}
