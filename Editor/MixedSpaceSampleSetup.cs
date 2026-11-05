using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.XR.Management;
using UnityEditor.XR.Management.Metadata;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR.Management;

namespace MixedSpace.Editor
{
    /// <summary>
    /// Sample 设置窗口共用的小范围项目修复，不检查或改写业务场景。
    /// Small project repairs shared by sample setup windows, without inspecting or rewriting application scenes.
    /// </summary>
    internal static class MixedSpaceSampleSetup
    {
        internal static bool IssueRow(string title, string detail, MessageType severity, string action, bool disabled = false)
        {
            bool clicked;
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label(EditorGUIUtility.IconContent(severity == MessageType.Error ? "console.erroricon.sml" : "console.warnicon.sml"), GUILayout.Width(20), GUILayout.Height(20));
                    EditorGUILayout.LabelField((severity == MessageType.Error ? "错误 · " : "警告 · ") + title, EditorStyles.boldLabel);
                    using (new EditorGUI.DisabledScope(disabled))
                        clicked = GUILayout.Button(new GUIContent(action, detail), GUILayout.Width(52), GUILayout.Height(20));
                }
                if (!string.IsNullOrEmpty(detail)) EditorGUILayout.LabelField(detail, EditorStyles.wordWrappedLabel);
            }
            return clicked;
        }

        internal static bool Busy => EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode;
        internal static bool Installed(string package) => UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages().Any(p => p.name == package);
        internal static Type FindType(string name) => AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(name, false)).FirstOrDefault(t => t != null);
        internal static bool AndroidReady => EditorUserBuildSettings.activeBuildTarget == BuildTarget.Android &&
            PlayerSettings.GetScriptingBackend(BuildTargetGroup.Android) == ScriptingImplementation.IL2CPP &&
            PlayerSettings.Android.targetArchitectures == AndroidArchitecture.ARM64;
        internal static bool GlesReady => !PlayerSettings.GetUseDefaultGraphicsAPIs(BuildTarget.Android) &&
            PlayerSettings.GetGraphicsAPIs(BuildTarget.Android).SequenceEqual(new[] { GraphicsDeviceType.OpenGLES3 });
        internal static bool LoaderReady(BuildTargetGroup group, string type)
        {
            var settings = XRGeneralSettingsPerBuildTarget.XRGeneralSettingsForBuildTarget(group);
            return settings != null && settings.Manager != null && settings.InitManagerOnStart && settings.Manager.activeLoaders.Count == 1 &&
                settings.Manager.activeLoaders[0] != null && settings.Manager.activeLoaders[0].GetType().FullName == type;
        }

        internal static void FixLoader(BuildTargetGroup group, string type)
        {
            if (FindType(type) == null) throw new InvalidOperationException("请先安装提供 " + type + " 的厂商或 Unity 包。");
            if (!EditorBuildSettings.TryGetConfigObject(XRGeneralSettings.k_SettingsKey, out XRGeneralSettingsPerBuildTarget all))
            {
                var existing = AssetDatabase.FindAssets("t:XRGeneralSettingsPerBuildTarget");
                if (existing.Length > 1) throw new InvalidOperationException("发现多份 XR 设置，请先在 XR Plug-in Management 选择有效配置。");
                if (existing.Length == 1) all = AssetDatabase.LoadAssetAtPath<XRGeneralSettingsPerBuildTarget>(AssetDatabase.GUIDToAssetPath(existing[0]));
                else
                {
                    all = ScriptableObject.CreateInstance<XRGeneralSettingsPerBuildTarget>();
                    Directory.CreateDirectory("Assets/XR/Settings");
                    AssetDatabase.Refresh();
                    AssetDatabase.CreateAsset(all, AssetDatabase.GenerateUniqueAssetPath("Assets/XR/Settings/XRGeneralSettingsPerBuildTarget.asset"));
                }
                EditorBuildSettings.AddConfigObject(XRGeneralSettings.k_SettingsKey, all, true);
            }
            if (all.SettingsForBuildTarget(group) == null) all.CreateDefaultSettingsForBuildTarget(group);
            var settings = all.SettingsForBuildTarget(group);
            if (settings.Manager == null) all.CreateDefaultManagerSettingsForBuildTarget(group);
            // Fix 明确选择此端的 Loader，只修改对应目标组，保留其他平台设置。
            // Fix explicitly selects this loader for its target group, preserving other platform groups.
            if (!XRPackageMetadataStore.AssignLoader(settings.Manager, type, group)) throw new InvalidOperationException("无法配置 XR Loader：" + type);
            foreach (var loader in settings.Manager.activeLoaders.ToArray())
                if (loader != null && loader.GetType().FullName != type && !XRPackageMetadataStore.RemoveLoader(settings.Manager, loader.GetType().FullName, group))
                    throw new InvalidOperationException("未能移除冲突 Loader：" + loader.name);
            settings.InitManagerOnStart = true;
            settings.Manager.automaticLoading = true;
            settings.Manager.automaticRunning = true;
            EditorUtility.SetDirty(settings); EditorUtility.SetDirty(settings.Manager); EditorUtility.SetDirty(all);
            AssetDatabase.SaveAssets();
        }

        internal static void FixAndroid(bool gles = false, string loader = null)
        {
            if (!BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.Android, BuildTarget.Android))
                throw new InvalidOperationException("请先通过 Unity Hub 安装 Android Build Support。");
            PlayerSettings.SetScriptingBackend(BuildTargetGroup.Android, ScriptingImplementation.IL2CPP);
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
            if (gles)
            {
                PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android, false);
                PlayerSettings.SetGraphicsAPIs(BuildTarget.Android, new[] { GraphicsDeviceType.OpenGLES3 });
            }
            MixedSpacePicoSetupWindow.RepairCameraPermission();
            if (loader != null) FixLoader(BuildTargetGroup.Android, loader);
            if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.Android &&
                !EditorUserBuildSettings.SwitchActiveBuildTargetAsync(BuildTargetGroup.Android, BuildTarget.Android))
                throw new InvalidOperationException("Android 切换失败，请检查 Build Support。");
        }
    }
}
