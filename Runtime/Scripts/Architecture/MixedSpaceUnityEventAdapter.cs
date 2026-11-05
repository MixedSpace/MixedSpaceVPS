using System;
using System.Linq;
using MixedSpace.XR;

namespace MixedSpace
{
    /// <summary>
    /// 将强类型运行时观测适配为 Unity 应用信号；首次定位呈现状态归此处，不归定位引擎或 SDK 场景入口。
    /// Adapts typed runtime observations to Unity-facing application signals.
    /// First-fix presentation state belongs here, not in the localization engine
    /// or the SDK scene entry point.
    /// </summary>
    internal sealed class MixedSpaceUnityEventAdapter :
        IMixedSpaceSessionObserver,
        IMixedSpaceLocalizationObserver
    {
        private readonly MixedSpaceApplicationSignals m_Signals;
        private readonly Action m_CloseManualLocalizationGate;
        private bool m_HasReportedFirstFix;

        public MixedSpaceUnityEventAdapter(
            MixedSpaceApplicationSignals signals,
            Action closeManualLocalizationGate)
        {
            m_Signals = signals ?? throw new ArgumentNullException(nameof(signals));
            m_CloseManualLocalizationGate = closeManualLocalizationGate;
        }

        public void Observe(MixedSpaceSessionTransition transition)
        {
            if (transition == null)
                return;

            if (transition.Command == MixedSpaceSessionCommand.Reset &&
                transition.Current == MixedSpaceSessionPhase.Resetting)
                m_HasReportedFirstFix = false;

            if (transition.Previous != transition.Current)
            {
                if (transition.Current == MixedSpaceSessionPhase.Running)
                    MixedSpaceCallbackGuard.Invoke(m_Signals.LocalizationStarted, nameof(m_Signals.LocalizationStarted));
                else if (transition.Current == MixedSpaceSessionPhase.Stopped)
                    MixedSpaceCallbackGuard.Invoke(m_Signals.LocalizationStopped, nameof(m_Signals.LocalizationStopped));
            }
            if (transition.Command == MixedSpaceSessionCommand.Reset &&
                transition.Previous == MixedSpaceSessionPhase.Resetting &&
                transition.Current != MixedSpaceSessionPhase.Resetting)
            {
                m_HasReportedFirstFix = false;
                MixedSpaceCallbackGuard.Invoke(m_Signals.LocalizationResetCompleted, nameof(m_Signals.LocalizationResetCompleted));
            }

            if (transition.Command == MixedSpaceSessionCommand.Reset &&
                transition.Current == MixedSpaceSessionPhase.Resetting)
            {
                MixedSpaceCallbackGuard.Invoke(
                    m_Signals.SessionReinitialized,
                    nameof(m_Signals.SessionReinitialized));
            }
            else if (transition.Current == MixedSpaceSessionPhase.Suspended)
            {
                MixedSpaceCallbackGuard.Invoke(
                    m_Signals.SessionSuspended,
                    nameof(m_Signals.SessionSuspended));
            }
            else if (transition.Current == MixedSpaceSessionPhase.Running &&
                (transition.Command == MixedSpaceSessionCommand.Continue ||
                 transition.Command == MixedSpaceSessionCommand.AllowLocalization))
            {
                MixedSpaceCallbackGuard.Invoke(
                    m_Signals.SessionActivated,
                    nameof(m_Signals.SessionActivated));
            }
        }

        public void Observe(MixedSpaceLocalizationCycleReport report)
        {
            if (report == null)
                return;

            MixedSpaceCallbackGuard.Invoke(
                () => m_Signals.FrameEvaluationFinished?.Invoke(report.Batch),
                nameof(m_Signals.FrameEvaluationFinished));

            if (!report.HasSuccess)
            {
                MixedSpaceCallbackGuard.Invoke(
                    m_Signals.FrameEvaluationMissed,
                    nameof(m_Signals.FrameEvaluationMissed));
                return;
            }

            MixedSpaceCallbackGuard.Invoke(
                m_CloseManualLocalizationGate,
                "ManualLocalizationGate");
            if (!m_HasReportedFirstFix)
            {
                m_HasReportedFirstFix = true;
                MixedSpaceCallbackGuard.Invoke(
                    m_Signals.FirstSpatialFix,
                    nameof(m_Signals.FirstSpatialFix));
            }

            MixedSpaceCallbackGuard.Invoke(
                () => m_Signals.SpatialFixesAvailable?.Invoke(
                    report.SuccessfulMapKeys.ToArray()),
                nameof(m_Signals.SpatialFixesAvailable));
        }
    }
}
