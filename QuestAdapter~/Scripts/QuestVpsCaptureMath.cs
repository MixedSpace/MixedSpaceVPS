using System;
using UnityEngine;

namespace QuestVPS
{
    // Matches MRUK 81's centered sensor crop, then converts Unity texture rows
    // (bottom first) into the top-first image expected by the VPS solver.
    public static class QuestVpsCaptureMath
    {
        // Top-first pixels use +Y down. Complete MixedSpace's X reflection
        // with this roll to retain the physical RGB camera's right/up axes.
        internal static readonly Quaternion ImageOrientation = Quaternion.Euler(0f, 0f, 180f);

        public static bool TryGetIntrinsics(Vector2 focalLength, Vector2 principalPoint,
            Vector2Int sensorSize, Vector2Int imageSize, out Vector4 intrinsics)
        {
            intrinsics = default;
            if (sensorSize.x <= 0 || sensorSize.y <= 0 || imageSize.x <= 0 || imageSize.y <= 0 ||
                !Finite(focalLength.x) || !Finite(focalLength.y) ||
                !Finite(principalPoint.x) || !Finite(principalPoint.y) ||
                focalLength.x <= 0f || focalLength.y <= 0f)
                return false;

            float scale = Mathf.Max((float)imageSize.x / sensorSize.x, (float)imageSize.y / sensorSize.y);
            float cropWidth = imageSize.x / scale;
            float cropHeight = imageSize.y / scale;
            float cropX = (sensorSize.x - cropWidth) * 0.5f;
            float cropY = (sensorSize.y - cropHeight) * 0.5f;
            intrinsics = new Vector4(focalLength.x * scale, focalLength.y * scale,
                (principalPoint.x - cropX) * scale,
                imageSize.y - 1f - (principalPoint.y - cropY) * scale);
            return Finite(intrinsics.x) && Finite(intrinsics.y) &&
                Finite(intrinsics.z) && Finite(intrinsics.w);
        }

        public static long ToUnixNanoseconds(DateTime utc)
        {
            return checked((utc.ToUniversalTime().Ticks - DateTime.UnixEpoch.Ticks) * 100L);
        }

        public static bool IsRecentFrame(long captured, long now, float maximumAgeSeconds)
        {
            return captured > 0 && captured <= now + 50000000L &&
                now - captured <= maximumAgeSeconds * 1000000000d;
        }

        public static byte Luminance(Color32 color)
        {
            return (byte)((77 * color.r + 150 * color.g + 29 * color.b + 128) >> 8);
        }

        public static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        public static bool ValidPose(Vector3 position, Quaternion rotation)
        {
            if (!Finite(position.x) || !Finite(position.y) || !Finite(position.z) ||
                !Finite(rotation.x) || !Finite(rotation.y) || !Finite(rotation.z) || !Finite(rotation.w))
                return false;
            float length = rotation.x * rotation.x + rotation.y * rotation.y +
                rotation.z * rotation.z + rotation.w * rotation.w;
            return Mathf.Abs(length - 1f) < 0.02f;
        }
    }
}
