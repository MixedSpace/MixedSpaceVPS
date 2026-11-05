using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MixedSpace.XR;

namespace MixedSpace
{
    /// <summary>
    /// 管理分阶段启动状态机，可继续未完成的启动，而不重复地图导入或平台激活。
    /// Owns the staged startup state machine. It can resume a partially completed
    /// startup without repeating map imports or platform activation.
    /// </summary>
    internal sealed class MixedSpaceStartupCoordinator
    {
        private readonly IMixedSpacePlatformAdapter m_Platform;
        private readonly IMixedSpaceLocalizationCoordinator m_Localization;
        private readonly IMixedSpaceMapRepository m_Maps;
        private readonly IMixedSpaceNativeCore m_NativeCore;
        private readonly MixedSpaceSceneMapImporter m_SceneMaps;
        private readonly Action<MixedSpaceRuntimeState> m_ReportState;
        private bool m_MapsReady;
        private bool m_PlatformReady;
        private bool m_LocalizationReady;

        public MixedSpaceStartupCoordinator(
            IMixedSpacePlatformAdapter platform,
            IMixedSpaceLocalizationCoordinator localization,
            IMixedSpaceMapRepository maps,
            IMixedSpaceNativeCore nativeCore,
            MixedSpaceSceneMapImporter sceneMaps,
            Action<MixedSpaceRuntimeState> reportState)
        {
            m_Platform = platform ?? throw new ArgumentNullException(nameof(platform));
            m_Localization = localization ?? throw new ArgumentNullException(nameof(localization));
            m_Maps = maps ?? throw new ArgumentNullException(nameof(maps));
            m_NativeCore = nativeCore ?? throw new ArgumentNullException(nameof(nativeCore));
            m_SceneMaps = sceneMaps ?? throw new ArgumentNullException(nameof(sceneMaps));
            m_ReportState = reportState ?? throw new ArgumentNullException(nameof(reportState));
        }

        public async Task<bool> StartAsync(
            IEnumerable<MixedSpaceMapBinding> sceneBindings,
            CancellationToken cancellationToken)
        {
            BindProviderServices();

            if (!m_MapsReady)
            {
                m_ReportState(MixedSpaceRuntimeState.LoadingMaps);
                m_MapsReady = await m_SceneMaps.ImportAsync(sceneBindings);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!m_PlatformReady)
            {
                m_ReportState(MixedSpaceRuntimeState.ConfiguringPlatform);
                m_PlatformReady = await m_Platform.StartAsync(cancellationToken);
                if (m_PlatformReady)
                    MixedSpaceLogger.Log(
                        "Platform configured.",
                        MixedSpaceLogger.LoggingLevel.Verbose);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!m_LocalizationReady)
            {
                m_ReportState(MixedSpaceRuntimeState.ConfiguringLocalization);
                m_LocalizationReady = await ApplyLocalizationPlanAsync();
                if (m_LocalizationReady)
                    MixedSpaceLogger.Log(
                        "Localization configured.",
                        MixedSpaceLogger.LoggingLevel.Verbose);
            }

            return m_MapsReady && m_PlatformReady && m_LocalizationReady;
        }

        public void Invalidate()
        {
            m_MapsReady = false;
            m_PlatformReady = false;
            m_LocalizationReady = false;
        }

        private async Task<bool> ApplyLocalizationPlanAsync()
        {
            IReadOnlyList<MixedSpaceMapDefinition> maps = m_Maps.SnapshotDefinitions();
            BindProviderServices();

            var additions = new Dictionary<string, IReadOnlyList<MixedSpaceMapDefinition>>(
                StringComparer.Ordinal);
            foreach (IGrouping<string, MixedSpaceMapDefinition> group in
                maps.GroupBy(map => map.ProviderKey))
            {
                if (!m_Localization.TryResolveProvider(group.Key, out _))
                    throw new InvalidOperationException(
                        $"Map provider '{group.Key}' is unavailable.");
                additions[group.Key] = group.ToArray();
            }

            return await m_Localization.ApplyPlanAsync(
                new MixedSpaceLocalizationPlan(
                    additions: additions,
                    cancelActiveCycle: true));
        }

        private void BindProviderServices()
        {
            IReadOnlyList<IMixedSpaceLocalizationProvider> providers =
                m_Localization.Providers ?? Array.Empty<IMixedSpaceLocalizationProvider>();
            foreach (IMixedSpaceLocalizationProvider provider in providers)
            {
                if (provider is IMixedSpaceMapRepositoryConsumer repositoryConsumer)
                    repositoryConsumer.SetMapRepository(m_Maps);
                if (provider is IMixedSpaceNativeCoreConsumer nativeCoreConsumer)
                    nativeCoreConsumer.SetNativeCore(m_NativeCore);
            }
        }
    }
}
