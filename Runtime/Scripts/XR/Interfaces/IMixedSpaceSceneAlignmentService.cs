namespace MixedSpace.XR
{
    /// <summary>
    /// 纯位姿求解器，将定位观测转换成世界对齐值，不调用场景对象，也不持有 Unity 生命周期。
    /// Pure pose solver. It converts a localization observation into a world
    /// alignment value and never calls a scene object or owns Unity lifecycle.
    /// </summary>
    public interface IMixedSpaceAlignmentSolver
    {
        MixedSpaceAlignmentSolution Solve(MixedSpaceAlignmentRequest request);
    }
}

