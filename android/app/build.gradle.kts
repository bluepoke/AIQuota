plugins {
    alias(libs.plugins.android.application)
    alias(libs.plugins.kotlin.compose)
}

android {
    namespace = "eu.schicht8.aiquota"
    compileSdk = libs.versions.compileSdk.get().toInt()

    defaultConfig {
        applicationId = "eu.schicht8.aiquota"
        minSdk = libs.versions.minSdk.get().toInt()
        targetSdk = libs.versions.compileSdk.get().toInt()
        versionCode = 1
        versionName = "0.1.0"
    }

    buildTypes {
        release {
            isMinifyEnabled = false
        }
    }

    buildFeatures {
        compose = true
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    packaging {
        resources.excludes += listOf("META-INF/LICENSE*", "META-INF/NOTICE*")
    }
}

dependencies {
    implementation(project(":core"))
    implementation(libs.core.ktx)
    implementation(libs.kotlinx.coroutines.android)
    implementation(libs.lifecycle.viewmodel.compose)
    implementation(libs.lifecycle.runtime.compose)
    implementation(libs.activity.compose)
    implementation(libs.browser)
    implementation(platform(libs.compose.bom))
    implementation(libs.compose.material3)
    implementation(libs.compose.ui.tooling.preview)
    debugImplementation(libs.compose.ui.tooling)
}
