using System.IO;
using System.Xml;
using UnityEditor.Android;

namespace ProjectBootstrap
{
    // Ensures Android gallery read permissions (API 33+ and legacy <= 32) are present in AndroidManifest.xml
    // for the Quadri photo gallery spatial window app.
    class GalleryPermissions : IPostGenerateGradleAndroidProject
    {
        const string k_AndroidNamespace = "http://schemas.android.com/apk/res/android";

        public int callbackOrder => 2;

        public void OnPostGenerateGradleAndroidProject(string path)
        {
            var manifestPath = Path.Combine(path, "src", "main", "AndroidManifest.xml");
            if (!File.Exists(manifestPath))
                return;

            var document = new XmlDocument();
            document.Load(manifestPath);
            var manifest = document.DocumentElement;
            if (manifest == null)
                return;

            var changed = false;
            changed |= EnsurePermission(document, manifest, "android.permission.READ_MEDIA_IMAGES", maxSdk: null);
            changed |= EnsurePermission(document, manifest, "android.permission.READ_MEDIA_VISUAL_USER_SELECTED", maxSdk: null);
            changed |= EnsurePermission(document, manifest, "android.permission.READ_EXTERNAL_STORAGE", maxSdk: "32");

            if (changed)
                document.Save(manifestPath);
        }

        static bool EnsurePermission(XmlDocument document, XmlElement manifest, string permissionName, string maxSdk)
        {
            foreach (XmlElement existing in manifest.GetElementsByTagName("uses-permission"))
            {
                if (existing.GetAttribute("name", k_AndroidNamespace) == permissionName)
                    return false;
            }
            var element = document.CreateElement("uses-permission");
            element.SetAttribute("name", k_AndroidNamespace, permissionName);
            if (!string.IsNullOrEmpty(maxSdk))
                element.SetAttribute("maxSdkVersion", k_AndroidNamespace, maxSdk);
            manifest.PrependChild(element);
            return true;
        }
    }
}
