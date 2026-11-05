using System;
using System.Threading;
using System.Threading.Tasks;

namespace MixedSpace
{
    /// <summary>
    /// 每个 SDK 实例独占的强类型云请求边界。HTTP、传输和解码失败抛出 REST.MixedSpaceCloudRequestException，调用方取消抛出
    /// OperationCanceledException，不以默认响应掩盖失败。
    /// Typed cloud request boundary owned by one MixedSpace SDK instance.
    /// HTTP, transport and decoding failures throw REST.MixedSpaceCloudRequestException;
    /// caller cancellation propagates as OperationCanceledException. They are not default responses.
    /// </summary>
    public interface IMixedSpaceCloudClient
    {
        Task<TResponse> PostAsync<TRequest, TResponse>(
            TRequest request,
            IProgress<float> progress = null,
            CancellationToken cancellationToken = default,
            string baseUrl = null);

        Task<byte[]> GetAsync(
            string uri,
            IProgress<float> progress = null,
            CancellationToken cancellationToken = default);

        Task<TResponse> UploadAsync<TRequest, TResponse>(
            TRequest request,
            byte[] data,
            IProgress<float> progress = null,
            CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// 核心云服务使用的实例级凭据和端点上下文，避免查找场景 SDK 单例。
    /// Instance-scoped credentials and endpoint context used by core cloud
    /// services. It prevents those services from resolving the scene SDK singleton.
    /// </summary>
    public interface IMixedSpaceCloudContext
    {
        IMixedSpaceCloudClient Client { get; }
        string DeveloperToken { get; }
        string LocalizationServer { get; }
    }

    public sealed class MixedSpaceCloudContext : IMixedSpaceCloudContext
    {
        private readonly Func<IMixedSpaceCloudClient> m_ClientAccessor;
        private readonly Func<string> m_TokenAccessor;
        private readonly Func<IMixedSpaceNetworkEnvironment> m_EnvironmentAccessor;

        public MixedSpaceCloudContext(
            Func<IMixedSpaceCloudClient> clientAccessor,
            Func<IMixedSpaceNetworkEnvironment> environmentAccessor,
            Func<string> tokenAccessor)
        {
            m_ClientAccessor = clientAccessor ??
                throw new ArgumentNullException(nameof(clientAccessor));
            m_EnvironmentAccessor = environmentAccessor ??
                throw new ArgumentNullException(nameof(environmentAccessor));
            m_TokenAccessor = tokenAccessor ?? throw new ArgumentNullException(nameof(tokenAccessor));
        }

        public IMixedSpaceCloudClient Client => m_ClientAccessor() ??
            throw new InvalidOperationException("MixedSpace cloud client is unavailable.");
        public string DeveloperToken => m_TokenAccessor() ?? string.Empty;
        public string LocalizationServer => m_EnvironmentAccessor()?.LocalizationServer ??
            string.Empty;
    }
}
