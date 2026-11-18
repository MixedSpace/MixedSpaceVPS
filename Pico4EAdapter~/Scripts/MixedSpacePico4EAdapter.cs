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
    /// PICO 4 Enterprise VST 拉取式相机适配。Unity 位姿只在主线程读取，
    /// 原生取帧与像素复制在工作线程串行执行，关闭相机前等待取帧结束。
    /// 位姿为请求附近的近似采样，不保证与设备曝光严格同步。
    /// Pull-based PICO 4 Enterprise VST adapter. Unity poses are read only on the main thread; native
    /// acquisition and pixel copies run serially on a worker, and shutdown waits for them. Poses approximate
    /// acquisition time, without exposure synchronization guarantees.
    /// </summary>
    [AddComponentMenu("MixedSpace/Platforms/PICO 4E Adapter")]
    public sealed class MixedSpacePico4EAdapter : MonoBehaviour, IMixedSpacePlatformAdapter
    {
        [SerializeField] private MixedSpacePico4EProfile m_Profile;
        private static MixedSpacePico4EAdapter s_Owner;
        private readonly SemaphoreSlim m_CaptureGate = new SemaphoreSlim(1, 1);
        private CancellationTokenSource m_Lifetime;
        private Task<bool> m_Start;
        private Task m_Stop;
        private Transform m_Camera;
        private bool m_Ready, m_Opened, m_Paused, m_PermissionPending, m_Resume;
        private int m_Generation;
        private long m_LastWarning;
        private bool m_FirstFrameLogged;

        public Task<bool> StartAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (m_Start != null && !m_Start.IsCompleted) return m_Start;
            if (m_Ready) return Task.FromResult(true);
            return m_Start = StartCoreAsync(token);
        }

        private async Task<bool> StartCoreAsync(CancellationToken token)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (m_Stop != null) await m_Stop;
            if (!isActiveAndEnabled || m_Paused || !m_Profile || !Camera.main)
                throw new InvalidOperationException("PICO 4E 需要前台运行、有效 Profile 和 MainCamera。");
            if ((!ReferenceEquals(s_Owner, null) && !ReferenceEquals(s_Owner, this)) || m_Opened)
                throw new InvalidOperationException("PICO 4E 相机仍被占用。");
            if (m_Profile.StartupTimeoutMilliseconds <= 0) throw new InvalidOperationException("启动超时必须大于零。");
            s_Owner = this;
            int generation = ++m_Generation;
            m_Camera = Camera.main.transform;
            m_Lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
            var lifetime = m_Lifetime.Token;
            try
            {
                await EnsurePermissionAsync(lifetime);
                lifetime.ThrowIfCancellationRequested();
                // 4E 的 VST 接口独立打开相机；企业服务绑定仅作辅助，不阻塞定位初始化。
                // The 4E VST API opens the camera independently; enterprise service binding is auxiliary and does not block
                // localization startup.
                _ = ObserveEnterpriseBindingAsync(lifetime);
                lifetime.ThrowIfCancellationRequested();
                if (generation != m_Generation) throw new OperationCanceledException();
                m_Opened = PXR_Enterprise.OpenVSTCamera();
                if (!m_Opened) throw new InvalidOperationException("PICO 4E OpenVSTCamera 失败，请检查企业相机授权和设备型号。");
                m_Ready = true;
                m_FirstFrameLogged = false;
                m_LastWarning = 0;
                MixedSpaceLogger.Log("[MixedSpacePico4EAdapter] VST 相机打开成功。", MixedSpaceLogger.LoggingLevel.Verbose);
                return true;
            }
            catch { if (generation == m_Generation) await ShutdownAsync(); throw; }
#else
            throw new PlatformNotSupportedException("PICO 4E 企业相机仅支持 Android 真机。");
#endif
        }

        private static async Task<bool> WaitAsync(Task<bool> operation, CancellationToken token)
        {
            var canceled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (token.Register(() => canceled.TrySetCanceled())) return await await Task.WhenAny(operation, canceled.Task);
        }

        private async Task ObserveEnterpriseBindingAsync(CancellationToken token)
        {
            try
            {
                if (!PXR_Enterprise.InitEnterpriseService())
                {
                    MixedSpaceLogger.LogWarning("[MixedSpacePico4EAdapter] 企业服务初始化未成功，继续检查 VST 相机。");
                    return;
                }
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    timeout.CancelAfter(m_Profile.StartupTimeoutMilliseconds);
                    var bound = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    PXR_Enterprise.BindEnterpriseService(value => bound.TrySetResult(value));
                    bool success = await WaitAsync(bound.Task, timeout.Token);
                    token.ThrowIfCancellationRequested();
                    if (success) MixedSpaceLogger.Log("[MixedSpacePico4EAdapter] 企业服务绑定成功。", MixedSpaceLogger.LoggingLevel.Verbose);
                    else MixedSpaceLogger.LogWarning("[MixedSpacePico4EAdapter] 企业服务绑定失败；VST 相机结果独立判断。");
                }
            }
            catch (OperationCanceledException)
            {
                if (!token.IsCancellationRequested)
                    MixedSpaceLogger.LogWarning("[MixedSpacePico4EAdapter] 企业服务绑定等待超时，不中断 VST 定位。");
            }
            catch (Exception e)
            {
                if (!token.IsCancellationRequested)
                    MixedSpaceLogger.LogWarning("[MixedSpacePico4EAdapter] 企业服务绑定异常，不中断 VST 定位：" + e.Message);
            }
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        private async Task EnsurePermissionAsync(CancellationToken token)
        {
            if (UnityEngine.Android.Permission.HasUserAuthorizedPermission(UnityEngine.Android.Permission.Camera)) return;
            m_PermissionPending = true;
            try
            {
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    timeout.CancelAfter(60000);
                    var result = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    var callbacks = new UnityEngine.Android.PermissionCallbacks();
                    callbacks.PermissionGranted += _ => result.TrySetResult(true);
                    callbacks.PermissionDenied += _ => result.TrySetResult(false);
                    callbacks.PermissionDeniedAndDontAskAgain += _ => result.TrySetResult(false);
                    UnityEngine.Android.Permission.RequestUserPermission(UnityEngine.Android.Permission.Camera, callbacks);
                    bool granted;
                    try
                    {
                        granted = await WaitAsync(result.Task, timeout.Token);
                        await Task.Yield();
                        while (m_Paused) await Task.Delay(20, timeout.Token);
                    }
                    catch (OperationCanceledException) when (!token.IsCancellationRequested)
                    { throw new TimeoutException("PICO 4E 相机权限或返回前台等待超时。"); }
                    token.ThrowIfCancellationRequested();
                    if (!granted || !UnityEngine.Android.Permission.HasUserAuthorizedPermission(UnityEngine.Android.Permission.Camera))
                        throw new UnauthorizedAccessException("PICO 4E 相机权限未授予。请确认 Manifest 的 CAMERA 声明并在设备允许相机访问。");
                }
            }
            finally { m_PermissionPending = false; }
        }
#endif

        public async Task<IMixedSpacePlatformFrame> CaptureFrameAsync(CancellationToken token)
        {
            await m_CaptureGate.WaitAsync(token);
            try
            {
                if (!m_Ready || m_Paused || !m_Camera) return EmptyFrame();
                int generation = m_Generation, width = m_Profile.Width, height = m_Profile.Height;
                Vector3 position = m_Camera.position;
                Quaternion rotation = m_Camera.rotation * Quaternion.Euler(0, 0, 180);
                long timestamp = Now();
                // 禁止把 Transform 或 ScriptableObject 的读取移入工作线程。
                // Do not move Transform or ScriptableObject reads onto the worker thread.
                string failure = null;
                var capture = await Task.Run(() => ReadCapture(width, height, position, rotation, timestamp, out failure), token);
                if (token.IsCancellationRequested || generation != m_Generation || !m_Ready)
                {
                    capture?.Capture?.Dispose();
                    token.ThrowIfCancellationRequested();
                    return EmptyFrame();
                }
                token.ThrowIfCancellationRequested();
                if (generation != m_Generation || !m_Ready) return EmptyFrame();
                if (capture == null && Now() - m_LastWarning > 5000000000L)
                {
                    m_LastWarning = Now();
                    MixedSpaceLogger.LogWarning("[MixedSpacePico4EAdapter] 未取得有效 VST 图像：" + failure);
                }
                if (capture != null && !m_FirstFrameLogged)
                {
                    m_FirstFrameLogged = true;
                    MixedSpaceLogger.Log($"[MixedSpacePico4EAdapter] 首个有效定位帧：{width}×{height}，Luminance8。", MixedSpaceLogger.LoggingLevel.Verbose);
                }
                return capture ?? EmptyFrame();
            }
            finally { m_CaptureGate.Release(); }
        }

        private static IMixedSpacePlatformFrame ReadCapture(int width, int height, Vector3 position, Quaternion rotation, long timestamp, out string failure)
        {
            int code = PXR_Enterprise.AcquireVSTCameraFrameAntiDistortion(width, height, out Frame frame);
            failure = $"code={code}, size={frame.width}×{frame.height}, expected={width}×{height}, status={frame.status}, bytes={frame.datasize}, nullData={frame.data == IntPtr.Zero}";
            if (code != 0 || frame.data == IntPtr.Zero || frame.width != width || frame.height != height || frame.status != 1) return null;
            int count = checked(width * height);
            if (frame.datasize != count * 3L && frame.datasize != count * 4L) return null;
            int channels = (int)(frame.datasize / count);
            var rgb = new byte[(int)frame.datasize];
            Marshal.Copy(frame.data, rgb, 0, rgb.Length);
            var pixels = new byte[count];
            for (int i = 0; i < count; ++i) pixels[i] = rgb[i * channels + 1];
            var parameters = PXR_Enterprise.GetCameraParameters();
            var intrinsics = new Vector4((float)parameters.fx, (float)parameters.fy, (float)parameters.cx, (float)parameters.cy);
            failure = $"内参无效：fx={intrinsics.x}, fy={intrinsics.y}, cx={intrinsics.z}, cy={intrinsics.w}";
            for (int i = 0; i < 4; i++) if (float.IsNaN(intrinsics[i]) || float.IsInfinity(intrinsics[i])) return null;
            if (intrinsics.x <= 0 || intrinsics.y <= 0) return null;
            failure = null;
            var profile = new MixedSpaceCaptureProfile(width, height, 1, MixedSpacePixelFormat.Luminance8,
                intrinsics, position, rotation, timestamp, Quaternion.identity);
            return new MixedSpacePlatformFrame(new MixedSpaceTrackingSnapshot(1), new MixedSpaceCameraCapture(profile, new MixedSpaceManagedPixelSource(pixels)));
        }

        public MixedSpacePlatformPoseSample SamplePose()
        {
            bool tracking = false;
#if UNITY_ANDROID && !UNITY_EDITOR
            tracking = m_Ready && !m_Paused && PXR_Enterprise.GetHeadTrackingConfidence() == 1;
#endif
            return new MixedSpacePlatformPoseSample { IsTracking = tracking && m_Camera, TimestampNanoseconds = Now(),
                TimestampSource = MixedSpaceTrackingTimestampSource.HostMonotonic,
                Position = m_Camera ? m_Camera.position : Vector3.zero, Rotation = m_Camera ? m_Camera.rotation : Quaternion.identity };
        }

        private static long Now() => (long)(Stopwatch.GetTimestamp() * (1000000000.0 / Stopwatch.Frequency));
        private static IMixedSpacePlatformFrame EmptyFrame() => new MixedSpacePlatformFrame(new MixedSpaceTrackingSnapshot(0), null);

        public Task ShutdownAsync()
        {
            if (m_Stop != null && !m_Stop.IsCompleted) return m_Stop;
            return m_Stop = StopCoreAsync();
        }

        private async Task StopCoreAsync()
        {
            ++m_Generation;
            m_Ready = false;
            m_Lifetime?.Cancel();
            m_Lifetime?.Dispose();
            m_Lifetime = null;
            await m_CaptureGate.WaitAsync();
            try
            {
#if UNITY_ANDROID && !UNITY_EDITOR
                if (m_Opened && !PXR_Enterprise.CloseVSTCamera()) throw new InvalidOperationException("PICO 4E 相机关闭失败，保留占用状态以阻止重复打开。");
#endif
                m_Opened = false;
                if (ReferenceEquals(s_Owner, this)) s_Owner = null;
            }
            finally { m_CaptureGate.Release(); }
        }

        private async void OnApplicationPause(bool paused)
        {
            if (m_Paused == paused) return;
            m_Paused = paused;
            if (m_PermissionPending) return;
            try
            {
                if (paused) { m_Resume = m_Ready; await ShutdownAsync(); }
                else if (m_Resume && isActiveAndEnabled) { m_Resume = false; await StartAsync(CancellationToken.None); }
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { UnityEngine.Debug.LogException(e); }
        }

        private async void OnDisable()
        {
            m_Resume = false;
            try { await ShutdownAsync(); }
            catch (Exception e) { UnityEngine.Debug.LogException(e); }
        }
    }
}
