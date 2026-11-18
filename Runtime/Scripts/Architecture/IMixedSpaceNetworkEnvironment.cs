using System;
using System.Net.Http;

namespace MixedSpace
{
    /// <summary>
    /// 提供令牌验证、连接提示、端点和认证请求构造，使 SDK 不绑定某种网络实现。
    /// Provides token validation, connectivity, endpoints and authenticated request
    /// construction without coupling the SDK to one networking implementation.
    /// </summary>
    public interface IMixedSpaceNetworkEnvironment
    {
        bool IsNetworkAvailable { get; }
        string LocalizationServer { get; }
        string DownloadDirectory { get; }
        void ConfigureMobilePlatform(bool enabled);
        void ValidateToken(string developerToken, Action<bool> completion);
        HttpRequestMessage CreateUploadRequest(string endpoint);
    }

    /// <summary>
    /// 默认网络环境使用当前令牌与端点提供器，可在 SDK 初始化前替换。
    /// Default MixedSpace network environment backed by the current token and
    /// endpoint provider. It is replaceable before SDK initialization.
    /// </summary>
    public sealed class MixedSpaceNetworkEnvironment : IMixedSpaceNetworkEnvironment
    {
        // 网络可达性只是连接提示，并不证明授权成功。
        // Reachability is a connectivity hint, not proof of successful authorization.
        public bool IsNetworkAvailable => UnityEngine.Application.internetReachability != UnityEngine.NetworkReachability.NotReachable;
        public const string DefaultMapServer = "https://vps.njyjxr.com:18444/vpssdk/vps/proxy";
        public const string DefaultLocalizeServer = "https://vps.njyjxr.com:18444/vpssdk/vps/limitRatesVpslocalize";
        
        private const string LegacyTestMapServer = "http://192.168.1.170/vps/proxy";
        private const string LegacyTestLocalizeServer = "http://192.168.1.170/vps/limitRatesVpslocalize";
        
        private readonly Func<string> m_MapServer;
        private readonly Func<string> m_LocalizeServer;
        private readonly Func<int> m_XrLocServerId;
        public string LocalizationServer => ValidateServer(m_MapServer()).AbsoluteUri.TrimEnd('/');
        public string DownloadDirectory => "Map Data";

        /// <summary>
        /// 每次使用时延迟读取服务器来源，使 Inspector 修改无需域重载即可生效；空委托回退到默认值，支持无参构造。
        /// Server sources are read lazily on every use so Inspector edits take
        /// effect without a domain reload. Null delegates fall back to defaults,
        /// which keeps parameterless construction working.
        /// </summary>
        public MixedSpaceNetworkEnvironment(
            Func<string> mapServer = null,
            Func<string> localizeServer = null,
            Func<int> xrLocServerId = null)
        {
            m_XrLocServerId = xrLocServerId ?? (() => (int)MixedSpaceXrLocServer.Production);
            m_MapServer = () => ResolveServer(mapServer?.Invoke(), m_XrLocServerId(), false);
            m_LocalizeServer = () => ResolveServer(localizeServer?.Invoke(), m_XrLocServerId(), true);
        }

        public static string ResolveServer(string configured, int serverId, bool localize)
        {
            string production = localize ? DefaultLocalizeServer : DefaultMapServer;
            string test = localize ? LegacyTestLocalizeServer : LegacyTestMapServer;
            string remoteTest = localize
                ? "http://61.169.107.43:1841/vps/limitRatesVpslocalize"
                : "http://61.169.107.43:1841/vps/proxy";
            if (serverId != 0 && serverId != 1)
                throw new ArgumentOutOfRangeException(nameof(serverId), "SDK network requests use Production (0).");
            string value = configured?.Trim().TrimEnd('/');
            if (string.IsNullOrEmpty(value) || value == production || value == test || value == remoteTest)
                return production;
            return configured;
        }

        private static Uri ValidateServer(string value)
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
                !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment))
                throw new ArgumentException("Server must be an absolute HTTP(S) URL without credentials or fragments.");
            return uri;
        }

        public void ConfigureMobilePlatform(bool enabled)
        {
            // 供自定义网络环境使用的平台配置钩子；默认实现无状态。
            // Platform configuration hook for custom network environments; the default implementation is stateless.
        }

        public void ValidateToken(string developerToken, Action<bool> completion)
        {
            if (completion == null)
                throw new ArgumentNullException(nameof(completion));

            completion(MixedSpaceNativeAuthorization.Validate(developerToken));
        }

        public HttpRequestMessage CreateUploadRequest(string endpoint)
        {
            MixedSpaceUploadRoute.ValidateEndpoint(endpoint);

            // 只有定位使用专用网关，其他上传端点与强类型 POST 操作共用 proxy 命名空间。
            // Only localization uses the dedicated gateway. Other upload endpoints belong to the same proxy namespace as
            // typed POST operations.
            if (endpoint != "localize")
                return new HttpRequestMessage(HttpMethod.Post,
                    MixedSpaceUploadRoute.ProxyEndpoint(LocalizationServer, endpoint));

            // 使用完整网关 URL，禁止重复追加 /localize 路径。
            // Full gateway URL; never append another /localize segment on iOS or Android.
            return new HttpRequestMessage(HttpMethod.Post, ValidateServer(m_LocalizeServer()));
        }
    }

    /// <summary>
    /// 纯请求路由，不记录凭据，也不发送网络请求。
    /// Pure request routing; never logs credentials or sends a request.
    /// </summary>
    internal static class MixedSpaceUploadRoute
    {
        internal static void ValidateEndpoint(string endpoint)
        {
            if (string.IsNullOrWhiteSpace(endpoint)) throw new ArgumentException("Upload endpoint is required.");
            foreach (char c in endpoint)
                if (!(c >= 'a' && c <= 'z') && !(c >= 'A' && c <= 'Z') &&
                    !(c >= '0' && c <= '9') && c != '_' && c != '-')
                    throw new ArgumentException("Upload endpoint must be a single route segment.");
        }

        internal static Uri ProxyEndpoint(string server, string endpoint)
        {
            ValidateEndpoint(endpoint);
            var uri = new Uri(server, UriKind.Absolute);
            var builder = new UriBuilder(uri) { Path = uri.AbsolutePath.TrimEnd('/') + "/" + endpoint };
            return builder.Uri;
        }

        // 只规范化一次重复的网关路径段，保留查询参数、正常 /proxy/localize 及已正确的网关 URL。
        // Normalize a duplicated gateway route segment once. Preserve query parameters, normal /proxy/localize routes
        // and already-correct gateway URLs.
        internal static Uri DropAppendedUploadSegment(Uri uri)
        {
            if (uri == null || !uri.IsAbsoluteUri) throw new ArgumentException("Absolute upload URI is required.");
            string path = uri.AbsolutePath.TrimEnd('/');
            if (!path.EndsWith("/limitRatesVpslocalize/localize", StringComparison.Ordinal)) return uri;
            return new UriBuilder(uri) { Path = path.Substring(0, path.Length - "/localize".Length) }.Uri;
        }
    }
}
