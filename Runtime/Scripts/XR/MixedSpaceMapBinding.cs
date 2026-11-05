using System;
using System.Collections.Generic;
using UnityEngine;

namespace MixedSpace.XR
{
    [Serializable]
    public struct MixedSpaceMapAlignment
    {
        public double tx;
        public double ty;
        public double tz;
        public double qx;
        public double qy;
        public double qz;
        public double qw;
        public double scale;
    }

    [Serializable]
    public struct MixedSpaceGeoOrigin
    {
        public double latitude;
        public double longitude;
        public double altitude;
    }

    /// <summary>
    /// 云工作区返回的 SDK 地图描述，排除传输状态、哈希和后端字段命名。
    /// SDK-owned map description returned by a cloud workspace. Transport status,
    /// hashes and backend field names are intentionally absent from this value.
    /// </summary>
    public sealed class MixedSpaceCloudMapDescriptor
    {
        public MixedSpaceCloudMapDescriptor(
            int key,
            string label,
            MixedSpaceMapAlignment alignment,
            MixedSpaceGeoOrigin geoOrigin,
            int accessLevel)
        {
            if (key <= 0)
                throw new ArgumentOutOfRangeException(nameof(key), "Map key must be positive.");

            Key = key;
            Label = string.IsNullOrWhiteSpace(label) ? $"Map {key}" : label;
            Alignment = alignment;
            GeoOrigin = geoOrigin;
            AccessLevel = accessLevel;
        }

        public int Key { get; }
        public string Label { get; }
        public MixedSpaceMapAlignment Alignment { get; }
        public MixedSpaceGeoOrigin GeoOrigin { get; }
        public int AccessLevel { get; }
    }

    /// <summary>
    /// 运行时使用的只读地图值，不持有 GameObject，不查找提供器，也不执行加载命令或编辑器行为。
    /// Read-only map value consumed by runtime use cases. It has no GameObject,
    /// provider lookup, loading command or editor behavior.
    /// </summary>
    public sealed class MixedSpaceMapDefinition
    {
        internal MixedSpaceMapDefinition(
            int key,
            string label,
            int accessLevel,
            MixedSpaceMapAlignment alignment,
            MixedSpaceGeoOrigin geoOrigin,
            string providerKey,
            TextAsset embeddedData,
            MixedSpaceMapContentPolicy contentPolicy,
            bool isReady)
        {
            Key = key;
            Label = label ?? string.Empty;
            AccessLevel = accessLevel;
            Alignment = alignment;
            GeoOrigin = geoOrigin;
            ProviderKey = string.IsNullOrWhiteSpace(providerKey)
                ? MixedSpaceProviderIds.OnDevice
                : providerKey;
            EmbeddedData = embeddedData;
            ContentPolicy = contentPolicy ??
                MixedSpaceMapContentPolicy.ProviderManaged();
            IsReady = isReady;
        }

        public int Key { get; }
        public string Label { get; }
        public int AccessLevel { get; }
        public MixedSpaceMapAlignment Alignment { get; }
        public MixedSpaceGeoOrigin GeoOrigin { get; }
        public string ProviderKey { get; }
        public TextAsset EmbeddedData { get; }
        public MixedSpaceMapContentPolicy ContentPolicy { get; }
        public bool IsReady { get; }

        public double[] BuildMapToEcef()
        {
            double[] rotation = MixedSpaceCoordinateFrame.BuildAlignmentRotation(Alignment);
            return new[]
            {
                Alignment.tx, Alignment.ty, Alignment.tz,
                rotation[0], rotation[1], rotation[2],
                rotation[3], rotation[4], rotation[5],
                rotation[6], rotation[7], rotation[8],
                Alignment.scale
            };
        }
    }

    /// <summary>
    /// 单地图的被动场景声明，运行时只读 Definition；编辑修改由 SDK 编辑器和准备层负责。
    /// Passive scene declaration for one map. Runtime code reads Definition;
    /// authoring changes are restricted to the SDK's editor and provisioning layer.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("MixedSpace/Map Binding")]
    public sealed class MixedSpaceMapBinding : MonoBehaviour
    {
        [SerializeField]
        private TextAsset m_EmbeddedData;

        [SerializeField]
        private int m_MapKey = -1;

        [SerializeField]
        private string m_Label;

        [SerializeField]
        private int m_AccessLevel;

        [SerializeField]
        private MixedSpaceMapAlignment m_WorldRelation;

        [SerializeField]
        private MixedSpaceGeoOrigin m_GeoOrigin;

        [SerializeField]
        private bool m_Ready;

        [SerializeField]
        private MixedSpacePointCloudView m_Preview;

        [SerializeField]
        private MixedSpaceMapContentLocation m_ContentLocation =
            MixedSpaceMapContentLocation.ProviderManaged;

        [SerializeField]
        private bool m_PresentPreviewAtRuntime;

        [SerializeField, HideInInspector]
        private byte[] m_RuntimePayload;

        public MixedSpaceMapDefinition Definition => new MixedSpaceMapDefinition(
            m_MapKey,
            m_Label,
            m_AccessLevel,
            m_WorldRelation,
            m_GeoOrigin,
            MixedSpaceProviderIds.OnDevice, // 仅作声明占位，运行时目录应用 SDK 路由。 / Declaration placeholder; runtime catalog applies the SDK route.
            m_EmbeddedData,
            new MixedSpaceMapContentPolicy(
                m_ContentLocation,
                m_PresentPreviewAtRuntime,
                m_RuntimePayload),
            m_Ready);

        public MixedSpacePointCloudView Preview => m_Preview;

        internal void Define(
            int key,
            string label,
            string providerKey,
            TextAsset embeddedData,
            MixedSpaceMapAlignment alignment,
            MixedSpaceGeoOrigin geoOrigin,
            int accessLevel,
            MixedSpaceMapContentPolicy contentPolicy,
            bool renameObject)
        {
            m_MapKey = key;
            m_Label = label ?? string.Empty;
            m_EmbeddedData = embeddedData;
            m_WorldRelation = NormalizeAlignment(alignment);
            m_GeoOrigin = geoOrigin;
            m_AccessLevel = accessLevel;
            SetContentPolicy(contentPolicy);
            m_Ready = true;
            if (renameObject)
                gameObject.name = $"Map Binding {m_MapKey} - {m_Label}";
        }

        internal void DefineFromCloud(
            MixedSpaceCloudMapDescriptor descriptor,
            string providerKey,
            TextAsset embeddedData,
            MixedSpaceMapContentPolicy contentPolicy,
            bool renameObject)
        {
            if (descriptor == null)
                throw new ArgumentNullException(nameof(descriptor));

            Define(
                descriptor.Key,
                descriptor.Label,
                providerKey,
                embeddedData,
                descriptor.Alignment,
                descriptor.GeoOrigin,
                descriptor.AccessLevel,
                contentPolicy,
                renameObject);
        }

        internal void SetContentPolicy(MixedSpaceMapContentPolicy contentPolicy)
        {
            MixedSpaceMapContentPolicy policy = contentPolicy ??
                MixedSpaceMapContentPolicy.ProviderManaged();
            m_ContentLocation = policy.Location;
            m_PresentPreviewAtRuntime = policy.PresentPreviewAtRuntime;
            m_RuntimePayload = policy.CopyRuntimePayload();
        }

        internal void ResetDeclaration()
        {
            m_EmbeddedData = null;
            m_Ready = false;
            m_WorldRelation = IdentityAlignment();
            m_GeoOrigin = default;
            m_AccessLevel = 0;
            SetContentPolicy(MixedSpaceMapContentPolicy.ProviderManaged());
            MixedSpacePointCloudPresenter.Remove(this);
        }

        internal void ApplyRelationToTransform()
        {
            Vector3 position = new Vector3(
                (float)m_WorldRelation.tx,
                (float)m_WorldRelation.ty,
                (float)m_WorldRelation.tz);
            Quaternion rotation = new Quaternion(
                (float)m_WorldRelation.qx,
                (float)m_WorldRelation.qy,
                (float)m_WorldRelation.qz,
                (float)m_WorldRelation.qw);
            float scale = (float)m_WorldRelation.scale;
            Matrix4x4 converted = Matrix4x4.TRS(
                position,
                rotation,
                Vector3.one * scale);
            converted = MixedSpaceCoordinateFrame.ReflectXAxis(converted);

            transform.localPosition = converted.GetColumn(3);
            transform.localRotation = converted.rotation;
            transform.localScale = Vector3.one * scale;
        }

        internal void AttachPreview(MixedSpacePointCloudView preview)
        {
            m_Preview = preview;
        }

        internal bool ValidateDeclaration(
            Func<string, bool> providerExists,
            out string message)
        {
            message = string.Empty;
            if (!gameObject.activeInHierarchy || !enabled)
                return false;
            if (!m_Ready || m_MapKey <= 0)
            {
                message = $"Map declaration on {name} is incomplete.";
                return false;
            }
            if (providerExists == null || !providerExists(Definition.ProviderKey))
            {
                message = $"Map {m_Label} references unavailable provider '{Definition.ProviderKey}'.";
                return false;
            }
            return true;
        }

        private static MixedSpaceMapAlignment NormalizeAlignment(
            MixedSpaceMapAlignment alignment)
        {
            if (Math.Abs(alignment.qw) < double.Epsilon &&
                Math.Abs(alignment.qx) < double.Epsilon &&
                Math.Abs(alignment.qy) < double.Epsilon &&
                Math.Abs(alignment.qz) < double.Epsilon)
            {
                alignment.qw = 1.0;
            }
            if (Math.Abs(alignment.scale) < double.Epsilon)
                alignment.scale = 1.0;
            return alignment;
        }

        internal static MixedSpaceMapAlignment IdentityAlignment()
        {
            return new MixedSpaceMapAlignment { qw = 1.0, scale = 1.0 };
        }
    }

    /// <summary>
    /// 可选点云的独立场景呈现服务。
    /// Separate scene-presentation service for optional point clouds.
    /// </summary>
    public static class MixedSpacePointCloudPresenter
    {
        public static MixedSpacePointCloudView Show(
            MixedSpaceMapBinding binding,
            MixedSpacePointCloudVisibility visibility =
                MixedSpacePointCloudVisibility.WorkspaceOnly,
            bool randomColor = false)
        {
            Color color = randomColor
                ? MixedSpacePointPalette.At(
                    UnityEngine.Random.Range(0, MixedSpacePointPalette.Count))
                : new Color(0.57f, 0.93f, 0.12f);
            return Show(binding, visibility, color);
        }

        public static MixedSpacePointCloudView Show(
            MixedSpaceMapBinding binding,
            MixedSpacePointCloudVisibility visibility,
            Color color)
        {
            if (!binding)
                throw new ArgumentNullException(nameof(binding));

            Remove(binding);
            MixedSpaceMapDefinition definition = binding.Definition;
            var previewObject = new GameObject(
                $"Point Cloud {definition.Key} - {definition.Label}");
            previewObject.transform.SetParent(binding.transform, false);
            MixedSpacePointCloudView preview =
                previewObject.AddComponent<MixedSpacePointCloudView>();
            preview.Initialize(definition.Key, visibility, color);
            binding.AttachPreview(preview);
            return preview;
        }

        public static void Remove(MixedSpaceMapBinding binding)
        {
            if (!binding || !binding.Preview)
                return;

            MixedSpacePointCloudView preview = binding.Preview;
            binding.AttachPreview(null);
            preview.ReleasePreview();
            if (Application.isPlaying)
                UnityEngine.Object.Destroy(preview.gameObject);
            else
                UnityEngine.Object.DestroyImmediate(preview.gameObject);
        }
    }
}
