using System;
using System.IO;
using System.Linq;
using System.Xml;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEditor.XR.Management;
using UnityEngine;

namespace MixedSpace.Editor
{
    /// <summary>
    /// PICO 可选适配器的环境检查。不依赖 PICO 程序集，不修改其他平台配置。
    /// Checks the optional PICO adapter environment without referencing PICO assemblies or modifying
    /// other platform settings.
    /// </summary>
    public sealed class MixedSpacePicoSetupWindow : EditorWindow
    {
        private const string PendingKey = "MixedSpace.Pico.SetupPending";
        private const string AndroidNs = "http://schemas.android.com/apk/res/android";
        private const string ToolsNs = "http://schemas.android.com/tools";
        private const string MainManifest = "Assets/Plugins/Android/AndroidManifest.xml";
        private const string LibraryFolder = "Assets/Plugins/Android/MixedSpacePicoPermissions.androidlib";
        private static AddRequest s_Install;
        private string m_Error;
        private Vector2 m_Scroll;

        [MenuItem("MixedSpaceSDK/Adapters/PICO 设置与依赖检查")]
        public static void Open() => GetWindow<MixedSpacePicoSetupWindow>("PICO 设置");

        [InitializeOnLoadMethod]
        private static void Initialize() => EditorApplication.delayCall += OpenPending;

        internal static void NotifyImport()
        {
            SessionState.SetBool(PendingKey, true);
            EditorApplication.delayCall += OpenPending;
        }

        private static void OpenPending()
        {
            if (!SessionState.GetBool(PendingKey, false) || Application.isBatchMode) return;
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                EditorApplication.delayCall += OpenPending;
                return;
            }
            SessionState.SetBool(PendingKey, false);
            Open();
        }

        private void OnInspectorUpdate()
        {
            if (s_Install != null && s_Install.IsCompleted)
            {
                m_Error = s_Install.Status == StatusCode.Failure ? s_Install.Error.message : null;
                s_Install = null;
            }
            Repaint();
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("PICO 4 Ultra / Ultra Enterprise / Enterprise · 待处理项目", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("只显示未通过项。检查项目配置，不代表真机权限已授予或相机已验收。", MessageType.Info);
            m_Scroll = EditorGUILayout.BeginScrollView(m_Scroll);
            int issues = 0;
            using (new EditorGUI.DisabledScope(s_Install != null || EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlaying))
            {
                if (GUILayout.Button("Fix All · 一键修复项目配置", GUILayout.Height(30)))
                {
                    try
                    {
                        m_Error = null;
                        const string loader = "Unity.XR.PXR.PXR_Loader";
                        MixedSpaceSampleSetup.FixAndroid(false, MixedSpaceSampleSetup.FindType(loader) != null ? loader : null);
                    }
                    catch (Exception e) { m_Error = e.Message; }
                }
                var pico = UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages()
                    .FirstOrDefault(p => p.name == "com.unity.xr.picoxr");
                if (pico == null)
                {
                    issues++;
                    Issue("PICO SDK 未安装", "4E 源工程使用 2.5.0，4UE 使用 3.1.0。请选择对应官方 SDK 的 package.json；同一工程只安装一个 PICO SDK 版本。", "选择并安装", InstallLocalSdk);
                }
                if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.Android)
                {
                    issues++;
                    Issue("构建目标不是 Android", "切换会重新导入平台资源。", "切换", () => EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Android, BuildTarget.Android), MessageType.Warning);
                }
                if (PlayerSettings.GetScriptingBackend(BuildTargetGroup.Android) != ScriptingImplementation.IL2CPP)
                {
                    issues++;
                    Issue("Android 未使用 IL2CPP", "使用 IL2CPP 构建。", "修复", () => PlayerSettings.SetScriptingBackend(BuildTargetGroup.Android, ScriptingImplementation.IL2CPP));
                }
                if (PlayerSettings.Android.targetArchitectures != AndroidArchitecture.ARM64)
                {
                    issues++;
                    Issue("Android 架构不是仅 ARM64", "当前定位原生插件面向 ARM64。", "修复", () => PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64);
                }
                try
                {
                    if (!HasCameraDeclaration())
                    {
                        issues++;
                        Issue("缺少 CAMERA 权限声明", "仅在运行时申请不够，APK 必须声明 android.permission.CAMERA。修复前备份已有清单。", "修复", RepairCameraPermission);
                    }
                }
                catch (Exception e)
                {
                    issues++;
                    EditorGUILayout.HelpBox("Android 清单读取失败：" + e.Message, MessageType.Error);
                }
                var settings = XRGeneralSettingsPerBuildTarget.XRGeneralSettingsForBuildTarget(BuildTargetGroup.Android);
                var loaders = settings != null && settings.Manager != null ? settings.Manager.activeLoaders : null;
                if (loaders == null || loaders.Count != 1 || loaders[0] == null || loaders[0].GetType().FullName != "Unity.XR.PXR.PXR_Loader")
                {
                    issues++;
                    Issue("Android XR Loader 未配置为 PICO", "Fix 将为 Android 选择 PICO Loader 并启用启动初始化。", "Fix", () => MixedSpaceSampleSetup.FixLoader(BuildTargetGroup.Android, "Unity.XR.PXR.PXR_Loader"));
                }
                if (settings != null && !settings.InitManagerOnStart)
                {
                    issues++;
                    Issue("XR 未设置启动时初始化", "PICO 相机位姿需要运行中的 XR 跟踪。", "修复", () => { settings.InitManagerOnStart = true; EditorUtility.SetDirty(settings); AssetDatabase.SaveAssets(); });
                }
            }
            if (issues == 0) EditorGUILayout.HelpBox("已检查的项目配置全部通过。请在设备首次运行时允许相机访问。", MessageType.Info);
            if (s_Install != null) EditorGUILayout.HelpBox("正在安装 PICO SDK…", MessageType.Info);
            if (!string.IsNullOrEmpty(m_Error))
            {
                EditorGUILayout.HelpBox(m_Error, MessageType.Error);
                if (GUILayout.Button("关闭操作错误提示")) m_Error = null;
            }
            EditorGUILayout.EndScrollView();
        }

        private void Issue(string title, string description, string button, Action action, MessageType severity = MessageType.Error)
        {
            if (MixedSpaceSampleSetup.IssueRow(title, description, severity, button.StartsWith("打开") ? "打开" : "Fix"))
            {
                try { m_Error = null; action(); }
                catch (Exception e) { m_Error = e.Message; }
            }
        }

        [Serializable] private sealed class PackageIdentity { public string name; }
        private static void InstallLocalSdk()
        {
            string path = EditorUtility.OpenFilePanel("选择 PICO SDK 的 package.json", "", "json");
            if (string.IsNullOrEmpty(path)) return;
            if (Path.GetFileName(path) != "package.json" || JsonUtility.FromJson<PackageIdentity>(File.ReadAllText(path))?.name != "com.unity.xr.picoxr")
                throw new InvalidOperationException("请选择 com.unity.xr.picoxr 包的 package.json。");
            s_Install = Client.Add("file:" + Path.GetDirectoryName(path).Replace('\\', '/'));
        }

        private static XmlDocument ReadManifest(string path)
        {
            var xml = new XmlDocument { PreserveWhitespace = true, XmlResolver = null };
            xml.Load(path);
            return xml;
        }

        internal static bool HasCameraDeclaration()
        {
            // 检查本窗口负责的显式声明，不将厂商包中可能被合并移除的声明当作保证。
            // Check explicit declarations owned by this window; vendor declarations that manifest merging may remove are not
            // guarantees.
            bool found = false;
            foreach (string path in new[] { MainManifest, LibraryFolder + "/AndroidManifest.xml" })
            {
                if (!File.Exists(path)) continue;
                foreach (XmlElement permission in ReadManifest(path).SelectNodes("/manifest/uses-permission"))
                {
                    if (permission.GetAttribute("name", AndroidNs) != "android.permission.CAMERA") continue;
                    if (permission.GetAttribute("node", ToolsNs) == "remove" || permission.HasAttribute("maxSdkVersion", AndroidNs)) return false;
                    found = true;
                }
            }
            return found;
        }

        internal static void RepairCameraPermission()
        {
            string path = File.Exists(MainManifest) ? MainManifest : LibraryFolder + "/AndroidManifest.xml";
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            XmlDocument xml;
            if (File.Exists(path))
            {
                Directory.CreateDirectory("Library/MixedSpaceBackups");
                File.Copy(path, "Library/MixedSpaceBackups/PicoManifest-" + Guid.NewGuid().ToString("N") + ".xml");
                xml = ReadManifest(path);
            }
            else
            {
                xml = new XmlDocument();
                xml.LoadXml("<manifest xmlns:android=\"" + AndroidNs + "\" package=\"com.mixedspace.pico.permissions\" />");
                File.WriteAllText(LibraryFolder + "/project.properties", "android.library=true\n");
            }
            var root = xml.DocumentElement;
            if (root == null || root.Name != "manifest") throw new InvalidOperationException("清单缺少 manifest 根节点。");
            var permission = root.SelectNodes("uses-permission").Cast<XmlElement>()
                .FirstOrDefault(p => p.GetAttribute("name", AndroidNs) == "android.permission.CAMERA");
            if (permission == null)
            {
                permission = xml.CreateElement("uses-permission");
                permission.SetAttribute("name", AndroidNs, "android.permission.CAMERA");
                root.AppendChild(permission);
            }
            permission.RemoveAttribute("node", ToolsNs);
            permission.RemoveAttribute("maxSdkVersion", AndroidNs);
            xml.Save(path);
            AssetDatabase.Refresh();
        }
    }

    internal sealed class MixedSpacePicoSetupImportNotice : AssetPostprocessor
    {
        private static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] oldPaths)
        {
            if (!Application.isBatchMode && imported.Any(p => p.StartsWith("Assets/", StringComparison.Ordinal) &&
                (p.EndsWith("/MixedSpacePicoAdapter.cs", StringComparison.Ordinal) ||
                 p.EndsWith("/MixedSpacePico4UAdapter.cs", StringComparison.Ordinal) ||
                 p.EndsWith("/MixedSpacePico4EAdapter.cs", StringComparison.Ordinal)))) MixedSpacePicoSetupWindow.NotifyImport();
        }
    }
}
