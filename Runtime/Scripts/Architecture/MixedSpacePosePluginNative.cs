#if !UNITY_IOS || UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;

namespace MixedSpace.NativeInterop
{
    [StructLayout(LayoutKind.Sequential)]
    public struct CaptureInfo
    {
        public int captureSize;
        public int connected;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct LocalizeInfo
    {
        public int mapId;
        public Vector3 position;
        public Quaternion rotation;
        public int confidence;
        public bool isSuccess;
    };

    /// <summary>
    /// 非 iOS 平台的 PosePlugin 原生 ABI 声明。调用方负责缓冲区、句柄和返回码处理。
    /// 这些声明不保证原生库已加载，也不作为应用层定位控制接口。
    /// PosePlugin ABI declarations for non-iOS platforms. Callers own buffers, handles and status
    /// interpretation. Declarations do not guarantee library availability and are not application-level localization
    /// controls.
    /// </summary>
    public static class NativeCore
    {
        private const string Assembly =
#if UNITY_IOS && !UNITY_EDITOR
        		"__Internal";
#else
        "PosePlugin";

#endif

        [DllImport(Assembly, CallingConvention = CallingConvention.Cdecl)]
        public static extern void PP_RegisterLogCallback(IntPtr callbackDelegate);

        [DllImport(Assembly, CallingConvention = CallingConvention.Cdecl)]
        public static extern int icvPointsGet(int mapHandle, IntPtr array, int maxCount);

        [DllImport(Assembly, CallingConvention = CallingConvention.Cdecl)]
        public static extern int icvPointsGetCount(int mapHandle);

        [DllImport(Assembly, CallingConvention = CallingConvention.Cdecl)]
        public static extern int icvLoadMap(byte[] buffer);

        [DllImport(Assembly, CallingConvention = CallingConvention.Cdecl)]
        public static extern int icvFreeMap(int mapHandle);

        [DllImport(Assembly, CallingConvention = CallingConvention.Cdecl)]
        public static extern CaptureInfo icvCaptureImage(IntPtr capture, int captureSizeMax, IntPtr pixels,
            int width, int height, int channels, int useMatching);

        [DllImport(Assembly, CallingConvention = CallingConvention.Cdecl)]
        public static extern LocalizeInfo icvLocalize(int n, IntPtr handles, int width,
            int height, ref Vector4 intrinsics, IntPtr pixels, int channels, int solverType, ref Quaternion cameraRotation);

        [DllImport(Assembly, CallingConvention = CallingConvention.Cdecl)]
        public static extern LocalizeInfo icvLocalizePrior(int n, IntPtr handles, int width,
            int height, ref Vector4 intrinsics, IntPtr pixels, int channels, ref Vector3 priorPos, int priorNNCount, float priorRadius);

        [DllImport(Assembly, CallingConvention = CallingConvention.Cdecl)]
        public static extern int icvPosMapToEcef(IntPtr ecef, ref Vector3 map, IntPtr mapToEcef);

        [DllImport(Assembly, CallingConvention = CallingConvention.Cdecl)]
        public static extern int icvPosEcefToWgs84(IntPtr wgs84, IntPtr ecef);

        [DllImport(Assembly, CallingConvention = CallingConvention.Cdecl)]
        public static extern int icvPosWgs84ToEcef(IntPtr ecef, IntPtr wgs84);

        [DllImport(Assembly, CallingConvention = CallingConvention.Cdecl)]
        public static extern int icvPosEcefToMap(out Vector3 map, IntPtr ecef, IntPtr mapToEcef);

        [DllImport(Assembly, CallingConvention = CallingConvention.Cdecl)]
        public static extern int icvRotMapToEcef(out Quaternion ecef, ref Quaternion map, IntPtr mapToEcef);

        [DllImport(Assembly, CallingConvention = CallingConvention.Cdecl)]
        public static extern int icvRotEcefToMap(out Quaternion map, ref Quaternion ecef, IntPtr mapToEcef);

        [DllImport(Assembly, CallingConvention = CallingConvention.Cdecl)]
        public static extern int icvSetInteger([MarshalAs(UnmanagedType.LPStr)] string parameter, int value);

        [DllImport(Assembly, CallingConvention = CallingConvention.Cdecl)]
        public static extern int icvGetInteger([MarshalAs(UnmanagedType.LPStr)] string parameter);

        [DllImport(Assembly, CallingConvention = CallingConvention.Cdecl)]
        public static extern int icvValidateUser([MarshalAs(UnmanagedType.LPStr)] string token);

        [DllImport(Assembly, CallingConvention = CallingConvention.Cdecl)]
        public static extern int icvMapAddImage(IntPtr pixels, int width, int height, int channels, ref Vector4 intrinsics, ref Vector3 pos, ref Quaternion rot);

        [DllImport(Assembly, CallingConvention = CallingConvention.Cdecl)]
        public static extern int icvMapImageGetCount();

        [DllImport(Assembly, CallingConvention = CallingConvention.Cdecl)]
        public static extern int icvMapPrepare([MarshalAs(UnmanagedType.LPStr)] string path);

        [DllImport(Assembly, CallingConvention = CallingConvention.Cdecl)]
        public static extern int icvMapGet(byte[] map);

        [DllImport(Assembly, CallingConvention = CallingConvention.Cdecl)]
        public static extern int icvMapPointsGetCount();

        [DllImport(Assembly, CallingConvention = CallingConvention.Cdecl)]
        public static extern int icvMapPointsGet(IntPtr points, int countMax);

        [DllImport(Assembly, CallingConvention = CallingConvention.Cdecl)]
        public static extern int icvMapResourcesFree();
    }
}
#endif
