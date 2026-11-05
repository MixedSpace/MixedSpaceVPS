using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MixedSpace.XR;

namespace MixedSpace
{
    /// <summary>
    /// 一个强类型定位周期结果；UI 在应用边界适配该值，不直接订阅协调器内部回调。
    /// One typed localization-cycle outcome. UI frameworks adapt this value at
    /// the application boundary instead of subscribing to coordinator callbacks.
    /// </summary>
    public sealed class MixedSpaceLocalizationCycleReport
    {
        public MixedSpaceLocalizationBatch Batch { get; }
        public IReadOnlyList<int> SuccessfulMapKeys { get; }
        public bool HasSuccess => SuccessfulMapKeys.Count > 0;

        public MixedSpaceLocalizationCycleReport(MixedSpaceLocalizationBatch batch)
        {
            Batch = batch ?? throw new ArgumentNullException(nameof(batch));
            SuccessfulMapKeys = new ReadOnlyCollection<int>(batch.Outcomes
                .Where(outcome => outcome.Success && outcome.Observation != null)
                .Select(outcome => outcome.Observation.MapKey)
                .ToArray());
        }
    }

    public interface IMixedSpaceLocalizationObserver
    {
        void Observe(MixedSpaceLocalizationCycleReport report);
    }

    /// <summary>
    /// 执行一次平台无关定位周期。无 MonoBehaviour 生命周期，不依赖 ARFoundation、JNI 或 Objective-C++；平台实现契约，会话控制器决定调度时间。
    /// Executes one platform-neutral localization cycle.
    /// 
    /// This application service deliberately has no MonoBehaviour lifecycle and no
    /// dependency on ARFoundation, JNI or Objective-C++. Platform packages provide
    /// the existing interfaces, while MixedSpaceSessionController decides when a cycle runs.
    /// </summary>
    public sealed class MixedSpaceLocalizationPipeline
    {
        private readonly IMixedSpacePlatformAdapter m_PlatformAdapter;
        private readonly IMixedSpaceLocalizationCoordinator m_Localization;
        private readonly IMixedSpaceMapRepository m_Maps;
        private readonly IMixedSpaceAlignmentSolver m_AlignmentSolver;
        private readonly IMixedSpaceTrackingService m_Tracking;
        private readonly MixedSpaceLocalizationReadModel m_ReadModel;
        private readonly IMixedSpaceLocalizationObserver m_Observer;
        private readonly bool m_WritePoseLogs;

        public MixedSpaceLocalizationPipeline(
            IMixedSpacePlatformAdapter platformAdapter,
            IMixedSpaceLocalizationCoordinator localization,
            IMixedSpaceMapRepository maps,
            IMixedSpaceAlignmentSolver alignmentSolver,
            IMixedSpaceTrackingService tracking,
            MixedSpaceLocalizationReadModel readModel,
            IMixedSpaceLocalizationObserver observer)
            : this(platformAdapter, localization, maps, alignmentSolver, tracking, readModel, observer, false)
        {
        }

        public MixedSpaceLocalizationPipeline(
            IMixedSpacePlatformAdapter platformAdapter,
            IMixedSpaceLocalizationCoordinator localization,
            IMixedSpaceMapRepository maps,
            IMixedSpaceAlignmentSolver alignmentSolver,
            IMixedSpaceTrackingService tracking,
            MixedSpaceLocalizationReadModel readModel,
            IMixedSpaceLocalizationObserver observer, bool writePoseLogs)
        {
            m_PlatformAdapter = platformAdapter ?? throw new ArgumentNullException(nameof(platformAdapter));
            m_Localization = localization ?? throw new ArgumentNullException(nameof(localization));
            m_Maps = maps ?? throw new ArgumentNullException(nameof(maps));
            m_AlignmentSolver = alignmentSolver ?? throw new ArgumentNullException(nameof(alignmentSolver));
            m_Tracking = tracking ?? throw new ArgumentNullException(nameof(tracking));
            m_ReadModel = readModel ?? throw new ArgumentNullException(nameof(readModel));
            m_Observer = observer ?? throw new ArgumentNullException(nameof(observer));
            m_WritePoseLogs = writePoseLogs;
        }

        /// <summary>
        /// 执行完整定位周期，管线持有平台返回的图像并始终释放；可选呈现绑定只观察求解快照，不变换输入。
        /// Runs a complete localization cycle. The pipeline owns the camera capture
        /// returned by the platform and always releases it. Optional presentation
        /// bindings observe a solved-fix snapshot; they never transform this input.
        /// </summary>
        public async Task ExecuteAsync(bool publishReadModel, CancellationToken cancellationToken)
        {
            IMixedSpacePlatformFrame platformResult = null;
            MixedSpaceLocalizationBatch localizationBatch = MixedSpaceLocalizationBatch.Empty;

            try
            {
                platformResult = await m_PlatformAdapter.CaptureFrameAsync(
                    cancellationToken);
                if (cancellationToken.IsCancellationRequested || platformResult == null)
                    return;

                if (platformResult.HasCapture)
                {
                    if (m_WritePoseLogs) MixedSpaceCaptureImageJournal.Append(platformResult.Capture);
                    localizationBatch = await m_Localization.EvaluateFrameAsync(
                        platformResult.Capture,
                        cancellationToken) ?? MixedSpaceLocalizationBatch.Empty;
                    if (cancellationToken.IsCancellationRequested)
                        return;

                    foreach (MixedSpaceLocalizationOutcome outcome in localizationBatch.Outcomes)
                    {
                        if (cancellationToken.IsCancellationRequested)
                            return;

                        if (!outcome.Success)
                            continue;

                        if (outcome.Observation == null ||
                            !m_Maps.TryResolve(
                                outcome.Observation.MapKey,
                                out MixedSpaceMapRuntimeRecord record))
                        {
                            MixedSpaceLogger.LogError("Localization result does not match any registered map.");
                            continue;
                        }

                        MixedSpaceLogger.Log(
                            $"Localized to map {record.Definition.Key}-{record.Definition.Label}",
                            MixedSpaceLogger.LoggingLevel.Verbose);

                        var request = new MixedSpaceAlignmentRequest(
                            record.Definition.Key,
                            record.Placement,
                            platformResult.Capture.Profile,
                            outcome,
                            publishReadModel ? record.Definition.BuildMapToEcef() : null);
                        MixedSpaceAlignmentSolution solution = m_AlignmentSolver.Solve(request);
                        if (solution == null || solution.MapKey != record.Definition.Key)
                            throw new InvalidOperationException(
                                "An alignment solver must return a solution for the requested map.");
                        if (cancellationToken.IsCancellationRequested)
                            return;

                        // 保留地图事件先于场景输出的顺序；呈现器不能替换解算结果，也不能以异常阻止其他视图。这是求解状态，不是场景写入凭据。
                        // Preserve map-event-before-scene-output ordering, but a presenter cannot replace the solution or stop other
                        // views by throwing. This is solved state, not a scene commit.
                        if (publishReadModel)
                            m_ReadModel.Record(request, solution);
                        if (cancellationToken.IsCancellationRequested)
                            return;
                        await record.Output.PublishAsync(solution);
                    }
                }

                // 最后一次异步发布期间也可能取消；不报告已取消的周期。
                // A channel or subscriber can cancel while the last asynchronous publication is finishing. Do not report that
                // cancelled cycle.
                if (cancellationToken.IsCancellationRequested)
                    return;

                m_Tracking.Ingest(new MixedSpaceTrackingSignal(
                    platformResult.Tracking,
                    localizationBatch.Outcomes.Count,
                    localizationBatch.Outcomes.Count(outcome => outcome.Success)));
                if (platformResult.HasCapture && !cancellationToken.IsCancellationRequested)
                    m_Observer.Observe(new MixedSpaceLocalizationCycleReport(localizationBatch));
            }
            finally
            {
                platformResult?.Capture?.Dispose();
            }
        }

    }
}
