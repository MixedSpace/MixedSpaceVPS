using UnityEngine;

namespace MixedSpace.XR
{
    /// <summary>
    /// PICO 4 Enterprise 的 VST 采集配置，与 Ultra Enterprise 分离。
    /// PICO 4 Enterprise VST capture settings, separate from Ultra Enterprise.
    /// </summary>
    [CreateAssetMenu(menuName = "MixedSpace/Platforms/PICO 4E Profile", fileName = "DefaultPico4EProfile")]
    public sealed class MixedSpacePico4EProfile : ScriptableObject
    {
        // 固定为已接入的图像规格，避免修改尺寸后沿用不匹配的内参。
        // Keep the integrated image dimensions fixed to avoid reusing incompatible intrinsics after a size change.
        public int Width => 1400;
        public int Height => 1050;
        [SerializeField, Min(1), Tooltip("企业服务辅助绑定的告警等待时间，单位毫秒；超时不阻塞 VST 相机启动。\nWarning timeout for auxiliary enterprise binding in milliseconds; expiry does not block VST camera startup.")]
        private int m_StartupTimeoutMilliseconds = 10000;
        public int StartupTimeoutMilliseconds => m_StartupTimeoutMilliseconds;
    }
}
