using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace MixedSpace.XR
{
    /// <summary>
    /// 一次取帧的跟踪状态与可选图像。此容器本身无需释放；有图像时，接收方负责释放 Capture。
    /// SDK 定位管线在周期结束的 finally 中释放图像，包含失败和取消路径。
    /// A frame result contains tracking state and an optional capture. The container needs no
    /// disposal, but the receiver owns Capture. The SDK pipeline disposes it in finally on success, failure and
    /// cancellation.
    /// </summary>
    public interface IMixedSpacePlatformFrame
    {
        /// <summary>
        /// 是否包含可用于本次处理的图像；不能据此推断设备是否正在跟踪。
        /// Whether a usable image is present; this alone does not indicate tracking availability.
        /// </summary>
        bool HasCapture { get; }
        /// <summary>
        /// 本次取帧时的跟踪状态；没有图像时仍可提供该状态。
        /// Tracking state for this acquisition, available even when there is no image.
        /// </summary>
        MixedSpaceTrackingSnapshot Tracking { get; }
        /// <summary>
        /// 可为空；处理完成后由接收方调用 Dispose，不能以释放像素租约代替释放图像。
        /// May be null. The receiver must Dispose it after processing; disposing a pixel lease does not
        /// dispose the capture.
        /// </summary>
        IMixedSpaceCameraCapture Capture { get; }
    }

    /// <summary>
    /// 平台相机与跟踪接口，以启动、取帧、采样和关闭表达生命周期。
    /// 平台负责将图像格式和位姿转换为 SDK 约定，核心层不直接依赖设备厂商 API。
    /// Platform camera/tracking lifecycle contract for startup, acquisition, pose sampling and
    /// shutdown. Adapters convert device pixels and poses to SDK conventions, keeping vendor APIs outside
    /// Core.
    /// </summary>
    public interface IMixedSpacePlatformAdapter
    {
        /// <summary>
        /// 启动平台能力并等待就绪；返回 false 表示未能就绪，调用失败也可能抛出异常。
        /// Starts platform capabilities and waits for readiness; false means not ready, and failures may
        /// also throw.
        /// </summary>
        /// <param name="cancellationToken">
        /// 取消启动等待；具体设备资源的关闭通过 ShutdownAsync 完成。
        /// Cancels startup waiting; ShutdownAsync closes the device resources.
        /// </param>
        /// <returns>
        /// 平台是否已具备取帧条件。
        /// Whether the platform is ready to acquire frames.
        /// </returns>
        Task<bool> StartAsync(CancellationToken cancellationToken);
        /// <summary>
        /// 获取一次平台结果；没有新图像时可返回不含 Capture 的结果。
        /// 成功返回图像后，其释放责任交给接收方；返回前失败时由实现释放已申请的资源。
        /// Acquires a platform result, possibly without a new capture. Returning a capture transfers
        /// disposal responsibility to the receiver; failures before return must release resources inside the
        /// implementation.
        /// </summary>
        /// <param name="cancellationToken">
        /// 取消本次取帧操作。
        /// Cancels this acquisition.
        /// </param>
        /// <returns>
        /// 跟踪状态及可选图像；调用方也应处理空结果。
        /// Tracking state and optional capture; callers must also handle a null result.
        /// </returns>
        Task<IMixedSpacePlatformFrame> CaptureFrameAsync(
            CancellationToken cancellationToken);
        /// <summary>
        /// 采样实时跟踪位姿，供融合使用；不一定对应最近一张图像的曝光时刻，应先检查 IsTracking。
        /// Samples a live tracking pose for fusion, not necessarily the latest exposure pose. Check
        /// IsTracking first.
        /// </summary>
        MixedSpacePlatformPoseSample SamplePose();
        /// <summary>
        /// 停止平台取帧和相关订阅；已交给调用方的图像及像素租约仍由调用方释放。
        /// Stops acquisition and subscriptions; callers still own already-published captures and active
        /// pixel leases.
        /// </summary>
        Task ShutdownAsync();
    }
    
    /// <summary>
    /// 提供跟踪丢失时间的可选接口，供位姿融合判断历史何时失效。
    /// Optional source of tracking loss timestamps that invalidate pose fusion history.
    /// </summary>
    public interface IMixedSpaceTrackingLossSource
    {
        /// <summary>
        /// 最近一次观测到跟踪由可用变为丢失的时间，单位为纳秒，与定位图像使用同一时钟。
        /// 首次丢失前返回 0，恢复跟踪后保留该值，供融合通道检测短暂丢失。
        /// Last observed transition from tracking to lost, in nanoseconds on the localization capture clock.
        /// Zero until the first loss. The value persists after recovery so fusion can detect brief losses.
        /// </summary>
        long LastTrackingLossTimestampNanoseconds { get; }
    }

    #region Simple implementations
    
    /// <summary>
    /// 平台结果的只读容器。是否包含图像直接由 Capture 是否为空决定，不维护独立标志。
    /// 容器只读不代表图像永久有效；图像仍受 Dispose 和像素租约的生命周期约束。
    /// Read-only result container. HasCapture derives from whether Capture is null; read-only metadata
    /// does not extend image lifetime, which remains governed by Dispose and leases.
    /// </summary>
    public sealed class MixedSpacePlatformFrame : IMixedSpacePlatformFrame
    {
        public MixedSpacePlatformFrame(
            MixedSpaceTrackingSnapshot tracking,
            IMixedSpaceCameraCapture capture)
        {
            Tracking = tracking;
            Capture = capture;
        }

        public bool HasCapture => Capture != null;
        public MixedSpaceTrackingSnapshot Tracking { get; }
        public IMixedSpaceCameraCapture Capture { get; }
    }

    /// <summary>
    /// 实时跟踪位姿时间戳来源。用于诊断，跨时钟同步由平台适配器负责。
    /// Origin of a live tracking timestamp. Diagnostic only; adapters own clock synchronization.
    /// </summary>
    public enum MixedSpaceTrackingTimestampSource
    {
        Unspecified,
        Device,
        DevicePredicted,
        HostMonotonic,
        HostUtc
    }

    /// <summary>
    /// 融合管线使用的实时跟踪位姿，由适配器转换到 Unity 坐标系。
    /// 图像采集对应的位姿另存于 Capture.Profile，同步精度由适配器决定，不应直接用本结构替代。
    /// Live fusion pose expressed in Unity coordinates. Capture.Profile contains the image-associated
    /// pose with adapter-specific synchronization; this structure must not replace it directly.
    /// </summary>
    public struct MixedSpacePlatformPoseSample
    {
        /// <summary>
        /// Unity 坐标系中的跟踪位置，单位为米。
        /// Tracking position in Unity coordinates, in meters.
        /// </summary>
        public Vector3 Position { get; set; }
        /// <summary>
        /// 与 Position 处于同一跟踪坐标系的 Unity 旋转四元数。
        /// Unity rotation quaternion in the same tracking coordinate frame as Position.
        /// </summary>
        public Quaternion Rotation { get; set; }
        /// <summary>
        /// 适配器时钟上的时间戳，单位为纳秒。比较位姿、图像采集和跟踪丢失时间时，三者必须使用同一时间域。
        /// 通过 TimestampSource 查询时钟来源；该值不保证能与 Unix 时间或其他设备的时间比较。
        /// Timestamp in nanoseconds on the adapter clock. Pose, capture, and tracking loss times must
        /// share a clock domain to be compared. See TimestampSource for the origin. The value need not
        /// be comparable to Unix time or another device's clock.
        /// </summary>
        public long TimestampNanoseconds { get; set; }
        /// <summary>
        /// 未提供来源的适配器使用 Unspecified。
        /// Adapters that do not identify their clock use Unspecified.
        /// </summary>
        public MixedSpaceTrackingTimestampSource TimestampSource { get; set; }
        /// <summary>
        /// 本次位姿是否可用于跟踪；为 false 时不应使用位置、旋转或时间戳参与融合。
        /// Whether this pose is usable for tracking; when false, do not feed its position, rotation or
        /// timestamp into fusion.
        /// </summary>
        public bool IsTracking { get; set; }
    }

    /// <summary>
    /// 取帧时的平台跟踪状态，与本次是否取得图像分别表达。
    /// Platform tracking state at acquisition, separate from image availability.
    /// </summary>
    public readonly struct MixedSpaceTrackingSnapshot
    {
        public MixedSpaceTrackingSnapshot(int quality)
        {
            Quality = quality;
        }

        /// <summary>
        /// 当前内置适配器使用 0 表示未跟踪、1 表示跟踪有效；不是百分比或统一的多级置信度。
        /// Built-in adapters currently use 0 for untracked and 1 for tracked. This is neither a percentage
        /// nor a shared multilevel confidence score.
        /// </summary>
        public int Quality { get; }
    }
    #endregion
}
