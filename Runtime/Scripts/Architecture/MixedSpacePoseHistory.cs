using System;
using System.Collections.Generic;
using UnityEngine;

namespace MixedSpace.XR
{
    /// <summary>
    /// 独立的观测值，不持有地图、定位结果对象或场景引用。
    /// A detached observation: no map, localization result or scene reference.
    /// </summary>
    internal readonly struct MixedSpacePoseBasis
    {
        public readonly Vector3 Position, Right, Forward;
        public MixedSpacePoseBasis(Vector3 position, Vector3 right, Vector3 forward)
        { Position = position; Right = right; Forward = forward; }

        public static MixedSpacePoseBasis FromMatrix(Matrix4x4 pose) =>
            new MixedSpacePoseBasis(pose.GetColumn(3), pose.GetColumn(0), pose.GetColumn(2));

        public Matrix4x4 ToMatrix()
        {
            Vector3 forward = Forward.normalized;
            Vector3 up = Vector3.Cross(forward, Right.normalized).normalized;
            return Matrix4x4.TRS(Position, Quaternion.LookRotation(forward, up), Vector3.one);
        }

        internal Vector3 Axis(int axis) => axis == 0 ? Position : axis == 1 ? Right : Forward;
    }

    /// <summary>
    /// 固定容量的样本存储和统计查询；插入不转换坐标或推进呈现时间。保留物理槽位顺序以稳定浮点累加顺序，计数有界，长时间运行不溢出。
    /// Fixed-capacity sample storage and statistical query. Insertion never
    /// performs coordinate transforms or advances a presentation clock.
    /// Physical slot order is retained to preserve floating-point summation order
    /// across ring wrap; count is bounded and cannot overflow after long sessions.
    /// </summary>
    internal sealed class MixedSpacePoseWindow
    {
        private readonly MixedSpacePoseBasis[] m_Samples;
        private readonly float[] m_Weights;
        private int m_Next;
        public int Count { get; private set; }

        public MixedSpacePoseWindow(int capacity)
        {
            if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
            m_Samples = new MixedSpacePoseBasis[capacity];
            m_Weights = new float[capacity];
        }

        public void Append(MixedSpacePoseBasis sample, float weight)
        {
            if (float.IsNaN(weight) || float.IsInfinity(weight) || weight < 0f)
                throw new ArgumentOutOfRangeException(nameof(weight));
            m_Samples[m_Next] = sample;
            m_Weights[m_Next] = weight;
            m_Next = (m_Next + 1) % m_Samples.Length;
            Count = Math.Min(Count + 1, m_Samples.Length);
        }

        public MixedSpacePoseBasis Estimate()
        {
            if (Count == 0) throw new InvalidOperationException("No pose samples are available.");
            return new MixedSpacePoseBasis(EstimateAxis(0), EstimateAxis(1), EstimateAxis(2));
        }

        private Vector3 EstimateAxis(int axis)
        {
            var centroid = new WeightedCentroid();
            for (int slot = 0; slot < Count; slot++)
                centroid.Include(m_Samples[slot].Axis(axis), m_Weights[slot]);

            // 总权重为零时使用最新有限观测，避免除零。
            // Use the latest finite observation when total weight is zero to avoid division by zero.
            if (centroid.Weight <= 0f)
                return m_Samples[(m_Next + m_Samples.Length - 1) % m_Samples.Length].Axis(axis);
            Vector3 center = centroid.Value;
            if (Count < 3) return center;

            float scatter = 0f;
            for (int slot = 0; slot < Count; slot++)
                scatter += ResidualSquared(slot, axis, center);
            float limit = scatter / centroid.Weight;
            var accepted = new WeightedCentroid();
            for (int slot = 0; slot < Count; slot++)
                if (ResidualSquared(slot, axis, center) <= limit)
                    accepted.Include(m_Samples[slot].Axis(axis), m_Weights[slot]);
            return accepted.Weight > 0f ? accepted.Value : center;
        }

        // 兼容规则把权重放入残差内，并非标准加权方差；修改需单独重新调参。
        // Compatibility rule: the weight is inside the residual. This is not conventional weighted variance; changing it
        // requires separate tuning.
        private float ResidualSquared(int slot, int axis, Vector3 center) =>
            (m_Samples[slot].Axis(axis) * m_Weights[slot] - center).sqrMagnitude;

        private struct WeightedCentroid
        {
            private Vector3 m_Total;
            public float Weight { get; private set; }
            public Vector3 Value => m_Total / Weight;
            public void Include(Vector3 value, float weight)
            { m_Total += value * weight; Weight += weight; }
        }
    }

    /// <summary>
    /// World 持有并按地图身份分区的历史；世界坐标估计使用共享分区。清理时释放全部分区，组件不持有或复用缓冲区。
    /// World-owned histories partitioned by map identity. World-space estimation
    /// uses one shared partition.
    /// Clearing releases every partition; components never own or reuse these buffers.
    /// </summary>
    internal sealed class MixedSpacePoseHistory
    {
        private readonly MixedSpaceWorldOutputSettings m_Policy;
        private readonly Dictionary<int, MixedSpacePoseWindow> m_Windows = new Dictionary<int, MixedSpacePoseWindow>();
        public int PartitionCount => m_Windows.Count;

        public MixedSpacePoseHistory(MixedSpaceWorldOutputSettings policy) => m_Policy = policy.Snapshot();

        public MixedSpacePoseBasis Observe(int mapKey, MixedSpacePoseBasis sample, int confidence)
        {
            int partition = m_Policy.Estimation == MixedSpacePoseEstimation.World ? 0 : mapKey;
            if (!m_Windows.TryGetValue(partition, out MixedSpacePoseWindow window))
                m_Windows.Add(partition, window = new MixedSpacePoseWindow(m_Policy.HistoryCapacity));
            float weight = 1f;
            if (m_Policy.UseConfidence)
            {
                float normalized = Math.Max(0f, Math.Min(1f, confidence / m_Policy.ConfidenceCeiling));
                weight = 1f - m_Policy.ConfidenceInfluence + normalized * m_Policy.ConfidenceInfluence * 2f;
            }
            window.Append(sample, weight);
            return window.Estimate();
        }

        public void Clear() => m_Windows.Clear();
    }
}
