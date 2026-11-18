using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEditor.SceneManagement;
using UnityEditor.XR.Management;
using UnityEngine;

namespace MixedSpace.Editor
{
    /// <summary>检测并配置 XREAL Android XR 开发环境。</summary>
    public sealed class MixedSpaceXrealSetupWindow : EditorWindow, IHasCustomMenu
    {
        internal const string SceneGuid = "c572f09e2f27e3e44880664d2e9f75ef";
        private const string PendingKey = "MixedSpace.XREAL.SetupPending";
        private const string FixStageKey = "MixedSpace.XREAL.FixStage";
        private const string StageDeadlineKey = "MixedSpace.XREAL.StageDeadline";
        private const string StatusKey = "MixedSpace.XREAL.FixStatus";
        private const string ErrorKey = "MixedSpace.XREAL.FixError";
        private const string AndroidXrPackage = "com.unity.xr.androidxr-openxr";
        private const string AndroidXrFeatureGroup = "com.unity.openxr.featureset.android";
        private const string Loader = "UnityEngine.XR.OpenXR.OpenXRLoader";
        private const string FeatureNamespace = "UnityEngine.XR.OpenXR.Features.Android.";
        private const string MainManifest = "Assets/Plugins/Android/AndroidManifest.xml";
        private const string PermissionLibrary = "Assets/Plugins/Android/MixedSpaceXrealPermissions.androidlib";
        private const string AndroidNs = "http://schemas.android.com/apk/res/android";
        private const string ToolsNs = "http://schemas.android.com/tools";
        private static readonly string[] Packages = {
            "com.unity.xr.management", "com.unity.xr.openxr", AndroidXrPackage,
            "com.unity.xr.arfoundation", "com.unity.xr.core-utils", "com.unity.inputsystem",
            "com.unity.render-pipelines.universal"
        };
        private static readonly string[] Features = { "AndroidXRSupportFeature", "ARSessionFeature", "ARCameraFeature" };
        private enum FixStage { Idle, Start, InstallingPackages, SwitchingAndroid }
        private static Request s_Install;
        private static FixStage Stage
        {
            get => (FixStage)SessionState.GetInt(FixStageKey, 0);
            set => SessionState.SetInt(FixStageKey, (int)value);
        }
        private int m_Issues;
        private Vector2 m_Scroll;

        [MenuItem("MixedSpaceSDK/Adapters/XREAL 设置与依赖检查")]
        public static void Open() => GetWindow<MixedSpaceXrealSetupWindow>("XREAL 设置");

        [InitializeOnLoadMethod]
        private static void Initialize()
        {
            EditorApplication.delayCall += OpenPending;
            EditorApplication.update += ResumeFixAll;
        }

        internal static void NotifyImport()
        {
            SessionState.SetBool(PendingKey, true);
            EditorApplication.delayCall -= OpenPending;
            EditorApplication.delayCall += OpenPending;
        }

        private static void OpenPending()
        {
            if (Application.isBatchMode || !SessionState.GetBool(PendingKey, false)) return;
            if (MixedSpaceSampleSetup.Busy) { EditorApplication.delayCall += OpenPending; return; }
            SessionState.SetBool(PendingKey, false);
            Open();
        }

        private void OnInspectorUpdate() => Repaint();

        private void OnGUI()
        {
            EditorGUILayout.LabelField("XREAL Aura · Android XR 环境设置", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("只显示待处理项。Fix 仅配置 Android 项目；不会修改地图、Token、内参或相机外参。此适配器使用 Unity OpenXR + Android XR，不安装旧 NRSDK。", MessageType.Info);
            bool busy = MixedSpaceSampleSetup.Busy || s_Install != null || Stage != FixStage.Idle;
            string error = SessionState.GetString(ErrorKey, "");
            string status = SessionState.GetString(StatusKey, "");
            if (!string.IsNullOrEmpty(error)) EditorGUILayout.HelpBox(error, MessageType.Error);
            else if (busy)
                EditorGUILayout.HelpBox(string.IsNullOrEmpty(status) ? "正在编译或导入资源，完成后自动重新检查…" : status, MessageType.Info);
            m_Scroll = EditorGUILayout.BeginScrollView(m_Scroll);
            m_Issues = 0;
            using (new EditorGUI.DisabledScope(busy))
            {
                try { DrawIssues(); }
                catch (Exception e) { m_Issues++; EditorGUILayout.HelpBox("检测失败：" + e.Message, MessageType.Error); }
                if (MixedSpaceSampleSetup.FixAllButton(m_Issues)) Run(BeginFixAll);
                if (!busy && m_Issues == 0)
                    EditorGUILayout.HelpBox(MixedSpaceSampleSetup.ReadyMessage, MessageType.Info);
            }
            EditorGUILayout.EndScrollView();
        }

        public void AddItemsToMenu(GenericMenu menu)
        {
            menu.AddItem(new GUIContent("打开 XR 设置 / Project Validation"), false, OpenXrSettings);
            menu.AddItem(new GUIContent("打开 XREAL 示例场景"), false, () => Run(OpenSample));
        }

        private void DrawIssues()
        {
            if (!UnityReady)
                Issue("Unity 版本不满足 Android XR 示例要求", "使用 Unity 6000.0.23f1 或更新版本，并确认所选 Android XR 包支持当前编辑器。", () => EditorApplication.ExecuteMenuItem("Window/Package Manager"), "打开", MessageType.Error);
            if (!BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.Android, BuildTarget.Android))
                Issue("缺少 Android Build Support", "通过 Unity Hub 为当前编辑器安装 Android Build Support、SDK/NDK 和 OpenJDK。", () => Application.OpenURL("unityhub://"), "打开");
            foreach (string id in Packages)
            {
                if (MixedSpaceSampleSetup.Installed(id)) continue;
                string package = id;
                Issue("缺少 " + id, "由 Package Manager 解析当前编辑器兼容版本；已有版本不自动升级。", () =>
                {
                    SessionState.SetString(StatusKey, "正在安装 " + package + "…");
                    s_Install = Client.Add(package);
                });
            }
            string scene = AssetDatabase.GUIDToAssetPath(SceneGuid);
            if (string.IsNullOrEmpty(scene) || !scene.StartsWith("Assets/", StringComparison.Ordinal))
                Issue("尚未导入 XREALAdapter 示例", "在 MixedSpace SDK 的 Package Manager / Samples 中导入 XREALAdapter。", () => EditorApplication.ExecuteMenuItem("Window/Package Manager"), "打开");
            if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.Android)
                Issue("构建目标不是 Android", "切换会重新导入平台资源。", SwitchAndroid, severity: MessageType.Warning);
            if (PlayerSettings.GetScriptingBackend(BuildTargetGroup.Android) != ScriptingImplementation.IL2CPP)
                Issue("Android 未使用 IL2CPP", "定位原生库使用 IL2CPP / ARM64。", () => PlayerSettings.SetScriptingBackend(BuildTargetGroup.Android, ScriptingImplementation.IL2CPP));
            if (PlayerSettings.Android.targetArchitectures != AndroidArchitecture.ARM64)
                Issue("Android 架构不是仅 ARM64", "定位原生库面向 ARM64。", () => PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64);
            if (!InputReady)
                Issue("未启用 Input System", "示例 TrackedPoseDriver 使用 Input System。修改后 Unity 可能要求重启编辑器。", FixInput);
            if (!LoaderReady)
                Issue("Android OpenXR Loader 或启动初始化未就绪", "仅为 Android 选择 OpenXR，并启用 XR 自动加载与运行；替换此目标下的冲突 Loader。", () => MixedSpaceSampleSetup.FixLoader(BuildTargetGroup.Android, Loader));
            if (MixedSpaceSampleSetup.Installed(AndroidXrPackage))
            {
                if (!FeatureGroupReady)
                    Issue("Android XR feature group 未启用", "在 XR Plug-in Management 的 Android 页启用 OpenXR 和 Android XR feature group。", () =>
                    {
                        if (!LoaderReady) MixedSpaceSampleSetup.FixLoader(BuildTargetGroup.Android, Loader);
                        EnableAndroidXrFeatureGroup();
                    });
                foreach (string feature in Features)
                {
                    string name = feature;
                    if (!FeatureReady(name))
                        Issue("Android XR 功能未启用：" + name, "Support 提供 Android XR 支持，Session 和 Camera 用于示例的 AR 会话与透视。", () =>
                        {
                            RefreshAndroidXrFeatures();
                            EnableFeature(name);
                            AssetDatabase.SaveAssets();
                        });
                }
            }
            if (!HasCameraPermission())
                Issue("缺少有效 CAMERA 权限声明", "修复前备份已有清单；补充显式声明并移除该权限上的 tools:node=remove / maxSdkVersion 限制。真机仍需用户授权。", RepairCameraPermission);
        }

        private void Issue(string title, string detail, Action fix, string button = "Fix", MessageType severity = MessageType.Error)
        {
            m_Issues++;
            if (MixedSpaceSampleSetup.IssueRow(title, detail, severity, button)) Run(fix);
        }

        private void Run(Action action)
        {
            SessionState.SetString(ErrorKey, "");
            try { action(); }
            catch (Exception e) { SessionState.SetString(ErrorKey, e.GetBaseException().Message); }
        }

        private static bool UnityReady
        {
            get
            {
                var parts = Application.unityVersion.Split('.');
                if (!int.TryParse(parts[0], out int major)) return false;
                if (major > 6000) return true;
                if (major < 6000 || parts.Length < 3) return false;
                return int.TryParse(parts[1], out int minor) && (minor > 0 ||
                    int.TryParse(new string(parts[2].TakeWhile(char.IsDigit).ToArray()), out int patch) && patch >= 23);
            }
        }

        private static bool LoaderReady
        {
            get
            {
                var settings = XRGeneralSettingsPerBuildTarget.XRGeneralSettingsForBuildTarget(BuildTargetGroup.Android);
                return MixedSpaceSampleSetup.LoaderReady(BuildTargetGroup.Android, Loader) &&
                    settings.Manager.automaticLoading && settings.Manager.automaticRunning;
            }
        }

        private static SerializedObject PlayerConfiguration => new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/ProjectSettings.asset").First());
        private static bool InputReady
        {
            get { var p = PlayerConfiguration.FindProperty("activeInputHandler"); return p != null && (p.intValue == 1 || p.intValue == 2); }
        }

        private static void FixInput()
        {
            var settings = PlayerConfiguration;
            var property = settings.FindProperty("activeInputHandler");
            if (property == null) throw new InvalidOperationException("无法访问 Active Input Handling，请在 Player Settings 手动启用 Input System。");
            property.intValue = 1;
            settings.ApplyModifiedProperties();
            AssetDatabase.SaveAssets();
        }

        // 读取 Android XR 功能配置。
        private static UnityEngine.Object FindFeature(string shortName)
        {
            var type = MixedSpaceSampleSetup.FindType("UnityEngine.XR.OpenXR.OpenXRSettings");
            var settings = type?.GetMethod("GetSettingsForBuildTargetGroup", BindingFlags.Public | BindingFlags.Static,
                    null, new[] { typeof(BuildTargetGroup) }, null)
                ?.Invoke(null, new object[] { BuildTargetGroup.Android });
            if (settings == null) return null;
            // 选择非泛型的无参数功能查询方法。
            var getFeatures = type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .SingleOrDefault(method => method.Name == "GetFeatures" && !method.IsGenericMethod && method.GetParameters().Length == 0);
            if (getFeatures == null) throw new InvalidOperationException("当前 OpenXR 包缺少非泛型 GetFeatures()，请检查包兼容性。");
            var features = getFeatures.Invoke(settings, null) as IEnumerable;
            return features?.Cast<object>().OfType<UnityEngine.Object>().FirstOrDefault(f => f != null && f.GetType().FullName == FeatureNamespace + shortName);
        }

        private static bool FeatureEnabled(UnityEngine.Object feature) =>
            (bool)(feature.GetType().GetProperty("enabled")?.GetValue(feature) ?? false);

        private static bool FeatureReady(string name)
        {
            var feature = FindFeature(name);
            return feature != null && FeatureEnabled(feature);
        }

        private static Type FeatureSetManager =>
            MixedSpaceSampleSetup.FindType("UnityEditor.XR.OpenXR.Features.OpenXRFeatureSetManager");

        private static object FindAndroidXrFeatureGroup(Type manager) =>
            manager?.GetMethod("GetFeatureSetWithId", BindingFlags.Public | BindingFlags.Static,
                null, new[] { typeof(BuildTargetGroup), typeof(string) }, null)
                ?.Invoke(null, new object[] { BuildTargetGroup.Android, AndroidXrFeatureGroup });

        private static bool FeatureGroupReady
        {
            get
            {
                var group = FindAndroidXrFeatureGroup(FeatureSetManager);
                return group?.GetType().GetField("isEnabled")?.GetValue(group) is true;
            }
        }

        private static void EnableAndroidXrFeatureGroup()
        {
            var manager = FeatureSetManager;
            var group = FindAndroidXrFeatureGroup(manager);
            var enabled = group?.GetType().GetField("isEnabled");
            var defaults = group?.GetType().GetField("defaultFeatureIds");
            var apply = manager?.GetMethod("SetFeaturesFromEnabledFeatureSets", BindingFlags.Public | BindingFlags.Static,
                null, new[] { typeof(BuildTargetGroup) }, null);
            if (enabled == null || defaults == null || apply == null)
                throw new InvalidOperationException("无法配置 Android XR feature group。请在 XR Plug-in Management 的 Android 页手动启用，并检查 OpenXR 包版本。");
            if (enabled.GetValue(group) is true) return;

            // 选中功能组时保留已有的可选功能设置，下面的检查项单独启用定位所需功能。
            var originalDefaults = defaults.GetValue(group);
            defaults.SetValue(group, Array.Empty<string>());
            try
            {
                enabled.SetValue(group, true);
                apply.Invoke(null, new object[] { BuildTargetGroup.Android });
            }
            finally
            {
                defaults.SetValue(group, originalDefaults);
            }
            AssetDatabase.SaveAssets();
        }

        private static void RefreshAndroidXrFeatures()
        {
            var helper = MixedSpaceSampleSetup.FindType("UnityEditor.XR.OpenXR.Features.FeatureHelpers");
            var refresh = helper?.GetMethod("RefreshFeatures", BindingFlags.Public | BindingFlags.Static,
                null, new[] { typeof(BuildTargetGroup) }, null);
            if (refresh == null)
                throw new InvalidOperationException("无法刷新 Android OpenXR 功能，请检查 OpenXR 包版本。");
            refresh.Invoke(null, new object[] { BuildTargetGroup.Android });
        }

        private static void EnableFeature(string name)
        {
            var feature = FindFeature(name);
            if (feature == null) throw new InvalidOperationException("未找到 " + name + "。请先安装依赖、配置 Android OpenXR Loader；仍缺失时打开 XR 设置检查包兼容性。");
            if (FeatureEnabled(feature)) return;
            var enabled = feature.GetType().GetProperty("enabled");
            if (enabled == null || !enabled.CanWrite) throw new InvalidOperationException("当前包未公开功能开关，请在 XR 设置中手动启用。");
            Undo.RecordObject(feature, "Enable Android XR feature");
            enabled.SetValue(feature, true);
            EditorUtility.SetDirty(feature);
        }

        private static void SwitchAndroid()
        {
            if (!BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.Android, BuildTarget.Android))
                throw new InvalidOperationException("请先安装 Android Build Support。");
            if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.Android &&
                !EditorUserBuildSettings.SwitchActiveBuildTargetAsync(BuildTargetGroup.Android, BuildTarget.Android))
                throw new InvalidOperationException("Android 构建目标切换失败。");
        }

        private static void BeginFixAll()
        {
            if (!UnityReady) throw new InvalidOperationException("请先使用兼容的 Unity 6 编辑器。");
            if (!BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.Android, BuildTarget.Android))
                throw new InvalidOperationException("请先安装 Android Build Support。");
            Stage = FixStage.Start;
            SessionState.SetString(StatusKey, "正在检查 XREAL 依赖和 Android 配置…");
        }

        private static void ResumeFixAll()
        {
            if (MixedSpaceSampleSetup.Busy || (Stage == FixStage.Idle && s_Install == null)) return;
            try
            {
                if (s_Install != null)
                {
                    if (!s_Install.IsCompleted) return;
                    var request = s_Install;
                    s_Install = null;
                    if (request.Status != StatusCode.Success)
                        throw new InvalidOperationException("依赖安装失败：" + request.Error?.message);
                    if (Stage == FixStage.Idle)
                    {
                        SessionState.SetString(StatusKey, "");
                        return;
                    }
                }

                string[] missing = Packages.Where(p => !MixedSpaceSampleSetup.Installed(p)).ToArray();
                if (Stage == FixStage.Start && missing.Length > 0)
                {
                    Stage = FixStage.InstallingPackages;
                    SessionState.SetFloat(StageDeadlineKey, 0);
                    SessionState.SetString(StatusKey, "正在安装缺少的依赖，完成后继续配置 Android…");
                    s_Install = Client.AddAndRemove(missing);
                    return;
                }
                if (missing.Length > 0)
                {
                    if (Stage == FixStage.InstallingPackages && WaitForStage(30)) return;
                    throw new InvalidOperationException("Package Manager 未解析到依赖：" + string.Join("、", missing));
                }

                if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.Android)
                {
                    if (Stage != FixStage.SwitchingAndroid)
                    {
                        Stage = FixStage.SwitchingAndroid;
                        SessionState.SetFloat(StageDeadlineKey, 0);
                        SessionState.SetString(StatusKey, "正在切换到 Android，完成后继续配置 XR…");
                        SwitchAndroid();
                    }
                    else if (!WaitForStage(300))
                        throw new InvalidOperationException("Android 平台切换未完成，请查看 Build Profiles 或 Console。");
                    return;
                }

                ApplyAndroidConfiguration();
                Stage = FixStage.Idle;
                SessionState.SetString(StatusKey, "");
            }
            catch (Exception e)
            {
                Stage = FixStage.Idle;
                s_Install = null;
                SessionState.SetString(StatusKey, "");
                SessionState.SetString(ErrorKey, e.GetBaseException().Message);
                Debug.LogError("XREAL 环境设置：" + e.GetBaseException().Message);
            }
        }

        private static bool WaitForStage(float timeout)
        {
            float deadline = SessionState.GetFloat(StageDeadlineKey, 0);
            if (deadline > 0) return EditorApplication.timeSinceStartup < deadline;
            SessionState.SetFloat(StageDeadlineKey, (float)EditorApplication.timeSinceStartup + timeout);
            return true;
        }

        private static void ApplyAndroidConfiguration()
        {
            PlayerSettings.SetScriptingBackend(BuildTargetGroup.Android, ScriptingImplementation.IL2CPP);
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
            if (!LoaderReady) MixedSpaceSampleSetup.FixLoader(BuildTargetGroup.Android, Loader);
            if (!FeatureGroupReady) EnableAndroidXrFeatureGroup();
            if (!Features.All(FeatureReady))
            {
                RefreshAndroidXrFeatures();
                foreach (string feature in Features) EnableFeature(feature);
            }
            if (!HasCameraPermission()) RepairCameraPermission();
            if (!InputReady) FixInput();
            AssetDatabase.SaveAssets();
        }

        private static void OpenXrSettings() => SettingsService.OpenProjectSettings("Project/XR Plug-in Management");
        private static void OpenSample()
        {
            string scene = AssetDatabase.GUIDToAssetPath(SceneGuid);
            if (string.IsNullOrEmpty(scene) || !scene.StartsWith("Assets/", StringComparison.Ordinal))
                throw new InvalidOperationException("请先通过 Package Manager 导入 XREALAdapter 示例。");
            if (EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) EditorSceneManager.OpenScene(scene);
        }

        private static XmlDocument ReadManifest(string path)
        {
            var xml = new XmlDocument { PreserveWhitespace = true, XmlResolver = null };
            xml.Load(path);
            if (xml.DocumentElement?.Name != "manifest") throw new InvalidDataException("无效的 Android manifest：" + path);
            return xml;
        }

        internal static bool HasCameraPermission()
        {
            bool found = false;
            foreach (string path in new[] { MainManifest, PermissionLibrary + "/AndroidManifest.xml" })
            {
                if (!File.Exists(path)) continue;
                foreach (XmlElement permission in ReadManifest(path).SelectNodes("/manifest/uses-permission"))
                {
                    if (permission.GetAttribute("node", ToolsNs) == "removeAll") return false;
                    if (permission.GetAttribute("name", AndroidNs) != "android.permission.CAMERA") continue;
                    string operation = permission.GetAttribute("node", ToolsNs);
                    if (operation == "remove" || operation == "removeAll" || permission.HasAttribute("maxSdkVersion", AndroidNs)) return false;
                    found = true;
                }
            }
            return found;
        }

        internal static void RepairCameraPermission()
        {
            // 补充相机权限并修复清单冲突。
            string library = PermissionLibrary + "/AndroidManifest.xml";
            foreach (string path in new[] { MainManifest, library })
                if (File.Exists(path) && ReadManifest(path).SelectNodes("/manifest/uses-permission").Cast<XmlElement>()
                    .Any(p => p.GetAttribute("node", ToolsNs) == "removeAll"))
                    throw new InvalidOperationException("存在全局 uses-permission tools:node=removeAll 规则，请先手动调整清单，避免影响其他权限：" + path);
            foreach (string path in new[] { MainManifest, library })
            {
                if (path == MainManifest && !File.Exists(path)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                XmlDocument xml;
                if (File.Exists(path))
                {
                    xml = ReadManifest(path);
                    Directory.CreateDirectory("Library/MixedSpaceBackups");
                    File.Copy(path, "Library/MixedSpaceBackups/XrealManifest-" + Guid.NewGuid().ToString("N") + ".xml");
                }
                else
                {
                    xml = new XmlDocument { XmlResolver = null };
                    xml.LoadXml("<manifest xmlns:android=\"" + AndroidNs + "\" package=\"com.mixedspace.xreal.permissions\" />");
                }
                var permissions = xml.SelectNodes("/manifest/uses-permission").Cast<XmlElement>()
                    .Where(p => p.GetAttribute("name", AndroidNs) == "android.permission.CAMERA").ToArray();
                if (permissions.Length == 0)
                {
                    var element = xml.CreateElement("uses-permission");
                    element.SetAttribute("name", AndroidNs, "android.permission.CAMERA");
                    xml.DocumentElement.AppendChild(element);
                    permissions = new[] { element };
                }
                foreach (var permission in permissions)
                {
                    permission.RemoveAttribute("node", ToolsNs);
                    permission.RemoveAttribute("maxSdkVersion", AndroidNs);
                }
                xml.Save(path);
            }
            File.WriteAllText(PermissionLibrary + "/project.properties", "android.library=true\n");
            AssetDatabase.Refresh();
        }
    }

    internal sealed class MixedSpaceXrealSetupImportNotice : AssetPostprocessor
    {
        private static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] oldPaths)
        {
            if (Application.isBatchMode) return;
            string key = "MixedSpace.XREAL.SampleSeen." + Application.dataPath;
            if (deleted.Contains(EditorPrefs.GetString(key, ""))) EditorPrefs.DeleteKey(key);
            foreach (string path in imported.Where(p => p.StartsWith("Assets/", StringComparison.Ordinal) && p.EndsWith(".unity", StringComparison.Ordinal)))
            {
                if (AssetDatabase.AssetPathToGUID(path) != MixedSpaceXrealSetupWindow.SceneGuid || EditorPrefs.GetString(key, "") == path) continue;
                EditorPrefs.SetString(key, path);
                MixedSpaceXrealSetupWindow.NotifyImport();
            }
        }
    }
}
