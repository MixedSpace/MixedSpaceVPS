using System;

namespace MixedSpace
{
    /// <summary>
    /// 连续定位的平台无关调度策略，时间由调用方传入，无 Unity 生命周期依赖，可在各平台复用和测试。
    /// Platform-neutral scheduling policy for continuous localization.
    /// Time is supplied by the caller so this class has no Unity lifecycle dependency
    /// and can be reused and unit tested on every supported platform.
    /// </summary>
    public sealed class MixedSpaceLocalizationScheduler
    {
        public bool BurstActive { get; private set; }

        public float LastRunTime { get; private set; }

        public float BurstStartTime { get; private set; }

        public void SetBurst(bool enabled, float currentTime)
        {
            if (enabled)
                BurstStartTime = currentTime;

            BurstActive = enabled;
        }

        public bool ShouldStopBurst(
            int localizationSuccessCount,
            int successLimit,
            float currentTime,
            float timeLimit)
        {
            if (!BurstActive)
                return false;

            bool reachedSuccessLimit = localizationSuccessCount >= Math.Max(0, successLimit);
            bool reachedTimeLimit = currentTime - BurstStartTime >= Math.Max(0f, timeLimit);
            return reachedSuccessLimit || reachedTimeLimit;
        }

        public bool ShouldRun(float currentTime, float interval, bool taskRunning, bool paused)
        {
            if (taskRunning || paused)
                return false;

            float safeInterval = Math.Max(0f, interval);
            return BurstActive || currentTime - LastRunTime >= safeInterval;
        }

        public void MarkRunStarted(float currentTime)
        {
            LastRunTime = currentTime;
        }

        public void Reset()
        {
            BurstActive = false;
            BurstStartTime = 0f;
            LastRunTime = 0f;
        }
    }
}
