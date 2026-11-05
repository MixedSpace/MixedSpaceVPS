using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace MixedSpace.XR
{
    /// <summary>
    /// Unity 平台入口将配置连接到相机/位姿能力并转发生命周期；采集顺序和就绪状态为普通 C# 状态机，无 AR 设备也可独立测试。
    /// Unity platform entry: binds authoring settings to camera/pose capabilities
    /// and forwards lifecycle calls. Capture ordering and readiness are ordinary
    /// C# state machines, independently testable without an AR device.
    /// </summary>
    public class MixedSpaceARFoundationAdapter : MonoBehaviour, IMixedSpacePlatformAdapter, IMixedSpaceCaptureDiagnostics
    {
        [SerializeField, Tooltip("必需的平台配置；运行时修改使用私有快照。\nRequired platform configuration; runtime changes use a private snapshot.")]
        private MixedSpaceARFoundationProfile m_Profile;
        private MixedSpaceARFoundationSettings m_Settings;
        private readonly ARFoundationCameraDevice m_Device = new ARFoundationCameraDevice();
        private readonly ARFoundationPoseSource m_Pose = new ARFoundationPoseSource();
        private readonly MixedSpaceCameraDisplayOrientation m_Display = new MixedSpaceCameraDisplayOrientation();
        private MixedSpaceCameraReadiness m_Readiness;
        private MixedSpaceCameraAcquisition m_Acquisition;
        private MixedSpacePixelFormat m_CaptureFormat;
        private bool m_WritePoseLogs;
        public void SetWritePoseLogs(bool enabled) => m_WritePoseLogs = enabled;

        public MixedSpaceARFoundationProfile Profile => m_Profile;
        private MixedSpaceARFoundationSettings Settings => m_Settings ?? (m_Settings = CreateSettings());
        protected virtual MixedSpaceARFoundationSettings CreateSettings() =>
            m_Profile != null ? m_Profile.CreateSettings() :
                throw new MixedSpaceRuntimeUnavailableException("Assign an AR Foundation Profile to the platform adapter.");
        private MixedSpaceCameraReadiness Readiness => m_Readiness ??=
            new MixedSpaceCameraReadiness(ApplyResolution);

        public enum CaptureResolutionMode { SystemDefault, Hd720, FullHd1080, HighestAvailable }
        public CaptureResolutionMode AndroidCaptureResolution
        {
            get => Settings.Android;
            set { Settings.SetAndroidResolution(value); ApplyResolution(); }
        }
        public CaptureResolutionMode IosCaptureResolution
        {
            get => Settings.Ios;
            set { Settings.SetIosResolution(value); ApplyResolution(); }
        }

        /// <summary>
        /// 从共享相机配置中选择平台分辨率。
        /// Select the platform's resolution from the shared camera profile.
        /// </summary>
        protected virtual CaptureResolutionMode SelectResolution(MixedSpaceARFoundationSettings settings)
        {
#if UNITY_ANDROID
            return settings.Android;
#else
            return settings.Ios;
#endif
        }

        protected virtual void ValidatePlatform() { }

        private bool ApplyResolution() => m_Device.TryConfigure(SelectResolution(Settings));

        public async Task<bool> StartAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidatePlatform();
            var settings = Settings;
            Readiness.Stop();
            MixedSpaceLogger.Log("Starting AR Foundation adapter");
#if UNITY_EDITOR
            MixedSpaceLogger.LogWarning("Running AR Foundation Platform in Unity Editor will result in failed updates.");
#endif
            m_Device.Discover();
            m_Pose.BindCamera();
            m_CaptureFormat = settings.Format;
            m_Acquisition ??= new MixedSpaceCameraAcquisition(m_Device, m_Pose,
                () => Quaternion.Euler(0f, 0f, m_Display.Angle(Screen.orientation)),
                profile => { if (m_WritePoseLogs) ARFoundationCaptureJournal.Append(profile); });
            return await Readiness.StartAsync(settings.ProbeLimit, settings.ProbeDelayMilliseconds, cancellationToken);
        }

        public Task<IMixedSpacePlatformFrame> CaptureFrameAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (m_Readiness?.IsReady != true)
                throw new MixedSpaceRuntimeUnavailableException("Trying to capture before platform startup.");
            var tracking = new MixedSpaceTrackingSnapshot(m_Pose.IsTracking ? 1 : 0);
            var capture = m_Acquisition.Acquire(m_CaptureFormat, cancellationToken);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult<IMixedSpacePlatformFrame>(new MixedSpacePlatformFrame(tracking, capture));
            }
            catch { capture?.Dispose(); throw; }
        }

        public MixedSpacePlatformPoseSample SamplePose() => m_Pose.SamplePose();
        public void UseDisplayOrientation(ScreenOrientation orientation) => m_Display.Use(orientation);
        public void FollowDisplayOrientation() => m_Display.FollowDevice();
        protected virtual void OnEnable() => m_Pose.Observe();
        protected virtual void OnDisable() => m_Pose.StopObserving();
        protected virtual void OnDestroy()
        {
            m_Readiness?.Stop();
            m_Pose.StopObserving();
        }

        public Task ShutdownAsync()
        {
            m_Readiness?.Stop();
            m_Pose.ClearTracking();
            // 已发布的采集图像和活动像素租约仍归调用方持有。
            // Published captures and active pixel leases remain owned by callers.
            return Task.CompletedTask;
        }
    }
}
