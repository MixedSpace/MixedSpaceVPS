using System;
using UnityEngine;

namespace MixedSpace.XR
{
    /// <summary>
    /// 设备无关的校正边界，输出必须是拥有独立存储的紧密亮度数据及输出图像内参。所需原生实现缺失时不允许隐式回退到针孔模型。
    /// Device-neutral rectification boundary. Implementations must return owned, tightly packed
    /// luminance pixels and the intrinsics of THAT output image. No implicit pinhole fallback
    /// is allowed when a required native implementation is unavailable.
    /// </summary>
    public abstract class MixedSpaceFisheyeProcessor
    {
        /// <summary>
        /// 原生导出或标定不可用时，应在相机启动前失败。
        /// Fail before camera startup if native exports or calibration are unavailable.
        /// </summary>
        public abstract void ValidateAvailability();

        /// <summary>
        /// 在 Unity 线程调用，输入内存属于本次调用而非相机。原生实现须在整个调用期间固定缓冲区并在 finally 释放，构造输出前检查返回码。
        /// Called on the Unity thread. Input memory belongs to this invocation, not the camera.
        /// A native implementation must pin buffers for the entire call and free them in finally.
        /// It must validate its documented return code before constructing an output.
        /// </summary>
        public abstract MixedSpaceRectifiedImage Rectify(byte[] luminance, int width, int height,
            Vector4 intrinsics, Vector4 distortion, float alpha);
    }

    /// <summary>
    /// 已验证的校正像素及标定组合；Pixels 归调用方所有。
    /// A validated pair of corrected pixels and calibration; caller owns Pixels.
    /// </summary>
    public sealed class MixedSpaceRectifiedImage
    {
        public byte[] Pixels { get; }
        public int Width { get; }
        public int Height { get; }
        public Vector4 Intrinsics { get; }

        public MixedSpaceRectifiedImage(byte[] pixels, int width, int height, Vector4 intrinsics)
        {
            if (width <= 0 || height <= 0 || pixels == null || pixels.Length != checked(width * height))
                throw new ArgumentException("Rectified image must be tightly packed Luminance8.");
            if (!Finite(intrinsics.x) || !Finite(intrinsics.y) || !Finite(intrinsics.z) ||
                !Finite(intrinsics.w) || intrinsics.x <= 0 || intrinsics.y <= 0)
                throw new ArgumentException("Rectified camera intrinsics are invalid.");
            Pixels = pixels;
            Width = width;
            Height = height;
            Intrinsics = intrinsics;
        }

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
