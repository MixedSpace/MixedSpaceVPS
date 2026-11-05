using System;
using MixedSpace.XR;

namespace MixedSpace
{
    /// <summary>
    /// 持有单 SDK 实例的平台无关服务图，统一接收 Unity 生命周期信号。
    /// Owns the platform-neutral service graph for one SDK instance and receives Unity lifecycle signals.
    /// </summary>
    public sealed class MixedSpaceRuntime : IDisposable
    {
        private readonly MixedSpaceSessionController m_Session;
        private readonly MixedSpaceTrackingPolicy m_Tracking;
        private readonly MixedSpaceWorldRegistry m_Worlds;
        private bool m_Disposed;

        public MixedSpaceRuntime(
            MixedSpaceSessionController session,
            MixedSpaceLocalizationEngine localization,
            IMixedSpaceAlignmentSolver alignmentSolver,
            MixedSpaceTrackingPolicy tracking,
            MixedSpaceWorldRegistry worlds)
        {
            m_Session = session ?? throw new ArgumentNullException(nameof(session));
            Localization = localization ?? throw new ArgumentNullException(nameof(localization));
            AlignmentSolver = alignmentSolver ?? throw new ArgumentNullException(nameof(alignmentSolver));
            m_Tracking = tracking ?? throw new ArgumentNullException(nameof(tracking));
            m_Worlds = worlds ?? throw new ArgumentNullException(nameof(worlds));
        }

        public IMixedSpaceSpatialSession SpatialSession => m_Session;
        public IMixedSpaceLocalizationCoordinator Localization { get; }
        public IMixedSpaceAlignmentSolver AlignmentSolver { get; }
        public IMixedSpaceTrackingService Tracking => m_Tracking;
        public MixedSpaceWorldRegistry Worlds => m_Worlds;

        public void Tick()
        {
            if (m_Disposed)
                return;
            m_Worlds.Tick();
            m_Tracking.Tick();
            m_Session.Tick();
        }

        public void OnSdkReady()
        {
            if (!m_Disposed)
                m_Session.OnSdkReady();
        }

        public void OnApplicationPause(bool paused)
        {
            if (!m_Disposed)
                m_Session.OnApplicationPause(paused);
        }

        public void Dispose()
        {
            if (m_Disposed)
                return;
            m_Disposed = true;
            m_Session.Dispose();
            m_Worlds.Dispose();
        }
    }
}
