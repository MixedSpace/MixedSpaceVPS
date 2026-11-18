using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.XR;

namespace MixedSpace.XR
{
    /// <summary>采集 Camera 0 图像和 OpenXR 位姿。</summary>
    [DisallowMultipleComponent]
    public sealed class MixedSpaceXrealAdapter : MonoBehaviour, IMixedSpacePlatformAdapter
    {
        public enum HeadPoseOrigin { CenterEye, Device, RenderedCamera }
        [SerializeField] private string m_DeviceName = "Camera 0";
        [SerializeField] private int m_RequestedWidth = 1920;
        [SerializeField] private int m_RequestedHeight = 1080;
        [SerializeField] private int m_RequestedFps = 30;
        [SerializeField] private Transform m_TrackingSpace;
        [SerializeField, Tooltip("位姿采样来源。")]
        private HeadPoseOrigin m_PoseOrigin = HeadPoseOrigin.CenterEye;
        [SerializeField, Tooltip("图像坐标系绕镜头轴的旋转角度。")]
        private float m_ImageBasisRollDegrees = 180f;
        public enum NativeImageDirection { Degrees0 = 0, Degrees90 = 90, Degrees180 = 180, Degrees270 = 270 }
        [SerializeField, Tooltip("原生定位解算器的图像方向。")]
        private NativeImageDirection m_NativeImageDirection = NativeImageDirection.Degrees90;
        [SerializeField, Tooltip("内参与实际输出匹配时启用。")]
        private bool m_CalibrationConfirmed = true;
        [SerializeField, Tooltip("1920×1080 内参：fx、fy、cx、cy，单位为像素。")]
        private Vector4 m_Intrinsics = new Vector4(1280f, 1280f, 961.33f, 536f);
        [SerializeField] private int m_CalibrationWidth = 1920;
        [SerializeField] private int m_CalibrationHeight = 1080;
        [SerializeField, Tooltip("镜头相对跟踪原点的位置，单位为米。")]
        private Vector3 m_LensPositionInHead;
        [SerializeField, Tooltip("镜头相对跟踪原点的欧拉角，单位为度。")]
        private Vector3 m_LensRotationInHead;
        [SerializeField, Tooltip("镜头外参已标定时启用。")]
        private bool m_ExtrinsicsConfirmed;
        [SerializeField, Tooltip("输入图像为针孔模型时启用。")]
        private bool m_PinholeImageConfirmed;
        [SerializeField, Tooltip("允许使用读图时刻的近似位姿。")]
        private bool m_AllowApproximateCapturePose;
        [SerializeField, Tooltip("针孔畸变系数：k1、k2、p1、p2、k3；无畸变时留空。")]
        private float[] m_LensDistortion = new float[0];
        private bool CanLocalize => m_AllowApproximateCapturePose &&
            m_CalibrationConfirmed && m_ExtrinsicsConfirmed && (m_PinholeImageConfirmed || HasDistortion);
        private bool HasDistortion => m_LensDistortion != null && m_LensDistortion.Length == 5;
        private readonly MixedSpaceXrealPinholeRectifier m_Rectifier = new MixedSpaceXrealPinholeRectifier();
        private bool m_LocalizationCaptureEnabled = true;
        public void SetLocalizationCaptureEnabled(bool enabled)
        {
            m_LocalizationCaptureEnabled = enabled;
        }
        private WebCamTexture m_Texture;
        private Color32[] m_Colors;
        private bool m_Paused;
        private bool m_RestartPreviewOnResume;
        private bool m_Starting;
        private int m_Epoch;
        private long m_FrameNumber;
        private long m_ConsumedFrame;
        private float m_LastFrameTime;
        private Camera m_RenderedCamera;

        public string Status { get; private set; } = "Not started";
        public WebCamTexture Preview => m_Texture;
        public long FrameNumber => m_FrameNumber;
        public string DeviceName { get => m_DeviceName; set => m_DeviceName = value; }
        public bool HasFreshImage => m_Texture != null && m_Texture.isPlaying && m_FrameNumber > 0 &&
            !m_Paused && Time.realtimeSinceStartup - m_LastFrameTime < 0.5f;
        private static long Now => (long)(Stopwatch.GetTimestamp() * (1000000000.0 / Stopwatch.Frequency));

        /// <summary>启动相机预览。</summary>
        public async Task<bool> StartPreviewAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            while (m_Starting)
            {
                token.ThrowIfCancellationRequested();
                await Task.Delay(50, token);
            }
            if (HasFreshImage) return true;
            StopCamera();
            int epoch = m_Epoch;
            m_Starting = true;
            bool ready = false;
            try
            {
#if UNITY_ANDROID && !UNITY_EDITOR
                if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(UnityEngine.Android.Permission.Camera))
                {
                    UnityEngine.Android.Permission.RequestUserPermission(UnityEngine.Android.Permission.Camera);
                    float deadline = Time.realtimeSinceStartup + 30f;
                    while (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(UnityEngine.Android.Permission.Camera) || m_Paused)
                    {
                        token.ThrowIfCancellationRequested();
                        if (epoch != m_Epoch || Time.realtimeSinceStartup > deadline) { Status = "Camera permission unavailable"; return false; }
                        await Task.Delay(100, token);
                    }
                }
#else
                Status = "Build and run on Aura; editor camera is not a substitute";
                return false;
#endif
                if (epoch != m_Epoch || !isActiveAndEnabled || m_Paused) return false;
                var devices = WebCamTexture.devices;
                string[] names = Array.ConvertAll(devices, d => d.name);
                // 匹配指定相机名称。
                string selectedName = m_DeviceName == "0" && !Array.Exists(devices, d => d.name == "0")
                    ? "Camera 0" : m_DeviceName;
                if (!Array.Exists(devices, d => d.name == selectedName))
                {
                    Status = "Selected camera not found: " + m_DeviceName + "; available: " + string.Join(", ", names);
                    return false;
                }
                m_Texture = new WebCamTexture(selectedName, m_RequestedWidth, m_RequestedHeight, m_RequestedFps);
                m_Texture.Play();
                Status = "Waiting for camera frames";
                float timeout = Time.realtimeSinceStartup + 15f;
                while (!HasFreshImage)
                {
                    token.ThrowIfCancellationRequested();
                    if (epoch != m_Epoch || !isActiveAndEnabled || m_Paused) return false;
                    if (Time.realtimeSinceStartup > timeout) { Status = "No frames; keep Aura awake and check camera access"; return false; }
                    await Task.Delay(50, token);
                }
                ready = true;
                Status = "Main camera preview ready (calibration required for VPS)";
                return true;
            }
            catch (Exception e) { Status = e.Message; throw; }
            finally { m_Starting = false; if (!ready && epoch == m_Epoch) StopCamera(); }
        }

        public async Task<bool> StartAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool valid = ValidConfiguration(out string validation);
            if (!CanLocalize || !valid)
            {
                // 检查定位采集配置。
                Status = "VPS blocked: " + (validation ?? "confirm intrinsics, head-to-camera extrinsics, pinhole model and approximate timing consent");
                UnityEngine.Debug.LogWarning("[AuraVPS] " + Status);
                return false;
            }
            bool ready = await StartPreviewAsync(cancellationToken);
            if (ready && (m_Texture.width != m_CalibrationWidth || m_Texture.height != m_CalibrationHeight))
            {
                Status = "VPS blocked: actual camera resolution does not match calibration";
                await ShutdownAsync();
                return false;
            }
            return ready;
        }

        private bool ValidConfiguration(out string error)
        {
            error = null;
            try { MixedSpaceXrealPinholeRectifier.Validate(m_CalibrationWidth, m_CalibrationHeight, m_Intrinsics, m_LensDistortion); }
            catch (ArgumentException e) { error = e.Message; return false; }
            if (m_TrackingSpace == null) { error = "assign the Main Camera's tracking-space parent"; return false; }
            if (!Finite(m_ImageBasisRollDegrees) || !Finite(m_LensPositionInHead.x) || !Finite(m_LensPositionInHead.y) || !Finite(m_LensPositionInHead.z) ||
                !Finite(m_LensRotationInHead.x) || !Finite(m_LensRotationInHead.y) || !Finite(m_LensRotationInHead.z))
            { error = "head-to-camera extrinsics and image rotation must be finite"; return false; }
            return true;
        }
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        private void Update()
        {
            if (m_Texture == null || !m_Texture.isPlaying || !m_Texture.didUpdateThisFrame || m_Texture.width <= 16) return;
            m_LastFrameTime = Time.realtimeSinceStartup;
            ++m_FrameNumber;
        }

        /// <summary>采样实时镜头位姿。</summary>
        public MixedSpacePlatformPoseSample SamplePose()
        {
            var head = InputDevices.GetDeviceAtXRNode(XRNode.Head);
            bool tracked = !m_Paused && head.isValid &&
                head.TryGetFeatureValue(CommonUsages.isTracked, out bool isTracked) && isTracked;
            tracked &= head.TryGetFeatureValue(m_PoseOrigin == HeadPoseOrigin.Device ? CommonUsages.devicePosition : CommonUsages.centerEyePosition, out Vector3 position);
            tracked &= head.TryGetFeatureValue(m_PoseOrigin == HeadPoseOrigin.Device ? CommonUsages.deviceRotation : CommonUsages.centerEyeRotation, out Quaternion rotation);
            if (head.TryGetFeatureValue(CommonUsages.trackingState, out InputTrackingState trackingState))
                tracked &= (trackingState & (InputTrackingState.Position | InputTrackingState.Rotation)) == (InputTrackingState.Position | InputTrackingState.Rotation);
            if (!tracked || !Finite(position.x) || !Finite(position.y) || !Finite(position.z) ||
                !Finite(rotation.x) || !Finite(rotation.y) || !Finite(rotation.z) || !Finite(rotation.w) ||
                Quaternion.Dot(rotation, rotation) < 1e-8f)
                return new MixedSpacePlatformPoseSample { TimestampNanoseconds = Now };
            rotation = Quaternion.Normalize(rotation);
            if (m_PoseOrigin == HeadPoseOrigin.RenderedCamera)
            {
                // 读取渲染相机的世界位姿。
                if (m_RenderedCamera == null) m_RenderedCamera = Camera.main;
                if (m_RenderedCamera == null || !m_RenderedCamera.isActiveAndEnabled)
                    return new MixedSpacePlatformPoseSample { TimestampNanoseconds = Now };
                position = m_RenderedCamera.transform.position;
                rotation = m_RenderedCamera.transform.rotation;
                if (!Finite(position.x) || !Finite(position.y) || !Finite(position.z) ||
                    !Finite(rotation.x) || !Finite(rotation.y) || !Finite(rotation.z) || !Finite(rotation.w) || Quaternion.Dot(rotation, rotation) < 1e-8f)
                    return new MixedSpacePlatformPoseSample { TimestampNanoseconds = Now };
                rotation = Quaternion.Normalize(rotation);
                position += rotation * m_LensPositionInHead;
                rotation *= Quaternion.Euler(m_LensRotationInHead);
                return new MixedSpacePlatformPoseSample { Position = position, Rotation = rotation,
                    TimestampNanoseconds = Now, IsTracking = true };
            }
            position += rotation * m_LensPositionInHead;
            rotation *= Quaternion.Euler(m_LensRotationInHead);
            if (m_TrackingSpace != null)
            {
                position = m_TrackingSpace.TransformPoint(position);
                rotation = m_TrackingSpace.rotation * rotation;
            }
            return new MixedSpacePlatformPoseSample { Position = position, Rotation = rotation,
                TimestampNanoseconds = Now, IsTracking = true };
        }

        /// <summary>采集灰度图像及其位姿。</summary>
        public Task<IMixedSpacePlatformFrame> CaptureFrameAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IMixedSpacePlatformFrame empty = new MixedSpacePlatformFrame(new MixedSpaceTrackingSnapshot(SamplePose().IsTracking ? 1 : 0), null);
            if (!m_LocalizationCaptureEnabled || !CanLocalize || !HasFreshImage || !ValidConfiguration(out _) ||
                m_ConsumedFrame == m_FrameNumber ||
                m_Texture.width != m_CalibrationWidth || m_Texture.height != m_CalibrationHeight)
                return Task.FromResult(empty);
            // 使用读图前后位姿的中点值。
            var before = SamplePose();
            if (!before.IsTracking) return Task.FromResult(empty);
            int width = m_Texture.width, height = m_Texture.height;
            int rotationAngle = m_Texture.videoRotationAngle;
            bool mirrored = m_Texture.videoVerticallyMirrored;
            long frameNumber = m_FrameNumber;
            Vector4 intrinsics = m_Intrinsics;
            byte[] pixels = ReadLuminance(mirrored);
            var after = SamplePose();
            if (!after.IsTracking || width != m_Texture.width || height != m_Texture.height ||
                rotationAngle != m_Texture.videoRotationAngle || mirrored != m_Texture.videoVerticallyMirrored)
                return Task.FromResult(empty);
            long timestamp = before.TimestampNanoseconds + (after.TimestampNanoseconds - before.TimestampNanoseconds) / 2;
            Vector3 position = Vector3.Lerp(before.Position, after.Position, 0.5f);
            Quaternion rotation = Quaternion.Slerp(before.Rotation, after.Rotation, 0.5f);
            if (HasDistortion) pixels = m_Rectifier.Rectify(pixels, width, height, intrinsics, m_LensDistortion);
            cancellationToken.ThrowIfCancellationRequested();
            var profile = new MixedSpaceCaptureProfile(width, height, 1,
                MixedSpacePixelFormat.Luminance8, intrinsics, position, rotation,
                timestamp, Quaternion.Euler(0, 0, m_ImageBasisRollDegrees - rotationAngle),
                nativeImageRotationAngle: (int)m_NativeImageDirection);
            var capture = new MixedSpaceCameraCapture(profile, new MixedSpaceManagedPixelSource(pixels));
            m_ConsumedFrame = frameNumber;
            return Task.FromResult<IMixedSpacePlatformFrame>(new MixedSpacePlatformFrame(new MixedSpaceTrackingSnapshot(1), capture));
        }

        // 将灰度图像行顺序统一为左上角原点。
        private byte[] ReadLuminance(bool mirrored)
        {
            int width = m_Texture.width, height = m_Texture.height;
            m_Colors = m_Texture.GetPixels32(m_Colors);
            byte[] bytes = new byte[checked(width * height)];
            for (int y = 0; y < height; ++y)
            {
                int sourceRow = (mirrored ? y : height - 1 - y) * width;
                for (int x = 0; x < width; ++x)
                {
                    Color32 c = m_Colors[sourceRow + x];
                    bytes[y * width + x] = (byte)((77 * c.r + 150 * c.g + 29 * c.b + 128) >> 8);
                }
            }
            return bytes;
        }

        private void StopCamera()
        {
            ++m_Epoch;
            if (m_Texture != null) { m_Texture.Stop(); Destroy(m_Texture); m_Texture = null; }
            m_Colors = null; m_FrameNumber = 0; m_ConsumedFrame = 0;
        }
        public Task ShutdownAsync() { StopCamera(); return Task.CompletedTask; }
        private void OnApplicationPause(bool paused)
        {
            m_Paused = paused;
            // 恢复时重新启动已暂停的相机。
            if (paused && m_Texture != null)
            {
                m_RestartPreviewOnResume = true;
                StopCamera();
            }
            else if (!paused && m_RestartPreviewOnResume && isActiveAndEnabled)
                ResumePreview();
        }
        private async void ResumePreview()
        {
            m_RestartPreviewOnResume = false;
            try
            {
                bool ready = await StartPreviewAsync(CancellationToken.None);
            }
            catch (Exception e) { UnityEngine.Debug.LogWarning("[AuraVPS] Camera resume failed: " + e.Message); }
        }
        private void OnDisable() { m_RestartPreviewOnResume = false; StopCamera(); }
        private void OnDestroy() => StopCamera();
    }
}
