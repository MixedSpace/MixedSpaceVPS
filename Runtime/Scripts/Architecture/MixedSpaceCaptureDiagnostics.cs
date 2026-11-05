using System;
using System.IO;
using UnityEngine;
using MixedSpace.XR;
using MixedSpace.ServiceContracts;

namespace MixedSpace
{
    // Explicit local diagnostics only; never persist a developer token or server error body.
    internal static class MixedSpaceCaptureDiagnostics
    {
        private static int s_Count;
        internal static void Save(MixedSpaceCaptureProfile profile, byte[] image, SDKLocalizeRequest request, SDKLocalizeResult result)
        {
            try
            {
                string root = Path.Combine(Application.persistentDataPath, "capture-diagnostics");
                if (s_Count >= 24 || !File.Exists(Path.Combine(Application.persistentDataPath, "capture-diagnostics.enabled"))) return;
                Directory.CreateDirectory(root);
                string stem = profile.CapturedAtNanoseconds + "-" + (++s_Count);
                request.token = null;
                result.error = result.error == "none" ? "none" : "redacted";
                int encodedWidth = 0, encodedHeight = 0;
                if (image.Length >= 24 && image[0] == 137 && image[1] == 80 && image[2] == 78 && image[3] == 71)
                {
                    encodedWidth = ReadInt(image, 16); encodedHeight = ReadInt(image, 20);
                }
                var entry = new Entry {
                    timestampNanoseconds = profile.CapturedAtNanoseconds,
                    sourceWidth = profile.PixelWidth, sourceHeight = profile.PixelHeight,
                    encodedWidth = encodedWidth, encodedHeight = encodedHeight,
                    intrinsics = profile.CameraIntrinsics, trackerPosition = profile.TrackerPosition,
                    trackerRotation = profile.TrackerRotation, displayRotation = profile.DisplayRotation,
                    request = request, result = result
                };
                File.WriteAllBytes(Path.Combine(root, stem + ".png"), image);
                File.WriteAllText(Path.Combine(root, stem + ".json"), JsonUtility.ToJson(entry, true));
                Debug.Log($"[CaptureDiagnostics] sample={s_Count} input={profile.PixelWidth}x{profile.PixelHeight} encoded={encodedWidth}x{encodedHeight} K={profile.CameraIntrinsics} solver={request.solverType} success={result.success}");
            }
            catch (Exception e) { Debug.LogWarning("[CaptureDiagnostics] Cannot save sample: " + e.GetType().Name); }
        }
        private static int ReadInt(byte[] bytes, int i) => (bytes[i] << 24) | (bytes[i+1] << 16) | (bytes[i+2] << 8) | bytes[i+3];
        [Serializable] private sealed class Entry
        {
            public long timestampNanoseconds;
            public int sourceWidth, sourceHeight, encodedWidth, encodedHeight;
            public Vector4 intrinsics;
            public Vector3 trackerPosition;
            public Quaternion trackerRotation, displayRotation;
            public SDKLocalizeRequest request;
            public SDKLocalizeResult result;
        }
    }
}