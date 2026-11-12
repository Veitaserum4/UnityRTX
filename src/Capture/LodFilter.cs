using System.Collections.Generic;
using UnityEngine;

namespace UnityRemix
{
    /// <summary>
    /// Unity keeps every level of an LODGroup enabled and draws one of them by distance; captured
    /// as-is, all levels overlap and the coarser ones poke through the detailed one. Remix draws
    /// the most detailed level (LOD0) only.
    /// </summary>
    internal static class LodFilter
    {
        /// <summary>
        /// Instance ids of renderers that appear only in lower-detail levels (LOD1 and up) of the
        /// loaded scenes' LOD groups.
        /// </summary>
        public static HashSet<int> CollectLowerLevelRenderers(out int groupCount)
        {
            var lower = new HashSet<int>();
            var detailed = new HashSet<int>();
            groupCount = 0;
            foreach (var group in Resources.FindObjectsOfTypeAll<LODGroup>())
            {
                if (group == null || !group.gameObject.scene.IsValid())
                    continue;
                var levels = group.GetLODs();
                if (levels == null || levels.Length < 2)
                    continue;
                groupCount++;
                for (int level = 0; level < levels.Length; level++)
                {
                    var renderers = levels[level].renderers;
                    if (renderers == null)
                        continue;
                    foreach (var renderer in renderers)
                    {
                        if (renderer != null)
                            (level == 0 ? detailed : lower).Add(renderer.GetInstanceID());
                    }
                }
            }
            lower.ExceptWith(detailed);
            return lower;
        }
    }
}
