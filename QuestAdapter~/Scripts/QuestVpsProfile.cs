using System;
using UnityEngine;

namespace QuestVPS
{
    [CreateAssetMenu(menuName = "MixedSpace/Platforms/Quest Profile")]
    public sealed class QuestVpsProfile : ScriptableObject
    {
        [SerializeField, Min(1f)] private float m_StartupTimeoutSeconds = 15f;
        [SerializeField, Range(0.1f, 2f)] private float m_MaximumFrameAgeSeconds = 0.5f;

        public float StartupTimeoutSeconds => m_StartupTimeoutSeconds;
        public float MaximumFrameAgeSeconds => m_MaximumFrameAgeSeconds;
        public QuestVpsSettings CreateSettings() => new QuestVpsSettings(m_StartupTimeoutSeconds, m_MaximumFrameAgeSeconds);
    }

    /// <summary>Immutable per-start snapshot; runtime code never writes into the shared asset.</summary>
    public sealed class QuestVpsSettings
    {
        public float StartupTimeoutSeconds { get; }
        public float MaximumFrameAgeSeconds { get; }

        public QuestVpsSettings(float startupTimeoutSeconds, float maximumFrameAgeSeconds)
        {
            if (float.IsNaN(startupTimeoutSeconds) || float.IsInfinity(startupTimeoutSeconds) || startupTimeoutSeconds < 1f)
                throw new ArgumentOutOfRangeException(nameof(startupTimeoutSeconds), "Quest camera startup timeout must be finite and at least one second.");
            if (float.IsNaN(maximumFrameAgeSeconds) || float.IsInfinity(maximumFrameAgeSeconds) || maximumFrameAgeSeconds < 0.1f || maximumFrameAgeSeconds > 2f)
                throw new ArgumentOutOfRangeException(nameof(maximumFrameAgeSeconds), "Quest maximum frame age must be between 0.1 and 2 seconds.");
            StartupTimeoutSeconds = startupTimeoutSeconds;
            MaximumFrameAgeSeconds = maximumFrameAgeSeconds;
        }
    }
}
