using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MixedSpace.XR;

namespace MixedSpace
{
    /// <summary>
    /// 将场景地图声明导入运行时库；场景发现留在 Unity 边界，此用例负责验证、并行导入、可选显示和过期 World 协调。
    /// Imports declarative scene bindings into the runtime map library. Scene
    /// discovery stays at the Unity boundary; this use case owns validation,
    /// parallel import, optional presentation and stale-world reconciliation.
    /// </summary>
    internal sealed class MixedSpaceSceneMapImporter
    {
        private readonly IMixedSpaceMapRepository m_Maps;
        private readonly MixedSpaceWorldRegistry m_Worlds;
        private readonly MixedSpaceMapPreviewService m_Previews;

        public MixedSpaceSceneMapImporter(
            IMixedSpaceMapRepository maps,
            MixedSpaceWorldRegistry worlds,
            MixedSpaceMapPreviewService previews)
        {
            m_Maps = maps ?? throw new ArgumentNullException(nameof(maps));
            m_Worlds = worlds ?? throw new ArgumentNullException(nameof(worlds));
            m_Previews = previews ?? throw new ArgumentNullException(nameof(previews));
        }

        public async Task<bool> ImportAsync(IEnumerable<MixedSpaceMapBinding> sceneBindings)
        {
            MixedSpaceMapBinding[] bindings = (sceneBindings ??
                Array.Empty<MixedSpaceMapBinding>())
                .Where(binding => binding)
                .ToArray();
            MixedSpaceLogger.Log($"Found {bindings.Length} maps in scene.");

            var importTasks = new List<Task<bool>>(bindings.Length);
            bool allBindingsValid = true;
            foreach (MixedSpaceMapBinding binding in bindings)
            {
                int mapKey = binding.Definition.Key;
                if (MixedSpaceMapDefinition.IsRetiredKey(mapKey))
                {
                    MixedSpaceLogger.LogWarning($"Skipping retired scene map {mapKey}. Use an XrLoc map.");
                    continue;
                }

                MixedSpaceWorldRoot root = binding.transform
                    .GetComponentInParent<MixedSpaceWorldRoot>(true);
                if (!root)
                {
                    allBindingsValid = false;
                    MixedSpaceLogger.LogError(
                        $"Map '{binding.name}' is not parented below a MixedSpaceWorldRoot.");
                    continue;
                }

                IMixedSpaceWorldChannel world = m_Worlds.Open(root);
                MixedSpaceLogger.Log($"Importing map {mapKey}");
                importTasks.Add(m_Maps.ImportAsync(
                    new MixedSpaceMapImport(binding, world)));
            }

            bool[] importResults = await Task.WhenAll(importTasks);
            Task[] previewTasks = bindings
                .Where(binding =>
                    m_Maps.TryResolve(
                        binding.Definition.Key,
                        out MixedSpaceMapRuntimeRecord record) &&
                    record.Binding == binding)
                .Select(binding => m_Previews.PresentIfRequestedAsync(binding))
                .Cast<Task>()
                .ToArray();
            await Task.WhenAll(previewTasks);

            IReadOnlyList<IMixedSpaceWorldChannel> importedWorlds = m_Maps.SnapshotOutputs();
            foreach (IMixedSpaceWorldChannel world in m_Worlds.ActiveChannels.ToArray())
            {
                if (!importedWorlds.Any(candidate => ReferenceEquals(candidate, world)))
                    m_Worlds.Close(world.View);
            }

            IReadOnlyList<MixedSpaceMapDefinition> definitions =
                m_Maps.SnapshotDefinitions();
            if (MixedSpaceLogger.Level <= MixedSpaceLogger.LoggingLevel.Verbose)
                MixedSpaceLogger.Log(
                    $"Scene map import completed: valid={allBindingsValid} imported={importResults.Count(result => result)}/{importResults.Length} " +
                    $"maps=[{string.Join(",", definitions.Select(map => map.Key))}]",
                    MixedSpaceLogger.LoggingLevel.Verbose);
            return allBindingsValid && importResults.All(success => success);
        }
    }
}
