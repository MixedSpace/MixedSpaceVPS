using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MixedSpace.ServiceContracts;
using UnityEngine;

namespace MixedSpace.XR
{
    /// <summary>
    /// 内置云定位服务，网络操作由运行时协调，不挂载为 GameObject 组件。
    /// Built-in cloud localization service. Network work is coordinated through
    /// the runtime and this service is never attached to a GameObject.
    /// </summary>
    public sealed class MixedSpaceCloudLocalization : IMixedSpaceLocalizationProvider
    {
        private readonly IMixedSpaceMapRepository m_Maps;
        private readonly IMixedSpaceNativeCore m_NativeCore;
        private readonly IMixedSpaceRuntimeClock m_Clock;
        private readonly IMixedSpaceCloudContext m_Cloud;
        private readonly Action<float> m_OnProgress;
        private readonly MixedSpaceProviderActivationMode m_ActivationMode;
        private MixedSpaceSolverType m_SolverType;
        private int m_PriorNearestNeighborCountMin;
        private int m_PriorNearestNeighborCountMax;
        private Vector3 m_PriorScale;
        private float m_PriorRadius;
        private SDKMapId[] m_MapIds = Array.Empty<SDKMapId>();
        private int m_PreviouslyLocalizedMapId;

        public MixedSpaceCloudLocalization(
            MixedSpaceCloudOptions options,
            IMixedSpaceMapRepository maps,
            IMixedSpaceNativeCore nativeCore,
            IMixedSpaceRuntimeClock clock,
            IMixedSpaceCloudContext cloud,
            Action<float> onProgress = null)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            m_Maps = maps ?? throw new ArgumentNullException(nameof(maps));
            m_NativeCore = nativeCore ?? throw new ArgumentNullException(nameof(nativeCore));
            m_Clock = clock ?? throw new ArgumentNullException(nameof(clock));
            m_Cloud = cloud ?? throw new ArgumentNullException(nameof(cloud));
            m_OnProgress = onProgress;
            m_ActivationMode = options.ActivationMode;
            m_SolverType = options.SolverType;
            m_PriorNearestNeighborCountMin = options.PriorNearestNeighborCountMin;
            m_PriorNearestNeighborCountMax = options.PriorNearestNeighborCountMax;
            m_PriorScale = options.PriorScale;
            m_PriorRadius = options.PriorRadius;
        }

        public string ProviderId => MixedSpaceProviderIds.Cloud;
        public MixedSpaceProviderActivationMode ActivationMode => m_ActivationMode;

        public Task<bool> ApplyAsync(MixedSpaceProviderPlan plan)
        {
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            m_SolverType = plan.SolverType ?? m_SolverType;
            m_PriorNearestNeighborCountMin =
                plan.PriorNearestNeighborCountMin ?? m_PriorNearestNeighborCountMin;
            m_PriorNearestNeighborCountMax =
                plan.PriorNearestNeighborCountMax ?? m_PriorNearestNeighborCountMax;
            m_PriorScale = plan.PriorScale ?? m_PriorScale;
            m_PriorRadius = plan.PriorRadius ?? m_PriorRadius;

            var maps = new HashSet<int>(m_MapIds.Select(value => value.id));
            if (plan.Additions != null)
                foreach (MixedSpaceMapDefinition map in plan.Additions.Where(map => map != null))
                    maps.Add(map.Key);
            if (plan.Removals != null)
                foreach (MixedSpaceMapDefinition map in plan.Removals.Where(map => map != null))
                    maps.Remove(map.Key);

            m_MapIds = maps.Select(id => new SDKMapId { id = id }).ToArray();
            return Task.FromResult(plan.Removals == null || m_MapIds.Length > 0);
        }

        public async Task<MixedSpaceLocalizationOutcome> EvaluateAsync(
            IMixedSpaceCameraCapture cameraCapture,
            CancellationToken cancellationToken)
        {
            if (cameraCapture == null) throw new ArgumentNullException(nameof(cameraCapture));
            if (m_MapIds.Length == 0)
            {
                MixedSpaceLogger.LogWarning("Cloud localization skipped: no map IDs configured.");
                return MixedSpaceLocalizationOutcome.Missed();
            }

            cancellationToken.ThrowIfCancellationRequested();
            MixedSpaceCaptureProfile profile = cameraCapture.Profile;
            Vector4 intrinsics = profile.CameraIntrinsics;
            int channels = profile.ChannelCount;
            int width = profile.PixelWidth;
            int height = profile.PixelHeight;
            byte[] capture;
            float startedAt = m_Clock.RealtimeSinceStartup;

            using (IMixedSpacePixelLease pixels = cameraCapture.LeasePixels())
            {
                byte[] pixelBytes = pixels.CopyPixels();
                capture = await Task.Run(
                    () => m_NativeCore.FrameEncoder.Encode(
                        pixelBytes,
                        width,
                        height,
                        channels),
                    cancellationToken);
            }

            if (capture == null || capture.Length == 0)
            {
                MixedSpaceLogger.LogWarning($"Cloud encoding produced no image: {width}x{height}, channels={channels}.");
                return MixedSpaceLocalizationOutcome.Missed();
            }

            Quaternion rotation = profile.TrackerRotation * profile.DisplayRotation;
            rotation = MixedSpaceCoordinateFrame.ReflectXAxis(rotation);
            var request = new SDKLocalizeRequest
            {
                token = m_Cloud.DeveloperToken,
                deviceModel = DeviceInfo.DeviceModel,
                fx = intrinsics.x,
                fy = intrinsics.y,
                ox = intrinsics.z,
                oy = intrinsics.w,
                mapIds = m_MapIds,
                qx = rotation.x,
                qy = rotation.y,
                qz = rotation.z,
                qw = rotation.w,
                solverType = m_SolverType == MixedSpaceSolverType.Prior ? 4 : 0
            };

            if (m_SolverType == MixedSpaceSolverType.Prior &&
                m_PreviouslyLocalizedMapId != 0 &&
                m_Maps.TryResolve(
                    m_PreviouslyLocalizedMapId,
                    out MixedSpaceMapRuntimeRecord record))
            {
                Matrix4x4 mapPose = record.Output.ProjectToLocal(
                    profile.TrackerPosition,
                    Quaternion.identity);
                Vector3 priorPosition = record.Placement
                    .RemovePlacement(mapPose)
                    .GetPosition();
                priorPosition = MixedSpaceCoordinateFrame.ReflectXAxis(priorPosition);
                request.priorX = priorPosition.x;
                request.priorY = priorPosition.y;
                request.priorZ = priorPosition.z;
                request.priorNeighborCountMin = m_PriorNearestNeighborCountMin;
                request.priorNeighborCountMax = m_PriorNearestNeighborCountMax;
                request.priorScaleX = m_PriorScale.x;
                request.priorScaleY = m_PriorScale.y;
                request.priorScaleZ = m_PriorScale.z;
                request.priorRadius = m_PriorRadius;
            }
            else
            {
                m_PreviouslyLocalizedMapId = 0;
            }

            IProgress<float> progress = m_OnProgress == null
                ? null
                : new Progress<float>(m_OnProgress);
            SDKLocalizeResult result = await m_Cloud.Client.UploadAsync<
                SDKLocalizeRequest,
                SDKLocalizeResult>(request, capture, progress, cancellationToken);
            float elapsed = m_Clock.RealtimeSinceStartup - startedAt;
            if (result.error != "none" || !result.success || result.map <= 0)
            {
                // 不输出任意服务器错误正文，可能反射出请求凭据。
                // Never emit arbitrary server error text: it may reflect request credentials.
                string errorState = result.error == "none" ? "none" : string.IsNullOrEmpty(result.error) ? "missing" : "server-error";
                MixedSpaceLogger.LogWarning($"Cloud localization not located: error={errorState}, success={result.success}, map={result.map}, searched={m_MapIds.Length}, image={capture.Length} bytes/{DescribeCapture(capture)}, elapsed={elapsed}s.");
                return MixedSpaceLocalizationOutcome.Missed();
            }

            var rotationMatrix = Matrix4x4.identity;
            rotationMatrix.m00 = result.r00; rotationMatrix.m01 = result.r01; rotationMatrix.m02 = result.r02;
            rotationMatrix.m10 = result.r10; rotationMatrix.m11 = result.r11; rotationMatrix.m12 = result.r12;
            rotationMatrix.m20 = result.r20; rotationMatrix.m21 = result.r21; rotationMatrix.m22 = result.r22;
            m_PreviouslyLocalizedMapId = result.map;
            MixedSpaceLogger.Log($"Relocalized to mapId {result.map} in {elapsed} seconds");
            return MixedSpaceLocalizationOutcome.Located(
                new MixedSpaceLocalizationObservation(
                    result.map,
                    new Vector3(result.px, result.py, result.pz),
                    rotationMatrix.rotation,
                    result.confidence));
        }

        public Task ReleaseAsync()
        {
            m_MapIds = Array.Empty<SDKMapId>();
            m_PreviouslyLocalizedMapId = 0;
            return Task.CompletedTask;
        }

        internal static string DescribeCapture(byte[] data)
        {
            if (data != null && data.Length >= 8 && data[0] == 137 && data[1] == 80 && data[2] == 78 && data[3] == 71 &&
                data[4] == 13 && data[5] == 10 && data[6] == 26 && data[7] == 10) return "PNG";
            if (data != null && data.Length >= 3 && data[0] == 255 && data[1] == 216 && data[2] == 255) return "JPEG";
            return "unknown";
        }
    }
}
