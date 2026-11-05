using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;

namespace MixedSpace.Editor
{
    /// <summary>
    /// 检查授权和地图定位所需的 xrloc 原生库，Android ARM64 库不依赖功能宏。
    /// Validates the xrloc libraries required by authorization and map localization; Android ARM64 inclusion is unconditional.
    /// </summary>
    internal sealed class MixedSpaceXrLocBuild : IPreprocessBuildWithReport
    {
        public int callbackOrder => -1010;
        public void OnPreprocessBuild(BuildReport report)
        {
            RepairBundledNativeImporters();
            var issues = CollectIssues(report.summary.platform);
            if (issues.Count > 0) throw new BuildFailedException(string.Join("\n", issues));
        }

        /// <summary>
        /// 修复随包提供的 iOS 静态库导入状态，避免旧项目缓存或跨平台导入设置阻止构建。
        /// Repairs the bundled iOS archive importer when stale or cross-platform settings would block a build.
        /// </summary>
        internal static void RepairBundledIosImporter()
        {
            const string path = "Packages/com.njyjxr.mixedspace/Runtime/Plugins/iOS/libxrloc.a";
            var plugin = AssetImporter.GetAtPath(path) as PluginImporter;
            if (plugin == null) return;
            bool needsRepair = plugin.GetCompatibleWithAnyPlatform() || plugin.GetCompatibleWithEditor() ||
                plugin.GetCompatibleWithPlatform(BuildTarget.Android) ||
                !plugin.GetCompatibleWithPlatform(BuildTarget.iOS) || plugin.DefineConstraints.Length != 0;
            if (!needsRepair) return;
            plugin.SetCompatibleWithAnyPlatform(false);
            plugin.SetCompatibleWithEditor(false);
            // 此文件仅是 iOS 静态库，不修改项目中其他第三方插件。
            // This archive is iOS-only; other third-party plugins are left untouched.
            foreach (var target in new[] { BuildTarget.Android, BuildTarget.StandaloneWindows,
                BuildTarget.StandaloneWindows64, BuildTarget.StandaloneOSX, BuildTarget.StandaloneLinux64,
                BuildTarget.WebGL, BuildTarget.WSAPlayer, BuildTarget.tvOS })
                plugin.SetCompatibleWithPlatform(target, false);
            if (Enum.TryParse("VisionOS", out BuildTarget visionTarget))
                plugin.SetCompatibleWithPlatform(visionTarget, false);
            plugin.SetCompatibleWithPlatform(BuildTarget.iOS, true);
            plugin.DefineConstraints = Array.Empty<string>();
            plugin.SaveAndReimport();
            UnityEngine.Debug.Log("MixedSpace: repaired libxrloc.a import settings (iOS only).");
        }

        /// <summary>
        /// 将随包提供的 Android 原生库恢复为 ARM64、无宏约束，不修改其他库或删除重复文件。
        /// Restores the bundled Android library to ARM64 without define constraints, leaving other libraries untouched.
        /// </summary>
        internal static void RepairBundledAndroidImporter()
        {
            const string path = "Packages/com.njyjxr.mixedspace/Runtime/Plugins/Android/libs/arm64-v8a/libxrloc.so";
            var plugin = AssetImporter.GetAtPath(path) as PluginImporter;
            if (plugin == null)
            {
                UnityEngine.Debug.LogWarning("MixedSpace: Android native library is missing or not imported: " + path);
                return;
            }
            bool needsRepair = plugin.GetCompatibleWithAnyPlatform() || plugin.GetCompatibleWithEditor() ||
                plugin.GetCompatibleWithPlatform(BuildTarget.iOS) ||
                !plugin.GetCompatibleWithPlatform(BuildTarget.Android) ||
                plugin.GetPlatformData(BuildTarget.Android, "CPU") != "ARM64" || plugin.DefineConstraints.Length != 0;
            if (!needsRepair) return;
            plugin.SetCompatibleWithAnyPlatform(false);
            plugin.SetCompatibleWithEditor(false);
            foreach (var target in new[] { BuildTarget.iOS, BuildTarget.StandaloneWindows,
                BuildTarget.StandaloneWindows64, BuildTarget.StandaloneOSX, BuildTarget.StandaloneLinux64,
                BuildTarget.WebGL, BuildTarget.WSAPlayer, BuildTarget.tvOS })
                plugin.SetCompatibleWithPlatform(target, false);
            if (Enum.TryParse("VisionOS", out BuildTarget visionTarget))
                plugin.SetCompatibleWithPlatform(visionTarget, false);
            plugin.SetCompatibleWithPlatform(BuildTarget.Android, true);
            plugin.SetPlatformData(BuildTarget.Android, "CPU", "ARM64");
            plugin.DefineConstraints = Array.Empty<string>();
            plugin.SaveAndReimport();
            UnityEngine.Debug.Log("MixedSpace: repaired libxrloc.so import settings (Android ARM64, no define constraints).");
        }

        /// <summary>
        /// Windows PE 原生库只允许 Windows 编辑器和 Windows x64，不进入移动平台或其他桌面系统。
        /// Restricts the Windows PE library to the Windows Editor and Windows x64, excluding mobile and other desktop systems.
        /// </summary>
        internal static void RepairBundledDesktopImporter()
        {
            const string path = "Packages/com.njyjxr.mixedspace/Runtime/Plugins/x86_64/xrloc.dll";
            var plugin = AssetImporter.GetAtPath(path) as PluginImporter;
            if (plugin == null) return;
            bool needsRepair = plugin.GetCompatibleWithAnyPlatform() || !plugin.GetCompatibleWithEditor() ||
                plugin.GetCompatibleWithPlatform(BuildTarget.Android) || plugin.GetCompatibleWithPlatform(BuildTarget.iOS) ||
                plugin.GetCompatibleWithPlatform(BuildTarget.StandaloneOSX) || plugin.GetCompatibleWithPlatform(BuildTarget.StandaloneLinux64) ||
                plugin.GetCompatibleWithPlatform(BuildTarget.StandaloneWindows) ||
                !plugin.GetCompatibleWithPlatform(BuildTarget.StandaloneWindows64) ||
                plugin.GetEditorData("OS") != "Windows" || plugin.GetEditorData("CPU") != "x86_64" ||
                plugin.GetPlatformData(BuildTarget.StandaloneWindows64, "CPU") != "x86_64";
            if (!needsRepair) return;
            plugin.SetCompatibleWithAnyPlatform(false);
            plugin.SetCompatibleWithEditor(true);
            plugin.SetEditorData("OS", "Windows");
            plugin.SetEditorData("CPU", "x86_64");
            foreach (var target in new[] { BuildTarget.Android, BuildTarget.iOS, BuildTarget.StandaloneWindows,
                BuildTarget.StandaloneOSX, BuildTarget.StandaloneLinux64, BuildTarget.WebGL, BuildTarget.WSAPlayer, BuildTarget.tvOS })
                plugin.SetCompatibleWithPlatform(target, false);
            if (Enum.TryParse("VisionOS", out BuildTarget visionTarget))
                plugin.SetCompatibleWithPlatform(visionTarget, false);
            plugin.SetCompatibleWithPlatform(BuildTarget.StandaloneWindows64, true);
            plugin.SetPlatformData(BuildTarget.StandaloneWindows64, "CPU", "x86_64");
            plugin.SaveAndReimport();
            UnityEngine.Debug.Log("MixedSpace: repaired xrloc.dll import settings (Windows Editor/Windows x64 only).");
        }

        /// <summary>
        /// 按 SDK 内的目录和架构修复原生插件，避免 Windows、WSA、iOS 库混入 Android。
        /// Repairs bundled native plugins by directory and architecture to keep Windows, WSA and iOS payloads out of Android.
        /// </summary>
        internal static void RepairBundledNativeImporters()
        {
            const string prefix = "Packages/com.njyjxr.mixedspace/Runtime/Plugins/";
            var targets = new List<BuildTarget> { BuildTarget.Android, BuildTarget.iOS,
                BuildTarget.StandaloneWindows, BuildTarget.StandaloneWindows64, BuildTarget.StandaloneOSX,
                BuildTarget.StandaloneLinux64, BuildTarget.WebGL, BuildTarget.WSAPlayer, BuildTarget.tvOS };
            if (Enum.TryParse("VisionOS", out BuildTarget visionTarget)) targets.Add(visionTarget);
            foreach (var plugin in PluginImporter.GetAllImporters())
            {
                if (!plugin.assetPath.StartsWith(prefix, StringComparison.Ordinal)) continue;
                string relative = plugin.assetPath.Substring(prefix.Length);
                BuildTarget allowed;
                string cpu;
                bool editor = false;
                if (relative.StartsWith("x86_64/", StringComparison.Ordinal) && relative.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                { allowed = BuildTarget.StandaloneWindows64; cpu = "x86_64"; editor = true; }
                else if (relative.StartsWith("WSA/ARM64/", StringComparison.Ordinal) && relative.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                { allowed = BuildTarget.WSAPlayer; cpu = "ARM64"; }
                else if (relative.StartsWith("iOS/", StringComparison.Ordinal) && relative.EndsWith(".a", StringComparison.OrdinalIgnoreCase))
                { allowed = BuildTarget.iOS; cpu = "AnyCPU"; }
                else if (relative.StartsWith("Android/libs/arm64-v8a/", StringComparison.Ordinal) && relative.EndsWith(".so", StringComparison.OrdinalIgnoreCase))
                { allowed = BuildTarget.Android; cpu = "ARM64"; }
                else if (relative.StartsWith("Android/x86_64/", StringComparison.Ordinal) && relative.EndsWith(".so", StringComparison.OrdinalIgnoreCase))
                { allowed = BuildTarget.Android; cpu = "x86_64"; }
                else if (relative.StartsWith("Android/", StringComparison.Ordinal) && relative.EndsWith(".aar", StringComparison.OrdinalIgnoreCase))
                { allowed = BuildTarget.Android; cpu = null; }
                else continue;
                bool clearConstraints = relative.EndsWith("/libxrloc.so", StringComparison.OrdinalIgnoreCase) ||
                    relative.EndsWith("/libxrloc.a", StringComparison.OrdinalIgnoreCase);
                bool changed = plugin.GetCompatibleWithAnyPlatform() || plugin.GetCompatibleWithEditor() != editor ||
                    (!string.IsNullOrEmpty(cpu) && plugin.GetPlatformData(allowed, "CPU") != cpu) ||
                    (editor && (plugin.GetEditorData("OS") != "Windows" || plugin.GetEditorData("CPU") != "x86_64")) ||
                    (clearConstraints && plugin.DefineConstraints.Length != 0);
                foreach (var target in targets)
                    changed |= plugin.GetCompatibleWithPlatform(target) != (target == allowed);
                if (!changed) continue;
                plugin.SetCompatibleWithAnyPlatform(false);
                plugin.SetCompatibleWithEditor(editor);
                foreach (var target in targets) plugin.SetCompatibleWithPlatform(target, target == allowed);
                if (!string.IsNullOrEmpty(cpu)) plugin.SetPlatformData(allowed, "CPU", cpu);
                if (editor)
                {
                    plugin.SetEditorData("OS", "Windows");
                    plugin.SetEditorData("CPU", "x86_64");
                }
                if (clearConstraints) plugin.DefineConstraints = Array.Empty<string>();
                plugin.SaveAndReimport();
                UnityEngine.Debug.Log($"MixedSpace: repaired {relative} -> {allowed}/{cpu}, Editor={editor}.");
            }
        }

        // 窗口和构建前复用只读检查。
        // The window and build share read-only importer checks.
        internal static List<string> CollectIssues(BuildTarget target)
        {
            var issues = new List<string>();
            var imports = PluginImporter.GetAllImporters().Where(p =>
                p.assetPath.EndsWith("/libxrloc.so", StringComparison.OrdinalIgnoreCase)).ToArray();
            foreach (var plugin in imports)
                if (plugin.GetCompatibleWithAnyPlatform() || plugin.GetCompatibleWithEditor() ||
                    plugin.GetCompatibleWithPlatform(BuildTarget.iOS))
                    issues.Add($"{plugin.assetPath}: libxrloc.so must be Android ARM64 only, never Editor/iOS.");

            // 桌面版本允许编辑器及 Windows/Linux 独立应用使用，不应进入使用 libxrloc.so 的移动构建。
            // Desktop twin of the same native library: allowed in the Editor and on Windows/Linux standalone, but it must
            // never leak into a mobile build where libxrloc.so is the payload.
            var desktopImports = PluginImporter.GetAllImporters().Where(p =>
                p.assetPath.EndsWith("/xrloc.dll", StringComparison.OrdinalIgnoreCase)).ToArray();
            foreach (var plugin in desktopImports)
                if (plugin.GetCompatibleWithAnyPlatform() ||
                    plugin.GetCompatibleWithPlatform(BuildTarget.Android) ||
                    plugin.GetCompatibleWithPlatform(BuildTarget.iOS))
                    issues.Add($"{plugin.assetPath}: xrloc.dll must be Editor/desktop only, never Android/iOS.");

            // iOS 地图加载依赖此静态库，构建前验证其存在且仅对 iOS 启用。
            // iOS map loading requires this archive; verify its presence and iOS-only import settings before building.
            var iosImports = PluginImporter.GetAllImporters().Where(p =>
                p.assetPath.EndsWith("/libxrloc.a", StringComparison.OrdinalIgnoreCase)).ToArray();
            foreach (var plugin in iosImports)
                if (plugin.GetCompatibleWithAnyPlatform() || plugin.GetCompatibleWithEditor() ||
                    plugin.GetCompatibleWithPlatform(BuildTarget.Android))
                    issues.Add($"{plugin.assetPath}: libxrloc.a must be iOS only, never Editor/Android.");

            if (target == BuildTarget.iOS &&
                (!Version.TryParse(PlayerSettings.iOS.targetOSVersionString, out var iosVersion) ||
                 iosVersion.CompareTo(new Version(13, 0)) < 0))
                issues.Add("MixedSpace bundled iOS native libraries require Minimum iOS Version 13.0 or newer. Use Mobile environment Fix All.");

            if (target == BuildTarget.iOS &&
                (iosImports.Length != 1 || !iosImports[0].GetCompatibleWithPlatform(BuildTarget.iOS) ||
                 iosImports[0].DefineConstraints.Length != 0))
                issues.Add("Expected exactly one enabled iOS libxrloc.a with no Define Constraints. " +
                           "It is required for authorization and for importing current-generation map containers.");

            if (target != BuildTarget.Android) return issues;
            if (PlayerSettings.Android.targetArchitectures != AndroidArchitecture.ARM64)
                issues.Add("MixedSpace xrloc authorization and map localization require an ARM64-only Android build.");
            var androidImports = imports.Where(p => p.GetCompatibleWithPlatform(BuildTarget.Android)).ToArray();
            if (androidImports.Length != 1 ||
                androidImports[0].GetPlatformData(BuildTarget.Android, "CPU") != "ARM64" ||
                androidImports[0].DefineConstraints.Length != 0)
            {
                issues.Add("Expected exactly one enabled Android ARM64 libxrloc.so with no Define Constraints. " +
                    "It is required for authorization and map localization. " +
                    $"Found {imports.Length} importer(s), {androidImports.Length} enabled for Android.");
                foreach (var plugin in imports)
                    issues.Add($"  {plugin.assetPath}: Android={plugin.GetCompatibleWithPlatform(BuildTarget.Android)}, " +
                        $"AnyPlatform={plugin.GetCompatibleWithAnyPlatform()}, Editor={plugin.GetCompatibleWithEditor()}, " +
                        $"CPU={plugin.GetPlatformData(BuildTarget.Android, "CPU")}, " +
                        $"DefineConstraints=[{string.Join(", ", plugin.DefineConstraints)}]");
            }
            return issues;
        }
    }
}
