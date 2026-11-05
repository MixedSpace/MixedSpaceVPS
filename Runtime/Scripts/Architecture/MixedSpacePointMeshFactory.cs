using System;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;

namespace MixedSpace.XR
{
    /// <summary>
    /// 独立点数据转 Unity Mesh 的唯一边界，不修改源数组。
    /// Sole conversion boundary from detached point data to a Unity Mesh.
    /// Source arrays are never modified.
    /// </summary>
    internal static class MixedSpacePointMeshFactory
    {
        public const int NativePreviewPointLimit = 65535;

        public static Mesh Create(
            MixedSpacePointCloudData data,
            string name,
            bool uploadReadOnly)
        {
            if (data == null)
                throw new ArgumentNullException(nameof(data));

            var mesh = new Mesh
            {
                name = string.IsNullOrWhiteSpace(name) ? "Sparse Point Preview" : name,
                indexFormat = data.Count > ushort.MaxValue
                    ? IndexFormat.UInt32
                    : IndexFormat.UInt16
            };
            mesh.vertices = data.Positions;
            mesh.colors32 = data.Colors;
            mesh.SetIndices(
                Enumerable.Range(0, data.Count).ToArray(),
                MeshTopology.Points,
                0);
            mesh.RecalculateBounds();
            if (uploadReadOnly)
                mesh.UploadMeshData(true);
            return mesh;
        }

        public static Mesh CreateNativePreview(Vector3[] source, string name)
        {
            if (source == null)
                throw new ArgumentNullException(nameof(source));
            int count = Math.Min(source.Length, NativePreviewPointLimit);
            var positions = new Vector3[count];
            var colors = new Color32[count];
            for (int index = 0; index < count; index++)
            {
                Vector3 point = source[index];
                positions[index] = new Vector3(-point.x, point.y, point.z);
            }
            return Create(
                new MixedSpacePointCloudData(positions, colors),
                name,
                uploadReadOnly: false);
        }
    }
}
