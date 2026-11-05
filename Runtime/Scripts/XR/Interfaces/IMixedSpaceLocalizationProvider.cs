using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace MixedSpace.XR
{
    /// <summary>
    /// 与提供器无关的位姿观测；原生和云传输结果在进入定位引擎前已转换。
    /// Provider-neutral pose observation. Native and cloud transport result types
    /// are translated before they enter the localization engine.
    /// </summary>
    public sealed class MixedSpaceLocalizationObservation
    {
        public MixedSpaceLocalizationObservation(
            int mapKey,
            Vector3 position,
            Quaternion rotation,
            int confidence)
        {
            if (mapKey <= 0)
                throw new ArgumentOutOfRangeException(nameof(mapKey), "Map key must be positive.");

            MapKey = mapKey;
            Position = position;
            Rotation = rotation;
            Confidence = confidence;
        }

        public int MapKey { get; }
        public Vector3 Position { get; }
        public Quaternion Rotation { get; }
        public int Confidence { get; }
    }

    /// <summary>
    /// 单个定位提供器使用的配置增量。
    /// Configuration delta consumed by one localization provider.
    /// </summary>
    public sealed class MixedSpaceProviderPlan
    {
        public MixedSpaceProviderPlan(
            IEnumerable<MixedSpaceMapDefinition> additions = null,
            IEnumerable<MixedSpaceMapDefinition> removals = null,
            MixedSpaceSolverType? solverType = null,
            int? priorNearestNeighborCountMin = null,
            int? priorNearestNeighborCountMax = null,
            Vector3? priorScale = null,
            float? priorRadius = null)
        {
            Additions = Snapshot(additions);
            Removals = Snapshot(removals);
            SolverType = solverType;
            PriorNearestNeighborCountMin = priorNearestNeighborCountMin;
            PriorNearestNeighborCountMax = priorNearestNeighborCountMax;
            PriorScale = priorScale;
            PriorRadius = priorRadius;
        }

        public IReadOnlyList<MixedSpaceMapDefinition> Additions { get; }
        public IReadOnlyList<MixedSpaceMapDefinition> Removals { get; }
        public MixedSpaceSolverType? SolverType { get; }
        public int? PriorNearestNeighborCountMin { get; }
        public int? PriorNearestNeighborCountMax { get; }
        public Vector3? PriorScale { get; }
        public float? PriorRadius { get; }

        private static IReadOnlyList<MixedSpaceMapDefinition> Snapshot(
            IEnumerable<MixedSpaceMapDefinition> maps)
        {
            return maps == null
                ? null
                : new ReadOnlyCollection<MixedSpaceMapDefinition>(maps.ToArray());
        }
    }

    public enum MixedSpaceProviderActivationMode
    {
        WhenNecessary,
        Always
    }

    /// <summary>
    /// 可插拔定位算法；引擎负责调度，提供器仅接收计划、计算一帧并释放自有资源。
    /// Pluggable localization algorithm. The engine owns scheduling; providers
    /// only accept a plan, evaluate one frame and release their own resources.
    /// </summary>
    public interface IMixedSpaceLocalizationProvider : IHasNullOrDeadCheck
    {
        string ProviderId { get; }
        MixedSpaceProviderActivationMode ActivationMode { get; }

        Task<bool> ApplyAsync(MixedSpaceProviderPlan plan);

        Task<MixedSpaceLocalizationOutcome> EvaluateAsync(
            IMixedSpaceCameraCapture capture,
            CancellationToken cancellationToken);

        Task ReleaseAsync();
    }

    public static class MixedSpaceProviderIds
    {
        public const string OnDevice = "mixedspace.on-device";
        public const string Cloud = "mixedspace.cloud";
        public const string Geo = "mixedspace.geo";
    }

    public interface IHasNullOrDeadCheck
    {
    }

    public static class NullOrDeadCheckExtension
    {
        public static bool IsNullOrDead(this IHasNullOrDeadCheck value)
        {
            if (value is UnityEngine.Object unityObject)
                return unityObject == null;
            return value == null;
        }
    }
}
