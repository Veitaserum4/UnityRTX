using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace UnityRemix
{
    /// <summary>
    /// Harmony patch to bypass Unity's Mesh.canAccess check, making all mesh data
    /// accessible regardless of the isReadable flag. This is critical for Unity 2019
    /// where statically-batched "Combined Mesh" objects are non-readable, preventing
    /// access to vertices/triangles/normals via the managed API.
    ///
    /// Unity keeps mesh data in native memory even for non-readable meshes — the
    /// canAccess property is purely an access guard. Bypassing it allows mesh.vertices,
    /// mesh.triangles etc. to return data from the native buffer.
    /// </summary>
    internal static class MeshAccessPatch
    {
        private static bool _applied;

        public static void Apply(Harmony harmony)
        {
            if (_applied) return;

            var canAccessGetter = AccessTools.PropertyGetter(typeof(Mesh), "canAccess");
            if (canAccessGetter == null)
            {
                // Fallback: try the internal property via reflection
                var prop = typeof(Mesh).GetProperty("canAccess",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                canAccessGetter = prop?.GetGetMethod(true);
            }

            if (canAccessGetter != null)
            {
                harmony.Patch(canAccessGetter,
                    prefix: new HarmonyMethod(typeof(MeshAccessPatch), nameof(CanAccessPrefix)));
                _applied = true;
            }
        }

        /// <summary>
        /// Prefix patch: always return true, skip the original native check.
        /// </summary>
        static bool CanAccessPrefix(ref bool __result)
        {
            __result = true;
            return false;
        }
    }
}
