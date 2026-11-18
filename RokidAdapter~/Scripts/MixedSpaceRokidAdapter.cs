#if UNITY_EDITOR || UNITY_ANDROID
using System;
using MixedSpace.XR;
using Rokid.UXR.Module;
using Rokid.UXR.Native;
using UnityEngine;

namespace MixedSpace.Platforms.Rokid
{
    /// <summary>
    /// UXR 3.0.3 桥接；定位、地图、融合和事件由核心负责。两种位姿 API 已返回 Unity 坐标系的物理相机位姿，不要再次反射。先 StartCameraPreview，等待
    /// IsPreviewing，再订阅 OnCameraDataUpdate，最后 SetCameraPreviewDataType
    /// 安装原生回调。仅复用系统预览而跳过启动等待会导致回调未绑定及启动超时；启动前须获得相机权限。
    /// Thin UXR 3.0.3 bridge. Localization, map ownership, fusion and events remain in Core.
    /// Both pose APIs already return Unity-space physical CAMERA poses; do not reflect twice.
    /// Follows the vendor uxr008 sample contract exactly, because a preview that is already
    /// running is NOT sufficient to receive frames:
    ///   1. StartCameraPreview()            - unconditionally ask the runtime to run preview.
    ///   2. wait until IsPreviewing()       - the stream must be live before registering.
    ///   3. OnCameraDataUpdate += handler   - subscribe on the managed side first.
    ///   4. SetCameraPreviewDataType(type)  - install the native frame callback.
    /// Skipping step 1/2 (as a "reuse the system preview" shortcut) leaves the native
    /// callback unbound and StartCoreAsync dies with TimeoutException. Camera permission
    /// must be granted before startup.
    /// </summary>
    [AddComponentMenu("MixedSpace/Platforms/Rokid Adapter")]
    public sealed class MixedSpaceRokidAdapter : MixedSpaceCallbackCameraAdapter, IMixedSpaceTrackingLossSource
    {
        /// <summary>
        /// 预览数据类型：1 为 ARGB，2 为 NV21，3 为 RGBA32。
        /// Preview data type: 1-ARGB, 2-NV21, 3-RGBA32.
        /// </summary>
        private const int PreviewDataTypeNv21 = 2;

        /// <summary>
        /// 绑定回调前等待预览就绪的最大忙等时长。
        /// Bounded spin budget for the preview to come alive before binding callbacks.
        /// </summary>
        private const int PreviewReadySpinMilliseconds = 500;

        private bool m_OwnsPreview;
        private bool m_Subscribed;
        private bool m_WasDeviceTracking;
        private bool m_HasTrackingOriginHeight;
        private float m_TrackingOriginHeight;

        /// <inheritdoc />
        public long LastTrackingLossTimestampNanoseconds { get; private set; }

        protected override void OpenCamera()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(UnityEngine.Android.Permission.Camera))
                throw new InvalidOperationException("Grant camera permission before starting the Rokid adapter.");

            // 始终请求启动预览；VST 设备可能已有预览，重复请求由厂商接口处理。
            // Step 1: always request preview. On VST devices the runtime may already be previewing; calling it again is
            // harmless and is what the vendor sample does.
            NativeInterface.NativeAPI.StartCameraPreview();
            m_OwnsPreview = true;

            // 步骤 2：原生帧回调需要活动图像流。此处短暂等待预览就绪，外层启动流程仍执行整体超时检查。
            // Step 2: the native frame callback can only be bound to a live stream. StartCoreAsync already tolerates the
            // stream starting up asynchronously, so a short bounded wait here is enough; the outer startup timeout still
            // applies.
            WaitForPreviewReady();

            // 步骤 3、4：先订阅 C# 事件，再启用原生回调派送。
            // Step 3 + 4: subscribe in C# before enabling delivery from the native callback.
            NativeInterface.NativeAPI.OnCameraDataUpdate += ReceiveNv21;
            m_Subscribed = true;
            NativeInterface.NativeAPI.SetCameraPreviewDataType(PreviewDataTypeNv21);

#else
            throw new PlatformNotSupportedException("Rokid camera capture requires an Android device.");
#endif
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        /// <summary>
        /// 在 Unity 主线程使用 Stopwatch 有界忙等预览就绪，避免设备时钟跳变影响等待。等待时间有意保持较短；流尚未就绪就绑定回调可能无法收到帧。
        /// Busy-waits (bounded) until the native preview reports itself as running.
        /// Runs on the Unity main thread, so the wait is kept short by design: the preview
        /// typically flips to ready within one or two frames of StartCameraPreview().
        /// Binding the callback before the stream is live is what made frames never arrive.
        /// Uses Stopwatch to stay immune to device clock jumps.
        /// </summary>
        private static void WaitForPreviewReady()
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (!NativeInterface.NativeAPI.IsPreviewing() &&
                   watch.ElapsedMilliseconds < PreviewReadySpinMilliseconds)
            {
            }
        }
#endif

        protected override void CloseCamera()
        {
            m_WasDeviceTracking = false;
            m_HasTrackingOriginHeight = false;
            m_TrackingOriginHeight = 0f;
            if (m_Subscribed)
            {
                NativeInterface.NativeAPI.OnCameraDataUpdate -= ReceiveNv21;
                m_Subscribed = false;
            }
            if (m_OwnsPreview)
            {
                // 仅移除 C# 订阅者仍会留下活动的原生 IL2CPP 回调。
                // Removing the C# subscriber alone leaves the native IL2CPP callback live.
                NativeInterface.NativeAPI.ClearCameraDataUpdate();
                NativeInterface.NativeAPI.StopCameraPreview();
                m_OwnsPreview = false;
            }
        }
        // 定位图像方向为 90 度。
        protected override int? NativeLocalizationImageRotationAngle => 90;

        protected override bool IsDeviceTracking
        {
            get
            {
                bool tracking = NativeInterface.NativeAPI.GetHeadTrackingStatus() == HeadTrackingStatus.Tracking;
                // 仅在跟踪丢失时记一次时间，使用与定位图像相同的本机单调时钟。
                // Timestamp the transition to lost once, on the same host monotonic clock as localization captures.
                if (m_WasDeviceTracking && !tracking)
                    LastTrackingLossTimestampNanoseconds = HostTicksToNanoseconds(System.Diagnostics.Stopwatch.GetTimestamp());
                m_WasDeviceTracking = tracking;
                return tracking;
            }
        }
        protected override Pose ReadCurrentPose(out long timestamp)
        {
            Pose cameraPose = NativeInterface.NativeAPI.GetCameraPhysicsPose(out timestamp);
            EnsureTrackingOriginHeight(cameraPose);
            return ApplyTrackingOriginHeight(cameraPose);
        }

        protected override Pose ReadCapturePose(long timestamp)
        {
            if (!m_HasTrackingOriginHeight)
            {
                Pose currentPose = NativeInterface.NativeAPI.GetCameraPhysicsPose(out _);
                EnsureTrackingOriginHeight(currentPose);
            }
            return ApplyTrackingOriginHeight(
                NativeInterface.NativeAPI.GetHistoryCameraPhysicsPose(timestamp));
        }

        private void EnsureTrackingOriginHeight(Pose physicalCameraPose)
        {
            if (m_HasTrackingOriginHeight) return;
            Camera viewCamera = Camera.main;
            if (viewCamera == null || viewCamera.transform.localPosition.y < 0.5f) return;
            m_TrackingOriginHeight = viewCamera.transform.position.y - physicalCameraPose.position.y;
            m_HasTrackingOriginHeight = true;
            MixedSpaceLogger.Log(
                $"Rokid tracking origin height calibrated: {m_TrackingOriginHeight:F3} m", MixedSpaceLogger.LoggingLevel.Verbose);
        }

        private Pose ApplyTrackingOriginHeight(Pose physicalCameraPose)
        {
            if (m_HasTrackingOriginHeight)
                physicalCameraPose.position.y += m_TrackingOriginHeight;
            return physicalCameraPose;
        }

        protected override void ReadCalibration(out Vector4 intrinsics, out Vector4 distortion, out float alpha)
        {
            var focal = new float[2];
            var center = new float[2];
            var lens = new float[5];
            NativeInterface.NativeAPI.GetFocalLength(focal);
            NativeInterface.NativeAPI.GetPrincipalPoint(center);
            NativeInterface.NativeAPI.GetDistortion(lens);
            intrinsics = new Vector4(focal[0], focal[1], center[0], center[1]);

            // 畸变参数顺序为 alpha、k1、k2、k3、k4。
            distortion = new Vector4(lens[1], lens[2], lens[3], lens[4]);
            alpha = lens[0];


        }
    }
}
#endif
