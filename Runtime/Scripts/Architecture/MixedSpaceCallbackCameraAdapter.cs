using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace MixedSpace.XR
{
    /// <summary>
    /// 回调式相机共用的生命周期与帧资源管理，接受紧密排列的 NV21 或单通道亮度数据。
    /// 设备 API 由子类实现；图像与实时位姿时间戳应使用一致的单调时钟，
    /// 并通过显式单位配置换算为纳秒，不能通过数值大小猜测单位。
    /// Shared lifecycle and frame ownership for callback cameras accepting tightly packed NV21 or
    /// luminance data. Subclasses own device APIs. Capture and live-pose timestamps should share a monotonic clock
    /// and use explicitly configured units when converted to nanoseconds.
    /// </summary>
    public abstract class MixedSpaceCallbackCameraAdapter : MonoBehaviour, IMixedSpacePlatformAdapter, IMixedSpaceCaptureDiagnostics
    {
        public enum TimestampUnit { Unconfirmed, Nanoseconds, Microseconds, Milliseconds }
        [SerializeField] private MixedSpaceRokidProfile m_Profile;
        private MixedSpaceFisheyeProcessor m_FisheyeProcessor;
        private TimestampUnit m_TimestampUnit;
        private int m_StartupTimeoutMilliseconds;
        private int m_MaxFrameAgeMilliseconds;
        private Vector3 m_ImageOrientationEuler;
        public MixedSpaceRokidProfile Profile => m_Profile;
        private readonly object m_Gate = new object();
        private Frame m_Latest;
        private bool m_Accepting;
        private bool m_Opened;
        private bool m_Ready;
        private int m_Generation;
        private long m_ConsumedTimestamp = long.MinValue;
        private Task<bool> m_Starting;
        private bool m_Paused;
        private bool m_ResumeRequested;
        private bool m_WritePoseLogs;
        private readonly TimestampProbe m_CaptureTimestampProbe = new TimestampProbe();
        private readonly TimestampProbe m_LiveTimestampProbe = new TimestampProbe();
        // 帧快照导出：用于人工核对相机原始画面。
        // Frame snapshots for manual inspection of raw camera images.
        private int m_DebugSnapshotRemaining;
        private string m_DebugSnapshotDirectory;
        // 相机线程只入队，Unity 线程在 Update 里落盘（Texture2D 是主线程 API）。
        // The camera thread only queues data; Unity Update writes files because Texture2D requires the main thread.
        private Queue<SnapshotRequest> m_SnapshotQueue;
        private const int SnapshotQueueLimit = 4;

        public void SetWritePoseLogs(bool enabled) => m_WritePoseLogs = enabled;

        /// <summary>
        /// 将后续至多 <paramref name="count"/> 张 NV21 回调图像加入快照导出队列，
        /// 保存到 <paramref name="directory"/>，包含原始数据和亮度 PNG。
        /// 队列已满时不接收该帧；导出不改变调度策略，但复制和主线程落盘会产生诊断开销。
        /// 单通道 ReceiveLuminance 路径不使用此快照队列。
        /// Queues up to count subsequent NV21 frames for raw-data and luminance-PNG export to directory.
        /// Full queues skip frames. Export preserves scheduling policy but adds copying and main-thread I/O overhead.
        /// ReceiveLuminance does not use this queue.
        /// </summary>
        public void SetFrameSnapshotCapture(string directory, int count)
        {
            m_DebugSnapshotDirectory = directory;
            m_DebugSnapshotRemaining = Mathf.Max(0, count);
            MixedSpaceLogger.Log(
                $"Frame snapshot capture configured: dir={directory} count={m_DebugSnapshotRemaining}", MixedSpaceLogger.LoggingLevel.Verbose);
        }

        /// <summary>
        /// 相机回调侧复制原始数据和亮度数据并入队，不调用 Unity 对象 API 或写文件。
        /// Texture2D 创建和 PNG 编码等操作交给主线程 Update 执行。
        /// Copies raw and luminance data on the camera callback thread without Unity object APIs or file
        /// I/O. Main-thread Update creates textures and encodes PNGs.
        /// </summary>
        private void QueueFrameSnapshot(byte[] pixels, int width, int height, long timestamp)
        {
            if (string.IsNullOrEmpty(m_DebugSnapshotDirectory)) return;

            // 先预留队列名额，避免约 50 fps 的相机回调快于 Update 时不断积压图像缓冲区。
            // Reserve a slot first: the camera pushes ~50 fps and Update runs at frame rate, so without this the queue would
            // grow unboundedly and retain hundreds of buffers.
            lock (m_Gate)
            {
                if (m_DebugSnapshotRemaining <= 0) return;
                if (m_SnapshotQueue == null) m_SnapshotQueue = new Queue<SnapshotRequest>(4);
                if (m_SnapshotQueue.Count >= SnapshotQueueLimit) return;
                m_DebugSnapshotRemaining--;
            }

            int count = checked(width * height);
            var luminance = new byte[count];
            Buffer.BlockCopy(pixels, 0, luminance, 0, count);
            var request = new SnapshotRequest
            {
                Luminance = luminance,
                Raw = (byte[])pixels.Clone(),
                Width = width,
                Height = height,
                Timestamp = timestamp
            };

            lock (m_Gate) { m_SnapshotQueue.Enqueue(request); }
        }

        /// <summary>
        /// 主线程消费快照队列，每次最多处理 SnapshotQueueLimit 项。
        /// 先保存原始 NV21 数据，再生成便于查看的亮度 PNG。
        /// Drains at most SnapshotQueueLimit items on the main thread, saving raw NV21 before producing
        /// luminance PNG previews.
        /// </summary>
        private void DrainFrameSnapshot()
        {
            int drained = 0;
            while (true)
            {
                SnapshotRequest request;
                lock (m_Gate)
                {
                    if (m_SnapshotQueue == null || m_SnapshotQueue.Count == 0) return;
                    request = m_SnapshotQueue.Dequeue();
                }
                WriteFrameSnapshot(request);
                // 限制每帧处理量，避免积压阻塞 Unity 主线程。
                // Bound the work per frame so a backlog cannot stall the Unity thread.
                if (++drained >= SnapshotQueueLimit) return;
            }
        }

        private void WriteFrameSnapshot(SnapshotRequest request)
        {
            try
            {
                System.IO.Directory.CreateDirectory(m_DebugSnapshotDirectory);
                string stem = System.IO.Path.Combine(m_DebugSnapshotDirectory,
                    $"frame_{request.Timestamp}_{request.Width}x{request.Height}");

                // 保留完整的厂商原始布局，供逐字节检查。
                // Untrimmed vendor layout, preserved for byte-level inspection.
                System.IO.File.WriteAllBytes(stem + ".nv21", request.Raw);

                // PNG 仅使用 Y 平面，缓冲区长度必须为 width*height 字节。
                // Use only the Y plane for PNG previews; the buffer must contain exactly width*height bytes.
                var texture = new Texture2D(request.Width, request.Height, TextureFormat.Alpha8, false);
                try
                {
                    texture.LoadRawTextureData(request.Luminance);
                    texture.Apply();
                    byte[] png = texture.EncodeToPNG();
                    if (png != null) System.IO.File.WriteAllBytes(stem + ".png", png);
                }
                finally
                {
                    UnityEngine.Object.Destroy(texture);
                }

                MixedSpaceLogger.Log($"Frame snapshot saved: {stem}.png / .nv21", MixedSpaceLogger.LoggingLevel.Verbose);
            }
            catch (Exception exception)
            {
                MixedSpaceLogger.LogError($"Frame snapshot export failed: {exception.Message}");
            }
        }

        /// <summary>
        /// 相机线程与 Unity 线程之间的交接记录。
        /// Hand-off record between the camera thread and the Unity thread.
        /// </summary>
        private sealed class SnapshotRequest
        {
            public byte[] Luminance;
            public byte[] Raw;
            public int Width, Height;
            public long Timestamp;
        }

        /// <summary>
        /// 可由 Inspector 调用，不打开相机或调用原生代码。
        /// Can be called by the Inspector without opening a camera or invoking native code.
        /// </summary>
        public void ValidateConfiguration()
        {
            if (!m_Profile) throw new InvalidOperationException("Assign DefaultRokidProfile to the Rokid adapter.");
            var settings = m_Profile.CreateSettings();
            if (!Enum.IsDefined(typeof(TimestampUnit), settings.TimestampUnit) || settings.TimestampUnit == TimestampUnit.Unconfirmed)
                throw new InvalidOperationException("Confirm device timestamp units before enabling camera fusion.");
            if (settings.StartupTimeoutMilliseconds <= 0 || settings.MaxFrameAgeMilliseconds <= 0)
                throw new InvalidOperationException("Camera timeout and maximum frame age must be positive.");
            if (!Finite(settings.ImageOrientationEuler.x) || !Finite(settings.ImageOrientationEuler.y) || !Finite(settings.ImageOrientationEuler.z))
                throw new InvalidOperationException("Image orientation must be finite.");
            new MixedSpaceXrLocFisheyeProcessor(settings).ValidateConfiguration();
        }

        private sealed class Frame
        {
            public byte[] Pixels;
            public int Width, Height;
            public long DeviceTime, ReceivedAt;
        }

        protected abstract void OpenCamera();
        protected abstract void CloseCamera();
        /// <summary>
        /// 可选的原生图像方向参数，不改变显示方向或位姿坐标转换。
        /// Optional native image-angle override, independent of display orientation and pose conversion.
        /// </summary>
        protected virtual int? NativeLocalizationImageRotationAngle => null;

        protected abstract bool IsDeviceTracking { get; }
        protected abstract Pose ReadCurrentPose(out long timestamp);
        protected abstract Pose ReadCapturePose(long timestamp);
        protected abstract void ReadCalibration(out Vector4 intrinsics, out Vector4 distortion, out float alpha);

        /// <summary>
        /// 可从原生相机回调线程调用，在返回厂商前复制 Y 平面。
        /// 输入必须是紧密排列的 NV21；有行填充的设备应先转换布局。
        /// 此路径不调用 Unity 对象 API，诊断图像仅入队并由主线程 Update 导出。
        /// May run on a native camera callback thread and copies Y before returning to the vendor. Input
        /// must be tightly packed NV21; convert padded rows first. Unity object APIs are avoided, and queued diagnostics
        /// are exported by main-thread Update.
        /// </summary>
        protected void ReceiveNv21(int width, int height, byte[] pixels, long timestamp)
        {
            if (width <= 0 || height <= 0 || (width & 1) != 0 || (height & 1) != 0 || timestamp <= 0)
                return;
            long count = (long)width * height;
            if (pixels == null || count > int.MaxValue || pixels.LongLength != count + count / 2)
                return;
            lock (m_Gate)
            {
                if (!m_Accepting || (m_Latest != null && timestamp <= m_Latest.DeviceTime)) return;
                var copy = new byte[(int)count];
                Buffer.BlockCopy(pixels, 0, copy, 0, copy.Length);
                m_Latest = new Frame { Pixels = copy, Width = width, Height = height,
                    DeviceTime = timestamp, ReceivedAt = Stopwatch.GetTimestamp() };
            }
            // 帧快照：仅入队，落盘由 Update() 在 Unity 线程完成。
            // Snapshots only enqueue here; Unity-thread Update writes them to disk.
            QueueFrameSnapshot(pixels, width, height, timestamp);
        }

        /// <summary>
        /// 接收厂商相机回调提供的紧密排列单通道帧。
        /// Accept a tightly packed single-channel frame from a vendor camera callback.
        /// </summary>
        protected void ReceiveLuminance(int width, int height, byte[] pixels, long timestamp)
        {
            if (width <= 0 || height <= 0 || timestamp <= 0 || pixels == null) return;
            long count = (long)width * height;
            if (count > int.MaxValue || pixels.LongLength != count) return;
            lock (m_Gate)
            {
                if (!m_Accepting || (m_Latest != null && timestamp <= m_Latest.DeviceTime)) return;
                var copy = new byte[(int)count];
                Buffer.BlockCopy(pixels, 0, copy, 0, copy.Length);
                m_Latest = new Frame { Pixels = copy, Width = width, Height = height,
                    DeviceTime = timestamp, ReceivedAt = Stopwatch.GetTimestamp() };
            }
        }
        /// <summary>
        /// 快照导出的 Unity 主线程部分；相机线程不操作 Texture2D，只交接帧，由此消费队列。
        /// Unity-thread half of the snapshot export. The camera thread cannot touch
        /// Texture2D, so it hands frames over and this drains them.
        /// </summary>
        private void Update()
        {
            DrainFrameSnapshot();
        }

        public Task<bool> StartAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (m_Paused || !isActiveAndEnabled)
                throw new InvalidOperationException("Camera cannot start while paused or disabled.");
            if (m_Ready) return Task.FromResult(true);
            if (m_Starting != null && !m_Starting.IsCompleted) return m_Starting;
            return m_Starting = StartCoreAsync(cancellationToken);
        }

        private async Task<bool> StartCoreAsync(CancellationToken token)
        {
            ValidateConfiguration();
            var settings = m_Profile.CreateSettings();
            m_TimestampUnit = settings.TimestampUnit;
            m_StartupTimeoutMilliseconds = settings.StartupTimeoutMilliseconds;
            m_MaxFrameAgeMilliseconds = settings.MaxFrameAgeMilliseconds;
            m_ImageOrientationEuler = settings.ImageOrientationEuler;
            m_FisheyeProcessor = new MixedSpaceXrLocFisheyeProcessor(settings);
            m_FisheyeProcessor.ValidateAvailability();
            int generation = ++m_Generation;
            long started = Stopwatch.GetTimestamp();
            try
            {
                lock (m_Gate) { m_Latest = null; m_Accepting = true; }
                m_ConsumedTimestamp = long.MinValue;
                m_Opened = true; // CloseCamera 也必须清理只完成一部分的 OpenCamera。 / CloseCamera must also unwind a partially completed OpenCamera.
                OpenCamera();
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    if (generation != m_Generation) throw new OperationCanceledException("Camera startup was stopped.");
                    lock (m_Gate)
                        if (m_Latest != null) { m_Ready = true; return true; }
                    if (ElapsedMilliseconds(started) >= m_StartupTimeoutMilliseconds)
                        throw new TimeoutException("Camera did not provide a valid packed NV21 frame before timeout.");
                    await Task.Delay(25, token);
                }
            }
            catch
            {
                if (generation == m_Generation) await ShutdownAsync();
                throw;
            }
        }

        public Task<IMixedSpacePlatformFrame> CaptureFrameAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            // 会话恢复与相机重启是独立回调；等待相机恢复期间不提交旧帧，也不在每个周期反复报错。
            // Session resume and camera restart are independent callbacks. Do not submit an old frame, or raise one error
            // per cycle, while waiting for the restarted camera.
            if (!m_Ready)
            {
                return Task.FromResult<IMixedSpacePlatformFrame>(
                    new MixedSpacePlatformFrame(new MixedSpaceTrackingSnapshot(0), null));
            }
            bool tracking = IsDeviceTracking;
            Frame frame;
            lock (m_Gate) frame = m_Latest;
            if (!tracking || frame == null || frame.DeviceTime == m_ConsumedTimestamp ||
                ElapsedMilliseconds(frame.ReceivedAt) > m_MaxFrameAgeMilliseconds)
            {

                return Task.FromResult<IMixedSpacePlatformFrame>(new MixedSpacePlatformFrame(
                    new MixedSpaceTrackingSnapshot(tracking ? 1 : 0), null));
            }


            Pose pose = ReadCapturePose(frame.DeviceTime);
            ValidatePose(pose);
            ReadCalibration(out var intrinsics, out var distortion, out float alpha);
            if (!Finite(intrinsics.x) || !Finite(intrinsics.y) || !Finite(intrinsics.z) || !Finite(intrinsics.w) ||
                intrinsics.x <= 0 || intrinsics.y <= 0 || !Finite(alpha) ||
                !Finite(distortion.x) || !Finite(distortion.y) || !Finite(distortion.z) || !Finite(distortion.w))
                throw new InvalidOperationException(
                    "Camera calibration is invalid; refusing native rectification. " +
                    $"intrinsics={intrinsics} distortion={distortion} alpha={alpha}");
            // 处理器可能修改输入，因此先复制邮箱快照以保护原数据。
            // Protect the mailbox snapshot from a processor that modifies its input buffer.
            MixedSpaceRectifiedImage image = m_FisheyeProcessor.Rectify((byte[])frame.Pixels.Clone(),
                frame.Width, frame.Height, intrinsics, distortion, alpha);
            token.ThrowIfCancellationRequested();
            if (image == null) throw new InvalidOperationException("Fisheye processor returned no image.");
            long captureTimestampNs = ToNanoseconds(frame.DeviceTime);
            TraceTimestamp("image", frame.DeviceTime, captureTimestampNs, frame.ReceivedAt, m_CaptureTimestampProbe, true);
            var profile = new MixedSpaceCaptureProfile(image.Width, image.Height, 1,
                MixedSpacePixelFormat.Luminance8, image.Intrinsics, pose.position, pose.rotation,
                captureTimestampNs, Quaternion.Euler(m_ImageOrientationEuler),
                nativeImageRotationAngle: NativeLocalizationImageRotationAngle);
            var capture = new MixedSpaceCameraCapture(profile, new MixedSpaceManagedPixelSource(image.Pixels));
            m_ConsumedTimestamp = frame.DeviceTime;
            if (m_WritePoseLogs) MixedSpaceCaptureJournal.Append(profile);
            return Task.FromResult<IMixedSpacePlatformFrame>(new MixedSpacePlatformFrame(
                new MixedSpaceTrackingSnapshot(1), capture));
        }

        public MixedSpacePlatformPoseSample SamplePose()
        {
            if (!m_Ready || !IsDeviceTracking) return new MixedSpacePlatformPoseSample { IsTracking = false };
            Pose pose = ReadCurrentPose(out long timestamp);
            if (timestamp <= 0) return new MixedSpacePlatformPoseSample { IsTracking = false };
            ValidatePose(pose);
            long poseTimestampNs = ToNanoseconds(timestamp);
            TraceTimestamp("GetCameraPhysicsPose", timestamp, poseTimestampNs, Stopwatch.GetTimestamp(), m_LiveTimestampProbe, false);
            return new MixedSpacePlatformPoseSample { Position = pose.position, Rotation = pose.rotation,
                TimestampNanoseconds = poseTimestampNs, IsTracking = true };
        }

        public Task ShutdownAsync()
        {
            ++m_Generation;
            m_Starting = null; // 新启动不必等待已经失效的启动尝试。 / A new start need not join an invalidated startup attempt.
            m_ResumeRequested = false;
            m_Ready = false;
            lock (m_Gate) { m_Accepting = false; m_Latest = null; }
            if (m_Opened) { m_Opened = false; CloseCamera(); }
            return Task.CompletedTask;
        }

        private sealed class TimestampProbe
        {
            internal long LastRaw, LoggedRaw, LoggedNs, LoggedHost;
            internal int BackwardCount;
        }

        private void TraceTimestamp(string source, long raw, long nanoseconds, long hostTicks, TimestampProbe probe, bool everySample)
        {
            if (probe.LastRaw > 0 && raw < probe.LastRaw) ++probe.BackwardCount;
            probe.LastRaw = raw;
            double elapsed = probe.LoggedHost == 0 ? 0 :
                (hostTicks - probe.LoggedHost) / (double)Stopwatch.Frequency;
            if (!everySample && probe.LoggedHost != 0 && elapsed < 1.0) return;
            string text = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "[TIME-CHECK] source={0} configured_unit={1} raw={2} converted_ns={3} raw_delta={4} ns_delta={5} host_delta_seconds={6:F6} raw_units_per_second={7:F3} backwards_since_last_log={8}",
                source, m_TimestampUnit, raw, nanoseconds,
                probe.LoggedHost == 0 ? 0 : raw - probe.LoggedRaw,
                probe.LoggedHost == 0 ? 0 : nanoseconds - probe.LoggedNs,
                elapsed, elapsed > 0 ? (raw - probe.LoggedRaw) / elapsed : 0, probe.BackwardCount);
            probe.LoggedRaw = raw;
            probe.LoggedNs = nanoseconds;
            probe.LoggedHost = hostTicks;
            probe.BackwardCount = 0;
            try
            {
                MixedSpacePoseFusionLogFile.Initialize();
                MixedSpacePoseFusionLogFile.Append(2, text);
                MixedSpaceLogger.Log(text, MixedSpaceLogger.LoggingLevel.Verbose, "TimestampCheck");
            }
            catch { } // Diagnostic failures must not interrupt localization.
        }

        private long ToNanoseconds(long timestamp)
        {
            switch (m_TimestampUnit)
            {
                case TimestampUnit.Nanoseconds: return timestamp;
                case TimestampUnit.Microseconds: return Scale(timestamp, 1000L);
                case TimestampUnit.Milliseconds: return Scale(timestamp, 1000000L);
                default: throw new InvalidOperationException("Device clock unit is unconfirmed.");
            }
        }

        private long Scale(long timestamp, long factor)
        {
            if (timestamp > long.MaxValue / factor || timestamp < long.MinValue / factor)
                throw new InvalidOperationException(
                    $"Device timestamp {timestamp} does not fit the configured clock unit " +
                    $"'{m_TimestampUnit}'. Check MixedSpaceRokidProfile.TimestampUnit.");
            return timestamp * factor;
        }
        private static double ElapsedMilliseconds(long start) =>
            (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        private static void ValidatePose(Pose pose)
        {
            var p = pose.position;
            var q = pose.rotation;
            float norm = q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w;
            if (!Finite(p.x) || !Finite(p.y) || !Finite(p.z) || !Finite(norm) || Math.Abs(norm - 1f) > 0.05f)
                throw new InvalidOperationException("Camera pose is non-finite or rotation is not normalized.");
        }

        /// <summary>
        /// 相机所有权跟随应用暂停，不执行全局重居中。
        /// Camera ownership follows application pause; no global recenter is performed.
        /// </summary>
        protected virtual async void OnApplicationPause(bool paused)
        {
            if (m_Paused == paused) return;
            m_Paused = paused;
            try
            {
                if (paused)
                {
                    bool resume = m_Opened;
                    await ShutdownAsync();
                    m_ResumeRequested = resume;
                }
                else if (m_ResumeRequested && isActiveAndEnabled)
                {
                    m_ResumeRequested = false;
                    await StartAsync(CancellationToken.None);
                }
            }
            catch (OperationCanceledException)
            {
                // 显式关闭优先于重启。
                // Explicit shutdown supersedes restart.
            }
            catch (Exception error) { UnityEngine.Debug.LogException(error); }
        }

        protected virtual void OnDisable()
        {
            try { _ = ShutdownAsync(); }
            catch (Exception error) { UnityEngine.Debug.LogException(error); }
        }

        protected virtual void OnDestroy()
        {
            try { _ = ShutdownAsync(); }
            catch (Exception error) { UnityEngine.Debug.LogException(error); }
        }
    }
}
