using UnityEngine;

namespace MixedSpace.XR
{
    /// <summary>
    /// World 的被动 Unity 视图，算法不放在此组件；融合与生命周期由 MixedSpaceWorldChannel 管理。
    /// Passive Unity view for a MixedSpace world. Runtime algorithms never live
    /// on this component; MixedSpaceWorldChannel owns fusion and lifecycle.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("MixedSpace/World Root")]
    public sealed class MixedSpaceWorldRoot : MonoBehaviour
    {
        [SerializeField]
        private bool m_ProcessAlignment;

        [SerializeField]
        private MixedSpaceWorldOutputSettings m_OutputSettings = new MixedSpaceWorldOutputSettings();

        private Matrix4x4 m_InitialPose = Matrix4x4.identity;

        public bool ProcessPoses
        {
            get => m_ProcessAlignment;
            set => m_ProcessAlignment = value;
        }

        /// <summary>
        /// 当前世界定位是否可信；区域检测在 false 时应暂停进出判定并清空防抖计时。
        /// Whether world localization is reliable. Area checks should pause and clear debounce timers while false.
        /// </summary>
        public bool IsPoseReliable { get; internal set; }

        public bool ProcessAlignment => m_ProcessAlignment;
        public Matrix4x4 InitialPose => m_InitialPose;

        public MixedSpaceWorldOutputSettings OutputSettings => m_OutputSettings;
        internal MixedSpaceWorldOutputSettings CaptureOutputSettings() =>
            (m_OutputSettings ?? new MixedSpaceWorldOutputSettings()).Snapshot();

        internal Matrix4x4 CurrentPose => Matrix4x4.TRS(
            transform.position,
            transform.rotation,
            Vector3.one);

        private void Awake()
        {
            m_InitialPose = CurrentPose;
        }

        internal void ApplyPose(Matrix4x4 pose, bool writePoseLog = false, long timestampNanoseconds = 0,
            MixedSpacePlatformPoseSample? trackingSample = null, int? fusionReturnCode = null)
        {
            if (!TryPreparePose(pose, out Matrix4x4 prepared))
            {
                if (writePoseLog)
                    MixedSpaceLocalizationJournal.AppendWorldPoseLog(this, pose, false,
                        Vector3.zero, Quaternion.identity, timestampNanoseconds, trackingSample, fusionReturnCode);
                return;
            }

            Vector3 position = prepared.GetPosition();
            Quaternion rotation = prepared.rotation;
            transform.SetPositionAndRotation(position, rotation);
            if (writePoseLog)
                MixedSpaceLocalizationJournal.AppendWorldPoseLog(this, pose, true,
                    position, rotation, timestampNanoseconds, trackingSample, fusionReturnCode);
        }

        /// <summary>
        /// 仅消除仿射矩阵齐次行的浮点舍入误差。旋转、平移和尺度保持原值，
        /// 非有限数、明显的透视项或无效 TRS 仍然拒绝，不以单位矩阵掩盖异常输出。
        /// Removes only rounding error in the affine homogeneous row, preserving rotation, translation and
        /// scale. Nonfinite values, significant perspective and invalid TRS remain rejected; identity never hides invalid
        /// output.
        /// </summary>
        internal static bool TryPreparePose(Matrix4x4 source, out Matrix4x4 prepared)
        {
            prepared = source;
            const float affineTolerance = 0.000001f;
            for (int i = 0; i < 16; ++i)
                if (float.IsNaN(source[i]) || float.IsInfinity(source[i])) return false;

            if (Mathf.Abs(source.m30) > affineTolerance ||
                Mathf.Abs(source.m31) > affineTolerance ||
                Mathf.Abs(source.m32) > affineTolerance ||
                Mathf.Abs(source.m33 - 1f) > affineTolerance)
                return false;

            prepared.m30 = 0f;
            prepared.m31 = 0f;
            prepared.m32 = 0f;
            prepared.m33 = 1f;
            return prepared.ValidTRS();
        }
    }
}
