using System;
using System.Threading;
using System.Threading.Tasks;
using MixedSpace.XR;
using UnityEngine;

namespace MixedSpace
{
    /// <summary>
    /// 管理应用级运行时边界，包括分阶段启动、监督、恢复串行化及生命周期取消，是 Unity 入口保留的单一操作对象。
    /// Owns the application-level runtime boundary: staged startup, supervision,
    /// recovery serialization and lifecycle cancellation. This is the single
    /// operational object retained by the Unity facade.
    /// </summary>
    internal sealed class MixedSpaceRuntimeCoordinator : IDisposable
    {
        private readonly MixedSpaceRuntimeComposition m_Composition;
        private readonly CancellationTokenSource m_Lifetime =
            new CancellationTokenSource();
        private readonly MixedSpaceRuntimeSupervisor m_Supervisor;
        private readonly MixedSpaceRuntimeControl m_Control;
        private bool m_Disposed;

        public MixedSpaceRuntimeCoordinator(
            MixedSpaceRuntimeComposition composition,
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
            bool initializeAutomatically,
            MixedSpaceLogger.LoggingLevel loggingLevel)
        {
            m_Composition = composition ?? throw new ArgumentNullException(nameof(composition));
            try
            {
                m_Composition.Compose();
                var sceneMaps = new MixedSpaceSceneMapImporter(
                    m_Composition.Maps,
                    m_Composition.Worlds,
                    m_Composition.MapPreviews);
                var startup = new MixedSpaceStartupCoordinator(
                    m_Composition.Platform,
                    m_Composition.Localization,
                    m_Composition.Maps,
                    m_Composition.NativeCore,
                    sceneMaps,
                    reportState);

                m_Supervisor = new MixedSpaceRuntimeSupervisor(
                    m_Composition,
                    startup,
                    () => m_Control,
                    developerToken,
                    discoverSceneMaps,
                    snapshot,
                    reportState,
                    reportError,
                    applyFrameRate,
                    applyDownsampling,
                    initializationCompleted,
                    validationCompleted,
                    runtimeReset,
                    publishSnapshot,
                    m_Lifetime.Token,
                    SynchronizationContext.Current,
                    initializeAutomatically,
                    loggingLevel);

                var recovery = new MixedSpaceRuntimeRecovery(
                    m_Composition.SpatialSession,
                    m_Composition.Localization,
                    m_Composition.Platform,
                    m_Composition.Tracking,
                    m_Composition.MapProvisioning,
                    m_Composition.Worlds,
                    startup,
                    m_Lifetime.Token,
                    m_Supervisor.MarkUnavailable,
                    reportState,
                    m_Supervisor.RequestFreshValidationAfterRebuild);
                m_Control = new MixedSpaceRuntimeControl(recovery.ExecuteAsync);
            }
            catch
            {
                m_Lifetime.Dispose();
                throw;
            }
        }

        public bool IsReady => m_Supervisor.IsReady;
        public int LicenseLevel => m_Supervisor.LicenseLevel;
        public bool HasValidated => m_Supervisor.HasValidated;
        public bool InitializationRunning => m_Supervisor.InitializationRunning;
        public CancellationToken LifetimeToken => m_Lifetime.Token;
        public bool IsCancellationRequested => m_Lifetime.IsCancellationRequested;
        public IMixedSpaceRuntimeControl RuntimeControl => m_Control;

        public void Begin()
        {
            ThrowIfDisposed();
            if (!Application.isEditor && m_Composition.PlatformInfo.IsMobile)
                m_Composition.NetworkEnvironment.ConfigureMobilePlatform(true);
            m_Supervisor.BeginValidation();
        }

        public Task InitializeAsync()
        {
            ThrowIfDisposed();
            return m_Supervisor.InitializeAsync();
        }

        public Task RefreshLicenseAsync()
        {
            ThrowIfDisposed();
            return m_Supervisor.RefreshLicenseAsync();
        }

        public void Tick()
        {
            if (!m_Disposed)
                m_Composition.Runtime?.Tick();
        }

        public void OnApplicationPause(bool paused)
        {
            if (!m_Disposed)
                m_Composition.Runtime?.OnApplicationPause(paused);
        }

        public void MarkUnavailable()
        {
            if (!m_Disposed)
                m_Supervisor.MarkUnavailable();
        }

        public void Dispose()
        {
            if (m_Disposed)
                return;
            m_Disposed = true;
            m_Supervisor.MarkUnavailable();
            m_Lifetime.Cancel();
            m_Control.Dispose();
            m_Composition.Dispose();
            m_Lifetime.Dispose();
        }

        private void ThrowIfDisposed()
        {
            if (m_Disposed)
                throw new ObjectDisposedException(nameof(MixedSpaceRuntimeCoordinator));
        }
    }
}
