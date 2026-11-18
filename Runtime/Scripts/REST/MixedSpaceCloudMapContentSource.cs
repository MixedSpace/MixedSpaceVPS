using System;
using System.Threading;
using System.Threading.Tasks;
using MixedSpace.ServiceContracts;
using MixedSpace.XR;

namespace MixedSpace.REST
{
    /// <summary>
    /// 默认 HTTP 地图附属数据源，URL 和认证细节不进入地图仓库；状态属于单一 SDK 实例。
    /// Default HTTP-backed map artifact source. URL/authentication details stay
    /// outside the map repository and all state belongs to one SDK instance.
    /// </summary>
    public sealed class MixedSpaceCloudMapContentSource : IMixedSpaceMapContentSource
    {
        private readonly IMixedSpaceCloudContext m_Context;

        public MixedSpaceCloudMapContentSource(IMixedSpaceCloudContext context)
        {
            m_Context = context ?? throw new ArgumentNullException(nameof(context));
        }

        public async Task<byte[]> DownloadBinaryAsync(
            int mapKey,
            CancellationToken cancellationToken = default)
        {
            byte[] data = await m_Context.Client.GetAsync(
                BuildUri(SDKMapBinaryRequest.endpoint, mapKey),
                cancellationToken: cancellationToken);
            return ValidateResponse(data, mapKey, "map binary");
        }

        public async Task<byte[]> DownloadPointCloudAsync(
            int mapKey,
            CancellationToken cancellationToken = default)
        {
            byte[] data = await m_Context.Client.GetAsync(
                BuildUri(SDKSparseDownloadRequest.endpoint, mapKey),
                cancellationToken: cancellationToken);
            return ValidateResponse(
                data,
                mapKey,
                "point cloud");
        }

        private string BuildUri(string endpoint, int mapKey)
        {
            if (MixedSpaceMapDefinition.IsRetiredKey(mapKey))
                throw new ArgumentOutOfRangeException(nameof(mapKey), mapKey, "Four-digit maps are retired.");

            string server = (m_Context.LocalizationServer ?? string.Empty).TrimEnd('/');
            string token = m_Context.DeveloperToken;
            string query = string.IsNullOrEmpty(token)
                ? $"id={mapKey}"
                : $"token={Uri.EscapeDataString(token)}&id={mapKey}";
            return $"{server}/{endpoint}?{query}";
        }

        private static byte[] ValidateResponse(
            byte[] data,
            int mapKey,
            string artifactName)
        {
            if (data == null || data.Length == 0)
            {
                MixedSpaceLogger.LogError(
                    $"Cloud returned no {artifactName} data for map {mapKey}.");
                return null;
            }

            int first = 0;
            while (first < data.Length && char.IsWhiteSpace((char)data[first]))
                first++;
            if (first >= data.Length || data[first] != (byte)'{')
                return data;

            MixedSpaceLogger.LogError(
                $"Cloud returned an error instead of {artifactName} data for " +
                $"map {mapKey}.");
            return null;
        }
    }
}
