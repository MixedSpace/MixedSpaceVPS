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
    /// Rokid 可选示例的依赖安装与 Android 设置入口，不引用厂商程序集。
    /// Dependency installation and Android setup for the optional Rokid sample, without vendor
    /// assembly references.
    /// </summary>
    public sealed class MixedSpaceRokidSetupWindow : EditorWindow, IHasCustomMenu
    {
        private const string RegistryUrl = "https://npm.rokid.com";
        private static AddAndRemoveRequest s_Install;
        private Vector2 m_Scroll;
        private string m_Status;
        private bool m_HasError;

        private void OnEnable()
        {
            // 重载后重新检测实际状态，不展示上一次窗口残留的完成提示。
            // Recheck actual state after reload rather than showing a stale completion message.
            m_Status = null;
            m_HasError = false;
        }
        private static readonly string[] Packages = {
            "com.rokid.xr.unity@3.0.3", "com.rokid.openxr@3.0.3",
            "com.unity.xr.openxr@1.13.2", "com.unity.xr.interaction.toolkit@3.0.5",
            "com.unity.xr.management@4.4.1", "com.unity.inputsystem@1.8.1",
            "com.unity.xr.hands@1.5.0" };

        [MenuItem("MixedSpaceSDK/Adapters/Rokid 设置与依赖检查")]
        public static void Open() => GetWindow<MixedSpaceRokidSetupWindow>("Rokid 设置");

        [InitializeOnLoadMethod]
        private static void Initialize()
        {
            EditorApplication.delayCall += OpenPending;
        }

        internal static void OpenPending()
        {
            if (!SessionState.GetBool("MixedSpace.Rokid.SetupPending", false)) return;
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                EditorApplication.delayCall += OpenPending;
                return;
            }
            SessionState.SetBool("MixedSpace.Rokid.SetupPending", false);
            Open();
        }

        // 在安装完成、脚本重载或设置变化后重新读取实际状态，不使用“已点击”作为成功依据。
        // Read actual state after installation, script reload or configuration changes; clicking a button does not prove
        // success.
        private void OnInspectorUpdate()
        {
            Poll();
            Repaint();
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("待处理项目", EditorStyles.boldLabel);
            m_Scroll = EditorGUILayout.BeginScrollView(m_Scroll);
            int issues = 0;
            bool busy = s_Install != null || EditorApplication.isCompiling ||
                EditorApplication.isUpdating || EditorApplication.isPlaying;
            using (new EditorGUI.DisabledScope(busy))
            {
                try
                {
                    var manifest = JsonUtility.FromJson<Manifest>(File.ReadAllText("Packages/manifest.json"));
                    bool configured = (manifest.scopedRegistries ?? Array.Empty<Registry>()).Any(r =>
                        r.name == "rokid" && r.url?.TrimEnd('/') == RegistryUrl &&
                        r.scopes != null && r.scopes.Contains("com.rokid"));
                    if (!configured)
                    {
                        issues++;
                        Issue("Rokid Registry 未配置",
                            "需要 rokid / https://npm.rokid.com / com.rokid。冲突配置不会自动覆盖。",
                            "修复", () => { ConfigureRegistry(); Client.Resolve(); }, MessageType.Warning);
                    }
                }
                catch (Exception e)
                {
                    issues++;
                    Issue("Registry 读取失败", e.Message, "打开设置",
                        () => SettingsService.OpenProjectSettings("Project/Package Manager"));
                }

                var installed = UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages();
                foreach (var spec in Packages)
                {
                    var parts = spec.Split('@');
                    var package = installed.FirstOrDefault(p => p.name == parts[0]);
                    if (package != null && package.version == parts[1]) continue;
                    issues++;
                    Issue(parts[0], package == null ? "尚未安装，所需版本：" + parts[1] :
                        "当前 " + package.version + "，所需 " + parts[1],
                        package == null ? "安装" : "修复版本", () => Install(spec), package == null ? MessageType.Error : MessageType.Warning);
                }
                if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.Android)
                {
                    issues++;
                    Issue("构建目标不是 Android", "切换目标会重新导入相关资源。", "修复",
                        () => EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Android, BuildTarget.Android), MessageType.Warning);
                }
                if (PlayerSettings.GetScriptingBackend(BuildTargetGroup.Android) != ScriptingImplementation.IL2CPP)
                {
                    issues++;
                    Issue("Android 脚本后端", "需要 IL2CPP。", "修复",
                        () => PlayerSettings.SetScriptingBackend(BuildTargetGroup.Android, ScriptingImplementation.IL2CPP));
                }
                if (PlayerSettings.Android.targetArchitectures != AndroidArchitecture.ARM64)
                {
                    issues++;
                    Issue("Android CPU 架构", "需要仅勾选 ARM64。", "修复",
                        () => PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64);
                }
                var apis = PlayerSettings.GetGraphicsAPIs(BuildTarget.Android);
                if (PlayerSettings.GetUseDefaultGraphicsAPIs(BuildTarget.Android) || apis.Length != 1 ||
                    apis[0] != UnityEngine.Rendering.GraphicsDeviceType.OpenGLES3)
                {
                    issues++;
                    Issue("Android 图形 API", "关闭自动选择，使用 OpenGLES3。", "修复", () =>
                    {
                        PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android, false);
                        PlayerSettings.SetGraphicsAPIs(BuildTarget.Android, new[] { UnityEngine.Rendering.GraphicsDeviceType.OpenGLES3 });
                    });
                }
                if (MixedSpaceSampleSetup.FixAllButton(issues))
                {
                    try
                    {
                        m_Status = null; m_HasError = false;
                        ConfigureRegistry();
                        MixedSpaceSampleSetup.FixAndroid(true);
                        var current = UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages();
                        var missing = Packages.Where(spec => {
                            var parts = spec.Split('@');
                            return !current.Any(p => p.name == parts[0] && p.version == parts[1]);
                        }).ToArray();
                        if (missing.Length > 0) s_Install = Client.AddAndRemove(missing);
                        else Client.Resolve();
                    }
                    catch (Exception e) { m_Status = e.Message; m_HasError = true; }
                }
            }
            if (busy) EditorGUILayout.HelpBox("正在安装、编译或导入资源，请稍候。完成后自动重新检测。", MessageType.Info);
            else if (issues == 0) EditorGUILayout.HelpBox(MixedSpaceSampleSetup.ReadyMessage, MessageType.Info);
            if (m_HasError && !string.IsNullOrEmpty(m_Status))
            {
                EditorGUILayout.HelpBox(m_Status, MessageType.Error);
                if (GUILayout.Button("关闭错误提示")) { m_Status = null; m_HasError = false; }
            }
            EditorGUILayout.EndScrollView();
        }

        public void AddItemsToMenu(GenericMenu menu)
        {
            menu.AddItem(new GUIContent("打开 XR 设置（手动检查 Loader / Rokid Features）"), false,
                () => SettingsService.OpenProjectSettings("Project/XR Plug-in Management"));
        }

        private void Issue(string title, string detail, string button, Action fix, MessageType severity = MessageType.Error)
        {
            if (MixedSpaceSampleSetup.IssueRow(title, detail, severity, button.StartsWith("打开") ? "打开" : "Fix"))
            {
                try { m_Status = null; m_HasError = false; fix(); }
                catch (Exception e) { m_Status = e.Message; m_HasError = true; }
                Repaint();
            }
        }

        private void Install(string spec)
        {
            if (s_Install != null) return;
            if (!EditorUtility.DisplayDialog("安装依赖",
                "安装 " + spec + "，Package Manager 可能调整其传递依赖版本。继续？", "安装", "取消")) return;
            ConfigureRegistry();
            s_Install = Client.AddAndRemove(new[] { spec });
            m_Status = null;
            m_HasError = false;
        }

        private void Poll()
        {
            if (s_Install == null || !s_Install.IsCompleted) return;
            m_Status = s_Install.Status == StatusCode.Success ? null : s_Install.Error?.message ?? "依赖安装失败，请查看 Package Manager。";
            m_HasError = s_Install.Status != StatusCode.Success;
            s_Install = null;
            // 成功项是否消失由 GetAllRegisteredPackages 的实际版本决定。
            // Completed items disappear based on the actual version returned by GetAllRegisteredPackages.
            Repaint();
        }

        [Serializable] private sealed class Registry { public string name; public string url; public string[] scopes; }
        [Serializable] private sealed class Manifest { public Registry[] scopedRegistries; }

        // 保留 manifest 中未识别的字段；只在 scopedRegistries 数组中追加条目。
        // Preserve unknown manifest fields; append entries only within scopedRegistries.
        private static void ConfigureRegistry()
        {
            const string path = "Packages/manifest.json";
            string json = File.ReadAllText(path);
            var manifest = JsonUtility.FromJson<Manifest>(json);
            foreach (var registry in manifest.scopedRegistries ?? Array.Empty<Registry>())
            {
                if (registry.scopes != null && registry.scopes.Contains("com.rokid"))
                {
                    if (registry.url?.TrimEnd('/') != RegistryUrl)
                        throw new InvalidOperationException("com.rokid 已配置到其他 registry，请先在 Package Manager 设置中确认地址，未覆盖该配置。");
                    if (registry.name != "rokid")
                        throw new InvalidOperationException("com.rokid 的 registry 名称不是 rokid，请在 Package Manager 设置中改名。");
                    return;
                }
                if (registry.name == "rokid") throw new InvalidOperationException("已存在不同配置的 rokid registry，请先在 Package Manager 设置中检查。");
            }
            string entry = "{\"name\":\"rokid\",\"url\":\"https://npm.rokid.com\",\"scopes\":[\"com.rokid\"]}";
            int key = json.IndexOf("\"scopedRegistries\"", StringComparison.Ordinal);
            if (key >= 0)
            {
                int start = json.IndexOf('[', key);
                if (start < 0) throw new InvalidOperationException("scopedRegistries 必须为数组。");
                int end = FindArrayEnd(json, start);
                bool empty = string.IsNullOrWhiteSpace(json.Substring(start + 1, end - start - 1));
                json = json.Insert(end, (empty ? "" : ",") + "\n" + entry);
            }
            else
            {
                int end = json.LastIndexOf('}');
                if (end < 0) throw new InvalidOperationException("manifest JSON 无效。");
                json = json.Insert(end, ",\n\"scopedRegistries\":[" + entry + "]\n");
            }
            JsonUtility.FromJson<Manifest>(json);
            Directory.CreateDirectory("Library/MixedSpaceBackups");
            File.Copy(path, "Library/MixedSpaceBackups/manifest-" + Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(path, json);
        }

        private static int FindArrayEnd(string json, int start)
        {
            int depth = 0; bool quoted = false, escaped = false;
            for (int i = start; i < json.Length; i++)
            {
                char c = json[i];
                if (quoted) { if (escaped) escaped = false; else if (c == '\\') escaped = true; else if (c == '"') quoted = false; continue; }
                if (c == '"') quoted = true;
                else if (c == '[') depth++;
                else if (c == ']' && --depth == 0) return i;
            }
            throw new InvalidOperationException("Registry 数组未闭合。");
        }

        private static void ConfigureAndroid()
        {
            if (!EditorUtility.DisplayDialog("Android 设置", "设置 IL2CPP、仅 ARM64、OpenGLES3。不会切换 XR Loader 或覆盖 iOS 设置。继续？", "设置", "取消")) return;
            PlayerSettings.SetScriptingBackend(BuildTargetGroup.Android, ScriptingImplementation.IL2CPP);
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android, false);
            PlayerSettings.SetGraphicsAPIs(BuildTarget.Android, new[] { UnityEngine.Rendering.GraphicsDeviceType.OpenGLES3 });
        }
    }

    internal sealed class MixedSpaceRokidSetupImportNotice : AssetPostprocessor
    {
        private static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] oldPaths)
        {
            if (Application.isBatchMode || !imported.Any(p => p.StartsWith("Assets/", StringComparison.Ordinal) &&
                (p.EndsWith("/MixedSpaceRokidDemo.unity", StringComparison.Ordinal) || p.EndsWith("/MixedSpaceRokidAdapter.cs", StringComparison.Ordinal)))) return;
            SessionState.SetBool("MixedSpace.Rokid.SetupPending", true);
            EditorApplication.delayCall += MixedSpaceRokidSetupWindow.OpenPending;
        }
    }
}
