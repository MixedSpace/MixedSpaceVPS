using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MixedSpace.XR;

namespace MixedSpace
{
    public enum MixedSpaceMapLibraryChangeKind
    {
        Imported,
        ImportFailed,
        Evicted
    }

    public sealed class MixedSpaceMapLibraryChange
    {
        public MixedSpaceMapLibraryChangeKind Kind { get; }
        public int MapKey { get; }

        public MixedSpaceMapLibraryChange(
            MixedSpaceMapLibraryChangeKind kind,
            int mapKey)
        {
            Kind = kind;
            MapKey = mapKey;
        }
    }

    /// <summary>
    /// 运行时地图库接收完整导入请求并持有原生数据生命周期，不创建 GameObject，也不配置提供器。
    /// Runtime map library. It accepts complete import requests and owns native
    /// data lifetime; it does not create GameObjects or configure providers.
    /// </summary>
    public interface IMixedSpaceMapRepository
    {
        event Action<MixedSpaceMapLibraryChange> Changed;

        bool HasMaps { get; }

        Task<bool> ImportAsync(MixedSpaceMapImport request);
        Task<bool> SupplyNativeDataAsync(int mapKey, byte[] bytes);

        IReadOnlyList<MixedSpaceMapDefinition> SnapshotDefinitions();
        IReadOnlyList<IMixedSpaceWorldChannel> SnapshotOutputs();

        bool TryResolve(int mapKey, out MixedSpaceMapRuntimeRecord record);

        Task EvictAsync(int mapKey);
        Task ClearAsync();

        void ReleaseNativeState();
    }

    public interface IMixedSpaceMapRepositoryConsumer
    {
        void SetMapRepository(IMixedSpaceMapRepository repository);
    }
}
