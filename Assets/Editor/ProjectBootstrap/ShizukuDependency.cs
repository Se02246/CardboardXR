using System.IO;
using System.Text.RegularExpressions;
using System.Xml;
using UnityEditor.Android;
using UnityEngine;

namespace ProjectBootstrap
{
    // Injects Rikka Shizuku API + Provider into unityLibrary/build.gradle, keeps the Java bridge
    // and Shizuku runtime classes in ProGuard, declares ShizukuProvider inside <application> (the AAR
    // only merges API_V23 + V3_SUPPORT), and declares Android 11+ <queries> visibility for Shizuku
    // and launchable phone apps in AndroidManifest.xml.
    class ShizukuDependency : IPostGenerateGradleAndroidProject
    {
        const string k_AndroidNamespace = "http://schemas.android.com/apk/res/android";
        const string k_ShizukuApi = "implementation 'dev.rikka.shizuku:api:13.1.5'";
        const string k_ShizukuProvider = "implementation 'dev.rikka.shizuku:provider:13.1.5'";
        const string k_ShizukuPkg = "moe.shizuku.privileged.api";
        const string k_ShizukuProviderClass = "rikka.shizuku.ShizukuProvider";
        const string k_NotificationListenerClass = "com.brenpoly.cardboardxr.system.CardboardNotificationListener";
        const string k_RunWithoutFocusMeta = "unity.run-without-focus";
        const string k_Keep =
            "-keep class com.brenpoly.cardboardxr.phoneapps.** { *; }\n" +
            "-keep class com.brenpoly.cardboardxr.system.** { *; }\n" +
            "-keep class com.brenpoly.cardboardxr.camera.** { *; }\n" +
            "-keep class rikka.shizuku.** { *; }";

        public int callbackOrder => 2;

        public void OnPostGenerateGradleAndroidProject(string path)
        {
            PatchGradle(path);
            PatchProguard(path);
            PatchManifest(path);
        }

        static void PatchGradle(string path)
        {
            var gradlePath = Path.Combine(path, "build.gradle");
            if (!File.Exists(gradlePath))
            {
                Debug.LogError($"[Shizuku] No {gradlePath}: phone apps integration won't build");
                return;
            }
            var gradle = File.ReadAllText(gradlePath);
            var block = new Regex(@"^dependencies\s*\{", RegexOptions.Multiline).Match(gradle);
            if (!block.Success)
            {
                Debug.LogError("[Shizuku] No dependencies block in unityLibrary/build.gradle");
                return;
            }

            var insertion = "";
            if (!gradle.Contains("dev.rikka.shizuku:api"))
                insertion += "\n    " + k_ShizukuApi;
            if (!gradle.Contains("dev.rikka.shizuku:provider"))
                insertion += "\n    " + k_ShizukuProvider;

            if (!string.IsNullOrEmpty(insertion))
            {
                gradle = gradle.Insert(block.Index + block.Length, insertion);
                File.WriteAllText(gradlePath, gradle);
            }
        }

        static void PatchProguard(string path)
        {
            var proguardPath = Path.Combine(path, "proguard-unity.txt");
            if (!File.Exists(proguardPath))
                return;
            var current = File.ReadAllText(proguardPath);
            var additions = "";
            if (!current.Contains("com.brenpoly.cardboardxr.phoneapps"))
                additions += "\n" + k_Keep + "\n";
            else if (!current.Contains("com.brenpoly.cardboardxr.system"))
                additions += "\n-keep class com.brenpoly.cardboardxr.system.** { *; }\n-keep class com.brenpoly.cardboardxr.camera.** { *; }\n";
            if (!string.IsNullOrEmpty(additions))
                File.AppendAllText(proguardPath, additions);
        }

        static void PatchManifest(string path)
        {
            var manifestPath = Path.Combine(path, "src", "main", "AndroidManifest.xml");
            if (!File.Exists(manifestPath))
                return;

            var doc = new XmlDocument();
            doc.Load(manifestPath);
            var manifest = doc.DocumentElement;
            if (manifest == null)
                return;

            XmlElement queries = null;
            XmlElement application = null;
            foreach (XmlNode child in manifest.ChildNodes)
            {
                if (child is XmlElement el)
                {
                    if (el.LocalName == "queries")
                        queries = el;
                    else if (el.LocalName == "application")
                        application = el;
                }
            }
            var changed = false;
            if (queries == null)
            {
                queries = doc.CreateElement("queries");
                manifest.AppendChild(queries);
                changed = true;
            }

            if (!HasLauncherQuery(queries))
            {
                var intent = doc.CreateElement("intent");
                var action = doc.CreateElement("action");
                action.SetAttribute("name", k_AndroidNamespace, "android.intent.action.MAIN");
                var category = doc.CreateElement("category");
                category.SetAttribute("name", k_AndroidNamespace, "android.intent.category.LAUNCHER");
                intent.AppendChild(action);
                intent.AppendChild(category);
                queries.AppendChild(intent);
                changed = true;
            }

            if (!HasPackageQuery(queries, k_ShizukuPkg))
            {
                var pkg = doc.CreateElement("package");
                pkg.SetAttribute("name", k_AndroidNamespace, k_ShizukuPkg);
                queries.AppendChild(pkg);
                changed = true;
            }

            if (!HasActionQuery(queries, "android.speech.RecognitionService"))
            {
                var intent = doc.CreateElement("intent");
                var action = doc.CreateElement("action");
                action.SetAttribute("name", k_AndroidNamespace, "android.speech.RecognitionService");
                intent.AppendChild(action);
                queries.AppendChild(intent);
                changed = true;
            }

            if (EnsurePermission(doc, manifest, "android.permission.RECORD_AUDIO"))
                changed = true;

            if (application != null && !HasShizukuProvider(application))
            {
                var provider = doc.CreateElement("provider");
                provider.SetAttribute("name", k_AndroidNamespace, k_ShizukuProviderClass);
                provider.SetAttribute("authorities", k_AndroidNamespace, "${applicationId}.shizuku");
                provider.SetAttribute("enabled", k_AndroidNamespace, "true");
                provider.SetAttribute("exported", k_AndroidNamespace, "true");
                provider.SetAttribute("multiprocess", k_AndroidNamespace, "false");
                provider.SetAttribute("permission", k_AndroidNamespace, "android.permission.INTERACT_ACROSS_USERS_FULL");
                application.AppendChild(provider);
                changed = true;
            }

            if (application != null && !HasNotificationListenerService(application))
            {
                var service = doc.CreateElement("service");
                service.SetAttribute("name", k_AndroidNamespace, k_NotificationListenerClass);
                service.SetAttribute("label", k_AndroidNamespace, "CardboardXR Notifiche e Media");
                service.SetAttribute("permission", k_AndroidNamespace, "android.permission.BIND_NOTIFICATION_LISTENER_SERVICE");
                service.SetAttribute("exported", k_AndroidNamespace, "true");
                var intentFilter = doc.CreateElement("intent-filter");
                var action = doc.CreateElement("action");
                action.SetAttribute("name", k_AndroidNamespace, "android.service.notification.NotificationListenerService");
                intentFilter.AppendChild(action);
                service.AppendChild(intentFilter);
                application.AppendChild(service);
                changed = true;
            }

            if (application != null && EnsureMetaDataTrue(doc, application, k_RunWithoutFocusMeta))
                changed = true;

            if (changed)
                doc.Save(manifestPath);
        }

        static bool HasNotificationListenerService(XmlElement application)
        {
            foreach (XmlElement service in application.GetElementsByTagName("service"))
            {
                if (service.GetAttribute("name", k_AndroidNamespace) == k_NotificationListenerClass)
                    return true;
            }
            return false;
        }

        static bool EnsureMetaDataTrue(XmlDocument doc, XmlElement application, string metaName)
        {
            foreach (XmlElement meta in application.GetElementsByTagName("meta-data"))
            {
                if (meta.GetAttribute("name", k_AndroidNamespace) == metaName)
                {
                    if (meta.GetAttribute("value", k_AndroidNamespace) == "true")
                        return false;
                    meta.SetAttribute("value", k_AndroidNamespace, "true");
                    return true;
                }
            }
            var created = doc.CreateElement("meta-data");
            created.SetAttribute("name", k_AndroidNamespace, metaName);
            created.SetAttribute("value", k_AndroidNamespace, "true");
            application.AppendChild(created);
            return true;
        }

        static bool HasShizukuProvider(XmlElement application)
        {
            foreach (XmlElement provider in application.GetElementsByTagName("provider"))
            {
                if (provider.GetAttribute("name", k_AndroidNamespace) == k_ShizukuProviderClass)
                    return true;
            }
            return false;
        }

        static bool HasLauncherQuery(XmlElement queries)
        {
            foreach (XmlElement intent in queries.GetElementsByTagName("intent"))
            {
                var hasMain = false;
                var hasLauncher = false;
                foreach (XmlElement action in intent.GetElementsByTagName("action"))
                {
                    if (action.GetAttribute("name", k_AndroidNamespace) == "android.intent.action.MAIN")
                        hasMain = true;
                }
                foreach (XmlElement cat in intent.GetElementsByTagName("category"))
                {
                    if (cat.GetAttribute("name", k_AndroidNamespace) == "android.intent.category.LAUNCHER")
                        hasLauncher = true;
                }
                if (hasMain && hasLauncher)
                    return true;
            }
            return false;
        }

        static bool HasPackageQuery(XmlElement queries, string pkgName)
        {
            foreach (XmlElement pkg in queries.GetElementsByTagName("package"))
            {
                if (pkg.GetAttribute("name", k_AndroidNamespace) == pkgName)
                    return true;
            }
            return false;
        }

        static bool HasActionQuery(XmlElement queries, string actionName)
        {
            foreach (XmlElement intent in queries.GetElementsByTagName("intent"))
            {
                foreach (XmlElement action in intent.GetElementsByTagName("action"))
                {
                    if (action.GetAttribute("name", k_AndroidNamespace) == actionName)
                        return true;
                }
            }
            return false;
        }

        static bool EnsurePermission(XmlDocument doc, XmlElement manifest, string permissionName)
        {
            foreach (XmlElement existing in manifest.GetElementsByTagName("uses-permission"))
            {
                if (existing.GetAttribute("name", k_AndroidNamespace) == permissionName)
                    return false;
            }
            var el = doc.CreateElement("uses-permission");
            el.SetAttribute("name", k_AndroidNamespace, permissionName);
            manifest.PrependChild(el);
            return true;
        }
    }
}
