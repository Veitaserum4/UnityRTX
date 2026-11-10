using System;
using System.Reflection;
using BepInEx.Logging;
using UnityEngine;

namespace UnityRemix.Games.Ultrakill
{
    /// <summary>
    /// ULTRAKILL draws blood stains on walls and floors from an offscreen CommandBuffer,
    /// which Remix never sees. With compute shaders off, BloodsplatterManager's CPU job
    /// (GenerateBloodMeshJob) combines every stain into totalStainMesh: one world-space
    /// quad per stain. That mesh is drawn as one dynamic mesh, re-uploaded only when the
    /// stains change.
    /// </summary>
    internal sealed class UltrakillBloodStains
    {
        private const int MeshId = 0x7B100D01;
        private const ulong MeshHash = 0x7B100D0000000001UL;

        // Albedo color of the stain material: ULTRAKILL's stain shader colors the splatter
        // atlas itself, which Remix doesn't run.
        public static readonly Color Tint = new Color(0.70f, 0.02f, 0.02f, 0.75f);

        private bool reflected;
        private Type managerType;
        private FieldInfo countField, meshField, materialField, usedComputeField, meshDirtyField;
        private MethodInfo rebuildMethod;

        private UnityEngine.Object manager;
        private Material stainMaterial;
        private int capturedCount = -1, capturedVerts = -1;
        private int version;
        private Vector3[] vertices, normals;
        private Vector2[] uvs;
        private Color32[] colors;
        private int[] triangles;

        public bool IsStainMaterial(Material material) =>
            material != null && (material == stainMaterial ||
                                 material.name.IndexOf("bloodstain", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                 (material.shader != null && material.shader.name.IndexOf("bloodstain", StringComparison.OrdinalIgnoreCase) >= 0));

        public void Reset()
        {
            manager = null;
            vertices = null;
            capturedCount = capturedVerts = -1;
        }

        public void Capture(ManualLogSource logger, RemixMaterialManager materials, RemixFrameCapture.FrameState state)
        {
            if (!reflected)
                Reflect(logger);
            if (managerType == null)
                return;

            if (manager == null)
                manager = UnityEngine.Object.FindObjectOfType(managerType);
            if (manager == null)
                return;

            // The compute path never builds totalStainMesh: switch to the CPU job.
            if (usedComputeField != null && (bool)usedComputeField.GetValue(manager))
            {
                usedComputeField.SetValue(manager, false);
                Rebuild();
                logger.LogInfo("[UltrakillBloodStains] Switched BloodsplatterManager to the CPU mesh path");
            }

            int count = countField != null ? (int)countField.GetValue(manager) : 0;
            var mesh = meshField?.GetValue(manager) as Mesh;
            stainMaterial = materialField?.GetValue(manager) as Material;

            if (count <= 0 || mesh == null || mesh.vertexCount == 0)
            {
                // Stains exist but the mesh is empty: ask for it.
                if (count > 0)
                    Rebuild();
                return;
            }

            if (vertices == null || count != capturedCount || mesh.vertexCount != capturedVerts)
            {
                if (!Extract(mesh))
                    return;
                capturedCount = count;
                capturedVerts = mesh.vertexCount;
                version++;
            }

            // The scene scan may capture this material first (its fallback quad): the tint and
            // blending come from UltrakillProfile.OverrideMaterial either way.
            int materialId = stainMaterial != null ? stainMaterial.GetInstanceID() : MeshId;
            if (stainMaterial != null)
                materials.CaptureMaterialTextures(stainMaterial, materialId);

            state.skinned.Add(new RemixFrameCapture.SkinnedMeshData
            {
                source = RemixFrameCapture.DynamicSource.Game,
                meshId = MeshId,
                remixMeshHash = MeshHash,
                materialId = materialId,
                vertices = vertices,
                normals = normals,
                uvs = uvs,
                colors = colors,
                triangles = triangles,
                localToWorld = Matrix4x4.identity,
                bakeFrame = version
            });
        }

        private void Rebuild()
        {
            meshDirtyField?.SetValue(manager, true);
            rebuildMethod?.Invoke(manager, null);
        }

        private void Reflect(ManualLogSource logger)
        {
            reflected = true;
            const BindingFlags instance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            bool computeDisabled = false;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type t = null;
                if (managerType == null)
                {
                    try { t = asm.GetType("BloodsplatterManager"); } catch { }
                    if (t != null)
                    {
                        managerType = t;
                        countField = t.GetField("currentBloodCount", instance);
                        meshField = t.GetField("totalStainMesh", instance);
                        materialField = t.GetField("stainMat", instance);
                        usedComputeField = t.GetField("usedComputeShadersAtStart", instance);
                        meshDirtyField = t.GetField("meshDirty", instance);
                        rebuildMethod = t.GetMethod("RebuildMesh", instance);
                    }
                }
                if (!computeDisabled)
                {
                    try { t = asm.GetType("SettingsMenu.Components.Pages.GraphicsSettings"); } catch { t = null; }
                    var field = t?.GetField("disabledComputeShaders", BindingFlags.Public | BindingFlags.Static);
                    if (field != null)
                    {
                        field.SetValue(null, true);
                        computeDisabled = true;
                    }
                }
            }
            logger.LogInfo($"[UltrakillBloodStains] BloodsplatterManager {(managerType != null ? "found" : "not found")}, compute shaders {(computeDisabled ? "disabled" : "left on")}");
        }

        // One quad per stain: lifted off its surface, wound to face out, mapped to its atlas cell.
        private bool Extract(Mesh mesh)
        {
            Vector3[] rawVerts = mesh.vertices;
            Vector3[] rawNorms = mesh.normals;
            Vector2[] rawUvs = mesh.uv;
            if (rawVerts == null || rawVerts.Length < 4)
                return false;

            int quadCount = rawVerts.Length / 4;
            int vertexCount = quadCount * 4;
            var verts = new Vector3[vertexCount];
            var norms = new Vector3[vertexCount];
            var uv = new Vector2[vertexCount];
            var tris = new int[quadCount * 6];
            bool hasNorms = rawNorms != null && rawNorms.Length == rawVerts.Length;
            bool hasUvs = rawUvs != null && rawUvs.Length == rawVerts.Length;

            for (int s = 0; s < quadCount; s++)
            {
                int vBase = s * 4;
                Vector3 v0 = rawVerts[vBase], v1 = rawVerts[vBase + 1], v2 = rawVerts[vBase + 2];

                Vector3 normal = hasNorms ? rawNorms[vBase] : Vector3.zero;
                if (normal.sqrMagnitude < 0.001f)
                    normal = Vector3.Cross(v2 - v0, v1 - v0).normalized;
                if (normal.sqrMagnitude < 0.001f)
                    normal = Vector3.up;

                // 12 mm off the surface, plus 0.5 mm per layer (16 layers): clears brush seams
                // and keeps overlapping stains from z-fighting.
                Vector3 offset = normal * (0.012f + (s % 16) * 0.0005f);

                // _SplatterAtlas holds 5 splatters side by side; ULTRAKILL's vertex shader picks
                // cell (index % 5) by scaling u by 0.2.
                float uOffset = (s % 5) * 0.2f;
                for (int k = 0; k < 4; k++)
                {
                    verts[vBase + k] = rawVerts[vBase + k] + offset;
                    norms[vBase + k] = normal;
                    Vector2 raw = hasUvs ? rawUvs[vBase + k] : new Vector2(k == 1 || k == 2 ? 1f : 0f, k >= 2 ? 1f : 0f);
                    uv[vBase + k] = new Vector2(uOffset + raw.x * 0.2f, raw.y);
                }

                // (0, 2, 1) and (0, 3, 2): facing out into the room.
                int tBase = s * 6;
                tris[tBase + 0] = vBase;
                tris[tBase + 1] = vBase + 2;
                tris[tBase + 2] = vBase + 1;
                tris[tBase + 3] = vBase;
                tris[tBase + 4] = vBase + 3;
                tris[tBase + 5] = vBase + 2;
            }

            if (colors == null || colors.Length != vertexCount)
            {
                // The atlas is already blood red.
                colors = new Color32[vertexCount];
                for (int i = 0; i < vertexCount; i++) colors[i] = new Color32(255, 255, 255, 255);
            }
            vertices = verts;
            normals = norms;
            uvs = uv;
            triangles = tris;
            return true;
        }
    }
}
