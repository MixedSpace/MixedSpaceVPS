#if UNITY_EDITOR || UNITY_ANDROID
using MixedSpace.XR;
using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using XvXR.Engine;
namespace MixedSpace.XR
{
    public class ParaTexture
    {
        public Texture2D texture;
        public int width;
        public int height;
        public byte[] raw;
    }


    public class XVXRPlatform : MonoBehaviour, IMixedSpacePlatformAdapter
    {
        XVXRCamera rgbRecord;
        private Transform m_CameraTransform;
        private XvXRHeadTracking m_HeadTracking;
        private XvXRManager m_XvXRManager;
        [SerializeField] private Vector3 m_RgbPositionOffset = new Vector3(0f, 0f, -0.04f);
        private Vector3 m_CameraPosOnFrameCapture = Vector3.zero;
        private Quaternion m_CameraRotOnFrameCapture = Quaternion.identity;

        private bool m_IsRunning;
        private float m_LastCaptureTimingTime = -10f;
        private int m_StartupGeneration;
        private IMixedSpacePlatformFrame m_StartupFrame;
        private float m_StartupFrameTime;

        private const int CameraStartupTimeoutMilliseconds = 3000;
        private const int CameraStartupProbeDelayMilliseconds = 50;
        private const float StartupFrameMaxAgeSeconds = 0.5f;

        public Task<bool> StartAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int generation = ++m_StartupGeneration;
            m_StartupFrame?.Capture?.Dispose();
            m_StartupFrame = null;
            m_IsRunning = false;
            if (!isActiveAndEnabled)
                return Task.FromResult(false);
            if (rgbRecord == null) rgbRecord = new XVXRCamera();
            Camera camera = Camera.main;
            m_CameraTransform = camera != null ? camera.transform : null;
            m_HeadTracking = camera != null ? camera.GetComponentInParent<XvXRHeadTracking>() : null;
            m_XvXRManager = camera != null ? camera.GetComponentInParent<XvXRManager>() : null;
            m_IsRunning = m_CameraTransform != null;
            if (!m_IsRunning)
                return Task.FromResult(false);
#if UNITY_ANDROID && !UNITY_EDITOR
            return WaitForCameraAsync(generation, cancellationToken);
#else
            return Task.FromResult(m_IsRunning);
#endif
        }

        private async Task<bool> WaitForCameraAsync(int generation, CancellationToken cancellationToken)
        {
            bool ready = false;
            var startupTimer = System.Diagnostics.Stopwatch.StartNew();
            int probe = 0;
            try
            {
                while (startupTimer.ElapsedMilliseconds < CameraStartupTimeoutMilliseconds)
                {
                    ++probe;
                    cancellationToken.ThrowIfCancellationRequested();
                    if (generation != m_StartupGeneration || !m_IsRunning || !isActiveAndEnabled)
                        return false;

                    IMixedSpacePlatformFrame frame = await CaptureFrameCoreAsync(cancellationToken, false);
                    if (generation != m_StartupGeneration)
                    {
                        frame?.Capture?.Dispose();
                        return false;
                    }
                    if (frame != null && frame.HasCapture)
                    {
                        m_StartupFrame = frame;
                        m_StartupFrameTime = Time.realtimeSinceStartup;
                        cancellationToken.ThrowIfCancellationRequested();
                        MixedSpaceLogger.Log($"[VPS2-Startup] cameraReady=True warmup={startupTimer.Elapsed.TotalMilliseconds:F1}ms probes={probe}", MixedSpaceLogger.LoggingLevel.Verbose);
                        ready = true;
                        return true;
                    }
                    frame?.Capture?.Dispose();
                    await Task.Delay(CameraStartupProbeDelayMilliseconds, cancellationToken);
                }

                MixedSpaceLogger.LogError($"[VPS2-Startup] Camera startup timed out after {startupTimer.Elapsed.TotalSeconds:F1}s: no valid RGB image with calibrated intrinsics/extrinsics and a matching tracked pose.");
                return false;
            }
            finally
            {
                if (!ready && generation == m_StartupGeneration)
                    await ShutdownAsync();
            }
        }

        public Task ShutdownAsync()
        {
            ++m_StartupGeneration;
            m_IsRunning = false;
            m_StartupFrame?.Capture?.Dispose();
            m_StartupFrame = null;
            rgbRecord?.Dispose();
            rgbRecord = null;
            m_CameraTransform = null;
            m_HeadTracking = null;
            m_XvXRManager = null;
            return Task.CompletedTask;
        }

        public long GetCurrentTimeStamp13()
        {
            return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }

        public byte[] FlipVertical(byte[] raw, int width, int height, int bytesPerPixel)
        {
            int stride = width * bytesPerPixel;
            byte[] flipped = new byte[raw.Length];

            for (int y = 0; y < height; y++)
            {
                int srcIndex = y * stride;
                int dstIndex = (height - 1 - y) * stride;

                Buffer.BlockCopy(raw, srcIndex, flipped, dstIndex, stride);
            }

            return flipped;
        }

        public Task<IMixedSpacePlatformFrame> CaptureFrameAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (m_StartupFrame != null)
            {
                IMixedSpacePlatformFrame frame = m_StartupFrame;
                m_StartupFrame = null;
                if (m_IsRunning && m_CameraTransform != null && isActiveAndEnabled &&
                    Time.realtimeSinceStartup - m_StartupFrameTime <= StartupFrameMaxAgeSeconds &&
                    SamplePose().IsTracking)
                {
                    MixedSpaceLogger.Log("[VPS2-Startup] First localization uses the prepared camera frame.", MixedSpaceLogger.LoggingLevel.Verbose);
                    return Task.FromResult(frame);
                }
                frame.Capture?.Dispose();
            }
            return CaptureFrameCoreAsync(cancellationToken, true);
        }

        private Task<IMixedSpacePlatformFrame> CaptureFrameCoreAsync(CancellationToken cancellationToken, bool logTiming)
        {
            cancellationToken.ThrowIfCancellationRequested();
            MixedSpacePlatformPoseSample pose = SamplePose();
            MixedSpaceTrackingSnapshot tracking = new MixedSpaceTrackingSnapshot(pose.IsTracking ? 1 : 0);
            if (!m_IsRunning || m_CameraTransform == null || rgbRecord == null || !pose.IsTracking)
                return Task.FromResult<IMixedSpacePlatformFrame>(new MixedSpacePlatformFrame(tracking, null));
            long captureStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            Vector4 ins = rgbRecord._ReadRgbCalibration();
            ParaTexture paraTexture = rgbRecord.UpdateGetPara(); 
            cancellationToken.ThrowIfCancellationRequested();
            bool captured = paraTexture != null && paraTexture.raw != null &&
                paraTexture.width > 0 && paraTexture.height > 0 &&
                (long)paraTexture.width * paraTexture.height == paraTexture.raw.Length &&
                ValidIntrinsics(ins);
            MixedSpacePlatformPoseSample imagePose = new MixedSpacePlatformPoseSample();
            bool poseAtImage = captured && TrySampleRgbPose(true, out imagePose);
            captured = captured && poseAtImage;
            float captureNow = Time.realtimeSinceStartup;
            if (logTiming && MixedSpaceLogger.Level <= MixedSpaceLogger.LoggingLevel.Verbose &&
                captureNow - m_LastCaptureTimingTime >= 10f)
            {
                m_LastCaptureTimingTime = captureNow;
                double captureMilliseconds = (System.Diagnostics.Stopwatch.GetTimestamp() - captureStarted) *
                    1000d / System.Diagnostics.Stopwatch.Frequency;
                MixedSpaceLogger.Log($"[VPS2-Timing] capture={captureMilliseconds:F1}ms success={captured} size={paraTexture?.width ?? 0}x{paraTexture?.height ?? 0} poseAtImage={poseAtImage} imageTimeNs={imagePose.TimestampNanoseconds} poseAgeMs={(pose.TimestampNanoseconds - imagePose.TimestampNanoseconds) / 1000000d:F1}", MixedSpaceLogger.LoggingLevel.Verbose);
                if (captured)
                    MixedSpaceLogger.Log($"[VPS2-RGB] imagePose={imagePose.Position.ToString("F4")} renderToRgb={(imagePose.Position - m_CameraTransform.position).ToString("F4")} rotation={imagePose.Rotation.ToString("F4")} rgbLocalOffset={m_RgbPositionOffset.ToString("F4")} displayZ=180", MixedSpaceLogger.LoggingLevel.Verbose);
            }
            if (!captured)
                return Task.FromResult<IMixedSpacePlatformFrame>(new MixedSpacePlatformFrame(tracking, null));

            m_CameraPosOnFrameCapture = imagePose.Position;
            m_CameraRotOnFrameCapture = imagePose.Rotation;
            // 保持 RGB 跟踪位姿为物理相机位姿，既有 180 度图像方向通过 DisplayRotation 应用。
            // Keep the RGB tracking pose physical; apply the existing 180-degree image orientation through DisplayRotation.
            MixedSpaceCaptureProfile profile = new MixedSpaceCaptureProfile(
                paraTexture.width, paraTexture.height, 1, MixedSpacePixelFormat.Luminance8,
                ins, m_CameraPosOnFrameCapture, m_CameraRotOnFrameCapture,
                imagePose.TimestampNanoseconds, Quaternion.Euler(0f, 0f, 180f));
            MixedSpaceCameraCapture capture = new MixedSpaceCameraCapture(
                profile, new MixedSpaceManagedPixelSource(paraTexture.raw));
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult<IMixedSpacePlatformFrame>(new MixedSpacePlatformFrame(tracking, capture));
            }
            catch
            {
                capture.Dispose();
                throw;
            }
        }

        private void OnDestroy()
        {
            _ = ShutdownAsync();
        }

        public MixedSpacePlatformPoseSample SamplePose()
        {
            if (!m_IsRunning || m_CameraTransform == null || !isActiveAndEnabled)
                return new MixedSpacePlatformPoseSample();
#if UNITY_ANDROID && !UNITY_EDITOR
            TrySampleRgbPose(false, out MixedSpacePlatformPoseSample pose);
            return pose;
#else
            return new MixedSpacePlatformPoseSample
            {
                Position = m_CameraTransform.position,
                Rotation = m_CameraTransform.rotation,
                TimestampNanoseconds = GetCurrentTimeStamp13() * 1000000L,
                IsTracking = false
            };
#endif
        }

        private bool TrySampleRgbPose(bool atImageTimestamp, out MixedSpacePlatformPoseSample sample)
        {
            sample = new MixedSpacePlatformPoseSample();
#if UNITY_ANDROID && !UNITY_EDITOR
            if (!m_IsRunning || !isActiveAndEnabled || rgbRecord == null ||
                m_HeadTracking == null || m_XvXRManager == null ||
                !m_HeadTracking.trackPosition || !m_HeadTracking.trackRotation)
                return false;
            if (float.IsNaN(m_RgbPositionOffset.x) || float.IsInfinity(m_RgbPositionOffset.x) ||
                float.IsNaN(m_RgbPositionOffset.y) || float.IsInfinity(m_RgbPositionOffset.y) ||
                float.IsNaN(m_RgbPositionOffset.z) || float.IsInfinity(m_RgbPositionOffset.z))
                return false;
            if (!rgbRecord.TryGetRgbExtrinsics(out Vector3 rgbTranslation, out Quaternion rgbRotation) ||
                !rgbRecord.TryGetHeadPose(atImageTimestamp, out Vector3 headPosition,
                    out Quaternion headRotation, out long timestampNanoseconds))
                return false;

            // 先应用 XvXRHeadTracking 的原点和重居中映射，再应用 RGB 相机到头部的标定。
            // Follow XvXRHeadTracking's origin/recenter mapping, then apply the RGB camera-to-head calibration.
            headRotation = headRotation * m_XvXRManager.ZeroQuaternion;
            Vector3 position = headPosition + m_HeadTracking.positionOrigin + headRotation * rgbTranslation;
            Quaternion rotation = headRotation * rgbRotation;
            // 在 RGB 相机坐标下施加手动修正，不改变设备标定。
            // Apply the manual correction in RGB camera coordinates without changing the device calibration.
            position += rotation * m_RgbPositionOffset;
            Transform parent = m_HeadTracking.transform.parent;
            sample = new MixedSpacePlatformPoseSample
            {
                Position = parent != null ? parent.TransformPoint(position) : position,
                Rotation = parent != null ? parent.rotation * rotation : rotation,
                TimestampNanoseconds = timestampNanoseconds,
                TimestampSource = atImageTimestamp ? MixedSpaceTrackingTimestampSource.Device :
                    MixedSpaceTrackingTimestampSource.DevicePredicted,
                IsTracking = true
            };
            return true;
#else
            return false;
#endif
        }

        private static bool ValidIntrinsics(Vector4 intrinsics)
        {
            for (int i = 0; i < 4; i++)
            {
                if (float.IsNaN(intrinsics[i]) || float.IsInfinity(intrinsics[i]))
                    return false;
            }
            return intrinsics.x > 0f && intrinsics.y > 0f;
        }
    }
}
#endif
