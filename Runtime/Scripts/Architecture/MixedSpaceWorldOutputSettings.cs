using System;
using UnityEngine;

namespace MixedSpace.XR
{
    public enum MixedSpacePoseEstimation { None, MapRelative, World }
    public enum MixedSpacePoseInterpolation { None, SlowApproach, Linear, Sinusoidal, Cubic }

    /// <summary>
    /// 非原生 World 输出的编辑策略，打开通道时复制，因而共享资产不共享历史。参数修改需重开 World，ProcessPoses 可实时切换；原生融合仍使用原始地图/跟踪观测。
    /// Authoring policy for the non-native world-pose output. Values are copied
    /// when a world channel opens, so sharing authoring data never shares history.
    /// Reopen the world to apply parameter edits; ProcessPoses can toggle live.
    /// Native fusion continues to consume the original map/tracker observations.
    /// </summary>
    [Serializable]
    public sealed class MixedSpaceWorldOutputSettings
    {
        [Header("Observation history")]
        [Tooltip("MapRelative 在地图放置前分别维护历史，World 则估计已放置的世界位姿。\nMapRelative keeps independent histories before map placement. World estimates placed world poses.")]
        public MixedSpacePoseEstimation Estimation;
        [Min(1)] public int HistoryCapacity = 8;
        public bool UseConfidence;
        [Min(0.001f)] public float ConfidenceCeiling = 50f;
        [Range(0f, 1f)] public float ConfidenceInfluence = 0.5f;

        [Header("Presentation transition")]
        public MixedSpacePoseInterpolation Interpolation;
        [Range(0f, 1f)] public float ApproachFactor = 0.025f;
        [Min(0.001f)] public float TransitionSeconds = 0.5f;
        public bool AllowWarp = true;
        [Min(0f)] public float WarpDistance = 5f;
        [Range(0f, 180f)] public float WarpAngle = 20f;

        internal MixedSpaceWorldOutputSettings Snapshot()
        {
            var copy = (MixedSpaceWorldOutputSettings)MemberwiseClone();
            if (!Enum.IsDefined(typeof(MixedSpacePoseEstimation), Estimation) ||
                !Enum.IsDefined(typeof(MixedSpacePoseInterpolation), Interpolation))
                throw new ArgumentException("Unknown world-output mode.");
            if (Estimation != MixedSpacePoseEstimation.None)
            {
                if (HistoryCapacity < 1)
                    throw new ArgumentOutOfRangeException(nameof(HistoryCapacity));
                if (UseConfidence)
                {
                    RequireRange(ConfidenceCeiling, float.Epsilon, float.MaxValue, nameof(ConfidenceCeiling));
                    RequireRange(ConfidenceInfluence, 0f, 1f, nameof(ConfidenceInfluence));
                }
            }
            if (Interpolation != MixedSpacePoseInterpolation.None)
            {
                RequireRange(ApproachFactor, 0f, 1f, nameof(ApproachFactor));
                RequireRange(TransitionSeconds, float.Epsilon, float.MaxValue, nameof(TransitionSeconds));
                RequireRange(WarpDistance, 0f, float.MaxValue, nameof(WarpDistance));
                RequireRange(WarpAngle, 0f, 180f, nameof(WarpAngle));
            }
            return copy;
        }

        private static void RequireRange(float value, float minimum, float maximum, string name)
        {
            if (float.IsNaN(value) || value < minimum || value > maximum)
                throw new ArgumentOutOfRangeException(name, "World-output values must be finite and within range.");
        }
    }
}
