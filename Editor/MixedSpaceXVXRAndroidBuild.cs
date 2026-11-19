#if UNITY_EDITOR && UNITY_ANDROID
using System;
using System.IO;
using System.Text;
using System.Xml;
using UnityEditor;
using UnityEditor.Android;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MixedSpace.Editor
{
    /// <summary>
    /// 仅在构建使用该示例的场景时补充厂商清单声明。
    /// Supplies the vendor manifest entry only when building a scene using this sample.
    /// </summary>
    internal sealed class MixedSpaceXVXRAndroidBuild : BuildPlayerProcessor, IProcessSceneWithReport, IPostGenerateGradleAndroidProject
    {
        private const string BuildKey = "MixedSpace.XVXR.ManifestBuild";
        private const string PlatformGuid = "6189958b5bbd4adaad4ed9e6e9e543e3";
        private const string AndroidNamespace = "http://schemas.android.com/apk/res/android";
        private const string UnityActivity = "com.unity3d.player.UnityPlayerActivity";
        private const string VendorActivity = "top.xv.xrlib.unity.XvMainActivity";
        private const string VendorLauncher = "top.xv.xrlib.launcher.XvARLauncher";
        private const string WaitLauncher = "top.xv.xrlib.launcher.XvARWaitLauncher";
        private const string MainAction = "android.intent.action.MAIN";
        private const string LauncherCategory = "android.intent.category.LAUNCHER";
        private const string UsbAction = "android.hardware.usb.action.USB_DEVICE_ATTACHED";
        private const string ConfigChanges = "mcc|mnc|locale|touchscreen|keyboard|keyboardHidden|navigation|orientation|screenLayout|uiMode|screenSize|smallestScreenSize|fontScale|layoutDirection|density";
        public override int callbackOrder => 1000;

        public override void PrepareForBuild(BuildPlayerContext context)
        {
            SessionState.SetBool(BuildKey, false);
            if (context == null || context.BuildPlayerOptions.target != BuildTarget.Android) return;
            foreach (string scenePath in context.BuildPlayerOptions.scenes ?? Array.Empty<string>())
            {
                if (string.IsNullOrEmpty(scenePath)) continue;
                bool selectedSample = MixedSpaceXVXRSetupWindow.IsSampleScene(scenePath);
                bool usesAdapter = selectedSample;
                if (!usesAdapter)
                    foreach (string dependency in AssetDatabase.GetDependencies(scenePath, true))
                        if (AssetDatabase.AssetPathToGUID(dependency) == PlatformGuid) { usesAdapter = true; break; }
                if (!usesAdapter) continue;
                var issues = MixedSpaceXVXRSetupWindow.CheckDependencies(selectedSample ? scenePath : null);
                if (issues.Count > 0) throw new BuildFailedException("XVXR: dependencies are not ready. " + string.Join("; ", issues));
                var configuration = MixedSpaceXVXRProjectConfigurator.CheckConfiguration();
                if (configuration.Count > 0) throw new BuildFailedException("XVXR: check Android Player Settings and Gradle configuration. " + string.Join("; ", configuration));
                if (PlayerSettings.Android.targetSdkVersion == AndroidSdkVersions.AndroidApiLevelAuto ||
                    (int)PlayerSettings.Android.targetSdkVersion >= 31)
                    Debug.LogWarning("XVXR: 旧版配套 SDK 的 USB 权限接口使用旧 PendingIntent 写法；请验证当前目标 API 下的 USB 授权兼容性，必要时升级原厂 SDK。");
                if (PlayerSettings.GetScriptingBackend(UnityEditor.Build.NamedBuildTarget.Android) != ScriptingImplementation.IL2CPP ||
                    PlayerSettings.Android.targetArchitectures != AndroidArchitecture.ARM64)
                    throw new BuildFailedException("XVXR: Android IL2CPP and ARM64-only are required for the bundled native libraries.");
                SessionState.SetBool(BuildKey, true);
                break;
            }
        }

        public void OnProcessScene(Scene scene, BuildReport report)
        {
            if (report == null || report.summary.platform != BuildTarget.Android) return;
            bool selectedSample = MixedSpaceXVXRSetupWindow.IsSampleScene(scene.path);
            bool usesAdapter = false;
            foreach (GameObject root in scene.GetRootGameObjects())
                foreach (MonoBehaviour component in root.GetComponentsInChildren<MonoBehaviour>(true))
                {
                    if (component == null) continue;
                    MonoScript script = MonoScript.FromMonoBehaviour(component);
                    if (script != null && AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(script)) == PlatformGuid) usesAdapter = true;
                }
            if (!selectedSample && !usesAdapter) return;
            var issues = MixedSpaceXVXRSetupWindow.CheckDependencies(selectedSample ? scene.path : null);
            if (issues.Count > 0) throw new BuildFailedException("XVXR: dependencies are not ready. " + string.Join("; ", issues));
            if (!usesAdapter) throw new BuildFailedException("XVXR: sample platform script was not loaded. Wait for compilation/domain reload after importing dependencies.");
            if (PlayerSettings.GetScriptingBackend(UnityEditor.Build.NamedBuildTarget.Android) != ScriptingImplementation.IL2CPP ||
                PlayerSettings.Android.targetArchitectures != AndroidArchitecture.ARM64)
                throw new BuildFailedException("XVXR: Android IL2CPP and ARM64-only are required for the bundled native libraries.");
            foreach (GameObject root in scene.GetRootGameObjects())
                foreach (MonoBehaviour component in root.GetComponentsInChildren<MonoBehaviour>(true))
                    if (component == null) throw new BuildFailedException("XVXR: the build scene contains a missing script. Open XVXR setup and check scene dependencies.");
            SessionState.SetBool(BuildKey, true);
        }

        public void OnPostGenerateGradleAndroidProject(string path)
        {
            if (!SessionState.GetBool(BuildKey, false) || string.IsNullOrEmpty(path)) return;
            string module = path;
            if (!File.Exists(Path.Combine(module, "libs", "xvxrlib-release.aar"))) module = Path.Combine(path, "unityLibrary");
            if (!File.Exists(Path.Combine(module, "libs", "xvxrlib-release.aar")) || !File.Exists(Path.Combine(module, "libs", "xvsdk-release.aar")))
                throw new BuildFailedException("XVXR: vendor AARs are missing from the generated unityLibrary module.");
            if (EnsureVendorManifest(module)) Debug.Log("XVXR: configured vendor Activity, Launcher, USB permissions and THREE_D in the generated manifest.");
        }

        internal static bool EnsureVendorManifest(string module)
        {
            if (string.IsNullOrEmpty(module)) throw new ArgumentException("Missing generated Android module.", nameof(module));
            string file = Path.Combine(module, "src", "main", "AndroidManifest.xml");
            if (!File.Exists(file)) throw new BuildFailedException("XVXR: generated Android manifest not found: " + file);
            var document = new XmlDocument { PreserveWhitespace = true, XmlResolver = null };
            document.Load(file);
            XmlElement manifest = document.DocumentElement;
            var application = document.SelectSingleNode("/manifest/application") as XmlElement;
            if (manifest == null || application == null) throw new BuildFailedException("XVXR: generated manifest has no application element.");
            string launcherFile = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(module)), "launcher", "src", "main", "AndroidManifest.xml");
            var otherDocument = new XmlDocument { XmlResolver = null };
            if (File.Exists(launcherFile)) otherDocument.Load(launcherFile);
            var otherApplication = otherDocument.SelectSingleNode("/manifest/application") as XmlElement;

            foreach (XmlElement app in new[] { application, otherApplication })
            {
                if (app == null) continue;
                foreach (XmlNode child in app.ChildNodes)
                    if (child is XmlElement activity && (activity.Name == "activity" || activity.Name == "activity-alias") && HasLauncher(activity))
                    {
                        string name = activity.GetAttribute("name", AndroidNamespace);
                        if (activity.Name != "activity" || (name != VendorLauncher && !(app == application && name == UnityActivity)))
                            throw new BuildFailedException("XVXR: custom launcher found: " + name + ". Integrate the vendor entry in the custom manifest; it will not be overwritten.");
                    }
            }
            XmlElement unity = FindNamed(application, "activity", UnityActivity);
            XmlElement vendor = FindNamed(application, "activity", VendorActivity) ?? FindNamed(otherApplication, "activity", VendorActivity);
            if ((unity == null && vendor == null) || (unity != null && vendor != null && HasLauncher(unity)) || FindNamed(otherApplication, "activity", UnityActivity) != null)
                throw new BuildFailedException("XVXR: current Unity entry cannot be safely converted. Configure XvMainActivity and XvARLauncher in the custom manifest.");
            bool changed = false;
            if (unity != null && vendor == null)
            {
                vendor = unity;
                SetAndroid(vendor, "name", VendorActivity);
                SetAndroid(vendor, "theme", "@style/Theme.AppCompat.DayNight.NoActionBar");
                SetAndroid(vendor, "screenOrientation", "landscape");
                SetAndroid(vendor, "hardwareAccelerated", "true");
                SetAndroid(vendor, "launchMode", "singleTask");
                SetAndroid(vendor, "resizeableActivity", "false");
                SetAndroid(vendor, "exported", "true");
                foreach (XmlNode node in vendor.ChildNodes)
                    if (node is XmlElement filter && filter.Name == "intent-filter")
                    {
                        XmlElement category = FindNamed(filter, "category", LauncherCategory);
                        if (category != null) filter.RemoveChild(category);
                    }
                changed = true;
            }
            changed |= AddIfMissing(application, "hardwareAccelerated", "true");
            changed |= AddIfMissing(application, "usesCleartextTraffic", "true");
            foreach (string permission in new[] { "android.permission.CAMERA", "android.permission.USB_PERMISSION", "android.permission.INTERNET" })
                if (FindNamed(manifest, "uses-permission", permission) == null)
                {
                    manifest.InsertBefore(NewNamed(document, "uses-permission", permission), application);
                    changed = true;
                }
            if (FindNamed(manifest, "uses-feature", "android.hardware.usb.host") == null)
            {
                manifest.InsertBefore(NewNamed(document, "uses-feature", "android.hardware.usb.host"), application);
                changed = true;
            }
            if (FindNamed(application, "activity", WaitLauncher) == null && FindNamed(otherApplication, "activity", WaitLauncher) == null)
            {
                XmlElement wait = NewNamed(document, "activity", WaitLauncher);
                SetAndroid(wait, "theme", "@android:style/Theme.Black.NoTitleBar.Fullscreen");
                SetAndroid(wait, "screenOrientation", "landscape");
                SetAndroid(wait, "exported", "true");
                SetAndroid(wait, "configChanges", ConfigChanges);
                SetAndroid(wait, "hardwareAccelerated", "true");
                wait.AppendChild(NewIntent(document, MainAction, "android.intent.category.DEFAULT"));
                application.AppendChild(wait);
                changed = true;
            }
            if (FindNamed(application, "activity", VendorLauncher) == null && FindNamed(otherApplication, "activity", VendorLauncher) == null)
            {
                XmlElement launcher = NewNamed(document, "activity", VendorLauncher);
                SetAndroid(launcher, "clearTaskOnLaunch", "true");
                SetAndroid(launcher, "noHistory", "true");
                SetAndroid(launcher, "exported", "true");
                SetAndroid(launcher, "screenOrientation", "landscape");
                SetAndroid(launcher, "theme", "@style/BaseUnityTheme1");
                launcher.AppendChild(NewIntent(document, MainAction, LauncherCategory, "android.intent.category.DEFAULT"));
                launcher.AppendChild(NewIntent(document, UsbAction));
                XmlElement usb = NewNamed(document, "meta-data", UsbAction);
                SetAndroid(usb, "resource", "@xml/usb_filter");
                launcher.AppendChild(usb);
                application.AppendChild(launcher);
                changed = true;
            }
            if (FindNamed(application, "meta-data", "com.xv.THREE_D") == null && FindNamed(otherApplication, "meta-data", "com.xv.THREE_D") == null)
            {
                XmlElement metadata = NewNamed(document, "meta-data", "com.xv.THREE_D");
                SetAndroid(metadata, "value", "true");
                application.AppendChild(metadata);
                changed = true;
            }
            if (changed)
                using (var writer = new XmlTextWriter(file, new UTF8Encoding(false))) document.Save(writer);
            return changed;
        }

        private static bool HasLauncher(XmlElement activity)
        {
            foreach (XmlNode node in activity.ChildNodes)
                if (node is XmlElement filter && filter.Name == "intent-filter" && FindNamed(filter, "category", LauncherCategory) != null) return true;
            return false;
        }

        private static XmlElement FindNamed(XmlElement parent, string elementName, string name)
        {
            if (parent == null) return null;
            foreach (XmlNode node in parent.ChildNodes)
                if (node is XmlElement element && element.Name == elementName && element.GetAttribute("name", AndroidNamespace) == name) return element;
            return null;
        }

        private static void SetAndroid(XmlElement element, string name, string value)
        {
            XmlAttribute attribute = element.OwnerDocument.CreateAttribute("android", name, AndroidNamespace);
            attribute.Value = value;
            element.Attributes.SetNamedItem(attribute);
        }

        private static bool AddIfMissing(XmlElement element, string name, string value)
        {
            if (element.HasAttribute(name, AndroidNamespace)) return false;
            SetAndroid(element, name, value);
            return true;
        }

        private static XmlElement NewNamed(XmlDocument document, string elementName, string name)
        {
            XmlElement element = document.CreateElement(elementName);
            SetAndroid(element, "name", name);
            return element;
        }

        private static XmlElement NewIntent(XmlDocument document, string action, params string[] categories)
        {
            XmlElement filter = document.CreateElement("intent-filter");
            filter.AppendChild(NewNamed(document, "action", action));
            foreach (string category in categories) filter.AppendChild(NewNamed(document, "category", category));
            return filter;
        }
    }
}
#endif
