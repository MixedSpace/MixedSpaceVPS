using System;
using System.Threading;
using System.Threading.Tasks;

namespace MixedSpace.XR
{
    /// <summary>
    /// 负责定位会话的调度、挂起、停止和取消，由 SDK 运行时驱动 Tick 和应用暂停通知。
    /// 这是普通 C# 对象，不挂载到 GameObject；会话重置不等于 SDK 运行时重建。
    /// Schedules, suspends, stops and cancels localization sessions, driven by runtime Tick and pause
    /// notifications. This ordinary C# object is not attached to a GameObject; session reset differs from SDK
    /// rebuild.
    /// </summary>
    public sealed class MixedSpaceSessionController : IMixedSpaceSpatialSession, IDisposable
    {
        private readonly MixedSpaceSessionOptions m_Options;
        private readonly IMixedSpaceRuntimeClock m_Clock;
        private readonly Func<bool> m_IsSdkReady;
        private readonly IMixedSpaceMapRepository m_Maps;
        private readonly IMixedSpaceTrackingService m_Tracking;
        private readonly MixedSpaceLocalizationReadModel m_ReadModel;
        private readonly MixedSpaceLocalizationConnections m_ViewConnections;
        private readonly MixedSpaceLocalizationPipeline m_LocalizationPipeline;
        private readonly MixedSpaceLocalizationScheduler m_Scheduler = new MixedSpaceLocalizationScheduler();
        private readonly IMixedSpaceSessionObserver m_Observer;

        private bool m_RunningTasks;
        private bool m_Disposed;
        private Task m_CurrentRunningTask;
        private Task m_ResetTask;
        private Task m_StopTask;
        private CancellationTokenSource m_Cancellation;

        public MixedSpaceSessionController(
            MixedSpaceSessionOptions options,
            IMixedSpaceRuntimeClock clock,
            Func<bool> isSdkReady,
            IMixedSpacePlatformAdapter platform,
            IMixedSpaceLocalizationCoordinator localization,
            IMixedSpaceMapRepository maps,
            IMixedSpaceAlignmentSolver alignmentSolver,
            IMixedSpaceTrackingService tracking,
            IMixedSpaceLocalizationBinding[] views,
            IMixedSpaceLocalizationObserver localizationObserver,
            IMixedSpaceSessionObserver sessionObserver)
            : this(options, clock, isSdkReady, platform, localization, maps, alignmentSolver, tracking, views, localizationObserver, sessionObserver, false)
        {
        }

        public MixedSpaceSessionController(
            MixedSpaceSessionOptions options,
            IMixedSpaceRuntimeClock clock,
            Func<bool> isSdkReady,
            IMixedSpacePlatformAdapter platform,
            IMixedSpaceLocalizationCoordinator localization,
            IMixedSpaceMapRepository maps,
            IMixedSpaceAlignmentSolver alignmentSolver,
            IMixedSpaceTrackingService tracking,
            IMixedSpaceLocalizationBinding[] views,
            IMixedSpaceLocalizationObserver localizationObserver,
            IMixedSpaceSessionObserver sessionObserver, bool writePoseLogs)
        {
            m_Options = options ?? throw new ArgumentNullException(nameof(options));
            m_Clock = clock ?? throw new ArgumentNullException(nameof(clock));
            m_IsSdkReady = isSdkReady ?? throw new ArgumentNullException(nameof(isSdkReady));
            m_Maps = maps ?? throw new ArgumentNullException(nameof(maps));
            m_Tracking = tracking ?? throw new ArgumentNullException(nameof(tracking));
            m_Observer = sessionObserver ?? throw new ArgumentNullException(nameof(sessionObserver));
            m_ReadModel = new MixedSpaceLocalizationReadModel();
            m_LocalizationPipeline = new MixedSpaceLocalizationPipeline(
                platform ?? throw new ArgumentNullException(nameof(platform)),
                localization ?? throw new ArgumentNullException(nameof(localization)),
                maps,
                alignmentSolver ?? throw new ArgumentNullException(nameof(alignmentSolver)),
                tracking,
                m_ReadModel,
                localizationObserver, writePoseLogs);
            m_ViewConnections = new MixedSpaceLocalizationConnections(m_ReadModel, views);
        }

        public bool ManualLocalizationControlEnabled => m_Options.ManualCycleGateEnabled;
        public MixedSpaceSessionPhase Phase { get; private set; } = MixedSpaceSessionPhase.Stopped;
        public IMixedSpaceLocalizationReadModel Localizations => m_ReadModel;

        public void OnSdkReady()
        {
            if (m_Options.StartWhenReady)
                _ = DispatchAsync(MixedSpaceSessionCommand.Begin);
        }

        public void Tick()
        {
            if (m_Disposed)
                return;

            if (Phase != MixedSpaceSessionPhase.Running ||
                !m_IsSdkReady() ||
                !m_Maps.HasMaps)
            {
                return;
            }

            float currentTime = m_Clock.UnscaledTime;
            int successCount = m_Tracking.TrackingStatus?.LocalizationSuccessCount ?? 0;
            if (m_Scheduler.ShouldStopBurst(
                successCount,
                m_Options.BurstFixLimit,
                currentTime,
                m_Options.BurstDurationSeconds))
            {
                SetBurstMode(false);
            }

            if (!m_Scheduler.ShouldRun(
                currentTime,
                m_Options.CycleIntervalSeconds,
                m_RunningTasks,
                Phase == MixedSpaceSessionPhase.Suspended))
            {
                return;
            }

            m_Scheduler.MarkRunStarted(currentTime);
            m_RunningTasks = true;
            m_Cancellation?.Dispose();
            m_Cancellation = new CancellationTokenSource();
            m_CurrentRunningTask = RunTasksAsync(m_Options.PublishLocalizationState, m_Cancellation.Token);
        }

        public async Task DispatchAsync(MixedSpaceSessionCommand command)
        {
            ThrowIfDisposed();
            switch (command)
            {
                case MixedSpaceSessionCommand.Begin:
                    BeginSession(command);
                    break;
                case MixedSpaceSessionCommand.Suspend:
                    await SuspendSessionAsync(command);
                    break;
                case MixedSpaceSessionCommand.Continue:
                    ContinueSession(command);
                    break;
                case MixedSpaceSessionCommand.Reset:
                    await RequestResetAsync();
                    break;
                case MixedSpaceSessionCommand.Stop:
                    await RequestStopAsync();
                    break;
                case MixedSpaceSessionCommand.RunOneCycle:
                    await RunOneCycleAsync();
                    break;
                case MixedSpaceSessionCommand.AllowLocalization:
                    if (m_Options.ManualCycleGateEnabled)
                        ContinueSession(command);
                    break;
                case MixedSpaceSessionCommand.BlockLocalization:
                    if (m_Options.ManualCycleGateEnabled)
                        await SuspendSessionAsync(command);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(command), command, null);
            }
        }

        private void BeginSession(MixedSpaceSessionCommand command)
        {
            ThrowIfDisposed();
            if (Phase != MixedSpaceSessionPhase.Stopped &&
                Phase != MixedSpaceSessionPhase.Resetting)
            {
                if (Phase == MixedSpaceSessionPhase.Suspended)
                    ContinueSession(command);
                return;
            }

            if (!m_IsSdkReady())
                return;
            MixedSpaceLogger.Log("Session starting", MixedSpaceLogger.LoggingLevel.Verbose);
            SetBurstMode(m_Options.StartupBurstEnabled);
            TransitionTo(MixedSpaceSessionPhase.Running, command);
        }

        private async Task SuspendSessionAsync(MixedSpaceSessionCommand command)
        {
            if (Phase == MixedSpaceSessionPhase.Stopped)
                return;
            if (m_Options.ReinitializeOnSuspend)
                await RequestResetAsync();
            TransitionTo(MixedSpaceSessionPhase.Suspended, command);
        }

        private void ContinueSession(MixedSpaceSessionCommand command)
        {
            if (Phase == MixedSpaceSessionPhase.Stopped)
                return;
            SetBurstMode(m_Options.StartupBurstEnabled);
            TransitionTo(MixedSpaceSessionPhase.Running, command);
        }

        public void OnApplicationPause(bool paused)
        {
            _ = DispatchSafelyAsync(
                paused
                    ? MixedSpaceSessionCommand.Suspend
                    : MixedSpaceSessionCommand.Continue);
        }

        private async Task RunOneCycleAsync()
        {
            ThrowIfDisposed();
            if (m_CurrentRunningTask != null)
                await m_CurrentRunningTask;

            m_Scheduler.MarkRunStarted(m_Clock.UnscaledTime);
            m_RunningTasks = true;
            m_Cancellation?.Dispose();
            m_Cancellation = new CancellationTokenSource();
            m_CurrentRunningTask = RunTasksAsync(m_Options.PublishLocalizationState, m_Cancellation.Token);
            await m_CurrentRunningTask;
        }

        private Task RequestResetAsync()
        {
            ThrowIfDisposed();
            if (m_ResetTask is { IsCompleted: false })
                return m_ResetTask;
            m_ResetTask = ExecuteResetTransitionAsync();
            return m_ResetTask;
        }

        private Task RequestStopAsync(bool cancelRunningTask = true)
        {
            if (m_StopTask is { IsCompleted: false })
            {
                if (cancelRunningTask)
                    m_Cancellation?.Cancel();
                return m_StopTask;
            }

            m_StopTask = QuiesceAsync(cancelRunningTask);
            return m_StopTask;
        }

        public void Dispose()
        {
            if (m_Disposed)
                return;
            m_Disposed = true;
            m_Cancellation?.Cancel();
            m_Cancellation?.Dispose();
            m_Cancellation = null;
            m_ReadModel.Dispose();
            m_ViewConnections.Dispose();
        }

        private async Task RunTasksAsync(bool publishReadModel, CancellationToken cancellationToken)
        {
            try
            {
                await m_LocalizationPipeline.ExecuteAsync(publishReadModel, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                // 周期异常后主动停止，不能继续向融合阶段供给数据；记录完整异常，才能区分错误图像和标定不匹配。
                // Stopping is intentional: a cycle that throws must not keep feeding the fusion stage. Report the full
                // exception, because a one-line summary is not enough to tell a bad frame apart from a calibration mismatch.
                MixedSpaceLogger.LogError(
                    $"Session task error: {exception.GetType().Name}: {exception.Message}. Stopping session.\n{exception}");
                TransitionTo(MixedSpaceSessionPhase.Stopped, MixedSpaceSessionCommand.Stop);
            }
            finally
            {
                m_RunningTasks = false;
            }
        }

        private async Task ExecuteResetTransitionAsync()
        {
            MixedSpaceLogger.Log("Resetting session");
            TransitionTo(MixedSpaceSessionPhase.Resetting, MixedSpaceSessionCommand.Reset);
            await QuiesceAsync(true);
            if (m_Disposed) return;
            m_ReadModel.Clear();
            // 视图可能在处理空修订时释放整个运行时。
            // A view may dispose the runtime while handling the empty revision.
            if (m_Disposed) return;
            if (m_Options.ResumeAfterReinitialize)
                BeginSession(MixedSpaceSessionCommand.Reset);
            else
                TransitionTo(MixedSpaceSessionPhase.Stopped, MixedSpaceSessionCommand.Reset);
        }

        private async Task QuiesceAsync(bool cancelRunningTask)
        {
            if (cancelRunningTask)
                m_Cancellation?.Cancel();
            if (m_CurrentRunningTask != null)
                await m_CurrentRunningTask;

            m_Cancellation?.Dispose();
            m_Cancellation = null;
            m_CurrentRunningTask = null;
            m_RunningTasks = false;
            m_Scheduler.Reset();
            MixedSpaceLogger.Log("Session stopped");
            if (Phase != MixedSpaceSessionPhase.Resetting)
                TransitionTo(MixedSpaceSessionPhase.Stopped, MixedSpaceSessionCommand.Stop);
        }

        private async Task DispatchSafelyAsync(MixedSpaceSessionCommand command)
        {
            try
            {
                await DispatchAsync(command);
            }
            catch (Exception exception)
            {
                MixedSpaceLogger.LogError(
                    $"Session command {command} failed: {exception.Message}");
            }
        }

        private void SetBurstMode(bool enabled) => m_Scheduler.SetBurst(enabled, m_Clock.UnscaledTime);

        private void ThrowIfDisposed()
        {
            if (m_Disposed)
                throw new ObjectDisposedException(nameof(MixedSpaceSessionController));
        }

        private void TransitionTo(
            MixedSpaceSessionPhase phase,
            MixedSpaceSessionCommand command)
        {
            MixedSpaceSessionPhase previous = Phase;
            if (previous == phase && command != MixedSpaceSessionCommand.Reset)
                return;
            Phase = phase;
            try
            {
                m_Observer.Observe(new MixedSpaceSessionTransition(
                    previous,
                    phase,
                    command));
            }
            catch (Exception exception)
            {
                MixedSpaceLogger.LogError(
                    $"Session observer failed: {exception.Message}");
            }
        }
    }

}
