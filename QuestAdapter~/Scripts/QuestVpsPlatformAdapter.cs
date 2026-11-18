using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Meta.XR;
using MixedSpace.XR;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Android;

namespace QuestVPS
{
    [DisallowMultipleComponent]
    public sealed class QuestVpsPlatformAdapter : MonoBehaviour, IMixedSpacePlatformAdapter
    {
        [SerializeField] private OVRCameraRig m_CameraRig;
        [SerializeField] private PassthroughCameraAccess m_CameraAccess;
        [SerializeField, Tooltip("Camera startup/capture settings. If empty, uses the settings serialized on this component.")] private QuestVpsProfile m_Profile;
        private QuestVpsSettings m_Settings;
        // Compatibility fallback for existing scenes without a Profile.
        [SerializeField, HideInInspector, Min(1f)] private float m_StartupTimeoutSeconds = 15f;
        [SerializeField, HideInInspector, Range(0.1f, 2f)] private float m_MaximumFrameAgeSeconds = 0.5f;
        private const string CameraPermission = "horizonos.permission.HEADSET_CAMERA";
        private readonly PixelPool m_Pixels = new PixelPool();
        private CancellationTokenSource m_StartupCancellation;
        private long m_LastCapturedTimestamp;
        private bool m_Running;
        private bool m_Paused;
        private bool m_Focused = true;

        public string Status { get; private set; } = "Camera has not started";
        public QuestVpsProfile Profile => m_Profile;
        private QuestVpsSettings Settings => m_Settings ?? (m_Settings = CreateSettings());
        private QuestVpsSettings CreateSettings() => m_Profile != null ? m_Profile.CreateSettings() :
            new QuestVpsSettings(m_StartupTimeoutSeconds, m_MaximumFrameAgeSeconds);
        public bool IsTracking => m_Running && isActiveAndEnabled && m_CameraRig != null &&
            m_CameraAccess != null && !m_Paused && m_Focused && HasTrackedPose();

        public async Task<bool> StartAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ShutdownAsync();
            m_Settings = CreateSettings();
            if (!isActiveAndEnabled || m_CameraRig == null || m_CameraAccess == null)
            {
                Status = "Assign Camera Rig and Passthrough Camera Access";
                return false;
            }
#if UNITY_ANDROID && !UNITY_EDITOR
            if (!PassthroughCameraAccess.IsSupported)
            {
                Status = "Quest 3/3S and Horizon OS 74+ are required";
                return false;
            }
            var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            m_StartupCancellation = startup;
            bool ready = false;
            try
            {
                Status = "Waiting for headset camera permission";
                if (!await RequestPermissionAsync(startup.Token))
                {
                    Status = "Camera permission denied; grant permission and retry";
                    return false;
                }
                m_CameraAccess.enabled = true;
                Status = "Waiting for camera and positional tracking";
                float deadline = Time.realtimeSinceStartup + Settings.StartupTimeoutSeconds;
                while (Time.realtimeSinceStartup < deadline)
                {
                    startup.Token.ThrowIfCancellationRequested();
                    if (!isActiveAndEnabled || m_CameraRig == null || m_CameraAccess == null ||
                        !m_CameraAccess.enabled) return false;
                    if (m_CameraAccess.IsPlaying && HasTrackedPose() && m_CameraRig.trackingSpace != null)
                    {
                        m_Running = true;
                        ready = true;
                        Status = "Camera ready";
                        return true;
                    }
                    await Task.Delay(50, startup.Token);
                }
                Status = "Camera startup timed out; check permission, tracking and passthrough";
                return false;
            }
            finally
            {
                if (ReferenceEquals(m_StartupCancellation, startup))
                {
                    m_StartupCancellation = null;
                    if (!ready && m_CameraAccess != null) m_CameraAccess.enabled = false;
                }
                startup.Dispose();
            }
#else
            Status = "Build and run on a Quest 3/3S Android headset";
            return false;
#endif
        }

        private async Task<bool> RequestPermissionAsync(CancellationToken cancellationToken)
        {
            if (Permission.HasUserAuthorizedPermission(CameraPermission)) return true;
            var answer = new TaskCompletionSource<bool>();
            var callbacks = new PermissionCallbacks();
            void Granted(string permission) { if (permission == CameraPermission) answer.TrySetResult(true); }
            void Denied(string permission) { if (permission == CameraPermission) answer.TrySetResult(false); }
            callbacks.PermissionGranted += Granted;
            callbacks.PermissionDenied += Denied;
            callbacks.PermissionDeniedAndDontAskAgain += Denied;
            try
            {
                using (cancellationToken.Register(() => answer.TrySetCanceled()))
                {
                    Permission.RequestUserPermission(CameraPermission, callbacks);
                    return await answer.Task;
                }
            }
            finally
            {
                callbacks.PermissionGranted -= Granted;
                callbacks.PermissionDenied -= Denied;
                callbacks.PermissionDeniedAndDontAskAgain -= Denied;
            }
        }

        public Task<IMixedSpacePlatformFrame> CaptureFrameAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IMixedSpacePlatformFrame empty = new MixedSpacePlatformFrame(
                new MixedSpaceTrackingSnapshot(IsTracking ? 1 : 0), null);
            if (!IsTracking || m_CameraRig == null || m_CameraAccess == null || !m_CameraAccess.isActiveAndEnabled ||
                !m_CameraAccess.IsPlaying || m_CameraRig.trackingSpace == null)
                return Task.FromResult(empty);

            long timestamp = QuestVpsCaptureMath.ToUnixNanoseconds(m_CameraAccess.Timestamp);
            long now = QuestVpsCaptureMath.ToUnixNanoseconds(DateTime.UtcNow);
            if (timestamp <= m_LastCapturedTimestamp ||
                !QuestVpsCaptureMath.IsRecentFrame(timestamp, now, Settings.MaximumFrameAgeSeconds))
            {
                Status = "Waiting for a fresh camera frame";
                return Task.FromResult(empty);
            }

            Vector2Int size = m_CameraAccess.CurrentResolution;
            var calibration = m_CameraAccess.Intrinsics;
            if (!QuestVpsCaptureMath.TryGetIntrinsics(calibration.FocalLength, calibration.PrincipalPoint,
                    calibration.SensorResolution, size, out Vector4 intrinsics))
                return Task.FromResult(empty);
            NativeArray<Color32> colors = m_CameraAccess.GetColors();
            if (!colors.IsCreated || (long)size.x * size.y != colors.Length)
                return Task.FromResult(empty);

            // MRUK 81's implementation returns tracking-space coordinates. Apply
            // TrackingSpace exactly once, as for the continuous native head pose.
            Pose imagePose = ToWorldPose(m_CameraAccess.GetCameraPose());
            if (!QuestVpsCaptureMath.ValidPose(imagePose.position, imagePose.rotation))
                return Task.FromResult(empty);
            PooledPixels source = m_Pixels.Rent(colors.Length);
            try
            {
                for (int y = 0; y < size.y; ++y)
                {
                    int sourceRow = (size.y - 1 - y) * size.x;
                    int destinationRow = y * size.x;
                    for (int x = 0; x < size.x; ++x)
                        source.Bytes[destinationRow + x] = QuestVpsCaptureMath.Luminance(colors[sourceRow + x]);
                }
                cancellationToken.ThrowIfCancellationRequested();
                var profile = new MixedSpaceCaptureProfile(size.x, size.y, 1, MixedSpacePixelFormat.Luminance8,
                    intrinsics, imagePose.position, imagePose.rotation, timestamp, QuestVpsCaptureMath.ImageOrientation);
                var capture = new MixedSpaceCameraCapture(profile, source);
                m_LastCapturedTimestamp = timestamp;
                Status = "Camera ready";
                return Task.FromResult<IMixedSpacePlatformFrame>(new MixedSpacePlatformFrame(
                    new MixedSpaceTrackingSnapshot(1), capture));
            }
            catch
            {
                source.Dispose();
                throw;
            }
        }

        public MixedSpacePlatformPoseSample SamplePose()
        {
            if (!IsTracking || m_CameraAccess == null || !m_CameraAccess.IsPlaying ||
                m_CameraRig == null || m_CameraRig.trackingSpace == null)
                return default;
            OVRPose head = OVRPlugin.GetNodePoseStateAtTime(OVRPlugin.GetTimeInSeconds(), OVRPlugin.Node.Head).Pose.ToOVRPose();
            Pose lens = m_CameraAccess.Intrinsics.LensOffset;
            Pose pose = ToWorldPose(new Pose(head.position + head.orientation * lens.position,
                head.orientation * lens.rotation));
            return new MixedSpacePlatformPoseSample
            {
                Position = pose.position,
                Rotation = pose.rotation,
                TimestampNanoseconds = QuestVpsCaptureMath.ToUnixNanoseconds(DateTime.UtcNow),
                TimestampSource = MixedSpaceTrackingTimestampSource.HostUtc,
                IsTracking = QuestVpsCaptureMath.ValidPose(pose.position, pose.rotation)
            };
        }

        private Pose ToWorldPose(Pose pose)
        {
            Transform space = m_CameraRig.trackingSpace;
            return new Pose(space.TransformPoint(pose.position), space.rotation * pose.rotation);
        }

        private static bool HasTrackedPose()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            return OVRPlugin.initialized && OVRPlugin.GetNodePositionTracked(OVRPlugin.Node.Head) &&
                OVRPlugin.GetNodeOrientationTracked(OVRPlugin.Node.Head);
#else
            return false;
#endif
        }

        public Task ShutdownAsync()
        {
            m_Running = false;
            m_StartupCancellation?.Cancel();
            m_StartupCancellation = null;
            m_LastCapturedTimestamp = 0;
            if (m_CameraAccess != null) m_CameraAccess.enabled = false;
            m_Pixels.Clear();
            return Task.CompletedTask;
        }

        private void OnApplicationPause(bool paused) { m_Paused = paused; }
        private void OnApplicationFocus(bool focused) { m_Focused = focused; }
        private void OnDisable() { _ = ShutdownAsync(); }
        private void OnDestroy() { _ = ShutdownAsync(); }

        // A capture keeps its own pinned buffer until the SDK releases the last
        // pixel lease. Shutdown never frees a buffer that a native solve still uses.
        internal sealed class PixelPool
        {
            private readonly object m_Gate = new object();
            private readonly Stack<byte[]> m_Available = new Stack<byte[]>(2);
            private int m_Generation;
            public PooledPixels Rent(int length)
            {
                lock (m_Gate)
                {
                    byte[] bytes = m_Available.Count > 0 ? m_Available.Pop() : null;
                    if (bytes == null || bytes.Length != length) bytes = new byte[length];
                    return new PooledPixels(bytes, this, m_Generation);
                }
            }
            public void Return(byte[] bytes, int generation)
            {
                lock (m_Gate)
                    if (generation == m_Generation && m_Available.Count < 2) m_Available.Push(bytes);
            }
            public void Clear()
            {
                lock (m_Gate) { ++m_Generation; m_Available.Clear(); }
            }
        }

        internal sealed class PooledPixels : IMixedSpacePixelSource
        {
            private GCHandle m_Handle;
            private readonly PixelPool m_Pool;
            private readonly int m_Generation;
            public byte[] Bytes { get; private set; }
            public PooledPixels(byte[] bytes, PixelPool pool, int generation)
            {
                Bytes = bytes;
                m_Pool = pool;
                m_Generation = generation;
                m_Handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            }
            public IntPtr Pointer => m_Handle.IsAllocated ? m_Handle.AddrOfPinnedObject() :
                throw new ObjectDisposedException(nameof(PooledPixels));
            public byte[] CopyPixels() => Bytes != null ? (byte[])Bytes.Clone() :
                throw new ObjectDisposedException(nameof(PooledPixels));
            public void Dispose()
            {
                byte[] bytes = Bytes;
                if (bytes == null) return;
                Bytes = null;
                if (m_Handle.IsAllocated) m_Handle.Free();
                m_Pool.Return(bytes, m_Generation);
            }
        }
    }
}
