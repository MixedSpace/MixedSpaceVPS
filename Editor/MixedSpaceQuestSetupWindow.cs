#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEngine;
using UnityEngine.Networking;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace MixedSpace.Editor
{
    /// <summary>The package-level UI deliberately has no compile-time Meta dependency.</summary>
    public sealed class MixedSpaceQuestSetupWindow : EditorWindow, IHasCustomMenu
    {
        private const string SceneGuid = "59a4aaaaf89d06742a681b1cacdf9843";
        private const string PlatformGuid = "c558c5344bba0b747ae499464ed4c4f8";
        private const string LegacySceneGuid = "33268a0ce5234e288161740b7ee44f0d";
        private const string LegacyPlatformGuid = "c47e04a19d8b41778450d37979282ebb";
        private const string Pending = "MixedSpace.Quest.DependenciesPending";
        private const string InstallSelection = "MixedSpace.Quest.InstallSelection";
        private const string InstallSubmitted = "MixedSpace.Quest.InstallSubmitted";
        private const string InstallRegisteredBy = "MixedSpace.Quest.InstallRegisteredBy";
        private const string FixAllPending = "MixedSpace.Quest.FixAllPending";
        private const string TmpImportStarted = "MixedSpace.Quest.TmpImportStarted";
        private const string ConfigurePending = "MixedSpace.Quest.ConfigurePending";
        private const string SwitchDeadline = "MixedSpace.Quest.SwitchDeadline";
        private const string Notice = "MixedSpace.Quest.OpenSetupOnFirstImport";
        private const string MessageKey = "MixedSpace.Quest.Message";
        private const string ErrorKey = "MixedSpace.Quest.Error";
        private const string Core = "com.meta.xr.sdk.core";
        private const string Mruk = "com.meta.xr.mrutilitykit";
        private const string OpenXr = "com.unity.xr.openxr";
        private const string Input = "com.unity.inputsystem";
        private const string FontGuid = "8f586378b4e144a9851e7b34d9b748ee";
        private const string MetaVersion = "81.0.0";
        private const string MetaRegistry = "https://npm.developer.oculus.com";
        private const string ArchiveDirectory = "Packages/MixedSpaceQuestDependencies";
        private static readonly string[] MetaPackages = { Core, Mruk };
        private static readonly string[] UnityPackages = { OpenXr, Input };
        private static readonly string[] Dependencies = MetaPackages.Concat(UnityPackages).ToArray();
        private static AddAndRemoveRequest s_Install;
        private static UnityWebRequest s_Download;
        private static string s_DownloadPath;
        private Vector2 m_Scroll;
        private double m_NextCheck;
        private Dictionary<string, string> m_Configuration;
        private string m_ConfigurationError;
        private const string PackageManagerMenu = "Window/Package Manager";
        private const string XrSettings = "Project/XR Plug-in Management";
        internal static bool SampleImported => ImportedScenePath != null;
        private static string ImportedScenePath
        {
            get
            {
                string path = AssetDatabase.GUIDToAssetPath(SceneGuid);
                if (path.StartsWith("Assets/", StringComparison.Ordinal)) return path;
                path = AssetDatabase.GUIDToAssetPath(LegacySceneGuid);
                return path.StartsWith("Assets/", StringComparison.Ordinal) ? path : null;
            }
        }
        internal static bool IsSampleScene(string path)
        {
            string guid = AssetDatabase.AssetPathToGUID(path);
            return guid == SceneGuid || guid == LegacySceneGuid;
        }
        internal static bool IsPlatformScript(string path)
        {
            string guid = AssetDatabase.AssetPathToGUID(path);
            return guid == PlatformGuid || guid == LegacyPlatformGuid;
        }
        private static string ImportNoticeKey => "MixedSpace.Quest.SampleImported." + Application.dataPath;
        private static Type SetupType => AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly => assembly.GetType("QuestVPS.Editor.QuestVpsProjectSetup", false)).FirstOrDefault(type => type != null);

        [MenuItem("MixedSpaceSDK/Adapters/Quest 设置与依赖检查")]
        public static void Open()
        {
            var window = GetWindow<MixedSpaceQuestSetupWindow>("Quest 设置");
            window.minSize = new Vector2(420, 240);
        }

        [InitializeOnLoadMethod]
        private static void Initialize()
        {
            EditorApplication.update -= Resume;
            EditorApplication.update += Resume;
            AssemblyReloadEvents.beforeAssemblyReload += DisposeDownload;
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

        private static PackageInfo FindPackage(string name) => PackageInfo.GetAllRegisteredPackages().FirstOrDefault(package => package.name == name);
        private static string Version(string name) => FindPackage(name)?.version;
        private static string RequiredVersion(string name) => name == OpenXr ? "1.14.3" : name == Input ? "1.11.2" : MetaVersion;
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
            foreach (string name in Dependencies)
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
            foreach (string name in Dependencies)
                if (!Ready(name)) issues.Add(name + " 需要至少 " + MinimumVersion(name) + "，当前：" + (Version(name) ?? "未安装"));
            if (!FontReady) issues.Add("缺少 TMP 示例字体资源。");
            if (SetupType == null && issues.Count == 0) issues.Add("请等待 Quest 脚本编译完成，或检查 Console。");
            return issues.ToArray();
        }

        internal static object InvokeSetup(string method) => InvokeSetup(method, Type.EmptyTypes, null);

        private static object InvokeSetup(string method, Type[] parameterTypes, object[] arguments)
        {
            MethodInfo action = SetupType?.GetMethod(method, BindingFlags.Public | BindingFlags.Static, null, parameterTypes, null);
            if (action == null) throw new InvalidOperationException("Quest 示例未就绪，请先安装对应版本依赖并等待编译。");
            try { return action.Invoke(null, arguments); }
            catch (TargetInvocationException error) { throw error.InnerException ?? error; }
        }

        private static void BeginInstall(string[] packages)
        {
            if (SessionState.GetBool(Pending, false)) return;
            if (packages.All(Ready)) return;
            SessionState.SetString(InstallSelection, string.Join(",", packages));
            SessionState.SetBool(InstallSubmitted, false);
            SessionState.SetFloat(InstallRegisteredBy, 0);
            SessionState.SetBool(Pending, true);
            SessionState.SetString(ErrorKey, "");
            SessionState.SetString(MessageKey, "正在准备 Quest 依赖…");
        }

        private static void InstallDependencies()
        {
            if (s_Download != null)
            {
                if (!s_Download.isDone) return;
                if (s_Download.result != UnityWebRequest.Result.Success)
                    throw new InvalidOperationException(Path.GetFileNameWithoutExtension(s_DownloadPath) +
                        " 下载失败：" + s_Download.error + "。请检查网络后重试。");
                string temporary = s_DownloadPath;
                s_Download.Dispose();
                s_Download = null;
                string archive = Path.ChangeExtension(temporary, null);
                if (!ArchiveVerified(temporary))
                    throw new InvalidOperationException(Path.GetFileName(archive) + " 校验失败。请重试下载。");
                File.Move(temporary, archive);
                s_DownloadPath = null;
            }

            if (s_Install != null)
            {
                if (!s_Install.IsCompleted) return;
                if (s_Install.Status != StatusCode.Success)
                    throw new InvalidOperationException("Quest 依赖安装失败：" + s_Install.Error?.message);
                s_Install = null;
                return;
            }

            string[] packages = SessionState.GetString(InstallSelection, string.Empty).Split(',');
            string[] missing = Dependencies.Where(name => packages.Contains(name) && !Ready(name)).ToArray();
            if (missing.Length == 0)
            {
                SessionState.SetBool(Pending, false);
                SessionState.SetBool(InstallSubmitted, false);
                SessionState.SetString(MessageKey, "所选依赖已安装。");
                return;
            }
            if (SessionState.GetBool(InstallSubmitted, false))
            {
                float deadline = SessionState.GetFloat(InstallRegisteredBy, 0);
                if (deadline == 0)
                {
                    SessionState.SetFloat(InstallRegisteredBy, (float)EditorApplication.timeSinceStartup + 15);
                    return;
                }
                if (EditorApplication.timeSinceStartup < deadline) return;
                throw new InvalidOperationException("Package Manager 未解析到所需版本：" + string.Join("、", missing) + "。请检查 Package Manager 后重试。");
            }

            var specs = new List<string>(missing.Length);
            foreach (string name in missing)
            {
                PackageInfo installed = FindPackage(name);
                if (installed?.source == PackageSource.Embedded)
                    throw new InvalidOperationException(name + " 是工程内嵌包。请先将该包更新至 " + MinimumVersion(name) + " 或更高。");
                if (name != Core && name != Mruk)
                {
                    specs.Add(name + "@" + RequiredVersion(name));
                    continue;
                }

                string file = name + "-" + MetaVersion + ".tgz";
                string path = Path.GetFullPath(Path.Combine(ArchiveDirectory, file));
                if (!File.Exists(path))
                {
                    Directory.CreateDirectory(ArchiveDirectory);
                    s_DownloadPath = path + ".download";
                    if (File.Exists(s_DownloadPath)) File.Delete(s_DownloadPath);
                    s_Download = new UnityWebRequest(MetaRegistry + "/" + name + "/-/" + file, UnityWebRequest.kHttpVerbGET);
                    s_Download.downloadHandler = new DownloadHandlerFile(s_DownloadPath) { removeFileOnAbort = true };
                    s_Download.timeout = 600;
                    s_Download.SendWebRequest();
                    SessionState.SetString(MessageKey, "正在从 Meta 官方源下载 " + file + "…");
                    return;
                }
                if (!ArchiveVerified(path))
                    throw new InvalidOperationException(file + " 校验失败。请移走 " + ArchiveDirectory + " 中的该文件后重试。");
                specs.Add("file:" + path.Replace('\\', '/'));
            }
            s_Install = Client.AddAndRemove(specs.ToArray());
            SessionState.SetBool(InstallSubmitted, true);
            SessionState.SetString(MessageKey, "Unity Package Manager 正在安装 Quest 依赖…");
        }

        private static bool ArchiveVerified(string path)
        {
            string expected = Path.GetFileName(path).StartsWith(Core + "-", StringComparison.Ordinal)
                ? "uhr26yUWFm9neDmIzwpLc5WdoA8QaDQBZZgR7k/gW0gqRfsWZsY1xDQrYg2BLJ2g+9IR1jI8iDU8g8YkR59r8g=="
                : "yay1tJLjnAYl03piUbUjuX5eHx43OaGaGnKUrZS+w+7sHqBAbW+KSv+vQvJXHFwsvxCT1NtLoDeI2bEN5BDnjA==";
            using var sha = SHA512.Create();
            using var stream = File.OpenRead(path);
            return Convert.ToBase64String(sha.ComputeHash(stream)) == expected;
        }

        private static void DisposeDownload()
        {
            s_Download?.Dispose();
            s_Download = null;
            if (s_DownloadPath != null && File.Exists(s_DownloadPath)) File.Delete(s_DownloadPath);
            s_DownloadPath = null;
        }

        private static void BeginFixAll()
        {
            SessionState.SetBool(FixAllPending, true);
            SessionState.SetBool(TmpImportStarted, false);
            BeginInstall(Dependencies);
        }

        private static void ContinueFixAll()
        {
            if (!SampleImported)
            {
                SessionState.SetBool(FixAllPending, false);
                SessionState.SetString(MessageKey, "依赖已就绪。请在 Package Manager 中导入 QuestAdapter 示例。");
                return;
            }
            if (!FontReady)
            {
                if (SessionState.GetBool(TmpImportStarted, false))
                    throw new InvalidOperationException("TMP Essentials 导入后仍缺少字体。请检查 Console 或在 TextMeshPro 菜单中重新导入。");
                SessionState.SetBool(TmpImportStarted, true);
                SessionState.SetString(MessageKey, "正在导入 TMP Essentials…");
                ImportTmp();
                return;
            }
            SessionState.SetBool(FixAllPending, false);
            BeginConfigure();
        }

        private static void ImportTmp()
        {
            var tmp = FindPackage("com.unity.textmeshpro");
            string archive = tmp == null ? null : Path.Combine(tmp.resolvedPath, "Package Resources", "TMP Essential Resources.unitypackage");
            if (!File.Exists(archive))
                throw new InvalidOperationException("未找到 TMP Essentials 安装包。请检查 TextMeshPro 包，或从 Window → TextMeshPro 导入。");
            AssetDatabase.ImportPackage(archive, false);
        }

        private static void BeginConfigure()
        {
            string[] issues = CheckDependencies();
            if (issues.Length != 0) throw new InvalidOperationException(string.Join("; ", issues));
            if (MixedSpaceSampleSetup.Busy)
                throw new InvalidOperationException("请等待导入、编译或运行结束。");
            if (SetupType.GetMethod("GetConfigurationIssues") != null)
            {
                var configuration = (Dictionary<string, string>)InvokeSetup("GetConfigurationIssues");
                if (configuration.TryGetValue("LoaderConflict", out string conflict))
                    throw new InvalidOperationException(conflict + "处理后再运行 Fix All。");
                if (configuration.Count == 0)
                {
                    SessionState.SetString(MessageKey, "Quest 环境已就绪。请打开示例场景。");
                    return;
                }
            }
            SessionState.SetFloat(SwitchDeadline, 0);
            SessionState.SetBool(ConfigurePending, true);
            SessionState.SetString(ErrorKey, "");
            SessionState.SetString(MessageKey, "正在配置 Quest 工程。");
        }

        private static void Resume()
        {
            if (MixedSpaceSampleSetup.Busy) return;
            try
            {
                if (SessionState.GetBool(Pending, false))
                {
                    InstallDependencies();
                    return;
                }
                if (SessionState.GetBool(FixAllPending, false))
                {
                    ContinueFixAll();
                    return;
                }
                if (SessionState.GetBool(ConfigurePending, false))
                {
                    // Persist before switching targets, which can cause a domain reload.
                    if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.Android)
                    {
                        float deadline = SessionState.GetFloat(SwitchDeadline, 0);
                        if (deadline == 0)
                        {
                            SessionState.SetFloat(SwitchDeadline, (float)EditorApplication.timeSinceStartup + 300);
                            SwitchAndroid();
                        }
                        else if (EditorApplication.timeSinceStartup >= deadline)
                            throw new InvalidOperationException("Android 切换未完成，请检查 Unity 的构建平台和 Console。");
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
                s_Install = null;
                SessionState.SetBool(Pending, false);
                SessionState.SetBool(InstallSubmitted, false);
                SessionState.SetBool(FixAllPending, false);
                SessionState.SetBool(ConfigurePending, false);
                SessionState.SetString(MessageKey, "");
                SessionState.SetString(ErrorKey, error.Message);
                try { DisposeDownload(); }
                catch (IOException cleanupError) { Debug.LogWarning(cleanupError.Message); }
                catch (UnauthorizedAccessException cleanupError) { Debug.LogWarning(cleanupError.Message); }
                Debug.LogError("Quest setup: " + error.Message);
            }
        }

        private static void SwitchAndroid()
        {
            if (!BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.Android, BuildTarget.Android))
                throw new InvalidOperationException("请先在 Unity Hub 中安装 Android Build Support。");
            if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.Android &&
                !EditorUserBuildSettings.SwitchActiveBuildTargetAsync(BuildTargetGroup.Android, BuildTarget.Android))
                throw new InvalidOperationException("Android 构建目标切换失败。");
        }

        private void OnInspectorUpdate()
        {
            if (!MixedSpaceSampleSetup.Busy && EditorApplication.timeSinceStartup >= m_NextCheck)
            {
                m_NextCheck = EditorApplication.timeSinceStartup + 0.5;
                m_Configuration = null;
                m_ConfigurationError = null;
                if (SampleImported && Dependencies.All(Ready) && SetupType != null)
                {
                    try
                    {
                        m_Configuration = SetupType.GetMethod("GetConfigurationIssues") != null
                            ? (Dictionary<string, string>)InvokeSetup("GetConfigurationIssues")
                            : ReadLegacyConfiguration();
                    }
                    catch (Exception error) { m_ConfigurationError = error.Message; }
                }
            }
            Repaint();
        }

        private static Dictionary<string, string> ReadLegacyConfiguration()
        {
            var issues = (string[])InvokeSetup("CheckConfiguration");
            var result = new Dictionary<string, string>();
            if (issues.Length > 0) result.Add("Legacy", string.Join("\n", issues));
            return result;
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("Quest 环境设置", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("只显示待处理项，Fix 后自动重新检查。", EditorStyles.wordWrappedLabel);
            m_Scroll = EditorGUILayout.BeginScrollView(m_Scroll);
            bool working = SessionState.GetBool(Pending, false) || SessionState.GetBool(FixAllPending, false) ||
                SessionState.GetBool(ConfigurePending, false);
            bool busy = MixedSpaceSampleSetup.Busy || working;
            using (new EditorGUI.DisabledScope(busy))
            {
                int fixable = 0;
                if (DrawPackageIssue("Meta XR Core SDK / MR Utility Kit", MetaPackages, "从 Meta 官方源安装缺少的包。"))
                    fixable++;
                if (DrawPackageIssue("OpenXR / Input System", UnityPackages, "通过 Unity Package Manager 安装缺少的包。"))
                    fixable++;
                if (!SampleImported)
                    DrawIssue("QuestAdapter 示例未导入", "在 MixedSpace SDK 的 Samples 中导入 QuestAdapter。",
                        () => EditorApplication.ExecuteMenuItem(PackageManagerMenu), "打开");
                else
                {
                    bool fontReady = FontReady;
                    if (!fontReady)
                    {
                        DrawIssue("缺少 TMP 示例字体", "导入 TextMesh Pro 的 TMP Essentials 资源。", ImportTmp);
                        fixable++;
                    }
                    if (Dependencies.All(Ready))
                    {
                        if (SetupType == null)
                            DrawIssue("Quest 配置脚本未就绪", "等待编译完成，或在 Console 查看编译错误。", OpenConsole, "打开");
                        else if (!string.IsNullOrEmpty(m_ConfigurationError))
                            DrawIssue("Quest 配置检测失败", m_ConfigurationError, OpenConsole, "打开");
                        else if (m_Configuration != null)
                        {
                            foreach (var issue in m_Configuration)
                                if (DrawConfigurationIssue(issue.Key, issue.Value)) fixable++;
                            if (fontReady && m_Configuration.Count == 0 && !busy)
                                EditorGUILayout.HelpBox(MixedSpaceSampleSetup.ReadyMessage, MessageType.Info);
                        }
                    }
                }

                if (MixedSpaceSampleSetup.FixAllButton(fixable)) Run(BeginFixAll);
            }
            string[] warnings = CheckVersionWarnings();
            if (warnings.Length > 0) EditorGUILayout.HelpBox(string.Join("\n", warnings), MessageType.Warning);
            string message = SessionState.GetString(MessageKey, string.Empty);
            if (busy && !working) EditorGUILayout.HelpBox("正在安装、编译或导入资源，请稍候。完成后自动重新检测。", MessageType.Info);
            else if (!string.IsNullOrEmpty(message)) EditorGUILayout.HelpBox(message, MessageType.Info);
            string error = SessionState.GetString(ErrorKey, "");
            if (!string.IsNullOrEmpty(error))
            {
                EditorGUILayout.HelpBox(error, MessageType.Error);
                if (GUILayout.Button("关闭错误提示")) SessionState.SetString(ErrorKey, "");
            }
            EditorGUILayout.EndScrollView();
        }

        private bool DrawPackageIssue(string title, string[] packages, string detail)
        {
            string[] missing = packages.Where(name => !Ready(name)).ToArray();
            if (missing.Length == 0) return false;
            string versions = string.Join("\n", missing.Select(name => name + "　当前 " +
                (Version(name) ?? "未安装") + "，最低 " + MinimumVersion(name)));
            DrawIssue(title, versions + "\n" + detail, () => BeginInstall(packages));
            return true;
        }

        private bool DrawConfigurationIssue(string issue, string detail)
        {
            string title = issue switch
            {
                "Target" => "构建目标不是 Android",
                "Build" => "Android 后端和目标架构",
                "Api" => "Android API 级别",
                "Graphics" => "Android 图形 API",
                "Player" => "Android Player 设置",
                "Input" => "Input System 未启用",
                "LoaderConflict" => "Android XR Loader 冲突",
                "Loader" => "Android OpenXR Loader 未就绪",
                "OpenXR" => "Quest OpenXR 功能",
                "Meta" => "Meta 设备和透视设置",
                _ => "Quest 工程配置"
            };
            if (issue == "LoaderConflict")
            {
                DrawIssue(title, detail, () => SettingsService.OpenProjectSettings(XrSettings), "打开");
                return false;
            }
            Action fix = issue == "Target" ? SwitchAndroid : issue == "Legacy" ? BeginConfigure :
                () => InvokeSetup("FixConfiguration", new[] { typeof(string) }, new object[] { issue });
            DrawIssue(title, detail, fix, severity: issue == "Target" ? MessageType.Warning : MessageType.Error);
            return true;
        }

        private void DrawIssue(string title, string detail, Action fix, string button = "Fix", MessageType severity = MessageType.Error)
        {
            if (!MixedSpaceSampleSetup.IssueRow(title, detail, severity, button)) return;
            Run(fix);
            m_NextCheck = 0;
        }

        private static void OpenConsole() => EditorApplication.ExecuteMenuItem("Window/General/Console");

        public void AddItemsToMenu(GenericMenu menu)
        {
            menu.AddItem(new GUIContent("打开 XR 设置"), false, () => SettingsService.OpenProjectSettings(XrSettings));
            if (!SampleImported) menu.AddDisabledItem(new GUIContent("打开 Quest 示例场景"));
            else menu.AddItem(new GUIContent("打开 Quest 示例场景"), false, () => Run(() =>
            {
                if (UnityEditor.SceneManagement.EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                    UnityEditor.SceneManagement.EditorSceneManager.OpenScene(ImportedScenePath);
            }));
        }

        private static void Run(Action action)
        {
            try
            {
                SessionState.SetString(ErrorKey, "");
                action();
            }
            catch (Exception error) { SessionState.SetString(ErrorKey, error.Message); }
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
            if (imported.Any(path => path.StartsWith("Assets/", StringComparison.Ordinal) && MixedSpaceQuestSetupWindow.IsSampleScene(path)))
                MixedSpaceQuestSetupWindow.Imported();
        }
    }
}
#endif
