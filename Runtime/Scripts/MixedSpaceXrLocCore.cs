using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using MixedSpace.XR;
using UnityEngine;

namespace MixedSpace
{
    /// <summary>
    /// 通过 libxrloc 提供地图、定位和图像编码。
    /// Provides map storage, localization and image encoding through libxrloc.
    /// </summary>
    public sealed class MixedSpaceXrLocCore :
        IMixedSpaceNativeCore,
        IMixedSpaceNativeRuntime,
        IMixedSpaceNativeMapStore,
        IMixedSpaceNativeFrameEncoder,
        IMixedSpaceNativeLocalizer
    {
        /// <summary>
        /// 最近记录的显式配置，仅用于避免重复日志，不参与选择解算器或图像方向。
        /// Last logged configuration, never an override of the requested solver.
        /// </summary>
        private int? m_LastLoggedSolver;
        private int? m_LastLoggedAngle;
        private int? m_LastLoggedPriorMapKey;
        private int? m_LastReportedFailureStatus;
        private long m_NextFailureReportTicks;

        private readonly object m_Gate = new object();
        private readonly HashSet<int> m_ResidentMaps = new HashSet<int>();
        private readonly string m_PositionPriorLogPath;

        public MixedSpaceXrLocCore() : this(null) { }

        internal MixedSpaceXrLocCore(string positionPriorLogPath)
        {
            m_PositionPriorLogPath = positionPriorLogPath;
        }

        public IMixedSpaceNativeRuntime Runtime => this;
        public IMixedSpaceNativeMapStore Maps => this;
        public IMixedSpaceNativeFrameEncoder FrameEncoder => this;
        public IMixedSpaceNativeLocalizer Localizer => this;
        public IMixedSpaceNativeGeoTransform GeoTransform { get; } = new MixedSpaceGeographicTransform();

        #region Runtime

        public void AttachLogSink(IntPtr callback)
        {
            MixedSpaceXrLocNative.RegisterLogCallback(callback);
        }

        /// <summary>
        /// 以 major.minor.patch 返回库版本；库缺失或调用被拒绝时返回 null，便于同时记录原生库与地图容器代际。
        /// Reports the library version as major.minor.patch. Returns null when the
        /// payload is missing or rejected the call, so callers can log the native
        /// generation next to the container generation they are trying to load.
        /// </summary>
        internal static string DescribeVersion()
        {
            int status = MixedSpaceXrLocNative.GetVersion(out MixedSpaceXrLocVersion version);
            return status == (int)MixedSpaceXrLocStatus.Ok
                ? $"{version.major}.{version.minor}.{version.patch}"
                : null;
        }

        #endregion Runtime

        #region Map storage

        public bool Import(int mapKey, byte[] payload)
        {
            if (payload == null)
                throw new ArgumentNullException(nameof(payload));
            if (payload.Length == 0)
                return false;
            if (MixedSpaceMapDefinition.IsRetiredKey(mapKey) ||
                MixedSpaceMapDefinition.HasLegacyHeader(payload))
            {
                MixedSpaceLogger.LogError($"Map {mapKey} uses a retired ID or container. An XrLoc map is required.");
                return false;
            }

            lock (m_Gate)
            {
                if (m_ResidentMaps.Contains(mapKey))
                    return true;
            }

            int status = MixedSpaceXrLocNative.LoadMap(mapKey, payload, new UIntPtr((ulong)payload.Length));

            if (status != (int)MixedSpaceXrLocStatus.Ok)
            {
                MixedSpaceLogger.LogError(
                    $"xrloc refused map {mapKey}: status={status} ({DescribeStatus(status)}), bytes={payload.Length}.");
                return false;
            }

            lock (m_Gate)
                m_ResidentMaps.Add(mapKey);
            MixedSpaceLogger.Log($"Loaded map {mapKey} into xrloc");
            return true;
        }

        public void Evict(int mapKey)
        {
            lock (m_Gate)
            {
                if (!m_ResidentMaps.Contains(mapKey))
                    return;
            }

            MixedSpaceXrLocNative.FreeMap(mapKey);
            lock (m_Gate)
                m_ResidentMaps.Remove(mapKey);
        }

        public Vector3[] ReadSparsePoints(int mapKey)
        {
            int status = MixedSpaceXrLocNative.PointsGetCount(mapKey, out int count);
            if (status != (int)MixedSpaceXrLocStatus.Ok || count <= 0)
                return Array.Empty<Vector3>();

            var buffer = new MixedSpaceXrLocPoint3D[count];
            GCHandle handle = default;
            try
            {
                handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
                status = MixedSpaceXrLocNative.PointsGet(
                    mapKey, handle.AddrOfPinnedObject(), count, out int written);
                if (status != (int)MixedSpaceXrLocStatus.Ok || written <= 0)
                    return Array.Empty<Vector3>();

                var points = new Vector3[written];
                for (int i = 0; i < written; i++)
                    points[i] = new Vector3(buffer[i].x, buffer[i].y, buffer[i].z);
                return points;
            }
            finally
            {
                if (handle.IsAllocated)
                    handle.Free();
            }
        }

        #endregion Map storage

        #region Frame encoding

        /// <summary>
        /// 将像素编码为独立 PNG。prepareForMatching 保留用于接口兼容。
        /// Encodes raw pixels into a standalone PNG. prepareForMatching is retained for interface compatibility.
        /// </summary>
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
            GCHandle encodedHandle = default;
            GCHandle pixelsHandle = default;
            try
            {
                encodedHandle = GCHandle.Alloc(encoded, GCHandleType.Pinned);
                pixelsHandle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
                int status = MixedSpaceXrLocNative.CaptureImage(
                    encodedHandle.AddrOfPinnedObject(),
                    encoded.Length,
                    pixelsHandle.AddrOfPinnedObject(),
                    width,
                    height,
                    channels,
                    out int captureSize);
                if (status != (int)MixedSpaceXrLocStatus.Ok ||
                    captureSize <= 0 ||
                    captureSize > encoded.Length)
                {
                    return Array.Empty<byte>();
                }

                Array.Resize(ref encoded, captureSize);
                return encoded;
            }
            finally
            {
                if (pixelsHandle.IsAllocated)
                    pixelsHandle.Free();
                if (encodedHandle.IsAllocated)
                    encodedHandle.Free();
            }
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
            if (pixelBuffer == IntPtr.Zero)
                return null;

            int[] mapIds = SnapshotResidentMaps();
            if (mapIds.Length == 0)
                return null;

            MixedSpaceXrLocCamera camera = BuildCamera(profile);
            float[] rotationPrior = BuildRotationPrior(profile);
            MixedSpaceXrLocPosePrior? positionPrior = BuildPositionPrior(mapIds, query);

            // 求解方式和角度由显式配置决定，定位失败不触发方向搜索。
            // Keep the configured solver and angle; localization misses never trigger orientation searches.
            int solver = SolverFor(query);
            int angle = ScreenRotationAngle(profile);
            int? priorMapKey = positionPrior?.mapId;
            if (m_LastLoggedSolver != solver || m_LastLoggedAngle != angle ||
                m_LastLoggedPriorMapKey != priorMapKey)
            {
                MixedSpaceLogger.Log(
                    $"[XRLOC-CONFIG] solver={solver} angle={angle} solverSource=RunPlan angleSource={(profile.NativeImageRotationAngle.HasValue ? "native-hint" : "display-rotation")} " +
                    $"rotationPrior=provided positionPriorMap={priorMapKey?.ToString() ?? "none"}",
                    MixedSpaceLogger.LoggingLevel.Verbose, nameof(MixedSpaceXrLocCore));
                m_LastLoggedSolver = solver;
                m_LastLoggedAngle = angle;
                m_LastLoggedPriorMapKey = priorMapKey;
            }
            if (!TryLocalize(
                    mapIds,
                    profile,
                    pixelBuffer,
                    camera,
                    rotationPrior,
                    solver,
                    angle,
                    positionPrior,
                    out MixedSpaceXrLocPose pose,
                    out int status,
                    out long elapsedMs))
            {
                // Report misses at most once per ten seconds (or when the status changes), including
                // the native search inputs. Cloud fallback otherwise hides these behind a generic miss.
                long now = System.Diagnostics.Stopwatch.GetTimestamp();
                if (m_LastReportedFailureStatus != status || now >= m_NextFailureReportTicks)
                {
                    m_LastReportedFailureStatus = status;
                    m_NextFailureReportTicks = now + 10L * System.Diagnostics.Stopwatch.Frequency;
                    MixedSpaceLogger.LogWarning(
                        $"[XRLOC-MISS] status={status} ({DescribeStatus(status)}) success={pose.success} " +
                        $"maps=[{DescribeResidentMaps(mapIds)}] solver={solver} angle={angle} " +
                        $"image={profile.PixelWidth}x{profile.PixelHeight}x{profile.ChannelCount} " +
                        $"intrinsics=({camera.fx},{camera.fy},{camera.cx},{camera.cy}) elapsedMs={elapsedMs}.");
                }

                return null;
            }

            m_LastReportedFailureStatus = null;
            return ToObservation(mapIds, pose);
        }

        /// <summary>
        /// 一次显式指定图像方向的原生定位调用。
        /// One native localization call with an explicit image orientation.
        /// </summary>
        private bool TryLocalize(
            int[] mapIds,
            MixedSpaceCaptureProfile profile,
            IntPtr pixelBuffer,
            MixedSpaceXrLocCamera camera,
            float[] rotationPrior,
            int solver,
            int rotationAngle,
            MixedSpaceXrLocPosePrior? positionPrior,
            out MixedSpaceXrLocPose pose,
            out int status,
            out long elapsedMs)
        {
            var image = new MixedSpaceXrLocImage
            {
                data = pixelBuffer,
                width = profile.PixelWidth,
                height = profile.PixelHeight,
                // 0 表示行连续，与采集缓冲区布局一致。
                // 0 means the rows are contiguous, which is how the capture buffer is laid out.
                stride = 0,
                format = ToImageFormat(profile.ChannelCount),
                rotationAngle = rotationAngle
            };

            MixedSpaceXrLocCamera localCamera = camera;
            var timer = System.Diagnostics.Stopwatch.StartNew();
            if (positionPrior is MixedSpaceXrLocPosePrior nativePrior)
            {
                status = MixedSpaceXrLocNative.LocalizeWithPrior(
                    mapIds, mapIds.Length, ref image, ref localCamera, solver, rotationPrior, ref nativePrior, out pose);
            }
            else
            {
                status = MixedSpaceXrLocNative.Localize(
                    mapIds, mapIds.Length, ref image, ref localCamera, solver, rotationPrior, out pose);
            }
            elapsedMs = timer.ElapsedMilliseconds;

            if (m_PositionPriorLogPath != null && positionPrior is MixedSpaceXrLocPosePrior loggedPrior)
            {
                MixedSpaceLocalizationJournal.AppendPositionPriorLog(
                    m_PositionPriorLogPath,
                    profile.CapturedAtNanoseconds,
                    mapIds,
                    solver,
                    rotationAngle,
                    rotationPrior,
                    loggedPrior,
                    status,
                    pose,
                    elapsedMs);
            }

            return status == (int)MixedSpaceXrLocStatus.Ok && pose.success != 0;
        }

        // 位置先验必须属于本次搜索的地图且坐标有限；只搜索一张地图时可推断未指定的地图 ID。
        // A position prior must have finite coordinates and belong to a requested map. Infer the map ID only for a single-map request.
        private static MixedSpaceXrLocPosePrior? BuildPositionPrior(
            int[] mapIds,
            MixedSpaceNativeLocalizationQuery query)
        {
            if (!query.HasPrior)
                return null;

            int mapKey = query.PriorMapKey ?? (mapIds.Length == 1 ? mapIds[0] : 0);
            Vector3 position = query.PriorPosition;
            if (mapKey <= 0 || Array.IndexOf(mapIds, mapKey) < 0 ||
                !IsFinite(position.x) || !IsFinite(position.y) || !IsFinite(position.z))
                return null;

            return new MixedSpaceXrLocPosePrior
            {
                mapId = mapKey,
                x = position.x,
                y = position.y,
                z = position.z
            };
        }

        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        private static int SolverFor(MixedSpaceNativeLocalizationQuery query) =>
            query != null && query.Solver == MixedSpaceSolverType.Lean ? 1 : 0;

        /// <summary>
        /// 以 id:points 列出驻留地图，区分图像无法匹配与地图为空或选错。
        /// "id:points" for each resident map, so a probe that matches nothing can be
        /// told apart from a probe running against an empty or wrong map.
        /// </summary>
        private static string DescribeResidentMaps(int[] mapIds)
        {
            var parts = new string[mapIds.Length];
            for (int i = 0; i < mapIds.Length; i++)
            {
                int status = MixedSpaceXrLocNative.PointsGetCount(mapIds[i], out int count);
                parts[i] = status == (int)MixedSpaceXrLocStatus.Ok
                    ? mapIds[i] + ":" + count
                    : mapIds[i] + ":err" + status;
            }

            return string.Join(",", parts);
        }

        /// <summary>
        /// 无标定方向路径的屏幕旋转；配置以纯 Z 滚转表示显示方向，xrloc 的顺时针约定相反，因此对符号取反。
        /// Screen rotation for the calibration-free image-orientation path. The
        /// profile carries the display orientation as a pure Z roll; xrloc documents
        /// its field as a clockwise angle, which is the opposite sign convention, so
        /// the roll is mirrored into it.
        /// </summary>
        private static int ScreenRotationAngle(MixedSpaceCaptureProfile profile)
        {
            if (profile.NativeImageRotationAngle.HasValue)
                return profile.NativeImageRotationAngle.Value;
            int counterClockwise = Mathf.RoundToInt(profile.DisplayRotation.eulerAngles.z) % 360;
            if (counterClockwise < 0)
                counterClockwise += 360;
            return (360 - counterClockwise) % 360;
        }

        private static MixedSpaceLocalizationObservation ToObservation(
            int[] mapIds,
            MixedSpaceXrLocPose pose)
        {
            int mapKey = pose.mapId;
            if (Array.IndexOf(mapIds, mapKey) < 0)
            {
                // C 头文件不保证每条成功路径都写 map_id；只有单地图时才可无歧义地回退。
                // The C header does not promise that map_id is written on every success path, so fall back when a single map
                // makes it unambiguous.
                if (mapIds.Length != 1)
                    return null;
                mapKey = mapIds[0];
            }

            if (mapKey <= 0)
                return null;

            return new MixedSpaceLocalizationObservation(
                mapKey,
                new Vector3((float)pose.px, (float)pose.py, (float)pose.pz),
                ToQuaternion(pose),
                pose.confidence);
        }

        private int[] SnapshotResidentMaps()
        {
            lock (m_Gate)
            {
                var ids = new int[m_ResidentMaps.Count];
                m_ResidentMaps.CopyTo(ids);
                // 固定排序使多地图搜索可重复。
                // Deterministic order keeps a multi-map search reproducible.
                Array.Sort(ids);
                return ids;
            }
        }

        private static MixedSpaceXrLocCamera BuildCamera(MixedSpaceCaptureProfile profile)
        {
            Vector4 intrinsics = profile.CameraIntrinsics;
            IReadOnlyList<double> distortion = profile.LensDistortion;
            return new MixedSpaceXrLocCamera
            {
                fx = intrinsics.x,
                fy = intrinsics.y,
                cx = intrinsics.z,
                cy = intrinsics.w,
                k1 = DistortionCoefficient(distortion, 0),
                k2 = DistortionCoefficient(distortion, 1),
                p1 = DistortionCoefficient(distortion, 2),
                p2 = DistortionCoefficient(distortion, 3)
            };
        }

        private static double DistortionCoefficient(IReadOnlyList<double> values, int index) =>
            values != null && index < values.Count ? values[index] : 0d;

        /// <summary>
        /// 合成跟踪和显示旋转，转换到原生手性，并按 C API 要求序列化 XYZW。
        /// Composes tracker and display rotations, reflects into the native frame and serializes in XYZW order.
        /// </summary>
        private static float[] BuildRotationPrior(MixedSpaceCaptureProfile profile)
        {
            Quaternion orientation = MixedSpaceCoordinateFrame.ReflectXAxis(
                profile.TrackerRotation * profile.DisplayRotation);
            return MixedSpaceCoordinateFrame.CopyQuaternionComponents(orientation);
        }

        /// <summary>
        /// 依据实际通道数而非语义像素格式推导布局，因为原生端需要据此计算行大小。
        /// Derives the layout from the real channel count rather than the semantic
        /// pixel format, because the native side needs the byte layout to size rows.
        /// </summary>
        private static MixedSpaceXrLocImageFormat ToImageFormat(int channelCount)
        {
            switch (channelCount)
            {
                case 4:
                    return MixedSpaceXrLocImageFormat.Rgba;
                case 3:
                    return MixedSpaceXrLocImageFormat.Rgb;
                default:
                    return MixedSpaceXrLocImageFormat.Gray;
            }
        }

        /// <summary>
        /// 从行优先相机到世界矩阵提取 Unity 旋转，排除平移列以保持纯旋转，满足四元数提取要求。
        /// Builds a Unity rotation from the row-major camera-to-world matrix. The
        /// translation column is excluded so the matrix stays a pure rotation, which
        /// is what Quaternion extraction requires.
        /// </summary>
        private static Quaternion ToQuaternion(MixedSpaceXrLocPose pose)
        {
            var matrix = new Matrix4x4
            {
                m00 = (float)pose.r00,
                m01 = (float)pose.r01,
                m02 = (float)pose.r02,
                m03 = 0f,
                m10 = (float)pose.r10,
                m11 = (float)pose.r11,
                m12 = (float)pose.r12,
                m13 = 0f,
                m20 = (float)pose.r20,
                m21 = (float)pose.r21,
                m22 = (float)pose.r22,
                m23 = 0f,
                m30 = 0f,
                m31 = 0f,
                m32 = 0f,
                m33 = 1f
            };
            return matrix.rotation;
        }

        internal static string DescribeStatus(int status)
        {
            var code = (MixedSpaceXrLocStatus)status;
            return Enum.IsDefined(typeof(MixedSpaceXrLocStatus), code)
                ? code.ToString()
                : "Unknown";
        }

        #endregion Localization
    }
}
