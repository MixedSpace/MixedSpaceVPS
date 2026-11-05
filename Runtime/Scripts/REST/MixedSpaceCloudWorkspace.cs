using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MixedSpace.ServiceContracts;
using MixedSpace.XR;

namespace MixedSpace.REST
{
    /// <summary>
    /// 应用云工作区的 REST 实现，是领域值转换为端点 DTO 的唯一层。
    /// REST implementation of the application cloud workspace. This adapter is
    /// the only layer that translates MixedSpace domain values to endpoint DTOs.
    /// </summary>
    internal sealed class MixedSpaceCloudWorkspace : IMixedSpaceCloudWorkspace
    {
        private readonly IMixedSpaceCloudContext m_Context;

        public MixedSpaceCloudWorkspace(IMixedSpaceCloudContext context)
        {
            m_Context = context ?? throw new ArgumentNullException(nameof(context));
        }

        public async Task<int> GetLicenseLevelAsync(
            IProgress<float> progress = null,
            CancellationToken cancellationToken = default)
        {
            var request = new SDKStatusRequest { token = m_Context.DeveloperToken };
            SDKStatusResult result = await m_Context.Client
                .PostAsync<SDKStatusRequest, SDKStatusResult>(
                    request,
                    progress,
                    cancellationToken,
                    m_Context.LocalizationServer);
            EnsureSuccess("license status", result.error);
            return result.level;
        }

        public async Task<IReadOnlyList<MixedSpaceCloudMapSummary>> FindLoadableMapsAsync(
            MixedSpaceCloudMapSearch search = null,
            IProgress<float> progress = null,
            CancellationToken cancellationToken = default)
        {
            search ??= MixedSpaceCloudMapSearch.AllAccessible();
            SDKJobsResult result;
            string token = search.Authenticate ? m_Context.DeveloperToken : string.Empty;
            if (search.UseGeographicFilter)
            {
                var request = new SDKGeoJobsRequest
                {
                    token = token,
                    latitude = search.Latitude,
                    longitude = search.Longitude,
                    radius = search.Radius
                };
                result = await m_Context.Client.PostAsync<SDKGeoJobsRequest, SDKJobsResult>(
                    request,
                    progress,
                    cancellationToken,
                    m_Context.LocalizationServer);
            }
            else
            {
                var request = new SDKJobsRequest { token = token };
                result = await m_Context.Client.PostAsync<SDKJobsRequest, SDKJobsResult>(
                    request,
                    progress,
                    cancellationToken,
                    m_Context.LocalizationServer);
            }

            EnsureSuccess("map directory", result.error);
            if (result.jobs == null)
                return Array.Empty<MixedSpaceCloudMapSummary>();

            return result.jobs
                .Where(job =>
                    job.type != (int)SDKJobType.Alignment &&
                    (job.status == SDKJobState.Sparse || job.status == SDKJobState.Done))
                .Select(job => new MixedSpaceCloudMapSummary(job.id, job.name))
                .ToArray();
        }

        public async Task<MixedSpaceCloudMapDescriptor> DescribeMapAsync(
            int mapKey,
            IProgress<float> progress = null,
            CancellationToken cancellationToken = default)
        {
            if (mapKey <= 0)
                throw new ArgumentOutOfRangeException(nameof(mapKey), "Map key must be positive.");

            var request = new SDKMapMetadataGetRequest
            {
                token = m_Context.DeveloperToken,
                id = mapKey
            };
            SDKMapMetadataGetResult result = await m_Context.Client
                .PostAsync<SDKMapMetadataGetRequest, SDKMapMetadataGetResult>(
                    request,
                    progress,
                    cancellationToken,
                    m_Context.LocalizationServer);
            EnsureSuccess("map description", result.error);
            return MixedSpaceCloudMapRecordAdapter.ToDomain(result);
        }

        public async Task<MixedSpaceCloudMapReceipt> UploadMapAsync(
            MixedSpaceCloudMapUpload upload,
            IProgress<float> progress = null,
            CancellationToken cancellationToken = default)
        {
            if (upload == null)
                throw new ArgumentNullException(nameof(upload));

            var request = new SDKMapUploadRequest
            {
                token = m_Context.DeveloperToken,
                name = upload.Label,
                latitude = upload.Latitude,
                longitude = upload.Longitude,
                altitude = upload.Altitude
            };
            SDKMapUploadResult result = await m_Context.Client
                .UploadAsync<SDKMapUploadRequest, SDKMapUploadResult>(
                    request,
                    upload.CopyPayload(),
                    progress,
                    cancellationToken);
            EnsureSuccess("map upload", result.error);
            return new MixedSpaceCloudMapReceipt(result.id);
        }

        private static void EnsureSuccess(string operation, string error)
        {
            if (!string.Equals(error, "none", StringComparison.OrdinalIgnoreCase))
                throw new MixedSpaceCloudException(operation, error);
        }
    }
}
