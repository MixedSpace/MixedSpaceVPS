using System;
using MixedSpace.XR;
using UnityEngine;

namespace MixedSpace
{
    /// <summary>
    /// 根据已求解元数据进行无状态地理计算，不持有相机、地图历史或 UI；原生地理接口可独立替换。
    /// Stateless geographic calculation from solved localization metadata.
    /// It owns neither a camera nor map history nor a Unity UI object. The native
    /// geographic port can be replaced independently from localization and views.
    /// </summary>
    public sealed class MixedSpaceGeoPoseProjection
    {
        private readonly IMixedSpaceNativeGeoTransform m_Geography;

        public MixedSpaceGeoPoseProjection(IMixedSpaceNativeGeoTransform geography)
        {
            m_Geography = geography ?? throw new ArgumentNullException(nameof(geography));
        }

        public bool TryProject(MixedSpaceLocalizationSnapshot snapshot,
            Vector3 cameraPosition, Vector3 cameraForward,
            out MixedSpaceGeographicPose pose)
        {
            pose = default;
            if (snapshot == null || !snapshot.HasFix) return false;
            double[] mapToEarth = snapshot.Request.CopyMapToEcef();
            if (mapToEarth == null) return false;

            Matrix4x4 worldPose = snapshot.Solution.WorldPose;
            Matrix4x4 trackerToMap = worldPose.inverse;
            Vector3 origin = worldPose.GetColumn(3);
            return TryProjectMapCoordinates(origin, worldPose.rotation,
                trackerToMap.MultiplyPoint(cameraPosition),
                trackerToMap.MultiplyPoint(cameraPosition + cameraForward), mapToEarth, out pose);
        }

        /// <summary>
        /// 地理原生契约不依赖 Unity 矩阵求值：ECEF 端点转换手性，罗盘纬度查询仍使用原地图点；显示位置是转换后的求解平移，不是相机 GPS。
        /// Native-boundary contract independent of Unity matrix evaluation. Keep
        /// the historical conventions explicit: ECEF endpoints are handedness
        /// converted, but the compass latitude query uses the original map point.
        /// Display position is the converted solved translation, not camera GPS.
        /// </summary>
        internal bool TryProjectMapCoordinates(Vector3 origin, Quaternion rotation,
            Vector3 cameraInMap, Vector3 headingInMap, double[] mapToEarth,
            out MixedSpaceGeographicPose pose)
        {
            pose = default;
            // 点的 D * TRS(point) * D 等价于 X 取反；使用副本避免调用方坐标被重复翻转。
            // For points, D * TRS(point) * D is exactly an X sign flip. Use explicit copies so no caller-owned coordinate
            // can flip twice.
            Vector3 nativeCamera = new Vector3(-cameraInMap.x, cameraInMap.y, cameraInMap.z);
            headingInMap = new Vector3(-headingInMap.x, headingInMap.y, headingInMap.z);

            float bearing = 0f;
            if (m_Geography.TryMapToEarth(nativeCamera, mapToEarth, out var start) &&
                m_Geography.TryMapToEarth(headingInMap, mapToEarth, out var end) &&
                m_Geography.TryMapToGeodetic(cameraInMap, mapToEarth, out var cameraLocation))
            {
                bearing = Bearing(start, end, cameraLocation);
            }

            origin = new Vector3(-origin.x, origin.y, origin.z);
            if (!m_Geography.TryMapToGeodetic(origin, mapToEarth, out var location))
                return false;

            pose = new MixedSpaceGeographicPose(
                location.Latitude,
                location.Longitude,
                location.Altitude,
                bearing,
                new Pose(origin, rotation));
            return true;
        }

        /// <summary>
        /// 将 ECEF 方向投影到当地东/北切平面，不构造矩阵即组合两个旋转；东向负号保留西向为 90 度的既有罗盘约定。
        /// Projects an ECEF direction onto the local east/north tangent basis.
        /// Computes the composition of two rotations without
        /// constructing rotation matrices. The negative-east sign
        /// defines the compass convention: west is 90 degrees.
        /// </summary>
        internal static float Bearing(MixedSpaceEarthPosition start,
            MixedSpaceEarthPosition end, MixedSpaceGeodeticPosition location)
        {
            const double radians = Math.PI / 180.0;
            double latitude = location.Latitude * radians;
            double longitude = location.Longitude * radians;
            double x = (float)(end.X - start.X), y = (float)(end.Y - start.Y), z = (float)(end.Z - start.Z);
            double lengthSquared = x * x + y * y + z * z;
            // 使用 Unity Vector3/Vector2 的归一化阈值。
            // Use Unity Vector3/Vector2 normalization thresholds.
            if (lengthSquared <= 1e-10) return 0f;
            double east = -Math.Sin(longitude) * x + Math.Cos(longitude) * y;
            double north = -Math.Sin(latitude) * Math.Cos(longitude) * x
                           - Math.Sin(latitude) * Math.Sin(longitude) * y
                           + Math.Cos(latitude) * z;
            if (east * east + north * north <= lengthSquared * 1e-10) return 0f;
            double degrees = Math.Atan2(-east, north) / radians;
            return (float)(degrees < 0 ? degrees + 360.0 : degrees);
        }
    }
}
