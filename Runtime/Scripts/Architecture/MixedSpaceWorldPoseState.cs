using System;
using UnityEngine;

namespace MixedSpace.XR
{
    /// <summary>
    /// 固定 World 输出模型先从历史估计位姿，再交给呈现轨迹；两者职责不同，不是可替换的异步阶段。限于 Unity 线程，原生融合不使用此回退模型。
    /// Fixed world-output model: observation history produces an estimate, then
    /// a presentation trajectory consumes that pose. These are different roles,
    /// not interchangeable asynchronous stages. This object is Unity-thread confined.
    /// Native fusion does not use this fallback model.
    /// </summary>
    internal sealed class MixedSpaceWorldPoseState
    {
        private readonly MixedSpaceWorldOutputSettings m_Policy;
        private readonly MixedSpacePoseHistory m_History;
        private readonly MixedSpacePoseTransition m_Transition;

        public MixedSpaceWorldPoseState(MixedSpaceWorldOutputSettings settings)
        {
            m_Policy = (settings ?? throw new ArgumentNullException(nameof(settings))).Snapshot();
            if (m_Policy.Estimation != MixedSpacePoseEstimation.None)
                m_History = new MixedSpacePoseHistory(m_Policy);
            if (m_Policy.Interpolation != MixedSpacePoseInterpolation.None)
                m_Transition = new MixedSpacePoseTransition(m_Policy);
        }

        public Matrix4x4 Accept(MixedSpaceAlignmentSolution observation, float deltaSeconds)
        {
            if (observation == null) throw new ArgumentNullException(nameof(observation));
            MixedSpaceTransitionTiming.ValidateDelta(deltaSeconds);
            Matrix4x4 estimate = observation.WorldPose;
            if (m_History != null)
            {
                bool relative = m_Policy.Estimation == MixedSpacePoseEstimation.MapRelative;
                Matrix4x4 sample = relative ? MixedSpaceMapPoseCoordinates.RemovePlacement(observation) : estimate;
                Matrix4x4 filtered = m_History.Observe(observation.MapKey,
                    MixedSpacePoseBasis.FromMatrix(sample), observation.Confidence).ToMatrix();
                estimate = relative ? MixedSpaceMapPoseCoordinates.ApplyPlacement(filtered, observation) : filtered;
            }
            if (m_Transition == null) return estimate;
            m_Transition.Retarget(new Pose(estimate.GetPosition(), estimate.rotation));
            // 每次定位结果到达时推进过渡，逐帧 Tick 也会推进。
            // Advance the transition on each localization result as well as on frame ticks.
            m_Transition.Advance(deltaSeconds);
            return Matrix4x4.TRS(m_Transition.Current.position, m_Transition.Current.rotation, Vector3.one);
        }

        public void Advance(float deltaSeconds) => m_Transition?.Advance(deltaSeconds);

        public void Clear()
        {
            m_History?.Clear();
            m_Transition?.Clear();
        }
    }

    /// <summary>
    /// 历史估计的坐标域：地图相对样本不包含放置缩放与偏移，仅在估计后应用。自定义求解器可改 WorldPose；MapRelative 仍从观测重建，World 模式估计传入的
    /// WorldPose。
    /// The coordinate chart surrounding history estimation. Map-relative samples
    /// exclude placement scale and offset; these are applied only to the estimate.
    /// A custom solver may change WorldPose; as before, MapRelative mode rebuilds
    /// it from the observation, while World mode estimates the supplied WorldPose.
    /// </summary>
    internal static class MixedSpaceMapPoseCoordinates
    {
        public static Matrix4x4 RemovePlacement(MixedSpaceAlignmentSolution observation)
        {
            Vector3 position = observation.LocalizedPosition;
            Quaternion rotation = observation.LocalizedRotation * observation.CameraOrientation;
            position = MixedSpaceCoordinateFrame.ReflectXAxis(position);
            rotation = MixedSpaceCoordinateFrame.ReflectXAxis(rotation);
            return observation.TrackerPose * Matrix4x4.TRS(position, rotation, Vector3.one).inverse;
        }

        public static Matrix4x4 ApplyPlacement(Matrix4x4 estimate, MixedSpaceAlignmentSolution observation)
        {
            Matrix4x4 cameraInMap = estimate.inverse * observation.TrackerPose;
            MixedSpaceMapPlacement placement = observation.Placement;
            Matrix4x4 placedCamera = Matrix4x4.TRS(placement.Translation, placement.Orientation, Vector3.one) *
                Matrix4x4.TRS(Vector3.Scale(cameraInMap.GetColumn(3), placement.Scale), cameraInMap.rotation, Vector3.one);
            return observation.TrackerPose * placedCamera.inverse;
        }
    }
}
