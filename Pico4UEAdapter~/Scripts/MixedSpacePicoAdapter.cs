using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using MixedSpace.XR;
using Unity.XR.PICO.TOBSupport;
using UnityEngine;

namespace MixedSpace.Platforms.Pico
{
    /// <summary>
    /// PICO 4 Ultra Enterprise 3.1.0 企业相机桥接。定位和地图管理由 SDK 核心负责。
    /// 回调复制像素；Unity 相机位姿在主线程读取。目前使用接收时刻位姿近似，不提供曝光同步保证。
    /// PICO 4 Ultra Enterprise 3.1.0 camera bridge; Core owns localization and maps. Callbacks copy
    /// pixels, while Unity poses are read on the main thread. Receive-time poses are approximate and do not guarantee
    /// exposure synchronization.
    /// </summary>
    [AddComponentMenu("MixedSpace/Platforms/PICO 4 UE Adapter")]
    public sealed class MixedSpacePicoAdapter : MonoBehaviour, IMixedSpacePlatformAdapter
    {
        [SerializeField] private MixedSpacePicoProfile m_Profile;
        private static MixedSpacePicoAdapter s_Owner;
#if UNITY_ANDROID && !UNITY_EDITOR
        private static TaskCompletionSource<bool> s_EnterpriseConnection;
#endif
        private readonly object m_Gate = new object();
        private IntPtr m_Buffer;
        private byte[] m_Pending;
        private long m_Received, m_Consumed;
        private int m_Quality, m_Width, m_Height, m_MaxAge;
        private bool m_Accepting, m_Ready;
        private Transform m_Camera;
        private Vector4 m_Intrinsics;
        private Task<bool> m_Starting;
        private int m_Generation;
        private CancellationTokenSource m_Lifetime;
        private bool m_OpenPending, m_Opened;
        private long m_Paired;
        private Vector3 m_FramePosition;
        private Quaternion m_FrameRotation;
        private bool m_Paused, m_ResumeRequested;
        private bool m_RequestingPermission;

        public Task<bool> StartAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (m_Paused) throw new InvalidOperationException("应用暂停期间不能启动 PICO 相机。");
            if (m_Ready) return Task.FromResult(true);
            if (m_Starting != null && !m_Starting.IsCompleted) return m_Starting;
            return m_Starting = StartCoreAsync(token);
        }

        private async Task<bool> StartCoreAsync(CancellationToken token)
        {
#if !UNITY_ANDROID || UNITY_EDITOR
            throw new PlatformNotSupportedException("PICO 企业相机仅支持 Android 真机。");
#else
            if (!isActiveAndEnabled || !m_Profile || !Camera.main)
                throw new InvalidOperationException("需要启用 PICO Adapter、绑定 Profile 并提供 MainCamera。");
            if (!ReferenceEquals(s_Owner, null) && !ReferenceEquals(s_Owner, this))
                throw new InvalidOperationException("PICO 企业相机已被其他适配器使用。");
            if (m_OpenPending || m_Opened || m_Buffer != IntPtr.Zero)
                throw new InvalidOperationException("PICO 相机仍在释放，请等待关闭完成后重试。");
            int generation = ++m_Generation;
            m_Camera = Camera.main.transform;
            m_Width = m_Profile.Width; m_Height = m_Profile.Height; m_MaxAge = m_Profile.MaxFrameAgeMilliseconds;
            if (m_Width <= 0 || m_Height <= 0 || m_MaxAge <= 0 || m_Profile.StartupTimeoutMilliseconds <= 0)
                throw new InvalidOperationException("PICO Profile 尺寸及超时必须为正数。");
            int bufferSize = checked(m_Width * m_Height * 4);
            s_Owner = this;
            m_Lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(m_Lifetime.Token))
            {
                string startupStage = "相机权限";
                try
                {
                    // 用户操作时间不占用原生相机启动预算。显式销毁仍由生命周期令牌取消。
                    // User interaction does not consume the native camera startup timeout; explicit destruction still cancels
                    // through the lifetime token.
                    await EnsureCameraPermissionAsync(m_Lifetime.Token);
                    timeout.CancelAfter(m_Profile.StartupTimeoutMilliseconds);
                    startupStage = "企业服务连接";
                    await EnsureEnterpriseConnectionAsync(timeout.Token);
                    timeout.Token.ThrowIfCancellationRequested();
                    startupStage = "打开相机";
                    PXR_Enterprise.Configurefor4U();
                    var opened = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    var context = SynchronizationContext.Current;
                    if (context == null) throw new InvalidOperationException("PICO 相机必须在 Unity 主线程启动。");
                    m_OpenPending = true;
                    try
                    {
                        PXR_Enterprise.OpenCameraAsyncfor4U(value => context.Post(_ =>
                        {
                            m_OpenPending = false;
                            m_Opened = value;
                            opened.TrySetResult(value);
                            // 超时或禁用后的迟到回调仍需关闭相机；在此之前不允许其他实例占用。
                            // Late callbacks after timeout or disable must still close the camera before another instance can take
                            // ownership.
                            if (generation != m_Generation)
                            {
                                try { ShutdownAsync(); }
                                catch (Exception e) { UnityEngine.Debug.LogException(e); }
                            }
                        }, null));
                    }
                    catch { m_OpenPending = false; throw; }
                    if (!await WaitAsync(opened.Task, timeout.Token)) throw new InvalidOperationException("PICO 相机打开失败。");
                    MixedSpaceLogger.Log("[MixedSpacePicoAdapter] 相机打开成功，等待图像帧。", MixedSpaceLogger.LoggingLevel.Verbose);
                    if (generation != m_Generation) throw new OperationCanceledException();
                    var parameters = PXR_Enterprise.GetCameraParametersNewfor4U(m_Width, m_Height);
                    m_Intrinsics = new Vector4((float)parameters.fx, (float)parameters.fy, (float)parameters.cx, (float)parameters.cy);
                    for (int i = 0; i < 4; i++)
                        if (float.IsNaN(m_Intrinsics[i]) || float.IsInfinity(m_Intrinsics[i]))
                            throw new InvalidOperationException("PICO 相机内参不是有限值。");
                    if (!(m_Intrinsics.x > 0) || !(m_Intrinsics.y > 0)) throw new InvalidOperationException("PICO 相机内参无效。");
                    m_Buffer = Marshal.AllocHGlobal(bufferSize);
                    lock (m_Gate) { m_Accepting = true; m_Pending = null; m_Consumed = 0; m_Paired = 0; }
                    var registeredBuffer = m_Buffer;
                    PXR_Enterprise.SetCameraFrameBufferfor4U(m_Width, m_Height, ref registeredBuffer, Receive);
                    if (registeredBuffer != m_Buffer) throw new InvalidOperationException("PICO 相机改变了调用方缓冲地址。");
                    if (!PXR_Enterprise.StartGetImageDatafor4U(PXRCaptureRenderMode.PXRCapture_RenderMode_LEFT, m_Width, m_Height))
                        throw new InvalidOperationException("PICO 相机取帧启动失败。");
                    startupStage = "等待首帧";
                    while (true)
                    {
                        timeout.Token.ThrowIfCancellationRequested();
                        if (generation != m_Generation) throw new OperationCanceledException();
                        lock (m_Gate) if (m_Pending != null && !m_Paused)
                        {
                            m_Ready = true;
                            MixedSpaceLogger.Log($"[MixedSpacePicoAdapter] 首帧已接收：{m_Width}×{m_Height}，tracking={m_Quality}。", MixedSpaceLogger.LoggingLevel.Verbose);
                            return true;
                        }
                        await Task.Delay(20, timeout.Token);
                    }
                }
                catch (OperationCanceledException e) when (generation == m_Generation && !token.IsCancellationRequested)
                {
                    await ShutdownAsync();
                    throw new TimeoutException($"PICO 相机启动超时（{startupStage}）。", e);
                }
                catch { if (generation == m_Generation) await ShutdownAsync(); throw; }
            }
#endif
        }

        private static async Task<bool> WaitAsync(Task<bool> task, CancellationToken token)
        {
            var canceled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (token.Register(() => canceled.TrySetCanceled()))
                return await await Task.WhenAny(task, canceled.Task);
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        private static async Task EnsureEnterpriseConnectionAsync(CancellationToken token)
        {
            // 相机暂停时只关闭采集。已连接的 ToB 服务重复绑定时不会再发连接回调。
            // Keep the process-wide service binding while capture is paused. Rebinding an active service may not call back.
            if (s_EnterpriseConnection == null)
            {
                if (!PXR_Enterprise.InitEnterpriseService())
                    throw new InvalidOperationException("PICO 企业服务初始化失败。");
                s_EnterpriseConnection = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                try { PXR_Enterprise.BindEnterpriseService(OnEnterpriseConnectionChanged); }
                catch { s_EnterpriseConnection = null; throw; }
            }

            // PICO can reconnect the same binding after a disconnect. Follow the current connection task.
            while (true)
            {
                TaskCompletionSource<bool> connection = s_EnterpriseConnection;
                await WaitAsync(connection.Task, token);
                token.ThrowIfCancellationRequested();
                if (ReferenceEquals(connection, s_EnterpriseConnection))
                    return;
            }
        }

        private static void OnEnterpriseConnectionChanged(bool connected)
        {
            // BindCallback dispatches to the Unity main thread.
            if (connected)
            {
                s_EnterpriseConnection?.TrySetResult(true);
            }
            else if (s_EnterpriseConnection != null && s_EnterpriseConnection.Task.IsCompleted)
            {
                s_EnterpriseConnection = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        /// <summary>
        /// 等待相机权限及前台恢复，避免系统权限窗口取消 SDK 正在等待的启动任务。
        /// Waits for camera permission and foreground recovery without letting the permission dialog
        /// cancel SDK startup.
        /// </summary>
        private async Task EnsureCameraPermissionAsync(CancellationToken token)
        {
            if (UnityEngine.Android.Permission.HasUserAuthorizedPermission(UnityEngine.Android.Permission.Camera)) return;
            m_RequestingPermission = true;
            var result = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var callbacks = new UnityEngine.Android.PermissionCallbacks();
            callbacks.PermissionGranted += _ => result.TrySetResult(true);
            callbacks.PermissionDenied += _ => result.TrySetResult(false);
            callbacks.PermissionDeniedAndDontAskAgain += _ => result.TrySetResult(false);
            try
            {
                using (var permissionTimeout = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    permissionTimeout.CancelAfter(60000);
                    MixedSpaceLogger.Log("[MixedSpacePicoAdapter] 等待相机权限，请在设备中允许相机访问。", MixedSpaceLogger.LoggingLevel.Verbose);
                    UnityEngine.Android.Permission.RequestUserPermission(UnityEngine.Android.Permission.Camera, callbacks);
                    bool granted;
                    try
                    {
                        granted = await WaitAsync(result.Task, permissionTimeout.Token);
                        // 权限回调可能先于 Unity 的恢复通知到达。
                        // Permission callbacks can arrive before Unity's resume notification.
                        await Task.Yield();
                        while (m_Paused) await Task.Delay(20, permissionTimeout.Token);
                    }
                    catch (OperationCanceledException) when (!token.IsCancellationRequested)
                    {
                        throw new TimeoutException("等待 PICO 相机权限或应用返回前台超时，请检查设备上的权限窗口。");
                    }
                    token.ThrowIfCancellationRequested();
                    if (!granted || !UnityEngine.Android.Permission.HasUserAuthorizedPermission(UnityEngine.Android.Permission.Camera))
                        throw new UnauthorizedAccessException("PICO 相机权限未授予。请在系统应用权限中允许相机访问，然后重新启动应用。");
                    MixedSpaceLogger.Log("[MixedSpacePicoAdapter] 相机权限已确认。", MixedSpaceLogger.LoggingLevel.Verbose);
                }
            }
            finally { m_RequestingPermission = false; }
        }
#endif

        private void Receive(Frame frame)
        {
            lock (m_Gate)
            {
                if (!m_Accepting || frame.data == IntPtr.Zero || frame.width != m_Width || frame.height != m_Height) return;
                int count = checked(m_Width * m_Height);
                if (frame.datasize != count * 3L && frame.datasize != count * 4L) return;
                int channels = (int)(frame.datasize / count);
                var rgb = new byte[(int)frame.datasize];
                Marshal.Copy(frame.data, rgb, 0, rgb.Length);
                var gray = new byte[count];
                for (int i = 0; i < count; i++) gray[i] = rgb[i * channels + 1];
                m_Pending = gray; m_Quality = frame.status; m_Received = Stopwatch.GetTimestamp();
            }
        }

        private void Update()
        {
            if (!m_Camera) return;
            lock (m_Gate)
            {
                if (!m_Accepting || m_Pending == null || m_Paired == m_Received) return;
                // 在帧到达后的主线程更新采样，避免定位调度间隔进一步放大图像与位姿的时间差。
                // Sample the pose in the main-thread update following frame arrival, avoiding further delay from localization
                // scheduling.
                m_FramePosition = m_Camera.position;
                m_FrameRotation = m_Camera.rotation;
                m_Paired = m_Received;
            }
        }

        public Task<IMixedSpacePlatformFrame> CaptureFrameAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            byte[] pixels; long received; int quality; Vector3 position; Quaternion rotation;
            lock (m_Gate)
            {
                pixels = m_Paired == m_Received ? m_Pending : null;
                received = m_Received; quality = m_Quality;
                position = m_FramePosition; rotation = m_FrameRotation;
            }
            if (!m_Ready || !m_Camera || quality != 1 || pixels == null || received == m_Consumed ||
                (Stopwatch.GetTimestamp() - received) * 1000.0 / Stopwatch.Frequency > m_MaxAge)
                return Task.FromResult<IMixedSpacePlatformFrame>(new MixedSpacePlatformFrame(new MixedSpaceTrackingSnapshot(0), null));
            // 与实时位姿使用同一主机单调时钟；这是采样时刻，不冒充设备曝光时间。
            // Use the same host monotonic clock as live poses; this is sampling time, not hardware exposure time.
            long timestamp = (long)(received * (1000000000.0 / Stopwatch.Frequency));
            var profile = new MixedSpaceCaptureProfile(m_Width, m_Height, 1, MixedSpacePixelFormat.Luminance8,
                m_Intrinsics, position, rotation * Quaternion.Euler(0, 0, 180), timestamp, Quaternion.identity);
            m_Consumed = received;
            return Task.FromResult<IMixedSpacePlatformFrame>(new MixedSpacePlatformFrame(new MixedSpaceTrackingSnapshot(1),
                new MixedSpaceCameraCapture(profile, new MixedSpaceManagedPixelSource(pixels))));
        }

        public MixedSpacePlatformPoseSample SamplePose()
        {
            int quality; long received;
            lock (m_Gate) { quality = m_Quality; received = m_Received; }
            bool tracking = m_Ready && m_Camera && quality == 1 &&
                (Stopwatch.GetTimestamp() - received) * 1000.0 / Stopwatch.Frequency <= m_MaxAge;
            return new MixedSpacePlatformPoseSample { IsTracking = tracking, TimestampNanoseconds = Now(),
                TimestampSource = MixedSpaceTrackingTimestampSource.HostMonotonic,
                Position = m_Camera ? m_Camera.position : Vector3.zero, Rotation = m_Camera ? m_Camera.rotation : Quaternion.identity };
        }

        private static long Now() => (long)(Stopwatch.GetTimestamp() * (1000000000.0 / Stopwatch.Frequency));
        public Task ShutdownAsync()
        {
            ++m_Generation;
            m_Lifetime?.Cancel();
            m_Lifetime?.Dispose();
            m_Lifetime = null;
            lock (m_Gate) { m_Accepting = false; m_Ready = false; m_Pending = null; m_Quality = 0; }
#if UNITY_ANDROID && !UNITY_EDITOR
            if (s_Owner == this)
            {
                if (m_OpenPending) return Task.CompletedTask;
                if (m_Opened && !PXR_Enterprise.CloseCamerafor4U())
                    throw new InvalidOperationException("PICO 相机关闭失败，保留原生缓冲以避免仍在写入时释放。");
                m_Opened = false;
                if (m_Buffer != IntPtr.Zero) Marshal.FreeHGlobal(m_Buffer);
                m_Buffer = IntPtr.Zero; s_Owner = null;
            }
#endif
            return Task.CompletedTask;
        }
        private async void OnApplicationPause(bool paused)
        {
            if (m_Paused == paused) return;
            m_Paused = paused;
            // 只有主动请求权限期间保留启动任务；正常后台暂停仍关闭相机。
            // Keep startup alive only during an explicit permission request; ordinary background pause still closes the
            // camera.
            if (m_RequestingPermission) return;
            try
            {
                if (paused)
                {
                    m_ResumeRequested |= m_Ready || (m_Starting != null && !m_Starting.IsCompleted);
                    await ShutdownAsync();
                }
                else if (m_ResumeRequested && isActiveAndEnabled)
                {
                    if (m_Starting != null && !m_Starting.IsCompleted)
                    {
                        try { await m_Starting; }
                        catch (OperationCanceledException) { }
                    }
                    if (!m_Paused && isActiveAndEnabled && await StartAsync(CancellationToken.None) && !m_Paused)
                        m_ResumeRequested = false;
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { UnityEngine.Debug.LogException(e); }
        }

        private async void OnDisable()
        {
            m_ResumeRequested = false;
            try { await ShutdownAsync(); }
            catch (Exception e) { UnityEngine.Debug.LogException(e); }
        }
    }
}
