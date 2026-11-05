using System;
using MixedSpace.XR;
using UnityEngine;

namespace MixedSpace
{
    /// <summary>
    /// 原生引擎能力目录，上层仅选择所需的窄接口；应用公共契约不直接暴露原生 ABI 入口。
    /// Capability directory for the native engine. Higher layers choose the one
    /// narrow capability they need; native ABI entry points never form the public
    /// application contract.
    /// </summary>
    public interface IMixedSpaceNativeCore
    {
        IMixedSpaceNativeRuntime Runtime { get; }
        IMixedSpaceNativeMapStore Maps { get; }
        IMixedSpaceNativeFrameEncoder FrameEncoder { get; }
        IMixedSpaceNativeLocalizer Localizer { get; }
        IMixedSpaceNativeGeoTransform GeoTransform { get; }
        IMixedSpaceNativeMappingWorkspace Mapping { get; }
    }

    public interface IMixedSpaceNativeRuntime
    {
        void ApplyImagePixelBudget(int maximumPixels);
        int ResolveLicenseLevel(string developerToken);
        void AttachLogSink(IntPtr callback);
    }

    public interface IMixedSpaceNativeMapStore
    {
        bool Import(int mapKey, byte[] payload);
        void Evict(int mapKey);
        Vector3[] ReadSparsePoints(int mapKey);
    }

    public interface IMixedSpaceNativeFrameEncoder
    {
        byte[] Encode(
            byte[] pixels,
            int width,
            int height,
            int channels,
            bool prepareForMatching = false);
    }

    /// <summary>
    /// 一次设备端定位尝试所使用的不可变约束。
    /// Immutable constraints for one on-device localization attempt.
    /// </summary>
    public sealed class MixedSpaceNativeLocalizationQuery
    {
        private MixedSpaceNativeLocalizationQuery(
            MixedSpaceSolverType solver,
            bool hasPrior,
            Vector3 priorPosition,
            int neighborCount,
            float radius)
        {
            Solver = solver;
            HasPrior = hasPrior;
            PriorPosition = priorPosition;
            NeighborCount = neighborCount;
            Radius = radius;
        }

        public MixedSpaceSolverType Solver { get; }
        public bool HasPrior { get; }
        public Vector3 PriorPosition { get; }
        public int NeighborCount { get; }
        public float Radius { get; }

        public static MixedSpaceNativeLocalizationQuery Unconstrained(
            MixedSpaceSolverType solver)
        {
            return new MixedSpaceNativeLocalizationQuery(
                solver,
                false,
                Vector3.zero,
                0,
                0f);
        }

        public static MixedSpaceNativeLocalizationQuery Around(
            Vector3 priorPosition,
            int neighborCount,
            float radius)
        {
            if (neighborCount < 0)
                throw new ArgumentOutOfRangeException(
                    nameof(neighborCount),
                    "Neighbor count cannot be negative.");
            if (radius < 0f)
                throw new ArgumentOutOfRangeException(nameof(radius), "Prior radius cannot be negative.");

            return new MixedSpaceNativeLocalizationQuery(
                MixedSpaceSolverType.Prior,
                true,
                priorPosition,
                neighborCount,
                radius);
        }
    }

    public interface IMixedSpaceNativeLocalizer
    {
        MixedSpaceLocalizationObservation Resolve(
            MixedSpaceCaptureProfile profile,
            IntPtr pixelBuffer,
            MixedSpaceNativeLocalizationQuery query);
    }

    public readonly struct MixedSpaceEarthPosition
    {
        public MixedSpaceEarthPosition(double x, double y, double z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public double X { get; }
        public double Y { get; }
        public double Z { get; }
    }

    public readonly struct MixedSpaceGeodeticPosition
    {
        public MixedSpaceGeodeticPosition(
            double latitude,
            double longitude,
            double altitude)
        {
            Latitude = latitude;
            Longitude = longitude;
            Altitude = altitude;
        }

        public double Latitude { get; }
        public double Longitude { get; }
        public double Altitude { get; }
    }

    public interface IMixedSpaceNativeGeoTransform
    {
        bool TryMapToEarth(
            Vector3 mapPosition,
            double[] mapToEarth,
            out MixedSpaceEarthPosition earthPosition);

        bool TryMapToGeodetic(
            Vector3 mapPosition,
            double[] mapToEarth,
            out MixedSpaceGeodeticPosition geodeticPosition);

        bool TryGeodeticToMap(
            MixedSpaceGeodeticPosition geodeticPosition,
            Quaternion earthRotation,
            double[] mapToEarth,
            out Vector3 mapPosition,
            out Quaternion mapRotation);
    }

    public sealed class MixedSpaceMappingFrame
    {
        public MixedSpaceMappingFrame(
            IntPtr pixelBuffer,
            int width,
            int height,
            int channels,
            Vector4 intrinsics,
            Vector3 position,
            Quaternion rotation)
        {
            if (pixelBuffer == IntPtr.Zero)
                throw new ArgumentException("Pixel buffer is required.", nameof(pixelBuffer));
            if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
            if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
            if (channels <= 0) throw new ArgumentOutOfRangeException(nameof(channels));

            PixelBuffer = pixelBuffer;
            Width = width;
            Height = height;
            Channels = channels;
            Intrinsics = intrinsics;
            Position = position;
            Rotation = rotation;
        }

        public IntPtr PixelBuffer { get; }
        public int Width { get; }
        public int Height { get; }
        public int Channels { get; }
        public Vector4 Intrinsics { get; }
        public Vector3 Position { get; }
        public Quaternion Rotation { get; }
    }

    public interface IMixedSpaceNativeMappingWorkspace
    {
        int CapturedFrameCount { get; }
        bool Append(MixedSpaceMappingFrame frame);
        byte[] BuildPayload(string workspacePath);
        Vector3[] ReadPreviewPoints();
        void ResetWorkspace();
    }

    /// <summary>
    /// 需要 SDK 原生能力目录的服务实现此契约；SDK 为所有使用方注入同一实例。
    /// Capability implemented by runtime services that need the SDK-owned native
    /// directory. The SDK injects the same instance into every consumer.
    /// </summary>
    public interface IMixedSpaceNativeCoreConsumer
    {
        void SetNativeCore(IMixedSpaceNativeCore nativeCore);
    }
}
