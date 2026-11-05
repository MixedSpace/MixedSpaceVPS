using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace MixedSpace.XR
{
    public enum MixedSpacePointCloudVisibility
    {
        Off,
        WorkspaceOnly,
        BuildAndWorkspace
    }

    /// <summary>
    /// 编辑器和运行时共用的纯显示决策。
    /// Pure display decisions shared by editor and runtime surfaces.
    /// </summary>
    internal static class MixedSpacePointDisplayPolicy
    {
        public static bool IsVisible(
            MixedSpacePointCloudVisibility visibility,
            bool isUnityEditor)
        {
            switch (visibility)
            {
                case MixedSpacePointCloudVisibility.Off:
                    return false;
                case MixedSpacePointCloudVisibility.WorkspaceOnly:
                    return isUnityEditor;
                case MixedSpacePointCloudVisibility.BuildAndWorkspace:
                    return true;
                default:
                    return false;
            }
        }

        public static float ShaderPointSize(float normalizedSize, bool perspective)
        {
            if (float.IsNaN(normalizedSize) || float.IsInfinity(normalizedSize))
                throw new ArgumentOutOfRangeException(nameof(normalizedSize));
            float amount = Math.Max(0f, Math.Min(1f, normalizedSize));
            if (perspective)
                amount = amount * amount * amount;
            float minimum = perspective ? 0.002f : 1.5f;
            float maximum = perspective ? 0.14f : 40f;
            return minimum + (maximum - minimum) * amount;
        }
    }

    /// <summary>
    /// 调色板 API 只读，使用方只能选择颜色，不能修改所有 SDK 共享的公共数组。
    /// Palette is read-only by API: consumers can select a value but cannot mutate
    /// a process-wide public array shared by every SDK instance.
    /// </summary>
    internal static class MixedSpacePointPalette
    {
        private static readonly IReadOnlyList<Color> s_Colors =
            Array.AsReadOnly(new[]
            {
                new Color(0.22f, 1f, 0.46f),
                new Color(0.96f, 0.14f, 0.14f),
                new Color(0.16f, 0.69f, 0.95f),
                new Color(0.93f, 0.84f, 0.12f),
                new Color(0.57f, 0.93f, 0.12f),
                new Color(1f, 0.38f, 0.78f),
                new Color(0.4f, 0f, 0.9f),
                new Color(0.89f, 0.4f, 0f)
            });

        public static int Count => s_Colors.Count;

        public static Color At(int index)
        {
            if (index < 0 || index >= s_Colors.Count)
                throw new ArgumentOutOfRangeException(nameof(index));
            return s_Colors[index];
        }
    }

    /// <summary>
    /// 管理单个预览对象的渲染资源，接入准备好的 Mesh、应用显示设置并释放自有 Mesh/Material；加载和解码由外部负责。
    /// Manages rendering resources for one preview: attaches prepared meshes, applies display settings and releases owned meshes/materials. Loading and decoding are external.
    /// </summary>
    internal sealed class MixedSpacePointCloudSurface
    {
        private const string ShaderName = "MixedSpace/Point Cloud";
        private readonly GameObject m_Host;

        public MixedSpacePointCloudSurface(
            GameObject host,
            Shader shader,
            Material material,
            Mesh mesh,
            MeshFilter filter,
            MeshRenderer renderer)
        {
            m_Host = host ? host : throw new ArgumentNullException(nameof(host));
            Shader = shader;
            Material = material;
            Mesh = mesh;
            Filter = filter;
            Renderer = renderer;
        }

        public Shader Shader { get; private set; }
        public Material Material { get; private set; }
        public Mesh Mesh { get; private set; }
        public MeshFilter Filter { get; private set; }
        public MeshRenderer Renderer { get; private set; }

        public void Attach(Mesh mesh)
        {
            if (!mesh)
                throw new ArgumentNullException(nameof(mesh));
            Shader resolvedShader = Shader
                ? Shader
                : Material && Material.shader
                    ? Material.shader
                    : UnityEngine.Shader.Find(ShaderName);
            if (!resolvedShader)
                throw new InvalidOperationException($"Shader '{ShaderName}' is unavailable.");
            Shader = resolvedShader;
            if (!Material)
                Material = new Material(Shader) { name = "MixedSpace Point Preview" };
            Filter = Filter ? Filter : m_Host.GetComponent<MeshFilter>();
            if (!Filter)
                Filter = m_Host.AddComponent<MeshFilter>();
            Renderer = Renderer ? Renderer : m_Host.GetComponent<MeshRenderer>();
            if (!Renderer)
                Renderer = m_Host.AddComponent<MeshRenderer>();

            if (Mesh && Mesh != mesh)
                DestroyObject(Mesh);
            Mesh = mesh;
            Filter.sharedMesh = Mesh;
            Renderer.sharedMaterial = Material;
            Renderer.shadowCastingMode = ShadowCastingMode.Off;
            Renderer.receiveShadows = false;
            Renderer.lightProbeUsage = LightProbeUsage.Off;
            Renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
        }

        public void Apply(
            MixedSpacePointCloudVisibility visibility,
            bool isUnityEditor,
            Color color,
            float pointSize,
            bool perspective)
        {
            if (!Renderer || !Material)
                return;
            Renderer.enabled = MixedSpacePointDisplayPolicy.IsVisible(
                visibility,
                isUnityEditor);
            Material.SetFloat("_PerspectiveEnabled", perspective ? 1f : 0f);
            Material.SetFloat(
                "_PointSize",
                MixedSpacePointDisplayPolicy.ShaderPointSize(pointSize, perspective));
            Material.SetColor("_PointColor", color);
        }

        public void Detach()
        {
            if (Renderer)
            {
                Renderer.enabled = false;
                Renderer.sharedMaterial = null;
            }
            if (Filter)
                Filter.sharedMesh = null;
            ReleaseOwnedAssets();
            Mesh = null;
            Material = null;
        }

        private void ReleaseOwnedAssets()
        {
            DestroyObject(Mesh);
            DestroyObject(Material);
        }

        private static void DestroyObject(UnityEngine.Object instance)
        {
            if (!instance)
                return;
            if (Application.isPlaying)
                UnityEngine.Object.Destroy(instance);
            else
                UnityEngine.Object.DestroyImmediate(instance);
        }
    }
}
