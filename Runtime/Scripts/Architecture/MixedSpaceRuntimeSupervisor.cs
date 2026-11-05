using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using MixedSpace.XR;
using UnityEngine;

namespace MixedSpace
{
    /// <summary>
    /// 监督单运行时的验证、初始化及许可刷新，管理异步重入和部分启动规则；Unity 入口仅转发生命周期意图。
    /// Supervises validation, initialization and entitlement refresh for one
    /// composed runtime. It owns asynchronous re-entry and partial-start rules;
    /// the Unity facade only forwards lifecycle intent.
    /// </summary>
    internal sealed class MixedSpaceRuntimeSupervisor
    {
        private readonly MixedSpaceRuntimeComposition m_Composition;
        private readonly MixedSpaceStartupCoordinator m_Startup;
        private readonly Func<MixedSpaceRuntimeControl> m_RuntimeControl;
        private readonly Func<string> m_DeveloperToken;
        private readonly Func<MixedSpaceMapBinding[]> m_DiscoverSceneMaps;
        private readonly Func<MixedSpaceRuntimeSnapshot> m_Snapshot;
        private readonly Action<MixedSpaceRuntimeState> m_ReportState;
        private readonly Action<Exception, string, bool> m_ReportError;
        private readonly Action m_ApplyFrameRate;
        private readonly Action m_ApplyDownsampling;
        private readonly Action m_InitializationCompleted;
        private readonly Action m_ValidationCompleted;
        private readonly Action m_RuntimeReset;
        private readonly Action m_PublishSnapshot;
        private readonly CancellationToken m_LifecycleToken;
        private readonly SynchronizationContext m_UnityContext;
        private readonly bool m_InitializeAutomatically;
        private readonly MixedSpaceLogger.LoggingLevel m_LoggingLevel;
        private Task m_InitializationTask;
        private LogCallback m_LogCallback;
        private bool m_PluginCallbackRegistered;

        public MixedSpaceRuntimeSupervisor(
            MixedSpaceRuntimeComposition composition,
            MixedSpaceStartupCoordinator startup,
            Func<MixedSpaceRuntimeControl> runtimeControl,
            Func<string> developerToken,
            Func<MixedSpaceMapBinding[]> discoverSceneMaps,
            Func<MixedSpaceRuntimeSnapshot> snapshot,
            Action<MixedSpaceRuntimeState> reportState,
            Action<Exception, string, bool> reportError,
            Action applyFrameRate,
            Action applyDownsampling,
            Action initializationCompleted,
            Action validationCompleted,
            Action runtimeReset,
            Action publishSnapshot,
            CancellationToken lifecycleToken,
            SynchronizationContext unityContext,
            bool initializeAutomatically,
            MixedSpaceLogger.LoggingLevel loggingLevel)
        {
            m_Composition = composition ?? throw new ArgumentNullException(nameof(composition));
            m_Startup = startup ?? throw new ArgumentNullException(nameof(startup));
            m_RuntimeControl = runtimeControl ?? throw new ArgumentNullException(nameof(runtimeControl));
            m_DeveloperToken = developerToken ?? throw new ArgumentNullException(nameof(developerToken));
            m_DiscoverSceneMaps = discoverSceneMaps ?? throw new ArgumentNullException(nameof(discoverSceneMaps));
            m_Snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
            m_ReportState = reportState ?? throw new ArgumentNullException(nameof(reportState));
            m_ReportError = reportError ?? throw new ArgumentNullException(nameof(reportError));
            m_ApplyFrameRate = applyFrameRate ?? throw new ArgumentNullException(nameof(applyFrameRate));
            m_ApplyDownsampling = applyDownsampling ?? throw new ArgumentNullException(nameof(applyDownsampling));
            m_InitializationCompleted = initializationCompleted;
            m_ValidationCompleted = validationCompleted;
            m_RuntimeReset = runtimeReset;
            m_PublishSnapshot = publishSnapshot;
            m_LifecycleToken = lifecycleToken;
            m_UnityContext = unityContext;
            m_InitializeAutomatically = initializeAutomatically;
            m_LoggingLevel = loggingLevel;
        }

        public bool IsReady { get; private set; }
        public int LicenseLevel { get; private set; } = -1;
        public bool HasValidated { get; private set; }
        public bool InitializationRunning =>
            m_InitializationTask is { IsCompleted: false };

        public async void BeginValidation()
        {
            m_ReportState(MixedSpaceRuntimeState.Validating);
            try
            {
                string token = m_DeveloperToken();
                await MixedSpaceAuthCache.InstallFromStreamingAssetsAsync(token, m_LifecycleToken);
                m_LifecycleToken.ThrowIfCancellationRequested();
                m_Composition.NetworkEnvironment.ValidateToken(token, DispatchValidationResult);
            }
            catch (OperationCanceledException) when (m_LifecycleToken.IsCancellationRequested) { }
            catch (Exception exception) { m_ReportError(exception, "Validation", true); }
        }

        public Task InitializeAsync()
        {
            if (IsReady)
                return Task.CompletedTask;

            MixedSpaceRuntimeControl control = m_RuntimeControl();
            if (control != null &&
                control.TryGetInFlight(
                    MixedSpaceRuntimeCommand.Rebuild,
                    out Task rebuildTask))
            {
                return InitializeAfterRebuildAsync(rebuildTask);
            }

            if (InitializationRunning)
                return m_InitializationTask;

            m_InitializationTask = InitializeCoreAsync();
            return m_InitializationTask;
        }

        public async Task RefreshLicenseAsync()
        {
            IMixedSpacePlatformInfo platform = m_Composition.PlatformInfo;
            string token = m_DeveloperToken();
            await MixedSpaceAuthCache.InstallFromStreamingAssetsAsync(token, m_LifecycleToken);
            m_LifecycleToken.ThrowIfCancellationRequested();
            if (platform.Kind == MixedSpacePlatformKind.IOS ||
                platform.Kind == MixedSpacePlatformKind.Android ||
                platform.Kind == MixedSpacePlatformKind.HarmonyOS)
            {
                LicenseLevel = m_Composition.NativeCore.Runtime
                    .ResolveLicenseLevel(token);
            }
            else
            {
                LicenseLevel = await m_Composition.CloudWorkspace
                    .GetLicenseLevelAsync(cancellationToken: m_LifecycleToken);
            }

            MixedSpaceLogger.Log(LicenseLevel >= 1 ?
                "Enterprise License" : "Free License");
            HasValidated = true;
            MixedSpaceCallbackGuard.Invoke(
                m_ValidationCompleted,
                "UserValidationComplete");
            m_PublishSnapshot?.Invoke();
        }

        public void MarkUnavailable()
        {
            IsReady = false;
        }

        public void RequestFreshValidationAfterRebuild()
        {
            MixedSpaceCallbackGuard.Invoke(m_RuntimeReset, "RuntimeReset");
            if (!Application.isEditor && m_Composition.PlatformInfo.IsMobile)
                m_Composition.NetworkEnvironment.ConfigureMobilePlatform(true);
            m_InitializationTask = null;
            BeginValidation();
        }

        private async Task InitializeAfterRebuildAsync(Task rebuildTask)
        {
            await rebuildTask;
            await InitializeAsync();
        }

        private async Task InitializeCoreAsync()
        {
            m_ReportState(MixedSpaceRuntimeState.Initializing);
            try
            {
                m_LifecycleToken.ThrowIfCancellationRequested();
                MixedSpaceLogger.Level = m_LoggingLevel;
                MixedSpaceLogger.Log(
                    "Initializing MixedSpace runtime",
                    MixedSpaceLogger.LoggingLevel.Verbose);
                MixedSpaceLogger.Log(
                    $"Runtime platform: {m_Composition.PlatformInfo.Kind} " +
                    $"({m_Composition.PlatformInfo.RuntimeName})",
                    MixedSpaceLogger.LoggingLevel.Verbose);

                EnsureRequiredServices(m_Snapshot());
                await MixedSpaceAuthCache.InstallFromStreamingAssetsAsync(m_DeveloperToken(), m_LifecycleToken);
                m_LifecycleToken.ThrowIfCancellationRequested();
                AttachNativeLogSink();
                ConfigureXrLocServer();
                m_Composition.PrepareHttpTransport();

                string token = m_DeveloperToken();
                if (!string.IsNullOrEmpty(token))
                {
                    PlayerPrefs.SetString("token", token);
                    _ = RefreshLicenseSafelyAsync();
                }

                m_ApplyFrameRate();
#if !UNITY_EDITOR
                m_ApplyDownsampling();
#endif
                IsReady = await m_Startup.StartAsync(
                    m_DiscoverSceneMaps(),
                    m_LifecycleToken);
                m_LifecycleToken.ThrowIfCancellationRequested();

                MixedSpaceLogger.Log($"Runtime initialization completed: IsReady={IsReady}", MixedSpaceLogger.LoggingLevel.Verbose);

                if (!IsReady)
                    throw new MixedSpaceRuntimeUnavailableException(
                        "Runtime service startup did not complete successfully.");

                MixedSpaceLogger.Log(
                    "MixedSpace runtime ready.",
                    MixedSpaceLogger.LoggingLevel.Verbose);
                m_ReportState(MixedSpaceRuntimeState.Ready);
                m_Composition.Runtime?.OnSdkReady();
                MixedSpaceCallbackGuard.Invoke(
                    m_InitializationCompleted,
                    "InitializationComplete");
            }
            catch (Exception exception)
            {
                if (exception is OperationCanceledException &&
                    m_LifecycleToken.IsCancellationRequested)
                {
                    return;
                }

                IsReady = false;
                m_ReportError(exception, "Initialization", true);
                throw;
            }
        }

        private void DispatchValidationResult(bool isValid)
        {
            if (m_UnityContext != null &&
                !ReferenceEquals(SynchronizationContext.Current, m_UnityContext))
            {
                m_UnityContext.Post(_ => CompleteValidationIfActive(isValid), null);
                return;
            }

            CompleteValidationIfActive(isValid);
        }

        private void CompleteValidationIfActive(bool isValid)
        {
            // 验证结果由当前实例处理；验证失败时中止初始化。
            // Handle validation for this instance; failed validation stops initialization.
            MixedSpaceLogger.Log(
                $"Token validation completed: isValid={isValid} " +
                $"initializeAutomatically={m_InitializeAutomatically} ", MixedSpaceLogger.LoggingLevel.Verbose);

            if (m_LifecycleToken.IsCancellationRequested)
                return;
            if (!isValid)
            {
                IsReady = false;
                m_ReportError(
                    new InvalidOperationException("MixedSpace token validation failed."),
                    "Validation",
                    true);
                return;
            }

            if (m_InitializeAutomatically)
                BeginAutomaticInitialization();
        }

        private async void BeginAutomaticInitialization()
        {
            try
            {
                await InitializeAsync();
            }
            catch
            {
                // InitializeCoreAsync 负责报告结构化错误，此处终止 async-void 异常传播。
                // InitializeCoreAsync already published a structured error. Do not rethrow an async-void exception into Unity's
                // context.
            }
        }

        private void AttachNativeLogSink()
        {
            if (m_PluginCallbackRegistered)
                return;

            m_LogCallback = MixedSpaceNativeLogBridge.Receive;
            IntPtr callback = Marshal.GetFunctionPointerForDelegate(m_LogCallback);
            m_Composition.NativeCore.Runtime.AttachLogSink(callback);
            m_PluginCallbackRegistered = true;
        }

        /// <summary>
        /// 启动时将 Inspector 中选择的服务器环境写入 xrloc（测试为 0，生产为 1）。
        /// 此处负责启动配置；MixedSpaceNativeAuthorization 也会在每次 xrloc 令牌校验前
        /// 重新应用当前选择，以覆盖编辑器中未启动 SDK 就执行校验的路径。
        /// Applies the Inspector server selection at startup: Test=0, Production=1.
        /// MixedSpaceNativeAuthorization reapplies the current selection before each xrloc token check, covering editor
        /// validation before SDK startup.
        /// </summary>
        private void ConfigureXrLocServer()
        {
            int serverStatus = MixedSpaceXrLocNative.SetServer(m_Composition.XrLocServerId);
            if (serverStatus != 0)
                MixedSpaceLogger.LogWarning($"XrLoc server selection rejected: status={serverStatus}");
        }

        private async Task RefreshLicenseSafelyAsync()
        {
            try
            {
                await RefreshLicenseAsync();
            }
            catch (Exception exception)
            {
                m_ReportError(exception, "Validation", false);
            }
        }

        private static void EnsureRequiredServices(MixedSpaceRuntimeSnapshot snapshot)
        {
            if (snapshot.HasRequiredComponents)
                return;

            var missing = new List<string>();
            if (!snapshot.HasSpatialSession) missing.Add("SpatialSession");
            if (!snapshot.HasPlatform) missing.Add("Platform");
            if (!snapshot.HasLocalization) missing.Add("Localization");
            if (!snapshot.HasAlignmentSolver) missing.Add("AlignmentSolver");
            if (!snapshot.HasTracking) missing.Add("Tracking");
            throw new MixedSpaceRuntimeUnavailableException(
                $"Required runtime services are unavailable: {string.Join(", ", missing)}.");
        }
    }

    /// <summary>
    /// 运行时服务无法就绪时抛出的异常。
    /// Signals that a runtime service could not become ready.
    /// </summary>
    public sealed class MixedSpaceRuntimeUnavailableException : Exception
    {
        public MixedSpaceRuntimeUnavailableException(string message) : base(message)
        {
        }
    }
}
