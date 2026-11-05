using System;
using UnityEngine;

namespace MixedSpace.XR
{
    /// <summary>
    /// AR Foundation 编辑资产归平台程序集而非 SDK 入口；各适配器获取独立快照，运行时 UI 修改不能回写共享资产或其他会话。
    /// AR Foundation authoring data, owned by the platform assembly rather than
    /// the SDK facade. Each adapter receives a detached snapshot: runtime UI
    /// changes must never write back into a shared asset or another session.
    /// </summary>
    [CreateAssetMenu(menuName = "MixedSpace/Platforms/AR Foundation Profile")]
    public sealed class MixedSpaceARFoundationProfile : ScriptableObject
    {
        [Header("Camera readiness")]
        [SerializeField, Min(1)] private int m_ProbeLimit = 10;
        [SerializeField, Min(1)] private int m_ProbeDelayMilliseconds = 100;
        [Header("Capture targets")]
        [SerializeField] private MixedSpaceARFoundationAdapter.CaptureResolutionMode m_Android =
            MixedSpaceARFoundationAdapter.CaptureResolutionMode.SystemDefault;
        [SerializeField] private MixedSpaceARFoundationAdapter.CaptureResolutionMode m_Ios =
            MixedSpaceARFoundationAdapter.CaptureResolutionMode.SystemDefault;
        [SerializeField] private MixedSpacePixelFormat m_Format = MixedSpacePixelFormat.Luminance8;

        /// <summary>
        /// 相机启动前验证，配置无效时不静默回退。
        /// Validate before camera startup; invalid assets do not silently fall back.
        /// </summary>
        public MixedSpaceARFoundationSettings CreateSettings()
        {
            if (m_ProbeLimit < 1 || m_ProbeDelayMilliseconds < 1)
                throw new InvalidOperationException("Camera readiness limits must be positive.");
            ValidateResolution(m_Android);
            ValidateResolution(m_Ios);
            if (m_Format != MixedSpacePixelFormat.Luminance8 && m_Format != MixedSpacePixelFormat.Rgb24)
                throw new InvalidOperationException("Unsupported AR Foundation capture format.");
            return new MixedSpaceARFoundationSettings(m_ProbeLimit, m_ProbeDelayMilliseconds,
                m_Android, m_Ios, m_Format);
        }

        internal static void ValidateResolution(MixedSpaceARFoundationAdapter.CaptureResolutionMode value)
        {
            if (!Enum.IsDefined(typeof(MixedSpaceARFoundationAdapter.CaptureResolutionMode), value))
                throw new ArgumentOutOfRangeException(nameof(value));
        }
    }

    /// <summary>
    /// 会话局部配置值，只有分辨率选项支持运行时控制。
    /// Session-local values. Only resolution choices are runtime controls.
    /// </summary>
    public sealed class MixedSpaceARFoundationSettings
    {
        public int ProbeLimit { get; }
        public int ProbeDelayMilliseconds { get; }
        public MixedSpacePixelFormat Format { get; }
        public MixedSpaceARFoundationAdapter.CaptureResolutionMode Android { get; private set; }
        public MixedSpaceARFoundationAdapter.CaptureResolutionMode Ios { get; private set; }

        internal MixedSpaceARFoundationSettings(int probes, int delay,
            MixedSpaceARFoundationAdapter.CaptureResolutionMode android,
            MixedSpaceARFoundationAdapter.CaptureResolutionMode ios, MixedSpacePixelFormat format)
        {
            ProbeLimit = probes;
            ProbeDelayMilliseconds = delay;
            Android = android;
            Ios = ios;
            Format = format;
        }

        public void SetAndroidResolution(MixedSpaceARFoundationAdapter.CaptureResolutionMode value)
        {
            MixedSpaceARFoundationProfile.ValidateResolution(value);
            Android = value;
        }

        public void SetIosResolution(MixedSpaceARFoundationAdapter.CaptureResolutionMode value)
        {
            MixedSpaceARFoundationProfile.ValidateResolution(value);
            Ios = value;
        }
    }
}
