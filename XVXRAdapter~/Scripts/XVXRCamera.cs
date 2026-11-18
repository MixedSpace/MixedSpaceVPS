#if UNITY_EDITOR || UNITY_ANDROID
using MixedSpace.XR;
using MixedSpace;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using static UnityEngine.XR.ARSubsystems.XRCpuImage;
using System.IO;
using XvXR.Engine;
namespace MixedSpace.XR
{

/// <summary>
/// XVXR 示例的 RGB 标定、图像采集与时间戳位姿查询封装，不是 MonoBehaviour。
/// 持有固定的托管采集缓冲区，所有者应在关闭适配器时调用 Dispose 释放。
/// XVXR RGB calibration, acquisition and timestamped pose-query helper, not a MonoBehaviour. It
/// pins managed capture buffers; the owning adapter must call Dispose on shutdown.
/// </summary>
public class XVXRCamera
{
    private Texture2D tex = null;

    private IntPtr pixelPtr;
    private GCHandle pixelHandle;
        private byte[] pixel8; // 托管采集缓冲区；从 YUV 数据提取亮度平面供定位使用。 / Managed acquisition buffer; extract luminance from YUV for localization.
        public double rgbTimestamp = 0;
    private int lastWidth = 0;
    private int lastHeight = 0;

      
    private bool ifInit = false;

    private bool m_HasRgbCalibration;

    private Vector4 m_RgbIntrinsics;

    private Vector3 m_RgbTranslation;

    private Quaternion m_RgbRotation = Quaternion.identity;

    private readonly double[] m_PoseData = new double[7];

        internal void Dispose()
        {
            if (pixelHandle.IsAllocated) pixelHandle.Free();
            pixelPtr = IntPtr.Zero;
            pixel8 = null;
            lastWidth = 0;
            lastHeight = 0;
            ifInit = false;
            rgbTimestamp = 0;
            m_HasRgbCalibration = false;
            m_RgbIntrinsics = Vector4.zero;
            m_RgbTranslation = Vector3.zero;
            m_RgbRotation = Quaternion.identity;
        }

        public Vector4 _ReadRgbCalibration()
        {
#if UNITY_EDITOR
            return Vector4.zero;
#else
            if (m_HasRgbCalibration) return m_RgbIntrinsics;
            if (!API.xslam_ready()) return Vector4.zero;
            API.rgb_calibration rgb_Calibration = default(API.rgb_calibration);
            if (API.readRGBCalibration(ref rgb_Calibration))
            {
                if (rgb_Calibration.intrinsic720.K == null || rgb_Calibration.intrinsic720.K.Length < 4)
                    return Vector4.zero;
                float fx = (float)rgb_Calibration.intrinsic720.K[0];
                float fy = (float)rgb_Calibration.intrinsic720.K[1];
                float cx = (float)rgb_Calibration.intrinsic720.K[2];
                float cy = (float)rgb_Calibration.intrinsic720.K[3];

                Vector4 intrinsics = new Vector4(fx, fy, cx, cy);

                for (int i = 0; i < 4; i++)

                {

                    if (float.IsNaN(intrinsics[i]) || float.IsInfinity(intrinsics[i]))

                        return Vector4.zero;

                }

                if (fx <= 0f || fy <= 0f)

                    return Vector4.zero;

                double[] r = rgb_Calibration.extrinsic.rotation;

                double[] t = rgb_Calibration.extrinsic.translation;

                if (!ValidValues(r, 9) || !ValidValues(t, 3))

                    return Vector4.zero;



                // 与 RGBRecord 相同的 Y 轴反射：R_unity = D * R_native * D。
                // Same Y-axis reflection as RGBRecord: R_unity = D * R_native * D.

                Vector3 right = new Vector3((float)r[0], -(float)r[3], (float)r[6]);

                Vector3 up = new Vector3(-(float)r[1], (float)r[4], -(float)r[7]);

                Vector3 forward = new Vector3((float)r[2], -(float)r[5], (float)r[8]);

                if (Mathf.Abs(right.sqrMagnitude - 1f) > 0.02f ||

                    Mathf.Abs(up.sqrMagnitude - 1f) > 0.02f ||

                    Mathf.Abs(forward.sqrMagnitude - 1f) > 0.02f ||

                    Mathf.Abs(Vector3.Dot(right, up)) > 0.02f ||

                    Mathf.Abs(Vector3.Dot(right, forward)) > 0.02f ||

                    Mathf.Abs(Vector3.Dot(up, forward)) > 0.02f ||

                    Vector3.Dot(right, Vector3.Cross(up, forward)) < 0.98f)

                    return Vector4.zero;



                m_RgbIntrinsics = intrinsics;

                m_RgbTranslation = new Vector3((float)t[0], -(float)t[1], (float)t[2]);

                m_RgbRotation = Quaternion.LookRotation(forward, up);

                m_HasRgbCalibration = true;

                MixedSpaceLogger.Log($"[VPS2-RGB] calibrated translation={m_RgbTranslation.ToString("F4")} rotation={m_RgbRotation.ToString("F4")} clock=NativeHostNanoseconds", MixedSpaceLogger.LoggingLevel.Verbose);

                return m_RgbIntrinsics;
            }
           
            return Vector4.zero;
#endif
        }


        internal bool TryGetRgbExtrinsics(out Vector3 translation, out Quaternion rotation)

        {

            if (!m_HasRgbCalibration) _ReadRgbCalibration();

            translation = m_RgbTranslation;

            rotation = m_RgbRotation;

            return m_HasRgbCalibration;

        }



        internal bool TryGetHeadPose(bool atImageTimestamp, out Vector3 position,

            out Quaternion rotation, out long timestampNanoseconds)

        {

            position = Vector3.zero;

            rotation = Quaternion.identity;

            timestampNanoseconds = 0;

#if UNITY_ANDROID && !UNITY_EDITOR

            if (!API.xslam_ready()) return false;

            if (atImageTimestamp)

            {

                // 封装以秒返回 ColorImage.hostTimestamp，get_pose_at 接收同一值，不再次换算。
                // The wrapper returns ColorImage.hostTimestamp in seconds and accepts it unchanged in get_pose_at.

                if (double.IsNaN(rgbTimestamp) || double.IsInfinity(rgbTimestamp) ||

                    rgbTimestamp <= 0d || rgbTimestamp >= long.MaxValue / 1000000000d ||

                    !API.xslam_get_pose_at(m_PoseData, rgbTimestamp))

                    return false;

                timestampNanoseconds = (long)(rgbTimestamp * 1000000000d);

            }

            else

            {

                // 现有封装已将位姿主机时间乘以 1e9；prediction=0 采样当前位姿。
                // The installed wrapper already multiplies the pose host timestamp by 1e9; prediction=0 samples the current
                // pose.

                if (!API.xslam_get_pose_prediction(m_PoseData, ref timestampNanoseconds, 0d))

                    return false;

            }

            if (timestampNanoseconds <= 0 || !ValidValues(m_PoseData, 7))

                return false;

            Quaternion headRotation = new Quaternion(-(float)m_PoseData[0], (float)m_PoseData[1],

                -(float)m_PoseData[2], (float)m_PoseData[3]);

            float normSquared = Quaternion.Dot(headRotation, headRotation);

            if (normSquared < 0.9f || normSquared > 1.1f)

                return false;

            position = new Vector3((float)m_PoseData[4], -(float)m_PoseData[5], (float)m_PoseData[6]);

            rotation = headRotation.normalized;

            return true;

#else

            return false;

#endif

        }



        private static bool ValidValues(double[] values, int count)

        {

            if (values == null || values.Length < count) return false;

            for (int i = 0; i < count; i++)

            {

                if (double.IsNaN(values[i]) || double.IsInfinity(values[i]) ||

                    values[i] > float.MaxValue || values[i] < -float.MaxValue)

                    return false;

            }

            return true;

        }



        public ParaTexture UpdateGetPara()
    {
        ParaTexture paraTexture = new ParaTexture() { width = 0, height = 0, texture = null };
#if UNITY_EDITOR
        return paraTexture;
#endif

        if (API.xslam_ready() && XvXR.Engine.XvDeviceManager.Manager != null && XvXR.Engine.XvDeviceManager.Manager.isRgbOn())
        {
            if (!ifInit)
            {
                if (!API.xslam_set_rgb_resolution(1))
                    return paraTexture;

                ifInit = true;
            }

            int width = API.xslam_get_rgb_width();
            int height = API.xslam_get_rgb_height();
            if (width > 0 && height > 0)
            {
                if (lastWidth != width || lastHeight != height)
                {
                    try
                    {
                        double r = 1.0;
                        int w = (int)(width * r);
                        int h = (int)(height * r);
                       
                        try
                        {
                                if (pixelHandle.IsAllocated)
                                    pixelHandle.Free();
                            }
                        catch { }
                            int yuvBufferSize = width * height * 3 / 2; // YUV420 数据布局：Y、U、V 平面。 / YUV420: Y + U + V
                            pixel8 = new byte[yuvBufferSize];
                            pixelHandle = GCHandle.Alloc(pixel8, GCHandleType.Pinned);
                        pixelPtr = pixelHandle.AddrOfPinnedObject();

                    }
                    catch (Exception e)
                    {
                        return paraTexture;
                    }

                    lastWidth = width;
                    lastHeight = height;

                }

                    try
                    {
                        if (API.xslam_get_rgb_image_YUV(pixelPtr, width, height, ref rgbTimestamp))
                        {
                             var  yChannel = new byte[width * height]; // 提取 Y 平面，作为单通道亮度图像。 / Extract the Y plane as a single-channel luminance image.
                                Array.Copy(pixel8, yChannel, width * height);
                                paraTexture.width = width;
                            paraTexture.height = height;
                            paraTexture.raw = yChannel;
                            return paraTexture;
                        }
                    }
                    catch (Exception e)
                    {
                        return paraTexture;
                    }
            }
        }
        return paraTexture;
    }
}

}
#endif
