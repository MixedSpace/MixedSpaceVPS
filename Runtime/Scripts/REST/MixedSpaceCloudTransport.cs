using System;
using System.Threading;
using System.Threading.Tasks;

namespace MixedSpace.REST
{
    /// <summary>
    /// 强类型云操作边界，委托请求构造、编码及响应生命周期，不保留或记录请求载荷。
    /// Typed cloud operation boundary. Request construction, wire encoding and
    /// response lifetime are delegated; no request payload is retained or logged.
    /// </summary>
    public sealed class MixedSpaceCloudClient : IMixedSpaceCloudClient
    {
        public const string UrlFormat = MixedSpaceCloudRequestFactory.UrlFormat;
        private readonly MixedSpaceCloudRequestFactory m_Requests;
        private readonly MixedSpaceCloudExchange m_Exchange;
        private readonly IMixedSpaceCloudCodec m_Codec;

        public MixedSpaceCloudClient(IMixedSpaceNetworkEnvironment networkEnvironment, IMixedSpaceHttpTransport transport)
            : this(networkEnvironment, transport, new MixedSpaceUnityCloudCodec(), new MixedSpaceCloudDiagnostics()) { }

        internal MixedSpaceCloudClient(IMixedSpaceNetworkEnvironment environment, IMixedSpaceHttpTransport transport,
            IMixedSpaceCloudCodec codec, IMixedSpaceCloudDiagnostics diagnostics)
        {
            m_Codec = codec ?? throw new ArgumentNullException(nameof(codec));
            m_Requests = new MixedSpaceCloudRequestFactory(environment, codec);
            m_Exchange = new MixedSpaceCloudExchange(transport, diagnostics);
        }

        // 保留异步包装，使构造或编码错误进入返回任务，与既有 API 一致，而非同步抛出。
        // Keep async wrappers: construction/encoding failures fault the returned Task, as in the existing API, rather
        // than throwing synchronously.
        public async Task<TResponse> PostAsync<TRequest, TResponse>(TRequest request,
            IProgress<float> progress = null, CancellationToken cancellationToken = default, string baseUrl = null) =>
            await m_Exchange.ExecuteAsync(m_Requests.Post(request, baseUrl), m_Codec.Decode<TResponse>,
                progress, cancellationToken, MixedSpaceCloudOperation.Post);

        public async Task<byte[]> GetAsync(string uri, IProgress<float> progress = null,
            CancellationToken cancellationToken = default) =>
            await m_Exchange.ExecuteAsync(m_Requests.Get(uri), bytes => bytes,
                progress, cancellationToken, MixedSpaceCloudOperation.Download);

        public async Task<TResponse> UploadAsync<TRequest, TResponse>(TRequest request, byte[] data,
            IProgress<float> progress = null, CancellationToken cancellationToken = default) =>
            await m_Exchange.ExecuteAsync(m_Requests.Upload(request, data, progress), m_Codec.Decode<TResponse>,
                null, cancellationToken, MixedSpaceCloudOperation.Upload);
    }
}
