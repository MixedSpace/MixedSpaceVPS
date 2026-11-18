using System;
using System.Runtime.InteropServices;

namespace MixedSpace
{
    /// <summary>
    /// PoseFusion 1.2.0 的原生配置布局。字段顺序与原生结构一致，6 个 float 和 1 字节 bool 按 4 字节对齐，共 28 字节。
    /// Native PoseFusion 1.2.0 layout: six floats and a one-byte C++ bool, padded to 28 bytes with four-byte alignment.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct MixedSpacePoseFusionConfig
    {
        /// <summary>
        /// 绕 X 轴的角度阈值（度）。
        /// X-axis angle threshold in degrees.
        /// </summary>
        public float x_axis_angle_threshold;
        /// <summary>
        /// 绕 Z 轴的角度阈值（度）。
        /// Z-axis angle threshold in degrees.
        /// </summary>
        public float z_axis_angle_threshold;
        /// <summary>
        /// 位姿纠偏的距离阈值（米）。
        /// Distance threshold for pose refinement in meters.
        /// </summary>
        public float refine_threshold_dist;
        /// <summary>
        /// 判定位姿无效的平移差阈值（米）。
        /// Translation difference threshold for rejecting a pose, in meters.
        /// </summary>
        public float diff_trans_threshold;
        /// <summary>
        /// 位姿纠偏的旋转阈值（度）。
        /// Rotation threshold for pose refinement in degrees.
        /// </summary>
        public float refine_threshold_angle;
        /// <summary>
        /// 判定位姿无效的旋转差阈值（度）。
        /// Rotation difference threshold for rejecting a pose, in degrees.
        /// </summary>
        public float diff_angle_threshold;
        /// <summary>
        /// 启用 3DoF 时仅使用 SLAM 旋转，忽略 SLAM 平移。
        /// In 3DoF mode, fusion uses SLAM rotation and ignores SLAM translation.
        /// </summary>
        [MarshalAs(UnmanagedType.I1)] public bool enable_three_dof;

        /// <summary>
        /// PoseFusion 1.2.0 的显式默认值。default(struct) 会将所有数值字段置零。
        /// Explicit PoseFusion 1.2.0 defaults. The zero-initialized struct sets all numeric fields to zero.
        /// </summary>
        public static MixedSpacePoseFusionConfig Default => new MixedSpacePoseFusionConfig
        {
            x_axis_angle_threshold = 0f,
            z_axis_angle_threshold = 0f,
            refine_threshold_dist = 0.2f,
            diff_trans_threshold = 0.5f,
            refine_threshold_angle = 1f,
            diff_angle_threshold = 5f,
            enable_three_dof = false
        };

        internal void Validate()
        {
            ValidateThreshold(x_axis_angle_threshold, nameof(x_axis_angle_threshold));
            ValidateThreshold(z_axis_angle_threshold, nameof(z_axis_angle_threshold));
            ValidateThreshold(refine_threshold_dist, nameof(refine_threshold_dist));
            ValidateThreshold(diff_trans_threshold, nameof(diff_trans_threshold));
            ValidateThreshold(refine_threshold_angle, nameof(refine_threshold_angle));
            ValidateThreshold(diff_angle_threshold, nameof(diff_angle_threshold));
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
        /// <summary>
        /// 为已初始化的会话应用当前原生库支持的配置项。
        /// Applies the settings supported by the native library to an initialized session.
        /// </summary>
        /// <returns>0：成功 / success；-2：未初始化 / not initialized；-1001：缺少配置入口 / configuration entry point unavailable。</returns>
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
