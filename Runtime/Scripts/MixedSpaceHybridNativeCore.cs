using System;
using System.Runtime.InteropServices;
using MixedSpace.XR;
using UnityEngine;

namespace MixedSpace
{
    /// <summary>
    /// 决定地图交给哪个原生加载器；Auto 优先检查容器头，未知旧云资产再按 ID 位数回退。显式选项可固定后端，便于设备 A/B 诊断。
    /// Which native loader receives a map container.
    /// Auto prefers the container header, with ID-length fallback for unrecognized legacy cloud assets;
    /// the two explicit values pin one loader
    /// and exist so a device build can A/B a single backend without a code change.
    /// </summary>
    public enum MixedSpaceNativeBackend
    {
        Auto = 0,
        PosePlugin = 1,
        XrLoc = 2
    }
}

namespace MixedSpace
{
    /// <summary>
    /// 混合原生核心按地图容器格式选择加载器；地理转换、建图及匹配编码由 PosePlugin 提供。
    /// Routes map loading by container format; PosePlugin provides geographic conversion, mapping and matching-aware encoding.
    /// </summary>
    public sealed class MixedSpaceHybridNativeCore :
        IMixedSpaceNativeCore,
        IMixedSpaceNativeRuntime,
        IMixedSpaceNativeMapStore,
        IMixedSpaceNativeFrameEncoder,
        IMixedSpaceNativeLocalizer,
        IMixedSpaceNativeGeoTransform,
        IMixedSpaceNativeMappingWorkspace
    {
        private readonly MixedSpaceNativeBackend m_Preference;
        private readonly MixedSpaceNativeCore m_Plugin;
        private readonly MixedSpaceXrLocCore m_XrLoc;

        public MixedSpaceHybridNativeCore()
            : this(MixedSpaceNativeBackend.Auto)
        {
        }

        public MixedSpaceHybridNativeCore(MixedSpaceNativeBackend preference)
        {
            m_Preference = preference;
            m_Plugin = new MixedSpaceNativeCore();
            // 共享插件实例，使地理转换、建图及插件地图索引都在同一对象中。
            // Share the plugin instance so geodetic conversion, mapping and the plugin-side map index all live in one object
            // rather than two.
            m_XrLoc = new MixedSpaceXrLocCore(m_Plugin);

            string xrLocVersion = MixedSpaceXrLocCore.DescribeVersion() ?? "unavailable";
            MixedSpaceLogger.Log(
                $"Native backend preference={preference}, xrloc version={xrLocVersion}");
        }
        public IMixedSpaceNativeRuntime Runtime => this;
        public IMixedSpaceNativeMapStore Maps => this;
        public IMixedSpaceNativeFrameEncoder FrameEncoder => this;
        public IMixedSpaceNativeLocalizer Localizer => this;
        public IMixedSpaceNativeGeoTransform GeoTransform => this;
        public IMixedSpaceNativeMappingWorkspace Mapping => this;

        #region Runtime

        /// <summary>
        /// 向两套原生库注册同一个托管日志桥接；相同 void(*)(const char*) 签名让 xrloc 错误进入同一日志。
        /// Installs the managed sink with both libraries. The XrLoc* and
        /// PosePlugin callbacks share the void(*)(const char*) signature, so one
        /// thunk serves both and xrloc failures become visible in the same log.
        /// </summary>
        public void AttachLogSink(IntPtr callback)
        {
            m_Plugin.AttachLogSink(callback);
            m_XrLoc.AttachLogSink(callback);
        }

        /// <summary>
        /// 像素预算选项 LocalizationMaxPixels 仅由 PosePlugin 提供。
        /// Pixel budgeting only exists in PosePlugin ("LocalizationMaxPixels").
        /// </summary>
        public void ApplyImagePixelBudget(int maximumPixels)
        {
            m_Plugin.ApplyImagePixelBudget(maximumPixels);
        }

        /// <summary>
        /// 许可验证继续使用 PosePlugin 的 icvValidateUser 入口。
        /// License validation stays on the PosePlugin entry (icvValidateUser).
        /// </summary>
        public int ResolveLicenseLevel(string developerToken)
        {
            return m_Plugin.ResolveLicenseLevel(developerToken);
        }

        #endregion Runtime

        #region Map storage

        /// <summary>
        /// 每个容器仅交给匹配的加载器，失败时不跨后端重试，以避免不兼容导入器阻塞启动。
        /// Uses only the matching loader for each container; failures are not retried across backends because incompatible imports can block startup.
        /// </summary>
        public bool Import(int mapKey, byte[] payload)
        {
            if (payload == null)
                throw new ArgumentNullException(nameof(payload));
            if (payload.Length == 0)
                return false;

            MixedSpaceNativeBackend backend = ResolveBackend(mapKey, payload);
            MixedSpaceLogger.Log($"Route map={mapKey} bytes={payload.Length} backend={backend} rule=ContainerHeaderOrLegacyMapId", MixedSpaceLogger.LoggingLevel.Verbose);

            if (backend == MixedSpaceNativeBackend.PosePlugin)
                return m_Plugin.Import(mapKey, payload);

            if (m_XrLoc.Import(mapKey, payload))
                return true;

            MixedSpaceLogger.LogError(
                $"xrloc rejected map {mapKey} ({payload.Length} bytes). " +
                "The legacy loader is not tried afterwards: it does not fail fast on containers it " +
                "cannot parse, and map import is awaited during startup. Check the map data and re-export it if needed.");
            return false;
        }

        /// <summary>
        /// 同时请求两库释放地图；未加载该 ID 的库会忽略，无需事先知道后端归属。
        /// Releases the map from both libraries. Each one ignores ids it never
        /// loaded, so the call never needs to know which backend owns the map.
        /// </summary>
        public void Evict(int mapKey)
        {
            m_Plugin.Evict(mapKey);
            m_XrLoc.Evict(mapKey);
        }

        /// <summary>
        /// 先查询插件点云，旧地图以其索引为准；未加载地图返回空数组，再回退查询 xrloc。
        /// Asks the plugin first: its index is authoritative for legacy maps and
        /// returns an empty array for anything it never loaded, which is exactly the
        /// signal needed to fall through to xrloc.
        /// </summary>
        public Vector3[] ReadSparsePoints(int mapKey)
        {
            Vector3[] points = m_Plugin.ReadSparsePoints(mapKey);
            return points.Length > 0 ? points : m_XrLoc.ReadSparsePoints(mapKey);
        }

        #endregion Map storage

        #region Frame encoding

        /// <summary>
        /// 始终使用支持匹配预处理的 PosePlugin 编码器，满足上传路径需要。
        /// Always the PosePlugin encoder: it is the only one that can prepare a
        /// frame for matching, which the upload path relies on.
        /// </summary>
        public byte[] Encode(
            byte[] pixels,
            int width,
            int height,
            int channels,
            bool prepareForMatching = false)
        {
            return m_Plugin.Encode(pixels, width, height, channels, prepareForMatching);
        }

        #endregion Frame encoding

        #region Localization

        public MixedSpaceLocalizationObservation Resolve(
            MixedSpaceCaptureProfile profile,
            IntPtr pixelBuffer,
            MixedSpaceNativeLocalizationQuery query)
        {
            if (profile == null)
                throw new ArgumentNullException(nameof(profile));
            if (query == null)
                throw new ArgumentNullException(nameof(query));

            // 未持有地图的后端无法产出结果，应跳过空搜索；只有混合代际地图同时存在才运行两条路径。
            // A backend that holds no map cannot contribute a result, and skipping it avoids a pointless native search on
            // every frame. Only maps of mixed generations make both calls run.
            MixedSpaceLocalizationObservation best = null;
            if (m_Plugin.HasLoadedMaps)
                best = Prefer(best, m_Plugin.Localizer.Resolve(profile, pixelBuffer, query));
            if (m_XrLoc.HasLoadedMaps)
                best = Prefer(best, m_XrLoc.Localizer.Resolve(profile, pixelBuffer, query));
            return best;
        }

        /// <summary>
        /// 保留置信度更高的观测；相同置信度或未命中保留较早结果，因调用顺序而由插件赢得平局。
        /// Keeps the more confident observation; a tie or a miss keeps the earlier
        /// one so the plugin result wins ties, matching the single-backend order.
        /// </summary>
        private static MixedSpaceLocalizationObservation Prefer(
            MixedSpaceLocalizationObservation current,
            MixedSpaceLocalizationObservation candidate)
        {
            if (candidate == null)
                return current;
            if (current == null)
                return candidate;
            return candidate.Confidence > current.Confidence ? candidate : current;
        }

        #endregion Localization

        #region Geo transform and on-device mapping

        public bool TryMapToEarth(
            Vector3 mapPosition,
            double[] mapToEarth,
            out MixedSpaceEarthPosition earthPosition)
        {
            return m_Plugin.TryMapToEarth(mapPosition, mapToEarth, out earthPosition);
        }

        public bool TryMapToGeodetic(
            Vector3 mapPosition,
            double[] mapToEarth,
            out MixedSpaceGeodeticPosition geodeticPosition)
        {
            return m_Plugin.TryMapToGeodetic(mapPosition, mapToEarth, out geodeticPosition);
        }

        public bool TryGeodeticToMap(
            MixedSpaceGeodeticPosition geodeticPosition,
            Quaternion earthRotation,
            double[] mapToEarth,
            out Vector3 mapPosition,
            out Quaternion mapRotation)
        {
            return m_Plugin.TryGeodeticToMap(
                geodeticPosition, earthRotation, mapToEarth, out mapPosition, out mapRotation);
        }

        public int CapturedFrameCount => m_Plugin.CapturedFrameCount;

        public bool Append(MixedSpaceMappingFrame frame)
        {
            return m_Plugin.Append(frame);
        }

        public byte[] BuildPayload(string workspacePath)
        {
            return m_Plugin.BuildPayload(workspacePath);
        }

        public Vector3[] ReadPreviewPoints()
        {
            return m_Plugin.ReadPreviewPoints();
        }

        public void ResetWorkspace()
        {
            m_Plugin.ResetWorkspace();
        }

        #endregion Geo transform and on-device mapping

        private MixedSpaceNativeBackend ResolveBackend(int mapKey, byte[] payload)
        {
            if (m_Preference != MixedSpaceNativeBackend.Auto)
                return m_Preference;

            // 本地地图 ID 是查询键，不是格式标志；即使文件名为 972，也需检查容器头。
            // Local map IDs are lookup keys, not format markers. Inspect the container for every map, including numeric
            // filenames such as 972.
            if (payload.Length >= 5 && payload[0] == 0x5d &&
                payload[1] == 0 && payload[2] == 0 &&
                payload[3] == 4 && payload[4] == 0)
                return MixedSpaceNativeBackend.PosePlugin;
            if (payload.Length >= 4 && payload[0] == 2 &&
                payload[1] == 0 && payload[2] == 0 && payload[3] == 0)
                return MixedSpaceNativeBackend.XrLoc;

            // 头部无法识别的旧云资产仍保留 ID 路由规则。
            // Keep the legacy route for existing cloud assets whose headers are not recognizable here.

            if (mapKey >= 1000 && mapKey <= 9999)
                return MixedSpaceNativeBackend.PosePlugin;
            if (mapKey >= 100000 && mapKey <= 999999)
                return MixedSpaceNativeBackend.XrLoc;
            throw new ArgumentException(
                $"Unknown map container header for key {mapKey}.", nameof(payload));
        }
    }
}
