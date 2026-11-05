using UnityEngine;

namespace MixedSpace
{
    /// <summary>
    /// 平台无关 SDK 支持的操作系统类别。保留 Unknown，使新平台能够报告明确的能力错误，而不是误入 Android 路径。
    /// Operating-system families supported by the platform-neutral SDK layer.
    /// Unknown is intentional: a new platform can boot far enough to report a
    /// clear capability error instead of entering an Android-specific code path.
    /// </summary>
    public enum MixedSpacePlatformKind
    {
        Unknown = 0,
        Android = 1,
        IOS = 2,
        HarmonyOS = 3,
        Windows = 10,
        MacOS = 11,
        Linux = 12,
        Web = 20
    }

    /// <summary>
    /// 提供平台身份，避免应用服务直接耦合 UnityEngine.Application；原生扩展包可在初始化前替换。
    /// Provides platform identity without coupling application services to
    /// UnityEngine.Application. Native platform packages may replace this service
    /// before SDK initialization.
    /// </summary>
    public interface IMixedSpacePlatformInfo
    {
        MixedSpacePlatformKind Kind { get; }

        string RuntimeName { get; }

        bool IsMobile { get; }
    }

    /// <summary>
    /// 已有场景使用的默认 Unity 平台实现。
    /// Default Unity implementation used by existing scenes.
    /// </summary>
    public sealed class UnityMixedSpacePlatformInfo : IMixedSpacePlatformInfo
    {
        public MixedSpacePlatformKind Kind => ResolvePlatform(Application.platform);

        public string RuntimeName => Application.platform.ToString();

        public bool IsMobile => Kind == MixedSpacePlatformKind.Android ||
                                Kind == MixedSpacePlatformKind.IOS ||
                                Kind == MixedSpacePlatformKind.HarmonyOS;

        private static MixedSpacePlatformKind ResolvePlatform(RuntimePlatform platform)
        {
            switch (platform)
            {
                case RuntimePlatform.Android:
                    return MixedSpacePlatformKind.Android;
                case RuntimePlatform.IPhonePlayer:
                    return MixedSpacePlatformKind.IOS;
                case RuntimePlatform.WindowsPlayer:
                case RuntimePlatform.WindowsEditor:
                    return MixedSpacePlatformKind.Windows;
                case RuntimePlatform.OSXPlayer:
                case RuntimePlatform.OSXEditor:
                    return MixedSpacePlatformKind.MacOS;
                case RuntimePlatform.LinuxPlayer:
                case RuntimePlatform.LinuxEditor:
                    return MixedSpacePlatformKind.Linux;
                case RuntimePlatform.WebGLPlayer:
                    return MixedSpacePlatformKind.Web;
                default:
                    return MixedSpacePlatformKind.Unknown;
            }
        }
    }
}
