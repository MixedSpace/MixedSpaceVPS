using System;
using HuaweiARUnitySDK;
using UnityEngine;

namespace MixedSpace.XR.Huawei
{
    /// <summary>
    /// 场景实例共享的 Huawei AR Engine 采集配置。
    /// Huawei AR Engine capture settings shared by scene instances.
    /// </summary>
    [CreateAssetMenu(menuName = "MixedSpace/Platforms/Huawei AR Engine Profile")]
    public sealed class MixedSpaceHuaweiProfile : ScriptableObject
    {
        [SerializeField] private ARConfigBase m_Config;
        [SerializeField] private MixedSpaceHuaweiAdapter.CaptureResolution m_Resolution =
            MixedSpaceHuaweiAdapter.CaptureResolution.Max;
        [SerializeField] private MixedSpacePixelFormat m_Format = MixedSpacePixelFormat.Luminance8;
        [SerializeField, Min(1)] private int m_StartupTimeoutSeconds = 15;

        public ARConfigBase Config => m_Config;
        public MixedSpaceHuaweiAdapter.CaptureResolution Resolution => m_Resolution;
        public MixedSpacePixelFormat Format => m_Format;
        public int StartupTimeoutSeconds => m_StartupTimeoutSeconds;

        public void Validate()
        {
            if (m_Config == null)
                throw new InvalidOperationException("Huawei profile needs an ARWorldTrackingConfig asset.");
            if (!Enum.IsDefined(typeof(MixedSpaceHuaweiAdapter.CaptureResolution), m_Resolution))
                throw new InvalidOperationException("Unsupported Huawei capture resolution.");
            if (m_Format != MixedSpacePixelFormat.Luminance8 && m_Format != MixedSpacePixelFormat.Rgb24)
                throw new InvalidOperationException("Unsupported Huawei pixel format.");
            if (m_StartupTimeoutSeconds < 1)
                throw new InvalidOperationException("Huawei startup timeout must be positive.");
        }
    }
}
