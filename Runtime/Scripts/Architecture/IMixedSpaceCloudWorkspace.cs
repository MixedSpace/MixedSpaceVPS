using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MixedSpace.XR;

namespace MixedSpace
{
    /// <summary>
    /// 不可变的云地图搜索条件，包含可选的地理位置筛选。
    /// Immutable cloud map search criteria with optional geographic filtering.
    /// </summary>
    public sealed class MixedSpaceCloudMapSearch
    {
        private MixedSpaceCloudMapSearch(
            bool useGeographicFilter,
            double latitude,
            double longitude,
            double radius,
            bool authenticate)
        {
            UseGeographicFilter = useGeographicFilter;
            Latitude = latitude;
            Longitude = longitude;
            Radius = radius;
            Authenticate = authenticate;
        }

        public bool UseGeographicFilter { get; }
        public double Latitude { get; }
        public double Longitude { get; }
        public double Radius { get; }
        public bool Authenticate { get; }

        public static MixedSpaceCloudMapSearch AllAccessible(bool authenticate = true)
        {
            return new MixedSpaceCloudMapSearch(false, 0d, 0d, 0d, authenticate);
        }

        public static MixedSpaceCloudMapSearch Near(
            double latitude,
            double longitude,
            double radius,
            bool authenticate = true)
        {
            if (radius <= 0d)
                throw new ArgumentOutOfRangeException(nameof(radius), "Search radius must be positive.");

            return new MixedSpaceCloudMapSearch(
                true,
                latitude,
                longitude,
                radius,
                authenticate);
        }
    }

    public sealed class MixedSpaceCloudMapSummary
    {
        internal MixedSpaceCloudMapSummary(int key, string label)
        {
            Key = key;
            Label = string.IsNullOrWhiteSpace(label) ? $"Map {key}" : label;
        }

        public int Key { get; }
        public string Label { get; }
    }

    /// <summary>
    /// 经验证的地图上传输入，内部持有载荷，避免异步上传期间被调用方修改。
    /// Validated map upload input. Payload ownership is kept inside the value so
    /// callers cannot modify bytes while an asynchronous upload is in progress.
    /// </summary>
    public sealed class MixedSpaceCloudMapUpload
    {
        private readonly byte[] m_Payload;

        public MixedSpaceCloudMapUpload(
            string label,
            byte[] payload,
            double latitude = 0d,
            double longitude = 0d,
            double altitude = 0d)
        {
            if (string.IsNullOrWhiteSpace(label))
                throw new ArgumentException("A map label is required.", nameof(label));
            if (payload == null || payload.Length == 0)
                throw new ArgumentException("Map payload cannot be empty.", nameof(payload));

            Label = label.Trim();
            Latitude = latitude;
            Longitude = longitude;
            Altitude = altitude;
            m_Payload = (byte[])payload.Clone();
        }

        public string Label { get; }
        public double Latitude { get; }
        public double Longitude { get; }
        public double Altitude { get; }

        internal byte[] CopyPayload()
        {
            return (byte[])m_Payload.Clone();
        }
    }

    public sealed class MixedSpaceCloudMapReceipt
    {
        internal MixedSpaceCloudMapReceipt(int mapKey)
        {
            MapKey = mapKey;
        }

        public int MapKey { get; }
    }

    public sealed class MixedSpaceCloudException : Exception
    {
        public MixedSpaceCloudException(string operation, string serviceError)
            : base($"MixedSpace cloud operation '{operation}' failed: " +
                   (string.IsNullOrWhiteSpace(serviceError) ? "connection failure" : serviceError))
        {
            Operation = operation;
            ServiceError = serviceError ?? string.Empty;
        }

        public string Operation { get; }
        public string ServiceError { get; }
    }

    /// <summary>
    /// 应用层云功能边界，不向应用暴露传输 DTO、回调或可变任务对象。
    /// Application-facing cloud use cases. Transport request DTOs, callbacks and
    /// mutable job objects do not cross this boundary.
    /// </summary>
    public interface IMixedSpaceCloudWorkspace
    {
        Task<int> GetLicenseLevelAsync(
            IProgress<float> progress = null,
            CancellationToken cancellationToken = default);

        Task<IReadOnlyList<MixedSpaceCloudMapSummary>> FindLoadableMapsAsync(
            MixedSpaceCloudMapSearch search = null,
            IProgress<float> progress = null,
            CancellationToken cancellationToken = default);

        Task<MixedSpaceCloudMapDescriptor> DescribeMapAsync(
            int mapKey,
            IProgress<float> progress = null,
            CancellationToken cancellationToken = default);

        Task<MixedSpaceCloudMapReceipt> UploadMapAsync(
            MixedSpaceCloudMapUpload upload,
            IProgress<float> progress = null,
            CancellationToken cancellationToken = default);
    }

}
