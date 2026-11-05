using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEngine;

namespace MixedSpace.Editor
{
    /// <summary>
    /// 导入 XRany、Huawei 或 Mobile sample 后弹出的轻量设置窗口。
    /// Lightweight setup opened after importing an XRany, Huawei or Mobile sample.
    /// </summary>
    public sealed class MixedSpaceBasicSampleSetupWindow : EditorWindow
    {
        internal enum Platform { XRany, Huawei, Mobile }
        [SerializeField] private Platform m_Platform;
        [SerializeField] private bool m_Ios;
        private static AddRequest s_Install;
        private string m_Error;
        private int m_Issues;
        private Vector2 m_Scroll;
        private const string ArCore = "UnityEngine.XR.ARCore.ARCoreLoader";
        private const string ArKit = "UnityEngine.XR.ARKit.ARKitLoader";
        private const string PendingKey = "MixedSpace.BasicSampleSetup.Pending";

        [MenuItem("MixedSpaceSDK/Adapters/XRany 设置")]
        private static void OpenXRany() => Open(Platform.XRany);
        [MenuItem("MixedSpaceSDK/Adapters/Huawei 设置")]
        private static void OpenHuawei() => Open(Platform.Huawei);
        [MenuItem("MixedSpaceSDK/Adapters/Mobile 设置")]
        private static void OpenMobile() => Open(Platform.Mobile);
        internal static void Open(Platform target)
        {
            var window = Resources.FindObjectsOfTypeAll<MixedSpaceBasicSampleSetupWindow>().FirstOrDefault(w => w.m_Platform == target);
            if (window == null)
            {
                window = CreateInstance<MixedSpaceBasicSampleSetupWindow>();
                window.m_Platform = target;
                window.m_Ios = EditorUserBuildSettings.activeBuildTarget == BuildTarget.iOS;
            }
            window.titleContent = new GUIContent(target + " 设置");
            window.minSize = new Vector2(420, 240); window.Show(); window.Focus();
        }
        [InitializeOnLoadMethod]
        private static void Initialize() => EditorApplication.delayCall += OpenPending;
        internal static void Queue(Platform target)
        {
            int mask = SessionState.GetInt(PendingKey, 0) | (1 << (int)target);
            SessionState.SetInt(PendingKey, mask);
            EditorApplication.delayCall -= OpenPending;
            EditorApplication.delayCall += OpenPending;
        }
        private static void OpenPending()
        {
            int mask = SessionState.GetInt(PendingKey, 0);
            if (mask == 0 || Application.isBatchMode) return;
            if (MixedSpaceSampleSetup.Busy) { EditorApplication.delayCall += OpenPending; return; }
            SessionState.SetInt(PendingKey, 0);
            foreach (Platform target in Enum.GetValues(typeof(Platform))) if ((mask & (1 << (int)target)) != 0) Open(target);
        }
        private void OnInspectorUpdate()
        {
            if (s_Install != null && s_Install.IsCompleted)
            {
                if (s_Install.Status == StatusCode.Failure) m_Error = s_Install.Error?.message;
                s_Install = null;
            }
            Repaint();
        }
        private void OnGUI()
        {
            EditorGUILayout.LabelField(m_Platform + " 环境设置", EditorStyles.boldLabel);
            if (m_Platform == Platform.Mobile) m_Ios = GUILayout.Toolbar(m_Ios ? 1 : 0, new[] { "Android", "iOS" }) == 1;
            EditorGUILayout.LabelField("只显示待处理项，Fix 后自动重新检查。", EditorStyles.wordWrappedLabel);
            if (m_Platform == Platform.Mobile) EditorGUILayout.LabelField("Fix 会为所选平台配置 ARCore / ARKit Loader。", EditorStyles.miniLabel);
            bool busy = MixedSpaceSampleSetup.Busy || s_Install != null;
            m_Issues = 0;
            using (new EditorGUI.DisabledScope(busy))
            {
                if (GUILayout.Button("Fix All · 一键修复项目配置", GUILayout.Height(30))) Run(FixAll);
                m_Scroll = EditorGUILayout.BeginScrollView(m_Scroll);
                if (m_Platform == Platform.XRany && !MixedSpaceSampleSetup.Installed("com.gzshixiang.gxrsdk"))
                    Issue("缺少 GxrSdk", "选择厂商 SDK 的 package.json。", InstallGxr, "选择并安装");
                if (m_Platform == Platform.Huawei && MixedSpaceSampleSetup.FindType("HuaweiARUnitySDK.ARSession") == null)
                    Issue("Huawei 依赖尚未就绪", "重新导入完整 HuaweiAdapter sample，避免与旧 SDK 副本重复。", () => EditorApplication.ExecuteMenuItem("Window/Package Manager"), "打开 Package Manager");
                if (m_Platform == Platform.Mobile)
                {
                    string id = m_Ios ? "com.unity.xr.arkit" : "com.unity.xr.arcore";
                    if (!MixedSpaceSampleSetup.Installed(id)) Issue("缺少 " + id, "安装与本包一致的 5.1.0 依赖。", () => s_Install = Client.Add(id + "@5.1.0"), "安装");
                }
                if (m_Platform == Platform.Mobile && m_Ios)
                {
                    if (!IosMinimumReady) Issue("iOS 最低版本低于 13.0", "随包原生定位库要求 iOS 13.0 或更高。", () => PlayerSettings.iOS.targetOSVersionString = "13.0");
                    if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.iOS) Issue("构建目标不是 iOS", "切换会重新导入资源。", FixAll, severity: MessageType.Warning);
                    if (!MixedSpaceSampleSetup.LoaderReady(BuildTargetGroup.iOS, ArKit)) Issue("ARKit Loader 未就绪", "配置 Loader 并启用启动初始化。", () => MixedSpaceSampleSetup.FixLoader(BuildTargetGroup.iOS, ArKit));
                    if (string.IsNullOrWhiteSpace(PlayerSettings.iOS.cameraUsageDescription)) Issue("缺少相机用途说明", "填写默认相机用途说明。", SetIosDescription);
                    if (string.IsNullOrWhiteSpace(PlayerSettings.iOS.locationUsageDescription)) Issue("缺少定位用途说明", "移动定位源使用系统定位服务，iOS 构建必须填写 Location Usage Description。", SetIosLocationDescription);
                }
                else
                {
                    if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.Android)
                        Issue("构建目标不是 Android", "切换会重新导入资源。", () =>
                        {
                            if (!EditorUserBuildSettings.SwitchActiveBuildTargetAsync(BuildTargetGroup.Android, BuildTarget.Android))
                                throw new InvalidOperationException("Android 切换失败，请检查 Android Build Support。");
                        }, severity: MessageType.Warning);
                    if (PlayerSettings.GetScriptingBackend(BuildTargetGroup.Android) != ScriptingImplementation.IL2CPP)
                        Issue("Android 未使用 IL2CPP", "使用 IL2CPP 构建。", () => PlayerSettings.SetScriptingBackend(BuildTargetGroup.Android, ScriptingImplementation.IL2CPP));
                    if (PlayerSettings.Android.targetArchitectures != AndroidArchitecture.ARM64)
                        Issue("Android 架构不是仅 ARM64", "当前定位原生插件面向 ARM64。", () => PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64);
                    if (!MixedSpacePicoSetupWindow.HasCameraDeclaration()) Issue("缺少 CAMERA 权限声明", "补权限声明，已有清单修改前备份。", MixedSpacePicoSetupWindow.RepairCameraPermission);
                    if (m_Platform == Platform.Huawei && !MixedSpaceSampleSetup.GlesReady) Issue("图形 API 未使用 OpenGLES3", "使用 Huawei 示例对应的图形 API。", FixAll);
                    if (m_Platform == Platform.Mobile && !MixedSpaceSampleSetup.LoaderReady(BuildTargetGroup.Android, ArCore))
                        Issue("ARCore Loader 未就绪", "配置 Loader 并启用启动初始化。", () => MixedSpaceSampleSetup.FixLoader(BuildTargetGroup.Android, ArCore));
                }
                if (m_Issues == 0) EditorGUILayout.HelpBox("当前环境配置已完成。", MessageType.Info);
                EditorGUILayout.EndScrollView();
            }
            if (busy) EditorGUILayout.LabelField("正在安装、编译或导入，请稍候…");
            if (!string.IsNullOrEmpty(m_Error)) EditorGUILayout.HelpBox(m_Error, MessageType.Error);
        }
        private void Issue(string title, string detail, Action fix, string button = "Fix", MessageType severity = MessageType.Error)
        {
            m_Issues++;
            if (MixedSpaceSampleSetup.IssueRow(title, detail, severity, button.StartsWith("打开") ? "打开" : "Fix")) Run(fix);
        }

        private void Run(Action action) { try { m_Error = null; action(); } catch (Exception e) { m_Error = e.Message; } }
        private static bool IosMinimumReady => Version.TryParse(PlayerSettings.iOS.targetOSVersionString, out var version) &&
            version.CompareTo(new Version(13, 0)) >= 0;
        private static void SetIosDescription() => PlayerSettings.iOS.cameraUsageDescription = "使用相机图像进行空间定位。";
        private static void SetIosLocationDescription() => PlayerSettings.iOS.locationUsageDescription = "使用您的位置辅助空间定位，匹配所在区域的地图。";
        private void FixAll()
        {
            if (m_Platform == Platform.Mobile && m_Ios)
            {
                if (!BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.iOS, BuildTarget.iOS)) throw new InvalidOperationException("请通过 Unity Hub 安装 iOS Build Support。");
                if (!IosMinimumReady) PlayerSettings.iOS.targetOSVersionString = "13.0";
                if (string.IsNullOrWhiteSpace(PlayerSettings.iOS.cameraUsageDescription)) SetIosDescription();
                if (string.IsNullOrWhiteSpace(PlayerSettings.iOS.locationUsageDescription)) SetIosLocationDescription();
                if (MixedSpaceSampleSetup.FindType(ArKit) != null) MixedSpaceSampleSetup.FixLoader(BuildTargetGroup.iOS, ArKit);
                if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.iOS && !EditorUserBuildSettings.SwitchActiveBuildTargetAsync(BuildTargetGroup.iOS, BuildTarget.iOS))
                    throw new InvalidOperationException("iOS 构建目标切换失败。");
            }
            else MixedSpaceSampleSetup.FixAndroid(m_Platform == Platform.Huawei,
                m_Platform == Platform.Mobile && MixedSpaceSampleSetup.FindType(ArCore) != null ? ArCore : null);
        }
        [Serializable] private sealed class PackageIdentity { public string name; }
        private static void InstallGxr()
        {
            string path = EditorUtility.OpenFilePanel("选择 GxrSdk/package.json", "", "json");
            if (string.IsNullOrEmpty(path)) return;
            if (Path.GetFileName(path) != "package.json" || JsonUtility.FromJson<PackageIdentity>(File.ReadAllText(path))?.name != "com.gzshixiang.gxrsdk")
                throw new InvalidOperationException("请选择 com.gzshixiang.gxrsdk 的 package.json。");
            s_Install = Client.Add("file:" + Path.GetDirectoryName(path).Replace('\\', '/'));
        }
    }

    internal sealed class MixedSpaceBasicSampleImportNotice : AssetPostprocessor
    {
        private static readonly string[] SceneGuids = { "86fbb92f473a4392a1cc11bc11a4510d", "64e352beaa748e24d96874c5366a1f12", "c9ad7f59342ce5441a7286c096217fdf" };
        private static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] oldPaths)
        {
            if (Application.isBatchMode) return;
            foreach (string guid in SceneGuids)
            {
                string key = "MixedSpace.SampleSetup.Seen." + Application.dataPath + "." + guid;
                if (deleted.Contains(EditorPrefs.GetString(key, ""))) EditorPrefs.DeleteKey(key);
            }
            foreach (string path in imported.Where(p => p.StartsWith("Assets/", StringComparison.Ordinal) && p.EndsWith(".unity", StringComparison.Ordinal)))
            {
                int index = Array.IndexOf(SceneGuids, AssetDatabase.AssetPathToGUID(path));
                if (index < 0) continue;
                // 只在首次导入该 sample 路径时弹出，普通保存场景不反复打断。
                // Open once for an imported sample path, not on ordinary scene saves.
                string key = "MixedSpace.SampleSetup.Seen." + Application.dataPath + "." + SceneGuids[index];
                if (EditorPrefs.GetString(key, "") == path) continue;
                EditorPrefs.SetString(key, path);
                MixedSpaceBasicSampleSetupWindow.Queue((MixedSpaceBasicSampleSetupWindow.Platform)index);
            }
        }
    }
}
