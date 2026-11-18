using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using MixedSpace.XR;
using Unity.XR.PXR;
using Unity.XR.PXR.SecureMR;
using UnityEngine;
using UnityEngine.XR;

namespace MixedSpace.Platforms.Pico
{
    /// <summary>
    /// 使用 PXR_CameraImage 拉取接口的 PICO 4 Ultra 相机适配器。
    /// PICO 4 Ultra camera adapter using the PXR_CameraImage pull API.
    /// </summary>
    [AddComponentMenu("MixedSpace/Platforms/PICO 4 U Adapter")]
    public sealed class MixedSpacePico4UAdapter : MonoBehaviour, IMixedSpacePlatformAdapter, IMixedSpaceCaptureDiagnostics
    {
        [SerializeField] private MixedSpacePicoProfile m_Profile;
        private static MixedSpacePico4UAdapter s_Owner;
        private Transform m_Camera;
        private XrCameraIdPICO m_CameraId = XrCameraIdPICO.XR_CAMERA_ID_RGB_LEFT_PICO;
        private int m_Width, m_Height;
        private bool m_Capturing, m_Paused, m_ResumeRequested, m_RequestingPermission;
        private long m_LastCaptureTime;
        private Task<bool> m_Starting;
        private InputDevice m_HeadDevice;
        private readonly MixedSpacePicoPoseHistory m_PoseHistory = new MixedSpacePicoPoseHistory();
        private readonly List<XRInputSubsystem> m_InputSubsystems = new List<XRInputSubsystem>();
        private bool m_WritePoseLogs;
        private float m_NextTrackingDiagnosticTime;
        private const long MaxPoseGapNanoseconds = 50000000L;

        public void SetWritePoseLogs(bool enabled) => m_WritePoseLogs = enabled;

        private void LateUpdate()
        {
            if (!m_WritePoseLogs || !m_Capturing || m_Paused ||
                Time.realtimeSinceStartup < m_NextTrackingDiagnosticTime) return;
            m_NextTrackingDiagnosticTime = Time.realtimeSinceStartup + 0.2f;
            try { MixedSpacePoseDiagnostics.AppendTrackingSample(SamplePose(), "pico_center_eye"); }
            catch (Exception error)
            {
                MixedSpaceLogger.LogError("PICO tracking diagnostic failed (" + error.GetType().Name + ").");
            }
        }

        private void OnEnable() => Application.onBeforeRender += RecordRenderedPose;

        // Run after TrackedPoseDriver's before-render update. Keep the existing center-eye/world basis.
        [BeforeRenderOrder(10000)]
        private void RecordRenderedPose()
        {
            if (!TryGetCurrentPose(out var pose))
            {
                m_PoseHistory.Clear();
                return;
            }
            if (pose.TimestampNanoseconds < m_PoseHistory.LatestTimestampNanoseconds) m_PoseHistory.Clear();
            m_PoseHistory.TryAdd(pose);
        }

        private bool HasTrackedHead()
        {
            if (!m_HeadDevice.isValid) m_HeadDevice = InputDevices.GetDeviceAtXRNode(XRNode.Head);
            const InputTrackingState required = InputTrackingState.Position | InputTrackingState.Rotation;
            return m_HeadDevice.isValid &&
                m_HeadDevice.TryGetFeatureValue(CommonUsages.isTracked, out bool tracked) && tracked &&
                m_HeadDevice.TryGetFeatureValue(CommonUsages.trackingState, out InputTrackingState state) &&
                (state & required) == required;
        }

        private bool TryGetCurrentPose(out MixedSpacePlatformPoseSample pose)
        {
            pose = default;
            if (!m_Capturing || m_Paused || !m_Camera || !HasTrackedHead()) return false;
#if UNITY_ANDROID && !UNITY_EDITOR
            // PXR returns the frame's predicted display time in milliseconds in the same runtime time domain
            // as camera captureTime (XrTime). Only units change; never estimate an offset from image arrival.
            double milliseconds = 0;
            if (PXR_Plugin.Pxr_GetPredictedDisplayTime(ref milliseconds) != 0 ||
                !MixedSpacePicoPoseHistory.TryDisplayTimeToNanoseconds(milliseconds, out long nanoseconds))
                return false;
            pose = new MixedSpacePlatformPoseSample
            {
                IsTracking = true, TimestampNanoseconds = nanoseconds,
                TimestampSource = MixedSpaceTrackingTimestampSource.DevicePredicted,
                Position = m_Camera.position, Rotation = m_Camera.rotation
            };
            return true;
#else
            return false;
#endif
        }

        private void OnTrackingOriginUpdated(XRInputSubsystem subsystem) => m_PoseHistory.Clear();

        public Task<bool> StartAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (m_Paused) throw new InvalidOperationException("Cannot start the PICO camera while paused.");
            if (m_Capturing) return Task.FromResult(true);
            if (m_Starting != null && !m_Starting.IsCompleted) return m_Starting;
            return m_Starting = StartCoreAsync(token);
        }

        private async Task<bool> StartCoreAsync(CancellationToken token)
        {
#if !UNITY_ANDROID || UNITY_EDITOR
            throw new PlatformNotSupportedException("PICO camera capture requires an Android device.");
#else
            if (!isActiveAndEnabled || !m_Profile || !Camera.main)
                throw new InvalidOperationException("Enable the adapter, assign a PICO profile, and provide a MainCamera.");
            if (s_Owner != null && s_Owner != this)
                throw new InvalidOperationException("Another PICO 4 U adapter owns the camera.");
            s_Owner = this;
            m_Camera = Camera.main.transform;
            try
            {
                m_RequestingPermission = true;
                try { await EnsureCameraPermissionAsync(token); }
                finally { m_RequestingPermission = false; }
                while (m_Paused) await Task.Delay(20, token);
                token.ThrowIfCancellationRequested();
                if (PXR_CameraImage.GetAvailableCameras(out XrCameraIdPICO[] ids) != PxrResult.SUCCESS ||
                    ids == null || Array.IndexOf(ids, m_CameraId) < 0)
                    throw new InvalidOperationException("PICO RGB left camera is unavailable.");
                var result = await PXR_CameraImage.CreateCameraDeviceAsync(m_CameraId);
                token.ThrowIfCancellationRequested();
                if (result != PxrResult.SUCCESS)
                    throw new InvalidOperationException("CreateCameraDeviceAsync failed: " + result);
                result = PXR_CameraImage.GetCameraImageResolutionCapability(m_CameraId, out PxrExtent2Di[] resolutions);
                if (result != PxrResult.SUCCESS || resolutions == null || resolutions.Length == 0)
                    throw new InvalidOperationException("PICO camera has no reported resolution.");
                m_Width = resolutions[0].width;
                m_Height = resolutions[0].height;
                foreach (var resolution in resolutions)
                    if (resolution.width == m_Profile.Width && resolution.height == m_Profile.Height)
                    {
                        m_Width = resolution.width;
                        m_Height = resolution.height;
                        break;
                    }
                result = await PXR_CameraImage.CreateCameraCaptureSessionAsync(m_CameraId, m_Width, m_Height,
                    XrCameraImageFpsPICO.XR_CAMERA_IMAGE_FPS_30_PICO,
                    XrCameraImageFormatPICO.XR_CAMERA_IMAGE_FORMAT_RGBA_8888_PICO,
                    XrCameraDataTransferTypePICO.XR_CAMERA_DATA_TRANSFER_TYPE_RAW_BUFFER_PICO,
                    XrCameraModelPICO.XR_CAMERA_MODEL_PINHOLE_PICO);
                token.ThrowIfCancellationRequested();
                if (result != PxrResult.SUCCESS)
                    throw new InvalidOperationException("CreateCameraCaptureSessionAsync failed: " + result);
                result = PXR_CameraImage.BeginCameraCapture(m_CameraId);
                if (result != PxrResult.SUCCESS)
                    throw new InvalidOperationException("BeginCameraCapture failed: " + result);
                m_LastCaptureTime = 0;
                m_PoseHistory.Clear();
                m_Capturing = true;
                SubsystemManager.GetInstances(m_InputSubsystems);
                foreach (XRInputSubsystem subsystem in m_InputSubsystems)
                    subsystem.trackingOriginUpdated += OnTrackingOriginUpdated;
                return true;
            }
            catch
            {
                await ShutdownAsync();
                throw;
            }
#endif
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        private static async Task EnsureCameraPermissionAsync(CancellationToken token)
        {
            if (UnityEngine.Android.Permission.HasUserAuthorizedPermission(UnityEngine.Android.Permission.Camera)) return;
            var result = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var callbacks = new UnityEngine.Android.PermissionCallbacks();
            callbacks.PermissionGranted += _ => result.TrySetResult(true);
            callbacks.PermissionDenied += _ => result.TrySetResult(false);
            callbacks.PermissionDeniedAndDontAskAgain += _ => result.TrySetResult(false);
            UnityEngine.Android.Permission.RequestUserPermission(UnityEngine.Android.Permission.Camera, callbacks);
            using (token.Register(() => result.TrySetCanceled()))
            {
                if (!await result.Task || !UnityEngine.Android.Permission.HasUserAuthorizedPermission(UnityEngine.Android.Permission.Camera))
                    throw new UnauthorizedAccessException("PICO camera permission was denied.");
            }
        }
#endif

        public Task<IMixedSpacePlatformFrame> CaptureFrameAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (!TryGetCurrentPose(out var livePose))
            {
                m_PoseHistory.Clear();
                return Task.FromResult<IMixedSpacePlatformFrame>(Empty());
            }

            ulong imageId;
            long captureTime;
            var result = PXR_CameraImage.AcquireCameraImage(m_CameraId, m_LastCaptureTime, out imageId, out captureTime);
            if (result != PxrResult.SUCCESS || imageId == 0)
                return Task.FromResult<IMixedSpacePlatformFrame>(Empty(true));

            byte[] pixels;
            int width, height;
            MixedSpacePlatformPoseSample exposurePose;
            try
            {
                if (captureTime <= 0 || captureTime <= m_LastCaptureTime)
                    return Task.FromResult<IMixedSpacePlatformFrame>(Empty(true));
                m_LastCaptureTime = captureTime;
                long age = livePose.TimestampNanoseconds - captureTime;
                if (age < 0 || age > (long)m_Profile.MaxFrameAgeMilliseconds * 1000000L ||
                    !m_PoseHistory.TrySample(captureTime, MaxPoseGapNanoseconds, out exposurePose,
                        out _, out _))
                {

                    return Task.FromResult<IMixedSpacePlatformFrame>(Empty(true));
                }

                if (PXR_CameraImage.GetCameraImageData(m_CameraId, imageId, out XrCameraImageDataRawBuffer image) != PxrResult.SUCCESS)
                    return Task.FromResult<IMixedSpacePlatformFrame>(Empty(true));
                width = checked((int)image.width);
                height = checked((int)image.height);
                int stride = checked((int)image.stride);
                int sourceChannels = checked((int)image.bytesPerPixel);
                if (image.buffer == IntPtr.Zero || width <= 0 || height <= 0 || sourceChannels < 3 ||
                    stride < checked(width * sourceChannels))
                    return Task.FromResult<IMixedSpacePlatformFrame>(Empty(true));
                pixels = new byte[checked(width * height * 3)];
                var row = new byte[stride];
                for (int y = 0; y < height; y++)
                {
                    Marshal.Copy(IntPtr.Add(image.buffer, checked(y * stride)), row, 0, stride);
                    int destination = checked((height - 1 - y) * width * 3);
                    for (int x = 0; x < width; x++)
                    {
                        int source = x * sourceChannels;
                        int target = destination + x * 3;
                        pixels[target] = row[source];
                        pixels[target + 1] = row[source + 1];
                        pixels[target + 2] = row[source + 2];
                    }
                }
            }
            finally
            {
                PXR_CameraImage.ReleaseCameraImage(m_CameraId, imageId);
            }
            if (PXR_CameraImage.GetCameraIntrinsics(m_CameraId, out XrCameraIntrinsics intrinsics) != PxrResult.SUCCESS)
                return Task.FromResult<IMixedSpacePlatformFrame>(Empty());
            token.ThrowIfCancellationRequested();
            if (m_Paused || !HasTrackedHead())
            {
                m_PoseHistory.Clear();
                return Task.FromResult<IMixedSpacePlatformFrame>(Empty());
            }
            var cameraIntrinsics = new Vector4((float)intrinsics.focalLength.X, (float)intrinsics.focalLength.Y,
                (float)intrinsics.principalPoint.X, (float)intrinsics.principalPoint.Y);
            var profile = new MixedSpaceCaptureProfile(width, height, 3, MixedSpacePixelFormat.Rgb24,
                cameraIntrinsics, exposurePose.Position, exposurePose.Rotation, captureTime,
                Quaternion.Euler(180f, -180f, 0f));
            return Task.FromResult<IMixedSpacePlatformFrame>(new MixedSpacePlatformFrame(
                new MixedSpaceTrackingSnapshot(1), new MixedSpaceCameraCapture(profile, new MixedSpaceManagedPixelSource(pixels))));
        }

        public MixedSpacePlatformPoseSample SamplePose()
        {
            if (TryGetCurrentPose(out var pose)) return pose;
            m_PoseHistory.Clear();
            return default;
        }

        private static MixedSpacePlatformFrame Empty(bool tracking = false) =>
            new MixedSpacePlatformFrame(new MixedSpaceTrackingSnapshot(tracking ? 1 : 0), null);

        public Task ShutdownAsync()
        {
            foreach (XRInputSubsystem subsystem in m_InputSubsystems)
                subsystem.trackingOriginUpdated -= OnTrackingOriginUpdated;
            m_InputSubsystems.Clear();
            m_PoseHistory.Clear();
            if (m_Capturing)
            {
                PXR_CameraImage.EndCameraCapture(m_CameraId);
                m_Capturing = false;
            }
            m_LastCaptureTime = 0;
            if (s_Owner == this) s_Owner = null;
            return Task.CompletedTask;
        }

        private async void OnApplicationPause(bool paused)
        {
            if (m_Paused == paused) return;
            m_Paused = paused;
            if (m_RequestingPermission) return;
            try
            {
                if (paused)
                {
                    m_ResumeRequested = m_Capturing;
                    await ShutdownAsync();
                }
                else if (m_ResumeRequested && isActiveAndEnabled)
                {
                    m_ResumeRequested = false;
                    await StartAsync(CancellationToken.None);
                }
            }
            catch (Exception error) { UnityEngine.Debug.LogException(error); }
        }

        private async void OnDisable()
        {
            Application.onBeforeRender -= RecordRenderedPose;
            m_ResumeRequested = false;
            try { await ShutdownAsync(); }
            catch (Exception error) { UnityEngine.Debug.LogException(error); }
        }
    }
}
