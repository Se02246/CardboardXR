using System.IO;
using System.Text.RegularExpressions;
using UnityEditor.Android;
using UnityEngine;

namespace ProjectBootstrap
{
    // Adds Play services' Wearable Data Layer to the generated unityLibrary module, for the Pixel
    // Watch bridge (Plugins/Android/WatchBridge.java). The watch app is in WearApp/.
    class WatchDependency : IPostGenerateGradleAndroidProject
    {
        const string k_Dependency = "implementation 'com.google.android.gms:play-services-wearable:19.0.0'";

        public int callbackOrder => 0;

        // path is the generated unityLibrary module.
        public void OnPostGenerateGradleAndroidProject(string path)
        {
            var gradlePath = Path.Combine(path, "build.gradle");
            if (!File.Exists(gradlePath))
            {
                Debug.LogError($"[Watch] No {gradlePath}: the watch bridge won't build");
                return;
            }
            var gradle = File.ReadAllText(gradlePath);
            if (gradle.Contains("play-services-wearable"))
                return;
            // The module's own dependencies block, at the start of a line (not buildscript's).
            var block = new Regex(@"^dependencies\s*\{", RegexOptions.Multiline).Match(gradle);
            if (!block.Success)
            {
                Debug.LogError("[Watch] No dependencies block in unityLibrary's build.gradle: the watch bridge won't build");
                return;
            }
            var at = block.Index + block.Length;
            gradle = gradle.Insert(at, "\n    " + k_Dependency);
            File.WriteAllText(gradlePath, gradle);

            // Unity reaches the bridge by name through JNI, so a minified build must keep it.
            var proguardPath = Path.Combine(path, "proguard-unity.txt");
            if (File.Exists(proguardPath) && !File.ReadAllText(proguardPath).Contains(k_Keep))
                File.AppendAllText(proguardPath, "\n" + k_Keep + "\n");
        }

        const string k_Keep = "-keep class com.brenpoly.cardboardxr.watch.** { *; }\n-keep class com.brenpoly.cardboardxr.browser.** { *; }\n-keep class com.brenpoly.cardboardxr.camera.** { *; }";
    }
}
