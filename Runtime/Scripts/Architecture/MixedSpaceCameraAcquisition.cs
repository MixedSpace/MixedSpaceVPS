using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace MixedSpace.XR
{
    /// <summary>
    /// 一次原始采集及标定。OpenPixels 只能消费图像一次，失败时也由实现释放转换资源；Dispose 只释放尚未被消费的图像。
    /// A single raw camera acquisition and its calibration. OpenPixels consumes
    /// the image exactly once, including on failure; implementations release
    /// failed conversions themselves. Dispose releases only unconsumed images.
    /// </summary>
    public interface IMixedSpaceCameraImage : IDisposable
    {
        int Width { get; }
        int Height { get; }
        Vector4 Intrinsics { get; }
        IMixedSpacePixelSource OpenPixels(MixedSpacePixelFormat format);
    }

    public interface IMixedSpaceCameraFeed
    {
        bool TryAcquire(out IMixedSpaceCameraImage image);
    }

    /// <summary>
    /// 跟踪独立于图像提供者，只有 WebCamera 图像并不构成位姿源，也不能声称已跟踪。
    /// Tracking is independent of the image supplier. A WebCamera implementation
    /// alone is not a pose source and must not claim tracked pose availability.
    /// </summary>
    public interface IMixedSpaceCameraPoseSource
    {
        bool HasPose { get; }
        MixedSpacePlatformPoseSample SamplePose();
    }

    /// <summary>
    /// 同步采集事务依次取得图像、在转换前采样位姿与时间、构建元数据、消费像素并移交所有权；中间没有 await 或重新采样。该顺序不保证硬件时间同步。
    /// Synchronous capture transaction: acquire image, snapshot pose/time before
    /// conversion, construct metadata, consume pixels, then publish ownership.
    /// There is no await or subsequent pose resampling within one capture.
    /// This preserves acquisition order, not a claim of hardware timestamp sync.
    /// </summary>
    public sealed class MixedSpaceCameraAcquisition
    {
        private readonly IMixedSpaceCameraFeed m_Camera;
        private readonly IMixedSpaceCameraPoseSource m_Pose;
        private readonly Func<Quaternion> m_DisplayRotation;
        private readonly Action<MixedSpaceCaptureProfile> m_Journal;

        public MixedSpaceCameraAcquisition(IMixedSpaceCameraFeed camera, IMixedSpaceCameraPoseSource pose,
            Func<Quaternion> displayRotation, Action<MixedSpaceCaptureProfile> journal = null)
        {
            m_Camera = camera ?? throw new ArgumentNullException(nameof(camera));
            m_Pose = pose ?? throw new ArgumentNullException(nameof(pose));
            m_DisplayRotation = displayRotation ?? throw new ArgumentNullException(nameof(displayRotation));
            m_Journal = journal;
        }

        public MixedSpaceCameraCapture Acquire(MixedSpacePixelFormat format, CancellationToken token)
        {
            return Acquire(format, token, null);
        }

        /// <summary>
        /// 采集图像并附带可选的原生定位方向；显示旋转和跟踪位姿独立保留。
        /// Captures with an optional native image direction while preserving display rotation and tracking pose.
        /// </summary>
        public MixedSpaceCameraCapture Acquire(MixedSpacePixelFormat format, CancellationToken token,
            int? nativeImageRotationAngle)
        {
            token.ThrowIfCancellationRequested();
            if (format != MixedSpacePixelFormat.Luminance8 && format != MixedSpacePixelFormat.Rgb24)
                throw new ArgumentOutOfRangeException(nameof(format));
            if (!m_Pose.HasPose) return null;
            if (!m_Camera.TryAcquire(out IMixedSpaceCameraImage image)) return null;
            if (image == null) throw new InvalidOperationException("Camera reported acquisition without an image.");
            using (image)
            {
                IMixedSpacePixelSource pixels = null;
                MixedSpaceCameraCapture capture = null;
                try
                {
                    MixedSpacePlatformPoseSample pose = m_Pose.SamplePose();
                    var profile = new MixedSpaceCaptureProfile(image.Width, image.Height,
                        format == MixedSpacePixelFormat.Luminance8 ? 1 : 3, format, image.Intrinsics,
                        pose.Position, pose.Rotation, pose.TimestampNanoseconds, m_DisplayRotation(),
                        nativeImageRotationAngle: nativeImageRotationAngle);
                    pixels = image.OpenPixels(format);
                    capture = new MixedSpaceCameraCapture(profile, pixels);
                    pixels = null; // 采集对象成为像素的唯一所有者。 / Capture becomes the sole pixel owner.
                    token.ThrowIfCancellationRequested();
                    try { m_Journal?.Invoke(profile); }
                    catch
                    {
                        // 可选诊断不能使有效相机数据失效。
                        // Optional diagnostics cannot invalidate usable camera data.
                    }
                    token.ThrowIfCancellationRequested();
                    return capture;
                }
                catch
                {
                    capture?.Dispose();
                    pixels?.Dispose();
                    throw;
                }
            }
        }
    }

    /// <summary>
    /// 就绪状态只属于一次启动；停止或替换会使旧探测无效，迟到的继续执行不能重启已停相机。由所有者在 Unity 线程驱动，本对象不持有相机资源。
    /// Readiness belongs to one startup attempt. Stop/replacement invalidates
    /// pending probes so a late continuation cannot reactivate a stopped camera.
    /// The owner calls this on the Unity thread; it does not own camera resources.
    /// </summary>
    public sealed class MixedSpaceCameraReadiness
    {
        private readonly Func<bool> m_Probe;
        private readonly Func<int, CancellationToken, Task> m_Delay;
        private long m_Attempt;
        public bool IsReady { get; private set; }

        public MixedSpaceCameraReadiness(Func<bool> probe, Func<int, CancellationToken, Task> delay = null)
        {
            m_Probe = probe ?? throw new ArgumentNullException(nameof(probe));
            m_Delay = delay ?? ((milliseconds, token) => Task.Delay(milliseconds, token));
        }

        public async Task<bool> StartAsync(int probeLimit, int delayMilliseconds, CancellationToken token)
        {
            if (probeLimit < 1) throw new ArgumentOutOfRangeException(nameof(probeLimit));
            if (delayMilliseconds < 1) throw new ArgumentOutOfRangeException(nameof(delayMilliseconds));
            long attempt = ++m_Attempt;
            IsReady = false;
            for (int index = 0; index < probeLimit; index++)
            {
                token.ThrowIfCancellationRequested();
                if (attempt != m_Attempt) return false;
                bool ready = m_Probe();
                token.ThrowIfCancellationRequested();
                if (attempt != m_Attempt) return false;
                if (ready) { IsReady = true; return true; }
                await m_Delay(delayMilliseconds, token);
                token.ThrowIfCancellationRequested();
            }
            return false;
        }

        public void Stop() { ++m_Attempt; IsReady = false; }
    }

    /// <summary>
    /// 显示方向是呈现策略，不是跟踪位姿。
    /// Display orientation is a presentation policy, not tracking pose.
    /// </summary>
    public sealed class MixedSpaceCameraDisplayOrientation
    {
        private ScreenOrientation? m_Override;
        public void Use(ScreenOrientation orientation) => m_Override = orientation;
        public void FollowDevice() => m_Override = null;
        public float Angle(ScreenOrientation deviceOrientation)
        {
            return (m_Override ?? deviceOrientation) switch
            {
                ScreenOrientation.Portrait => 90f,
                ScreenOrientation.LandscapeLeft => 180f,
                ScreenOrientation.LandscapeRight => 0f,
                ScreenOrientation.PortraitUpsideDown => -90f,
                _ => 0f
            };
        }
    }
}
