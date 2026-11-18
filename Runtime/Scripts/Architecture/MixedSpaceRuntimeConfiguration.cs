using System;
using System.Linq;
using MixedSpace.XR;
using UnityEngine;
using UnityEngine.Events;
using Object = UnityEngine.Object;

namespace MixedSpace
{
    /// <summary>
    /// 创建运行时时选择的 SDK 级定位路由。
    /// SDK-wide routing selected when a runtime is created.
    /// </summary>
    public enum MixedSpaceLocalizationMode { OnDevice = 1, Cloud = 2 }
    /// <summary>
    /// 平台无关运行时的可序列化启动配置，每个运行时独占；组装层提供强类型设置和扩展接口，服务不自行查找场景组件。
    /// Serializable startup settings for the platform-neutral MixedSpace runtime.
    /// Each runtime owns this data; composition supplies typed settings and
    /// extension ports to services.
    /// </summary>
    [Serializable]
    public sealed class MixedSpaceRuntimeConfiguration
    {
        public MixedSpaceLocalizationMode LocalizationMode { get; set; } = MixedSpaceLocalizationMode.OnDevice;
        public bool WritePoseLogs { get; set; }
        private MixedSpacePoseFusionConfig m_PoseFusionConfig = MixedSpacePoseFusionConfig.Default;
        /// <summary>
        /// 创建 World 融合会话时使用的配置。
        /// Configuration used when creating a World fusion session.
        /// </summary>
        public MixedSpacePoseFusionConfig PoseFusionConfig
        {
            get => m_PoseFusionConfig;
            set
            {
                value.Validate();
                m_PoseFusionConfig = value;
            }
        }

        /// <summary>
        /// 兼容已有调用方，读写 PoseFusionConfig 中的距离纠偏阈值（米）。
        /// Compatibility accessor for the distance refinement threshold in PoseFusionConfig, in meters.
        /// </summary>
        public float PoseFusionRefineThresholdDist
        {
            get => m_PoseFusionConfig.refine_threshold_dist;
            set
            {
                if (float.IsNaN(value) || float.IsInfinity(value) || value < 0f)
                    throw new ArgumentOutOfRangeException(nameof(value), "Pose fusion refine distance must be finite and nonnegative.");
                m_PoseFusionConfig.refine_threshold_dist = value;
            }
        }
        [SerializeField] private MixedSpaceSessionOptions m_Session = new MixedSpaceSessionOptions();
        [SerializeField] private MixedSpaceOnDeviceOptions m_OnDevice = new MixedSpaceOnDeviceOptions();
        [SerializeField] private MixedSpaceCloudOptions m_Cloud = new MixedSpaceCloudOptions();
        [SerializeField] private MixedSpaceGeoOptions m_Geo = new MixedSpaceGeoOptions();
        [SerializeField] private MixedSpaceTrackingOptions m_Tracking = new MixedSpaceTrackingOptions();

        [SerializeField]
        [MixedSpaceComponentContract(typeof(IMixedSpaceLocalizationBinding))]
        [Tooltip("只读定位视图，观察解算状态，不修改位姿。\nRead-only localization views. They observe solved state and cannot change the pose.")]
        private Object[] m_LocalizationViews = Array.Empty<Object>();

        [SerializeField, MixedSpaceComponentContract(typeof(IMixedSpaceAlignmentSolver))]
        [Tooltip("可选位姿计算策略；留空使用标准地图对齐求解器。\nOptional pose calculation strategy. Leave empty for the standard map alignment solver.")]
        private Object m_AlignmentSolverOverride;

        [SerializeField]
        [MixedSpaceComponentContract(typeof(IMixedSpaceLocalizationProvider))]
        [Tooltip("可选的应用自定义定位提供器；内置提供器由代码创建。\nOptional application-defined localization providers. Built-in providers are created in code.")]
        private Object[] m_ExtensionProviders = Array.Empty<Object>();

        public MixedSpaceSessionOptions Session => m_Session ??= new MixedSpaceSessionOptions();
        public MixedSpaceOnDeviceOptions OnDevice => m_OnDevice ??= new MixedSpaceOnDeviceOptions();
        public MixedSpaceCloudOptions Cloud => m_Cloud ??= new MixedSpaceCloudOptions();
        public MixedSpaceGeoOptions Geo => m_Geo ??= new MixedSpaceGeoOptions();
        public MixedSpaceTrackingOptions Tracking => m_Tracking ??= new MixedSpaceTrackingOptions();

        /// <summary>
        /// 创建独立的策略值副本，不复制场景对象。
        /// Builds detached policy values without copying scene objects.
        /// </summary>
        internal void CopyPoliciesFrom(
            MixedSpaceSessionOptions session, MixedSpaceOnDeviceOptions device,
            MixedSpaceCloudOptions cloud, MixedSpaceGeoOptions geo,
            MixedSpaceTrackingOptions tracking)
        {
            m_Session = CopyValue(session);
            m_OnDevice = CopyValue(device);
            m_Cloud = CopyValue(cloud);
            m_Geo = CopyValue(geo);
            m_Tracking = CopyValue(tracking);
        }

        internal static T CopyValue<T>(T value) where T : new() =>
            value == null ? new T() : JsonUtility.FromJson<T>(JsonUtility.ToJson(value));

        /// <summary>
        /// 场景扩展接口由场景所有者提供。
        /// Scene ports are provided by the scene owner.
        /// </summary>
        internal void CopyScenePortsFrom(MixedSpaceScenePorts scene)
        {
            m_LocalizationViews = scene.CopyViews();
            m_ExtensionProviders = scene.CopyProviders();
            m_AlignmentSolverOverride = scene.Solver;
        }

        public IMixedSpaceLocalizationBinding[] LocalizationViews =>
            (m_LocalizationViews ?? Array.Empty<Object>())
            .Where(value => value != null)
            .Select(value => value as IMixedSpaceLocalizationBinding ??
                throw new InvalidOperationException(
                    $"'{value.name}' must implement IMixedSpaceLocalizationBinding. " +
                    "Use an alignment solver override for pose corrections, not a frame processor."))
            .ToArray();

        public IMixedSpaceAlignmentSolver AlignmentSolverOverride =>
            m_AlignmentSolverOverride == null ? null :
            m_AlignmentSolverOverride as IMixedSpaceAlignmentSolver ??
            throw new InvalidOperationException("The alignment override must implement IMixedSpaceAlignmentSolver.");

        public IMixedSpaceLocalizationProvider[] ExtensionProviders =>
            (m_ExtensionProviders ?? Array.Empty<Object>())
            .OfType<IMixedSpaceLocalizationProvider>()
            .ToArray();
    }

    [Serializable]
    public sealed class MixedSpaceSessionOptions
    {
        [SerializeField, Tooltip("SDK 初始化完成后自动开始定位；与是否自动初始化 SDK 是两个独立设置。\nStart localization automatically after SDK initialization; this is separate from automatically initializing the SDK.")]
        private bool m_StartWhenReady = true;
        [SerializeField, Min(0f), Tooltip("连续定位周期的调度间隔，单位为秒。\nScheduling interval between continuous localization cycles, in seconds.")]
        private float m_CycleIntervalSeconds = 2f;
        [SerializeField, Tooltip("开始或恢复定位后启用密集定位，暂时跳过常规调度间隔。\nEnable burst localization after start or resume, temporarily skipping the regular interval.")]
        private bool m_StartupBurstEnabled = true;
        [SerializeField, Min(0), Tooltip("退出密集定位所需的成功定位次数。\nSuccessful localization count required to exit burst mode.")]
        private int m_BurstFixLimit = 10;
        [SerializeField, Min(0f), Tooltip("密集定位的最长持续时间，单位为秒。\nMaximum burst duration, in seconds.")]
        private float m_BurstDurationSeconds = 15f;
        [SerializeField, Tooltip("会话挂起时重置定位会话结果，包括应用暂停或主动挂起；不重建整个 SDK。\nReset session results when suspended, including application pause or explicit suspension; do not rebuild the SDK.")]
        private bool m_ReinitializeOnSuspend;
        [SerializeField, Tooltip("定位会话重置完成后恢复定位；关闭时保持停止，不控制整个 SDK 的重建。\nResume localization after session reset; otherwise remain stopped. This does not control SDK rebuilds.")]
        private bool m_ResumeAfterReinitialize = true;
        [SerializeField, Tooltip("向只读视图和应用订阅者发布已求解的定位状态。\nPublish solved localization state to read-only views and application subscribers.")]
        private bool m_PublishLocalizationState;
        [SerializeField, Tooltip("启用手动定位闸门：成功定位后自动挂起，应用可通过 AllowLocalization 恢复、BlockLocalization 挂起。放行后可连续尝试至成功，并非每次只处理一帧；不禁止自动开始。\nEnable the manual gate: automatically suspend after success; AllowLocalization resumes and BlockLocalization suspends. An open gate may process multiple attempts until success, not just one frame. Automatic startup is not disabled.")]
        private bool m_ManualCycleGateEnabled;

        public bool StartWhenReady { get => m_StartWhenReady; set => m_StartWhenReady = value; }
        public float CycleIntervalSeconds { get => m_CycleIntervalSeconds; set => m_CycleIntervalSeconds = Mathf.Max(0f, value); }
        public bool StartupBurstEnabled { get => m_StartupBurstEnabled; set => m_StartupBurstEnabled = value; }
        public int BurstFixLimit { get => m_BurstFixLimit; set => m_BurstFixLimit = Mathf.Max(0, value); }
        public float BurstDurationSeconds { get => m_BurstDurationSeconds; set => m_BurstDurationSeconds = Mathf.Max(0f, value); }
        public bool ReinitializeOnSuspend { get => m_ReinitializeOnSuspend; set => m_ReinitializeOnSuspend = value; }
        public bool ResumeAfterReinitialize { get => m_ResumeAfterReinitialize; set => m_ResumeAfterReinitialize = value; }
        public bool PublishLocalizationState { get => m_PublishLocalizationState; set => m_PublishLocalizationState = value; }
        public bool ManualCycleGateEnabled { get => m_ManualCycleGateEnabled; set => m_ManualCycleGateEnabled = value; }
    }

    [Serializable]
    public sealed class MixedSpaceOnDeviceOptions
    {
        [SerializeField] private MixedSpaceProviderActivationMode m_ActivationMode = MixedSpaceProviderActivationMode.Always;
        [SerializeField, Tooltip("Prior 使用可靠的地图位置帮助 XRLoc 选择定位候选。\nPrior uses a reliable map position to rank XRLoc localization candidates.")]
        private MixedSpaceSolverType m_SolverType = MixedSpaceSolverType.Lean;
        [SerializeField, Min(0), Tooltip("供支持近邻数的自定义定位器使用。\nUsed by custom localizers that support a neighbor count.")]
        private int m_PriorNearestNeighborCount;
        [SerializeField, Min(0f), Tooltip("供支持搜索半径的自定义定位器使用。\nUsed by custom localizers that support a search radius.")]
        private float m_PriorRadius;

        public MixedSpaceProviderActivationMode ActivationMode { get => m_ActivationMode; set => m_ActivationMode = value; }
        public MixedSpaceSolverType SolverType { get => m_SolverType; set => m_SolverType = value; }
        public int PriorNearestNeighborCount { get => m_PriorNearestNeighborCount; set => m_PriorNearestNeighborCount = Mathf.Max(0, value); }
        public float PriorRadius { get => m_PriorRadius; set => m_PriorRadius = Mathf.Max(0f, value); }
    }

    [Serializable]
    public sealed class MixedSpaceCloudOptions
    {
        [SerializeField] private MixedSpaceProviderActivationMode m_ActivationMode = MixedSpaceProviderActivationMode.WhenNecessary;
        [SerializeField] private MixedSpaceSolverType m_SolverType = MixedSpaceSolverType.Lean;
        [SerializeField, Min(0)] private int m_PriorNearestNeighborCountMin = 60;
        [SerializeField, Min(0)] private int m_PriorNearestNeighborCountMax = 720;
        [SerializeField] private Vector3 m_PriorScale = Vector3.one;
        [SerializeField, Min(0f)] private float m_PriorRadius;

        public MixedSpaceProviderActivationMode ActivationMode { get => m_ActivationMode; set => m_ActivationMode = value; }
        public MixedSpaceSolverType SolverType { get => m_SolverType; set => m_SolverType = value; }
        public int PriorNearestNeighborCountMin { get => m_PriorNearestNeighborCountMin; set => m_PriorNearestNeighborCountMin = Mathf.Max(0, value); }
        public int PriorNearestNeighborCountMax { get => m_PriorNearestNeighborCountMax; set => m_PriorNearestNeighborCountMax = Mathf.Max(0, value); }
        public Vector3 PriorScale { get => m_PriorScale; set => m_PriorScale = value; }
        public float PriorRadius { get => m_PriorRadius; set => m_PriorRadius = Mathf.Max(0f, value); }
    }

    [Serializable]
    public sealed class MixedSpaceGeoOptions
    {
        [SerializeField] private MixedSpaceProviderActivationMode m_ActivationMode = MixedSpaceProviderActivationMode.WhenNecessary;
        [SerializeField] private MixedSpaceSolverType m_SolverType = MixedSpaceSolverType.Default;
        [SerializeField, Min(0)] private int m_PriorNearestNeighborCountMin = 60;
        [SerializeField, Min(0)] private int m_PriorNearestNeighborCountMax = 720;
        [SerializeField] private Vector3 m_PriorScale = Vector3.one;
        [SerializeField, Min(0f)] private float m_PriorRadius;

        public MixedSpaceProviderActivationMode ActivationMode { get => m_ActivationMode; set => m_ActivationMode = value; }
        public MixedSpaceSolverType SolverType { get => m_SolverType; set => m_SolverType = value; }
        public int PriorNearestNeighborCountMin { get => m_PriorNearestNeighborCountMin; set => m_PriorNearestNeighborCountMin = Mathf.Max(0, value); }
        public int PriorNearestNeighborCountMax { get => m_PriorNearestNeighborCountMax; set => m_PriorNearestNeighborCountMax = Mathf.Max(0, value); }
        public Vector3 PriorScale { get => m_PriorScale; set => m_PriorScale = value; }
        public float PriorRadius { get => m_PriorRadius; set => m_PriorRadius = Mathf.Max(0f, value); }
    }

    [Serializable]
    public sealed class MixedSpaceTrackingOptions
    {
        [SerializeField, Min(0f)]
        private float m_PoseRetentionSeconds = 2f;
        [SerializeField]
        private bool m_CountProvidersSeparately;

        public float PoseRetentionSeconds { get => m_PoseRetentionSeconds; set => m_PoseRetentionSeconds = Mathf.Max(0f, value); }
        public bool CountProvidersSeparately { get => m_CountProvidersSeparately; set => m_CountProvidersSeparately = value; }
    }

    /// <summary>
    /// 面向 Unity 的事件绑定，纯运行时服务通过委托发布；这是默认组装中的 UnityEvent 桥接点。
    /// Unity-facing event bindings. Pure runtime services publish through delegates;
    /// this object is the only UnityEvent bridge in the default composition.
    /// </summary>
    [Serializable]
    public sealed class MixedSpaceApplicationSignals
    {
        /// <summary>
        /// 定位会话进入运行状态时触发，包括开始、恢复和重置后自动恢复；不代表定位已成功。
        /// Raised when the session enters Running after start, resume or automatic recovery after reset;
        /// not a localization success event.
        /// </summary>
        [Tooltip("定位会话进入运行状态时触发，包括开始、恢复和重置后自动恢复；不代表定位已成功。\nRaised when the session enters Running after start, resume or automatic recovery after reset; not a localization success event.")]
        public UnityEvent LocalizationStarted = new UnityEvent();
        /// <summary>
        /// 定位会话进入停止状态时触发，包括异常停止；不表示关闭相机或卸载地图。
        /// Raised when the session stops, including failure; does not imply camera shutdown or map
        /// unloading.
        /// </summary>
        [Tooltip("定位会话进入停止状态时触发，包括异常停止；不表示关闭相机或卸载地图。\nRaised when the session stops, including failure; does not imply camera shutdown or map unloading.")]
        public UnityEvent LocalizationStopped = new UnityEvent();
        /// <summary>
        /// 定位会话结果重置完成时触发；是否自动恢复由 RunPlan 控制，累计统计不清零。
        /// Raised after session results are reset; RunPlan controls resumption and cumulative statistics
        /// remain unchanged.
        /// </summary>
        [Tooltip("定位会话结果重置完成时触发；是否自动恢复由 RunPlan 控制，累计统计不清零。\nRaised after session results are reset; RunPlan controls resumption and cumulative statistics remain unchanged.")]
        public UnityEvent LocalizationResetCompleted = new UnityEvent();
        /// <summary>
        /// 定位会话进入暂停状态时触发。
        /// Raised when the localization session becomes suspended.
        /// </summary>
        [Tooltip("定位会话进入暂停状态时触发。\nRaised when the localization session becomes suspended.")]
        public UnityEvent SessionSuspended = new UnityEvent();
        /// <summary>
        /// 通过 Continue 或 AllowLocalization 恢复运行时触发；首次 Begin 不触发此事件。
        /// Raised on resumption through Continue or AllowLocalization, not on the initial Begin.
        /// </summary>
        [Tooltip("通过 Continue 或 AllowLocalization 恢复运行时触发；首次 Begin 不触发此事件。\nRaised on resumption through Continue or AllowLocalization, not on the initial Begin.")]
        public UnityEvent SessionActivated = new UnityEvent();
        /// <summary>
        /// 定位会话进入重置状态时触发，表示开始重置；完成通知使用 LocalizationResetCompleted。
        /// Raised on entering Resetting, indicating reset start. Use LocalizationResetCompleted for
        /// completion.
        /// </summary>
        [Tooltip("定位会话进入重置状态时触发，表示开始重置；完成通知使用 LocalizationResetCompleted。\nRaised on entering Resetting, indicating reset start. Use LocalizationResetCompleted for completion.")]
        public UnityEvent SessionReinitialized = new UnityEvent();
        /// <summary>
        /// 首次定位成功时触发；会话重置后重新计算首次，停止再开始不重置首次标志。
        /// Raised on the first successful localization. Reset restarts first-fix tracking; stop and
        /// restart do not.
        /// </summary>
        [Tooltip("首次定位成功时触发；会话重置后重新计算首次，停止再开始不重置首次标志。\nRaised on the first successful localization. Reset restarts first-fix tracking; stop and restart do not.")]
        public UnityEvent FirstSpatialFix = new UnityEvent();
        /// <summary>
        /// 当前定位周期成功时触发；参数为成功结果对应的地图 ID 集合。
        /// Raised when the current cycle succeeds, carrying the IDs of successfully localized maps.
        /// </summary>
        [Tooltip("当前定位周期成功时触发；参数为成功结果对应的地图 ID 集合。\nRaised when the current cycle succeeds, carrying the IDs of successfully localized maps.")]
        public UnityEvent<int[]> SpatialFixesAvailable = new UnityEvent<int[]>();
        /// <summary>
        /// 定位周期报告产生时触发；参数包含本轮结果，可能没有成功结果；不是初始化完成通知。
        /// Raised when a cycle report is produced, possibly with no successful result; not an
        /// initialization-complete event.
        /// </summary>
        [Tooltip("定位周期报告产生时触发；参数包含本轮结果，可能没有成功结果；不是初始化完成通知。\nRaised when a cycle report is produced, possibly with no successful result; not an initialization-complete event.")]
        public UnityEvent<MixedSpaceLocalizationBatch> FrameEvaluationFinished =
            new UnityEvent<MixedSpaceLocalizationBatch>();
        /// <summary>
        /// 当前定位周期没有成功结果时触发；不能据此判断网络是否断开。
        /// Raised when a cycle has no successful outcome; this alone does not prove network
        /// disconnection.
        /// </summary>
        [Tooltip("当前定位周期没有成功结果时触发；不能据此判断网络是否断开。\nRaised when a cycle has no successful outcome; this alone does not prove network disconnection.")]
        public UnityEvent FrameEvaluationMissed = new UnityEvent();
        /// <summary>
        /// 云定位提供器报告进度时触发；参数为提供器进度值，不是定位置信度。
        /// Cloud provider progress notification; the parameter is progress, not localization
        /// confidence.
        /// </summary>
        [Tooltip("云定位提供器报告进度时触发；参数为提供器进度值，不是定位置信度。\nCloud provider progress notification; the parameter is progress, not localization confidence.")]
        public UnityEvent<float> CloudSolveProgress = new UnityEvent<float>();
        /// <summary>
        /// 地理定位进度通知接口；参数为提供器进度值，是否触发取决于提供器实现。
        /// Geographic provider progress notification; emission depends on the provider
        /// implementation.
        /// </summary>
        [Tooltip("地理定位进度通知接口；参数为提供器进度值，是否触发取决于提供器实现。\nGeographic provider progress notification; emission depends on the provider implementation.")]
        public UnityEvent<float> GeoSolveProgress = new UnityEvent<float>();
        /// <summary>
        /// 平台跟踪位姿不可用时触发。
        /// Raised when the platform tracking pose becomes unavailable.
        /// </summary>
        [Tooltip("平台跟踪位姿不可用时触发。\nRaised when the platform tracking pose becomes unavailable.")]
        public UnityEvent PlatformPoseUnavailable = new UnityEvent();
        /// <summary>
        /// 跟踪转为稳定时触发；bool 参数表示自通知状态清理后是否首次稳定。
        /// Raised on stable tracking; the bool indicates the first stable notification since notification
        /// state was cleared.
        /// </summary>
        [Tooltip("跟踪转为稳定时触发；bool 参数表示自通知状态清理后是否首次稳定。\nRaised on stable tracking; the bool indicates the first stable notification since notification state was cleared.")]
        public UnityEvent<bool> TrackingStabilized = new UnityEvent<bool>();
        /// <summary>
        /// 跟踪转为退化时触发；bool 参数表示自通知状态清理后是否首次退化。
        /// Raised on degraded tracking; the bool indicates the first degraded notification since
        /// notification state was cleared.
        /// </summary>
        [Tooltip("跟踪转为退化时触发；bool 参数表示自通知状态清理后是否首次退化。\nRaised on degraded tracking; the bool indicates the first degraded notification since notification state was cleared.")]
        public UnityEvent<bool> TrackingDegraded = new UnityEvent<bool>();
    }
}
