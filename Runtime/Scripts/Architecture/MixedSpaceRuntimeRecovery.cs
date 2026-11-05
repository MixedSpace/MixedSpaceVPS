using System;
using System.Threading;
using System.Threading.Tasks;
using MixedSpace.XR;

namespace MixedSpace
{
    /// <summary>
    /// 将地图、定位、跟踪和平台资源的恢复作为单个事务执行。
    /// Executes recovery of map, localization, tracking and platform resources as one transaction.
    /// </summary>
    internal sealed class MixedSpaceRuntimeRecovery
    {
        private readonly IMixedSpaceSpatialSession m_Session;
        private readonly IMixedSpaceLocalizationCoordinator m_Localization;
        private readonly IMixedSpacePlatformAdapter m_Platform;
        private readonly IMixedSpaceTrackingService m_Tracking;
        private readonly MixedSpaceMapProvisioner m_Maps;
        private readonly MixedSpaceWorldRegistry m_Worlds;
        private readonly MixedSpaceStartupCoordinator m_Startup;
        private readonly CancellationToken m_LifecycleToken;
        private readonly Action m_MarkUnavailable;
        private readonly Action<MixedSpaceRuntimeState> m_ReportState;
        private readonly Action m_RequestFreshValidation;

        public MixedSpaceRuntimeRecovery(
            IMixedSpaceSpatialSession session,
            IMixedSpaceLocalizationCoordinator localization,
            IMixedSpacePlatformAdapter platform,
            IMixedSpaceTrackingService tracking,
            MixedSpaceMapProvisioner maps,
            MixedSpaceWorldRegistry worlds,
            MixedSpaceStartupCoordinator startup,
            CancellationToken lifecycleToken,
            Action markUnavailable,
            Action<MixedSpaceRuntimeState> reportState,
            Action requestFreshValidation)
        {
            m_Session = session ?? throw new ArgumentNullException(nameof(session));
            m_Localization = localization ?? throw new ArgumentNullException(nameof(localization));
            m_Platform = platform ?? throw new ArgumentNullException(nameof(platform));
            m_Tracking = tracking ?? throw new ArgumentNullException(nameof(tracking));
            m_Maps = maps ?? throw new ArgumentNullException(nameof(maps));
            m_Worlds = worlds ?? throw new ArgumentNullException(nameof(worlds));
            m_Startup = startup ?? throw new ArgumentNullException(nameof(startup));
            m_LifecycleToken = lifecycleToken;
            m_MarkUnavailable = markUnavailable ?? throw new ArgumentNullException(nameof(markUnavailable));
            m_ReportState = reportState ?? throw new ArgumentNullException(nameof(reportState));
            m_RequestFreshValidation = requestFreshValidation ??
                throw new ArgumentNullException(nameof(requestFreshValidation));
        }

        public async Task ExecuteAsync(
            MixedSpaceRuntimeCommand command,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            m_LifecycleToken.ThrowIfCancellationRequested();
            switch (command)
            {
                case MixedSpaceRuntimeCommand.RecoverTracking:
                    // 重置可能恢复调度；清理前先等待旧周期退出，所有依赖状态清理完成后才能重置或恢复。
                    // Reset may resume scheduling. Drain the old cycle before clearing, and only reset/resume after every dependent
                    // state is cleared.
                    await m_Session.DispatchAsync(MixedSpaceSessionCommand.Stop);
                    cancellationToken.ThrowIfCancellationRequested();
                    m_LifecycleToken.ThrowIfCancellationRequested();
                    await ClearWorldsAsync(cancellationToken);
                    m_Tracking.Reset();
                    cancellationToken.ThrowIfCancellationRequested();
                    m_LifecycleToken.ThrowIfCancellationRequested();
                    await m_Session.DispatchAsync(MixedSpaceSessionCommand.Reset);
                    return;
                case MixedSpaceRuntimeCommand.ClearWorld:
                    await ClearWorldsAsync(cancellationToken);
                    return;
                case MixedSpaceRuntimeCommand.Rebuild:
                    await RebuildAsync(cancellationToken);
                    return;
                default:
                    throw new ArgumentOutOfRangeException(nameof(command), command, null);
            }
        }

        private async Task RebuildAsync(CancellationToken cancellationToken)
        {
            MixedSpaceLogger.Log(
                "Rebuilding MixedSpace runtime",
                MixedSpaceLogger.LoggingLevel.Verbose);
            m_ReportState(MixedSpaceRuntimeState.Resetting);
            m_MarkUnavailable();
            cancellationToken.ThrowIfCancellationRequested();
            m_LifecycleToken.ThrowIfCancellationRequested();

            await m_Session.DispatchAsync(MixedSpaceSessionCommand.Stop);
            await m_Maps.ClearAsync();
            m_Startup.Invalidate();
            await m_Localization.ShutdownAsync();
            await m_Platform.ShutdownAsync();
            m_LifecycleToken.ThrowIfCancellationRequested();
            await ClearWorldsAsync(cancellationToken);
            m_RequestFreshValidation();
        }

        private async Task ClearWorldsAsync(CancellationToken cancellationToken)
        {
            MixedSpaceLogger.Log("Clearing MixedSpace world channels");
            foreach (IMixedSpaceWorldChannel world in m_Worlds.ActiveChannels)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await world.ClearAsync();
            }
        }
    }
}
