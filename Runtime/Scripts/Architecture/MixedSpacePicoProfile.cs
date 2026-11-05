using UnityEngine;

namespace MixedSpace.XR
{
    /// <summary>
    /// PICO 4 Ultra Enterprise 相机采集配置，不包含厂商程序集依赖。
    /// PICO 4 Ultra Enterprise capture settings without vendor assembly dependencies.
    /// </summary>
    [CreateAssetMenu(menuName = "MixedSpace/Platforms/PICO 4 UE Profile", fileName = "DefaultPicoProfile")]
    public sealed class MixedSpacePicoProfile : ScriptableObject
    {
        [SerializeField, Min(1), Tooltip("企业相机输出宽度。\nEnterprise camera output width.")]
        private int m_Width = 1920;
        [SerializeField, Min(1), Tooltip("企业相机输出高度。\nEnterprise camera output height.")]
        private int m_Height = 1080;
        [SerializeField, Min(1), Tooltip("绑定服务、打开相机和等待首帧的超时（毫秒）。\nTimeout for service binding, camera opening and first-frame arrival, in milliseconds.")]
        private int m_StartupTimeoutMilliseconds = 10000;
        [SerializeField, Min(1), Tooltip("帧从接收到使用的最大允许时间（毫秒）。\nMaximum permitted frame age between reception and use, in milliseconds.")]
        private int m_MaxFrameAgeMilliseconds = 500;
        public int Width => m_Width;
        public int Height => m_Height;
        public int StartupTimeoutMilliseconds => m_StartupTimeoutMilliseconds;
        public int MaxFrameAgeMilliseconds => m_MaxFrameAgeMilliseconds;
    }
}
