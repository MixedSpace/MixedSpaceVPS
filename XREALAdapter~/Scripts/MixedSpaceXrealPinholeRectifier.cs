using System;
using UnityEngine;

namespace MixedSpace.XR
{
    /// <summary>五参数针孔图像去畸变。</summary>
    internal sealed class MixedSpaceXrealPinholeRectifier
    {
        private int m_Width, m_Height;
        private Vector4 m_K;
        private float[] m_Distortion;
        private float[] m_X, m_Y;

        internal static void Validate(int width, int height, Vector4 k, float[] distortion)
        {
            if (width <= 0 || height <= 0 || (long)width * height > 16 * 1024 * 1024)
                throw new ArgumentException("Calibration resolution is invalid.");
            if (!Finite(k.x) || !Finite(k.y) || !Finite(k.z) || !Finite(k.w) || k.x <= 0 || k.y <= 0 ||
                k.z < 0 || k.z >= width || k.w < 0 || k.w >= height)
                throw new ArgumentException("Intrinsics must be finite, with positive focal lengths and a principal point inside the image.");
            if (distortion == null || distortion.Length == 0) return;
            if (distortion.Length != 5)
                throw new ArgumentException("Expected OpenCV pinhole k1,k2,p1,p2,k3; fisheye/rational coefficients are not supported.");
            foreach (float value in distortion)
                if (!Finite(value)) throw new ArgumentException("Distortion coefficients must be finite.");
        }

        internal byte[] Rectify(byte[] source, int width, int height, Vector4 k, float[] distortion)
        {
            Validate(width, height, k, distortion);
            if (source == null || source.Length != checked(width * height))
                throw new ArgumentException("Expected tightly packed Gray8 pixels.");
            if (distortion == null || distortion.Length == 0) return (byte[])source.Clone();
            bool identity = true;
            foreach (float value in distortion) identity &= value == 0;
            if (identity) return (byte[])source.Clone();
            if (!Matches(width, height, k, distortion)) BuildMap(width, height, k, distortion);
            byte[] output = new byte[source.Length];
            for (int i = 0; i < output.Length; i++)
            {
                float sx = m_X[i], sy = m_Y[i];
                if (!Finite(sx) || !Finite(sy) || sx < 0 || sy < 0 || sx > width - 1 || sy > height - 1) continue;
                int x0 = (int)sx, y0 = (int)sy;
                int x1 = Math.Min(x0 + 1, width - 1), y1 = Math.Min(y0 + 1, height - 1);
                float tx = sx - x0, ty = sy - y0;
                float top = source[y0 * width + x0] * (1 - tx) + source[y0 * width + x1] * tx;
                float bottom = source[y1 * width + x0] * (1 - tx) + source[y1 * width + x1] * tx;
                output[i] = (byte)Math.Min(255, Math.Max(0, (int)(top * (1 - ty) + bottom * ty + 0.5f)));
            }
            return output;
        }

        private bool Matches(int width, int height, Vector4 k, float[] distortion)
        {
            if (m_X == null || m_Width != width || m_Height != height ||
                m_K.x != k.x || m_K.y != k.y || m_K.z != k.z || m_K.w != k.w) return false;
            for (int i = 0; i < 5; i++) if (m_Distortion[i] != distortion[i]) return false;
            return true;
        }

        private void BuildMap(int width, int height, Vector4 k, float[] distortion)
        {
            var mapX = new float[checked(width * height)];
            var mapY = new float[mapX.Length];
            double k1 = distortion[0], k2 = distortion[1], p1 = distortion[2], p2 = distortion[3], k3 = distortion[4];
            for (int v = 0; v < height; v++)
            for (int u = 0; u < width; u++)
            {
                double x = (u - (double)k.z) / k.x, y = (v - (double)k.w) / k.y;
                double r2 = x * x + y * y;
                double radial = 1 + r2 * (k1 + r2 * (k2 + r2 * k3));
                int index = v * width + u;
                mapX[index] = (float)(k.x * (x * radial + 2 * p1 * x * y + p2 * (r2 + 2 * x * x)) + k.z);
                mapY[index] = (float)(k.y * (y * radial + p1 * (r2 + 2 * y * y) + 2 * p2 * x * y) + k.w);
            }
            m_Width = width; m_Height = height; m_K = k;
            m_Distortion = (float[])distortion.Clone(); m_X = mapX; m_Y = mapY;
        }
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
