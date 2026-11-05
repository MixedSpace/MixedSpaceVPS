using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;

namespace MixedSpace.XR
{
    /// <summary>
    /// 仓库可选能力：确认原生地图确实已导入并能离线求解。
    /// Optional repository capability: native maps actually imported for offline solving.
    /// </summary>
    public interface IMixedSpaceOfflineMapReadiness
    {
        bool HasOfflineMaps { get; }
    }

    /// <summary>
    /// 运行时地图库接收的完整请求。
    /// Complete request accepted by the runtime map library.
    /// </summary>
    public sealed class MixedSpaceMapImport
    {
        public MixedSpaceMapBinding Binding { get; }
        public MixedSpaceMapDefinition Definition { get; }
        public IMixedSpaceWorldChannel Output { get; }
        public MixedSpaceMapPlacement Placement { get; }

        public MixedSpaceMapImport(
            MixedSpaceMapBinding binding,
            IMixedSpaceWorldChannel output)
        {
            Binding = binding ? binding : throw new ArgumentNullException(nameof(binding));
            Output = output ?? throw new ArgumentNullException(nameof(output));
            Definition = binding.Definition;
            Transform transform = binding.transform;
            Placement = new MixedSpaceMapPlacement(
                transform.localPosition,
                transform.localRotation,
                transform.localScale);
        }
    }

    /// <summary>
    /// 每 SDK 独占的运行时地图库，持有原生数据和解析记录；编辑与定位配置留给独立用例。
    /// Per-SDK runtime map library. It owns native map data and resolved entries,
    /// while authoring and localization configuration live in separate use cases.
    /// </summary>
    public sealed class MixedSpaceMapCatalog : IMixedSpaceMapRepository, IMixedSpaceOfflineMapReadiness
    {
        private readonly IMixedSpaceNativeCore m_NativeCore;
        private readonly IMixedSpaceMapContentSource m_ContentSource;
        private readonly MixedSpaceLocalizationMode m_Mode;
        private readonly bool m_PrepareOfflineMaps;
        private readonly Func<bool> m_CanDownload;
        private readonly HashSet<int> m_NativeReady = new HashSet<int>();
        public bool HasOfflineMaps => m_NativeReady.Count > 0;
        private readonly Dictionary<int, MixedSpaceMapRuntimeRecord> m_Records =
            new Dictionary<int, MixedSpaceMapRuntimeRecord>();

        public MixedSpaceMapCatalog(
            IMixedSpaceNativeCore nativeCore,
            IMixedSpaceMapContentSource contentSource,
            MixedSpaceLocalizationMode mode = MixedSpaceLocalizationMode.OnDevice)
            : this(nativeCore, contentSource, mode, false, null) { }

        public MixedSpaceMapCatalog(
            IMixedSpaceNativeCore nativeCore,
            IMixedSpaceMapContentSource contentSource,
            MixedSpaceLocalizationMode mode,
            bool prepareOfflineMaps,
            Func<bool> canDownload)
        {
            m_NativeCore = nativeCore ?? throw new ArgumentNullException(nameof(nativeCore));
            m_ContentSource = contentSource ??
                throw new ArgumentNullException(nameof(contentSource));
            if (mode == 0) mode = MixedSpaceLocalizationMode.OnDevice;
            if (!Enum.IsDefined(typeof(MixedSpaceLocalizationMode), mode))
                throw new ArgumentOutOfRangeException(nameof(mode));
            m_Mode = mode;
            m_PrepareOfflineMaps = prepareOfflineMaps;
            m_CanDownload = canDownload ?? (() => true);
        }

        public event Action<MixedSpaceMapLibraryChange> Changed;

        /// <summary>
        /// 解析有效声明而不重写场景资产；显式覆盖适用于全部地图，地图本身不选择提供器。云端只需身份，本地保留已有字节或下载数据。
        /// Resolve an effective declaration without rewriting scene assets. Explicit overrides
        /// target every map; provider selection is owned by the runtime configuration.
        /// Cloud needs only map identity; local mode preserves supplied bytes or downloads them.
        /// </summary>
        internal static MixedSpaceMapDefinition ResolveDefinition(
            MixedSpaceMapDefinition source, MixedSpaceLocalizationMode mode)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (mode == 0) mode = MixedSpaceLocalizationMode.OnDevice;
            if (mode != MixedSpaceLocalizationMode.OnDevice && mode != MixedSpaceLocalizationMode.Cloud)
                throw new ArgumentOutOfRangeException(nameof(mode));
            bool cloud = mode == MixedSpaceLocalizationMode.Cloud;
            var policy = cloud
                ? MixedSpaceMapContentPolicy.ProviderManaged(source.ContentPolicy.PresentPreviewAtRuntime)
                : source.ContentPolicy;
            return new MixedSpaceMapDefinition(source.Key, source.Label, source.AccessLevel,
                source.Alignment, source.GeoOrigin,
                cloud ? MixedSpaceProviderIds.Cloud : MixedSpaceProviderIds.OnDevice,
                source.EmbeddedData, policy, source.IsReady);
        }

        public bool HasMaps => m_Records.Count > 0;

        public async Task<bool> ImportAsync(MixedSpaceMapImport request)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));

            if (m_Mode == MixedSpaceLocalizationMode.Cloud &&
                request.Definition.Key >= 0x40000000)
            {
                MixedSpaceLogger.LogError(
                    $"Local asset '{request.Definition.Label}' has no cloud map ID. " +
                    "Use on-device localization or bind a cloud map ID.");
                Changed?.Invoke(new MixedSpaceMapLibraryChange(
                    MixedSpaceMapLibraryChangeKind.ImportFailed,
                    request.Definition.Key));
                return false;
            }

            MixedSpaceMapDefinition definition = ResolveDefinition(request.Definition, m_Mode);
            if (!definition.IsReady || definition.Key <= 0)
            {
                MixedSpaceLogger.LogError(
                    $"Cannot import incomplete map declaration '{definition.Label}'.");
                Changed?.Invoke(new MixedSpaceMapLibraryChange(
                    MixedSpaceMapLibraryChangeKind.ImportFailed,
                    definition.Key));
                return false;
            }

            if (m_Records.TryGetValue(definition.Key, out MixedSpaceMapRuntimeRecord existing))
            {
                if (existing.Binding == request.Binding &&
                    ReferenceEquals(existing.Output, request.Output))
                {
                    return true;
                }

                MixedSpaceLogger.LogError(
                    $"Map {definition.Key} is already imported by another scene binding.");
                Changed?.Invoke(new MixedSpaceMapLibraryChange(
                    MixedSpaceMapLibraryChangeKind.ImportFailed,
                    definition.Key));
                return false;
            }

            m_Records.Add(definition.Key, new MixedSpaceMapRuntimeRecord(
                definition,
                request.Binding,
                request.Output,
                request.Placement));

            bool loaded;
            var importTimer = System.Diagnostics.Stopwatch.StartNew();
            MixedSpaceLogger.Log($"Import begin map={definition.Key} mode={m_Mode} location={definition.ContentPolicy.Location} prepareOffline={m_PrepareOfflineMaps}", MixedSpaceLogger.LoggingLevel.Verbose);
            try
            {
                loaded = await LoadDeclaredDataAsync(definition);
                MixedSpaceLogger.Log($"Declared data complete map={definition.Key} loaded={loaded} elapsedMs={importTimer.ElapsedMilliseconds}", MixedSpaceLogger.LoggingLevel.Verbose);
                if (loaded && m_Mode == MixedSpaceLocalizationMode.Cloud && m_PrepareOfflineMaps)
                {
                    MixedSpaceLogger.Log($"Offline preparation begin map={definition.Key}", MixedSpaceLogger.LoggingLevel.Verbose);
                    await PrepareOfflineDataAsync(request.Definition);
                    MixedSpaceLogger.Log($"Offline preparation complete map={definition.Key} elapsedMs={importTimer.ElapsedMilliseconds}", MixedSpaceLogger.LoggingLevel.Verbose);
                }
            }
            catch (Exception exception)
            {
                MixedSpaceLogger.LogError($"Import exception map={definition.Key} type={exception.GetType().Name} elapsedMs={importTimer.ElapsedMilliseconds}");
                loaded = false;
                MixedSpaceLogger.LogError(
                    $"Map {definition.Key} import failed: {exception.Message}");
            }

            if (!loaded)
            {
                m_NativeCore.Maps.Evict(definition.Key);
                m_NativeReady.Remove(definition.Key);
                m_Records.Remove(definition.Key);
            }

            Changed?.Invoke(new MixedSpaceMapLibraryChange(
                loaded
                    ? MixedSpaceMapLibraryChangeKind.Imported
                    : MixedSpaceMapLibraryChangeKind.ImportFailed,
                definition.Key));

            MixedSpaceLogger.Log(
                $"Map import {definition.Key} completed: {loaded}.",
                MixedSpaceLogger.LoggingLevel.Verbose);
            return loaded;
        }

        private async Task<bool> LoadDeclaredDataAsync(
            MixedSpaceMapDefinition definition)
        {
            MixedSpaceMapContentPolicy policy = definition.ContentPolicy;

            // 设备端地图需要原生数据；声明未给出明确策略时，在应用边界推导一次安全默认值。
            // On-device maps need native data. When the declaration has no explicit plan, infer the same safe default once
            // at the application boundary.
            if (policy.Location == MixedSpaceMapContentLocation.ProviderManaged &&
                string.Equals(
                    definition.ProviderKey,
                    MixedSpaceProviderIds.OnDevice,
                    StringComparison.Ordinal))
            {
                policy = definition.EmbeddedData
                    ? MixedSpaceMapContentPolicy.Embedded(
                        policy.PresentPreviewAtRuntime)
                    : MixedSpaceMapContentPolicy.Remote(
                        policy.PresentPreviewAtRuntime);
            }

            // 仅云端及扩展提供器可能不需要原生地图字节。
            // Cloud-only and extension providers may not require native map bytes.
            if (policy.Location == MixedSpaceMapContentLocation.ProviderManaged)
                return true;

            if (policy.Location == MixedSpaceMapContentLocation.InMemory)
                return await SupplyNativeDataAsync(
                    definition.Key,
                    policy.CopyRuntimePayload());

            if (policy.Location == MixedSpaceMapContentLocation.RemoteBinary)
            {
                MixedSpaceLogger.Log($"Binary download begin map={definition.Key}", MixedSpaceLogger.LoggingLevel.Verbose);
                byte[] downloaded = await m_ContentSource.DownloadBinaryAsync(
                    definition.Key);
                MixedSpaceLogger.Log($"Binary download complete map={definition.Key} bytes={downloaded?.Length ?? 0}", MixedSpaceLogger.LoggingLevel.Verbose);
                return await SupplyNativeDataAsync(definition.Key, downloaded);
            }

            if (policy.Location == MixedSpaceMapContentLocation.EmbeddedAsset)
            {
                if (!definition.EmbeddedData)
                {
                    MixedSpaceLogger.LogError(
                        $"Embedded data is missing for map {definition.Key}.");
                    return false;
                }

                MixedSpaceLogger.Log($"Embedded data read begin map={definition.Key}", MixedSpaceLogger.LoggingLevel.Verbose);
                byte[] embeddedBytes = definition.EmbeddedData.bytes;
                MixedSpaceLogger.Log($"Embedded data read complete map={definition.Key} bytes={embeddedBytes.Length}", MixedSpaceLogger.LoggingLevel.Verbose);
                return await SupplyNativeDataAsync(definition.Key, embeddedBytes);
            }

            return true;
        }

        /// <summary>
        /// 离线准备失败不影响云身份导入成功；使用原声明而非云端投影，以保留嵌入及内存字节，不新增磁盘缓存或重写场景。宣布导入完成前等待准备，避免与本地求解竞争。
        /// Cloud identity import remains successful even if offline preparation fails.
        /// Use the original declaration (not Cloud's ProviderManaged projection) so
        /// embedded and in-memory bytes survive. No disk cache or scene rewrite is introduced.
        /// Await preparation before announcing map import, avoiding a race with local solving.
        /// </summary>
        private async Task PrepareOfflineDataAsync(MixedSpaceMapDefinition source)
        {
            try
            {
                var local = ResolveDefinition(source, MixedSpaceLocalizationMode.OnDevice);
                var location = local.ContentPolicy.Location;
                bool needsDownload = location == MixedSpaceMapContentLocation.RemoteBinary ||
                    (location == MixedSpaceMapContentLocation.ProviderManaged && !local.EmbeddedData);
                if (needsDownload && !m_CanDownload())
                {
                    MixedSpaceLogger.LogWarning($"Offline map {source.Key} unavailable: no embedded data and network is disconnected.");
                    return;
                }
                bool ready = await LoadDeclaredDataAsync(local);
                if (ready)
                    MixedSpaceLogger.Log($"Offline fallback map {source.Key} ready.");
                else
                    MixedSpaceLogger.LogWarning($"Offline fallback map {source.Key} could not be prepared; cloud remains available.");
            }
            catch (Exception exception)
            {
                // 可选下载失败不应删除可用云地图，也不回显异常中的 URL 或响应数据。
                // A failed optional download must not remove a usable cloud map. Do not echo backend URLs or response data from
                // arbitrary exceptions.
                MixedSpaceLogger.LogWarning($"Offline fallback map {source.Key} preparation failed ({exception.GetType().Name}); cloud remains available.");
            }
        }

        public async Task<bool> SupplyNativeDataAsync(int mapKey, byte[] bytes)
        {
            if (!m_Records.ContainsKey(mapKey))
            {
                MixedSpaceLogger.LogError(
                    $"Cannot supply data for unknown map {mapKey}.");
                return false;
            }
            if (bytes == null || bytes.Length == 0)
                return false;

            var nativeTimer = System.Diagnostics.Stopwatch.StartNew();
            bool imported = await Task.Run(() => m_NativeCore.Maps.Import(mapKey, bytes));
            if (MixedSpaceLogger.Level <= MixedSpaceLogger.LoggingLevel.Verbose)
                MixedSpaceLogger.Log(
                    $"Native map import completed: map={mapKey} imported={imported} elapsedMs={nativeTimer.ElapsedMilliseconds}",
                    MixedSpaceLogger.LoggingLevel.Verbose);
            if (imported && m_Records.ContainsKey(mapKey)) m_NativeReady.Add(mapKey);
            return imported;
        }

        public IReadOnlyList<MixedSpaceMapDefinition> SnapshotDefinitions()
        {
            return m_Records.Values
                .Select(entry => entry.Definition)
                .ToArray();
        }

        public IReadOnlyList<IMixedSpaceWorldChannel> SnapshotOutputs()
        {
            return m_Records.Values
                .Select(record => record.Output)
                .Distinct()
                .ToArray();
        }

        public bool TryResolve(int mapKey, out MixedSpaceMapRuntimeRecord record)
        {
            return m_Records.TryGetValue(mapKey, out record);
        }

        public Task EvictAsync(int mapKey)
        {
            Evict(mapKey);
            return Task.CompletedTask;
        }

        public Task ClearAsync()
        {
            foreach (int key in m_Records.Keys.ToArray())
                Evict(key);
            return Task.CompletedTask;
        }

        public void ReleaseNativeState()
        {
            foreach (int key in m_Records.Keys.ToArray())
                Evict(key);
        }

        private void Evict(int mapKey)
        {
            if (!m_Records.TryGetValue(mapKey, out MixedSpaceMapRuntimeRecord record))
                return;

            m_NativeCore.Maps.Evict(mapKey);
            m_NativeReady.Remove(mapKey);
            m_Records.Remove(mapKey);
            Changed?.Invoke(new MixedSpaceMapLibraryChange(
                MixedSpaceMapLibraryChangeKind.Evicted,
                mapKey));
        }
    }

    /// <summary>
    /// 动态场景地图用例，是允许运行时创建地图 GameObject 并更新提供器配置的服务入口。
    /// Dynamic scene-map use case. This is the only runtime service allowed to
    /// create a map GameObject and update provider configuration.
    /// </summary>
    public sealed class MixedSpaceMapProvisioner
    {
        private readonly IMixedSpaceMapRepository m_Library;
        private readonly IMixedSpaceLocalizationCoordinator m_Localization;
        private readonly MixedSpaceWorldRegistry m_Worlds;
        private readonly MixedSpaceMapPreviewService m_Previews;

        public MixedSpaceMapProvisioner(
            IMixedSpaceMapRepository library,
            IMixedSpaceLocalizationCoordinator localization,
            MixedSpaceWorldRegistry worlds,
            MixedSpaceMapPreviewService previews)
        {
            m_Library = library ?? throw new ArgumentNullException(nameof(library));
            m_Localization = localization ?? throw new ArgumentNullException(nameof(localization));
            m_Worlds = worlds ?? throw new ArgumentNullException(nameof(worlds));
            m_Previews = previews ?? throw new ArgumentNullException(nameof(previews));
        }

        public async Task<MixedSpaceMapProvisioningResult> ProvisionAsync(
            MixedSpaceMapBlueprint blueprint)
        {
            if (blueprint == null)
                throw new ArgumentNullException(nameof(blueprint));

            string label = blueprint.Label;
            var mapObject = new GameObject(label);
            var binding = mapObject.AddComponent<MixedSpaceMapBinding>();

            MixedSpaceWorldRoot root = blueprint.WorldRoot;
            bool createdRoot = !root;
            if (!root)
            {
                var rootObject = new GameObject("MixedSpace World");
                root = rootObject.AddComponent<MixedSpaceWorldRoot>();
            }
            mapObject.transform.SetParent(root.transform, false);

            IMixedSpaceWorldChannel openedWorld = null;
            MixedSpaceMapDefinition definition = null;
            bool imported = false;
            try
            {
                binding.Define(
                    blueprint.Key,
                    blueprint.Label,
                    blueprint.ProviderId,
                    blueprint.EmbeddedData,
                    blueprint.Alignment,
                    blueprint.GeoOrigin,
                    blueprint.AccessLevel,
                    blueprint.ContentPolicy,
                    renameObject: true);

                if (blueprint.ApplyRelationToTransform)
                    binding.ApplyRelationToTransform();

                definition = binding.Definition;
                if (m_Library.TryResolve(definition.Key, out _))
                {
                    MixedSpaceLogger.LogError(
                        $"Map {definition.Key} is already present in the runtime library.");
                    CleanFailedProvision(binding, root, createdRoot);
                    return MixedSpaceMapProvisioningResult.Failed();
                }

                openedWorld = m_Worlds.Open(root);
                imported = await m_Library.ImportAsync(
                    new MixedSpaceMapImport(binding, openedWorld));
                if (!imported)
                {
                    CloseWorldIfUnused(openedWorld);
                    openedWorld = null;
                    CleanFailedProvision(binding, root, createdRoot);
                    return MixedSpaceMapProvisioningResult.Failed();
                }

                // 使用仓库的有效路由，它可能覆盖蓝图声明。
                // Use the library's effective route, which may override the blueprint.
                if (!m_Library.TryResolve(definition.Key, out MixedSpaceMapRuntimeRecord effective))
                    throw new InvalidOperationException("Imported map is missing from the library.");
                definition = effective.Definition;
                var additions =
                    new Dictionary<string, IReadOnlyList<MixedSpaceMapDefinition>>(
                        StringComparer.Ordinal)
                    {
                        { definition.ProviderKey, new[] { definition } }
                    };
                bool configured = await m_Localization.ApplyPlanAsync(
                    new MixedSpaceLocalizationPlan(additions: additions));
                if (configured)
                {
                    await m_Previews.PresentIfRequestedAsync(binding);
                    return MixedSpaceMapProvisioningResult.Succeeded(binding, root);
                }

                await m_Library.EvictAsync(definition.Key);
                imported = false;
                CloseWorldIfUnused(openedWorld);
                openedWorld = null;
                CleanFailedProvision(binding, root, createdRoot);
                return MixedSpaceMapProvisioningResult.Failed();
            }
            catch (Exception exception)
            {
                MixedSpaceLogger.LogError(
                    $"Runtime map provisioning failed: {exception.Message}");

                if (imported && definition != null)
                    await m_Library.EvictAsync(definition.Key);
                CloseWorldIfUnused(openedWorld);
                CleanFailedProvision(binding, root, createdRoot);
                return MixedSpaceMapProvisioningResult.Failed();
            }
        }

        public async Task RemoveAsync(int mapKey, bool destroySceneView = false)
        {
            if (!m_Library.TryResolve(mapKey, out MixedSpaceMapRuntimeRecord record))
                return;

            await m_Localization.ApplyPlanAsync(
                new MixedSpaceLocalizationPlan(
                    removals:
                        new Dictionary<string, IReadOnlyList<MixedSpaceMapDefinition>>(
                            StringComparer.Ordinal)
                        {
                            { record.Definition.ProviderKey, new[] { record.Definition } }
                        }));

            MixedSpaceMapBinding binding = record.Binding;
            MixedSpaceWorldRoot root = record.Output?.View;
            IMixedSpaceWorldChannel world = record.Output;
            await m_Library.EvictAsync(mapKey);
            bool worldClosed = CloseWorldIfUnused(world);
            if (destroySceneView)
                DestroySceneView(binding, root, worldClosed);
        }

        public async Task ClearAsync(bool destroySceneViews = false)
        {
            IReadOnlyList<MixedSpaceMapDefinition> definitions = m_Library.SnapshotDefinitions();
            var removals =
                new Dictionary<string, IReadOnlyList<MixedSpaceMapDefinition>>(
                    StringComparer.Ordinal);

            foreach (IGrouping<string, MixedSpaceMapDefinition> group in
                definitions.GroupBy(definition => definition.ProviderKey))
            {
                removals[group.Key] = group.ToArray();
            }

            if (removals.Count > 0)
            {
                await m_Localization.ApplyPlanAsync(
                    new MixedSpaceLocalizationPlan(removals: removals));
            }

            MixedSpaceMapRuntimeRecord[] records = definitions
                .Select(definition =>
                    m_Library.TryResolve(definition.Key, out MixedSpaceMapRuntimeRecord record)
                        ? record
                        : null)
                .Where(record => record != null)
                .ToArray();
            IMixedSpaceWorldChannel[] worlds = m_Library.SnapshotOutputs().ToArray();
            await m_Library.ClearAsync();
            foreach (IMixedSpaceWorldChannel world in worlds)
                CloseWorldIfUnused(world);
            if (destroySceneViews)
                DestroySceneViews(records);
        }

        private bool CloseWorldIfUnused(IMixedSpaceWorldChannel world)
        {
            if (world == null)
                return false;

            bool isUsed = m_Library.SnapshotOutputs()
                .Any(candidate => ReferenceEquals(candidate, world));
            return !isUsed && m_Worlds.Close(world.View);
        }

        private static void DestroySceneViews(IEnumerable<MixedSpaceMapRuntimeRecord> records)
        {
            MixedSpaceMapRuntimeRecord[] snapshot = records?.ToArray() ??
                Array.Empty<MixedSpaceMapRuntimeRecord>();
            var destroyedRoots = new HashSet<MixedSpaceWorldRoot>();
            foreach (MixedSpaceWorldRoot root in snapshot
                .Select(record => record.Output?.View)
                .Where(root => root)
                .Distinct())
            {
                if (!CanDestroyWorldRoot(root))
                    continue;
                UnityEngine.Object.Destroy(root.gameObject);
                destroyedRoots.Add(root);
            }

            foreach (MixedSpaceMapRuntimeRecord record in snapshot)
            {
                MixedSpaceWorldRoot root = record.Output?.View;
                if (root && destroyedRoots.Contains(root))
                    continue;
                DestroyMapObject(record.Binding);
            }
        }

        private static void DestroySceneView(
            MixedSpaceMapBinding binding,
            MixedSpaceWorldRoot root,
            bool worldIsUnused)
        {
            if (worldIsUnused && root && CanDestroyWorldRoot(root))
            {
                UnityEngine.Object.Destroy(root.gameObject);
                return;
            }

            DestroyMapObject(binding);
        }

        private static void DestroyMapObject(MixedSpaceMapBinding binding)
        {
            if (!binding)
                return;
            MixedSpacePointCloudPresenter.Remove(binding);
            UnityEngine.Object.Destroy(binding.gameObject);
        }

        private static bool CanDestroyWorldRoot(MixedSpaceWorldRoot root)
        {
            if (!root)
                return false;

            foreach (Transform child in root.transform)
            {
                if (!child.GetComponent<MixedSpaceMapBinding>())
                    return false;
            }

            return true;
        }

        private static void CleanFailedProvision(
            MixedSpaceMapBinding binding,
            MixedSpaceWorldRoot root,
            bool createdRoot)
        {
            if (binding)
                MixedSpacePointCloudPresenter.Remove(binding);

            if (createdRoot && root)
            {
                UnityEngine.Object.Destroy(root.gameObject);
            }
            else if (binding)
            {
                UnityEngine.Object.Destroy(binding.gameObject);
            }
        }
    }

    public sealed class MixedSpaceMapRuntimeRecord
    {
        public MixedSpaceMapRuntimeRecord(
            MixedSpaceMapDefinition definition,
            MixedSpaceMapBinding binding,
            IMixedSpaceWorldChannel output,
            MixedSpaceMapPlacement placement)
        {
            Definition = definition ?? throw new ArgumentNullException(nameof(definition));
            Binding = binding;
            Output = output ?? throw new ArgumentNullException(nameof(output));
            Placement = placement ?? throw new ArgumentNullException(nameof(placement));
        }

        public MixedSpaceMapDefinition Definition { get; }
        public MixedSpaceMapBinding Binding { get; }
        public IMixedSpaceWorldChannel Output { get; }
        public MixedSpaceMapPlacement Placement { get; }
    }

    public sealed class MixedSpaceMapPlacement
    {
        public MixedSpaceMapPlacement(
            Vector3 translation,
            Quaternion orientation,
            Vector3 scale)
        {
            Translation = translation;
            Orientation = orientation;
            Scale = scale;
        }

        public Vector3 Translation { get; }
        public Quaternion Orientation { get; }
        public Vector3 Scale { get; }

        public Matrix4x4 Place(Matrix4x4 mapLocalPose)
        {
            Vector3 inPosition = mapLocalPose.GetPosition();
            Quaternion inRotation = mapLocalPose.rotation;
            Matrix4x4 offset = Matrix4x4.TRS(Translation, Orientation, Vector3.one);
            Vector3 scaledPosition = Vector3.Scale(inPosition, Scale);
            return offset * Matrix4x4.TRS(scaledPosition, inRotation, Vector3.one);
        }

        public Matrix4x4 RemovePlacement(Matrix4x4 placedPose)
        {
            Matrix4x4 intermediate =
                Matrix4x4.TRS(Translation, Orientation, Vector3.one).inverse * placedPose;
            return Matrix4x4.TRS(
                DivideComponents(intermediate.GetPosition(), Scale),
                intermediate.rotation,
                Vector3.one);
        }

        private static Vector3 DivideComponents(Vector3 value, Vector3 divisor)
        {
            return new Vector3(
                DivideComponent(value.x, divisor.x),
                DivideComponent(value.y, divisor.y),
                DivideComponent(value.z, divisor.z));
        }

        private static float DivideComponent(float value, float divisor)
        {
            if (Mathf.Approximately(divisor, 0f))
                return 0f;
            float quotient = value / divisor;
            return float.IsNaN(quotient) ? 0f : quotient;
        }
    }

    /// <summary>
    /// 动态准备使用的已验证声明，不包含提供器实例或传输 DTO。
    /// Validated declaration used by the dynamic provisioning use case. Provider
    /// instances and transport DTOs are intentionally excluded from this model.
    /// </summary>
    public sealed class MixedSpaceMapBlueprint
    {
        public MixedSpaceMapBlueprint(
            int key,
            string label,
            string providerId,
            MixedSpaceMapContentPolicy contentPolicy,
            TextAsset embeddedData = null,
            MixedSpaceMapAlignment alignment = default,
            MixedSpaceGeoOrigin geoOrigin = default,
            int accessLevel = 0,
            MixedSpaceWorldRoot worldRoot = null,
            bool applyRelationToTransform = false)
        {
            if (key <= 0)
                throw new ArgumentOutOfRangeException(nameof(key), "Map key must be positive.");
            if (string.IsNullOrWhiteSpace(providerId))
                throw new ArgumentException("A provider id is required.", nameof(providerId));

            Key = key;
            Label = string.IsNullOrWhiteSpace(label) ? $"Map {key}" : label;
            ProviderId = providerId;
            ContentPolicy = contentPolicy ??
                throw new ArgumentNullException(nameof(contentPolicy));
            EmbeddedData = embeddedData;
            Alignment = NormalizeAlignment(alignment);
            GeoOrigin = geoOrigin;
            AccessLevel = accessLevel;
            WorldRoot = worldRoot;
            ApplyRelationToTransform = applyRelationToTransform;
        }

        public int Key { get; }
        public string Label { get; }
        public string ProviderId { get; }
        public MixedSpaceMapContentPolicy ContentPolicy { get; }
        public TextAsset EmbeddedData { get; }
        public MixedSpaceMapAlignment Alignment { get; }
        public MixedSpaceGeoOrigin GeoOrigin { get; }
        public int AccessLevel { get; }
        public MixedSpaceWorldRoot WorldRoot { get; }
        public bool ApplyRelationToTransform { get; }

        public static MixedSpaceMapBlueprint FromCloud(
            MixedSpaceCloudMapDescriptor descriptor,
            string providerId,
            MixedSpaceMapContentPolicy contentPolicy,
            TextAsset embeddedData = null,
            MixedSpaceWorldRoot worldRoot = null,
            bool applyRelationToTransform = false)
        {
            if (descriptor == null)
                throw new ArgumentNullException(nameof(descriptor));

            return new MixedSpaceMapBlueprint(
                descriptor.Key,
                descriptor.Label,
                providerId,
                contentPolicy,
                embeddedData,
                descriptor.Alignment,
                descriptor.GeoOrigin,
                descriptor.AccessLevel,
                worldRoot,
                applyRelationToTransform);
        }

        private static MixedSpaceMapAlignment NormalizeAlignment(
            MixedSpaceMapAlignment alignment)
        {
            if (Math.Abs(alignment.qw) < double.Epsilon &&
                Math.Abs(alignment.qx) < double.Epsilon &&
                Math.Abs(alignment.qy) < double.Epsilon &&
                Math.Abs(alignment.qz) < double.Epsilon)
            {
                alignment.qw = 1.0;
            }
            if (Math.Abs(alignment.scale) < double.Epsilon)
                alignment.scale = 1.0;
            return alignment;
        }
    }

    public sealed class MixedSpaceMapProvisioningResult
    {
        private MixedSpaceMapProvisioningResult(
            bool success,
            MixedSpaceMapBinding binding,
            MixedSpaceWorldRoot worldRoot)
        {
            Success = success;
            Binding = binding;
            WorldRoot = worldRoot;
        }

        public bool Success { get; }
        public MixedSpaceMapBinding Binding { get; }
        public MixedSpaceWorldRoot WorldRoot { get; }

        internal static MixedSpaceMapProvisioningResult Succeeded(
            MixedSpaceMapBinding binding,
            MixedSpaceWorldRoot worldRoot)
        {
            return new MixedSpaceMapProvisioningResult(true, binding, worldRoot);
        }

        internal static MixedSpaceMapProvisioningResult Failed()
        {
            return new MixedSpaceMapProvisioningResult(false, null, null);
        }
    }
}
