using System;
using UnityEngine;

namespace MixedSpace.XR
{
    /// <summary>
    /// Rokid 编辑配置；运行时使用快照，不应修改共享资产。
    /// Rokid authoring settings. Runtime consumers take snapshots; never modify this shared
    /// asset.
    /// </summary>
    [CreateAssetMenu(menuName = "MixedSpace/Platforms/Rokid Profile", fileName = "DefaultRokidProfile")]
    public sealed class MixedSpaceRokidProfile : ScriptableObject
    {
        [SerializeField, Min(1)] private int m_StartupTimeoutMilliseconds = 10000;
        [SerializeField, Min(1)] private int m_MaxFrameAgeMilliseconds = 500;
        [SerializeField] private Vector3 m_ImageOrientationEuler = new Vector3(0, 0, 180);
        [SerializeField] private MixedSpaceXrLocFisheyeProcessor.CalibrationMode m_Calibration;
        [SerializeField] private Vector4 m_OutputIntrinsics;
        [SerializeField] private Vector2Int m_CalibrationResolution;

        // 每次启动实测图像和位姿的时间单位，避免 SDK/固件切换后沿用错误的固定缩放。
        // Measure both clocks on each startup instead of trusting a fixed SDK/firmware unit.
        public MixedSpaceRokidSettings CreateSettings() => new MixedSpaceRokidSettings(
            m_StartupTimeoutMilliseconds, m_MaxFrameAgeMilliseconds, MixedSpaceCallbackCameraAdapter.TimestampUnit.Auto,
            m_ImageOrientationEuler, m_Calibration, m_OutputIntrinsics, m_CalibrationResolution);
    }

    /// <summary>
    /// 不可变的运行时配置，与 Inspector 实时修改隔离。
    /// Immutable runtime configuration, isolated from live Inspector edits.
    /// </summary>
    public sealed class MixedSpaceRokidSettings
    {
        public int StartupTimeoutMilliseconds { get; }
        public int MaxFrameAgeMilliseconds { get; }
        public MixedSpaceCallbackCameraAdapter.TimestampUnit TimestampUnit { get; }
        public Vector3 ImageOrientationEuler { get; }
        public MixedSpaceXrLocFisheyeProcessor.CalibrationMode Calibration { get; }
        public Vector4 OutputIntrinsics { get; }
        public Vector2Int CalibrationResolution { get; }
        public MixedSpaceRokidSettings(int timeout, int age, MixedSpaceCallbackCameraAdapter.TimestampUnit unit,
            Vector3 orientation, MixedSpaceXrLocFisheyeProcessor.CalibrationMode calibration, Vector4 intrinsics, Vector2Int resolution)
        {
            StartupTimeoutMilliseconds = timeout;
            MaxFrameAgeMilliseconds = age;
            TimestampUnit = unit;
            ImageOrientationEuler = orientation;
            Calibration = calibration;
            OutputIntrinsics = intrinsics;
            CalibrationResolution = resolution;
        }
    }
}
