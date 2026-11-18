using System;

namespace MixedSpace
{
    /// <summary>
    /// 描述 MixedSpace 运行时生命周期，与 Unity 和具体定位实现分离，使平台适配器共享同一契约。
    /// Describes the lifecycle state of the MixedSpace runtime.
    /// 
    /// The state is intentionally independent from Unity and the underlying
    /// localization implementation so platform adapters can share the same
    /// lifecycle contract.
    /// </summary>
    public enum MixedSpaceRuntimeState
    {
        Stopped = 0,
        Validating = 1,
        Initializing = 2,
        LoadingMaps = 3,
        ConfiguringPlatform = 4,
        ConfiguringLocalization = 5,
        Ready = 6,
        Resetting = 7,
        Stopping = 8,
        Failed = 9
    }

    /// <summary>
    /// 提供运行时失败的结构化信息。
    /// Provides structured information about a runtime failure.
    /// </summary>
    public sealed class MixedSpaceRuntimeError
    {
        public string Component { get; }
        public string Message { get; }
        public Exception Exception { get; }

        public MixedSpaceRuntimeError(string component, Exception exception)
        {
            Component = string.IsNullOrWhiteSpace(component) ? "MixedSpace" : component;
            Exception = exception;
            Message = exception?.Message ?? "Unknown MixedSpace runtime error.";
        }
    }

    /// <summary>
    /// 不可变、平台无关的健康快照，移动应用层无需访问场景对象即可序列化或显示。
    /// Immutable, platform-neutral view of runtime health. Mobile application layers
    /// can serialize or display this object without accessing Unity scene objects.
    /// </summary>
    public sealed class MixedSpaceRuntimeSnapshot
    {
        public MixedSpaceRuntimeState State { get; }
        public MixedSpacePlatformKind Platform { get; }
        public string PlatformRuntimeName { get; }
        public bool IsReady { get; }
        public bool HasValidated => TokenValidationResult.HasValue;
        public bool? TokenValidationResult { get; }
        public int RegisteredMapCount { get; }
        public bool HasSpatialSession { get; }
        public bool HasPlatform { get; }
        public bool HasLocalization { get; }
        public bool HasAlignmentSolver { get; }
        public bool HasTracking { get; }
        public MixedSpaceRuntimeError LastError { get; }

        public bool HasRequiredComponents =>
            HasSpatialSession && HasPlatform && HasLocalization && HasAlignmentSolver && HasTracking;

        public MixedSpaceRuntimeSnapshot(
            MixedSpaceRuntimeState state,
            MixedSpacePlatformKind platform,
            string platformRuntimeName,
            bool isReady,
            bool? tokenValidationResult,
            int registeredMapCount,
            bool hasSpatialSession,
            bool hasPlatform,
            bool hasLocalization,
            bool hasAlignmentSolver,
            bool hasTracking,
            MixedSpaceRuntimeError lastError)
        {
            State = state;
            Platform = platform;
            PlatformRuntimeName = platformRuntimeName ?? string.Empty;
            IsReady = isReady;
            TokenValidationResult = tokenValidationResult;
            RegisteredMapCount = Math.Max(0, registeredMapCount);
            HasSpatialSession = hasSpatialSession;
            HasPlatform = hasPlatform;
            HasLocalization = hasLocalization;
            HasAlignmentSolver = hasAlignmentSolver;
            HasTracking = hasTracking;
            LastError = lastError;
        }
    }
}
