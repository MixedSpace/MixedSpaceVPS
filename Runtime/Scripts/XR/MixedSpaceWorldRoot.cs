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
        private Matrix4x4 m_InitialPose = Matrix4x4.identity;
        private Pose? m_LastWrittenPose;

        /// <summary>
        /// 当前世界定位是否可信；区域检测在 false 时应暂停进出判定并清空防抖计时。
        /// Whether world localization is reliable. Area checks should pause and clear debounce timers while false.
        /// </summary>
        public bool IsPoseReliable { get; internal set; }

        public Matrix4x4 InitialPose => m_InitialPose;

        internal Matrix4x4 CurrentPose => Matrix4x4.TRS(
            transform.position,
            transform.rotation,
            Vector3.one);

        private void Awake()
        {
            m_InitialPose = CurrentPose;
        }

        internal bool TryApplyPose(Matrix4x4 pose, out bool transformWritten, bool writePoseLog = false,
            long timestampNanoseconds = 0, MixedSpacePlatformPoseSample? trackingSample = null,
            int? fusionReturnCode = null)
        {
            transformWritten = false;
            if (!TryPreparePose(pose, out Matrix4x4 prepared))
            {
                if (writePoseLog)
                    MixedSpaceLocalizationJournal.AppendWorldPoseLog(this, pose, false, false,
                        Vector3.zero, Quaternion.identity, timestampNanoseconds, trackingSample, fusionReturnCode);
                return false;
            }

            Vector3 position = prepared.GetPosition();
            Quaternion rotation = prepared.rotation;
            Transform target = transform;
            bool needsWrite = true;
            if (m_LastWrittenPose.HasValue && m_LastWrittenPose.Value.position.Equals(position) &&
                m_LastWrittenPose.Value.rotation.Equals(rotation))
            {
                // A parent or another component may have moved this World since the last write.
                target.GetPositionAndRotation(out Vector3 currentPosition, out Quaternion currentRotation);
                needsWrite = !currentPosition.Equals(position) || !currentRotation.Equals(rotation);
            }

            if (needsWrite)
            {
                target.SetPositionAndRotation(position, rotation);
                m_LastWrittenPose = new Pose(position, rotation);
                transformWritten = true;
            }
            if (writePoseLog)
                MixedSpaceLocalizationJournal.AppendWorldPoseLog(this, pose, true, transformWritten,
                    position, rotation, timestampNanoseconds, trackingSample, fusionReturnCode);
            return true;
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
