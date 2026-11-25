using System.IO;
using System.Xml;
using UnityEditor.Android;

namespace ProjectBootstrap
{
    // Ensures LAN/Wi-Fi/mDNS permissions are present in the generated AndroidManifest for the
    // embedded StreamReceiverServer (HTTPS/WSS screen casting + AirPlay NSD discovery).
    class StreamPermissions : IPostGenerateGradleAndroidProject
    {
        const string k_AndroidNamespace = "http://schemas.android.com/apk/res/android";

        static readonly string[] k_Permissions =
        {
            "android.permission.INTERNET",
            "android.permission.ACCESS_NETWORK_STATE",
            "android.permission.ACCESS_WIFI_STATE",
            "android.permission.CHANGE_WIFI_MULTICAST_STATE",
        };

        public int callbackOrder => 1;

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
            foreach (var perm in k_Permissions)
            {
                var found = false;
                foreach (XmlElement existing in manifest.GetElementsByTagName("uses-permission"))
                {
                    if (existing.GetAttribute("name", k_AndroidNamespace) == perm)
                    {
                        found = true;
                        break;
                    }
                }
                if (found)
                    continue;
                var element = document.CreateElement("uses-permission");
                element.SetAttribute("name", k_AndroidNamespace, perm);
                manifest.PrependChild(element);
                changed = true;
            }

            var appNodes = manifest.GetElementsByTagName("application");
            if (appNodes.Count > 0 && appNodes[0] is XmlElement app &&
                app.GetAttribute("usesCleartextTraffic", k_AndroidNamespace) != "true")
            {
                app.SetAttribute("usesCleartextTraffic", k_AndroidNamespace, "true");
                changed = true;
            }

            if (changed)
                document.Save(manifestPath);
        }
    }
}
