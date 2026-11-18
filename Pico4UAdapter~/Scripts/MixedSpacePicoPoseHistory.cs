using System;
using MixedSpace.XR;
using Unity.XR.PXR;
using UnityEngine;

namespace MixedSpace.Platforms.Pico
{
    /// <summary>
    /// 同一跟踪坐标系中的有界位姿历史。调用方在失去跟踪或原点改变时必须 Clear。
    /// Bounded pose history within one tracking epoch. The caller must Clear on tracking loss or origin changes.
    /// </summary>
    internal sealed class MixedSpacePicoPoseHistory
    {
        private readonly MixedSpacePlatformPoseSample[] m_Samples;
        private int m_First;
        

        public MixedSpacePicoPoseHistory(int capacity = 256)
        {
            if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
            m_Samples = new MixedSpacePlatformPoseSample[capacity];
        }

        public int Count { get; private set; }
        public long LatestTimestampNanoseconds => Count == 0 ? 0 : At(Count - 1).TimestampNanoseconds;

        public void Clear()
        {
            m_First = 0;
            Count = 0;
        }

        public bool TryAdd(MixedSpacePlatformPoseSample sample)
        {
            if (!IsValid(sample) || sample.TimestampNanoseconds <= LatestTimestampNanoseconds)
                return false;

            if (Count == m_Samples.Length)
            {
                m_Samples[m_First] = sample;
                m_First = (m_First + 1) % m_Samples.Length;
            }
            else
            {
                m_Samples[(m_First + Count) % m_Samples.Length] = sample;
                ++Count;
            }
            return true;
        }

        public bool TrySample(long timestampNs, long maxGapNs, out MixedSpacePlatformPoseSample sample,
            out long beforeNs, out long afterNs)
        {
            sample = default;
            beforeNs = afterNs = 0;
            if (timestampNs <= 0 || maxGapNs < 0 || Count == 0)
                return false;

            int lower = 0;
            int upper = Count - 1;
            while (lower <= upper)
            {
                int middle = lower + (upper - lower) / 2;
                MixedSpacePlatformPoseSample candidate = At(middle);
                if (candidate.TimestampNanoseconds == timestampNs)
                {
                    sample = candidate;
                    beforeNs = afterNs = timestampNs;
                    return true;
                }
                if (candidate.TimestampNanoseconds < timestampNs)
                    lower = middle + 1;
                else
                    upper = middle - 1;
            }

            // No extrapolation: lower is the first sample after the requested time.
            if (lower == 0 || lower == Count)
                return false;
            MixedSpacePlatformPoseSample before = At(lower - 1);
            MixedSpacePlatformPoseSample after = At(lower);
            long gapNs = after.TimestampNanoseconds - before.TimestampNanoseconds;
            if (gapNs > maxGapNs)
                return false;

            float amount = (float)((double)(timestampNs - before.TimestampNanoseconds) / gapNs);
            sample = new MixedSpacePlatformPoseSample
            {
                IsTracking = true,
                TimestampNanoseconds = timestampNs,
                Position = Vector3.Lerp(before.Position, after.Position, amount),
                Rotation = Quaternion.Slerp(before.Rotation, after.Rotation, amount)
            };
            beforeNs = before.TimestampNanoseconds;
            afterNs = after.TimestampNanoseconds;
            return true;
        }

        public static bool TryDisplayTimeToNanoseconds(double milliseconds, out long nanoseconds)
        {
            nanoseconds = 0;
            double value = milliseconds * 1000000.0;
            if (double.IsNaN(value) || double.IsInfinity(value) || value < 1 || value >= long.MaxValue)
                return false;
            nanoseconds = (long)Math.Round(value, MidpointRounding.AwayFromZero);
            return nanoseconds > 0;
        }

        private MixedSpacePlatformPoseSample At(int index) => m_Samples[(m_First + index) % m_Samples.Length];

        private static bool IsValid(MixedSpacePlatformPoseSample sample)
        {
            Quaternion rotation = sample.Rotation;
            float rotationLength = Quaternion.Dot(rotation, rotation);
            return sample.IsTracking && sample.TimestampNanoseconds > 0 &&
                IsFinite(sample.Position.x) && IsFinite(sample.Position.y) && IsFinite(sample.Position.z) &&
                IsFinite(rotation.x) && IsFinite(rotation.y) && IsFinite(rotation.z) && IsFinite(rotation.w) &&
                IsFinite(rotationLength) && rotationLength > 0.000000000001f;
        }

        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
