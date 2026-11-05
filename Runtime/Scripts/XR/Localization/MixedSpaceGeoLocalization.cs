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
    /// 内置地理定位服务是运行时持有的普通 C# 对象，场景中不需要地理提供器组件。
    /// Built-in geographic localization service. The runtime owns this ordinary
    /// C# object; no geographic provider component is required in the scene.
    /// </summary>
    public sealed class MixedSpaceGeoLocalization : IMixedSpaceLocalizationProvider
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

        public MixedSpaceGeoLocalization(
            MixedSpaceGeoOptions options,
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

        public string ProviderId => MixedSpaceProviderIds.Geo;
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
            {
                foreach (MixedSpaceMapDefinition map in
                    plan.Additions.Where(map => map != null))
                {
                    maps.Add(map.Key);
                }
            }

            if (plan.Removals != null)
            {
                foreach (MixedSpaceMapDefinition map in
                    plan.Removals.Where(map => map != null))
                {
                    maps.Remove(map.Key);
                }
            }

            m_MapIds = maps.Select(id => new SDKMapId { id = id }).ToArray();
            return Task.FromResult(plan.Removals == null || m_MapIds.Length > 0);
        }

        public async Task<MixedSpaceLocalizationOutcome> EvaluateAsync(
            IMixedSpaceCameraCapture cameraCapture,
            CancellationToken cancellationToken)
        {
            if (cameraCapture == null) throw new ArgumentNullException(nameof(cameraCapture));
            if (m_MapIds.Length == 0)
                return MixedSpaceLocalizationOutcome.Missed();

            cancellationToken.ThrowIfCancellationRequested();
            MixedSpaceCaptureProfile profile = cameraCapture.Profile;
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

            if (capture.Length == 0)
                return MixedSpaceLocalizationOutcome.Missed();

            Vector4 intrinsics = profile.CameraIntrinsics;
            Quaternion captureRotation =
                profile.TrackerRotation * profile.DisplayRotation;
            captureRotation = MixedSpaceCoordinateFrame.ReflectXAxis(captureRotation);
            var request = new SDKGeoPoseRequest
            {
                token = m_Cloud.DeveloperToken,
                fx = intrinsics.x,
                fy = intrinsics.y,
                ox = intrinsics.z,
                oy = intrinsics.w,
                mapIds = m_MapIds,
                qx = captureRotation.x,
                qy = captureRotation.y,
                qz = captureRotation.z,
                qw = captureRotation.w,
                solverType = m_SolverType == MixedSpaceSolverType.Prior ? 4 : 0
            };

            if (m_SolverType == MixedSpaceSolverType.Prior &&
                m_PreviouslyLocalizedMapId != 0 &&
                m_Maps.TryResolve(
                    m_PreviouslyLocalizedMapId,
                    out MixedSpaceMapRuntimeRecord previousRecord))
            {
                Matrix4x4 pose = previousRecord.Output.ProjectToLocal(
                    profile.TrackerPosition,
                    Quaternion.identity);
                Vector3 priorPosition = previousRecord.Placement
                    .RemovePlacement(pose)
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
            SDKGeoPoseResult response = await m_Cloud.Client.UploadAsync<
                SDKGeoPoseRequest,
                SDKGeoPoseResult>(request, capture, progress, cancellationToken);
            float elapsed = m_Clock.RealtimeSinceStartup - startedAt;
            if (response.error != "none" || !response.success || response.map <= 0)
            {
                MixedSpaceLogger.Log(
                    $"Geographic localization failed after {elapsed} seconds.");
                return MixedSpaceLocalizationOutcome.Missed();
            }

            if (!m_Maps.TryResolve(response.map, out MixedSpaceMapRuntimeRecord record))
            {
                MixedSpaceLogger.LogError(
                    $"Geographic localization returned unknown map {response.map}.");
                return MixedSpaceLocalizationOutcome.Missed();
            }

            double[] mapToEcef = record.Definition.BuildMapToEcef();
            var ecefRotation = new Quaternion(
                response.quaternion[1],
                response.quaternion[2],
                response.quaternion[3],
                response.quaternion[0]);
            var geodeticPosition = new MixedSpaceGeodeticPosition(
                response.latitude,
                response.longitude,
                response.ellipsoidHeight);
            if (!m_NativeCore.GeoTransform.TryGeodeticToMap(
                geodeticPosition,
                ecefRotation,
                mapToEcef,
                out Vector3 mapPosition,
                out Quaternion mapRotation))
            {
                MixedSpaceLogger.LogError(
                    $"Could not project geographic pose into map {response.map}.");
                return MixedSpaceLocalizationOutcome.Missed();
            }

            m_PreviouslyLocalizedMapId = response.map;
            MixedSpaceLogger.Log(
                $"Geographically relocalized to map {response.map} in {elapsed} seconds.");
            return MixedSpaceLocalizationOutcome.Located(
                new MixedSpaceLocalizationObservation(
                    response.map,
                    mapPosition,
                    mapRotation,
                    0));
        }

        public Task ReleaseAsync()
        {
            m_MapIds = Array.Empty<SDKMapId>();
            m_PreviouslyLocalizedMapId = 0;
            return Task.CompletedTask;
        }
    }
}
