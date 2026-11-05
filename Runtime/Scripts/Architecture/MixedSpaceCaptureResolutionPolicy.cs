using System;
using System.Collections.Generic;
using UnityEngine;

namespace MixedSpace
{
    /// <summary>
    /// 纯分辨率选择策略，返回原候选列表索引；平台保留原生配置所有权，只有选择成功才应用。
    /// Pure resolution selection shared by platform adapters. The result is an
    /// index into the original candidate list, so a platform retains ownership of
    /// its native configuration and applies it only after selection succeeds.
    /// </summary>
    public static class MixedSpaceCaptureResolutionPolicy
    {
        /// <summary>
        /// 选择像素数最接近的候选；相同评分取首项。忽略无效尺寸，无可用项返回 -1，不修改帧率等配置。
        /// Selects the nearest pixel count, keeping the first tie. Ignores invalid sizes and returns -1 if none are usable; other configuration fields are unchanged.
        /// </summary>
        public static int FindNearest(IReadOnlyList<Vector2Int> candidates, Vector2Int desired)
        {
            if (candidates == null) throw new ArgumentNullException(nameof(candidates));
            if (desired.x <= 0 || desired.y <= 0)
                throw new ArgumentOutOfRangeException(nameof(desired));

            long targetArea = (long)desired.x * desired.y;
            long bestDistance = long.MaxValue;
            int selectedIndex = -1;
            for (int index = 0; index < candidates.Count; index++)
            {
                Vector2Int candidate = candidates[index];
                if (candidate.x <= 0 || candidate.y <= 0)
                    continue;

                // 先提升数值类型再相乘，避免现代相机尺寸导致 32 位溢出，无需构造巨大目标尺寸。
                // Promote before multiplication; modern camera dimensions must not overflow a 32-bit score or require a
                // synthetic huge target.
                long distance = Math.Abs((long)candidate.x * candidate.y - targetArea);
                if (distance >= bestDistance)
                    continue;

                bestDistance = distance;
                selectedIndex = index;
            }

            return selectedIndex;
        }

        /// <summary>
        /// 选择最大正像素数；相同尺寸按提供器顺序选择，与 FindNearest 一致。
        /// Chooses the greatest positive pixel count directly. Equal resolutions
        /// retain the provider's ordering, as in FindNearest.
        /// </summary>
        public static int FindLargest(IReadOnlyList<Vector2Int> candidates)
        {
            if (candidates == null) throw new ArgumentNullException(nameof(candidates));

            long bestArea = 0;
            int selectedIndex = -1;
            for (int index = 0; index < candidates.Count; index++)
            {
                Vector2Int candidate = candidates[index];
                if (candidate.x <= 0 || candidate.y <= 0)
                    continue;

                long area = (long)candidate.x * candidate.y;
                if (area <= bestArea)
                    continue;

                bestArea = area;
                selectedIndex = index;
            }

            return selectedIndex;
        }
    }
}
