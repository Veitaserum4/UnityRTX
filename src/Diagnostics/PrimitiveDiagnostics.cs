using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;

namespace UnityRemix
{
    /// <summary>
    /// Logs, once per renderer (up to MaxPerScene per scene), each Unity primitive mesh (Cube,
    /// Sphere, ...) or Default-Material renderer UnityRTX captures: games use them for trigger volumes and
    /// helper shapes they hide one way or another, which show up as stray untextured blocks.
    /// Main thread only.
    /// </summary>
    internal static class PrimitiveDiagnostics
    {
        public static ManualLogSource Logger;
        private static readonly HashSet<int> logged = new HashSet<int>();
        // Quads left out: decals and sprites use them by the thousand.
        private static readonly HashSet<string> primitives = new HashSet<string> { "Cube", "Sphere", "Capsule", "Cylinder", "Plane" };
        private const int MaxPerScene = 100;

        public static void Note(Renderer renderer, Mesh mesh, string origin)
        {
            if (Logger == null || renderer == null || mesh == null || logged.Count >= MaxPerScene)
                return;
            var material = renderer.sharedMaterial;
            bool defaultMaterial = material != null && material.name.StartsWith("Default-Material");
            if (!primitives.Contains(mesh.name) && !defaultMaterial)
                return;
            if (!logged.Add(renderer.GetInstanceID()))
                return;

            string path = renderer.name;
            for (var p = renderer.transform.parent; p != null; p = p.parent)
                path = p.name + "/" + path;
            var go = renderer.gameObject;
            Logger.LogInfo($"[Primitive] {origin} draws '{path}': mesh '{mesh.name}', layer {LayerMask.LayerToName(go.layer)}({go.layer}), " +
                           $"material '{(material != null ? material.name : "-")}' shader '{(material != null && material.shader != null ? material.shader.name : "-")}', " +
                           $"shadows {renderer.shadowCastingMode}, position {renderer.bounds.center}, size {renderer.bounds.size}");
        }

        public static void Clear() => logged.Clear();
    }
}
