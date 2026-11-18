using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace MixedSpace.XR
{
    /// <summary>
    /// 回调式相机共用的生命周期与帧资源管理，接受紧密排列的 NV21 或单通道亮度数据。
    /// 图像回调和实时位姿默认使用同一个系统单调时钟，历史位姿查询使用原始图像时间戳。
    /// Shared lifecycle and frame ownership for callback cameras accepting tightly packed NV21 or luminance data.
    /// Image callbacks and live pose reads use a shared host monotonic clock by default.
    /// Vendor history lookups use the original image timestamp.
    /// </summary>
    public abstract class MixedSpaceCallbackCameraAdapter : MonoBehaviour, IMixedSpacePlatformAdapter, IMixedSpaceCaptureDiagnostics
    {
        public enum TimestampUnit { Unconfirmed, Nanoseconds, Microseconds, Milliseconds, Auto }
        [SerializeField] private MixedSpaceRokidProfile m_Profile;
        private MixedSpaceFisheyeProcessor m_FisheyeProcessor;
        private TimestampUnit m_TimestampUnit;
        private TimestampUnit m_ExpectedTimestampUnit;
        private readonly MixedSpaceTimestampUnitDetector m_ImageClock = new MixedSpaceTimestampUnitDetector(Stopwatch.Frequency);
        private readonly MixedSpaceTimestampUnitDetector m_PoseClock = new MixedSpaceTimestampUnitDetector(Stopwatch.Frequency);
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
        private long m_ConsumedFrameReceivedAt = long.MinValue;
        private Task<bool> m_Starting;
        private bool m_Paused;
        private bool m_ResumeRequested;
        private bool m_WritePoseLogs;
        private float m_NextTrackingDiagnosticTime;
        private readonly TimestampProbe m_CaptureTimestampProbe = new TimestampProbe();
        private readonly TimestampProbe m_LiveTimestampProbe = new TimestampProbe();
        protected bool WritePoseLogs => m_WritePoseLogs;

        public void SetWritePoseLogs(bool enabled)
        {
            lock (m_Gate) m_WritePoseLogs = enabled;
        }

        /// <summary>
        /// 可由 Inspector 调用，不打开相机或调用原生代码。
        /// Can be called by the Inspector without opening a camera or invoking native code.
        /// </summary>
        public void ValidateConfiguration()
        {
            if (!m_Profile) throw new InvalidOperationException("Assign DefaultRokidProfile to the Rokid adapter.");
            var settings = m_Profile.CreateSettings();
            if (!UseHostMonotonicTimestamps &&
                (!Enum.IsDefined(typeof(TimestampUnit), settings.TimestampUnit) || settings.TimestampUnit == TimestampUnit.Unconfirmed))
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

        /// <summary>
        /// 默认使用本机单调时钟：图像取回调到达时间，实时位姿取读取完成时间。
        /// By default, the host monotonic clock timestamps images at callback arrival and live poses after the read.
        /// </summary>
        protected virtual bool UseHostMonotonicTimestamps => true;

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
                long receivedAt = Stopwatch.GetTimestamp();
                if (!m_Accepting ||
                    (!UseHostMonotonicTimestamps && !m_ImageClock.Observe(timestamp, receivedAt))) return;
                var copy = new byte[(int)count];
                Buffer.BlockCopy(pixels, 0, copy, 0, copy.Length);
                m_Latest = new Frame { Pixels = copy, Width = width, Height = height,
                    DeviceTime = timestamp, ReceivedAt = receivedAt };
            }
            // 帧快照：仅入队，落盘由 Update() 在 Unity 线程完成。
            // Snapshots only enqueue here; Unity-thread Update writes them to disk.
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
                long receivedAt = Stopwatch.GetTimestamp();
                if (!m_Accepting ||
                    (!UseHostMonotonicTimestamps && !m_ImageClock.Observe(timestamp, receivedAt))) return;
                var copy = new byte[(int)count];
                Buffer.BlockCopy(pixels, 0, copy, 0, copy.Length);
                m_Latest = new Frame { Pixels = copy, Width = width, Height = height,
                    DeviceTime = timestamp, ReceivedAt = receivedAt };
            }
        }
        // 在常规跟踪更新之后，对照物理相机与 Unity 相机；仅诊断，不写任何场景位姿。
        // Compare physical and Unity camera poses after Update without moving scene objects.
        private void LateUpdate()
        {
            if (!m_WritePoseLogs || !m_Ready || !isActiveAndEnabled ||
                Time.realtimeSinceStartup < m_NextTrackingDiagnosticTime) return;
            m_NextTrackingDiagnosticTime = Time.realtimeSinceStartup + 0.2f;
            try { MixedSpaceLocalizationJournal.AppendTrackingDiagnostic(SamplePose()); }
            catch (Exception error)
            {
                MixedSpaceLogger.LogError("Tracking diagnostic failed (" + error.GetType().Name + ").");
            }
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
            m_ExpectedTimestampUnit = settings.TimestampUnit;
            m_TimestampUnit = UseHostMonotonicTimestamps ? TimestampUnit.Nanoseconds : TimestampUnit.Unconfirmed;
            m_StartupTimeoutMilliseconds = settings.StartupTimeoutMilliseconds;
            m_MaxFrameAgeMilliseconds = settings.MaxFrameAgeMilliseconds;
            m_ImageOrientationEuler = settings.ImageOrientationEuler;
            m_FisheyeProcessor = new MixedSpaceXrLocFisheyeProcessor(settings);
            m_FisheyeProcessor.ValidateAvailability();
            int generation = ++m_Generation;
            long started = Stopwatch.GetTimestamp();
            try
            {
                lock (m_Gate)
                {
                    m_Latest = null;
                    m_ImageClock.Reset();
                    m_PoseClock.Reset();
                    m_Accepting = true;
                }
                m_CaptureTimestampProbe.Reset();
                m_LiveTimestampProbe.Reset();
                m_ConsumedFrameReceivedAt = long.MinValue;
                m_Opened = true; // CloseCamera 也必须清理只完成一部分的 OpenCamera。 / CloseCamera must also unwind a partially completed OpenCamera.
                OpenCamera();
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    if (generation != m_Generation) throw new OperationCanceledException("Camera startup was stopped.");
                    if (IsReadyToCapture())
                    {
                        m_Ready = true;
                        return true;
                    }

                    if (ElapsedMilliseconds(started) >= m_StartupTimeoutMilliseconds)
                    {
                        throw new TimeoutException(UseHostMonotonicTimestamps
                            ? "Camera startup timed out waiting for valid frames and tracking."
                            : "Camera startup timed out waiting for valid frames, tracking and consistent timestamp units.");
                    }
                    await Task.Delay(25, token);
                }
            }
            catch
            {
                if (generation == m_Generation) await ShutdownAsync();
                throw;
            }
        }

        private bool IsReadyToCapture()
        {
            bool tracking = IsDeviceTracking;
            if (UseHostMonotonicTimestamps)
            {
                lock (m_Gate) return tracking && m_Latest != null;
            }

            if (tracking) ObserveCurrentPose(out _, out _, out _, out _);
            EnsureTimestampClocksHealthy();
            return TryConfirmTimestampUnit();
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
            EnsureTimestampClocksHealthy();
            bool tracking = IsDeviceTracking;
            Frame frame;
            lock (m_Gate) frame = m_Latest;
            if (!tracking || frame == null || frame.ReceivedAt == m_ConsumedFrameReceivedAt ||
                ElapsedMilliseconds(frame.ReceivedAt) > m_MaxFrameAgeMilliseconds)
            {

                return Task.FromResult<IMixedSpacePlatformFrame>(new MixedSpacePlatformFrame(
                    new MixedSpaceTrackingSnapshot(tracking ? 1 : 0), null));
            }


            // 设备的历史位姿仍以图像的原始设备时间戳查询。
            // Look up the vendor pose history with the original camera timestamp.
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
            long captureTimestampNs;
            if (UseHostMonotonicTimestamps)
            {
                captureTimestampNs = HostTicksToNanoseconds(frame.ReceivedAt);
            }
            else
            {
                lock (m_Gate)
                    if (!m_ImageClock.TryToNanoseconds(frame.DeviceTime, out captureTimestampNs))
                        throw new InvalidOperationException("Image timestamp cannot be converted using the verified clock unit.");
                EnsureTimestampClocksHealthy();
            }
            TraceTimestamp("image", frame.DeviceTime, captureTimestampNs, frame.ReceivedAt, m_CaptureTimestampProbe, true);
            var profile = new MixedSpaceCaptureProfile(image.Width, image.Height, 1,
                MixedSpacePixelFormat.Luminance8, image.Intrinsics, pose.position, pose.rotation,
                captureTimestampNs, Quaternion.Euler(m_ImageOrientationEuler),
                nativeImageRotationAngle: NativeLocalizationImageRotationAngle);
            var capture = new MixedSpaceCameraCapture(profile, new MixedSpaceManagedPixelSource(image.Pixels));
            m_ConsumedFrameReceivedAt = frame.ReceivedAt;
            if (m_WritePoseLogs) MixedSpaceCaptureJournal.Append(profile);
            return Task.FromResult<IMixedSpacePlatformFrame>(new MixedSpacePlatformFrame(
                new MixedSpaceTrackingSnapshot(1), capture));
        }

        public MixedSpacePlatformPoseSample SamplePose()
        {
            if (!m_Ready || !IsDeviceTracking) return new MixedSpacePlatformPoseSample { IsTracking = false };
            EnsureTimestampClocksHealthy();
            bool valid = ObserveCurrentPose(out Pose pose, out long timestamp, out long poseTimestampNs, out long sampledAt);
            EnsureTimestampClocksHealthy();
            if (!valid) return new MixedSpacePlatformPoseSample { IsTracking = false };
            TraceTimestamp("GetCameraPhysicsPose", timestamp, poseTimestampNs, sampledAt, m_LiveTimestampProbe, false);
            return new MixedSpacePlatformPoseSample
            {
                Position = pose.position,
                Rotation = pose.rotation,
                TimestampNanoseconds = poseTimestampNs,
                TimestampSource = UseHostMonotonicTimestamps
                    ? MixedSpaceTrackingTimestampSource.HostMonotonic
                    : MixedSpaceTrackingTimestampSource.Device,
                IsTracking = true
            };
        }

        public Task ShutdownAsync()
        {
            ++m_Generation;
            m_Starting = null; // 新启动不必等待已经失效的启动尝试。 / A new start need not join an invalidated startup attempt.
            m_ResumeRequested = false;
            m_Ready = false;
            lock (m_Gate)
            {
                m_Accepting = false;
                m_Latest = null;
                m_ImageClock.Reset();
                m_PoseClock.Reset();
            }
            m_TimestampUnit = TimestampUnit.Unconfirmed;
            if (m_Opened) { m_Opened = false; CloseCamera(); }
            return Task.CompletedTask;
        }

        private sealed class TimestampProbe
        {
            internal long LastRaw, LoggedRaw, LoggedNs, LoggedHost;
            internal int BackwardCount;
            internal void Reset()
            {
                LastRaw = LoggedRaw = LoggedNs = LoggedHost = 0;
                BackwardCount = 0;
            }
        }

        private void TraceTimestamp(string source, long raw, long nanoseconds, long hostTicks, TimestampProbe probe, bool everySample)
        {
            if (!m_WritePoseLogs && MixedSpaceLogger.Level > MixedSpaceLogger.LoggingLevel.Verbose) return;
            if (probe.LastRaw > 0 && raw < probe.LastRaw) ++probe.BackwardCount;
            probe.LastRaw = raw;
            double elapsed = probe.LoggedHost == 0 ? 0 :
                (hostTicks - probe.LoggedHost) / (double)Stopwatch.Frequency;
            if (!everySample && probe.LoggedHost != 0 && elapsed < 1.0) return;
            string text = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "[TIME-CHECK] source={0} configured_unit={1} raw={2} converted_ns={3} raw_delta={4} ns_delta={5} host_delta_seconds={6:F6} raw_units_per_second={7:F3} backwards_since_last_log={8} timestamp_source={9}",
                source, m_TimestampUnit, raw, nanoseconds,
                probe.LoggedHost == 0 ? 0 : raw - probe.LoggedRaw,
                probe.LoggedHost == 0 ? 0 : nanoseconds - probe.LoggedNs,
                elapsed, elapsed > 0 ? (raw - probe.LoggedRaw) / elapsed : 0, probe.BackwardCount,
                UseHostMonotonicTimestamps ? "HostMonotonic" : "Device");
            probe.LoggedRaw = raw;
            probe.LoggedNs = nanoseconds;
            probe.LoggedHost = hostTicks;
            probe.BackwardCount = 0;
            try
            {
                if (m_WritePoseLogs)
                {
                    MixedSpacePoseFusionLogFile.Initialize();
                    MixedSpacePoseFusionLogFile.Append(2, text);
                }
                MixedSpaceLogger.Log(text, MixedSpaceLogger.LoggingLevel.Verbose, "TimestampCheck");
            }
            catch { } // Diagnostic failures must not interrupt localization.
        }

        private bool ObserveCurrentPose(out Pose pose, out long raw, out long nanoseconds, out long sampledAt)
        {
            pose = ReadCurrentPose(out raw);
            sampledAt = Stopwatch.GetTimestamp();
            nanoseconds = 0;
            if (!UseHostMonotonicTimestamps && raw <= 0) return false;
            ValidatePose(pose);

            if (UseHostMonotonicTimestamps)
            {
                nanoseconds = HostTicksToNanoseconds(sampledAt);
                return true;
            }

            lock (m_Gate)
                return m_PoseClock.TryObserveCurrentTimestamp(raw, sampledAt, out nanoseconds);
        }

        private bool TryConfirmTimestampUnit()
        {
            long scale;
            double imageRate, poseRate;
            lock (m_Gate)
            {
                if (m_Latest == null || !m_ImageClock.Ready || !m_PoseClock.Ready) return false;
                scale = m_ImageClock.NanosecondsPerUnit;
                if (scale != m_PoseClock.NanosecondsPerUnit)
                    throw new InvalidOperationException("Image and pose clocks use different units; the adapter must align its history-pose query before fusion.");
                imageRate = m_ImageClock.RawUnitsPerSecond;
                poseRate = m_PoseClock.RawUnitsPerSecond;
            }
            m_TimestampUnit = scale == 1 ? TimestampUnit.Nanoseconds :
                scale == 1000 ? TimestampUnit.Microseconds : TimestampUnit.Milliseconds;
            string diagnostic = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "[TIME-UNIT] expected={0} detected={1} scale_to_ns={2} action={3} image_raw_units_per_second={4:F3} pose_raw_units_per_second={5:F3}",
                m_ExpectedTimestampUnit, m_TimestampUnit, scale, scale == 1 ? "passthrough" : "convert",
                imageRate, poseRate);
            try
            {
                if (m_WritePoseLogs)
                {
                    MixedSpacePoseFusionLogFile.Initialize();
                    MixedSpacePoseFusionLogFile.Append(2, diagnostic);
                }
                if (scale != 1 || (m_ExpectedTimestampUnit != TimestampUnit.Auto && m_ExpectedTimestampUnit != m_TimestampUnit))
                    MixedSpaceLogger.LogWarning(diagnostic);
                else
                    MixedSpaceLogger.Log(diagnostic, MixedSpaceLogger.LoggingLevel.Verbose);
            }
            catch { } // Diagnostic I/O must not reject a verified clock.
            return true;
        }

        private void EnsureTimestampClocksHealthy()
        {
            if (UseHostMonotonicTimestamps) return;

            string failure;
            lock (m_Gate)
                failure = m_ImageClock.FailureReason == null ?
                    (m_PoseClock.FailureReason == null ? null : "pose: " + m_PoseClock.FailureReason) :
                    "image: " + m_ImageClock.FailureReason;
            if (failure != null)
            {
                _ = ShutdownAsync();
                throw new InvalidOperationException(
                    "[TIME-UNIT] " + failure + " No further timestamps will be submitted to fusion.");
            }
        }
        protected static long HostTicksToNanoseconds(long ticks) =>
            (long)(ticks * (1_000_000_000.0 / Stopwatch.Frequency));

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
