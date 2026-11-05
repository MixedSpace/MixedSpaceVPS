using System;

namespace MixedSpace.REST
{
    public enum MixedSpaceCloudFailureKind { Http, Transport, Timeout, Decode }

    /// <summary>
    /// 安全的云失败元数据，不保留原始 URI、正文、请求头或内部异常，以免泄露凭据。HTTP 200 且 success=false 仍是正常未命中，不属此异常。
    /// Safe cloud failure metadata. No raw URI, response body, headers or inner
    /// exception is retained: any of those can contain application credentials.
    /// HTTP 200 with success=false remains a normal localization miss, not this exception.
    /// </summary>
    public sealed class MixedSpaceCloudRequestException : Exception
    {
        public MixedSpaceCloudFailureKind Kind { get; }
        public int? StatusCode { get; }
        public string Operation { get; }
        internal MixedSpaceCloudRequestException(MixedSpaceCloudFailureKind kind, string operation, int? status = null)
            : base("Cloud " + operation + " failed: " + kind + (status.HasValue ? " HTTP " + status.Value : "") + ".")
        { Kind = kind; Operation = operation; StatusCode = status; }
    }
}
