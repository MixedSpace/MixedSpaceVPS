using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace MixedSpace
{
    /// <summary>
    /// 云操作的 HTTP 流边界，可替换为证书固定、企业代理或原生 HarmonyOS 实现。
    /// HTTP streaming boundary used by cloud operations. Platform packages can replace it
    /// for certificate pinning, enterprise proxies or a native HarmonyOS stack.
    /// </summary>
    public interface IMixedSpaceHttpTransport
    {
        Task<HttpResponseMessage> DownloadAsync(
            HttpRequestMessage request,
            Stream destination,
            IProgress<float> progress = null,
            CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// 默认 System.Net.Http 传输，其 HttpClient 生命周期由创建它的 SDK 实例持有。
    /// Default System.Net.Http implementation. HttpClient lifetime is owned by
    /// the MixedSpaceSDK instance that constructs this transport.
    /// </summary>
    public sealed class SystemMixedSpaceHttpTransport : IMixedSpaceHttpTransport
    {
        private readonly HttpClient m_Client;

        public SystemMixedSpaceHttpTransport(HttpClient client)
        {
            m_Client = client ?? throw new ArgumentNullException(nameof(client));
        }

        public async Task<HttpResponseMessage> DownloadAsync(
            HttpRequestMessage request,
            Stream destination,
            IProgress<float> progress = null,
            CancellationToken cancellationToken = default)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (destination == null) throw new ArgumentNullException(nameof(destination));

            HttpResponseMessage response;
            var timer = Stopwatch.StartNew();
            try
            {
                response = await m_Client.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);
            }
            catch (Exception error)
            {
                // 只记录类型与耗时；异常信息和 URL 可能包含凭据。
                // Log types and timing only. Exception messages and URLs may contain credentials.
                MixedSpaceLogger.LogWarning(
                    $"Cloud HTTP failed before response headers: type={error.GetType().Name} inner={error.InnerException?.GetType().Name ?? "none"} elapsed={timer.Elapsed.TotalSeconds:F1}s cancelled={cancellationToken.IsCancellationRequested}.");
                throw;
            }
            finally
            {
                // 云操作将请求所有权移交给传输；即使 DNS、TLS 或取消发生在响应前，也要释放请求。
                // Every cloud operation transfers request ownership to the transport. Dispose even when DNS, TLS or cancellation
                // fails before a response is available.
                request.Dispose();
            }

            try
            {
                long? contentLength = response.Content.Headers.ContentLength;
                using (Stream download = await response.Content.ReadAsStreamAsync())
                {
                    if (progress == null || !contentLength.HasValue || contentLength.Value <= 0)
                    {
                        await CopyStreamAsync(download, destination, null, cancellationToken);
                    }
                    else
                    {
                        var byteProgress = new Progress<long>(bytes =>
                            progress.Report((float)bytes / contentLength.Value));
                        await CopyStreamAsync(download, destination, byteProgress, cancellationToken);
                    }
                }

                return response;
            }
            catch (Exception error)
            {
                MixedSpaceLogger.LogWarning(
                    $"Cloud HTTP failed while reading response: type={error.GetType().Name} inner={error.InnerException?.GetType().Name ?? "none"} elapsed={timer.Elapsed.TotalSeconds:F1}s cancelled={cancellationToken.IsCancellationRequested}.");
                response.Dispose();
                throw;
            }
        }

        private static async Task CopyStreamAsync(
            Stream source,
            Stream destination,
            IProgress<long> progress,
            CancellationToken cancellationToken)
        {
            var buffer = new byte[81920];
            long totalBytesRead = 0;
            int bytesRead;
            while ((bytesRead = await source.ReadAsync(
                       buffer,
                       0,
                       buffer.Length,
                       cancellationToken).ConfigureAwait(false)) != 0)
            {
                await destination.WriteAsync(
                    buffer,
                    0,
                    bytesRead,
                    cancellationToken).ConfigureAwait(false);
                totalBytesRead += bytesRead;
                progress?.Report(totalBytesRead);
            }
        }
    }
}
