using System;
using System.Runtime.InteropServices;

namespace MixedSpace
{
    /// <summary>Native PoseFusionConfig layout: four floats and a one-byte C++ bool, padded to 20 bytes.</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct MixedSpacePoseFusionConfig
    {
        public float x_axis_angle_threshold;
        public float z_axis_angle_threshold;
        public float refine_threshold_dist;
        public float diff_trans_threshold;
        [MarshalAs(UnmanagedType.I1)] public bool enable_three_dof;

        /// <summary>Use this factory rather than default(struct), which initializes every numeric field to zero.</summary>
        public static MixedSpacePoseFusionConfig Default => new MixedSpacePoseFusionConfig
        {
            x_axis_angle_threshold = 0f,
            z_axis_angle_threshold = 0f,
            refine_threshold_dist = 0.4f,
            diff_trans_threshold = 0.5f,
            enable_three_dof = false
        };

        internal void Validate()
        {
            ValidateThreshold(x_axis_angle_threshold, nameof(x_axis_angle_threshold));
            ValidateThreshold(z_axis_angle_threshold, nameof(z_axis_angle_threshold));
            ValidateThreshold(refine_threshold_dist, nameof(refine_threshold_dist));
            ValidateThreshold(diff_trans_threshold, nameof(diff_trans_threshold));
        }

        private static void ValidateThreshold(float value, string name)
        {
            if (float.IsNaN(value) || float.IsInfinity(value) || value < 0f)
                throw new ArgumentOutOfRangeException(name, "Pose fusion thresholds must be finite and nonnegative.");
        }
    }

    /// <summary>
    /// 融合会话消费的原生布局定位样本，数组长度由跨平台 C ABI 固定。
    /// Native-layout localization sample consumed by a pose-fusion session.
    /// Array sizes are fixed by the cross-platform C ABI.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MixedSpacePoseFusionLocalization
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)]
        public float[] Position;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 9)]
        public float[] Rotation;

        public int Confidence;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public float[] TrackingPose;

        public long TimestampNanoseconds;
    }

    /// <summary>
    /// 独立的原生位姿融合会话。状态操作以 0 表示成功，非 0 表示失败。矩阵接口返回前完成所有缓冲区读写，不保留缓冲区，调用方可在后续调用中复用。
    /// One independent native pose-fusion session.
    /// Status-returning operations use zero for success and nonzero for failure.
    /// Matrix calls complete all buffer reads/writes before returning; callers may
    /// reuse their buffers on subsequent calls. Implementations must not retain them.
    /// </summary>
    public interface IMixedSpacePoseFusion : IDisposable
    {
        int Initialize();
        /// <summary>Apply configuration to an initialized session; 0=success, -2=not initialized, -1001=unsupported native library.</summary>
        int SetConfig(MixedSpacePoseFusionConfig config);
        int AddLocalization(MixedSpacePoseFusionLocalization localization);
        int GetTransform(long timestampNanoseconds, float[] trackingPose, float[] outputTransform);
        int Reset();
        int ConvertUnityToNativePose(float[] unityMatrix, float[] nativePose);
        int ConvertNativePoseToUnity(float[] nativePose, float[] unityMatrix);
    }

    /// <summary>
    /// 创建位姿融合会话的平台扩展点。
    /// Platform extension point for creating pose-fusion sessions.
    /// </summary>
    public interface IMixedSpacePoseFusionFactory
    {
        IMixedSpacePoseFusion Create();
    }
}
