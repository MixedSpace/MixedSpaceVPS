using System;
using System.Collections.Generic;
using GxrSdk;
using MixedSpace.XR;
using UnityEngine;

namespace MixedSpace.Platforms.XRany
{
    /// <summary>
    /// GXR 左鱼眼图像桥接，定位和地图状态仍由 MixedSpace 核心管理。
    /// GXR left fisheye frame bridge. Localization and map state remain in MixedSpace Core.
    /// </summary>
    [AddComponentMenu("MixedSpace/Platforms/XRany Adapter")]
    public sealed class MixedSpaceXRanyAdapter : MixedSpaceCallbackCameraAdapter,
        IGxrFishEyeCameraFrameHandler, IGxrHeadPoseHandler
    {
        private GxrPoseCacheProcessor m_PoseCache = new GxrPoseCacheProcessor();
        private const int CapturePoseCapacity = 256;
        private readonly object m_CapturePoseGate = new object();
        private readonly Dictionary<long, Pose> m_CapturePoses = new Dictionary<long, Pose>();
        private readonly Queue<long> m_CapturePoseOrder = new Queue<long>();
        private Pose m_CameraFromHead;
        private long m_CurrentTimestamp;
        private Vector4 m_Intrinsics;
        private Vector4 m_Distortion;
        private int m_CalibrationWidth;
        private int m_CalibrationHeight;
        private bool m_Calibrated;
        private bool m_CameraSubscribed;
        private bool m_PoseSubscribed;

        private Coroutine m_BindRoutine;

        protected override void OpenCamera()
        {
            m_Calibrated = false;
            lock (m_CapturePoseGate)
            {
                m_CapturePoses.Clear();
                m_CapturePoseOrder.Clear();
            }
            m_PoseCache = new GxrPoseCacheProcessor();
            m_CurrentTimestamp = 0;
            m_BindRoutine = StartCoroutine(BindWhenReady());
        }

        private System.Collections.IEnumerator BindWhenReady()
        {
            while (GxrCameraAccessor.LeftFishEyeAnchor == null ||
                   GxrSystemAccessor.CameraSystem == null ||
                   GxrSystemAccessor.InputSystem == null)
                yield return null;
            var anchor = GxrCameraAccessor.LeftFishEyeAnchor;
            m_CameraFromHead = new Pose(anchor.localPosition, anchor.localRotation);
            GxrSystemAccessor.InputSystem.RegisterHandler<IGxrHeadPoseHandler>(this);
            m_PoseSubscribed = true;
            GxrSystemAccessor.CameraSystem.RegisterHandler<IGxrFishEyeCameraFrameHandler>(this);
            m_CameraSubscribed = true;
            m_BindRoutine = null;
        }
        protected override void CloseCamera()
        {
            if (m_BindRoutine != null) { StopCoroutine(m_BindRoutine); m_BindRoutine = null; }
            if (m_CameraSubscribed)
            {
                GxrSystemAccessor.CameraSystem?.UnregisterHandler<IGxrFishEyeCameraFrameHandler>(this);
                m_CameraSubscribed = false;
            }
            if (m_PoseSubscribed)
            {
                GxrSystemAccessor.InputSystem?.UnregisterHandler<IGxrHeadPoseHandler>(this);
                m_PoseSubscribed = false;
            }
            m_Calibrated = false;
        }

        protected override bool IsDeviceTracking => GxrServiceKeeper.IsGlasses6DofTracking;

        protected override bool UseHostMonotonicTimestamps => false;

        protected override Pose ReadCurrentPose(out long timestamp)
        {
            timestamp = m_CurrentTimestamp;
            // 实时融合使用渲染相机位姿，图像采集使用鱼眼相机位姿。
            // Live fusion uses the render camera pose; image capture uses the fisheye camera pose.
            var renderCamera = GxrCameraAccessor.MainCamera;
            if (renderCamera == null)
                throw new InvalidOperationException("GXR main camera is not ready for live tracking.");
            return new Pose(renderCamera.transform.position, renderCamera.transform.rotation);
        }

        protected override Pose ReadCapturePose(long timestamp)
        {
            lock (m_CapturePoseGate)
            {
                if (m_CapturePoses.TryGetValue(timestamp, out Pose pose))
                {
                    return pose;
                }
            }
            throw new InvalidOperationException("GXR capture pose is no longer cached for frame " + timestamp + ".");
        }

        protected override void ReadCalibration(out Vector4 intrinsics, out Vector4 distortion, out float alpha)
        {
            if (!m_Calibrated) throw new InvalidOperationException("GXR left fisheye calibration is not ready.");
            intrinsics = m_Intrinsics;
            distortion = m_Distortion;
            alpha = 0f; // 当前 XRLoc 校正器使用 K' = K。 / The current XRLoc rectifier uses K' = K.
        }



        public void OnHeadPoseUpdated(GxrHeadPoseEventData eventData)
        {
            if (eventData == null || eventData.SystemTimestamp == 0 ||
                eventData.SystemTimestamp > long.MaxValue) return;
            m_PoseCache.EnqueuePose(eventData.PoseData, eventData.SystemTimestamp);
            m_CurrentTimestamp = (long)eventData.SystemTimestamp;
        }

        public void OnFishEyeCameraFrameUpdated(GxrCameraFrameEventData eventData)
        {
            if (eventData == null || !IsDeviceTracking) return;
            var frame = eventData.FrameInfo;
            if (frame.Timestamp == 0 || frame.Timestamp > long.MaxValue ||
                frame.GrayData == null) return;
            if (!m_Calibrated && !TryReadCalibration()) return;
            if (frame.Width != m_CalibrationWidth || frame.Height != m_CalibrationHeight ||
                frame.GrayData.Length != m_CalibrationWidth * m_CalibrationHeight) return;
            Pose headPose = m_PoseCache.GetPose(frame.Timestamp);
            if (headPose == Pose.identity) return;
            long timestamp = (long)frame.Timestamp;
            // 保留每帧匹配的位姿，直到采集使用方读取。
            // Retain each frame's matched pose until the capture consumer reads it.
            lock (m_CapturePoseGate)
            {
                if (!m_CapturePoses.ContainsKey(timestamp)) m_CapturePoseOrder.Enqueue(timestamp);
                m_CapturePoses[timestamp] = CameraPose(headPose);
                while (m_CapturePoseOrder.Count > CapturePoseCapacity)
                {
                    long expired = m_CapturePoseOrder.Dequeue();
                    m_CapturePoses.Remove(expired);
                }
            }
            ReceiveLuminance(m_CalibrationWidth, m_CalibrationHeight, frame.GrayData,
                timestamp);
        }

        private bool TryReadCalibration()
        {
            if (GxrGlassesProperties.Instance == null ||
                !GxrGlassesProperties.Instance.IsDeviceReady ||
                !GxrSystemAccessor.GetHeadDisplayDataProvider().IsFirstValidPoseCached()) return false;
            var c = GxrGlassesProperties.Instance.FishEyeCameraCalibrationLeft;
            if (c.distortionType != "kb4" || c.width <= 0 || c.height <= 0 || c.k == null || c.k.Length < 4 ||
                c.dist == null || c.dist.Length < 4) return false;
            m_CalibrationWidth = c.width;
            m_CalibrationHeight = c.height;
            Matrix4x4 k = GxrCameraCalibrationUtils.GetIntrinsicMatrixFromFishEyeCalibration(c);
            m_Intrinsics = new Vector4(k[0, 0], k[1, 1], k[0, 2], k[1, 2]);
            m_Distortion = new Vector4(c.dist[0], c.dist[1], c.dist[2], c.dist[3]);
            m_Calibrated = true;
            return true;
        }

        private Pose CameraPose(Pose head) => new Pose(
            head.position + head.rotation * m_CameraFromHead.position,
            head.rotation * m_CameraFromHead.rotation);
    }
}
