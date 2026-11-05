using UnityEngine;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using MixedSpace.XR;
#if UNITY_IOS && !UNITY_EDITOR
using NativeCore = MixedSpace.MixedSpaceIosNative;
using LocalizeInfo = MixedSpace.MixedSpaceIosLocalizationResult;
using CaptureInfo = MixedSpace.MixedSpaceIosCaptureResult;
#else
using MixedSpace.NativeInterop;
#endif

#if UNITY_EDITOR || UNITY_STANDALONE

using System.Diagnostics;

#endif

namespace MixedSpace
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void LogCallback(IntPtr msg);

    public sealed class MixedSpaceNativeCore :
        IMixedSpaceNativeCore,
        IMixedSpaceNativeRuntime,
        IMixedSpaceNativeMapStore,
        IMixedSpaceNativeFrameEncoder,
        IMixedSpaceNativeLocalizer,
        IMixedSpaceNativeGeoTransform,
        IMixedSpaceNativeMappingWorkspace
    {
        private readonly MixedSpaceMapHandleIndex m_MapHandles = new MixedSpaceMapHandleIndex();

        public IMixedSpaceNativeRuntime Runtime => this;
        public IMixedSpaceNativeMapStore Maps => this;
        public IMixedSpaceNativeFrameEncoder FrameEncoder => this;
        public IMixedSpaceNativeLocalizer Localizer => this;
        public IMixedSpaceNativeGeoTransform GeoTransform => this;
        public IMixedSpaceNativeMappingWorkspace Mapping => this;

        /// <summary>
        /// 插件至少驻留一张地图时为 true，多后端主机用其跳过无地图的原生搜索。
        /// True while at least one map is resident in the plugin. A host that runs
        /// several native loaders side by side uses this to skip a loader that has
        /// nothing to search, so a frame never pays for an empty native pass.
        /// </summary>
        internal bool HasLoadedMaps => m_MapHandles.Count > 0;

        public void AttachLogSink(IntPtr callback)
        {
            NativeCore.PP_RegisterLogCallback(callback);
        }

        #region On-Device Mapping

        public int CapturedFrameCount => NativeCore.icvMapImageGetCount();

        public bool Append(MixedSpaceMappingFrame frame)
        {
            if (frame == null)
                throw new ArgumentNullException(nameof(frame));

            Vector4 intrinsics = frame.Intrinsics;
            Vector3 position = frame.Position;
            Quaternion rotation = frame.Rotation;
            return NativeCore.icvMapAddImage(
                frame.PixelBuffer,
                frame.Width,
                frame.Height,
                frame.Channels,
                ref intrinsics,
                ref position,
                ref rotation) == 0;
        }

        public byte[] BuildPayload(string workspacePath)
        {
            int size = NativeCore.icvMapPrepare(workspacePath);
            if (size <= 0)
                return null;

            var payload = new byte[size];
            return NativeCore.icvMapGet(payload) == 1 ? payload : null;
        }

        public Vector3[] ReadPreviewPoints()
        {
            int count = NativeCore.icvMapPointsGetCount();
            if (count <= 0)
                return Array.Empty<Vector3>();

            var points = new Vector3[count];
            FillMappingPoints(points);
            return points;
        }

        public void ResetWorkspace()
        {
            NativeCore.icvMapResourcesFree();
        }

        private static int FillMappingPoints(Vector3[] points)
        {
            if (points == null)
                throw new ArgumentNullException(nameof(points));
            if (points.Length == 0)
                return 0;

            GCHandle pointsHandle = default;
            try
            {
                pointsHandle = GCHandle.Alloc(points, GCHandleType.Pinned);
                return NativeCore.icvMapPointsGet(pointsHandle.AddrOfPinnedObject(), points.Length);
            }
            finally
            {
                if (pointsHandle.IsAllocated)
                    pointsHandle.Free();
            }
        }

        #endregion On-Device Mapping

        public bool Import(int mapKey, byte[] payload)
        {
            return ImportMap(mapKey, payload) != -1;
        }

        public void Evict(int mapKey)
        {
            EvictMap(mapKey);
        }

        public Vector3[] ReadSparsePoints(int mapKey)
        {
            int count = GetSparsePointCount(mapKey);
            if (count <= 0)
                return Array.Empty<Vector3>();

            var points = new Vector3[count];
            return FillSparsePoints(mapKey, points) > 0
                ? points
                : Array.Empty<Vector3>();
        }

        /// <summary>
        /// 读取地图的 Vector3 点云表示。
        /// Get a Vector3 point cloud representation of the map data.
        /// </summary>
        /// <param name="mapId">
        /// 应用侧地图 ID。
        /// An integer map id
        /// </param>
        /// <param name="points">
        /// 用于接收点数据的预分配 Vector3 数组。
        /// A preallocated Vector3 array for the points
        /// </param>
        /// <returns>
        /// 成功时返回点数，否则返回 0。
        /// Returns the number of points if succeeded, 0 otherwise.
        /// </returns>
        private int FillSparsePoints(int mapId, Vector3[] points)
        {
            if (points == null)
                throw new ArgumentNullException(nameof(points));
            if (points.Length == 0)
                return 0;

            if (m_MapHandles.TryResolveNativeHandle(mapId, out int handle))
            {
                GCHandle pointsHandle = default;
                try
                {
                    pointsHandle = GCHandle.Alloc(points, GCHandleType.Pinned);
                    return NativeCore.icvPointsGet(handle, pointsHandle.AddrOfPinnedObject(), points.Length);
                }
                finally
                {
                    if (pointsHandle.IsAllocated)
                        pointsHandle.Free();
                }
            }

            return -1;
        }

        /// <summary>
        /// 读取地图点云的点数。
        /// Get point count of the map's point cloud.
        /// </summary>
        /// <param name="mapId">
        /// 应用侧地图 ID。
        /// An integer map id
        /// </param>
        /// <returns>
        /// 返回点数。
        /// Returns the number of points.
        /// </returns>
        private int GetSparsePointCount(int mapId)
        {
            if (m_MapHandles.TryResolveNativeHandle(mapId, out int handle))
            {
                return NativeCore.icvPointsGetCount(handle);
            }

            return -1;
        }

        /// <summary>
        /// 从内存中的地图容器导入原生地图，并记录应用地图 ID 与原生句柄的对应关系。
        /// Imports an in-memory map container and records the application-ID to native-handle
        /// mapping.
        /// </summary>
        /// <param name="buffer">
        /// 地图文件的完整二进制内容；本方法不读取文件路径。
        /// Complete binary map-file contents; this method does not read a file path.
        /// </param>
        /// <param name="mapId">
        /// 应用侧地图 ID，用于查询对应的原生句柄。
        /// Application map ID used to resolve its native handle.
        /// </param>
        /// <returns>
        /// 已存在或新导入地图的原生句柄；空内容返回 -1，其他导入结果返回原生状态值。
        /// Native handle of an existing or newly imported map; empty data returns -1 and other import
        /// results retain native values.
        /// </returns>
        private int ImportMap(int mapId, byte[] buffer)
        {
            if (buffer == null)
                throw new ArgumentNullException(nameof(buffer));
            if (buffer.Length == 0)
                return -1;

            // 同 ID 已加载时返回对应的现有原生句柄。
            // if there is already a loaded map with the same id, return the handle pointing to that
            if (m_MapHandles.TryResolveNativeHandle(mapId, out int handle))
            {
                MixedSpaceLogger.Log($"Trying to load map {mapId}({handle}) into plugin, but it's already mapped.");
                return handle;
            }

            int mapHandle = NativeCore.icvLoadMap(buffer);
            if (mapHandle == -1)
            {
                MixedSpaceLogger.LogError($"Native map loading failed for map {mapId}.");
                return -1;
            }

            if (!m_MapHandles.Register(mapId, mapHandle))
            {
                NativeCore.icvFreeMap(mapHandle);
                MixedSpaceLogger.LogError(
                    $"Native map handle {mapHandle} conflicts with an existing map mapping.");
                return -1;
            }

            MixedSpaceLogger.Log($"Loaded map {mapId}({mapHandle}) into plugin");
            return mapHandle;
        }

        /// <summary>
        /// 根据应用地图 ID 查找原生句柄，移除映射并请求原生库释放地图。
        /// Resolves the application map ID, removes its handle mapping and requests native map
        /// release.
        /// </summary>
        /// <param name="mapId">
        /// 应用侧地图 ID，不是原生地图句柄。
        /// Application map ID, not a native map handle.
        /// </param>
        /// <returns>
        /// 找到地图时返回 icvFreeMap 的原始结果，未找到映射时返回 0。
        /// Raw icvFreeMap result when found; 0 when there is no handle mapping.
        /// </returns>
        private int EvictMap(int mapId)
        {
            if (m_MapHandles.TryResolveNativeHandle(mapId, out int mapHandle))
            {
                MixedSpaceLogger.Log($"Freeing map {mapId}({mapHandle}) from plugin");
                m_MapHandles.ForgetMap(mapId);
                return NativeCore.icvFreeMap(mapHandle);
            }

            return 0;
        }

        public byte[] Encode(
            byte[] pixels,
            int width,
            int height,
            int channels,
            bool prepareForMatching = false)
        {
            if (pixels == null)
                throw new ArgumentNullException(nameof(pixels));
            if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
            if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
            if (channels <= 0) throw new ArgumentOutOfRangeException(nameof(channels));

            var encoded = new byte[checked(width * height * channels + 8192)];
            CaptureInfo result = CaptureEncodedFrame(
                encoded,
                encoded.Length,
                pixels,
                width,
                height,
                channels,
                prepareForMatching ? 1 : 0);
            if (result.captureSize <= 0 || result.captureSize > encoded.Length)
                return Array.Empty<byte>();

            Array.Resize(ref encoded, result.captureSize);
            return encoded;
        }

        private static CaptureInfo CaptureEncodedFrame(
            byte[] capture,
            int captureSizeMax,
            byte[] pixels,
            int width,
            int height,
            int channels,
            int useMatching)
        {
            if (capture == null)
                throw new ArgumentNullException(nameof(capture));
            if (pixels == null)
                throw new ArgumentNullException(nameof(pixels));
            if (captureSizeMax < 0 || captureSizeMax > capture.Length)
                throw new ArgumentOutOfRangeException(nameof(captureSizeMax));

            GCHandle captureHandle = default;
            GCHandle pixelsHandle = default;
            try
            {
                captureHandle = GCHandle.Alloc(capture, GCHandleType.Pinned);
                pixelsHandle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
                return NativeCore.icvCaptureImage(
                    captureHandle.AddrOfPinnedObject(),
                    captureSizeMax,
                    pixelsHandle.AddrOfPinnedObject(),
                    width,
                    height,
                    channels,
                    useMatching);
            }
            finally
            {
                if (pixelsHandle.IsAllocated)
                    pixelsHandle.Free();
                if (captureHandle.IsAllocated)
                    captureHandle.Free();
            }
        }

        public MixedSpaceLocalizationObservation Resolve(
            MixedSpaceCaptureProfile profile,
            IntPtr pixelBuffer,
            MixedSpaceNativeLocalizationQuery query)
        {
            if (profile == null)
                throw new ArgumentNullException(nameof(profile));
            if (query == null)
                throw new ArgumentNullException(nameof(query));

            LocalizeInfo nativeResult;
            if (query.HasPrior)
            {
                Vector3 priorPosition = query.PriorPosition;
                nativeResult = ResolveNativeWithPrior(
                    profile,
                    pixelBuffer,
                    ref priorPosition,
                    query.NeighborCount,
                    query.Radius);
            }
            else
            {
                nativeResult = ResolveNative(
                    profile,
                    pixelBuffer,
                    (int)query.Solver);
            }

            return nativeResult.isSuccess && nativeResult.mapId > 0
                ? new MixedSpaceLocalizationObservation(
                    nativeResult.mapId,
                    nativeResult.position,
                    nativeResult.rotation,
                    nativeResult.confidence)
                : null;
        }

        private LocalizeInfo ResolveNative(
            MixedSpaceCaptureProfile profile,
            IntPtr pixelBuffer,
            int solverType)
        {
            int channels = profile.ChannelCount == 0 ? 1 : profile.ChannelCount;
            Vector4 intrinsics = profile.CameraIntrinsics;
            Quaternion cameraRotation =
                profile.TrackerRotation * profile.DisplayRotation;
            cameraRotation = MixedSpaceCoordinateFrame.ReflectXAxis(cameraRotation);
            int[] mapIds = new int[1];
            m_MapHandles.ReadSnapshots(
                out Dictionary<int, int> mapToHandle,
                out Dictionary<int, int> handleToMap);
            // iOS ABI 返回原生地图句柄而非应用 ID；使用全地图哨兵 n=0 及固定的单元素零数组。
            // The iOS ABI returns a native map handle, never an application map ID. Use the all-maps sentinel: n=0 and a
            // pinned one-element zero array.
            var handlePin = GCHandle.Alloc(mapIds, GCHandleType.Pinned);
            try
            {
                LocalizeInfo result = NativeCore.icvLocalize(0, handlePin.AddrOfPinnedObject(),
                    profile.PixelWidth, profile.PixelHeight, ref intrinsics, pixelBuffer,
                    channels, solverType, ref cameraRotation);
                result.mapId = handleToMap.TryGetValue(result.mapId, out int mapId) ? mapId : -1;
#if !UNITY_IOS || UNITY_EDITOR
                // Android 和桌面以正的原生地图句柄表示成功。
                // Android and desktop report success through a positive native map handle.
                result.isSuccess = result.mapId > 0;
#endif
                return result;
            }
            finally { handlePin.Free(); }
        }

        private LocalizeInfo ResolveNativeWithPrior(
            MixedSpaceCaptureProfile profile,
            IntPtr pixelBuffer,
            ref Vector3 priorPosition,
            int priorNeighborCount,
            float priorRadius)
        {
            int channels = profile.ChannelCount == 0 ? 1 : profile.ChannelCount;
            Vector4 intrinsics = profile.CameraIntrinsics;

            m_MapHandles.ReadSnapshots(
                out Dictionary<int, int> mapToHandle,
                out Dictionary<int, int> handleToMap);
            var handlePin = GCHandle.Alloc(new int[1], GCHandleType.Pinned);
            try
            {
                LocalizeInfo result = NativeCore.icvLocalizePrior(0, handlePin.AddrOfPinnedObject(),
                    profile.PixelWidth, profile.PixelHeight, ref intrinsics, pixelBuffer,
                    channels, ref priorPosition, priorNeighborCount, priorRadius);
                result.mapId = handleToMap.TryGetValue(result.mapId, out int mapId) ? mapId : -1;
#if !UNITY_IOS || UNITY_EDITOR
                result.isSuccess = result.mapId > 0;
#endif
                return result;
            }
            finally { handlePin.Free(); }
        }

        public bool TryMapToEarth(
            Vector3 mapPosition,
            double[] mapToEarth,
            out MixedSpaceEarthPosition earthPosition)
        {
            var earth = new double[3];
            bool success = MapToEarthNative(earth, mapPosition, mapToEarth) == 0;
            earthPosition = success
                ? new MixedSpaceEarthPosition(earth[0], earth[1], earth[2])
                : default;
            return success;
        }

        public bool TryMapToGeodetic(
            Vector3 mapPosition,
            double[] mapToEarth,
            out MixedSpaceGeodeticPosition geodeticPosition)
        {
            var geodetic = new double[3];
            bool success = MapToGeodeticNative(geodetic, mapPosition, mapToEarth) == 0;
            geodeticPosition = success
                ? new MixedSpaceGeodeticPosition(
                    geodetic[0],
                    geodetic[1],
                    geodetic[2])
                : default;
            return success;
        }

        public bool TryGeodeticToMap(
            MixedSpaceGeodeticPosition geodeticPosition,
            Quaternion earthRotation,
            double[] mapToEarth,
            out Vector3 mapPosition,
            out Quaternion mapRotation)
        {
            var geodetic = new[]
            {
                geodeticPosition.Latitude,
                geodeticPosition.Longitude,
                geodeticPosition.Altitude
            };
            var earth = new double[3];
            if (GeodeticToEarthNative(earth, geodetic) != 0 ||
                EarthToMapNative(out mapPosition, earth, mapToEarth) != 0 ||
                EarthRotationToMapNative(
                    out mapRotation,
                    earthRotation,
                    mapToEarth) != 0)
            {
                mapPosition = default;
                mapRotation = Quaternion.identity;
                return false;
            }

            return true;
        }

        private static int MapToEarthNative(
            double[] ecef,
            Vector3 map,
            double[] mapToEcef)
        {
            RequireLength(ecef, 3, nameof(ecef));
            RequireLength(mapToEcef, 13, nameof(mapToEcef));

            GCHandle ecefHandle = default;
            GCHandle mapToEcefHandle = default;
            try
            {
                ecefHandle = GCHandle.Alloc(ecef, GCHandleType.Pinned);
                mapToEcefHandle = GCHandle.Alloc(mapToEcef, GCHandleType.Pinned);
                return NativeCore.icvPosMapToEcef(
                    ecefHandle.AddrOfPinnedObject(),
                    ref map,
                    mapToEcefHandle.AddrOfPinnedObject());
            }
            finally
            {
                if (mapToEcefHandle.IsAllocated)
                    mapToEcefHandle.Free();
                if (ecefHandle.IsAllocated)
                    ecefHandle.Free();
            }
        }

        /// <summary>
        /// 将 ECEF 坐标转换为 WGS84 地理坐标，输出写入调用方提供的数组。
        /// Converts ECEF coordinates to WGS84, writing the caller-provided output array.
        /// </summary>
        /// <param name="wgs84">
        /// 输出数组，至少包含 3 个元素；分量遵循原生 WGS84 接口约定。
        /// Output array with at least 3 elements; components follow the native WGS84 convention.
        /// </param>
        /// <param name="ecef">
        /// 输入的 ECEF 三维坐标，数组至少包含 3 个元素。
        /// Input ECEF coordinates in an array of at least 3 elements.
        /// </param>
        /// <returns>
        /// icvPosEcefToWgs84 的原始返回码。
        /// Raw return code from icvPosEcefToWgs84.
        /// </returns>
        private static int EarthToGeodeticNative(double[] wgs84, double[] ecef)
        {
            RequireLength(wgs84, 3, nameof(wgs84));
            RequireLength(ecef, 3, nameof(ecef));

            GCHandle wgs84Handle = default;
            GCHandle ecefHandle = default;
            try
            {
                wgs84Handle = GCHandle.Alloc(wgs84, GCHandleType.Pinned);
                ecefHandle = GCHandle.Alloc(ecef, GCHandleType.Pinned);
                return NativeCore.icvPosEcefToWgs84(
                    wgs84Handle.AddrOfPinnedObject(),
                    ecefHandle.AddrOfPinnedObject());
            }
            finally
            {
                if (ecefHandle.IsAllocated)
                    ecefHandle.Free();
                if (wgs84Handle.IsAllocated)
                    wgs84Handle.Free();
            }
        }

        /// <summary>
        /// 将 WGS84 地理坐标转换为 ECEF 坐标，输出写入调用方提供的数组。
        /// Converts WGS84 coordinates to ECEF, writing the caller-provided output array.
        /// </summary>
        /// <param name="ecef">
        /// 输出 ECEF 三维坐标，数组至少包含 3 个元素。
        /// Output ECEF coordinates in an array of at least 3 elements.
        /// </param>
        /// <param name="wgs84">
        /// 输入数组，至少包含 3 个元素；分量遵循原生 WGS84 接口约定。
        /// Input array with at least 3 elements; components follow the native WGS84 convention.
        /// </param>
        /// <returns>
        /// icvPosWgs84ToEcef 的原始返回码。
        /// Raw return code from icvPosWgs84ToEcef.
        /// </returns>
        private static int GeodeticToEarthNative(double[] ecef, double[] wgs84)
        {
            RequireLength(ecef, 3, nameof(ecef));
            RequireLength(wgs84, 3, nameof(wgs84));

            GCHandle ecefHandle = default;
            GCHandle wgs84Handle = default;
            try
            {
                ecefHandle = GCHandle.Alloc(ecef, GCHandleType.Pinned);
                wgs84Handle = GCHandle.Alloc(wgs84, GCHandleType.Pinned);
                return NativeCore.icvPosWgs84ToEcef(
                    ecefHandle.AddrOfPinnedObject(),
                    wgs84Handle.AddrOfPinnedObject());
            }
            finally
            {
                if (wgs84Handle.IsAllocated)
                    wgs84Handle.Free();
                if (ecefHandle.IsAllocated)
                    ecefHandle.Free();
            }
        }

        /// <summary>
        /// 使用地图地理变换，将 ECEF 点转换到原生地图坐标系。
        /// Transforms an ECEF point into native map coordinates using the map's geographic
        /// transform.
        /// </summary>
        /// <param name="map">
        /// 输出的地图坐标。
        /// Output map coordinates.
        /// </param>
        /// <param name="ecef">
        /// 输入 ECEF 三维坐标，数组至少包含 3 个元素。
        /// Input ECEF coordinates in an array of at least 3 elements.
        /// </param>
        /// <param name="mapToEcef">
        /// 原生地图到 ECEF 的变换数据，数组至少包含 13 个元素。
        /// Native map-to-ECEF transform data with at least 13 elements.
        /// </param>
        /// <returns>
        /// icvPosEcefToMap 的原始返回码。
        /// Raw return code from icvPosEcefToMap.
        /// </returns>
        private static int EarthToMapNative(
            out Vector3 map,
            double[] ecef,
            double[] mapToEcef)
        {
            RequireLength(ecef, 3, nameof(ecef));
            RequireLength(mapToEcef, 13, nameof(mapToEcef));

            GCHandle ecefHandle = default;
            GCHandle mapToEcefHandle = default;
            try
            {
                ecefHandle = GCHandle.Alloc(ecef, GCHandleType.Pinned);
                mapToEcefHandle = GCHandle.Alloc(mapToEcef, GCHandleType.Pinned);
                return NativeCore.icvPosEcefToMap(
                    out map,
                    ecefHandle.AddrOfPinnedObject(),
                    mapToEcefHandle.AddrOfPinnedObject());
            }
            finally
            {
                if (mapToEcefHandle.IsAllocated)
                    mapToEcefHandle.Free();
                if (ecefHandle.IsAllocated)
                    ecefHandle.Free();
            }
        }

        /// <summary>
        /// 先将原生地图坐标转换为 ECEF，再转换为 WGS84 地理坐标。
        /// Converts native map coordinates to ECEF and then WGS84 geographic coordinates.
        /// </summary>
        /// <param name="wgs84">
        /// 输出 WGS84 坐标数组，至少包含 3 个元素。
        /// Output WGS84 coordinate array with at least 3 elements.
        /// </param>
        /// <param name="map">
        /// 输入的原生地图坐标。
        /// Input native map coordinates.
        /// </param>
        /// <param name="mapToEcef">
        /// 原生地图到 ECEF 的变换数据，数组至少包含 13 个元素。
        /// Native map-to-ECEF transform data with at least 13 elements.
        /// </param>
        /// <returns>
        /// 地图到 ECEF 转换失败时返回该错误码，否则返回地理坐标转换的返回码。
        /// Map-to-ECEF error code on failure; otherwise the geographic conversion return code.
        /// </returns>
        private static int MapToGeodeticNative(
            double[] wgs84,
            Vector3 map,
            double[] mapToEcef)
        {
            double[] ecef = new double[3];
            int err = MapToEarthNative(ecef, map, mapToEcef);
            if (err != 0)
                return err;
            return EarthToGeodeticNative(wgs84, ecef);
        }

        /// <summary>
        /// 使用地图地理变换，将原生地图坐标系中的旋转转换到 ECEF 坐标系。
        /// Transforms a native map rotation into the ECEF coordinate system.
        /// </summary>
        /// <param name="ecef">
        /// 输出的 ECEF 旋转。
        /// Output ECEF rotation.
        /// </param>
        /// <param name="map">
        /// 输入的原生地图旋转。
        /// Input native map rotation.
        /// </param>
        /// <param name="mapToEcef">
        /// 原生地图到 ECEF 的变换数据，数组至少包含 13 个元素。
        /// Native map-to-ECEF transform data with at least 13 elements.
        /// </param>
        /// <returns>
        /// icvRotMapToEcef 的原始返回码。
        /// Raw return code from icvRotMapToEcef.
        /// </returns>
        private static int MapRotationToEarthNative(
            out Quaternion ecef,
            Quaternion map,
            double[] mapToEcef)
        {
            RequireLength(mapToEcef, 13, nameof(mapToEcef));

            GCHandle mapToEcefHandle = default;
            try
            {
                mapToEcefHandle = GCHandle.Alloc(mapToEcef, GCHandleType.Pinned);
                return NativeCore.icvRotMapToEcef(
                    out ecef,
                    ref map,
                    mapToEcefHandle.AddrOfPinnedObject());
            }
            finally
            {
                if (mapToEcefHandle.IsAllocated)
                    mapToEcefHandle.Free();
            }
        }

        /// <summary>
        /// 使用地图地理变换，将 ECEF 旋转转换到原生地图坐标系。
        /// Transforms an ECEF rotation into the native map coordinate system.
        /// </summary>
        /// <param name="map">
        /// 输出的原生地图旋转。
        /// Output native map rotation.
        /// </param>
        /// <param name="ecef">
        /// 输入的 ECEF 旋转。
        /// Input ECEF rotation.
        /// </param>
        /// <param name="mapToEcef">
        /// 原生地图到 ECEF 的变换数据，数组至少包含 13 个元素。
        /// Native map-to-ECEF transform data with at least 13 elements.
        /// </param>
        /// <returns>
        /// icvRotEcefToMap 的原始返回码。
        /// Raw return code from icvRotEcefToMap.
        /// </returns>
        private static int EarthRotationToMapNative(
            out Quaternion map,
            Quaternion ecef,
            double[] mapToEcef)
        {
            RequireLength(mapToEcef, 13, nameof(mapToEcef));

            GCHandle mapToEcefHandle = default;
            try
            {
                mapToEcefHandle = GCHandle.Alloc(mapToEcef, GCHandleType.Pinned);
                return NativeCore.icvRotEcefToMap(
                    out map,
                    ref ecef,
                    mapToEcefHandle.AddrOfPinnedObject());
            }
            finally
            {
                if (mapToEcefHandle.IsAllocated)
                    mapToEcefHandle.Free();
            }
        }

        public void ApplyImagePixelBudget(int maximumPixels)
        {
            if (maximumPixels < 0)
                throw new ArgumentOutOfRangeException(nameof(maximumPixels));

            NativeCore.icvSetInteger("LocalizationMaxPixels", maximumPixels);
        }

        public int ResolveLicenseLevel(string developerToken)
        {
            return NativeCore.icvValidateUser(developerToken);
        }

        private static void RequireLength(Array values, int minimumLength, string parameterName)
        {
            if (values == null)
                throw new ArgumentNullException(parameterName);
            if (values.Length < minimumLength)
            {
                throw new ArgumentException(
                    $"The native call requires at least {minimumLength} values.",
                    parameterName);
            }
        }
    }

    internal sealed class MixedSpaceMapHandleIndex
    {
        private readonly object m_Gate = new object();
        private volatile Dictionary<int, int> m_MapIdToNativeHandle = new Dictionary<int, int>();
        private volatile Dictionary<int, int> m_NativeHandleToMapId = new Dictionary<int, int>();

        /// <summary>
        /// 当前驻留在插件内的地图数量。
        /// Number of maps currently resident in the plugin.
        /// </summary>
        public int Count
        {
            get
            {
                lock (m_Gate)
                    return m_MapIdToNativeHandle.Count;
            }
        }

        // 原生定位读取稳定快照；修改时发布新字典，使后台定位不会枚举正在被其他线程修改的集合。
        // Native localization receives stable snapshots. Mutations publish new dictionaries so a background localization
        // request never enumerates a collection that another thread is modifying.
        public void ReadSnapshots(
            out Dictionary<int, int> mapIdToNativeHandle,
            out Dictionary<int, int> nativeHandleToMapId)
        {
            lock (m_Gate)
            {
                mapIdToNativeHandle = m_MapIdToNativeHandle;
                nativeHandleToMapId = m_NativeHandleToMapId;
            }
        }

        public bool Register(int mapId, int pluginHandle)
        {
            lock (m_Gate)
            {
                if (m_MapIdToNativeHandle.ContainsKey(mapId) ||
                    m_NativeHandleToMapId.ContainsKey(pluginHandle))
                {
                    return false;
                }

                Dictionary<int, int> mapToHandle =
                    new Dictionary<int, int>(m_MapIdToNativeHandle)
                    {
                        [mapId] = pluginHandle
                    };
                Dictionary<int, int> handleToMap =
                    new Dictionary<int, int>(m_NativeHandleToMapId)
                    {
                        [pluginHandle] = mapId
                    };

                m_MapIdToNativeHandle = mapToHandle;
                m_NativeHandleToMapId = handleToMap;
                return true;
            }
        }

        public bool TryResolveNativeHandle(int mapId, out int pluginHandle)
        {
            Dictionary<int, int> snapshot = m_MapIdToNativeHandle;
            return snapshot.TryGetValue(mapId, out pluginHandle);
        }

        public bool ForgetMap(int mapId)
        {
            lock (m_Gate)
            {
                if (!m_MapIdToNativeHandle.TryGetValue(mapId, out int pluginHandle))
                    return false;

                Dictionary<int, int> mapToHandle =
                    new Dictionary<int, int>(m_MapIdToNativeHandle);
                Dictionary<int, int> handleToMap =
                    new Dictionary<int, int>(m_NativeHandleToMapId);
                mapToHandle.Remove(mapId);
                handleToMap.Remove(pluginHandle);
                m_MapIdToNativeHandle = mapToHandle;
                m_NativeHandleToMapId = handleToMap;
                return true;
            }
        }

    }
}
