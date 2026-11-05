using System;
using System.Threading.Tasks;
using UnityEngine;

namespace MixedSpace.XR
{
    /// <summary>
    /// 一个逻辑 World 的运行时输出通道；定位发布不可变对齐值，由通道决定如何更新 Unity。
    /// Runtime output channel for one logical world. Localization publishes
    /// immutable alignment values here; the channel decides how Unity is updated.
    /// </summary>
    public interface IMixedSpaceWorldChannel : IDisposable
    {
        MixedSpaceWorldRoot View { get; }
        Matrix4x4 CurrentWorldPose { get; }

        Task PublishAsync(MixedSpaceAlignmentSolution solution);
        Matrix4x4 ProjectToLocal(Vector3 position, Quaternion rotation);
        Task ClearAsync();
        void Tick();
    }
}

