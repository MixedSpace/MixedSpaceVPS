using System;

namespace MixedSpace.XR
{
    /// <summary>
    /// 协调实例级结果计数、平台可用性边沿、时间稳定性及回调派送；不保留提供器结果，也没有 Unity 生命周期方法。
    /// Coordinates four instance-owned roles: outcome accounting, platform
    /// availability edges, temporal stability and callback delivery. It retains
    /// no provider results and has no Unity lifecycle method.
    /// </summary>
    public sealed class MixedSpaceTrackingPolicy : IMixedSpaceTrackingService
    {
        private readonly IMixedSpaceRuntimeClock m_Clock;
        private readonly MixedSpaceTrackingLedger m_Ledger;
        private readonly MixedSpaceTrackingStability m_Stability;
        private readonly MixedSpacePlatformAvailability m_PlatformAvailability;
        private readonly MixedSpaceTrackingNotifications m_Notifications;
        private readonly MixedSpaceTrackingStatusView m_Status =
            new MixedSpaceTrackingStatusView();
        private long m_Revision;

        public MixedSpaceTrackingPolicy(
            MixedSpaceTrackingOptions options,
            IMixedSpaceRuntimeClock clock,
            Action onPlatformTrackingLost = null,
            Action<bool> onTrackingWell = null,
            Action<bool> onTrackingLost = null)
        {
            if (options == null)
                throw new ArgumentNullException(nameof(options));
            m_Clock = clock ?? throw new ArgumentNullException(nameof(clock));
            m_Ledger = new MixedSpaceTrackingLedger(options.CountProvidersSeparately);
            m_Stability = new MixedSpaceTrackingStability(options.PoseRetentionSeconds);
            m_PlatformAvailability = new MixedSpacePlatformAvailability();
            m_Notifications = new MixedSpaceTrackingNotifications(
                onPlatformTrackingLost,
                onTrackingWell,
                onTrackingLost);
            PublishStatus();
        }

        public ITrackingStatus TrackingStatus => m_Status;

        public void Ingest(MixedSpaceTrackingSignal signal)
        {
            if (signal == null)
                throw new ArgumentNullException(nameof(signal));

            // 保留事件顺序：平台丢失回调先执行，随后才发布本帧定位计数。
            // Preserve event ordering: platform-loss callbacks run before this frame's localization counters become visible.
            if (m_PlatformAvailability.Observe(signal.Platform.Quality))
                m_Notifications.PlatformUnavailable();

            m_Ledger.Record(signal);
            m_Revision++;
            PublishStatus();
        }

        public void Tick()
        {
            if (!m_Ledger.HasAttempt)
                return;

            int successfulObservations = m_Ledger.PendingSuccessfulObservations;
            MixedSpaceTrackingChange change = m_Stability.Advance(
                true,
                successfulObservations,
                m_Clock.Time);
            m_Ledger.AcknowledgeSuccessfulObservations();

            // 事件观察到的是上次发布的质量；若回调重入 Ingest/Reset，不覆盖它的新状态。
            // Existing event callbacks observe the previous published quality. If one reenters Ingest/Reset, do not
            // overwrite its newer status.
            long evaluatedRevision = m_Revision;
            m_Notifications.Publish(change);
            if (evaluatedRevision == m_Revision)
                PublishStatus();
        }

        public void Reset()
        {
            MixedSpaceLogger.Log("Resetting MixedSpace tracking policy");
            m_Ledger.Clear();
            m_Stability.Clear();
            m_PlatformAvailability.Clear();
            m_Notifications.Clear();
            m_Revision++;
            PublishStatus();
        }

        private void PublishStatus()
        {
            m_Status.Publish(m_Ledger.Totals, m_Stability.Score);
        }
    }

}
