plugins {
    id("java")
    id("org.jetbrains.kotlin.jvm") version "2.1.0"
    id("org.jetbrains.intellij.platform") version "2.2.1"
    id("org.jetbrains.grammarkit") version "2022.3.2.2"
}

group = providers.gradleProperty("pluginGroup").get()
version = providers.gradleProperty("pluginVersion").get()

kotlin {
    jvmToolchain(providers.gradleProperty("javaVersion").get().toInt())
}

repositories {
    mavenCentral()
    intellijPlatform {
        defaultRepositories()
    }
}

dependencies {
    intellijPlatform {
        create(
            providers.gradleProperty("platformType"),
            providers.gradleProperty("platformVersion"),
        )
    }
}

sourceSets {
    main {
        java.srcDir("src/main/gen")
    }
}

tasks {
    generateLexer {
        sourceFile.set(file("src/main/resources/com/zachhowe/zscheme/ZScheme.flex"))
        targetOutputDir.set(file("src/main/gen/com/zachhowe/zscheme"))
        purgeOldFiles.set(true)
    }

    compileKotlin {
        dependsOn(generateLexer)
    }

    compileJava {
        dependsOn(generateLexer)
    }

    intellijPlatform {
        pluginConfiguration {
            id = providers.gradleProperty("pluginGroup")
            name = providers.gradleProperty("pluginName")
            version = providers.gradleProperty("pluginVersion")
            ideaVersion {
                sinceBuild = "243"
            }
        }
    }
}
