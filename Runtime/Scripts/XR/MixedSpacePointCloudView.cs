using System;
using System.IO;
using UnityEngine;

namespace MixedSpace.XR
{
    /// <summary>
    /// 场景点云预览入口，存储编辑值和序列化资源引用；解码、建网格、资源所有权及显示计算由独立服务处理。
    /// Scene-facing point-preview endpoint. It stores authoring values and
    /// serialized Unity resource references; decoding, mesh creation, resource
    /// ownership and display calculation live in separate services.
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [AddComponentMenu("")]
    public sealed class MixedSpacePointCloudView : MonoBehaviour
    {
        [SerializeField, HideInInspector]
        private bool m_HasPointCloud;

        [SerializeField, HideInInspector]
        private MixedSpacePointCloudVisibility m_RenderMode =
            MixedSpacePointCloudVisibility.WorkspaceOnly;

        [SerializeField, HideInInspector]
        private Color m_PointColor = new Color(0.57f, 0.93f, 0.12f);

        [SerializeField, HideInInspector, Range(0f, 1f)]
        private float m_PointSize = 0.33f;

        [SerializeField, HideInInspector]
        private bool m_PerspectivePoints = true;

        [SerializeField, HideInInspector]
        private int m_MapKey = -1;

        // 这些字段保留场景内嵌预览资源，由普通 C# 呈现对象管理运行时生命周期。
        // These fields retain existing scene-embedded preview resources. The ordinary C# surface owns their runtime
        // lifecycle.
        [SerializeField, HideInInspector] private Shader m_Shader;
        [SerializeField, HideInInspector] private Material m_Material;
        [SerializeField, HideInInspector] private Mesh m_Mesh;
        [SerializeField, HideInInspector] private MeshFilter m_MeshFilter;
        [SerializeField, HideInInspector] private MeshRenderer m_MeshRenderer;

        [NonSerialized] private MixedSpacePointCloudSurface m_Surface;

        public bool HasPointCloud => m_HasPointCloud && m_Mesh;
        public int MapKey => m_MapKey;
        public Mesh Mesh => m_Mesh;

        public MixedSpacePointCloudVisibility Visibility
        {
            get => m_RenderMode;
            set
            {
                m_RenderMode = value;
                RefreshAppearance();
            }
        }

        public Color PointColor
        {
            get => m_PointColor;
            set
            {
                m_PointColor = value;
                RefreshAppearance();
            }
        }

        public float PointSize
        {
            get => m_PointSize;
            set
            {
                if (float.IsNaN(value) || float.IsInfinity(value))
                    throw new ArgumentOutOfRangeException(nameof(value));
                m_PointSize = Math.Max(0f, Math.Min(1f, value));
                RefreshAppearance();
            }
        }

        public bool PerspectivePoints
        {
            get => m_PerspectivePoints;
            set
            {
                m_PerspectivePoints = value;
                RefreshAppearance();
            }
        }

        internal void Initialize(
            int mapKey,
            MixedSpacePointCloudVisibility visibility,
            Color pointColor)
        {
            if (mapKey <= 0)
                throw new ArgumentOutOfRangeException(nameof(mapKey));
            m_MapKey = mapKey;
            m_RenderMode = visibility;
            m_PointColor = pointColor;
            RefreshAppearance();
        }

        /// <summary>
        /// 解码并显示本地稀疏地图文件。
        /// Decodes and presents a local sparse-map document.
        /// </summary>
        public bool PresentFile(string filePath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(filePath))
                    throw new ArgumentException("Point-cloud path is empty.", nameof(filePath));
                byte[] document = File.ReadAllBytes(filePath);
                return PresentDocument(
                    document,
                    Path.GetFileNameWithoutExtension(filePath));
            }
            catch (Exception exception)
            {
                MixedSpaceLogger.LogError(
                    $"Point-cloud file presentation failed: {exception.Message}");
                return false;
            }
        }

        /// <summary>
        /// 解码并显示内存中的稀疏地图数据。
        /// Decodes and presents an in-memory sparse-map document.
        /// </summary>
        public bool PresentDocument(byte[] document, string label)
        {
            try
            {
                MixedSpacePointCloudData data = MixedSpacePlyDecoder.Decode(document);
                Mesh mesh = MixedSpacePointMeshFactory.Create(
                    data,
                    label,
                    uploadReadOnly: true);
                TransferMeshOwnership(mesh);
                return true;
            }
            catch (Exception exception)
            {
                MixedSpaceLogger.LogError(
                    $"Point-cloud document presentation failed: {exception.Message}");
                return false;
            }
        }

        /// <summary>
        /// 通过原生地图库接口读取稀疏点。
        /// Reads sparse points through the native map-store port.
        /// </summary>
        public void PresentNativeMap(IMixedSpaceNativeCore nativeCore)
        {
            if (nativeCore == null)
                throw new ArgumentNullException(nameof(nativeCore));
            if (m_MapKey <= 0)
                throw new InvalidOperationException("Point-cloud view has no valid map key.");

            Vector3[] points = nativeCore.Maps.ReadSparsePoints(m_MapKey);
            Mesh mesh = MixedSpacePointMeshFactory.CreateNativePreview(
                points,
                $"Map {m_MapKey} Sparse Preview");
            TransferMeshOwnership(mesh);
        }

        /// <summary>
        /// 接管已准备的预览 Mesh；替换或释放预览时销毁此前持有的 Mesh 和 Material。
        /// Takes ownership of a prepared preview Mesh. Replacing or releasing the
        /// preview destroys the previously owned Mesh and Material.
        /// </summary>
        public void PresentMesh(Mesh mesh)
        {
            if (!mesh)
                throw new ArgumentNullException(nameof(mesh));
            EnsureSurface().Attach(mesh);
            SyncSurface();
            m_HasPointCloud = true;
            RefreshAppearance();
        }

        private void TransferMeshOwnership(Mesh mesh)
        {
            try
            {
                PresentMesh(mesh);
            }
            catch
            {
                DestroyUnclaimed(mesh);
                throw;
            }
        }

        public void ReleasePreview()
        {
            EnsureSurface().Detach();
            SyncSurface();
            m_HasPointCloud = false;
        }

        public void RefreshAppearance()
        {
            if (!this)
                return;
            EnsureSurface().Apply(
                m_RenderMode,
                Application.isEditor,
                m_PointColor,
                m_PointSize,
                m_PerspectivePoints);
            SyncSurface();
        }

        private void OnEnable()
        {
            if (m_Mesh)
            {
                EnsureSurface().Attach(m_Mesh);
                SyncSurface();
            }
            RefreshAppearance();
        }

        private void OnValidate()
        {
            if (float.IsNaN(m_PointSize) || float.IsInfinity(m_PointSize))
                m_PointSize = 0.33f;
            m_PointSize = Math.Max(0f, Math.Min(1f, m_PointSize));
            RefreshAppearance();
        }

        private void OnDestroy()
        {
            if (m_Surface != null || m_Mesh || m_Material)
            {
                EnsureSurface().Detach();
                SyncSurface();
            }
            m_HasPointCloud = false;
        }

        private MixedSpacePointCloudSurface EnsureSurface()
        {
            return m_Surface ??= new MixedSpacePointCloudSurface(
                gameObject,
                m_Shader,
                m_Material,
                m_Mesh,
                m_MeshFilter,
                m_MeshRenderer);
        }

        private void SyncSurface()
        {
            if (m_Surface == null)
                return;
            m_Shader = m_Surface.Shader;
            m_Material = m_Surface.Material;
            m_Mesh = m_Surface.Mesh;
            m_MeshFilter = m_Surface.Filter;
            m_MeshRenderer = m_Surface.Renderer;
        }

        private static void DestroyUnclaimed(UnityEngine.Object instance)
        {
            if (!instance)
                return;
            if (Application.isPlaying)
                Destroy(instance);
            else
                DestroyImmediate(instance);
        }
    }
}
