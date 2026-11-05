using System;

namespace MixedSpace.XR
{
    internal enum MixedSpaceTrackingChange
    {
        None,
        Stabilized,
        Degraded
    }

    /// <summary>
    /// 定位置信度的时间状态机，不依赖提供器结果或 UnityEvent；成功观测最高升至 3，无新观测的保留间隔每推进一次下降一级。
    /// Temporal state machine for localization confidence. It knows neither
    /// provider results nor Unity events. Successful observations raise the score
    /// to at most three; silent retention intervals lower it one step per advance.
    /// </summary>
    internal sealed class MixedSpaceTrackingStability
    {
        private const int StableScore = 3;
        private readonly float m_RetentionSeconds;
        private float m_LastObservationTime;
        private bool m_HasPose;
        private bool m_IsStable;

        public MixedSpaceTrackingStability(float retentionSeconds)
        {
            if (float.IsNaN(retentionSeconds) ||
                float.IsInfinity(retentionSeconds) ||
                retentionSeconds < 0f)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(retentionSeconds),
                    "Tracking retention must be finite and non-negative.");
            }
            m_RetentionSeconds = retentionSeconds;
        }

        public int Score { get; private set; }

        public MixedSpaceTrackingChange Advance(
            bool hasAnyAttempt,
            int successfulObservations,
            float now)
        {
            if (successfulObservations < 0)
                throw new ArgumentOutOfRangeException(nameof(successfulObservations));
            if (float.IsNaN(now) || float.IsInfinity(now))
                throw new ArgumentOutOfRangeException(nameof(now));
            if (!hasAnyAttempt)
                return MixedSpaceTrackingChange.None;

            if (successfulObservations > 0)
            {
                m_LastObservationTime = now;
                Score = successfulObservations >= StableScore - Score
                    ? StableScore
                    : Score + successfulObservations;
                m_HasPose = true;
            }
            else if (now - m_LastObservationTime > m_RetentionSeconds)
            {
                m_LastObservationTime = now;
                Score = Math.Max(Score - 1, 0);
            }

            if (m_HasPose && Score < 1)
            {
                m_HasPose = false;
                if (m_IsStable)
                {
                    m_IsStable = false;
                    return MixedSpaceTrackingChange.Degraded;
                }
            }
            else if (m_HasPose &&
                     successfulObservations > 0 &&
                     Score >= StableScore &&
                     !m_IsStable)
            {
                m_IsStable = true;
                return MixedSpaceTrackingChange.Stabilized;
            }

            return MixedSpaceTrackingChange.None;
        }

        public void Clear()
        {
            Score = 0;
            m_LastObservationTime = 0f;
            m_HasPose = false;
            m_IsStable = false;
        }
    }

    /// <summary>
    /// 仅检测平台质量从正数变成零的边沿；启动即为零不算丢失，须先可用。
    /// Detects only the positive-to-zero platform-quality edge. Starting at zero
    /// is not a loss; quality must first become available.
    /// </summary>
    internal sealed class MixedSpacePlatformAvailability
    {
        private bool m_WasAvailable;

        public bool Observe(int quality)
        {
            if (quality > 0)
            {
                m_WasAvailable = true;
                return false;
            }

            // 仅零值表示丢失；未定义的负质量值不触发状态转换。
            // Only zero signals loss; undefined negative quality values do not trigger a transition.
            if (quality != 0)
                return false;
            if (!m_WasAvailable)
                return false;
            m_WasAvailable = false;
            return true;
        }

        public void Clear() => m_WasAvailable = false;
    }
}
