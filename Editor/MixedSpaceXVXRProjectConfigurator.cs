#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.XR.Management;
#if UNITY_ANDROID
using UnityEditor.Android;
#endif
using UnityEngine;
using UnityEngine.Rendering;

namespace MixedSpace.Editor
{
    /// <summary>
    /// 在依赖编译和程序集重载后完成显式的一键配置。
    /// Finishes the explicit one-click setup after dependency compilation and domain reload.
    /// </summary>
    internal static class MixedSpaceXVXRProjectConfigurator
    {
        private const string PendingKey = "MixedSpace.XVXR.ProjectSetupPending";
        private const string StageKey = "MixedSpace.XVXR.ProjectSetupStage";
        private const string MessageKey = "MixedSpace.XVXR.ProjectSetupMessage";
        private const string ErrorKey = "MixedSpace.XVXR.ProjectSetupError";
        private const string TimeKey = "MixedSpace.XVXR.ProjectSetupTime";
        private const string TemplateAsset = "Assets/Plugins/Android/mainTemplate.gradle";
        private const string TemplateSetting = "useCustomMainGradleTemplate";
        private static readonly string[] Dependencies =
        {
            "com.google.android.material:material:1.3.0",
            "androidx.appcompat:appcompat:1.3.1",
            "org.jetbrains.kotlin:kotlin-stdlib-jdk8:1.6.0"
        };

        internal static bool IsRunning => SessionState.GetBool(PendingKey, false);
        internal static string StatusMessage => SessionState.GetString(MessageKey, string.Empty);
        internal static bool HasError => SessionState.GetBool(ErrorKey, false);
        private static string ProjectRoot => Directory.GetParent(Application.dataPath).FullName;
        private static string TemplateFile => Path.Combine(ProjectRoot, TemplateAsset);
        private static string UnitySdkRoot => Path.Combine(EditorApplication.applicationContentsPath, "PlaybackEngines/AndroidPlayer/SDK");

        internal static bool UsesUnitySdk
        {
            get
            {
#if UNITY_ANDROID
                string selected = AndroidExternalToolsSettings.sdkRootPath;
                return !string.IsNullOrEmpty(selected) && string.Equals(
                    Path.GetFullPath(selected).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    Path.GetFullPath(UnitySdkRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    Application.platform == RuntimePlatform.WindowsEditor ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
#else
                return false;
#endif
            }
        }

        private static void ValidateUnitySdk()
        {
            string adb = Application.platform == RuntimePlatform.WindowsEditor ? "adb.exe" : "adb";
            if (!File.Exists(Path.Combine(UnitySdkRoot, "platform-tools", adb)) ||
                !Directory.Exists(Path.Combine(UnitySdkRoot, "build-tools")) ||
                !Directory.Exists(Path.Combine(UnitySdkRoot, "platforms")))
                throw new InvalidOperationException("当前 Unity 未安装 Android SDK；请通过 Unity Hub 补装 Android SDK & NDK Tools。");
        }

        internal static void UseUnitySdk()
        {
            ValidateUnitySdk();
#if UNITY_ANDROID
            AndroidExternalToolsSettings.sdkRootPath = string.Empty;
            if (!UsesUnitySdk) throw new InvalidOperationException("未能切换到 Unity 自带 Android SDK；请检查 Android External Tools。");
#else
            throw new InvalidOperationException("请先切换到 Android 构建目标。");
#endif
        }

        [InitializeOnLoadMethod]
        private static void Initialize() { if (IsRunning) Listen(); }

        private static void Listen()
        {
            EditorApplication.update -= Resume;
            EditorApplication.update += Resume;
        }

        internal static void BeginSetup()
        {
            if (IsRunning || EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlaying)
                throw new InvalidOperationException("请等待导入、编译或运行结束后再配置 XVXR。");
            if (!MixedSpaceXVXRSetupWindow.SampleImported)
                throw new InvalidOperationException("请先通过 Package Manager 导入 XVXRAdapter 示例。");
            var dependencies = MixedSpaceXVXRSetupWindow.CheckDependencies();
            if (dependencies.Count > 0 || MixedSpaceXVXRDependencyInstaller.MissingGroups().Count > 0)
                throw new InvalidOperationException("请先完成插件导入。");
            Preflight();
            PrepareTemplate();
            GetSetting(TemplateSetting);
            SessionState.SetBool(PendingKey, true);
            SessionState.SetInt(StageKey, 0);
            SessionState.SetBool(ErrorKey, false);
            SessionState.SetString(MessageKey, "正在配置工程。");
            Listen();
        }

        private static void Resume()
        {
            if (!IsRunning) { EditorApplication.update -= Resume; return; }
            if (EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlaying) return;
            try
            {
                int step = SessionState.GetInt(StageKey, 0);
                if (step == 0)
                {
                    var issues = MixedSpaceXVXRSetupWindow.CheckDependencies();
                    if (issues.Count > 0) throw new InvalidOperationException("依赖未就绪或导入已取消：" + string.Join("; ", issues));
                    ApplyConfiguration();
                    SessionState.SetInt(StageKey, 1);
                    SessionState.SetFloat(TimeKey, (float)EditorApplication.timeSinceStartup);
                    SessionState.SetString(MessageKey, "正在切换目标并编译。");
                    if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.Android &&
                        !EditorUserBuildSettings.SwitchActiveBuildTargetAsync(BuildTargetGroup.Android, BuildTarget.Android))
                        throw new InvalidOperationException("Android 配置已写入，但目标切换失败；请检查 Android Build Support 后重新点击配置。");
                    return;
                }
                if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.Android)
                {
                    if (EditorApplication.timeSinceStartup - SessionState.GetFloat(TimeKey, 0) > 120)
                        throw new InvalidOperationException("Android 目标尚未切换完成；请检查 Android Build Support。");
                    return;
                }
                if (step == 1)
                {
                    UseUnitySdk();
                    SessionState.SetInt(StageKey, 2);
                    return;
                }
                if (MixedSpaceXVXRSetupWindow.FindType("MixedSpace.XR.XVXRPlatform") == null)
                {
                    if (EditorApplication.timeSinceStartup - SessionState.GetFloat(TimeKey, 0) > 120)
                        throw new InvalidOperationException("适配脚本尚未编译成功；请处理 Console 中的编译错误后重新配置。");
                    return;
                }
                var remaining = MixedSpaceXVXRSetupWindow.CheckDependencies();
                remaining.AddRange(CheckConfiguration());
                if (remaining.Count > 0) throw new InvalidOperationException(string.Join("; ", remaining));
                AssetDatabase.SaveAssets();
                Complete("工程配置已完成。", false);
            }
            catch (Exception exception) { Complete(exception.Message, true); }
        }

        private static void Complete(string message, bool error)
        {
            SessionState.SetBool(PendingKey, false);
            SessionState.SetBool(ErrorKey, error);
            SessionState.SetString(MessageKey, message);
            EditorApplication.update -= Resume;
            if (error) Debug.LogError("XVXR setup: " + message);
            else Debug.Log("XVXR setup: " + message);
        }

        private static void Preflight()
        {
            ValidateUnitySdk();
            var xr = XRGeneralSettingsPerBuildTarget.XRGeneralSettingsForBuildTarget(BuildTargetGroup.Android);
            if (xr != null && xr.Manager != null && xr.Manager.activeLoaders.Count > 0)
                throw new InvalidOperationException("Android 已启用其他 XR Loader；现有 Loader 未修改，请先确认与配套 XVXR 双眼渲染的兼容性。");
            if ((int)PlayerSettings.Android.targetSdkVersion >= 31)
                throw new InvalidOperationException("当前目标 Android API 为 31 或更高。配套 SDK 的 USB 权限接口仍含旧 PendingIntent 写法；现有目标版本未覆盖，请选择已验证的 API 27 或升级兼容 SDK。");
            string properties = Path.Combine(ProjectRoot, "Assets/Plugins/Android/gradleTemplate.properties");
            if (GetSetting("useCustomGradlePropertiesTemplate") && File.Exists(properties) &&
                Regex.IsMatch(File.ReadAllText(properties), @"(?m)^\s*android\.(?:useAndroidX|enableJetifier)\s*=\s*false\s*$"))
                throw new InvalidOperationException("自定义 Gradle 属性关闭了 AndroidX 或 Jetifier；现有文件未覆盖，请先处理此冲突。");
        }

        internal static List<string> CheckConfiguration()
        {
            var issues = new List<string>();
            try
            {
                Preflight();
                if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.Android) issues.Add("构建目标需要 Android。");
#if UNITY_ANDROID
                if (!UsesUnitySdk) issues.Add("Android SDK 需要使用 Unity 自带版本。");
#endif
                if (PlayerSettings.GetScriptingBackend(NamedBuildTarget.Android) != ScriptingImplementation.IL2CPP) issues.Add("Android 需要 IL2CPP。");
                if (PlayerSettings.Android.targetArchitectures != AndroidArchitecture.ARM64) issues.Add("Android 需要仅 ARM64。");
                if ((int)PlayerSettings.Android.minSdkVersion < 26) issues.Add("最低 Android API 需要至少 26。");
                if (PlayerSettings.Android.targetSdkVersion == AndroidSdkVersions.AndroidApiLevelAuto) issues.Add("当前 SDK 请使用已验证的目标 API 27。");
                GraphicsDeviceType[] apis = PlayerSettings.GetGraphicsAPIs(BuildTarget.Android);
                if (PlayerSettings.GetUseDefaultGraphicsAPIs(BuildTarget.Android) || apis.Length != 1 || apis[0] != GraphicsDeviceType.OpenGLES3)
                    issues.Add("Android 显示需要固定为 OpenGLES3。");
                if (!GetSetting(TemplateSetting) || !File.Exists(TemplateFile)) issues.Add("需要启用 Android 主 Gradle 模板。");
                else if (PrepareTemplate() != File.ReadAllText(TemplateFile)) issues.Add("主 Gradle 模板需要补齐配套 Maven 依赖。");
            }
            catch (Exception exception) { issues.Add(exception.Message); }
            return issues;
        }

        private static SerializedObject SettingsObject()
        {
            PlayerSettings[] settings = Resources.FindObjectsOfTypeAll<PlayerSettings>();
            if (settings.Length != 1) throw new InvalidOperationException("无法取得当前 Unity PlayerSettings；没有写入工程配置。");
            return new SerializedObject(settings[0]);
        }

        private static bool GetSetting(string name)
        {
            using (SerializedObject settings = SettingsObject())
            {
                SerializedProperty property = settings.FindProperty(name);
                if (property == null || property.propertyType != SerializedPropertyType.Boolean)
                    throw new InvalidOperationException("当前 Unity 缺少模板设置字段：" + name);
                return property.boolValue;
            }
        }

        private static void SetTemplateEnabled(bool value)
        {
            using (SerializedObject settings = SettingsObject())
            {
                SerializedProperty property = settings.FindProperty(TemplateSetting);
                if (property == null || property.propertyType != SerializedPropertyType.Boolean)
                    throw new InvalidOperationException("当前 Unity 缺少主 Gradle 模板设置字段。");
                if (property.boolValue == value) return;
                property.boolValue = value;
                settings.ApplyModifiedPropertiesWithoutUndo();
            }
        }

        private static string PrepareTemplate()
        {
            string source = File.Exists(TemplateFile) ? TemplateFile : Path.Combine(EditorApplication.applicationContentsPath,
                "PlaybackEngines/AndroidPlayer/Tools/GradleTemplates/mainTemplate.gradle");
            if (!File.Exists(source)) throw new InvalidOperationException("当前 Unity 未安装 Android 默认 Gradle 模板；请安装 Android Build Support。");
            return ConfigureTemplate(File.ReadAllText(source));
        }

        internal static string ConfigureTemplate(string text)
        {
            if (string.IsNullOrEmpty(text)) throw new InvalidOperationException("Android 主 Gradle 模板为空。");
            string clean = StripComments(text);
            var blocks = Regex.Matches(clean, @"\bdependencies\s*\{");
            int marker = clean.IndexOf("**DEPS**", StringComparison.Ordinal);
            if (blocks.Count != 1 || marker < 0 || clean.IndexOf("**DEPS**", marker + 1, StringComparison.Ordinal) >= 0)
                throw new InvalidOperationException("自定义主 Gradle 模板需要唯一 dependencies 块和 **DEPS** 标记；现有内容未覆盖。");
            int start = blocks[0].Index + blocks[0].Value.LastIndexOf('{');
            int end = FindClosingBrace(clean, start);
            if (marker <= start || marker >= end) throw new InvalidOperationException("**DEPS** 不在 dependencies 块内；现有模板未修改。");
            string block = clean.Substring(start + 1, end - start - 1);
            var additions = new List<string>();
            foreach (string coordinate in Dependencies)
            {
                string[] parts = coordinate.Split(':');
                string prefix = parts[0] + ":" + parts[1] + ":";
                var matches = Regex.Matches(block, @"\b(?:implementation|api)\s*(?:\(\s*)?['" + "\"" + @"]" + Regex.Escape(prefix) + @"([^'" + "\"" + @"]+)['" + "\"" + @"]");
                if (matches.Count > 1) throw new InvalidOperationException("Gradle 中重复声明了 " + parts[0] + ":" + parts[1] + "；现有内容未修改。");
                if (matches.Count == 1)
                {
                    if (matches[0].Groups[1].Value != parts[2])
                        throw new InvalidOperationException("已有 Gradle 版本与已验证配置不同：" + prefix + matches[0].Groups[1].Value + "；需要 " + coordinate + "。现有版本未覆盖。");
                    continue;
                }
                if (block.Contains(prefix) || (block.Contains(parts[0]) && block.Contains(parts[1])))
                    throw new InvalidOperationException("Gradle 使用了非标准或变量形式的 " + parts[0] + ":" + parts[1] + " 声明；请手动确认，现有内容未修改。");
                additions.Add("    implementation '" + coordinate + "'");
            }
            if (additions.Count == 0) return text;
            string newline = text.Contains("\r\n") ? "\r\n" : "\n";
            int lineStart = text.LastIndexOf('\n', marker) + 1;
            bool atLineStart = string.IsNullOrWhiteSpace(text.Substring(lineStart, marker - lineStart));
            int insertion = atLineStart ? lineStart : marker;
            return text.Insert(insertion, (atLineStart ? string.Empty : newline) + string.Join(newline, additions) + newline);
        }

        private static string StripComments(string text)
        {
            char[] chars = text.ToCharArray();
            char quote = '\0';
            for (int i = 0; i < chars.Length; i++)
            {
                if (quote != '\0')
                {
                    if (chars[i] == '\\') { i++; continue; }
                    if (chars[i] == quote) quote = '\0';
                    continue;
                }
                if (chars[i] == '\'' || chars[i] == '"') { quote = chars[i]; continue; }
                if (chars[i] != '/' || i + 1 >= chars.Length) continue;
                if (chars[i + 1] == '/')
                    for (; i < chars.Length && chars[i] != '\n'; i++) chars[i] = ' ';
                else if (chars[i + 1] == '*')
                {
                    chars[i++] = ' '; chars[i] = ' ';
                    for (i++; i < chars.Length; i++)
                    {
                        if (chars[i] == '*' && i + 1 < chars.Length && chars[i + 1] == '/')
                        { chars[i] = ' '; chars[++i] = ' '; break; }
                        if (chars[i] != '\r' && chars[i] != '\n') chars[i] = ' ';
                    }
                }
            }
            return new string(chars);
        }

        private static int FindClosingBrace(string text, int start)
        {
            int depth = 0;
            char quote = '\0';
            for (int i = start; i < text.Length; i++)
            {
                if (quote != '\0')
                {
                    if (text[i] == '\\') { i++; continue; }
                    if (text[i] == quote) quote = '\0';
                    continue;
                }
                if (text[i] == '\'' || text[i] == '"') { quote = text[i]; continue; }
                if (text[i] == '{') depth++;
                else if (text[i] == '}' && --depth == 0) return i;
            }
            throw new InvalidOperationException("Gradle dependencies 块未闭合；现有内容未修改。");
        }

        private static void ApplyConfiguration()
        {
            Preflight();
            string configured = PrepareTemplate();
            byte[] original = File.Exists(TemplateFile) ? File.ReadAllBytes(TemplateFile) : null;
            bool previousTemplate = GetSetting(TemplateSetting);
            bool automatic = PlayerSettings.GetUseDefaultGraphicsAPIs(BuildTarget.Android);
            GraphicsDeviceType[] apis = PlayerSettings.GetGraphicsAPIs(BuildTarget.Android);
            ScriptingImplementation backend = PlayerSettings.GetScriptingBackend(NamedBuildTarget.Android);
            AndroidArchitecture architectures = PlayerSettings.Android.targetArchitectures;
            AndroidSdkVersions minimum = PlayerSettings.Android.minSdkVersion;
            AndroidSdkVersions target = PlayerSettings.Android.targetSdkVersion;
            bool write = original == null || File.ReadAllText(TemplateFile) != configured;
            try
            {
                if (write)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(TemplateFile));
                    bool bom = original != null && original.Length >= 3 && original[0] == 0xef && original[1] == 0xbb && original[2] == 0xbf;
                    File.WriteAllText(TemplateFile, configured, new UTF8Encoding(bom));
                }
                SetTemplateEnabled(true);
                if (backend != ScriptingImplementation.IL2CPP) PlayerSettings.SetScriptingBackend(NamedBuildTarget.Android, ScriptingImplementation.IL2CPP);
                if (architectures != AndroidArchitecture.ARM64) PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
                if ((int)minimum < 26) PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel26;
                if (target == AndroidSdkVersions.AndroidApiLevelAuto) PlayerSettings.Android.targetSdkVersion = AndroidSdkVersions.AndroidApiLevel27;
                if (automatic) PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android, false);
                if (apis.Length != 1 || apis[0] != GraphicsDeviceType.OpenGLES3)
                    PlayerSettings.SetGraphicsAPIs(BuildTarget.Android, new[] { GraphicsDeviceType.OpenGLES3 });
                AssetDatabase.SaveAssets();
            }
            catch
            {
                if (write)
                {
                    if (original == null) { if (File.Exists(TemplateFile)) File.Delete(TemplateFile); }
                    else File.WriteAllBytes(TemplateFile, original);
                }
                SetTemplateEnabled(previousTemplate);
                PlayerSettings.SetScriptingBackend(NamedBuildTarget.Android, backend);
                PlayerSettings.Android.targetArchitectures = architectures;
                PlayerSettings.Android.minSdkVersion = minimum;
                PlayerSettings.Android.targetSdkVersion = target;
                PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android, automatic);
                PlayerSettings.SetGraphicsAPIs(BuildTarget.Android, apis);
                throw;
            }
            if (write) AssetDatabase.ImportAsset(TemplateAsset);
        }
    }
}
#endif
