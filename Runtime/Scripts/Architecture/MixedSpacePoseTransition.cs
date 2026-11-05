using System;
using UnityEngine;

namespace MixedSpace.XR
{
    /// <summary>
    /// 由 World 提供 deltaSeconds；SlowApproach 将帧步长限制为 1..6，突变阈值按四元数点积计算。
    /// World supplies deltaSeconds; SlowApproach clamps frame steps to 1..6 and warp detection uses a quaternion-dot threshold.
    /// </summary>
    internal static class MixedSpaceTransitionTiming
    {
        public static float Alpha(MixedSpaceWorldOutputSettings policy, float elapsedSeconds, float deltaSeconds)
        {
            ValidateDelta(deltaSeconds);
            float fraction = Math.Max(0f, Math.Min(1f, elapsedSeconds / policy.TransitionSeconds));
            switch (policy.Interpolation)
            {
                case MixedSpacePoseInterpolation.None: return 1f;
                case MixedSpacePoseInterpolation.Linear: return fraction;
                case MixedSpacePoseInterpolation.Sinusoidal:
                    return (float)Math.Sin(fraction * (float)Math.PI * 0.5f);
                case MixedSpacePoseInterpolation.Cubic:
                    return fraction < 0.5f ? 4f * fraction * fraction * fraction :
                        1f - (float)Math.Pow(2f - 2f * fraction, 3f) / 2f;
                case MixedSpacePoseInterpolation.SlowApproach:
                    float frames = Math.Max(1f, Math.Min(6f, deltaSeconds / (1f / 60f)));
                    return 1f - (float)Math.Pow(1f - policy.ApproachFactor, frames);
                default: throw new ArgumentOutOfRangeException(nameof(policy.Interpolation));
            }
        }

        public static bool MustWarp(MixedSpaceWorldOutputSettings policy, float distanceSquared, float quaternionDot) =>
            policy.AllowWarp && (distanceSquared > policy.WarpDistance * policy.WarpDistance ||
                quaternionDot < (float)Math.Cos(policy.WarpAngle * (float)Math.PI / 180f));

        internal static void ValidateDelta(float seconds)
        {
            if (float.IsNaN(seconds) || float.IsInfinity(seconds) || seconds < 0f)
                throw new ArgumentOutOfRangeException(nameof(seconds));
        }
    }

    /// <summary>
    /// 一个 World 的呈现轨迹；接收目标与推进时间独立，不保留定位结果。重置清空起点、目标、当前值、耗时及预备状态，也覆盖过渡中途重置。
    /// One world's presentation trajectory. Receiving a target and advancing time
    /// are distinct operations; neither retains a localization result. Reset clears
    /// start/target/current, elapsed time and priming, including mid-transition reset.
    /// </summary>
    internal sealed class MixedSpacePoseTransition
    {
        private readonly MixedSpaceWorldOutputSettings m_Policy;
        private Pose m_Start, m_Target;
        private float m_Elapsed;
        private bool m_Primed;
        public Pose Current { get; private set; }

        public MixedSpacePoseTransition(MixedSpaceWorldOutputSettings policy)
        {
            m_Policy = policy.Snapshot();
            Clear();
        }

        public void Retarget(Pose target)
        {
            m_Start = Current;
            m_Target = target;
            m_Elapsed = 0f;
        }

        public void Advance(float deltaSeconds)
        {
            MixedSpaceTransitionTiming.ValidateDelta(deltaSeconds);
            if (!m_Primed || m_Policy.Interpolation == MixedSpacePoseInterpolation.None ||
                MixedSpaceTransitionTiming.MustWarp(m_Policy,
                    (Current.position - m_Target.position).sqrMagnitude,
                    Quaternion.Dot(Current.rotation, m_Target.rotation)))
            {
                Current = m_Target;
                m_Primed = true;
                return;
            }

            m_Elapsed += deltaSeconds;
            float alpha = MixedSpaceTransitionTiming.Alpha(m_Policy, m_Elapsed, deltaSeconds);
            Pose origin = m_Policy.Interpolation == MixedSpacePoseInterpolation.SlowApproach ? Current : m_Start;
            Current = new Pose(
                Vector3.Lerp(origin.position, m_Target.position, alpha),
                Quaternion.Slerp(origin.rotation, m_Target.rotation, alpha));
        }

        public void Clear()
        {
            Current = m_Start = m_Target = new Pose(Vector3.zero, Quaternion.identity);
            m_Elapsed = 0f;
            m_Primed = false;
        }
    }
}
