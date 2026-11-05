using System;
using System.Runtime.InteropServices;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.XR.ARSubsystems;

namespace MixedSpace.XR
{
    internal sealed class ARFoundationPixelSource : IMixedSpacePixelSource
    {
        private XRCpuImage m_Image;
        private IntPtr m_Pointer;
        private byte[] m_ConvertedBytes;
        private GCHandle m_ConvertedBufferHandle;
        private bool m_Disposed;
        private readonly MixedSpacePixelFormat m_Format;

        public ARFoundationPixelSource(
            XRCpuImage image,
            MixedSpacePixelFormat format)
        {
            m_Image = image;
            m_Format = format;
            try
            {
                switch (format)
                {
                    case MixedSpacePixelFormat.Rgb24:
                        ConvertToRgb24();
                        break;
                    default:
                    case MixedSpacePixelFormat.Luminance8:
                        BindLuminancePlane();
                        break;
                }
            }
            catch
            {
                // 转换失败时仍持有已申请的 XRCpuImage 及此前固定的转换缓冲区，必须一并释放。
                // A failed conversion still owns both the acquired XRCpuImage and any pinned conversion buffer allocated before
                // the exception.
                ReleaseResources();
                throw;
            }
        }

        public IntPtr Pointer
        {
            get
            {
                EnsureActive();
                return m_Pointer;
            }
        }

        public byte[] CopyPixels()
        {
            EnsureActive();
            int pixelSize = m_Format == MixedSpacePixelFormat.Luminance8 ? 1 : 3;
            int size = m_Image.width * m_Image.height * pixelSize;
            byte[] bytes = new byte[size];
            Marshal.Copy(m_Pointer, bytes, 0, size);
            return bytes;
        }

        public void Dispose()
        {
            if (m_Disposed)
                return;

            m_Disposed = true;
            ReleaseResources();
        }

        /// <summary>
        /// 在图像完整生命周期内持有并固定托管转换缓冲区；fixed 块内取得的指针不能保存给块外的后台定位任务。
        /// Retains and pins a managed conversion buffer for the complete lifetime
        /// of this image. A pointer obtained inside a fixed block is only valid for
        /// that block and must never be stored for a background localization task.
        /// </summary>
        private void RetainConvertedBuffer(byte[] data)
        {
            if (m_ConvertedBufferHandle.IsAllocated)
                m_ConvertedBufferHandle.Free();

            m_ConvertedBytes = data ?? throw new ArgumentNullException(nameof(data));
            m_ConvertedBufferHandle = GCHandle.Alloc(
                m_ConvertedBytes,
                GCHandleType.Pinned);
            m_Pointer = m_ConvertedBufferHandle.AddrOfPinnedObject();
        }

        private void BindLuminancePlane()
        {
            XRCpuImage.Plane plane = m_Image.GetPlane(0);
            int width = m_Image.width, height = m_Image.height;

            if (width == plane.rowStride)
            {
                unsafe
                {
                    m_Pointer = (IntPtr)NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(plane.data);
                }
            }
            else
            {
                byte[] data = new byte[width * height];

                unsafe
                {
                    fixed (byte* dstPtr = data)
                    {
                        byte* srcPtr = (byte*)NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(plane.data);
                        if (width > 0 && height > 0)
                        {
                            UnsafeUtility.MemCpyStride(dstPtr, width, srcPtr, plane.rowStride, width, height);
                        }
                    }
                }

                RetainConvertedBuffer(data);
            }
        }

        private void ConvertToRgb24()
        {
            var conversionParams = new XRCpuImage.ConversionParams
            {
                inputRect = new RectInt(0, 0, m_Image.width, m_Image.height),
                outputDimensions = new Vector2Int(m_Image.width, m_Image.height),
                outputFormat = TextureFormat.RGB24,
                transformation = XRCpuImage.Transformation.None
            };

            int size = m_Image.GetConvertedDataSize(conversionParams);
            byte[] data = new byte[size];

            // 交给原生转换前先固定目标缓冲区，直到采集所有者关闭像素源才释放句柄。
            // Pin before passing the destination to the native conversion and retain the handle until the owning capture
            // closes the pixel source.
            RetainConvertedBuffer(data);
            m_Image.Convert(conversionParams, m_Pointer, data.Length);
        }

        private void ReleaseResources()
        {
            if (m_ConvertedBufferHandle.IsAllocated)
                m_ConvertedBufferHandle.Free();
            if (m_Image.valid)
                m_Image.Dispose();

            m_ConvertedBytes = null;
            m_Pointer = IntPtr.Zero;
        }

        private void EnsureActive()
        {
            if (m_Disposed)
                throw new ObjectDisposedException(nameof(ARFoundationPixelSource));
        }
    }
}
