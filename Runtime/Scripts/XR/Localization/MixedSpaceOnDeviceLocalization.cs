using System;
using MixedSpace.ServiceContracts;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MixedSpace.REST;
using UnityEngine;

namespace MixedSpace.XR
{
    /// <summary>
    /// 内置原生定位服务，由 MixedSpaceRuntime 持有的普通 C# 对象，无 GameObject 或 Unity 生命周期依赖。
    /// Built-in native localization service. It is an ordinary C# object owned by
    /// MixedSpaceRuntime and has no GameObject or Unity lifecycle dependency.
    /// </summary>
    public sealed class MixedSpaceOnDeviceLocalization : IMixedSpaceLocalizationProvider
    {
        private readonly IMixedSpaceMapRepository m_Maps;
        private readonly IMixedSpaceNativeCore m_NativeCore;
        private readonly IMixedSpaceRuntimeClock m_Clock;
        private readonly MixedSpaceProviderActivationMode m_ActivationMode;
        private MixedSpaceSolverType m_SolverType;
        private int m_PriorNearestNeighborCount;
        private float m_PriorRadius;
        private SDKMapId[] m_MapIds = Array.Empty<SDKMapId>();
        private int m_PreviouslyLocalizedMapId;

        /// <summary>
        /// 连续未命中时的日志间隔，单位为秒。定位尝试由会话调度，不等同于相机帧率。
        /// 结果状态变化时立即记录；持续未命中按此间隔记录，避免逐次输出重复日志。
        /// Log interval for continuing misses, in seconds. Session scheduling controls attempts, not
        /// camera frame rate. State changes log immediately; unchanged misses use this interval to avoid repetitive
        /// output.
        /// </summary>
        private const float MissHeartbeatSeconds = 10f;

        private bool m_AttemptReported;
        private bool m_LastAttemptSucceeded;
        private int m_ConsecutiveMisses;
        private float m_NextMissReportAt;

        public MixedSpaceOnDeviceLocalization(
            MixedSpaceOnDeviceOptions options,
            IMixedSpaceMapRepository maps,
            IMixedSpaceNativeCore nativeCore,
            IMixedSpaceRuntimeClock clock)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            m_Maps = maps ?? throw new ArgumentNullException(nameof(maps));
            m_NativeCore = nativeCore ?? throw new ArgumentNullException(nameof(nativeCore));
            m_Clock = clock ?? throw new ArgumentNullException(nameof(clock));
            m_ActivationMode = options.ActivationMode;
            m_SolverType = options.SolverType;
            m_PriorNearestNeighborCount = options.PriorNearestNeighborCount;
            m_PriorRadius = options.PriorRadius;
        }

        public string ProviderId => MixedSpaceProviderIds.OnDevice;
        public MixedSpaceProviderActivationMode ActivationMode => m_ActivationMode;

        public Task<bool> ApplyAsync(MixedSpaceProviderPlan plan)
        {
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            m_SolverType = plan.SolverType ?? m_SolverType;
            m_PriorNearestNeighborCount =
                plan.PriorNearestNeighborCountMin ?? m_PriorNearestNeighborCount;
            m_PriorRadius = plan.PriorRadius ?? m_PriorRadius;

            var maps = new HashSet<int>(m_MapIds.Select(value => value.id));
            if (plan.Additions != null)
                foreach (MixedSpaceMapDefinition map in plan.Additions.Where(map => map != null))
                    maps.Add(map.Key);
            if (plan.Removals != null)
                foreach (MixedSpaceMapDefinition map in plan.Removals.Where(map => map != null))
                    maps.Remove(map.Key);

            m_MapIds = maps.Select(id => new SDKMapId { id = id }).ToArray();
            return Task.FromResult(plan.Removals == null || m_MapIds.Length > 0);
        }

        public async Task<MixedSpaceLocalizationOutcome> EvaluateAsync(
            IMixedSpaceCameraCapture capture,
            CancellationToken cancellationToken)
        {
            if (capture == null) throw new ArgumentNullException(nameof(capture));
            cancellationToken.ThrowIfCancellationRequested();
            using IMixedSpacePixelLease pixels = capture.LeasePixels();
            MixedSpaceCaptureProfile profile = capture.Profile;
            float startedAt = m_Clock.RealtimeSinceStartup;

            MixedSpaceLocalizationObservation observation;
            if (m_SolverType == MixedSpaceSolverType.Prior &&
                m_PreviouslyLocalizedMapId != 0 &&
                m_Maps.TryResolve(
                    m_PreviouslyLocalizedMapId,
                    out MixedSpaceMapRuntimeRecord record))
            {
                Vector3 position = profile.TrackerPosition;
                Matrix4x4 mapPose = record.Output.ProjectToLocal(position, Quaternion.identity);
                Vector3 priorPosition = record.Placement.RemovePlacement(mapPose).GetPosition();
                priorPosition = MixedSpaceCoordinateFrame.ReflectXAxis(priorPosition);
                MixedSpaceNativeLocalizationQuery query =
                    MixedSpaceNativeLocalizationQuery.Around(
                        priorPosition,
                        m_PriorNearestNeighborCount,
                        m_PriorRadius);
                observation = await Task.Run(
                    () => m_NativeCore.Localizer.Resolve(
                        profile,
                        pixels.Pointer,
                        query),
                    cancellationToken);
            }
            else
            {
                m_PreviouslyLocalizedMapId = 0;
                MixedSpaceNativeLocalizationQuery query =
                    MixedSpaceNativeLocalizationQuery.Unconstrained(m_SolverType);
                observation = await Task.Run(
                    () => m_NativeCore.Localizer.Resolve(
                        profile,
                        pixels.Pointer,
                        query),
                    cancellationToken);
            }

            float elapsed = m_Clock.RealtimeSinceStartup - startedAt;
            bool success = observation != null;
            if (success)
                m_PreviouslyLocalizedMapId = observation.MapKey;

            ReportAttempt(success, elapsed, m_Clock.RealtimeSinceStartup);

            return success
                ? MixedSpaceLocalizationOutcome.Located(observation)
                : MixedSpaceLocalizationOutcome.Missed();
        }

        /// <summary>
        /// 状态首次改变时记录一次，持续未命中低频重报，持续成功保持安静，因为状态转移已证明恢复。
        /// Reports the outcome of one attempt without narrating every frame: the first
        /// attempt in a new state is logged so the transition is visible, a continuing
        /// run of misses re-reports at a low rate, and a continuing run of hits stays
        /// quiet because its transition line already proved the localizer recovered.
        /// </summary>
        private void ReportAttempt(bool success, float elapsed, float now)
        {
            bool stateChanged = !m_AttemptReported || success != m_LastAttemptSucceeded;
            m_AttemptReported = true;
            m_LastAttemptSucceeded = success;

            if (success)
            {
                m_ConsecutiveMisses = 0;
                if (stateChanged)
                    MixedSpaceLogger.Log($"Relocalized in {elapsed} seconds");
                return;
            }

            m_ConsecutiveMisses++;
            if (stateChanged)
            {
                m_NextMissReportAt = now + MissHeartbeatSeconds;
                MixedSpaceLogger.Log($"Localization attempt failed after {elapsed} seconds");
                return;
            }

            if (now >= m_NextMissReportAt)
            {
                m_NextMissReportAt = now + MissHeartbeatSeconds;
                MixedSpaceLogger.Log(
                    $"Localization still failing after {m_ConsecutiveMisses} consecutive attempts; last one took {elapsed} seconds.");
            }
        }

        public Task ReleaseAsync()
        {
            m_MapIds = Array.Empty<SDKMapId>();
            m_PreviouslyLocalizedMapId = 0;
            m_AttemptReported = false;
            m_LastAttemptSucceeded = false;
            m_ConsecutiveMisses = 0;
            m_NextMissReportAt = 0f;
            return Task.CompletedTask;
        }

    }
}
