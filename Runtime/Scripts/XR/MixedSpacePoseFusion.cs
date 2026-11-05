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
        private long m_NextTimestampLogTicks;

        public int Initialize()
        {
            if (m_Handle != IntPtr.Zero)
                return 0;

            try
            {
#if UNITY_ANDROID && !UNITY_EDITOR
                MixedSpacePoseFusionLogBridge.Register();
#endif
                int status = MixedSpacePoseFusionNative.Create(out m_Handle);
                if (status != 0) return status;
                if (m_Handle == IntPtr.Zero) return -2;
                status = ApplyStoredConfig();
                if (status != 0) Dispose();
                return status;
            }
            catch (DllNotFoundException exception)
            {
                // 原生库不可用时返回状态码，使 World 可以回退到已求解位姿。
                // Let the world fall back to its solved pose when the native payload is unavailable.
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
            try { status = MixedSpacePoseFusionNative.SetConfig(m_Handle, ref config); }
            catch (EntryPointNotFoundException) { status = ConfigurationNotSupported; }
            if (status == 0) m_Config = config;
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
            // Older payloads may continue fusing, but never pretend that custom thresholds were applied.
            return status == ConfigurationNotSupported ? 0 : status;
        }

        public int AddLocalization(MixedSpacePoseFusionLocalization localization)
        {
            int status = MixedSpacePoseFusionNative.AddLocalization(m_Handle, ref localization);
            TraceFusionTimestamp("PoseFusionAddLocalizeInfo", localization.TimestampNanoseconds, status);
            return status;
        }

        public int GetTransform(long timestampNanoseconds, float[] trackingPose, float[] outputTransform)
        {
            int status = MixedSpacePoseFusionNative.GetTransform(
                m_Handle,
                timestampNanoseconds,
                trackingPose,
                outputTransform);
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (now >= m_NextTimestampLogTicks)
            {
                m_NextTimestampLogTicks = now + System.Diagnostics.Stopwatch.Frequency;
                TraceFusionTimestamp("PoseFusionGetTransform", timestampNanoseconds, status);
            }
            return status;
        }

        private static void TraceFusionTimestamp(string api, long timestampNanoseconds, int status)
        {
            try
            {
                string text = $"[TIME-CHECK] api={api} input_timestamp_ns={timestampNanoseconds} return_code={status}";
                MixedSpacePoseFusionLogFile.Initialize();
                MixedSpacePoseFusionLogFile.Append(2, text);
                MixedSpaceLogger.Log(text, MixedSpaceLogger.LoggingLevel.Verbose, "TimestampCheck");
            }
            catch { }
        }

        public int Reset()
        {
            if (m_Handle == IntPtr.Zero) return 0;
            int status = MixedSpacePoseFusionNative.Reset(m_Handle);
            return status == 0 ? ApplyStoredConfig() : status;
        }

        public int ConvertUnityToNativePose(float[] unityMatrix, float[] nativePose)
        {
            return MixedSpacePoseFusionNative.ConvertUnityToPose(unityMatrix, nativePose);
        }

        public int ConvertNativePoseToUnity(float[] nativePose, float[] unityMatrix)
        {
            return MixedSpacePoseFusionNative.ConvertPoseToUnity(nativePose, unityMatrix);
        }

        public void Dispose()
        {
            if (m_Handle == IntPtr.Zero)
                return;

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

        internal static void Register()
        {
            lock (Gate)
            {
                if (s_Attempted) return;
                s_Attempted = true;
                MixedSpacePoseFusionLogFile.Initialize();
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
        // The weak-import shim keeps older iOS static libraries linkable when SetConfig is absent.
        [DllImport(NativeLibrary, EntryPoint = "MixedSpacePoseFusionSetConfigIfAvailable", CallingConvention = CallingConvention.Cdecl)]
#else
        [DllImport(NativeLibrary, EntryPoint = "PoseFusionSetConfig", CallingConvention = CallingConvention.Cdecl)]
#endif
        internal static extern int SetConfig(IntPtr poseFusion, ref MixedSpacePoseFusionConfig config);

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
