#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;
using UnityEngine.Rendering;

namespace MixedSpace.Editor
{
    /// <summary>
    /// 检查可选 XVXR 示例，不直接链接厂商程序集。
    /// Checks the optional XVXR sample without linking its vendor assembly.
    /// </summary>
    public sealed class MixedSpaceXVXRSetupWindow : EditorWindow
    {
        internal const string SceneGuid = "3fdd89169e764993bcaf99ca7417d91c";
        private static readonly Dictionary<string, string> SampleScripts = new Dictionary<string, string>
        {
            { "XVXRPlatform", "6189958b5bbd4adaad4ed9e6e9e543e3" }, { "XVXRCamera", "9828f37c3aac403b967bc830d44664f5" },
            { "LocalizationStatus", "851280af666948c8b59dcfe06d79477a" }, { "PoseIndicator", "dcc6ea890e3741be869245e9e3b1cde7" }
        };
        private Vector2 m_Scroll;
        private string m_Message;
        private bool m_Error;
        private readonly List<string> m_Issues = new List<string>();
        private readonly List<string> m_ConfigurationIssues = new List<string>();
        private readonly List<MixedSpaceXVXRDependencyInstaller.DependencyGroup> m_MissingGroups =
            new List<MixedSpaceXVXRDependencyInstaller.DependencyGroup>();
        private double m_NextCheck;

        [MenuItem("MixedSpaceSDK/Adapters/XVXR 设置与依赖检查")]
        public static void Open() => GetWindow<MixedSpaceXVXRSetupWindow>("XVXR 设置");

        [InitializeOnLoadMethod]
        private static void Initialize()
        {
            EditorApplication.delayCall += OpenPending;
        }

        private static void OpenPending()
        {
            if (!SessionState.GetBool("MixedSpace.XVXR.SetupPending", false)) return;
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                EditorApplication.delayCall += OpenPending;
                return;
            }
            SessionState.SetBool("MixedSpace.XVXR.SetupPending", false);
            if (!Application.isBatchMode) Open();
        }

        internal static bool SampleImported => AssetDatabase.GUIDToAssetPath(SceneGuid).StartsWith("Assets/", StringComparison.Ordinal);

        internal static Type FindType(string name)
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type type = assembly.GetType(name, false);
                if (type != null) return type;
            }
            return null;
        }

        internal static List<string> CheckDependencies()
        {
            var issues = new List<string>();
            if (!SampleImported) { issues.Add("请先通过 Package Manager 导入 XVXRAdapter sample。"); return issues; }
            foreach (KeyValuePair<string, string> script in SampleScripts)
                foreach (string guid in AssetDatabase.FindAssets(script.Key + " t:MonoScript", new[] { "Assets" }))
                {
                    string path = AssetDatabase.GUIDToAssetPath(guid);
                    if (Path.GetFileNameWithoutExtension(path) == script.Key && guid != script.Value)
                    {
                        Type candidate = AssetDatabase.LoadAssetAtPath<MonoScript>(path)?.GetClass();
                        string expectedNamespace = script.Key == "XVXRPlatform" || script.Key == "XVXRCamera"
                            ? "MixedSpace.XR" : "MixedSpace.Samples.Util";
                        // Other samples may own same-named HUD scripts in an isolated namespace.
                        if (candidate != null && candidate.Namespace != expectedNamespace) continue;
                        issues.Add("检测到同名旧示例：" + path + "。请在独立工程使用新 sample。");
                    }
                }
            Type api = FindType("API");
            if (api == null) issues.Add("缺少厂商 API 源码。");
            else
            {
                foreach (string method in new[] { "xslam_ready", "readRGBCalibration", "xslam_get_rgb_image_YUV", "xslam_get_pose_at", "xslam_get_pose_prediction" })
                    if (api.GetMethod(method, BindingFlags.Static | BindingFlags.Public) == null) issues.Add("当前厂商 API 缺少接口：" + method);
            }
            Type deviceManager = FindType("XvXR.Engine.XvDeviceManager");
            if (deviceManager != null && deviceManager.Assembly.GetName().Name != "Assembly-CSharp") issues.Add("内部 XvDeviceManager 必须与适配器同属 Assembly-CSharp。");
            foreach (string name in new[] { "XvXR.Engine.XvXRManager", "XvXR.Engine.XvXRHeadTracking", "XvXR.Engine.XvDeviceManager", "Microsoft.MixedReality.Toolkit.MixedRealityToolkit" })
                if (FindType(name) == null) issues.Add("缺少类型：" + name);
            try
            {
                var manifest = MixedSpaceXVXRDependencyInstaller.ReadManifest();
                foreach (var reference in manifest.requiredGuids)
                    if (string.IsNullOrEmpty(AssetDatabase.GUIDToAssetPath(reference.guid))) issues.Add("缺少场景资源：" + reference.path);
                foreach (var file in manifest.files)
                {
                    string extension = Path.GetExtension(file.path).ToLowerInvariant();
                    if (extension != ".aar" && extension != ".so") continue;
                    string path = AssetDatabase.GUIDToAssetPath(file.guid);
                    var importer = AssetImporter.GetAtPath(path) as PluginImporter;
                    if (importer == null || !importer.GetCompatibleWithPlatform(BuildTarget.Android)) issues.Add("缺少或禁用 Android 插件：" + file.path);
                    else if (extension == ".so" && file.path.Contains("arm64-v8a/") && importer.GetPlatformData(BuildTarget.Android, "CPU") != "ARM64")
                        issues.Add("原生插件未配置 ARM64：" + path);
                }
            }
            catch (Exception exception) { issues.Add(exception.Message); }
            return issues;
        }

        private void OnEnable() { m_Message = null; m_NextCheck = 0; }

        private void OnInspectorUpdate()
        {
            if (!EditorApplication.isCompiling && !EditorApplication.isUpdating && EditorApplication.timeSinceStartup >= m_NextCheck)
            {
                m_NextCheck = EditorApplication.timeSinceStartup + 2;
                m_Issues.Clear();
                m_ConfigurationIssues.Clear();
                m_MissingGroups.Clear();
                if (SampleImported)
                    try
                    {
                        m_MissingGroups.AddRange(MixedSpaceXVXRDependencyInstaller.MissingGroups());
                        if (m_MissingGroups.Count == 0) m_Issues.AddRange(CheckDependencies());
                        m_ConfigurationIssues.AddRange(MixedSpaceXVXRProjectConfigurator.CheckConfiguration());
                    }
                    catch (Exception exception) { m_Issues.Add(exception.Message); }
            }
            Repaint();
        }

        private void OnGUI()
        {
            bool busy = EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlaying ||
                MixedSpaceXVXRProjectConfigurator.IsRunning;
            EditorGUILayout.LabelField("待处理项目", EditorStyles.boldLabel);
            using (new EditorGUI.DisabledScope(busy || !SampleImported || m_MissingGroups.Count > 0 || m_Issues.Count > 0))
                if (GUILayout.Button("Fix All · 一键修复项目配置", GUILayout.Height(30)))
                {
                    Run(MixedSpaceXVXRProjectConfigurator.BeginSetup);
                    GUIUtility.ExitGUI();
                }
            if (m_MissingGroups.Count > 0) EditorGUILayout.LabelField("先用各项 Fix 导入依赖，再使用 Fix All 配置工程。", EditorStyles.wordWrappedMiniLabel);
            m_Scroll = EditorGUILayout.BeginScrollView(m_Scroll);
            if (!SampleImported) EditorGUILayout.LabelField("请先导入 XVXRAdapter 示例。");
            else if (busy) EditorGUILayout.LabelField("请等待导入或编译完成。");
            else if (m_MissingGroups.Count == 0 && m_Issues.Count == 0 && m_ConfigurationIssues.Count == 0)
                EditorGUILayout.LabelField("当前检测项已通过。");
            foreach (var group in m_MissingGroups)
            {
                string name = group == MixedSpaceXVXRDependencyInstaller.DependencyGroup.MRTK ? "MRTK" :
                    group == MixedSpaceXVXRDependencyInstaller.DependencyGroup.TextMeshPro ? "TMP 资源" : "XVisio SDK";
                bool needsMrtk = group == MixedSpaceXVXRDependencyInstaller.DependencyGroup.XVisio &&
                    m_MissingGroups.Contains(MixedSpaceXVXRDependencyInstaller.DependencyGroup.MRTK);
                Issue(name, needsMrtk ? "先导入 MRTK。" : string.Empty, "导入",
                    () => MixedSpaceXVXRDependencyInstaller.ImportMissing(group), busy || needsMrtk);
            }
            if (SampleImported)
            {
#if UNITY_ANDROID
                if (!MixedSpaceXVXRProjectConfigurator.UsesUnitySdk)
                    Issue("Android SDK", "Unity 自带", "修复", MixedSpaceXVXRProjectConfigurator.UseUnitySdk, busy);
#endif
                if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.Android)
                    Issue("构建目标", "Android", "修复", () =>
                    {
                        if (!EditorUserBuildSettings.SwitchActiveBuildTargetAsync(BuildTargetGroup.Android, BuildTarget.Android))
                            throw new InvalidOperationException("Android 目标切换失败。");
                    }, busy, MessageType.Warning);
                if (PlayerSettings.GetScriptingBackend(NamedBuildTarget.Android) != ScriptingImplementation.IL2CPP)
                    Issue("Android 脚本后端", "IL2CPP", "修复",
                        () => PlayerSettings.SetScriptingBackend(NamedBuildTarget.Android, ScriptingImplementation.IL2CPP), busy);
                if (PlayerSettings.Android.targetArchitectures != AndroidArchitecture.ARM64)
                    Issue("Android CPU 架构", "ARM64", "修复",
                        () => PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64, busy);
                if ((int)PlayerSettings.Android.minSdkVersion < 26)
                    Issue("最低 Android API", "至少 26", "修复",
                        () => PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel26, busy);
                if (PlayerSettings.Android.targetSdkVersion == AndroidSdkVersions.AndroidApiLevelAuto ||
                    (int)PlayerSettings.Android.targetSdkVersion >= 31)
                {
                    bool minimumTooHigh = (int)PlayerSettings.Android.minSdkVersion >= 31;
                    Issue("目标 Android API", "已验证版本：27", minimumTooHigh ? "打开设置" : "修复", () =>
                    {
                        if (minimumTooHigh) SettingsService.OpenProjectSettings("Project/Player");
                        else PlayerSettings.Android.targetSdkVersion = AndroidSdkVersions.AndroidApiLevel27;
                    }, busy);
                }
                GraphicsDeviceType[] apis = PlayerSettings.GetGraphicsAPIs(BuildTarget.Android);
                if (PlayerSettings.GetUseDefaultGraphicsAPIs(BuildTarget.Android) || apis.Length != 1 || apis[0] != GraphicsDeviceType.OpenGLES3)
                    Issue("Android 图形 API", "OpenGLES3", "修复", () =>
                    {
                        PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android, false);
                        PlayerSettings.SetGraphicsAPIs(BuildTarget.Android, new[] { GraphicsDeviceType.OpenGLES3 });
                    }, busy);
                if (m_ConfigurationIssues.Count > 0)
                    Issue("工程配置", "Android / SDK / Gradle", "配置工程", MixedSpaceXVXRProjectConfigurator.BeginSetup,
                        busy || m_MissingGroups.Count > 0 || m_Issues.Count > 0);
            }
            if (m_Issues.Count > 0) EditorGUILayout.HelpBox(m_Issues[0], MessageType.Error);
            string message = !string.IsNullOrEmpty(m_Message) ? m_Message : MixedSpaceXVXRProjectConfigurator.StatusMessage;
            bool error = !string.IsNullOrEmpty(m_Message) ? m_Error : MixedSpaceXVXRProjectConfigurator.HasError;
            if (!string.IsNullOrEmpty(message)) EditorGUILayout.HelpBox(message, error ? MessageType.Error : MessageType.Info);
            EditorGUILayout.EndScrollView();
        }

        private void Issue(string title, string detail, string button, Action action, bool disabled, MessageType severity = MessageType.Error)
        {
            if (MixedSpaceSampleSetup.IssueRow(title, detail, severity, button.StartsWith("打开") ? "打开" : "Fix", disabled))
            {
                Run(action);
                GUIUtility.ExitGUI();
            }
        }

        private void Run(Action action)
        {
            try { m_Error = false; m_Message = null; action(); }
            catch (Exception exception) { m_Error = true; m_Message = exception.Message; }
            m_NextCheck = 0;
        }
    }

    internal sealed class MixedSpaceXVXRImportNotice : AssetPostprocessor
    {
        private static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] oldPaths)
        {
            bool sample = false;
            foreach (string path in imported)
                if (path.StartsWith("Assets/", StringComparison.Ordinal) && path.EndsWith("/XVXRSample.unity", StringComparison.Ordinal)) sample = true;
            if (!sample) return;
            SessionState.SetBool("MixedSpace.XVXR.SetupPending", true);
        }
    }
}
#endif
