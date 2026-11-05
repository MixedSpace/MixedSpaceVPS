using System.Threading;
using System.Threading.Tasks;

namespace MixedSpace
{
    /// <summary>
    /// 提供可选的运行时地图附属数据，不向地图仓库暴露 HTTP 任务或全局 SDK 实例。
    /// Supplies optional runtime map artifacts without exposing HTTP jobs or a
    /// global SDK instance to the map repository.
    /// </summary>
    public interface IMixedSpaceMapContentSource
    {
        Task<byte[]> DownloadBinaryAsync(
            int mapKey,
            CancellationToken cancellationToken = default);

        Task<byte[]> DownloadPointCloudAsync(
            int mapKey,
            CancellationToken cancellationToken = default);
    }
}
