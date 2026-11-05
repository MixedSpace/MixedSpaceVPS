using System;
using TMPro;
using UnityEngine;

namespace MixedSpace.XR
{
    public readonly struct MixedSpaceGeographicPose
    {
        public MixedSpaceGeographicPose(
            double latitude,
            double longitude,
            double altitude,
            float bearing,
            Pose worldPose)
        {
            Latitude = latitude;
            Longitude = longitude;
            Altitude = altitude;
            Bearing = bearing;
            WorldPose = worldPose;
        }

        public double Latitude { get; }
        public double Longitude { get; }
        public double Altitude { get; }
        public float Bearing { get; }
        public Pose WorldPose { get; }
    }

    /// <summary>
    /// 只负责地理显示，从已求解会话状态读取，将数学计算委托给无状态投影服务。
    /// Geographic presentation only. It reads solved session state and delegates
    /// geographic mathematics to a stateless projection service.
    /// </summary>
    [AddComponentMenu("MixedSpace/Views/Geographic Pose")]
    public sealed class MixedSpaceGeographicPoseView : MonoBehaviour,
        IMixedSpaceLocalizationBinding,
        IMixedSpaceNativeCoreConsumer
    {
        [SerializeField]
        private TextMeshProUGUI m_OutputLabel;
        private Camera m_ViewCamera;
        private MixedSpaceGeoPoseProjection m_Projection;
        private Connection m_Connection;

        public MixedSpaceGeographicPose CurrentPose { get; private set; }

        public void SetNativeCore(IMixedSpaceNativeCore nativeCore)
        {
            if (nativeCore == null)
                throw new ArgumentNullException(nameof(nativeCore));
            m_Projection = new MixedSpaceGeoPoseProjection(nativeCore.GeoTransform);
        }

        public IDisposable Connect(IMixedSpaceLocalizationReadModel source)
        {
            if (source == null)
                throw new ArgumentNullException(nameof(source));
            if (m_Connection != null)
            {
                throw new InvalidOperationException(
                    "A geographic pose view can display only one session at a time.");
            }

            return m_Connection = new Connection(this, source);
        }

        private void Update()
        {
            MixedSpaceLocalizationSnapshot snapshot = m_Connection?.Source.Snapshot;
            if (snapshot == null || !snapshot.HasFix || m_Projection == null)
                return;

            Camera camera = ResolveViewCamera();
            if (!camera)
                return;
            if (!m_Projection.TryProject(
                    snapshot,
                    camera.transform.position,
                    camera.transform.forward,
                    out MixedSpaceGeographicPose pose))
            {
                return;
            }

            CurrentPose = pose;
            if (m_OutputLabel)
            {
                m_OutputLabel.text = string.Format(
                    "VLat: {0:0.000000}, VLon: {1:0.000000}, VAlt: {2:0.0}, VBRG: {3:0.0}",
                    pose.Latitude,
                    pose.Longitude,
                    pose.Altitude,
                    pose.Bearing);
            }
        }

        private Camera ResolveViewCamera()
        {
            if (!m_ViewCamera)
                m_ViewCamera = Camera.main;
            return m_ViewCamera;
        }

        private void OnDestroy()
        {
            m_Connection?.Dispose();
        }

        private sealed class Connection : IDisposable
        {
            private MixedSpaceGeographicPoseView m_View;
            private IDisposable m_Subscription;

            public Connection(
                MixedSpaceGeographicPoseView view,
                IMixedSpaceLocalizationReadModel source)
            {
                m_View = view;
                Source = source;
                m_Subscription = source.Subscribe(snapshot =>
                {
                    if (!snapshot.HasFix && m_View)
                        m_View.CurrentPose = default;
                });
                view.CurrentPose = default;
            }

            public IMixedSpaceLocalizationReadModel Source { get; }

            public void Dispose()
            {
                m_Subscription?.Dispose();
                m_Subscription = null;
                if (m_View)
                {
                    m_View.CurrentPose = default;
                    m_View.m_Connection = null;
                }
                m_View = null;
            }
        }
    }
}
