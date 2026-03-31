package com.zachhowe.zscheme

import com.intellij.lang.Language

class ZSchemeLanguage private constructor() : Language("ZScheme") {
    companion object {
        @JvmField
        val INSTANCE = ZSchemeLanguage()
    }
}
