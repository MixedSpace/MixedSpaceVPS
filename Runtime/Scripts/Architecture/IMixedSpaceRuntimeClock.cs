namespace MixedSpace
{
    /// <summary>
    /// 运行时服务的时间来源；隔离 Unity 时间，使测试调度可重复，也便于其他客户端替换。
    /// Time source used by runtime services. Keeping Unity time behind this port
    /// makes scheduling deterministic in tests and replaceable on other clients.
    /// </summary>
    public interface IMixedSpaceRuntimeClock
    {
        float Time { get; }
        float UnscaledTime { get; }
        float RealtimeSinceStartup { get; }
    }

    public sealed class UnityMixedSpaceRuntimeClock : IMixedSpaceRuntimeClock
    {
        public float Time => UnityEngine.Time.time;
        public float UnscaledTime => UnityEngine.Time.unscaledTime;
        public float RealtimeSinceStartup => UnityEngine.Time.realtimeSinceStartup;
    }
}
