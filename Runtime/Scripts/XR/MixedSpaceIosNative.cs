#if UNITY_IOS && !UNITY_EDITOR
using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace MixedSpace
{
    // iOS 定位插件 ABI 声明，返回布局不含 bool，添加 bool 会改变 ABI；本文件不参与 Android 或编辑器编译。
    // Native ABI declarations for the iOS localization plugin. The return layout excludes a bool field; adding one
    // changes the native ABI. This file is excluded from Android and Editor compilations.
    [StructLayout(LayoutKind.Sequential)]
    internal struct MixedSpaceIosLocalizationResult
    {
        public int mapId;
        public Vector3 position;
        public Quaternion rotation;
        public int confidence;
        public bool isSuccess => mapId > 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MixedSpaceIosCaptureResult
    {
        public int captureSize;
        public int connected;
    }

    internal static class MixedSpaceIosNative
    {
        static MixedSpaceIosNative()
        {
            // 首次原生调用前验证按值返回的 ABI 布局。
            // Validate the plugin's by-value return ABI before the first native call.
            if (Marshal.SizeOf<MixedSpaceIosLocalizationResult>() != 36 ||
                Marshal.SizeOf<MixedSpaceIosCaptureResult>() != 8)
                throw new PlatformNotSupportedException("Unexpected legacy iOS localization ABI layout.");
        }

        private const string Assembly = "__Internal";

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
        public static extern MixedSpaceIosLocalizationResult icvLocalize(int n, IntPtr handles, int width,
        int height, ref Vector4 intrinsics, IntPtr pixels, int channels, int solverType, ref Quaternion cameraRotation);

        [DllImport(Assembly, CallingConvention = CallingConvention.Cdecl)]
        public static extern MixedSpaceIosLocalizationResult icvLocalizePrior(int n, IntPtr handles, int width,
            int height, ref Vector4 intrinsics, IntPtr pixels, int channels, ref Vector3 priorPos, int priorNNCount, float priorRadius);

        [DllImport(Assembly, CallingConvention = CallingConvention.Cdecl)]
        public static extern MixedSpaceIosCaptureResult icvCaptureImage(IntPtr capture, int captureSizeMax, IntPtr pixels,
            int width, int height, int channels, int useMatching);

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
