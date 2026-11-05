using System;
using System.Text;
using UnityEngine;

namespace MixedSpace.XR
{
    /// <summary>
    /// 解码后的稀疏几何，不依赖 Mesh、Material、场景对象或地图所有权；数组仅供预览管线使用。
    /// Decoded sparse geometry independent from Mesh, Material, scene objects and
    /// map ownership. Arrays are private to the preview pipeline.
    /// </summary>
    internal sealed class MixedSpacePointCloudData
    {
        public MixedSpacePointCloudData(Vector3[] positions, Color32[] colors)
        {
            Positions = positions ?? throw new ArgumentNullException(nameof(positions));
            Colors = colors ?? throw new ArgumentNullException(nameof(colors));
            if (positions.Length != colors.Length)
                throw new ArgumentException("Every point must have one color.");
        }

        public Vector3[] Positions { get; }
        public Color32[] Colors { get; }
        public int Count => Positions.Length;
    }

    /// <summary>
    /// SDK 稀疏地图 PLY 解码器：小端二进制记录包含 float XYZ 和 byte RGB；逐字节识别 LF/CRLF 文件头，不依赖 StreamReader 缓冲。
    /// Decoder for the SDK sparse-map PLY contract: binary little-endian records
    /// containing float x/y/z followed by byte red/green/blue. Header scanning is
    /// byte-accurate for LF and CRLF and never depends on StreamReader buffering.
    /// </summary>
    internal static class MixedSpacePlyDecoder
    {
        private const int BytesPerPoint = 15;
        private const int MaximumHeaderBytes = 64 * 1024;
        private const int MaximumHeaderLines = 1024;

        public static MixedSpacePointCloudData Decode(byte[] document)
        {
            if (document == null)
                throw new ArgumentNullException(nameof(document));

            int cursor = 0;
            int lines = 0;
            string magic = ReadLine(document, ref cursor, ref lines);
            if (!string.Equals(magic, "ply", StringComparison.Ordinal))
                throw new FormatException("PLY magic line is missing.");

            string format = ReadLine(document, ref cursor, ref lines);
            if (!string.Equals(format, "format binary_little_endian 1.0",
                    StringComparison.Ordinal))
            {
                throw new FormatException(
                    "Only PLY binary_little_endian 1.0 is supported.");
            }

            int vertexCount = -1;
            bool ended = false;
            while (cursor <= document.Length &&
                   cursor <= MaximumHeaderBytes &&
                   lines < MaximumHeaderLines)
            {
                string line = ReadLine(document, ref cursor, ref lines);
                if (string.Equals(line, "end_header", StringComparison.Ordinal))
                {
                    ended = true;
                    break;
                }

                string[] parts = line.Split(
                    (char[])null,
                    StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 3 &&
                    string.Equals(parts[0], "element", StringComparison.Ordinal) &&
                    string.Equals(parts[1], "vertex", StringComparison.Ordinal))
                {
                    if (!int.TryParse(
                            parts[2],
                            System.Globalization.NumberStyles.None,
                            System.Globalization.CultureInfo.InvariantCulture,
                            out vertexCount) ||
                        vertexCount < 0)
                    {
                        throw new FormatException("PLY vertex count is invalid.");
                    }
                }
            }

            if (!ended)
                throw new FormatException("PLY end_header was not found within the header limit.");
            if (vertexCount < 0)
                throw new FormatException("PLY vertex element is missing.");

            long requiredBytes = (long)vertexCount * BytesPerPoint;
            if (requiredBytes > document.Length - cursor)
                throw new FormatException("PLY point body is truncated.");

            var positions = new Vector3[vertexCount];
            var colors = new Color32[vertexCount];
            for (int index = 0; index < vertexCount; index++)
            {
                float x = ReadSingle(document, ref cursor);
                float y = ReadSingle(document, ref cursor);
                float z = ReadSingle(document, ref cursor);
                byte red = document[cursor++];
                byte green = document[cursor++];
                byte blue = document[cursor++];
                positions[index] = new Vector3(-x, y, z);
                colors[index] = new Color32(red, green, blue, byte.MaxValue);
            }
            return new MixedSpacePointCloudData(positions, colors);
        }

        private static string ReadLine(
            byte[] document,
            ref int cursor,
            ref int lines)
        {
            if (cursor >= document.Length)
                throw new FormatException("PLY header ended unexpectedly.");
            if (cursor > MaximumHeaderBytes || lines >= MaximumHeaderLines)
                throw new FormatException("PLY header exceeds the supported limit.");

            int start = cursor;
            while (cursor < document.Length && document[cursor] != (byte)'\n')
                cursor++;
            if (cursor >= document.Length)
                throw new FormatException("PLY header line has no terminator.");

            int length = cursor - start;
            if (length > 0 && document[start + length - 1] == (byte)'\r')
                length--;
            string line = Encoding.ASCII.GetString(document, start, length);
            cursor++;
            lines++;
            return line;
        }

        private static float ReadSingle(byte[] document, ref int cursor)
        {
            uint bits =
                document[cursor] |
                ((uint)document[cursor + 1] << 8) |
                ((uint)document[cursor + 2] << 16) |
                ((uint)document[cursor + 3] << 24);
            cursor += 4;
            return BitConverter.Int32BitsToSingle(unchecked((int)bits));
        }
    }
}
