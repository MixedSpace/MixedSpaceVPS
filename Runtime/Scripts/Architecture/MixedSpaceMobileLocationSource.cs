using System.Runtime.InteropServices;
using UnityEngine;

namespace MixedSpace
{
    /// <summary>
    /// 核心持有的移动定位适配器，无 ARFoundation 依赖；平台 ABI 细节封装在内部，可随 Android、iOS 或 Harmony 集成独立替换。
    /// Mobile location adapter owned by Core, with no ARFoundation dependency.
    /// Platform ABI details are private and can be
    /// replaced independently when Android, iOS or Harmony integrations evolve.
    /// </summary>
    public sealed class MixedSpaceMobileLocationSource : IMixedSpaceLocationSource
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        private const string AndroidBridgeClass = "com.MixedSpace.nativebindings.Main";
        private AndroidJavaClass m_AndroidBridge;
#endif
        private bool m_Started;

        public bool IsAvailable
        {
            get
            {
#if (UNITY_ANDROID || UNITY_IOS) && !UNITY_EDITOR
                return true;
#else
                return false;
#endif
            }
        }

        public bool IsServiceEnabled
        {
            get
            {
                if (!IsAvailable || !Input.location.isEnabledByUser)
                    return false;

#if UNITY_IOS && !UNITY_EDITOR
                return IosLocationAbi.IsServiceEnabled();
#elif UNITY_ANDROID && !UNITY_EDITOR
                return AndroidBridge.CallStatic<bool>("locationServicesEnabled");
#else
                return false;
#endif
            }
        }

        public bool TryStart()
        {
#if (UNITY_ANDROID || UNITY_IOS) && !UNITY_EDITOR
            // 原生样本要启动更新后才可用，因此启动依据系统/用户设置，而不是当前是否已有样本。
            // Native sample availability becomes true only after updates begin, so startup is gated by the OS/user setting
            // rather than a sample.
            if (!Input.location.isEnabledByUser)
                return false;

#if UNITY_IOS && !UNITY_EDITOR
            IosLocationAbi.Start();
#else
            AndroidBridge.CallStatic("startLocation");
#endif
            m_Started = true;
            return true;
#else
            return false;
#endif
        }

        public bool TryRead(out MixedSpaceLocationSample sample)
        {
            sample = default;
            // 开始更新并不代表原生提供器已经得到定位结果。
            // Starting updates does not mean the native provider has a fix yet.
            if (!m_Started || !IsServiceEnabled)
                return false;

#if UNITY_IOS && !UNITY_EDITOR
            sample = new MixedSpaceLocationSample(
                IosLocationAbi.Latitude(),
                IosLocationAbi.Longitude(),
                IosLocationAbi.Altitude(),
                IosLocationAbi.HorizontalAccuracy(),
                IosLocationAbi.VerticalAccuracy());
            return true;
#elif UNITY_ANDROID && !UNITY_EDITOR
            sample = new MixedSpaceLocationSample(
                AndroidBridge.CallStatic<double>("getLatitude"),
                AndroidBridge.CallStatic<double>("getLongitude"),
                AndroidBridge.CallStatic<double>("getAltitude"),
                AndroidBridge.CallStatic<double>("getHorizontalAccuracy"),
                AndroidBridge.CallStatic<double>("getVerticalAccuracy"));
            return true;
#else
            return false;
#endif
        }

        public void Stop()
        {
            if (!m_Started)
                return;

#if UNITY_IOS && !UNITY_EDITOR
            IosLocationAbi.Stop();
#elif UNITY_ANDROID && !UNITY_EDITOR
            AndroidBridge.CallStatic("stopLocation");
#endif
            m_Started = false;
        }

        public void Dispose()
        {
            Stop();
#if UNITY_ANDROID && !UNITY_EDITOR
            m_AndroidBridge?.Dispose();
            m_AndroidBridge = null;
#endif
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        private AndroidJavaClass AndroidBridge =>
            m_AndroidBridge ?? (m_AndroidBridge = new AndroidJavaClass(AndroidBridgeClass));
#endif

#if UNITY_IOS && !UNITY_EDITOR
        private static class IosLocationAbi
        {
            [DllImport("__Internal", EntryPoint = "startLocation")]
            internal static extern void Start();

            [DllImport("__Internal", EntryPoint = "stopLocation")]
            internal static extern void Stop();

            [DllImport("__Internal", EntryPoint = "getLatitude")]
            internal static extern double Latitude();

            [DllImport("__Internal", EntryPoint = "getLongitude")]
            internal static extern double Longitude();

            [DllImport("__Internal", EntryPoint = "getAltitude")]
            internal static extern double Altitude();

            [DllImport("__Internal", EntryPoint = "getHorizontalAccuracy")]
            internal static extern double HorizontalAccuracy();

            [DllImport("__Internal", EntryPoint = "getVerticalAccuracy")]
            internal static extern double VerticalAccuracy();

            [DllImport("__Internal", EntryPoint = "locationServicesEnabled")]
            [return: MarshalAs(UnmanagedType.I1)]
            internal static extern bool IsServiceEnabled();
        }
#endif
    }
}
