using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace UnityRemix.Games.Ultrakill
{
    /// <summary>
    /// StaticSceneOptimizer.Start (SetupMeshes) rewrites each level renderer it lists: its mesh
    /// becomes one of the scene's baked combined meshes (static batch info: one submesh at
    /// firstSubMesh), its materials the batch material, whose texture is the scene's atlas and
    /// whose shader finds each vertex's cell from per-vertex atlas rectangles. Read after that,
    /// a renderer's uv0 spans the whole atlas. The level is normally scanned before it runs, but
    /// CheckPoint.Start clones each room (Unity orders the two Starts as it likes) and a restart
    /// clones the room again from its hidden original: a clone of a rewritten renderer drew the
    /// atlas (an electricity box in 2-2). The original mesh and materials are recorded before
    /// the rewrite, by the combined mesh and first submesh, which a clone keeps.
    /// </summary>
    internal static class UltrakillStaticOptimizer
    {
        private static readonly Dictionary<(int mesh, int subMesh), (Mesh mesh, Material[] materials)> originals =
            new Dictionary<(int, int), (Mesh, Material[])>();
        private static FieldInfo renderersField, dataField, bakedMeshesField, meshIndicesField, firstSubMeshField;
        private static ManualLogSource log;

        public static void Apply(Harmony harmony, ManualLogSource logger)
        {
            log = logger;
            var type = AccessTools.TypeByName("StaticSceneOptimizer");
            var dataType = AccessTools.TypeByName("StaticSceneData");
            var setup = type != null ? AccessTools.Method(type, "SetupMeshes") : null;
            renderersField = type != null ? AccessTools.Field(type, "staticMRends") : null;
            dataField = type != null ? AccessTools.Field(type, "bakedDataAsset") : null;
            bakedMeshesField = dataType != null ? AccessTools.Field(dataType, "bakedMeshes") : null;
            meshIndicesField = dataType != null ? AccessTools.Field(dataType, "mrMeshIndices") : null;
            firstSubMeshField = dataType != null ? AccessTools.Field(dataType, "firstSubMesh") : null;
            if (setup == null || renderersField == null || dataField == null || bakedMeshesField == null ||
                meshIndicesField == null || firstSubMeshField == null)
            {
                logger?.LogWarning("[UltrakillStaticOptimizer] StaticSceneOptimizer not as expected: rewritten renderers read later draw its atlas");
                return;
            }
            try
            {
                harmony.Patch(setup, prefix: new HarmonyMethod(typeof(UltrakillStaticOptimizer), nameof(RecordOriginals)));
            }
            catch (Exception ex)
            {
                logger?.LogWarning($"[UltrakillStaticOptimizer] SetupMeshes: {ex.Message}");
            }
        }

        private static void RecordOriginals(object __instance)
        {
            try
            {
                var renderers = renderersField.GetValue(__instance) as IList;
                var data = dataField.GetValue(__instance);
                if (renderers == null || data == null)
                    return;
                var meshes = bakedMeshesField.GetValue(data) as IList;
                var meshIndices = meshIndicesField.GetValue(data) as IList;
                var firstSubMeshes = firstSubMeshField.GetValue(data) as IList;
                if (meshes == null || meshIndices == null || firstSubMeshes == null)
                    return;
                // One optimizer a scene: a new one replaces the last scene's records.
                originals.Clear();
                int count = Math.Min(renderers.Count, Math.Min(meshIndices.Count, firstSubMeshes.Count));
                for (int i = 0; i < count; i++)
                {
                    var renderer = renderers[i] as MeshRenderer;
                    var filter = renderer != null ? renderer.GetComponent<MeshFilter>() : null;
                    if (filter == null || filter.sharedMesh == null)
                        continue;
                    int index = Convert.ToInt32(meshIndices[i]);
                    var baked = index >= 0 && index < meshes.Count ? meshes[index] as Mesh : null;
                    if (baked == null)
                        continue;
                    originals[(baked.GetInstanceID(), Convert.ToInt32(firstSubMeshes[i]))] = (filter.sharedMesh, renderer.sharedMaterials);
                }
                if (VerboseLog.On)
                    log?.LogInfo($"[UltrakillStaticOptimizer] recorded {originals.Count} of {renderers.Count} renderers before the rewrite");
            }
            catch (Exception ex)
            {
                log?.LogWarning($"[UltrakillStaticOptimizer] recording originals failed: {ex.Message}");
            }
        }

        /// <summary>A rewritten renderer's (or its clone's) mesh and materials before the rewrite.</summary>
        public static bool TryGetOriginal(Renderer renderer, out Mesh mesh, out Material[] materials)
        {
            mesh = null;
            materials = null;
            if (originals.Count == 0 || !(renderer is MeshRenderer meshRenderer))
                return false;
            var filter = renderer.GetComponent<MeshFilter>();
            var current = filter != null ? filter.sharedMesh : null;
            if (current == null || !originals.TryGetValue((current.GetInstanceID(), meshRenderer.subMeshStartIndex), out var original) ||
                original.mesh == null)
                return false;
            mesh = original.mesh;
            materials = original.materials;
            return true;
        }
    }
}
