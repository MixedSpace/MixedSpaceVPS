using UnityEngine;
using UnityEngine.Events;
using System;
using System.Threading.Tasks;
using MixedSpace.XR;
using Object = UnityEngine.Object;

namespace MixedSpace
{
    /// <summary>
    /// 场景中的 SDK 入口，负责创建运行时、提供定位控制接口并转发应用事件。
    /// 同一场景只允许一个主实例；定位控制方法应在 RuntimeReady 之后调用。
    /// Scene SDK entry that creates the runtime, exposes localization controls and forwards
    /// application events. Only one primary instance is allowed per scene; call localization controls after
    /// RuntimeReady.
    /// </summary>
    public class MixedSpaceSDK : MonoBehaviour
    {
        public const string PackageVersion = "2.0.0";

        [SerializeField] private MixedSpaceRunPlan m_RunPlan;
        [SerializeField] private MixedSpaceApplicationBinding m_ApplicationBinding;
        [SerializeField] private MixedSpaceScenePorts m_ScenePorts = new MixedSpaceScenePorts();
        private MixedSpaceRuntimeConfiguration m_InstanceSettings;
        private int? m_RuntimeFrameRate;
        private bool? m_RuntimeCaptureLimit;

        public MixedSpaceRunPlan RunPlan => m_RunPlan;
        // 必需的编辑期所有者；没有内嵌配置回退。
        // Required authoring owners. There is no embedded configuration fallback.
        private MixedSpaceApplicationBinding ApplicationBinding => m_ApplicationBinding != null
            ? m_ApplicationBinding : throw new InvalidOperationException("Assign an Application Binding to MixedSpaceSDK.");

        [SerializeField]
        [Tooltip("SDK 开发者令牌。\nSDK developer token")]
        private string m_DeveloperToken = "";
        [SerializeField] private string m_ServerMapLoad = MixedSpaceNetworkEnvironment.DefaultMapServer;
        [SerializeField] private string m_ServerAddressLocalize = MixedSpaceNetworkEnvironment.DefaultLocalizeServer;
        
        [SerializeField]
        [Tooltip("Production=0、Test=1；选择原生认证环境和默认 HTTP 端点。\nProduction=0, Test=1. Selects native authentication and default HTTP endpoints.")]
        private int m_XrLocServer = (int)MixedSpaceXrLocServer.Production;
        /// <summary>
        /// Inspector 当前选择的原生 xrloc 服务器环境。
        /// Native xrloc server profile selected on the Inspector.
        /// </summary>
        public int XrLocServer => m_XrLocServer;

        /// <summary>
        /// 用于验证及云请求的开发者令牌。
        /// Developer token used by validation and cloud requests.
        /// </summary>
        public string DeveloperToken
        {
            get => m_DeveloperToken;
            set => m_DeveloperToken = value ?? string.Empty;
        }

        #region Interfaced components

        public MixedSpaceRuntimeConfiguration RuntimeConfiguration
        {
            get
            {
                if (m_RunPlan == null)
                    throw new InvalidOperationException("Assign a Run Plan to MixedSpaceSDK.");
                if (m_InstanceSettings != null) return m_InstanceSettings;
                var settings = m_RunPlan.CreateRuntimeSettings();
                settings.CopyScenePortsFrom(m_ScenePorts);
                // Inspector 读取使用独立配置，只有运行中的实例缓存策略。
                // Inspector reads are detached; only a running instance caches its policies.
                if (Application.isPlaying)
                {
                    m_InstanceSettings = settings;
                    m_RuntimeFrameRate ??= m_RunPlan.FrameRate;
                    m_RuntimeCaptureLimit ??= m_RunPlan.LimitCapture;
                }
                return settings;
            }
        }

        public MixedSpaceApplicationSignals ApplicationSignals => ApplicationBinding.Signals;

        public IMixedSpaceSpatialSession SpatialSession => m_Composition?.SpatialSession;

        [SerializeField]
        [MixedSpaceComponentContract(typeof(IMixedSpacePlatformAdapter))]
        private Object m_PlatformAdapter;

        public IMixedSpacePlatformAdapter Platform => m_PlatformAdapter as IMixedSpacePlatformAdapter;

        public IMixedSpaceLocalizationCoordinator Localization =>
            m_Composition?.Localization;

        public IMixedSpaceAlignmentSolver AlignmentSolver => m_Composition?.AlignmentSolver;

        public IMixedSpaceTrackingService Tracking => m_Composition?.Tracking;

        /// <summary>
        /// 整个运行时的恢复边界；监督器串行执行命令并合并执行中的重复请求。
        /// Runtime-wide recovery boundary. Commands are serialized and duplicate
        /// in-flight requests are coalesced by the supervisor.
        /// </summary>
        public IMixedSpaceRuntimeControl RuntimeControl =>
            m_Coordinator?.RuntimeControl ?? throw new InvalidOperationException(
                "Runtime control is unavailable before the Unity Awake boundary.");

        #endregion Interfaced components

        public ITrackingStatus TrackingHealth => Tracking?.TrackingStatus;
        public bool IsOperational => m_Coordinator?.IsReady ?? false;
        public int EntitlementLevel => m_Coordinator?.LicenseLevel ?? -1;
        public bool HasEntitlementResult => m_Coordinator?.HasValidated ?? false;
        public MixedSpaceRuntimeState RuntimeState => Diagnostics.State;

        /// <summary>
        /// 由当前 SDK 实例持有的地图访问边界。
        /// Map access boundary owned by this SDK instance.
        /// </summary>
        public IMixedSpaceMapRepository Maps
            => Composition.Maps;

        public MixedSpaceMapProvisioner MapProvisioning
            => Composition.MapProvisioning;

        /// <summary>
        /// 用于应用层决策的平台身份；相机和位姿采集仍由序列化的平台适配器负责。
        /// Platform identity used by application-level decisions. Camera and pose
        /// acquisition remain behind the serialized IMixedSpacePlatformAdapter component.
        /// </summary>
        public IMixedSpacePlatformInfo PlatformInfo => Composition.PlatformInfo;

        /// <summary>
        /// SDK 当前使用的原生定位能力边界。
        /// Native localization boundary currently used by the SDK process.
        /// </summary>
        public IMixedSpaceNativeCore NativeCore => Composition.NativeCore;

        /// <summary>
        /// 创建独立场景位姿融合会话的工厂。
        /// Factory for independent scene pose-fusion sessions.
        /// </summary>
        public IMixedSpacePoseFusionFactory PoseFusionFactory =>
            Composition.PoseFusionFactory;

        /// <summary>
        /// 应用层云功能接口；应用无需构造可变 REST 任务对象。
        /// High-level cloud use cases. Applications depend on this boundary rather
        /// than constructing mutable REST job objects.
        /// </summary>
        public IMixedSpaceCloudWorkspace CloudWorkspace =>
            Composition.CloudWorkspace;

        /// <summary>
        /// SDK 生命周期使用的令牌、连接提示和端点环境。
        /// Token, connectivity and endpoint environment used by the SDK lifecycle.
        /// </summary>
        public IMixedSpaceNetworkEnvironment NetworkEnvironment =>
            Composition.NetworkEnvironment;

        /// <summary>
        /// 初始化前替换当前实例的地图仓库，供测试和新平台存储适配使用；启动后拒绝替换，以免活跃仓库仍持有原生地图。
        /// Replaces the map repository before initialization. This is the intended
        /// extension point for tests and future native platform storage adapters.
        /// Runtime replacement is rejected because registered native maps may still
        /// be owned by the active repository.
        /// </summary>
        /// <param name="repository">
        /// 当前 SDK 实例要使用的地图仓库。
        /// Repository to use for this SDK instance.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// 仓库为空。
        /// The repository is null.
        /// </exception>
        /// <exception cref="InvalidOperationException">
        /// 初始化已经开始。
        /// Initialization has started.
        /// </exception>
        public void SetMapRepository(IMixedSpaceMapRepository repository) =>
            InstallPortBeforeStart(
                repository,
                nameof(repository),
                "map repository",
                value => Composition.SetMapRepository(value));

        /// <summary>
        /// 初始化前指定平台身份，HarmonyOS 扩展可据此接入而无需修改核心程序集。
        /// Assigns platform identity before initialization. HarmonyOS packages use
        /// this hook to identify themselves without modifying the core SDK assembly.
        /// </summary>
        public void SetPlatformInfo(IMixedSpacePlatformInfo platformInfo) =>
            InstallPortBeforeStart(
                platformInfo,
                nameof(platformInfo),
                "platform information",
                value => Composition.SetPlatformInfo(value));

        /// <summary>
        /// 初始化前选择原生实现，平台扩展无需修改核心 SDK 即可安装桥接。
        /// Selects the native implementation before initialization. Platform
        /// packages use this hook to install their bridge without changing the core SDK.
        /// </summary>
        public void SetNativeCore(IMixedSpaceNativeCore nativeCore) =>
            InstallPortBeforeStart(
                nativeCore,
                nameof(nativeCore),
                "native core",
                value => Composition.SetNativeCore(value));

        /// <summary>
        /// 初始化前选择平台位姿融合实现。
        /// Selects the platform pose-fusion implementation before initialization.
        /// </summary>
        public void SetPoseFusionFactory(IMixedSpacePoseFusionFactory poseFusionFactory) =>
            InstallPortBeforeStart(
                poseFusionFactory,
                nameof(poseFusionFactory),
                "pose-fusion factory",
                value => Composition.SetPoseFusionFactory(value));

        /// <summary>
        /// 初始化前替换令牌和端点处理。
        /// Replaces token and endpoint handling before initialization.
        /// </summary>
        public void SetNetworkEnvironment(IMixedSpaceNetworkEnvironment networkEnvironment) =>
            InstallPortBeforeStart(
                networkEnvironment,
                nameof(networkEnvironment),
                "network environment",
                value => Composition.SetNetworkEnvironment(value));

        /// <summary>
        /// 初始化前替换 HTTP 流传输；注入实现 IDisposable 的传输会在 SDK 销毁时被释放。
        /// Replaces streaming HTTP before initialization. The SDK disposes an
        /// injected transport on destruction when it implements IDisposable.
        /// </summary>
        public void SetHttpTransport(IMixedSpaceHttpTransport httpTransport) =>
            InstallPortBeforeStart(
                httpTransport,
                nameof(httpTransport),
                "HTTP transport",
                value => Composition.SetHttpTransport(value));

        /// <summary>
        /// 初始化前替换强类型云客户端。
        /// Replaces the typed cloud client before initialization.
        /// </summary>
        public void SetCloudClient(IMixedSpaceCloudClient cloudClient) =>
            InstallPortBeforeStart(
                cloudClient,
                nameof(cloudClient),
                "cloud client",
                value => Composition.SetCloudClient(value));

        /// <summary>
        /// 运行时生命周期改变时触发的 C# 事件，与 UnityEvent 分离，使非 MonoBehaviour 客户端也能订阅。
        /// Raised whenever the runtime lifecycle changes. This C# event is kept
        /// separate from UnityEvents so non-MonoBehaviour clients can subscribe.
        /// </summary>
        public event Action<MixedSpaceRuntimeState> RuntimeStateChanged
        {
            add => Diagnostics.StateChanged += value;
            remove => Diagnostics.StateChanged -= value;
        }

        /// <summary>
        /// 报告运行时诊断错误，包括初始化失败及非致命的定位控制错误。
        /// 事件参数提供来源和异常，不能仅根据事件触发就断言整个运行时已失败；应结合 RuntimeState 判断。
        /// Reports initialization and runtime diagnostic errors, including nonfatal localization-control
        /// failures. Arguments provide source and exception; combine the event with RuntimeState rather than assuming
        /// every error means total runtime failure.
        /// </summary>
        public event Action<MixedSpaceRuntimeError> RuntimeErrorOccurred
        {
            add => Diagnostics.ErrorOccurred += value;
            remove => Diagnostics.ErrorOccurred -= value;
        }

        /// <summary>
        /// 生命周期状态变化后发布不可变快照，供原生 UI、遥测和诊断使用。
        /// Raised after lifecycle state changes. The immutable snapshot is suitable
        /// for native UI bridges, telemetry and diagnostics.
        /// </summary>
        public event Action<MixedSpaceRuntimeSnapshot> RuntimeSnapshotChanged
        {
            add => Diagnostics.SnapshotChanged += value;
            remove => Diagnostics.SnapshotChanged -= value;
        }

        public MixedSpaceRuntimeSnapshot GetRuntimeSnapshot()
        {
            return Diagnostics.Capture();
        }

        public int TargetFrameRate
        {
            get { _ = RuntimeConfiguration; return m_RuntimeFrameRate ?? m_RunPlan.FrameRate; }
            set
            {
                _ = RuntimeConfiguration;
                m_RuntimeFrameRate = value;
                ApplyFrameRatePreference();
            }
        }

        public bool UseCaptureResolutionLimit
        {
            get { _ = RuntimeConfiguration; return m_RuntimeCaptureLimit ?? m_RunPlan.LimitCapture; }
            set
            {
                _ = RuntimeConfiguration;
                m_RuntimeCaptureLimit = value;
                ApplyImageBudgetPreference();
            }
        }

        public string CacheDirectory
        {
            get { return NetworkEnvironment.DownloadDirectory; }
        }

        private void ApplyImageBudgetPreference()
        {
            if (UseCaptureResolutionLimit)
            {
                NativeCore.Runtime.ApplyImagePixelBudget(960 * 720);
            }
            else
            {
                NativeCore.Runtime.ApplyImagePixelBudget(0);
            }
        }

        private void CloseManualCycleGate()
        {
            if (SpatialSession != null)
            {
                _ = SpatialSession.DispatchAsync(
                    MixedSpaceSessionCommand.BlockLocalization);
            }
        }

        /// <summary>
        /// 分阶段运行时启动完成后触发。
        /// Raised after the runtime has completed staged startup.
        /// </summary>
        public UnityEvent RuntimeReady
        {
            get => ApplicationBinding.Ready;
            set => ApplicationBinding.Ready = value;
        }

        /// <summary>
        /// 当前许可检查完成后触发。
        /// Raised after the current entitlement check has completed.
        /// </summary>
        public UnityEvent EntitlementResolved
        {
            get => ApplicationBinding.Entitlement;
            set => ApplicationBinding.Entitlement = value;
        }

        /// <summary>
        /// 运行时重建清理后、重新开始验证前触发；不代表重新初始化已完成，就绪应监听 RuntimeReady。
        /// Raised after rebuild cleanup and before fresh validation; initialization is not complete.
        /// Listen to RuntimeReady for readiness.
        /// </summary>
        public UnityEvent RuntimeRebuilt
        {
            get => ApplicationBinding.Rebuilt;
            set => ApplicationBinding.Rebuilt = value;
        }

        private MixedSpaceRuntimeComposition m_Composition;
        private MixedSpaceRuntimeCoordinator m_Coordinator;
        private MixedSpaceRuntimeDiagnostics m_Diagnostics;
        private bool m_IsPrimaryInstance;

        private bool InitializationStarted =>
            IsOperational || (m_Coordinator?.InitializationRunning ?? false);

        private void InstallPortBeforeStart<T>(
            T implementation,
            string parameterName,
            string role,
            Action<T> install) where T : class
        {
            if (implementation == null)
                throw new ArgumentNullException(parameterName);
            if (InitializationStarted)
                throw new InvalidOperationException(
                    $"The {role} must be assigned before MixedSpaceSDK initialization starts.");
            install(implementation);
        }

        private MixedSpaceRuntimeComposition Composition =>
            m_Composition ??= new MixedSpaceRuntimeComposition(
                RuntimeConfiguration,
                ApplicationSignals,
                Platform,
                () => DeveloperToken,
                () => IsOperational,
                CloseManualCycleGate,
                  HandlePlatformTrackingLost,
                  () => m_ServerMapLoad,
                  () => m_ServerAddressLocalize,
                  () => (int)m_XrLocServer);

        private MixedSpaceRuntimeDiagnostics Diagnostics =>
            m_Diagnostics ??= new MixedSpaceRuntimeDiagnostics(
                () => m_Composition?.ExistingMapRepository,
                () => m_Composition?.ExistingPlatformInfo,
                () => IsOperational,
                () => HasEntitlementResult,
                () => EntitlementLevel,
                () => SpatialSession != null,
                () => Platform != null,
                () => Localization != null,
                () => AlignmentSolver != null,
                () => Tracking != null);

        // 进程原生资源仍需单一活动所有者；这是生命周期保护，不是供应用使用的全局服务定位器。
        // Process-native resources still require a single active owner. This is a lifetime guard, not a public service
        // locator for application code.
        private static MixedSpaceSDK s_Current;

        /// <summary>
        /// 激活纯 C# 服务组装，并安装应用层启动和恢复用例。
        /// Activates the pure C# composition and installs application-level
        /// startup/recovery use cases around it.
        /// </summary>
        private void CreateRuntimeCoordinator()
        {
            if (m_Coordinator != null)
                return;

            m_Coordinator = new MixedSpaceRuntimeCoordinator(
                Composition,
                () => DeveloperToken,
                () => FindObjectsOfType<MixedSpaceMapBinding>(),
                GetRuntimeSnapshot,
                SetRuntimeState,
                ReportRuntimeError,
                ApplyFrameRatePreference,
                ApplyImageBudgetPreference,
                () => RuntimeReady?.Invoke(),
                () => EntitlementResolved?.Invoke(),
                () => RuntimeRebuilt?.Invoke(),
                PublishRuntimeSnapshot,
                m_RunPlan.StartAutomatically,
                m_RunPlan.Diagnostics);
        }

        private void HandlePlatformTrackingLost()
        {
            MixedSpaceCallbackGuard.Invoke(
                ApplicationSignals.PlatformPoseUnavailable,
                nameof(ApplicationSignals.PlatformPoseUnavailable));
            DispatchRuntimeCommandSafely(MixedSpaceRuntimeCommand.RecoverTracking);
        }

        private void Awake()
        {
            if (s_Current == null)
            {
                s_Current = this;
            }

            if (s_Current != this)
            {
                MixedSpaceLogger.LogError("There must be only one MixedSpaceSDK object in a scene.");
                UnityEngine.Object.DestroyImmediate(this);
                return;
            }

            m_IsPrimaryInstance = true;
            try
            {
                CreateRuntimeCoordinator();
                m_Coordinator.Begin();
                // Begin 在自动初始化前调用配置的验证边界。
                // Begin invokes the configured validation boundary before automatic initialization.
            }
            catch (Exception e)
            {
                FailRuntime(e, "NetworkEnvironment");
            }
        }

        private void OnEnable()
        {
            // 缺少所有者时跳过 UI 绑定，错误由 Awake 报告。
            // Awake reports missing owners. Do not create a second UI binding path.
            m_ApplicationBinding?.Attach(this);
        }

        private void OnDisable()
        {
            m_ApplicationBinding?.Detach();
        }

        private void Update()
        {
            m_Coordinator?.Tick();
        }

        private void OnApplicationPause(bool paused)
        {
            m_Coordinator?.OnApplicationPause(paused);
        }

        /// <summary>
        /// 初始化 SDK 运行时。并发调用复用同一初始化任务，避免重复加载地图和创建网络客户端。
        /// 不等同于单独开始定位；初始化完成后是否自动定位由运行时 Session.StartWhenReady 决定，
        /// 该设置从 RunPlan 的调度配置复制。
        /// Initializes the SDK runtime and shares an in-flight initialization task to avoid duplicate map
        /// loads or clients. This is not a separate localization-start command. Session.StartWhenReady, copied from
        /// RunPlan scheduling, controls automatic localization after readiness.
        /// </summary>
        public Task StartRuntimeAsync()
        {
            if (m_Coordinator == null)
                return Task.FromException(new InvalidOperationException(
                    "MixedSpace runtime has not reached its Awake boundary."));
            return m_Coordinator.InitializeAsync();
        }

        /// <summary>
        /// 开始定位或恢复暂停的会话；必须等待 RuntimeReady，不清零统计。可 await，异常交由调用方处理。
        /// Starts or resumes localization after RuntimeReady without clearing statistics. Awaitable;
        /// exceptions belong to the caller.
        /// </summary>
        public Task StartLocalizationAsync() => DispatchLocalizationAsync(MixedSpaceSessionCommand.Begin);

        /// <summary>
        /// 取消并等待当前定位周期退出，停止后续调度；不卸载地图或关闭相机。必须在 SDK 就绪后调用。
        /// Cancels and awaits the active cycle, then stops scheduling without unloading maps or closing
        /// the camera. Requires SDK readiness.
        /// </summary>
        public Task StopLocalizationAsync() => DispatchLocalizationAsync(MixedSpaceSessionCommand.Stop);

        /// <summary>
        /// 取消并等待当前定位周期退出，再清空会话结果；不清零累计统计，也不重建整个 SDK。
        /// 是否恢复定位由运行时 Session.ResumeAfterReinitialize 决定，该设置从 RunPlan 的调度配置复制。
        /// 必须等待 RuntimeReady；返回的任务完成时重置流程已结束，异常交由调用方处理。
        /// Cancels and drains the active cycle, then clears session results without clearing cumulative
        /// statistics or rebuilding the SDK. Session.ResumeAfterReinitialize, copied from RunPlan, controls resumption.
        /// Requires RuntimeReady; task completion means reset finished and failures propagate to the caller.
        /// </summary>
        public Task ResetLocalizationAsync() => DispatchLocalizationAsync(MixedSpaceSessionCommand.Reset);

        private readonly System.Threading.SemaphoreSlim m_LocalizationCommands = new System.Threading.SemaphoreSlim(1, 1);

        private async Task DispatchLocalizationAsync(MixedSpaceSessionCommand command)
        {
            await m_LocalizationCommands.WaitAsync();
            try
            {
                if (!IsOperational || SpatialSession == null)
                    throw new InvalidOperationException("Wait for RuntimeReady before controlling localization.");
                await SpatialSession.DispatchAsync(command);
            }
            finally { m_LocalizationCommands.Release(); }
        }

        /// <summary>
        /// 开始定位的按钮/UnityEvent 入口。异步执行，失败通过 RuntimeErrorOccurred 报告；需等待 RuntimeReady。
        /// Button/UnityEvent entry for asynchronous localization start. Requires RuntimeReady; failures
        /// are reported through RuntimeErrorOccurred.
        /// </summary>
        public void StartLocalization() => ExecuteLocalizationCommand(MixedSpaceSessionCommand.Begin);
        /// <summary>
        /// 停止定位的按钮/UnityEvent 入口。异步执行，不卸载地图或关闭相机；失败通过运行时诊断报告。
        /// Button/UnityEvent entry for stopping localization without unloading maps or closing the camera;
        /// failures go to runtime diagnostics.
        /// </summary>
        public void StopLocalization() => ExecuteLocalizationCommand(MixedSpaceSessionCommand.Stop);
        /// <summary>
        /// 重置定位的按钮/UnityEvent 入口。完成后触发 LocalizationResetCompleted，累计统计不清零。
        /// Button/UnityEvent reset entry. Completion raises LocalizationResetCompleted without clearing
        /// cumulative statistics.
        /// </summary>
        public void ResetLocalization() => ExecuteLocalizationCommand(MixedSpaceSessionCommand.Reset);

        private async void ExecuteLocalizationCommand(MixedSpaceSessionCommand command)
        {
            try { await DispatchLocalizationAsync(command); }
            catch (Exception error) { ReportRuntimeError(error, "LocalizationControl", false); }
        }

        /// <summary>
        /// 通过平台许可能力或云端服务刷新许可等级，完成后触发 EntitlementResolved。
        /// 此方法不是开始定位操作，也不是单次定位成功通知。
        /// Refreshes the license level using platform or cloud capabilities and raises
        /// EntitlementResolved. This neither starts localization nor reports a successful fix.
        /// </summary>
        public Task RefreshLicenseAsync()
        {
            if (m_Coordinator == null)
                return Task.FromException(new InvalidOperationException(
                    "MixedSpace runtime has not reached its Awake boundary."));
            return m_Coordinator.RefreshLicenseAsync();
        }

        private void ApplyFrameRatePreference()
        {
            Application.targetFrameRate = TargetFrameRate;
        }

        private void FailRuntime(Exception e, string source = "")
        {
            m_Coordinator?.MarkUnavailable();
            ReportRuntimeError(e, source, true);
        }

        private void ReportRuntimeError(Exception e, string component, bool critical)
        {
            Diagnostics.Report(e, component, critical);
        }

        private void SetRuntimeState(MixedSpaceRuntimeState state)
        {
            Diagnostics.TransitionTo(state);
        }

        private void PublishRuntimeSnapshot()
        {
            Diagnostics.PublishSnapshot();
        }

        private void OnDestroy()
        {
            // 销毁重复组件不能释放主实例的地图、HTTP 客户端或进程级原生资源。
            // Destroying a duplicate component must not tear down the primary SDK's maps, HTTP client or process-wide native
            // resources.
            if (!m_IsPrimaryInstance)
                return;

            SetRuntimeState(MixedSpaceRuntimeState.Stopping);
            m_ApplicationBinding?.Detach();
            m_Coordinator?.Dispose();

            SetRuntimeState(MixedSpaceRuntimeState.Stopped);
            m_Coordinator = null;
            m_Composition = null;
            m_Diagnostics = null;
            if (s_Current == this)
                s_Current = null;
        }

        [ContextMenu("Rebuild MixedSpace Runtime")]
        private void RebuildRuntimeFromContextMenu()
        {
            DispatchRuntimeCommandSafely(MixedSpaceRuntimeCommand.Rebuild);
        }

        private async void DispatchRuntimeCommandSafely(MixedSpaceRuntimeCommand command)
        {
            try
            {
                if (m_Coordinator == null)
                    return;
                await RuntimeControl.ExecuteAsync(
                    command,
                    m_Coordinator.LifetimeToken);
            }
            catch (Exception e)
            {
                if (e is OperationCanceledException &&
                    (m_Coordinator == null ||
                     m_Coordinator.IsCancellationRequested))
                    return;

                FailRuntime(e, $"Runtime command {command}");
            }
        }

    }
}
