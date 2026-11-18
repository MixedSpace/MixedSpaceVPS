using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.XR.Management;
using UnityEditor.XR.Management.Metadata;
using UnityEditor.XR.OpenXR.Features;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR.Management;
using UnityEngine.XR.OpenXR;
using UnityEngine.XR.OpenXR.Features.Interactions;
using UnityEngine.XR.OpenXR.Features.MetaQuestSupport;

namespace QuestVPS.Editor
{
    public static class QuestVpsProjectSetup
    {
        private const BuildTargetGroup TargetGroup = BuildTargetGroup.Android;
        private const BuildTarget Target = BuildTarget.Android;
        private const AndroidSdkVersions MinimumApi = AndroidSdkVersions.AndroidApiLevel29;
        private const AndroidSdkVersions MinimumTargetApi = AndroidSdkVersions.AndroidApiLevel32;
        private static readonly OVRProjectConfig.DeviceType[] QuestDevices =
        {
            OVRProjectConfig.DeviceType.Quest3,
            OVRProjectConfig.DeviceType.Quest3S
        };

        public static void Configure()
        {
            if (EditorUserBuildSettings.activeBuildTarget != Target)
                throw new InvalidOperationException("请先切换构建目标为 Android。");
            EnsureEditorReady();
            if (HasConflictingLoader(GetXrSettings()))
                throw new InvalidOperationException("请先在 XR Plug-in Management 的 Android 页禁用其他 Loader。");
            using var player = GetPlayerConfiguration();
            if (player?.FindProperty("activeInputHandler") == null)
                throw new InvalidOperationException("无法读取 Active Input Handling，请检查 Unity 编辑器版本。");

            ConfigureOpenXr();
            ConfigureLoader();
            ConfigureMeta();
            ConfigureBuild();
            ConfigureApi();
            ConfigureGraphics();
            ConfigurePlayer();
            ConfigureInput();
            AssetDatabase.SaveAssets();
        }

        public static void FixConfiguration(string issue)
        {
            EnsureEditorReady();
            switch (issue)
            {
                case "Build": ConfigureBuild(); break;
                case "Api": ConfigureApi(); break;
                case "Graphics": ConfigureGraphics(); break;
                case "Player": ConfigurePlayer(); break;
                case "Input": ConfigureInput(); break;
                case "Loader": ConfigureLoader(); break;
                case "OpenXR": ConfigureOpenXr(); break;
                case "Meta": ConfigureMeta(); break;
                default: throw new ArgumentException("无法自动修复此 Quest 配置项。", nameof(issue));
            }
            AssetDatabase.SaveAssets();
        }

        private static void EnsureEditorReady()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode)
                throw new InvalidOperationException("请等待 Unity 完成导入或编译，并退出运行模式。");
        }

        private static void ConfigureOpenXr()
        {
            FeatureHelpers.RefreshFeatures(TargetGroup);
            var openXr = OpenXRSettings.GetSettingsForBuildTargetGroup(TargetGroup);
            var quest = openXr?.GetFeature<MetaQuestFeature>();
            var touch = openXr?.GetFeature<OculusTouchControllerProfile>();
            if (quest == null || touch == null)
                throw new InvalidOperationException("未找到 Android OpenXR 的 Meta Quest Support 或 Oculus Touch Controller Profile。");

            openXr.renderMode = OpenXRSettings.RenderMode.SinglePassInstanced;
            openXr.depthSubmissionMode = OpenXRSettings.DepthSubmissionMode.None;
            quest.enabled = true;
            touch.enabled = true;
            EditorUtility.SetDirty(openXr);
            EditorUtility.SetDirty(quest);
            EditorUtility.SetDirty(touch);
        }

        private static void ConfigureMeta()
        {
            var meta = OVRProjectConfig.CachedProjectConfig;
            if (meta == null)
                throw new InvalidOperationException("无法读取 Meta XR 工程配置，请检查 Meta XR Core SDK。");
            if (MetaReady(meta)) return;
            meta.targetDeviceTypes = QuestDevices.ToList();
            meta.insightPassthroughSupport = OVRProjectConfig.FeatureSupport.Required;
            meta.isPassthroughCameraAccessEnabled = true;
            OVRProjectConfig.CommitProjectConfig(meta);
        }

        private static void ConfigureBuild()
        {
            PlayerSettings.SetScriptingBackend(NamedBuildTarget.Android, ScriptingImplementation.IL2CPP);
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
        }

        private static void ConfigureApi()
        {
            if ((int)PlayerSettings.Android.minSdkVersion < (int)MinimumApi)
                PlayerSettings.Android.minSdkVersion = MinimumApi;
            int requiredTargetApi = Math.Max((int)MinimumTargetApi, (int)PlayerSettings.Android.minSdkVersion);
            if ((int)PlayerSettings.Android.targetSdkVersion < requiredTargetApi)
                PlayerSettings.Android.targetSdkVersion = (AndroidSdkVersions)requiredTargetApi;
        }

        private static void ConfigureGraphics()
        {
            PlayerSettings.SetUseDefaultGraphicsAPIs(Target, false);
            PlayerSettings.SetGraphicsAPIs(Target, new[] { GraphicsDeviceType.OpenGLES3 });
        }

        private static void ConfigurePlayer()
        {
            PlayerSettings.Android.forceInternetPermission = true;
            PlayerSettings.defaultInterfaceOrientation = UIOrientation.LandscapeLeft;
            PlayerSettings.colorSpace = ColorSpace.Linear;
        }

        private static void ConfigureInput()
        {
            using var player = GetPlayerConfiguration();
            var input = player?.FindProperty("activeInputHandler");
            if (input == null)
                throw new InvalidOperationException("无法读取 Active Input Handling，请在 Player Settings 中启用 Input System。");
            if (InputReady(input)) return;
            input.intValue = 1;
            player.ApplyModifiedPropertiesWithoutUndo();
        }

        public static string[] CheckConfiguration() => GetConfigurationIssues().Values.ToArray();

        public static Dictionary<string, string> GetConfigurationIssues()
        {
            var issues = new Dictionary<string, string>();
            if (EditorUserBuildSettings.activeBuildTarget != Target)
                issues.Add("Target", "切换构建目标为 Android，Unity 会重新导入平台资源。");
            if (PlayerSettings.GetScriptingBackend(NamedBuildTarget.Android) != ScriptingImplementation.IL2CPP ||
                PlayerSettings.Android.targetArchitectures != AndroidArchitecture.ARM64)
                issues.Add("Build", "脚本后端使用 IL2CPP，目标架构仅选择 ARM64。");
            if ((int)PlayerSettings.Android.minSdkVersion < (int)MinimumApi ||
                (int)PlayerSettings.Android.targetSdkVersion < Math.Max((int)MinimumTargetApi, (int)PlayerSettings.Android.minSdkVersion))
                issues.Add("Api", "最低 API 至少设为 29，显式设置目标 API 至少为 32，且不低于最低 API。");
            if (PlayerSettings.GetUseDefaultGraphicsAPIs(Target) ||
                !PlayerSettings.GetGraphicsAPIs(Target).SequenceEqual(new[] { GraphicsDeviceType.OpenGLES3 }))
                issues.Add("Graphics", "关闭 Auto Graphics API，仅使用 OpenGLES3。");
            if (!PlayerSettings.Android.forceInternetPermission || PlayerSettings.colorSpace != ColorSpace.Linear ||
                PlayerSettings.defaultInterfaceOrientation != UIOrientation.LandscapeLeft)
                issues.Add("Player", "启用 Internet 权限，使用 Linear 色彩空间和 Landscape Left 方向。");
            using var player = GetPlayerConfiguration();
            if (!InputReady(player?.FindProperty("activeInputHandler")))
                issues.Add("Input", "在 Active Input Handling 中启用 Input System。Unity 可能需要重启。");

            var general = GetXrSettings();
            if (HasConflictingLoader(general))
                issues.Add("LoaderConflict", "在 XR Plug-in Management 的 Android 页禁用其他 XR Loader。");
            else if (general?.Manager == null || !general.InitManagerOnStart ||
                !general.Manager.activeLoaders.Any(loader => loader is OpenXRLoader))
                issues.Add("Loader", "启用 Android OpenXR Loader 和 Initialize XR on Startup。");

            var openXr = OpenXRSettings.GetSettingsForBuildTargetGroup(TargetGroup);
            if (openXr == null || openXr.GetFeature<MetaQuestFeature>()?.enabled != true ||
                openXr.GetFeature<OculusTouchControllerProfile>()?.enabled != true ||
                openXr.renderMode != OpenXRSettings.RenderMode.SinglePassInstanced ||
                openXr.depthSubmissionMode != OpenXRSettings.DepthSubmissionMode.None)
                issues.Add("OpenXR", "启用 Meta Quest Support 和 Oculus Touch Controller Profile，使用 Single Pass Instanced，关闭 Depth Submission。");
            if (!MetaReady(OVRProjectConfig.CachedProjectConfig))
                issues.Add("Meta", "将目标设备设为 Quest 3 和 Quest 3S，并启用 Passthrough 和 Passthrough Camera Access。");
            return issues;
        }

        private static SerializedObject GetPlayerConfiguration()
        {
            var asset = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/ProjectSettings.asset").FirstOrDefault();
            return asset == null ? null : new SerializedObject(asset);
        }

        private static bool InputReady(SerializedProperty input) => input != null && (input.intValue == 1 || input.intValue == 2);

        private static XRGeneralSettings GetXrSettings() => XRGeneralSettingsPerBuildTarget.XRGeneralSettingsForBuildTarget(TargetGroup);

        private static bool HasConflictingLoader(XRGeneralSettings settings) =>
            settings?.Manager != null && settings.Manager.activeLoaders.Any(loader => loader != null && !(loader is OpenXRLoader));

        private static bool MetaReady(OVRProjectConfig meta) =>
            meta != null && meta.targetDeviceTypes != null && meta.targetDeviceTypes.Count == QuestDevices.Length &&
            QuestDevices.All(meta.targetDeviceTypes.Contains) &&
            meta.insightPassthroughSupport == OVRProjectConfig.FeatureSupport.Required && meta.isPassthroughCameraAccessEnabled;

        private static void ConfigureLoader()
        {
            if (HasConflictingLoader(GetXrSettings()))
                throw new InvalidOperationException("请先在 XR Plug-in Management 的 Android 页禁用其他 Loader。");
            if (!EditorBuildSettings.TryGetConfigObject(XRGeneralSettings.k_SettingsKey, out XRGeneralSettingsPerBuildTarget settings) ||
                settings == null)
            {
                string[] assets = AssetDatabase.FindAssets("t:XRGeneralSettingsPerBuildTarget", new[] { "Assets" });
                if (assets.Length > 1)
                    throw new InvalidOperationException("发现多份 XR 设置，请先在 XR Plug-in Management 选择有效配置。");
                if (assets.Length == 1)
                    settings = AssetDatabase.LoadAssetAtPath<XRGeneralSettingsPerBuildTarget>(AssetDatabase.GUIDToAssetPath(assets[0]));
                if (settings == null)
                {
                    Directory.CreateDirectory("Assets/XR/Settings");
                    AssetDatabase.Refresh();
                    settings = ScriptableObject.CreateInstance<XRGeneralSettingsPerBuildTarget>();
                    AssetDatabase.CreateAsset(settings, AssetDatabase.GenerateUniqueAssetPath("Assets/XR/Settings/XRGeneralSettingsPerBuildTarget.asset"));
                }
                EditorBuildSettings.AddConfigObject(XRGeneralSettings.k_SettingsKey, settings, true);
            }

            if (!settings.HasSettingsForBuildTarget(TargetGroup))
                settings.CreateDefaultSettingsForBuildTarget(TargetGroup);
            if (!settings.HasManagerSettingsForBuildTarget(TargetGroup))
                settings.CreateDefaultManagerSettingsForBuildTarget(TargetGroup);
            var general = settings.SettingsForBuildTarget(TargetGroup);
            var manager = general.Manager;
            if (!manager.activeLoaders.Any(loader => loader is OpenXRLoader) &&
                !XRPackageMetadataStore.AssignLoader(manager, typeof(OpenXRLoader).FullName, TargetGroup))
                throw new InvalidOperationException("无法配置 Android OpenXR Loader。");
            general.InitManagerOnStart = true;
            EditorUtility.SetDirty(manager);
            EditorUtility.SetDirty(general);
            EditorUtility.SetDirty(settings);
        }
    }
}
