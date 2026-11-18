using System.Diagnostics;
using UnityEngine;
using UnityEngine.XR.ARFoundation;

namespace MixedSpace.XR
{
    /// <summary>
    /// 管理场景跟踪绑定和订阅；位姿时间戳使用融合接口约定的主机单调时钟。
    /// Manages scene tracking bindings and subscriptions; pose timestamps use the monotonic host clock required by fusion.
    /// </summary>
    internal sealed class ARFoundationPoseSource : IMixedSpaceCameraPoseSource
    {
        private Transform m_Camera;
        private bool m_Listening;
        public bool IsTracking { get; private set; }
        public bool HasPose => m_Camera != null;

        public void BindCamera() => m_Camera = Camera.main != null ? Camera.main.transform : null;

        public void Observe()
        {
            if (m_Listening) return;
            m_Listening = true;
#if !UNITY_EDITOR
            IsTracking = ARSession.state == ARSessionState.SessionTracking;
            ARSession.stateChanged += OnTrackingChanged;
#endif
        }

        public void StopObserving()
        {
            if (m_Listening)
            {
#if !UNITY_EDITOR
                ARSession.stateChanged -= OnTrackingChanged;
#endif
                m_Listening = false;
            }
            ClearTracking();
        }

        public void ClearTracking() => IsTracking = false;
        private void OnTrackingChanged(ARSessionStateChangedEventArgs state) =>
            IsTracking = state.state == ARSessionState.SessionTracking;

        public MixedSpacePlatformPoseSample SamplePose() => new MixedSpacePlatformPoseSample
        {
            Position = m_Camera != null ? m_Camera.position : Vector3.zero,
            Rotation = m_Camera != null ? m_Camera.rotation : Quaternion.identity,
            TimestampNanoseconds = (long)(Stopwatch.GetTimestamp() * (1000000000.0 / Stopwatch.Frequency)),
            TimestampSource = MixedSpaceTrackingTimestampSource.HostMonotonic,
            IsTracking = IsTracking
        };
    }
}
