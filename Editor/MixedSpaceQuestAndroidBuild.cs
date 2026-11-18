#if UNITY_EDITOR && UNITY_ANDROID
using System;
using System.IO;
using System.Linq;
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
    /// <summary>Changes only the generated Android module of builds using the Quest adapter.</summary>
    internal sealed class MixedSpaceQuestAndroidBuild : BuildPlayerProcessor, IProcessSceneWithReport, IPostGenerateGradleAndroidProject
    {
        private const string BuildKey = "MixedSpace.Quest.Build";
        private const string Android = "http://schemas.android.com/apk/res/android";
        private const string Tools = "http://schemas.android.com/tools";
        public override int callbackOrder => 20000;

        public override void PrepareForBuild(BuildPlayerContext context)
        {
            SessionState.SetBool(BuildKey, false);
            if (context == null || context.BuildPlayerOptions.target != BuildTarget.Android) return;
            foreach (string scene in context.BuildPlayerOptions.scenes ?? Array.Empty<string>())
            {
                if (string.IsNullOrEmpty(scene)) continue;
                if (!MixedSpaceQuestSetupWindow.IsSampleScene(scene) &&
                    !AssetDatabase.GetDependencies(scene, true).Any(MixedSpaceQuestSetupWindow.IsPlatformScript)) continue;
                Check();
                SessionState.SetBool(BuildKey, true);
            }
        }

        private static void Check()
        {
            string[] dependencies = MixedSpaceQuestSetupWindow.CheckDependencies();
            if (dependencies.Length > 0) throw new BuildFailedException("Quest: " + string.Join("; ", dependencies));
            string[] configuration = (string[])MixedSpaceQuestSetupWindow.InvokeSetup("CheckConfiguration");
            if (configuration.Length > 0) throw new BuildFailedException("Quest: " + string.Join("; ", configuration));
        }

        public void OnProcessScene(Scene scene, BuildReport report)
        {
            if (report == null || report.summary.platform != BuildTarget.Android) return;
            bool selected = MixedSpaceQuestSetupWindow.IsSampleScene(scene.path);
            bool adapter = scene.GetRootGameObjects().SelectMany(root => root.GetComponentsInChildren<MonoBehaviour>(true))
                .Where(component => component != null).Any(component =>
                {
                    MonoScript script = MonoScript.FromMonoBehaviour(component);
                    return script != null && MixedSpaceQuestSetupWindow.IsPlatformScript(AssetDatabase.GetAssetPath(script));
                });
            if (!selected && !adapter) return;
            Check();
            if (!adapter || scene.GetRootGameObjects().Any(root => root.GetComponentsInChildren<MonoBehaviour>(true).Any(component => component == null)))
                throw new BuildFailedException("Quest: scene contains a missing adapter/script. Wait for dependency compilation.");
            SessionState.SetBool(BuildKey, true);
        }

        public void OnPostGenerateGradleAndroidProject(string path)
        {
            if (!SessionState.GetBool(BuildKey, false)) return;
            string module = File.Exists(Path.Combine(path, "src/main/AndroidManifest.xml")) ? path : Path.Combine(path, "unityLibrary");
            ApplyGeneratedManifest(module);
        }

        internal static void ApplyGeneratedManifest(string module)
        {
            string manifest = Path.Combine(module, "src/main/AndroidManifest.xml");
            var document = new XmlDocument { XmlResolver = null };
            document.Load(manifest);
            XmlElement root = document.DocumentElement;
            XmlElement application = root?.SelectSingleNode("application") as XmlElement;
            if (root == null || application == null) throw new BuildFailedException("Quest: generated Android Manifest is incomplete.");
            foreach (string permission in new[] { "android.permission.INTERNET", "android.permission.ACCESS_NETWORK_STATE", "horizonos.permission.HEADSET_CAMERA" })
                Ensure(document, root, "uses-permission", permission);
            foreach (string permission in new[] { "android.permission.CAMERA", "android.permission.WRITE_EXTERNAL_STORAGE" })
                Ensure(document, root, "uses-permission", permission).SetAttribute("node", Tools, "remove");
            Ensure(document, root, "uses-feature", "com.oculus.feature.BOUNDARYLESS_APP").SetAttribute("required", Android, "true");
            using (var output = new StringWriter())
            {
                using (var writer = XmlWriter.Create(output, new XmlWriterSettings { Indent = true, OmitXmlDeclaration = true, NewLineChars = "\n" })) document.Save(writer);
                string text = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" + output + "\n";
                if (File.ReadAllText(manifest) != text) File.WriteAllText(manifest, text, new UTF8Encoding(false));
            }
        }

        private static XmlElement Ensure(XmlDocument document, XmlElement root, string element, string name)
        {
            foreach (XmlNode child in root.ChildNodes)
                if (child is XmlElement existing && existing.Name == element && existing.GetAttribute("name", Android) == name) return existing;
            XmlElement created = document.CreateElement(element);
            created.SetAttribute("name", Android, name);
            root.AppendChild(created);
            return created;
        }
    }
}
#endif
