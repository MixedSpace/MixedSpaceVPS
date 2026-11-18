using System;

namespace MixedSpace.XR
{
    /// <summary>
    /// 用多个时间增量窗口识别纳秒、微秒或毫秒；不根据时间戳位数猜测，也不修改设备时钟原点。
    /// Detects units from several device/host delta windows. It never guesses from the absolute
    /// timestamp or rebases the device epoch. Access must be serialized by the owning adapter.
    /// </summary>
    internal sealed class MixedSpaceTimestampUnitDetector
    {
        private const double WindowSeconds = 0.25;
        private const int MinimumSamples = 6;
        private const int ConfirmationWindows = 3;
        private readonly long m_HostFrequency;
        private long m_LastRaw, m_LastHost, m_WindowRaw, m_WindowHost;
        private long m_CandidateScale;
        private int m_WindowSamples, m_ConfirmedWindows, m_InvalidWindows;

        public long NanosecondsPerUnit { get; private set; }
        public bool Ready => NanosecondsPerUnit != 0 && FailureReason == null;
        public string FailureReason { get; private set; }
        public double RawUnitsPerSecond { get; private set; }

        public MixedSpaceTimestampUnitDetector(long hostFrequency)
        {
            if (hostFrequency <= 0) throw new ArgumentOutOfRangeException(nameof(hostFrequency));
            m_HostFrequency = hostFrequency;
        }

        public void Reset()
        {
            m_LastRaw = m_LastHost = m_WindowRaw = m_WindowHost = 0;
            m_CandidateScale = NanosecondsPerUnit = 0;
            m_WindowSamples = m_ConfirmedWindows = m_InvalidWindows = 0;
            RawUnitsPerSecond = 0;
            FailureReason = null;
        }

        public bool Observe(long raw, long hostTicks)
        {
            if (FailureReason != null || raw <= 0 || hostTicks <= 0) return false;
            if (m_LastRaw != 0)
            {
                if (hostTicks <= m_LastHost) return false;
                double elapsed = (hostTicks - m_LastHost) / (double)m_HostFrequency;
                // 跳过重复及偶发乱序；持续倒退/停滞说明设备时钟已重置，必须重启融合会话。
                // Ignore duplicates and occasional reordering; a persistent reset cannot share a fusion session.
                if (raw <= m_LastRaw)
                {
                    if (NanosecondsPerUnit != 0 && elapsed > 2.0)
                        FailureReason = "Device timestamps stopped advancing or reset; reinitialize the SDK/fusion session.";
                    return false;
                }
                if (NanosecondsPerUnit != 0 &&
                    Math.Abs((raw - m_LastRaw) * (double)NanosecondsPerUnit / 1e9 - elapsed) > 1.0)
                {
                    FailureReason = "Device timestamp rate or epoch changed; reinitialize the SDK/fusion session.";
                    return false;
                }
            }
            m_LastRaw = raw;
            m_LastHost = hostTicks;
            if (m_WindowSamples == 0)
            {
                BeginWindow(raw, hostTicks);
                return true;
            }
            ++m_WindowSamples;
            double seconds = (hostTicks - m_WindowHost) / (double)m_HostFrequency;
            if (seconds < WindowSeconds || m_WindowSamples < MinimumSamples) return true;

            RawUnitsPerSecond = (raw - m_WindowRaw) / seconds;
            long candidate = DetectScale(RawUnitsPerSecond);
            if (NanosecondsPerUnit == 0)
            {
                m_ConfirmedWindows = candidate != 0 && candidate == m_CandidateScale ? m_ConfirmedWindows + 1 : 1;
                m_CandidateScale = candidate;
                if (candidate != 0 && m_ConfirmedWindows >= ConfirmationWindows)
                    NanosecondsPerUnit = candidate;
            }
            else
            {
                m_InvalidWindows = candidate == NanosecondsPerUnit ? 0 : m_InvalidWindows + 1;
                if (m_InvalidWindows >= ConfirmationWindows)
                    FailureReason = "Device timestamp rate no longer matches the detected unit; reinitialize the SDK/fusion session.";
            }
            BeginWindow(raw, hostTicks);
            if (NanosecondsPerUnit != 0 && raw > long.MaxValue / NanosecondsPerUnit)
                FailureReason = "Device timestamp overflows the nanosecond clock.";
            return FailureReason == null;
        }

        public bool TryToNanoseconds(long raw, out long nanoseconds)
        {
            nanoseconds = 0;
            if (!Ready || raw <= 0 || raw > long.MaxValue / NanosecondsPerUnit) return false;
            nanoseconds = NanosecondsPerUnit == 1 ? raw : raw * NanosecondsPerUnit;
            return true;
        }

        /// <summary>
        /// 同一设备帧可以被多个使用方读取；重复读取仍是有效跟踪，但不增加单位确认样本。
        /// Re-reading the current device frame remains tracked. Only advancing samples contribute
        /// to unit detection; stale, backward and failed-clock samples remain rejected.
        /// </summary>
        public bool TryObserveCurrentTimestamp(long raw, long hostTicks, out long nanoseconds)
        {
            bool advanced = Observe(raw, hostTicks);
            if (!advanced && raw != m_LastRaw)
            {
                nanoseconds = 0;
                return false;
            }
            return TryToNanoseconds(raw, out nanoseconds);
        }

        private void BeginWindow(long raw, long hostTicks)
        {
            m_WindowRaw = raw;
            m_WindowHost = hostTicks;
            m_WindowSamples = 1;
        }

        private static long DetectScale(double rawUnitsPerSecond)
        {
            // 三类单位相差 1000 倍；35% 容差用于回调调度抖动，不接受模糊的任意缩放。
            // Units are 1000x apart. Tolerance permits scheduling jitter, not arbitrary clock scaling.
            if (rawUnitsPerSecond >= 0.65e9 && rawUnitsPerSecond <= 1.35e9) return 1;
            if (rawUnitsPerSecond >= 0.65e6 && rawUnitsPerSecond <= 1.35e6) return 1000;
            if (rawUnitsPerSecond >= 650 && rawUnitsPerSecond <= 1350) return 1000000;
            return 0;
        }
    }
}
