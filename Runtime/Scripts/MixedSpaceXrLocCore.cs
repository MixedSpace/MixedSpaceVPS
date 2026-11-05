/* xrloc 核心负责运行配置、地图、定位和编码；地理转换与设备建图委托给共享的 PosePlugin 实例。
   Provides runtime configuration, maps, localization and encoding through xrloc.
   Geographic conversion and on-device mapping use the shared PosePlugin core. */

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using MixedSpace.XR;
using UnityEngine;

namespace MixedSpace
{
    /// <summary>
    /// 通过 libxrloc 提供地图、定位和编码，地理转换和建图委托给共享的 PosePlugin 核心。地图驻留索引用托管锁保护，方向探测需调用方串行定位；整个对象不保证任意并发调用安全。
    /// Maps, localization and encoding use libxrloc; geographic conversion and mapping delegate to a
    /// shared PosePlugin-backed core. Managed locks protect map residency, while callers must serialize
    /// orientation-probing localization. This does not guarantee arbitrary concurrent use of the whole object.
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

        private readonly object m_Gate = new object();
        private readonly HashSet<int> m_ResidentMaps = new HashSet<int>();
        private readonly MixedSpaceNativeCore m_PluginFallback;


        public MixedSpaceXrLocCore()
            : this(null)
        {
        }

        public MixedSpaceXrLocCore(MixedSpaceNativeCore pluginFallback)
        {
            m_PluginFallback = pluginFallback ?? new MixedSpaceNativeCore();
        }

        public IMixedSpaceNativeRuntime Runtime => this;
        public IMixedSpaceNativeMapStore Maps => this;
        public IMixedSpaceNativeFrameEncoder FrameEncoder => this;
        public IMixedSpaceNativeLocalizer Localizer => this;
        // xrloc 不提供 icvPos/icvRot/icvMap，因此复用 PosePlugin 核心。
        // xrloc exports no icvPos / icvRot / icvMap family; reuse the PosePlugin core.
        public IMixedSpaceNativeGeoTransform GeoTransform => m_PluginFallback.GeoTransform;
        public IMixedSpaceNativeMappingWorkspace Mapping => m_PluginFallback.Mapping;

        /// <summary>
        /// 至少一个容器驻留时为 true，表示定位有可搜索地图。
        /// True while at least one container is resident, i.e. localization has something to
        /// search.
        /// </summary>
        internal bool HasLoadedMaps
        {
            get
            {
                lock (m_Gate)
                    return m_ResidentMaps.Count > 0;
            }
        }

        #region Runtime

        public void AttachLogSink(IntPtr callback)
        {
            MixedSpaceXrLocNative.RegisterLogCallback(callback);
        }

        /// <summary>
        /// 空操作：xrloc 无 LocalizationMaxPixels 对应项，自行建立图像金字塔，仅暴露 NumThreads 和 ImageCompressionLevel。
        /// No-op. The PosePlugin budget knob ("LocalizationMaxPixels") has no xrloc
        /// counterpart; xrloc sizes its own pyramid and only exposes "NumThreads"
        /// and "ImageCompressionLevel".
        /// </summary>
        public void ApplyImagePixelBudget(int maximumPixels)
        {
            if (maximumPixels < 0)
                throw new ArgumentOutOfRangeException(nameof(maximumPixels));
        }

        public int ResolveLicenseLevel(string developerToken)
        {
            return MixedSpaceXrLocNative.VerifyDeveloperToken(developerToken);
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
        /// 将像素编码为独立 PNG，忽略 prepareForMatching，因为 xrloc 不提供 icvCaptureImage 的匹配准备模式。
        /// Encodes raw pixels into PNG. prepareForMatching is ignored: unlike
        /// icvCaptureImage, the xrloc encoder has no matching-preparation mode and
        /// always emits a standalone image.
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
            float[] prior = BuildRotationPrior(profile);

            // A successful orientation probe is not evidence that its solver is
            // appropriate. In particular, never replace configured solver 0 with
            // solver 1 (rotation-prior mode) after one successful startup frame.
            int solver = SolverFor(query);
            int angle = ScreenRotationAngle(profile);
            if (m_LastLoggedSolver != solver || m_LastLoggedAngle != angle)
            {
                MixedSpaceLogger.Log($"[XRLOC-CONFIG] solver={solver} angle={angle} source=RunPlan orientation={(solver == 1 ? "rotation-prior" : "image-angle")}",
                    MixedSpaceLogger.LoggingLevel.Verbose, nameof(MixedSpaceXrLocCore));
                m_LastLoggedSolver = solver;
                m_LastLoggedAngle = angle;
            }
            if (!TryLocalize(
                    mapIds,
                    profile,
                    pixelBuffer,
                    camera,
                    prior,
                    solver,
                    angle,
                    out MixedSpaceXrLocPose pose,
                    out int status,
                    out long elapsedMs))
            {
                // 状态 Ok 但无位姿是正常未命中；定位服务已低频报告持续未命中，此处仅为意外状态写日志，避免逐帧刷屏。
                // Ok with no pose is the ordinary miss: the call ran to completion and found nothing in frame. Writing that once
                // per frame buries the log, and the localization service already reports a continuing run of misses at a low
                // rate, so only an unexpected status earns a line here.
                if (status != (int)MixedSpaceXrLocStatus.Ok)
                {
                    MixedSpaceLogger.Log(
                        $"xrloc localization returned status={status} ({DescribeStatus(status)}) after {elapsedMs} ms across {mapIds.Length} map(s).",
                        MixedSpaceLogger.LoggingLevel.Verbose,
                        nameof(MixedSpaceXrLocCore));
                }

                return null;
            }

            return ToObservation(mapIds, pose);
        }

        /// <summary>
        /// 一次显式指定图像方向的原生定位调用。
        /// One native localization call with an explicit image orientation.
        /// </summary>
        private static bool TryLocalize(
            int[] mapIds,
            MixedSpaceCaptureProfile profile,
            IntPtr pixelBuffer,
            MixedSpaceXrLocCamera camera,
            float[] prior,
            int solver,
            int rotationAngle,
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
            status = MixedSpaceXrLocNative.Localize(
                mapIds, mapIds.Length, ref image, ref localCamera, solver, prior, out pose);
            elapsedMs = timer.ElapsedMilliseconds;

            return status == (int)MixedSpaceXrLocStatus.Ok && pose.success != 0;
        }

        /// <summary>
        /// 历史方向诊断辅助函数，不由正式定位路径调用，不修改后续帧的解算配置。
        /// Offline diagnostic helper only; never called by the production path and
        /// never overrides the caller's solver or orientation on subsequent frames.
        /// </summary>
        private MixedSpaceLocalizationObservation ProbeImageOrientationForDiagnostics(
            int[] mapIds,
            MixedSpaceCaptureProfile profile,
            IntPtr pixelBuffer,
            MixedSpaceXrLocCamera camera,
            float[] prior)
        {
            int derived = ScreenRotationAngle(profile);
            DumpProbeFrame(mapIds, profile, pixelBuffer, "a");
            var candidates = new List<int[]>(6)
            {
                new[] { 1, derived },   // 由先验四元数决定方向，忽略角度字段。 / prior quaternion decides; angle is ignored
                new[] { 0, derived },
                new[] { 0, 0 }
            };
            foreach (int angle in new[] { 90, 180, 270 })
                candidates.Add(new[] { 0, angle });

            MixedSpaceLogger.Log(
                $"[XRLOC-ORIENT] probe start: maps=[{DescribeResidentMaps(mapIds)}] " +
                $"displayZ={profile.DisplayRotation.eulerAngles.z} derivedAngle={derived}", MixedSpaceLogger.LoggingLevel.Verbose);

            var seen = new HashSet<string>();
            for (int i = 0; i < candidates.Count; i++)
            {
                int solver = candidates[i][0];
                int angle = candidates[i][1];
                string key = solver + ":" + angle;
                if (!seen.Add(key))
                    continue;

                bool ok = TryLocalize(
                    mapIds, profile, pixelBuffer, camera, prior, solver, angle,
                    out MixedSpaceXrLocPose pose, out int status, out long elapsedMs);
                MixedSpaceLogger.Log(
                    $"[XRLOC-ORIENT] solver={solver} angle={angle} -> status={status} success={(ok ? 1 : 0)} {elapsedMs}ms", MixedSpaceLogger.LoggingLevel.Verbose);

                if (!ok)
                    continue;

                MixedSpaceLogger.Log($"[XRLOC-ORIENT] diagnostic-only solver={solver} angle={angle} confidence={pose.confidence}", MixedSpaceLogger.LoggingLevel.Verbose);
                return ToObservation(mapIds, pose);
            }

            // 对同一租约再次取样；字节相同说明是冻结快照，而非持续变化的相机缓冲区。
            // Second sample of the same lease: identical bytes mean the buffer is a frozen snapshot rather than a live
            // frame.
            DumpProbeFrame(mapIds, profile, pixelBuffer, "b");
            MixedSpaceLogger.Log("[XRLOC-ORIENT] no orientation convention matched; falling back to the steady-state path.", MixedSpaceLogger.LoggingLevel.Verbose);
            return null;
        }

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
        /// 定义 MIXEDSPACE_CAPTURE_DIAGNOSTICS 时导出方向探测帧、参数及亮度统计。
        /// Exports orientation probe frames, parameters and luminance statistics when
        /// MIXEDSPACE_CAPTURE_DIAGNOSTICS is defined.
        /// </summary>
        [System.Diagnostics.Conditional("MIXEDSPACE_CAPTURE_DIAGNOSTICS")]
        private static void DumpProbeFrame(
            int[] mapIds,
            MixedSpaceCaptureProfile profile,
            IntPtr pixelBuffer,
            string stage)
        {
            try
            {
                int channels = profile.ChannelCount == 0 ? 1 : profile.ChannelCount;
                int size = checked(profile.PixelWidth * profile.PixelHeight * channels);
                var bytes = new byte[size];
                Marshal.Copy(pixelBuffer, bytes, 0, size);

                int min = 255, max = 0;
                long total = 0;
                int distinct = 0;
                var seenLevels = new bool[256];
                for (int i = 0; i < bytes.Length; i++)
                {
                    int value = bytes[i];
                    if (value < min) min = value;
                    if (value > max) max = value;
                    total += value;
                    if (!seenLevels[value])
                    {
                        seenLevels[value] = true;
                        distinct++;
                    }
                }

                Vector4 k = profile.CameraIntrinsics;
                Quaternion tracker = profile.TrackerRotation;
                Quaternion display = profile.DisplayRotation;
                var distortion = new List<string>();
                if (profile.LensDistortion != null)
                    foreach (double value in profile.LensDistortion)
                        distortion.Add(value.ToString("R"));

                string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + "_" + stage;
                string rawPath = Path.Combine(Application.persistentDataPath, "xrloc_frame_" + stamp + ".raw");
                string jsonPath = Path.Combine(Application.persistentDataPath, "xrloc_frame_" + stamp + ".json");
                File.WriteAllBytes(rawPath, bytes);
                File.WriteAllText(jsonPath, string.Concat(
                    "{\n",
                    "  \"raw\": \"", Path.GetFileName(rawPath), "\",\n",
                    "  \"width\": ", profile.PixelWidth, ",\n",
                    "  \"height\": ", profile.PixelHeight, ",\n",
                    "  \"channels\": ", channels, ",\n",
                    "  \"pixelFormat\": \"", profile.PixelFormat, "\",\n",
                    "  \"lensDistortion\": [", string.Join(", ", distortion.ToArray()), "],\n",
                    "  \"fx\": ", k.x.ToString("R"), ", \"fy\": ", k.y.ToString("R"),
                    ", \"cx\": ", k.z.ToString("R"), ", \"cy\": ", k.w.ToString("R"), ",\n",
                    "  \"trackerRotation\": [",
                        tracker.x.ToString("R"), ", ", tracker.y.ToString("R"), ", ",
                        tracker.z.ToString("R"), ", ", tracker.w.ToString("R"), "],\n",
                    "  \"displayRotation\": [",
                        display.x.ToString("R"), ", ", display.y.ToString("R"), ", ",
                        display.z.ToString("R"), ", ", display.w.ToString("R"), "],\n",
                    "  \"displayEulerZ\": ", display.eulerAngles.z.ToString("R"), ",\n",
                    "  \"mapIds\": [", string.Join(", ", Array.ConvertAll(mapIds, id => id.ToString())), "],\n",
                    "  \"min\": ", min, ", \"max\": ", max, ", \"mean\": ",
                        bytes.Length == 0 ? "0" : ((double)total / bytes.Length).ToString("F2"), ",\n",
                    "  \"distinctLevels\": ", distinct, "\n",
                    "}\n"));

                MixedSpaceLogger.Log(
                    $"[XRLOC-DUMP] frame[{stage}] {profile.PixelWidth}x{profile.PixelHeight}x{channels} " +
                    $"min={min} max={max} mean={(bytes.Length == 0 ? 0d : (double)total / bytes.Length):F2} " +
                    $"levels={distinct} -> {rawPath}", MixedSpaceLogger.LoggingLevel.Verbose);
            }
            catch (Exception error)
            {
                MixedSpaceLogger.LogWarning($"[XRLOC-DUMP] frame dump failed: {error.Message}");
            }
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
        /// 与 PosePlugin icvLocalize 一致：合成跟踪和显示旋转，转换到原生手性，并按 C API 要求序列化 XYZW。
        /// Same orientation the PosePlugin path hands to icvLocalize: the tracker and
        /// display rotations composed, then reflected into the native handedness and
        /// serialized XYZW as the C API expects.
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
