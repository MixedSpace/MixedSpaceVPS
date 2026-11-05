using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MixedSpace.XR
{
    /// <summary>
    /// 定位提供器地图集合的一次原子变更，任务调度及操作对象留在引擎内部。
    /// One atomic change to the set of maps handled by localization providers.
    /// Task scheduling and operation objects remain private to the engine.
    /// </summary>
    public sealed class MixedSpaceLocalizationPlan
    {
        public MixedSpaceLocalizationPlan(
            IReadOnlyDictionary<string, IReadOnlyList<MixedSpaceMapDefinition>> additions = null,
            IReadOnlyDictionary<string, IReadOnlyList<MixedSpaceMapDefinition>> removals = null,
            bool cancelActiveCycle = false)
        {
            Additions = Snapshot(additions);
            Removals = Snapshot(removals);
            CancelActiveCycle = cancelActiveCycle;
        }

        public IReadOnlyDictionary<string, IReadOnlyList<MixedSpaceMapDefinition>> Additions { get; }
        public IReadOnlyDictionary<string, IReadOnlyList<MixedSpaceMapDefinition>> Removals { get; }
        public bool CancelActiveCycle { get; }

        private static IReadOnlyDictionary<
            string,
            IReadOnlyList<MixedSpaceMapDefinition>> Snapshot(
                IReadOnlyDictionary<
                    string,
                    IReadOnlyList<MixedSpaceMapDefinition>> source)
        {
            if (source == null)
                return null;

            var snapshot = source.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyList<MixedSpaceMapDefinition>)
                    new ReadOnlyCollection<MixedSpaceMapDefinition>(
                        pair.Value?.ToArray() ?? Array.Empty<MixedSpaceMapDefinition>()),
                StringComparer.Ordinal);
            return new ReadOnlyDictionary<
                string,
                IReadOnlyList<MixedSpaceMapDefinition>>(snapshot);
        }
    }

    /// <summary>
    /// 应用定位边界接收计划、返回周期值，不暴露内部任务或提供器操作句柄。
    /// Application boundary for localization. It accepts plans and returns cycle
    /// values; it does not expose internal tasks or provider-operation handles.
    /// </summary>
    public interface IMixedSpaceLocalizationCoordinator
    {
        IReadOnlyList<IMixedSpaceLocalizationProvider> Providers { get; }

        bool TryResolveProvider(
            string providerId,
            out IMixedSpaceLocalizationProvider provider);

        Task<bool> ApplyPlanAsync(MixedSpaceLocalizationPlan plan);

        Task<MixedSpaceLocalizationBatch> EvaluateFrameAsync(
            IMixedSpaceCameraCapture capture,
            CancellationToken cancellationToken);

        Task ShutdownAsync();
    }
}
