using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using HuaweiARUnitySDK;
using UnityEngine;
using ARSession = HuaweiARUnitySDK.ARSession;

namespace MixedSpace.XR.Huawei
{
    /// <summary>
    /// MixedSpace 2.x 的 Huawei AR Engine 相机与跟踪桥接。
    /// Huawei AR Engine camera and tracking bridge for MixedSpace 2.x.
    /// </summary>
    [DefaultExecutionOrder(-1000)]
    [AddComponentMenu("MixedSpace/Platforms/Huawei AR Engine Adapter")]
    public sealed class MixedSpaceHuaweiAdapter : MonoBehaviour, IMixedSpacePlatformAdapter
    {
        public enum CaptureResolution { Default, Max }

        [SerializeField] private MixedSpaceHuaweiProfile m_Profile;
        // 未指定 Profile 时使用此组件上的配置。
        // Use this component's settings when no Profile is assigned.
        [SerializeField] private ARConfigBase m_Config;
        [SerializeField] private CaptureResolution m_Resolution = CaptureResolution.Max;
        [SerializeField] private MixedSpacePixelFormat m_Format = MixedSpacePixelFormat.Luminance8;
        [SerializeField, Min(1)] private int m_StartupTimeoutSeconds = 15;

        private static MixedSpaceHuaweiAdapter s_Owner;
        private Transform m_Camera;
        private bool m_Ready;
        private bool m_SessionCreated;
        private bool m_Paused;
        private int m_BackgroundVersion;
        private bool m_InstallRequested;
        private CaptureResolution? m_ResolutionOverride;
        private Task<bool> m_Starting;
        private readonly MixedSpaceCameraDisplayOrientation m_Display = new MixedSpaceCameraDisplayOrientation();
        private Vector4? m_CachedImageIntrinsics;
        private int m_CachedImageWidth;
        private int m_CachedImageHeight;
        private ScreenOrientation m_DisplayGeometryOrientation;
        private int m_DisplayGeometryWidth;
        private int m_DisplayGeometryHeight;

        public MixedSpaceHuaweiProfile Profile => m_Profile;
        private ARConfigBase Config => m_Profile != null ? m_Profile.Config : m_Config;
        private CaptureResolution CaptureSize => m_ResolutionOverride ??
            (m_Profile != null ? m_Profile.Resolution : m_Resolution);
        private MixedSpacePixelFormat Format => m_Profile != null ? m_Profile.Format : m_Format;
        private int StartupTimeoutSeconds => m_Profile != null ? m_Profile.StartupTimeoutSeconds : m_StartupTimeoutSeconds;
        public CaptureResolution Resolution
        {
            get => CaptureSize;
            set
            {
                if (!Enum.IsDefined(typeof(CaptureResolution), value)) throw new ArgumentOutOfRangeException(nameof(value));
                m_ResolutionOverride = value;
            }
        }
        public void UseDisplayOrientation(ScreenOrientation value) => m_Display.Use(value);
        public void FollowDisplayOrientation() => m_Display.FollowDevice();

        public Task<bool> StartAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (m_Ready) return Task.FromResult(true);
            if (m_Starting != null && !m_Starting.IsCompleted) return m_Starting;
            return m_Starting = StartCoreAsync(token);
        }

        private async Task<bool> StartCoreAsync(CancellationToken token)
        {
#if !UNITY_ANDROID || UNITY_EDITOR
            throw new PlatformNotSupportedException("Huawei AR Engine requires an Android device.");
#else
            m_Profile?.Validate();
            if (!isActiveAndEnabled || Config == null || Camera.main == null)
                throw new InvalidOperationException("Enable the Huawei adapter, assign its profile/config, and provide a MainCamera.");
            if (s_Owner != null && s_Owner != this)
                throw new InvalidOperationException("Another Huawei adapter owns the AR Engine session.");
            s_Owner = this;
            m_Camera = Camera.main.transform;
            try
            {
                if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(UnityEngine.Android.Permission.Camera))
                {
                    var grant = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    var callbacks = new UnityEngine.Android.PermissionCallbacks();
                    callbacks.PermissionGranted += _ => grant.TrySetResult(true);
                    callbacks.PermissionDenied += _ => grant.TrySetResult(false);
                    callbacks.PermissionDeniedAndDontAskAgain += _ => grant.TrySetResult(false);
                    UnityEngine.Android.Permission.RequestUserPermission(UnityEngine.Android.Permission.Camera, callbacks);
                    using (token.Register(() => grant.TrySetCanceled()))
                        if (!await grant.Task) throw new UnauthorizedAccessException("Camera permission was denied.");
                }

                // 权限获准不代表权限窗口已关闭。Unity 仍暂停时启动相机可能使本次启动失败；等待前台恢复，且不消耗相机超时预算。
                // A granted permission does not imply the permission activity has closed. Starting the native camera while Unity
                // is still paused can fail permanently for this startup attempt. Wait without consuming the camera timeout.
                await HuaweiStartupForegroundGate.WaitAsync(
                    () => !m_Paused && Application.isFocused, token);
                token.ThrowIfCancellationRequested();

                float installDeadline = Time.realtimeSinceStartup + 120f;
                while (AREnginesApk.Instance.RequestInstall(!m_InstallRequested) == ARInstallStatus.INSTALL_REQUESTED)
                {
                    m_InstallRequested = true;
                    token.ThrowIfCancellationRequested();
                    if (Time.realtimeSinceStartup >= installDeadline)
                        throw new TimeoutException("Huawei AR Engine installation did not complete.");
                    await Task.Delay(500, token);
                }

                // 安装 AR Engine 也可能让应用离开前台。
                // AR Engine installation can also take the application out of the foreground.
                await HuaweiStartupForegroundGate.WaitAsync(
                    () => !m_Paused && Application.isFocused, token);
                token.ThrowIfCancellationRequested();

                ARSession.CreateSession();
                m_SessionCreated = true;
                // 相机或 SLAM 更新迟到时，保持 Unity 渲染响应。
                // Keep Unity rendering responsive when the camera or SLAM result is late.
                Config.UpdateMode = ARConfigUpdateMode.LATEST_CAMERA_IMAGE;
                ARSession.Config(Config);
                ARSession.Resume();
                ARSession.SetCameraTextureNameAuto();
                UpdateDisplayGeometry();

                float remaining = StartupTimeoutSeconds;
                float lastProbe = Time.realtimeSinceStartup;
                int lastBackgroundVersion = m_BackgroundVersion;
                while (m_Paused || !Application.isFocused || !ARFrame.TextureIsAvailable())
                {
                    token.ThrowIfCancellationRequested();
                    float now = Time.realtimeSinceStartup;
                    // 暂停期间 Unity 可能完全不更新；通过生命周期版本变化，避免恢复时把整个暂停时长计入超时。
                    // Unity may stop ticking entirely while paused. A lifecycle version change prevents charging that whole
                    // suspended interval on resume.
                    if (!m_Paused && Application.isFocused && lastBackgroundVersion == m_BackgroundVersion)
                        remaining -= now - lastProbe;
                    lastProbe = now;
                    lastBackgroundVersion = m_BackgroundVersion;
                    if (remaining <= 0f)
                        throw new TimeoutException("Huawei AR Engine camera did not become ready.");
                    await Task.Yield();
                }
                m_Ready = true;
                return true;
            }
            catch
            {
                await ShutdownAsync();
                throw;
            }
#endif
        }

        private void Update()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (!m_SessionCreated || m_Paused) return;

            AsyncTask.Update();
            UpdateDisplayGeometry();

            long updateStart = Stopwatch.GetTimestamp();
            ARSession.Update();

            double updateMs = (Stopwatch.GetTimestamp() - updateStart) * 1000.0 / Stopwatch.Frequency;
            if (updateMs > 100.0) MixedSpaceLogger.LogWarning($"[MixedSpace Huawei] ARSession.Update took {updateMs:F0} ms");
#endif
        }

        private void UpdateDisplayGeometry()
        {
            ScreenOrientation orientation = Screen.orientation;
            int width = Screen.width;
            int height = Screen.height;
            if (width <= 0 || height <= 0 ||
                (orientation != ScreenOrientation.Portrait &&
                 orientation != ScreenOrientation.LandscapeLeft &&
                 orientation != ScreenOrientation.PortraitUpsideDown &&
                 orientation != ScreenOrientation.LandscapeRight))
                return;
            if (orientation == m_DisplayGeometryOrientation &&
                width == m_DisplayGeometryWidth && height == m_DisplayGeometryHeight)
                return;

            ARSession.SetDisplayGeometry(width, height);
            m_DisplayGeometryOrientation = orientation;
            m_DisplayGeometryWidth = width;
            m_DisplayGeometryHeight = height;
            m_CachedImageIntrinsics = null;
        }

        private void OnApplicationPause(bool paused)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            m_Paused = paused;
            if (paused) ++m_BackgroundVersion;
            if (!m_SessionCreated) return;
            if (paused) ARSession.Pause();
            else ARSession.Resume();
#endif
        }

        private void OnApplicationFocus(bool focused)
        {
            if (!focused) ++m_BackgroundVersion;
        }

        public Task<IMixedSpacePlatformFrame> CaptureFrameAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (!m_Ready || m_Paused || !ARFrame.TextureIsAvailable())
                return Task.FromResult<IMixedSpacePlatformFrame>(Empty());

            var pose = SamplePose();

            bool tracking = pose.IsTracking;
            ARCameraImageBytes image = null;
            try
            {
                bool preview = CaptureSize == CaptureResolution.Max;
                if (preview)
                {
                    try { image = ARFrame.AcquirePreviewImageBytes(); }
                    catch (SystemException) { preview = false; }
                }
                if (image == null) image = ARFrame.AcquireCameraImageBytes();

                if (image == null || !image.IsAvailable || image.Width <= 0 || image.Height <= 0)
                    return Task.FromResult<IMixedSpacePlatformFrame>(new MixedSpacePlatformFrame(new MixedSpaceTrackingSnapshot(tracking ? 1 : 0), null));

                var intrinsics = GetIntrinsics(preview, image.Width, image.Height);
                byte[] pixels = Format == MixedSpacePixelFormat.Luminance8
                    ? CopyLuminance(image) : CopyRgb(image);

                float displayAngle = m_Display.Angle(Screen.orientation);
                var profile = new MixedSpaceCaptureProfile(image.Width, image.Height,
                    Format == MixedSpacePixelFormat.Luminance8 ? 1 : 3, Format,
                    intrinsics, pose.Position, pose.Rotation, pose.TimestampNanoseconds,
                    Quaternion.Euler(0f, 0f, displayAngle),
                    nativeImageRotationAngle: LocalizationImageRotationAngle(displayAngle));
                token.ThrowIfCancellationRequested();
                var capture = new MixedSpaceCameraCapture(profile, new PinnedPixels(pixels));
                return Task.FromResult<IMixedSpacePlatformFrame>(new MixedSpacePlatformFrame(
                    new MixedSpaceTrackingSnapshot(tracking ? 1 : 0), capture));
            }
            finally { image?.Dispose(); }
        }

        /// <summary>定位图像方向：竖屏 0°、左横屏 90°、竖屏反转 180°、右横屏 270°。</summary>
        private static int LocalizationImageRotationAngle(float displayAngle)
        {
            return ((Mathf.RoundToInt(displayAngle) - 90) % 360 + 360) % 360;
        }

        private Vector4 GetIntrinsics(bool preview, int width, int height)
        {
            if (preview)
            {
                if (m_CachedImageIntrinsics.HasValue &&
                    m_CachedImageWidth == width && m_CachedImageHeight == height)
                    return m_CachedImageIntrinsics.Value;

                try
                {
                    using (ARCamera camera = ARFrame.GetCamera())
                    {
                        ARCameraIntrinsics intr = camera.GetImageIntrinsics();
                        // 保留活动内参句柄。
                        GC.SuppressFinalize(intr);
                        Vector2 focal = intr.GetFocalLength();
                        Vector2 principal = intr.GetPrincipalPoint();

                        if (focal.x > 0f && focal.y > 0f &&
                            !float.IsNaN(focal.x) && !float.IsNaN(focal.y))
                        {
                            m_CachedImageWidth = width;
                            m_CachedImageHeight = height;
                            m_CachedImageIntrinsics = ToImageIntrinsics(
                                new Vector4(focal.x, focal.y, principal.x, principal.y),
                                width, height, m_DisplayGeometryOrientation);
                            return m_CachedImageIntrinsics.Value;
                        }
                    }
                }
                catch (SystemException) { }
            }

            Camera cameraUnity = Camera.main;
            Matrix4x4 projection = ARSession.GetProjectionMatrix(cameraUnity.nearClipPlane, cameraUnity.farClipPlane);
            float fy = 0.5f * projection.m11 * width;
            float cx = 0.5f * (projection.m02 + 1f) * height;
            float cy = 0.5f * (projection.m12 + 1f) * width;
            return ToImageIntrinsics(new Vector4(fy, fy, cx, cy),
                width, height, m_DisplayGeometryOrientation);
        }

        /// <summary>
        /// 将显示方向下的华为内参还原到 CPU 图像坐标，焦距轴和主点同步转换。
        /// Converts Huawei display-oriented intrinsics to CPU image coordinates, including focal axes and principal point.
        /// </summary>
        private static Vector4 ToImageIntrinsics(Vector4 intrinsics, int width, int height,
            ScreenOrientation orientation)
        {
            switch (orientation)
            {
                case ScreenOrientation.Portrait:
                    return new Vector4(intrinsics.y, intrinsics.x, intrinsics.w, height - intrinsics.z);
                case ScreenOrientation.PortraitUpsideDown:
                    return new Vector4(intrinsics.y, intrinsics.x, width - intrinsics.w, intrinsics.z);
                case ScreenOrientation.LandscapeRight:
                    return new Vector4(intrinsics.x, intrinsics.y, width - intrinsics.z, height - intrinsics.w);
                default:
                    return intrinsics;
            }
        }
        private static byte[] CopyLuminance(ARCameraImageBytes image)
        {
            byte[] data = new byte[checked(image.Width * image.Height)];
            for (int y = 0; y < image.Height; y++)
                Marshal.Copy(IntPtr.Add(image.Y, y * image.YRowStride), data, y * image.Width, image.Width);
            return data;
        }

        private static byte[] CopyRgb(ARCameraImageBytes image)
        {
            byte[] data = new byte[checked(image.Width * image.Height * 3)];
            for (int y = 0; y < image.Height; y++)
            for (int x = 0; x < image.Width; x++)
            {
                int uv = (y / 2) * image.UVRowStride + (x / 2) * image.UVPixelStride;
                int yy = Marshal.ReadByte(image.Y, y * image.YRowStride + x);
                int u = Marshal.ReadByte(image.U, uv) - 128;
                int v = Marshal.ReadByte(image.V, uv) - 128;
                int c = Math.Max(0, yy - 16);
                int offset = (y * image.Width + x) * 3;
                data[offset] = Clamp((298 * c + 409 * v + 128) >> 8);
                data[offset + 1] = Clamp((298 * c - 100 * u - 208 * v + 128) >> 8);
                data[offset + 2] = Clamp((298 * c + 516 * u + 128) >> 8);
            }
            return data;
        }

        private static byte Clamp(int value) => (byte)Math.Max(0, Math.Min(255, value));
        public MixedSpacePlatformPoseSample SamplePose()
        {
            Vector3 position = m_Camera != null ? m_Camera.position : Vector3.zero;
            Quaternion rotation = m_Camera != null ? m_Camera.rotation : Quaternion.identity;
            bool tracking = false;
            if (m_Ready && !m_Paused && ARFrame.TextureIsAvailable())
            {
                using (ARCamera camera = ARFrame.GetCamera())
                {
                    Pose pose = camera.GetPose();
                    position = pose.position;
                    rotation = pose.rotation;
                    tracking = camera.GetTrackingState() == ARTrackable.TrackingState.TRACKING;
                }
            }
            return new MixedSpacePlatformPoseSample
            {
                Position = position,
                Rotation = rotation,
                TimestampNanoseconds = (long)(Stopwatch.GetTimestamp() * (1000000000.0 / Stopwatch.Frequency)),
                TimestampSource = MixedSpaceTrackingTimestampSource.HostMonotonic,
                IsTracking = tracking
            };
        }

        private static MixedSpacePlatformFrame Empty() =>
            new MixedSpacePlatformFrame(new MixedSpaceTrackingSnapshot(0), null);

        public Task ShutdownAsync()
        {
            m_Ready = false;
            m_CachedImageIntrinsics = null;
            m_DisplayGeometryWidth = 0;
            m_DisplayGeometryHeight = 0;
            if (m_SessionCreated)
            {
                ARSession.Stop();
                m_SessionCreated = false;
            }
            m_Camera = null;
            if (s_Owner == this) s_Owner = null;
            return Task.CompletedTask;
        }

        private void OnDestroy() { _ = ShutdownAsync(); }

        private sealed class PinnedPixels : IMixedSpacePixelSource
        {
            private byte[] m_Data;
            private GCHandle m_Handle;
            public PinnedPixels(byte[] data)
            {
                m_Data = data ?? throw new ArgumentNullException(nameof(data));
                m_Handle = GCHandle.Alloc(data, GCHandleType.Pinned);
            }
            public IntPtr Pointer => m_Handle.IsAllocated ? m_Handle.AddrOfPinnedObject() :
                throw new ObjectDisposedException(nameof(PinnedPixels));
            public byte[] CopyPixels() => m_Data != null ? (byte[])m_Data.Clone() :
                throw new ObjectDisposedException(nameof(PinnedPixels));
            public void Dispose()
            {
                if (m_Handle.IsAllocated) m_Handle.Free();
                m_Data = null;
            }
        }
    }
}
