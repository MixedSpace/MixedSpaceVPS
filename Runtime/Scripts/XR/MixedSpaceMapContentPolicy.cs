using System;

namespace MixedSpace.XR
{
    /// <summary>
    /// 声明地图二进制数据的来源。ProviderManaged 表示主导入路径不要求本地原生地图数据。
    /// 云模式仍可通过独立的离线准备流程加载原生数据，以支持本地回退。
    /// Declares the source of map binary data. ProviderManaged means the main import path requires no
    /// local native data; Cloud mode may still prepare it separately for offline fallback.
    /// </summary>
    public enum MixedSpaceMapContentLocation
    {
        ProviderManaged = 0,
        EmbeddedAsset = 1,
        RemoteBinary = 2,
        InMemory = 3
    }

    /// <summary>
    /// 单地图不可变数据策略，明确二进制来源、运行时数据和预览选项。
    /// Immutable per-map policy defining binary data location, runtime payload and preview options.
    /// </summary>
    public sealed class MixedSpaceMapContentPolicy
    {
        private readonly byte[] m_RuntimePayload;

        public MixedSpaceMapContentPolicy(
            MixedSpaceMapContentLocation location,
            bool presentPreviewAtRuntime = false,
            byte[] runtimePayload = null)
        {
            if (location == MixedSpaceMapContentLocation.InMemory &&
                (runtimePayload == null || runtimePayload.Length == 0))
            {
                throw new ArgumentException(
                    "In-memory map content requires a non-empty payload.",
                    nameof(runtimePayload));
            }
            if (location != MixedSpaceMapContentLocation.InMemory &&
                runtimePayload is { Length: > 0 })
            {
                throw new ArgumentException(
                    "A runtime payload is only valid for in-memory map content.",
                    nameof(runtimePayload));
            }

            Location = location;
            PresentPreviewAtRuntime = presentPreviewAtRuntime;
            m_RuntimePayload = runtimePayload == null
                ? null
                : (byte[])runtimePayload.Clone();
        }

        public MixedSpaceMapContentLocation Location { get; }
        public bool PresentPreviewAtRuntime { get; }

        internal byte[] CopyRuntimePayload()
        {
            return m_RuntimePayload == null
                ? null
                : (byte[])m_RuntimePayload.Clone();
        }

        public static MixedSpaceMapContentPolicy ProviderManaged(
            bool presentPreviewAtRuntime = false)
        {
            return new MixedSpaceMapContentPolicy(
                MixedSpaceMapContentLocation.ProviderManaged,
                presentPreviewAtRuntime);
        }

        public static MixedSpaceMapContentPolicy Embedded(
            bool presentPreviewAtRuntime = false)
        {
            return new MixedSpaceMapContentPolicy(
                MixedSpaceMapContentLocation.EmbeddedAsset,
                presentPreviewAtRuntime);
        }

        public static MixedSpaceMapContentPolicy Remote(
            bool presentPreviewAtRuntime = false)
        {
            return new MixedSpaceMapContentPolicy(
                MixedSpaceMapContentLocation.RemoteBinary,
                presentPreviewAtRuntime);
        }

        public static MixedSpaceMapContentPolicy FromMemory(
            byte[] payload,
            bool presentPreviewAtRuntime = false)
        {
            return new MixedSpaceMapContentPolicy(
                MixedSpaceMapContentLocation.InMemory,
                presentPreviewAtRuntime,
                payload);
        }
    }
}
