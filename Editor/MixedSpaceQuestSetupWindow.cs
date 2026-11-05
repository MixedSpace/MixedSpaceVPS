#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEngine;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace MixedSpace.Editor
{
    /// <summary>The package-level UI deliberately has no compile-time Meta dependency.</summary>
    public sealed class MixedSpaceQuestSetupWindow : EditorWindow
    {
        internal const string SceneGuid = "33268a0ce5234e288161740b7ee44f0d";
        internal const string PlatformGuid = "c47e04a19d8b41778450d37979282ebb";
        private const string Pending = "MixedSpace.Quest.DependenciesPending";
        private const string ConfigurePending = "MixedSpace.Quest.ConfigurePending";
        private const string SwitchStarted = "MixedSpace.Quest.SwitchStarted";
        private const string Notice = "MixedSpace.Quest.OpenSetupOnFirstImport";
        private const string MessageKey = "MixedSpace.Quest.Message";
        private const string Core = "com.meta.xr.sdk.core";
        private const string Mruk = "com.meta.xr.mrutilitykit";
        private const string OpenXr = "com.unity.xr.openxr";
        private const string Input = "com.unity.inputsystem";
        private const string FontGuid = "8f586378b4e144a9851e7b34d9b748ee";
        private static AddRequest s_Add;
        private static string s_AddName;
        private Vector2 m_Scroll;
        private double m_NextCheck;
        private string[] m_Configuration = Array.Empty<string>();
        internal static bool SampleImported => AssetDatabase.GUIDToAssetPath(SceneGuid).StartsWith("Assets/", StringComparison.Ordinal);
        private static string ImportNoticeKey => "MixedSpace.Quest.SampleImported." + Application.dataPath;
        private static Type SetupType => AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly => assembly.GetType("QuestVPS.Editor.QuestVpsProjectSetup", false)).FirstOrDefault(type => type != null);

        [MenuItem("MixedSpaceSDK/Adapters/Quest 设置与依赖检查")]
        public static void Open() => GetWindow<MixedSpaceQuestSetupWindow>("Quest 设置");

        [InitializeOnLoadMethod]
        private static void Initialize()
        {
            EditorApplication.update -= Resume;
            EditorApplication.update += Resume;
            EditorApplication.delayCall -= RememberExistingSample;
            EditorApplication.delayCall += RememberExistingSample;
        }

        private static void RememberExistingSample()
        {
            // Keep the import notice across editor restarts, scoped to this project.
            // An existing sample is already imported when this fix is installed.
            if (!EditorPrefs.HasKey(ImportNoticeKey))
                EditorPrefs.SetBool(ImportNoticeKey, SampleImported);
        }

        private static string Version(string name) => PackageInfo.GetAllRegisteredPackages().FirstOrDefault(package => package.name == name)?.version;
        private static string RequiredVersion(string name) => name == OpenXr ? "1.14.3" : name == Input ? "1.11.2" : "81.0.0";
        private static string MinimumVersion(string name) => name == Input ? "1.6.3" : RequiredVersion(name);
        private static bool MeetsMinimum(string installed, string minimum)
        {
            if (string.IsNullOrWhiteSpace(installed)) return false;
            string release = installed.Split('+')[0];
            int suffix = release.IndexOf('-');
            string numeric = suffix >= 0 ? release.Substring(0, suffix) : release;
            if (!System.Version.TryParse(numeric, out var actual) || !System.Version.TryParse(minimum, out var required)) return false;
            int comparison = actual.CompareTo(required);
            return comparison > 0 || (comparison == 0 && suffix < 0);
        }
        private static bool Ready(string name) => MeetsMinimum(Version(name), MinimumVersion(name));
        private static bool FontReady => !string.IsNullOrEmpty(AssetDatabase.GUIDToAssetPath(FontGuid));

        internal static string[] CheckVersionWarnings()
        {
            var warnings = new System.Collections.Generic.List<string>();
            foreach (string name in new[] { Core, Mruk, OpenXr, Input })
                if (Ready(name) && Version(name) != RequiredVersion(name))
                    warnings.Add(name + " 当前 " + Version(name) + "；本示例已验证 " + RequiredVersion(name) + "。请确认接口兼容，允许继续配置。");
            if (Ready(Core) && Ready(Mruk) && Version(Core) != Version(Mruk))
                warnings.Add("Meta Core 与 MRUK 版本不同，请核对厂商配套关系；此提示不阻止配置。");
            return warnings.ToArray();
        }

        internal static string[] CheckDependencies()
        {
            var issues = new System.Collections.Generic.List<string>();
            if (!SampleImported) issues.Add("请通过 Package Manager 导入 QuestAdapter。");
            foreach (string name in new[] { Core, Mruk, OpenXr, Input })
                if (!Ready(name)) issues.Add(name + " 需要至少 " + MinimumVersion(name) + "，当前：" + (Version(name) ?? "未安装"));
            if (!FontReady) issues.Add("缺少 TMP 示例字体资源。");
            if (SetupType == null && issues.Count == 0) issues.Add("请等待 Quest 脚本编译完成，或检查 Console。");
            return issues.ToArray();
        }

        internal static object InvokeSetup(string method)
        {
            MethodInfo action = SetupType?.GetMethod(method, BindingFlags.Public | BindingFlags.Static);
            if (action == null) throw new InvalidOperationException("Quest 示例未就绪，请先安装对应版本依赖并等待编译。");
            try { return action.Invoke(null, null); }
            catch (TargetInvocationException error) { throw error.InnerException ?? error; }
        }

        internal static void BeginInstall()
        {
            if (!SampleImported) throw new InvalidOperationException("请先导入 QuestAdapter。");
            foreach (string name in new[] { Core, Mruk })
                if (Version(name) != null && !Ready(name))
                    throw new InvalidOperationException(name + " 需要至少 " + MinimumVersion(name) + "，请先在 Package Manager 升级。");
            if (new[] { Core, Mruk, OpenXr, Input }.All(Ready))
            {
                SessionState.SetString(MessageKey, "依赖已满足最低要求，保留当前版本。");
                return;
            }
            foreach (string name in new[] { Core, Mruk })
                if (!Ready(name)) VerifyArchive(name);
            SessionState.SetBool(Pending, true);
            SessionState.SetString(MessageKey, "正在安装依赖；完成后请点击配置工程。");
        }

        private static string Archive(string name) => Path.Combine(MixedSpaceXVXRDependencyInstaller.PackageRoot(), "Dependencies~", name + "-81.0.0.tgz");

        private static void VerifyArchive(string name)
        {
            string path = Archive(name);
            string expected = name == Core ? "39f133f32b31e3da2216cb4ce00916cb80e0aea9b21435a0510a995429c9f01f" : "fb36129d9c14fa97b9be6e7b3ffaaaf42f96e1001f65079a206fedd8085bad3f";
            using (var hash = SHA256.Create())
            using (var input = File.OpenRead(path))
                if (!string.Equals(BitConverter.ToString(hash.ComputeHash(input)).Replace("-", ""), expected, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Meta 依赖压缩包 SHA256 校验失败：" + name);
        }

        internal static void BeginConfigure()
        {
            string[] issues = CheckDependencies();
            if (issues.Length != 0) throw new InvalidOperationException(string.Join("; ", issues));
            if (EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlaying)
                throw new InvalidOperationException("请等待导入、编译或运行结束。");
            // Preflight is also performed inside Configure, before writing settings.
            SessionState.SetBool(SwitchStarted, false);
            SessionState.SetBool(ConfigurePending, true);
            SessionState.SetString(MessageKey, "正在配置 Quest 工程。");
        }

        private static void Resume()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlaying) return;
            try
            {
                if (SessionState.GetBool(Pending, false))
                {
                    if (s_Add != null)
                    {
                        if (!s_Add.IsCompleted) return;
                        if (s_Add.Status == StatusCode.Failure)
                            throw new InvalidOperationException(s_AddName + ": " + s_Add.Error?.message);
                        s_Add = null;
                        return; // Let PackageInfo catch up before choosing the next dependency.
                    }
                    foreach (string name in new[] { Core, Mruk, OpenXr, Input })
                    {
                        if (Ready(name)) continue;
                        s_AddName = name;
                        bool bundled = name == Core || name == Mruk;
                        if (bundled) VerifyArchive(name);
                        s_Add = Client.Add(bundled ? "file:" + Archive(name).Replace('\\', '/') : name + "@" + RequiredVersion(name));
                        return;
                    }
                    SessionState.SetBool(Pending, false);
                    SessionState.SetString(MessageKey, "依赖安装完成，等待编译后可配置工程。");
                }
                if (SessionState.GetBool(ConfigurePending, false))
                {
                    // Persist before switching targets, which can cause a domain reload.
                    if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.Android)
                    {
                        if (SessionState.GetBool(SwitchStarted, false)) return;
                        SessionState.SetBool(SwitchStarted, true);
                        if (!EditorUserBuildSettings.SwitchActiveBuildTargetAsync(BuildTargetGroup.Android, BuildTarget.Android))
                            throw new InvalidOperationException("无法切换到 Android，请安装 Android Build Support。");
                        return;
                    }
                    SessionState.SetBool(ConfigurePending, false);
                    InvokeSetup("Configure");
                    SessionState.SetString(MessageKey, "Quest 工程配置完成。请打开示例场景并填写自己的 Token。");
                }
                if (SessionState.GetBool(Notice, false))
                {
                    SessionState.SetBool(Notice, false);
                    if (!Application.isBatchMode) Open();
                }
            }
            catch (Exception error)
            {
                s_Add = null;
                SessionState.SetBool(Pending, false);
                SessionState.SetBool(ConfigurePending, false);
                SessionState.SetString(MessageKey, error.Message);
                Debug.LogError("Quest setup: " + error.Message);
            }
        }

        private void OnInspectorUpdate()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.timeSinceStartup < m_NextCheck) return;
            m_NextCheck = EditorApplication.timeSinceStartup + 2;
            try { m_Configuration = SetupType != null ? (string[])InvokeSetup("CheckConfiguration") : Array.Empty<string>(); }
            catch (Exception error) { m_Configuration = new[] { error.Message }; }
            Repaint();
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("待处理项目", EditorStyles.boldLabel);
            m_Scroll = EditorGUILayout.BeginScrollView(m_Scroll);
            bool busy = EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlaying ||
                SessionState.GetBool(Pending, false) || SessionState.GetBool(ConfigurePending, false);
            using (new EditorGUI.DisabledScope(busy))
            {
                if (!SampleImported) EditorGUILayout.LabelField("请先导入 QuestAdapter 示例。");
                else
                {
                    if (!Ready(Core) || !Ready(Mruk) || !Ready(OpenXr) || !Ready(Input))
                    {
                        EditorGUILayout.HelpBox("最低版本：Meta Core / MRUK 81.0.0、OpenXR 1.14.3、Input System 1.6.3。满足最低要求的已有版本保留。", MessageType.Info);
                        if (GUILayout.Button("安装 Meta / OpenXR 依赖")) Run(BeginInstall);
                    }
                    if (!FontReady && GUILayout.Button("导入 TMP 资源"))
                        Run(() => MixedSpaceXVXRDependencyInstaller.ImportMissing(MixedSpaceXVXRDependencyInstaller.DependencyGroup.TextMeshPro));
                    string[] issues = CheckDependencies();
                    foreach (string issue in issues) EditorGUILayout.LabelField(issue, EditorStyles.wordWrappedLabel);
                    string[] warnings = CheckVersionWarnings();
                    if (warnings.Length > 0) EditorGUILayout.HelpBox(string.Join("\n", warnings), MessageType.Warning);
                    if (issues.Length == 0)
                    {
                        foreach (string issue in m_Configuration) EditorGUILayout.LabelField(issue, EditorStyles.wordWrappedLabel);
                        if (m_Configuration.Length > 0 && GUILayout.Button("配置工程")) Run(BeginConfigure);
                        if (m_Configuration.Length == 0) EditorGUILayout.LabelField("当前检测项已通过。");
                        if (GUILayout.Button("打开 Quest 示例场景"))
                            Run(() =>
                            {
                                if (UnityEditor.SceneManagement.EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                                    UnityEditor.SceneManagement.EditorSceneManager.OpenScene(AssetDatabase.GUIDToAssetPath(SceneGuid));
                            });
                    }
                }
            }
            string message = SessionState.GetString(MessageKey, string.Empty);
            if (!string.IsNullOrEmpty(message)) EditorGUILayout.HelpBox(message, MessageType.Info);
            EditorGUILayout.EndScrollView();
        }

        private static void Run(Action action)
        {
            try { action(); }
            catch (Exception error) { SessionState.SetString(MessageKey, error.Message); }
        }

        internal static void Imported()
        {
            RememberExistingSample();
            if (!SampleImported || EditorPrefs.GetBool(ImportNoticeKey, true)) return;
            EditorPrefs.SetBool(ImportNoticeKey, true);
            SessionState.SetBool(Notice, true);
        }
    }

    internal sealed class MixedSpaceQuestImportNotice : AssetPostprocessor
    {
        private static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] oldPaths)
        {
            if (imported.Any(path => path.StartsWith("Assets/", StringComparison.Ordinal) && AssetDatabase.AssetPathToGUID(path) == MixedSpaceQuestSetupWindow.SceneGuid))
                MixedSpaceQuestSetupWindow.Imported();
        }
    }
}
#endif
