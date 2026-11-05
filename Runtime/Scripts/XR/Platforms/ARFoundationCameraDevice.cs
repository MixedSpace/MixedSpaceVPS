using System;
using Unity.Collections;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using Resolution = MixedSpace.XR.MixedSpaceARFoundationAdapter.CaptureResolutionMode;

namespace MixedSpace.XR
{
    /// <summary>
    /// 原生相机边界负责发现 AR 服务、选择支持的分辨率及取得标定原始图像；不保存会话调度、跟踪位姿、显示方向或采集日志。
    /// Native camera boundary: discover required AR services, negotiate supported
    /// resolution and acquire calibrated raw images. No session scheduling,
    /// tracking pose, display orientation or capture journal is stored here.
    /// </summary>
    internal sealed class ARFoundationCameraDevice : IMixedSpaceCameraFeed
    {
        private ARCameraManager m_Manager;
        private XRCameraSubsystem m_ConfigurationOwner;
        private XRCameraConfiguration? m_InitialConfiguration;

        public void Discover()
        {
            m_Manager = UnityEngine.Object.FindObjectOfType<ARCameraManager>();
            if (!m_Manager) throw new MixedSpaceRuntimeUnavailableException("Could not find ARCameraManager.");
            if (!UnityEngine.Object.FindObjectOfType<ARSession>())
                throw new MixedSpaceRuntimeUnavailableException("Could not find ARSession.");
        }

        public bool TryConfigure(Resolution mode)
        {
#if !UNITY_EDITOR && (UNITY_ANDROID || UNITY_IOS)
            // 分辨率控制可能早于 SDK 启动，保留此能力但不假装已取得跟踪位姿。
            // Resolution controls may run before SDK startup. Preserve that capability without pretending to have acquired a
            // tracking pose.
            if (m_Manager == null) m_Manager = UnityEngine.Object.FindObjectOfType<ARCameraManager>();
            var subsystem = m_Manager != null ? m_Manager.subsystem : null;
            if (subsystem == null || !subsystem.running) return false;
            XRCameraConfiguration? current = subsystem.currentConfiguration;
            if (!current.HasValue || current.Value.width <= 0 || current.Value.height <= 0) return false;
            if (!ReferenceEquals(m_ConfigurationOwner, subsystem))
            {
                m_ConfigurationOwner = subsystem;
                m_InitialConfiguration = null;
            }
            if (!m_InitialConfiguration.HasValue) m_InitialConfiguration = current;
            if (mode == Resolution.SystemDefault)
            {
                subsystem.currentConfiguration = m_InitialConfiguration;
                return true;
            }
            var supported = subsystem.GetConfigurations(Allocator.Temp);
            try
            {
                if (!supported.IsCreated || supported.Length == 0) return false;
                var sizes = new Vector2Int[supported.Length];
                for (int i = 0; i < supported.Length; i++)
                    sizes[i] = new Vector2Int(supported[i].width, supported[i].height);
                int index = mode == Resolution.HighestAvailable
                    ? MixedSpaceCaptureResolutionPolicy.FindLargest(sizes)
                    : MixedSpaceCaptureResolutionPolicy.FindNearest(sizes,
                        mode == Resolution.Hd720 ? new Vector2Int(1280, 720) : new Vector2Int(1920, 1080));
                if (index < 0) return false;
                subsystem.currentConfiguration = supported[index];
                MixedSpaceLogger.Log("resolution = " + supported[index].width + "x" + supported[index].height);
                return true;
            }
            finally { if (supported.IsCreated) supported.Dispose(); }
#else
            return true;
#endif
        }

        public bool TryAcquire(out IMixedSpaceCameraImage image)
        {
            image = null;
            if (m_Manager == null || !m_Manager.TryGetIntrinsics(out XRCameraIntrinsics calibration))
            {
                MixedSpaceLogger.LogError("Could not acquire camera intrinsics.");
                return false;
            }
            if (!m_Manager.TryAcquireLatestCpuImage(out XRCpuImage raw))
            {
                MixedSpaceLogger.LogError("Could not acquire camera image.");
                return false;
            }
            try
            {
                image = new RawImage(raw, new Vector4(calibration.focalLength.x, calibration.focalLength.y,
                    calibration.principalPoint.x, calibration.principalPoint.y));
                return true;
            }
            catch { raw.Dispose(); throw; }
        }

        private sealed class RawImage : IMixedSpaceCameraImage
        {
            private readonly XRCpuImage m_Image;
            private bool m_Consumed;
            public int Width { get; }
            public int Height { get; }
            public Vector4 Intrinsics { get; }

            public RawImage(XRCpuImage image, Vector4 intrinsics)
            {
                m_Image = image;
                Width = image.width; Height = image.height; Intrinsics = intrinsics;
            }

            public IMixedSpacePixelSource OpenPixels(MixedSpacePixelFormat format)
            {
                if (m_Consumed) throw new ObjectDisposedException(nameof(RawImage));
                // PixelSource 构造失败时也会释放 CPU 图像。
                // PixelSource also releases the CPU image when construction fails.
                m_Consumed = true;
                return new ARFoundationPixelSource(m_Image, format);
            }

            public void Dispose()
            {
                if (m_Consumed) return;
                m_Consumed = true;
                if (m_Image.valid) m_Image.Dispose();
            }
        }
    }
}
