using System;
using UnityEngine;

namespace MixedSpace
{
    /// <summary>
    /// Converts between the native map frame, ECEF in meters, and WGS84 in degrees and meters.
    /// </summary>
    internal sealed class MixedSpaceGeographicTransform : IMixedSpaceNativeGeoTransform
    {
        private const double SemiMajorAxis = 6378137.0;
        private const double Flattening = 1.0 / 298.257223563;
        private const double EccentricitySquared = Flattening * (2.0 - Flattening);
        private const double RadiansPerDegree = Math.PI / 180.0;

        public bool TryMapToEarth(
            Vector3 mapPosition,
            double[] mapToEarth,
            out MixedSpaceEarthPosition earthPosition)
        {
            earthPosition = default;
            if (!IsValidTransform(mapToEarth))
                return false;

            double scale = mapToEarth[12];
            double x = mapPosition.x * scale;
            double y = mapPosition.y * scale;
            double z = mapPosition.z * scale;
            double earthX = mapToEarth[0] + mapToEarth[3] * x + mapToEarth[4] * y + mapToEarth[5] * z;
            double earthY = mapToEarth[1] + mapToEarth[6] * x + mapToEarth[7] * y + mapToEarth[8] * z;
            double earthZ = mapToEarth[2] + mapToEarth[9] * x + mapToEarth[10] * y + mapToEarth[11] * z;
            if (!IsFinite(earthX) || !IsFinite(earthY) || !IsFinite(earthZ))
                return false;

            earthPosition = new MixedSpaceEarthPosition(earthX, earthY, earthZ);
            return true;
        }

        public bool TryMapToGeodetic(
            Vector3 mapPosition,
            double[] mapToEarth,
            out MixedSpaceGeodeticPosition geodeticPosition)
        {
            geodeticPosition = default;
            if (!TryMapToEarth(mapPosition, mapToEarth, out MixedSpaceEarthPosition earth))
                return false;

            double horizontal = Math.Sqrt(earth.X * earth.X + earth.Y * earth.Y);
            if (horizontal == 0.0 && earth.Z == 0.0)
                return false;

            double latitude = Math.Atan2(earth.Z, horizontal * (1.0 - EccentricitySquared));
            for (int i = 0; i < 12; i++)
            {
                double sine = Math.Sin(latitude);
                double next = Math.Atan2(
                    earth.Z + EccentricitySquared * PrimeVerticalRadius(sine) * sine,
                    horizontal);
                if (Math.Abs(next - latitude) < 1e-14)
                    break;
                latitude = next;
            }

            double sinLatitude = Math.Sin(latitude);
            // Project the difference along the ellipsoid normal, including at the poles.
            double altitude = horizontal * Math.Cos(latitude) + earth.Z * sinLatitude -
                SemiMajorAxis * Math.Sqrt(1.0 - EccentricitySquared * sinLatitude * sinLatitude);
            if (!IsFinite(altitude))
                return false;

            geodeticPosition = new MixedSpaceGeodeticPosition(
                latitude / RadiansPerDegree,
                Math.Atan2(earth.Y, earth.X) / RadiansPerDegree,
                altitude);
            return true;
        }

        public bool TryGeodeticToMap(
            MixedSpaceGeodeticPosition geodeticPosition,
            Quaternion earthRotation,
            double[] mapToEarth,
            out Vector3 mapPosition,
            out Quaternion mapRotation)
        {
            mapPosition = default;
            mapRotation = Quaternion.identity;
            if (!IsValidTransform(mapToEarth) ||
                !IsFinite(geodeticPosition.Latitude) || Math.Abs(geodeticPosition.Latitude) > 90.0 ||
                !IsFinite(geodeticPosition.Longitude) || !IsFinite(geodeticPosition.Altitude))
                return false;

            double latitude = geodeticPosition.Latitude * RadiansPerDegree;
            double longitude = geodeticPosition.Longitude * RadiansPerDegree;
            double sine = Math.Sin(latitude);
            double radius = PrimeVerticalRadius(sine);
            double horizontal = (radius + geodeticPosition.Altitude) * Math.Cos(latitude);
            double x = horizontal * Math.Cos(longitude) - mapToEarth[0];
            double y = horizontal * Math.Sin(longitude) - mapToEarth[1];
            double z = (radius * (1.0 - EccentricitySquared) + geodeticPosition.Altitude) * sine - mapToEarth[2];

            // Invert the map similarity transform with the transpose of its rotation.
            double scale = mapToEarth[12];
            var position = new Vector3(
                (float)((mapToEarth[3] * x + mapToEarth[6] * y + mapToEarth[9] * z) / scale),
                (float)((mapToEarth[4] * x + mapToEarth[7] * y + mapToEarth[10] * z) / scale),
                (float)((mapToEarth[5] * x + mapToEarth[8] * y + mapToEarth[11] * z) / scale));
            if (!IsFinite(position.x) || !IsFinite(position.y) || !IsFinite(position.z))
                return false;

            var mapToEarthRotation = Quaternion.LookRotation(
                new Vector3((float)mapToEarth[5], (float)mapToEarth[8], (float)mapToEarth[11]),
                new Vector3((float)mapToEarth[4], (float)mapToEarth[7], (float)mapToEarth[10]));
            mapRotation = Quaternion.Inverse(mapToEarthRotation) * earthRotation;
            mapPosition = position;
            return true;
        }

        private static double PrimeVerticalRadius(double sinLatitude) =>
            SemiMajorAxis / Math.Sqrt(1.0 - EccentricitySquared * sinLatitude * sinLatitude);

        private static bool IsValidTransform(double[] transform)
        {
            if (transform == null || transform.Length < 13 || transform[12] <= 0.0)
                return false;
            for (int i = 0; i < 13; i++)
                if (!IsFinite(transform[i]))
                    return false;
            return true;
        }

        private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
