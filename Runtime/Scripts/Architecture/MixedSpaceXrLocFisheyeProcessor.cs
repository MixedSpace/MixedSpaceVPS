using System;
using UnityEngine;

namespace MixedSpace.XR
{
    /// <summary>
    /// Windows 编辑器与 Android ARM64 的鱼眼图像预处理，通过 XrLocUndistortFishEye 校正后再定位。
    /// 适用于使用该处理器的 Rokid、XRany 等适配器。
    /// Fisheye preprocessing for Windows Editor and Android ARM64, using XrLocUndistortFishEye before
    /// localization. Used by adapters such as Rokid and XRany.
    /// </summary>
    public sealed class MixedSpaceXrLocFisheyeProcessor : MixedSpaceFisheyeProcessor
    {
        public enum CalibrationMode { Unconfirmed, ConfirmedInputIntrinsics, ExplicitOutputIntrinsics }
        private readonly CalibrationMode m_Calibration;
        private readonly Vector4 m_OutputIntrinsics;
        private readonly Vector2Int m_CalibrationResolution;

        public MixedSpaceXrLocFisheyeProcessor(MixedSpaceRokidSettings settings)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            m_Calibration = settings.Calibration;
            m_OutputIntrinsics = settings.OutputIntrinsics;
            m_CalibrationResolution = settings.CalibrationResolution;
        }

        /// <summary>
        /// 检查配置并探测 xrloc 版本，使库缺失或版本查询失败在相机启动前暴露。
        /// 此处查询版本，不调用畸变校正函数；校正接口的调用错误仍可能在 Rectify 时出现。
        /// Validates settings and probes the xrloc version before camera startup. This does not invoke
        /// rectification; failures of the rectification entry point can still occur in Rectify.
        /// </summary>
        public override void ValidateAvailability()
        {
            ValidateConfiguration();
            MixedSpaceXrLocVersion version;
            int status = MixedSpaceXrLocNative.GetVersion(out version);
            if (status != (int)MixedSpaceXrLocStatus.Ok)
                throw new InvalidOperationException(
                    "XrLocUndistortFishEye is unavailable: the xrloc library version probe returned " +
                    $"{status}. Check that the xrloc native plugin ships with this build.");
        }

        /// <summary>
        /// 仅验证编辑配置，可在所有平台的 Inspector 安全调用。
        /// Authoring validation only; safe for Inspector use on all platforms.
        /// </summary>
        public void ValidateConfiguration()
        {
            if (m_Calibration != CalibrationMode.ConfirmedInputIntrinsics &&
                m_Calibration != CalibrationMode.ExplicitOutputIntrinsics)
                throw new InvalidOperationException("Confirm XRLoc corrected-image calibration before localization.");
            // 设备内参随帧传入，无需手填尺寸；固定覆盖内参仍需参考尺寸，避免套用于其他分辨率。
            // Device intrinsics travel with the captured frame; no manually entered size is needed. A fixed override still
            // needs its reference size to prevent applying it to another resolution.
            if (m_Calibration == CalibrationMode.ExplicitOutputIntrinsics &&
                (m_CalibrationResolution.x <= 0 || m_CalibrationResolution.y <= 0))
                throw new InvalidOperationException("Manual output intrinsics require their calibration resolution.");
            if (m_Calibration == CalibrationMode.ExplicitOutputIntrinsics) ValidateIntrinsics(m_OutputIntrinsics);
        }

        /// <summary>
        /// 校正图像并使用原生接口返回的输出内参。
        /// </summary>
        public override MixedSpaceRectifiedImage Rectify(byte[] luminance, int width, int height,
            Vector4 intrinsics, Vector4 distortion, float alpha)
        {
            ValidateAvailability();
            int size = checked(width * height);
            if (width <= 0 || height <= 0 || luminance == null || luminance.Length != size)
                throw new ArgumentException("XRLoc requires tightly packed Luminance8 input.");
            if (m_Calibration == CalibrationMode.ExplicitOutputIntrinsics &&
                (m_CalibrationResolution.x != width || m_CalibrationResolution.y != height))
                throw new InvalidOperationException("Frame resolution differs from verified output calibration.");
            ValidateIntrinsics(intrinsics);
            // 校正后为无剩余畸变的针孔图，下游 k1..p2 应为零；由无镜头畸变数据的采集配置实现。
            // The rectified frame is pinhole with no residual distortion: downstream XrLocLocalize must therefore receive
            // k1..p2 = 0, which it already does because the capture profile built from this image carries no lens data.
            var rectified = new byte[size];
            float[] camera = { intrinsics.x, intrinsics.y, intrinsics.z, intrinsics.w };
            float[] fisheye = { distortion.x, distortion.y, distortion.z, distortion.w };
            int status = MixedSpaceXrLocNative.UndistortFishEye(
                rectified, size, luminance, width, height, 1, camera, fisheye, alpha, out int written);
            if (status != (int)MixedSpaceXrLocStatus.Ok)
                throw new InvalidOperationException(
                    $"XrLocUndistortFishEye failed with status {status} " +
                    $"(intrinsics={intrinsics} fisheye k1..k4={distortion} alpha={alpha}).");
            if (written != size)
                throw new InvalidOperationException(
                    $"XrLocUndistortFishEye produced {written} bytes for a {size}-byte frame.");
            Vector4 outputIntrinsics = new Vector4(camera[0], camera[1], camera[2], camera[3]);
            ValidateIntrinsics(outputIntrinsics);
            return new MixedSpaceRectifiedImage(rectified, width, height, outputIntrinsics);
        }

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        private static void ValidateIntrinsics(Vector4 value)
        {
            if (!Finite(value.x) || !Finite(value.y) || !Finite(value.z) || !Finite(value.w) || value.x <= 0 || value.y <= 0)
                throw new ArgumentException("Camera intrinsics are invalid.");
        }

        // 托管验证、原生鱼眼校正及缓冲区所有权移交。
        // Managed validation, native fisheye rectification, buffer ownership transfer.
    }
}
