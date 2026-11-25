plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
}

android {
    // The application id must be the phone app's (com.brenpoly.cardboardxr), and both must be signed
    // with the same key: the Wearable Data Layer only delivers between apps that match.
    namespace = "com.brenpoly.cardboardxr.wear"
    compileSdk = 35

    defaultConfig {
        applicationId = "com.brenpoly.cardboardxr"
        minSdk = 30
        targetSdk = 35
        versionCode = 1
        versionName = "1.0"
    }

    buildTypes {
        release {
            isMinifyEnabled = false
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
    kotlinOptions {
        jvmTarget = "17"
    }
}

dependencies {
    implementation("com.google.android.gms:play-services-wearable:19.0.0")
    implementation("androidx.core:core-ktx:1.13.1")
    testImplementation("junit:junit:4.13.2")
}
