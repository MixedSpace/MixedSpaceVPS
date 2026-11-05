using System;
using MixedSpace.XR;
using UnityEngine;

namespace MixedSpace
{
    /// <summary>
    /// 持有不可变健康快照和隔离故障的诊断事件；采集快照时容忍服务尚未组装完整或正在释放。
    /// Owns immutable health snapshots and fault-isolated diagnostics events.
    /// Snapshot collection tolerates partially composed and tearing-down services.
    /// </summary>
    internal sealed class MixedSpaceRuntimeDiagnostics
    {
        private readonly Func<IMixedSpaceMapRepository> m_Maps;
        private readonly Func<IMixedSpacePlatformInfo> m_PlatformInfo;
        private readonly Func<bool> m_IsReady;
        private readonly Func<bool> m_HasValidated;
        private readonly Func<int> m_LicenseLevel;
        private readonly Func<bool> m_HasSession;
        private readonly Func<bool> m_HasPlatform;
        private readonly Func<bool> m_HasLocalization;
        private readonly Func<bool> m_HasAlignment;
        private readonly Func<bool> m_HasTracking;
        private MixedSpaceRuntimeError m_LastError;

        public MixedSpaceRuntimeDiagnostics(
            Func<IMixedSpaceMapRepository> maps,
            Func<IMixedSpacePlatformInfo> platformInfo,
            Func<bool> isReady,
            Func<bool> hasValidated,
            Func<int> licenseLevel,
            Func<bool> hasSession,
            Func<bool> hasPlatform,
            Func<bool> hasLocalization,
            Func<bool> hasAlignment,
            Func<bool> hasTracking)
        {
            m_Maps = maps ?? throw new ArgumentNullException(nameof(maps));
            m_PlatformInfo = platformInfo ?? throw new ArgumentNullException(nameof(platformInfo));
            m_IsReady = isReady ?? throw new ArgumentNullException(nameof(isReady));
            m_HasValidated = hasValidated ?? throw new ArgumentNullException(nameof(hasValidated));
            m_LicenseLevel = licenseLevel ?? throw new ArgumentNullException(nameof(licenseLevel));
            m_HasSession = hasSession ?? throw new ArgumentNullException(nameof(hasSession));
            m_HasPlatform = hasPlatform ?? throw new ArgumentNullException(nameof(hasPlatform));
            m_HasLocalization = hasLocalization ?? throw new ArgumentNullException(nameof(hasLocalization));
            m_HasAlignment = hasAlignment ?? throw new ArgumentNullException(nameof(hasAlignment));
            m_HasTracking = hasTracking ?? throw new ArgumentNullException(nameof(hasTracking));
        }

        public MixedSpaceRuntimeState State { get; private set; } =
            MixedSpaceRuntimeState.Stopped;

        public event Action<MixedSpaceRuntimeState> StateChanged;
        public event Action<MixedSpaceRuntimeError> ErrorOccurred;
        public event Action<MixedSpaceRuntimeSnapshot> SnapshotChanged;

        public MixedSpaceRuntimeSnapshot Capture()
        {
            int mapCount = 0;
            try
            {
                mapCount = m_Maps()?.SnapshotDefinitions()?.Count ?? 0;
            }
            catch (Exception)
            {
                // 自定义仓库可能正在启动或释放原生状态，健康报告仍必须可用。
                // A custom repository may be starting or releasing native state. Health reporting must remain available through
                // that transition.
            }

            MixedSpacePlatformKind platformKind = MixedSpacePlatformKind.Unknown;
            string platformName = string.Empty;
            try
            {
                IMixedSpacePlatformInfo platform = m_PlatformInfo();
                if (platform != null)
                {
                    platformKind = platform.Kind;
                    platformName = platform.RuntimeName;
                }
            }
            catch (Exception)
            {
                // 缺失计划或损坏的平台接口不应让错误报告再次抛错，从而掩盖原始启动错误。
                // A missing plan or broken platform port must not mask the original startup error by throwing again during
                // reporting.
            }
            return new MixedSpaceRuntimeSnapshot(
                State,
                platformKind,
                platformName,
                m_IsReady(),
                m_HasValidated(),
                m_LicenseLevel(),
                mapCount,
                m_HasSession(),
                m_HasPlatform(),
                m_HasLocalization(),
                m_HasAlignment(),
                m_HasTracking(),
                m_LastError);
        }

        public void TransitionTo(MixedSpaceRuntimeState state)
        {
            if (State == state)
                return;

            State = state;
            MixedSpaceCallbackGuard.Publish(StateChanged, state, nameof(StateChanged));
            PublishSnapshot();
        }

        public void Report(Exception exception, string component, bool critical)
        {
            m_LastError = new MixedSpaceRuntimeError(component, exception);
            if (critical)
                TransitionTo(MixedSpaceRuntimeState.Failed);

            MixedSpaceCallbackGuard.Publish(
                ErrorOccurred,
                m_LastError,
                nameof(ErrorOccurred));
            MixedSpaceLogger.LogError(
                $"{m_LastError.Component}: {m_LastError.Message}");
            Debug.LogException(exception);
        }

        public void PublishSnapshot()
        {
            MixedSpaceCallbackGuard.Publish(
                SnapshotChanged,
                Capture(),
                nameof(SnapshotChanged));
        }
    }
}
