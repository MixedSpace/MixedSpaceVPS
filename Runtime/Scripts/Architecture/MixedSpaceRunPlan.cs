using UnityEngine;

namespace MixedSpace
{
    /// <summary>
    /// 可复用的启动策略资产，不保存令牌、场景引用、UI 回调或平台实现。
    /// 创建运行时配置时复制各项策略，避免运行中的修改影响共享资产。
    /// StartAutomatically 控制 SDK 自动初始化；调度配置中的 StartWhenReady 控制就绪后自动定位。
    /// Reusable startup policy asset without tokens, scene references, UI callbacks or platform
    /// implementations. Runtime settings are copied to isolate shared assets. StartAutomatically controls SDK
    /// initialization; scheduling StartWhenReady controls automatic localization after readiness.
    /// </summary>
    [CreateAssetMenu(menuName = "MixedSpace/Run Plan", fileName = "MixedSpaceRunPlan")]
    public sealed class MixedSpaceRunPlan : ScriptableObject
    {
        [SerializeField] private bool m_StartAutomatically = true;
        [SerializeField, Tooltip("写入融合输入及 World 矩阵/位姿 JSON 日志。World 输出可能逐帧写入，仅在诊断时启用；重新启动运行时后生效。\nWrite localization input images/metadata, fusion-input and World matrix/pose JSON logs. World output may write every frame; enable only for diagnostics. Restart the runtime to apply.")]
        private bool m_WritePoseLogs;
        [SerializeField, Min(0f), Tooltip("PoseFusionSetConfig 的 refine_threshold_dist，单位米，默认 0.4。重新初始化 SDK 后生效。\nPoseFusion refine_threshold_dist in meters. Restart the SDK runtime to apply.")]
        private float m_PoseFusionRefineThresholdDist = 0.4f;
        [SerializeField] private MixedSpaceLocalizationMode m_LocalizationMode = MixedSpaceLocalizationMode.OnDevice;
        [SerializeField] private int m_FrameRate = 60;
        [SerializeField] private bool m_LimitCapture = true;
        [SerializeField] private MixedSpaceLogger.LoggingLevel m_Diagnostics = MixedSpaceLogger.LoggingLevel.ErrorsAndWarnings;
        [SerializeField] private MixedSpaceSessionOptions m_Scheduling = new MixedSpaceSessionOptions();
        [SerializeField] private MixedSpaceTrackingOptions m_Quality = new MixedSpaceTrackingOptions();
        [SerializeField] private MixedSpaceOnDeviceOptions m_DeviceSearch = new MixedSpaceOnDeviceOptions();
        [SerializeField] private MixedSpaceCloudOptions m_RemoteSearch = new MixedSpaceCloudOptions();
        [SerializeField] private MixedSpaceGeoOptions m_GeographicSearch = new MixedSpaceGeoOptions();

        public bool StartAutomatically => m_StartAutomatically;
        public int FrameRate => m_FrameRate;
        public bool LimitCapture => m_LimitCapture;
        public MixedSpaceLogger.LoggingLevel Diagnostics => m_Diagnostics;

        /// <summary>
        /// 复制运行时独占的定位和跟踪策略；自动初始化、帧率等启动选项通过本资产的独立属性读取。
        /// Copies localization and tracking policies for one runtime; separate asset properties provide
        /// initialization, frame-rate and other startup options.
        /// </summary>
        public MixedSpaceRuntimeConfiguration CreateRuntimeSettings()
        {
            var settings = new MixedSpaceRuntimeConfiguration();
            settings.WritePoseLogs = m_WritePoseLogs;
            settings.PoseFusionRefineThresholdDist = m_PoseFusionRefineThresholdDist;
            settings.LocalizationMode = m_LocalizationMode == 0 ? MixedSpaceLocalizationMode.OnDevice : m_LocalizationMode;
            settings.CopyPoliciesFrom(m_Scheduling, m_DeviceSearch, m_RemoteSearch,
                m_GeographicSearch, m_Quality);
            return settings;
        }

        /// <summary>
        /// 将当前运行时策略和指定的启动选项复制到本资产，仅用于显式编辑配置。
        /// 不持有来源配置对象或其中的场景引用。
        /// Explicit authoring operation that copies runtime policies and supplied startup options to this
        /// asset without retaining source settings or scene references.
        /// </summary>
        public void Capture(MixedSpaceRuntimeConfiguration source, bool startAutomatically,
            int frameRate, bool limitCapture, MixedSpaceLogger.LoggingLevel diagnostics)
        {
            if (source == null) throw new System.ArgumentNullException(nameof(source));
            m_WritePoseLogs = source.WritePoseLogs;
            m_PoseFusionRefineThresholdDist = source.PoseFusionRefineThresholdDist;
            m_LocalizationMode = source.LocalizationMode;
            m_Scheduling = MixedSpaceRuntimeConfiguration.CopyValue(source.Session);
            m_Quality = MixedSpaceRuntimeConfiguration.CopyValue(source.Tracking);
            m_DeviceSearch = MixedSpaceRuntimeConfiguration.CopyValue(source.OnDevice);
            m_RemoteSearch = MixedSpaceRuntimeConfiguration.CopyValue(source.Cloud);
            m_GeographicSearch = MixedSpaceRuntimeConfiguration.CopyValue(source.Geo);
            m_StartAutomatically = startAutomatically;
            m_FrameRate = frameRate;
            m_LimitCapture = limitCapture;
            m_Diagnostics = diagnostics;
        }
    }
}
