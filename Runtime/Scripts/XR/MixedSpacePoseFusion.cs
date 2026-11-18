using System;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace MixedSpace.XR
{
    /// <summary>
    /// 默认原生位姿融合实现；平台无关场景代码依赖 IMixedSpacePoseFusion，不直接依赖 C ABI。
    /// Default native pose-fusion implementation. All platform-independent scene
    /// code depends on IMixedSpacePoseFusion rather than the C ABI directly.
    /// </summary>
    public sealed class MixedSpacePoseFusion : IMixedSpacePoseFusion
    {
        public const int ConfigurationNotSupported = -1001;
        private IntPtr m_Handle;
        private MixedSpacePoseFusionConfig m_Config = MixedSpacePoseFusionConfig.Default;
        private bool m_ReportedConfigUnavailable;
        private bool m_SupportsAngleThresholds;
        private bool m_ReportedAngleThresholdsUnavailable;
        private long m_NextTimestampLogTicks;

        internal bool WritePoseLogs { private get; set; }

        private bool ShouldTraceTimestamps => WritePoseLogs ||
            MixedSpaceLogger.Level <= MixedSpaceLogger.LoggingLevel.Verbose;

        public int Initialize()
        {
            if (m_Handle != IntPtr.Zero)
                return 0;

            try
            {
                if (WritePoseLogs) MixedSpacePoseFusionLogFile.Initialize();
#if UNITY_ANDROID && !UNITY_EDITOR
                MixedSpacePoseFusionLogBridge.Register();
#endif
                using var logScope = new MixedSpacePoseFusionLogBridge.FileScope(WritePoseLogs);
                int status = MixedSpacePoseFusionNative.Create(out m_Handle);
                if (status != 0) return status;
                if (m_Handle == IntPtr.Zero) return -2;
                m_SupportsAngleThresholds = MixedSpacePoseFusionNative.SupportsAngleThresholds();
                status = ApplyStoredConfig();
                if (status != 0) Dispose();
                return status;
            }
            catch (DllNotFoundException exception)
            {
                MixedSpaceLogger.LogWarning(
                    $"Pose fusion native library unavailable: {exception.Message}");
                return -1;
            }
        }

        public int SetConfig(MixedSpacePoseFusionConfig config)
        {
            config.Validate();
            if (m_Handle == IntPtr.Zero) return -2;
            int status;
            try
            {
                using var logScope = new MixedSpacePoseFusionLogBridge.FileScope(WritePoseLogs);
                status = MixedSpacePoseFusionNative.SetConfig(m_Handle, ref config, m_SupportsAngleThresholds);
            }
            catch (EntryPointNotFoundException) { status = ConfigurationNotSupported; }
            if (status == 0)
            {
                m_Config = config;
                if (!m_SupportsAngleThresholds && !m_ReportedAngleThresholdsUnavailable &&
                    (config.refine_threshold_angle != MixedSpacePoseFusionConfig.Default.refine_threshold_angle ||
                     config.diff_angle_threshold != MixedSpacePoseFusionConfig.Default.diff_angle_threshold))
                {
                    m_ReportedAngleThresholdsUnavailable = true;
                    MixedSpaceLogger.LogWarning("PoseFusion 1.2.0 or later is required to apply the refine and difference angle thresholds.");
                }
            }
            else if (status == ConfigurationNotSupported && !m_ReportedConfigUnavailable)
            {
                m_ReportedConfigUnavailable = true;
                MixedSpaceLogger.LogWarning("PoseFusionSetConfig 不受当前原生库支持（code=-1001），配置未应用；融合继续使用库内默认值。请更新 pose_fusion 库。");
            }
            return status;
        }

        private int ApplyStoredConfig()
        {
            int status = SetConfig(m_Config);
            // 缺少配置入口时，会话继续使用原生库默认值。
            // The session uses native defaults when the configuration entry point is unavailable.
            return status == ConfigurationNotSupported ? 0 : status;
        }

        public int AddLocalization(MixedSpacePoseFusionLocalization localization)
        {
            using var logScope = new MixedSpacePoseFusionLogBridge.FileScope(WritePoseLogs);
            int status = MixedSpacePoseFusionNative.AddLocalization(m_Handle, ref localization);
            if (ShouldTraceTimestamps)
                TraceFusionTimestamp("PoseFusionAddLocalizeInfo", localization.TimestampNanoseconds, status);
            return status;
        }

        public int GetTransform(long timestampNanoseconds, float[] trackingPose, float[] outputTransform)
        {
            using var logScope = new MixedSpacePoseFusionLogBridge.FileScope(WritePoseLogs);
            int status = MixedSpacePoseFusionNative.GetTransform(
                m_Handle,
                timestampNanoseconds,
                trackingPose,
                outputTransform);
            if (ShouldTraceTimestamps)
            {
                long now = System.Diagnostics.Stopwatch.GetTimestamp();
                if (now >= m_NextTimestampLogTicks)
                {
                    m_NextTimestampLogTicks = now + System.Diagnostics.Stopwatch.Frequency;
                    TraceFusionTimestamp("PoseFusionGetTransform", timestampNanoseconds, status);
                }
            }
            return status;
        }

        private void TraceFusionTimestamp(string api, long timestampNanoseconds, int status)
        {
            try
            {
                string text = $"[TIME-CHECK] api={api} input_timestamp_ns={timestampNanoseconds} return_code={status}";
                if (WritePoseLogs)
                    MixedSpacePoseFusionLogFile.Append(2, text);
                MixedSpaceLogger.Log(text, MixedSpaceLogger.LoggingLevel.Verbose, "TimestampCheck");
            }
            catch { }
        }

        public int Reset()
        {
            if (m_Handle == IntPtr.Zero) return 0;
            using var logScope = new MixedSpacePoseFusionLogBridge.FileScope(WritePoseLogs);
            int status = MixedSpacePoseFusionNative.Reset(m_Handle);
            // 原生重置成功后，重新应用已保存的会话配置。
            // Reapply the stored session configuration after a successful native reset.
            return status == 0 ? ApplyStoredConfig() : status;
        }

        public int ConvertUnityToNativePose(float[] unityMatrix, float[] nativePose)
        {
            using var logScope = new MixedSpacePoseFusionLogBridge.FileScope(WritePoseLogs);
            return MixedSpacePoseFusionNative.ConvertUnityToPose(unityMatrix, nativePose);
        }

        public int ConvertNativePoseToUnity(float[] nativePose, float[] unityMatrix)
        {
            using var logScope = new MixedSpacePoseFusionLogBridge.FileScope(WritePoseLogs);
            return MixedSpacePoseFusionNative.ConvertPoseToUnity(nativePose, unityMatrix);
        }

        public void Dispose()
        {
            if (m_Handle == IntPtr.Zero)
                return;

            using var logScope = new MixedSpacePoseFusionLogBridge.FileScope(WritePoseLogs);
            MixedSpacePoseFusionNative.Destroy(m_Handle);
            m_Handle = IntPtr.Zero;
        }
    }

    /// <summary>
    /// 为每个 MixedSpaceWorldRoot 创建独立融合会话。
    /// Creates an independent fusion session for each MixedSpaceWorldRoot.
    /// </summary>
    public sealed class MixedSpacePoseFusionFactory : IMixedSpacePoseFusionFactory
    {
        public IMixedSpacePoseFusion Create() => new MixedSpacePoseFusion();
    }

    /// <summary>Process-wide Android log callback, retained for the entire native library lifetime.</summary>
    internal static class MixedSpacePoseFusionLogBridge
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate void LogCallback(int level, IntPtr message, IntPtr userData);

        // Never unregister when an individual world closes: other fusion instances share this callback.
        private static readonly LogCallback Callback = Receive;
        private static readonly object Gate = new object();
        private static bool s_Attempted;
        [ThreadStatic] private static bool s_WritePoseLogs;

        // The callback has no session handle. Associate synchronous messages with the calling fusion
        // instance, and leave callbacks outside a native call on the normal Console path.
        internal readonly struct FileScope : IDisposable
        {
            private readonly bool m_Previous;

            internal FileScope(bool enabled)
            {
                m_Previous = s_WritePoseLogs;
                s_WritePoseLogs = enabled;
            }

            public void Dispose() => s_WritePoseLogs = m_Previous;
        }

        internal static void Register()
        {
            lock (Gate)
            {
                if (s_Attempted) return;
                s_Attempted = true;
                try
                {
                    int status = MixedSpacePoseFusionNative.SetLogCallback(Callback, IntPtr.Zero);
                    if (status != 0)
                        MixedSpaceLogger.LogWarning($"PoseFusionSetLogCallback failed with code {status}.");
                }
                catch (EntryPointNotFoundException)
                {
                    // Older Android libraries can continue fusing without managed log forwarding.
                    MixedSpaceLogger.LogWarning("PoseFusionSetLogCallback unavailable; update the Android pose_fusion library to 1.1.2 or later.");
                }
            }
        }

        [AOT.MonoPInvokeCallback(typeof(LogCallback))]
        private static void Receive(int level, IntPtr message, IntPtr userData)
        {
            // Native message memory is valid only during this callback. Copy before returning;
            // never allow a managed exception to cross the native boundary.
            if (message == IntPtr.Zero) return;
            try
            {
                string text = Marshal.PtrToStringUTF8(message);
                if (string.IsNullOrEmpty(text)) return;
                if (s_WritePoseLogs)
                    MixedSpacePoseFusionLogFile.Append(level, text);
                if (level >= 4) MixedSpaceLogger.LogError(text, "PoseFusionNative");
                else if (level == 3) MixedSpaceLogger.LogWarning(text, "PoseFusionNative");
                else MixedSpaceLogger.Log(text, MixedSpaceLogger.LoggingLevel.Verbose, "PoseFusionNative");
            }
            catch { }
        }
    }

    // File I/O runs outside the synchronous native callback. The bounded queue limits diagnostic memory.
    internal static class MixedSpacePoseFusionLogFile
    {
        private const int Capacity = 1024;
        private static readonly object Gate = new object();
        private static readonly Queue<string> Pending = new Queue<string>();
        private static string s_Path;
        private static Task s_Worker;
        private static long s_Dropped;
        private static bool s_Initialized;
        private static bool s_ReportedFailure;

        internal static void Initialize()
        {
            // Called on the Unity thread, never resolve Application.persistentDataPath in a native worker callback.
            lock (Gate)
            {
                if (s_Initialized) return;
                s_Path = MixedSpacePoseLogFiles.GetPath("pose_fusion_native.log");
                s_Initialized = true;
            }
            UnityEngine.Application.quitting += Flush;
        }

        internal static void Append(int level, string message)
        {
            string severity = level >= 4 ? "ERROR" : level == 3 ? "WARNING" : level == 2 ? "INFO" : "DEBUG";
            string line = DateTime.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture) +
                " [" + severity + "] " + MixedSpaceLogText.Redact(message).Replace("\r", "\\r").Replace("\n", "\\n");
            lock (Gate)
            {
                if (!s_Initialized) return;
                if (Pending.Count >= Capacity) { ++s_Dropped; return; }
                Pending.Enqueue(line);
                if (s_Worker == null) s_Worker = Task.Run((Action)Drain);
            }
        }

        private static void Drain()
        {
            try
            {
                while (true)
                {
                    var batch = new StringBuilder();
                    lock (Gate)
                    {
                        if (Pending.Count == 0 && s_Dropped == 0)
                        {
                            s_Worker = null;
                            return;
                        }
                        for (int i = 0; i < 64 && Pending.Count != 0; ++i)
                            batch.AppendLine(Pending.Dequeue());
                        if (s_Dropped != 0)
                        {
                            batch.AppendLine("[WARNING] Native log queue overflow; dropped messages=" + s_Dropped);
                            s_Dropped = 0;
                        }
                    }
                    // The handle is closed before another worker can take ownership.
                    File.AppendAllText(s_Path, batch.ToString(), new UTF8Encoding(false));
                }
            }
            catch (Exception error)
            {
                bool report;
                lock (Gate)
                {
                    s_Worker = null;
                    Pending.Clear();
                    report = !s_ReportedFailure;
                    s_ReportedFailure = true;
                }
                if (report)
                    MixedSpaceLogger.LogWarning("Pose fusion native log write failed (" + error.GetType().Name + ").");
            }
        }

        private static void Flush()
        {
            Task worker;
            lock (Gate) worker = s_Worker;
            try { worker?.Wait(2000); }
            catch { }
        }
    }

    internal static class MixedSpacePoseFusionNative
    {
        private const string NativeLibrary =
#if UNITY_IOS && !UNITY_EDITOR
            "__Internal";
#elif UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
            "pose_fusion";
#else
            "libpose_fusion";
#endif

        [DllImport(NativeLibrary, EntryPoint = "PoseFusionSetLogCallback", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int SetLogCallback(MixedSpacePoseFusionLogBridge.LogCallback callback, IntPtr userData);

        [DllImport(NativeLibrary, EntryPoint = "PoseFusionCreate", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int Create(out IntPtr poseFusion);

#if UNITY_IOS && !UNITY_EDITOR
        // iOS 通过弱符号桥接调用可选入口，兼容缺少这些符号的静态库。
        // The iOS shim uses weak imports so static libraries without these symbols still link.
        private const string GetVersionEntryPoint = "MixedSpacePoseFusionGetVersionIfAvailable";
        private const string SetConfigEntryPoint = "MixedSpacePoseFusionSetConfigIfAvailable";
#else
        private const string GetVersionEntryPoint = "PoseFusionGetVersion";
        private const string SetConfigEntryPoint = "PoseFusionSetConfig";
#endif

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeVersion
        {
            public int Major;
            public int Minor;
            public int Patch;
        }

        // 旧布局共 20 字节，3DoF 标志位于字节偏移 16；1.2.0 新增两个旋转阈值后，该偏移变为 24。
        // The 20-byte legacy layout places 3DoF at byte offset 16. Version 1.2.0 adds two rotation thresholds, moving it to offset 24.
        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct LegacyConfig
        {
            public float x_axis_angle_threshold;
            public float z_axis_angle_threshold;
            public float refine_threshold_dist;
            public float diff_trans_threshold;
            [MarshalAs(UnmanagedType.I1)] public bool enable_three_dof;
        }

        internal static bool SupportsAngleThresholds()
        {
            try
            {
                return GetVersion(out NativeVersion version) == 0 &&
                    (version.Major > 1 || (version.Major == 1 && version.Minor >= 2));
            }
            catch (EntryPointNotFoundException) { return false; }
        }

        internal static int SetConfig(IntPtr poseFusion, ref MixedSpacePoseFusionConfig config,
            bool supportsAngleThresholds)
        {
            if (supportsAngleThresholds)
                return SetConfigV12(poseFusion, ref config);

            var legacyConfig = new LegacyConfig
            {
                x_axis_angle_threshold = config.x_axis_angle_threshold,
                z_axis_angle_threshold = config.z_axis_angle_threshold,
                refine_threshold_dist = config.refine_threshold_dist,
                diff_trans_threshold = config.diff_trans_threshold,
                enable_three_dof = config.enable_three_dof
            };
            return SetLegacyConfig(poseFusion, ref legacyConfig);
        }

        [DllImport(NativeLibrary, EntryPoint = GetVersionEntryPoint, CallingConvention = CallingConvention.Cdecl)]
        private static extern int GetVersion(out NativeVersion version);

        [DllImport(NativeLibrary, EntryPoint = SetConfigEntryPoint, CallingConvention = CallingConvention.Cdecl)]
        private static extern int SetConfigV12(IntPtr poseFusion, ref MixedSpacePoseFusionConfig config);

        [DllImport(NativeLibrary, EntryPoint = SetConfigEntryPoint, CallingConvention = CallingConvention.Cdecl)]
        private static extern int SetLegacyConfig(IntPtr poseFusion, ref LegacyConfig config);

        [DllImport(NativeLibrary, EntryPoint = "PoseFusionAddLocalizeInfo", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int AddLocalization(
            IntPtr poseFusion,
            ref MixedSpacePoseFusionLocalization localization);

        [DllImport(NativeLibrary, EntryPoint = "PoseFusionGetTransform", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int GetTransform(
            IntPtr poseFusion,
            long timestampNanoseconds,
            float[] trackingPose,
            float[] outputTransform);

        [DllImport(NativeLibrary, EntryPoint = "PoseFusionReset", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int Reset(IntPtr poseFusion);

        [DllImport(NativeLibrary, EntryPoint = "PoseFusionDestroy", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int Destroy(IntPtr poseFusion);

        [DllImport(NativeLibrary, EntryPoint = "PoseFusionConvertPoseToUnity", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int ConvertPoseToUnity(float[] nativePose, float[] unityMatrix);

        [DllImport(NativeLibrary, EntryPoint = "PoseFusionConvertUnityToPose", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int ConvertUnityToPose(float[] unityMatrix, float[] nativePose);
    }
}
