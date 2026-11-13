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
    /// quad per stain, lying on its surface.
    ///
    /// The game draws those quads into a mask (where the splatter atlas is opaque) and
    /// composites it by keeping only the lit frame's red channel there, so overlapping
    /// stains merge and the blood takes the scene's lighting.
    ///
    /// By default the quads are path-traced as OpenRemix multiplicative paint: a decal
    /// instance blending DST_COLOR * ZERO with an atlas that is red where the game's is
    /// opaque. Paint coats the surface just behind it (none where a quad hangs past an
    /// edge) and every ray sees it, so the blood shows in reflections; its finish
    /// (remixapi_MaterialInfoPaintFinishEXT) makes the surface under it glossy, as wet.
    /// With Overlay set and host passes available (ShaderPassThrough.CanDrawStains), the
    /// stains are instead drawn as the game composites them over the path-traced frame:
    /// its exact look, at no tracing cost, but in no reflection.
    /// </summary>
    internal sealed class UltrakillBloodStains
    {
        private const int MeshId = 0x7B100D01;
        private const ulong MeshHash = 0x7B100D0000000001UL;
        // The paint's roughness: wet blood.
        private const float PaintRoughness = 0.12f;
        // How far the paint lies above its surface (OpenRemix attaches paint within 5 cm).
        private const float PaintLift = 0.01f;

        /// <summary>Config: draw the stains over the frame as the game does (host passes) instead of path-traced.</summary>
        public static BepInEx.Configuration.ConfigEntry<bool> Overlay;

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
        // The host-pass stains: (x, y, z, u, v) per vertex, and whether any were sent.
        private float[] stainVertices;
        private bool stainsSent;
        private bool overlayActive;
        // The paint's material: the atlas, red where the game's is opaque.
        private Texture paintAtlas;
        private Texture2D paintTexture;
        private int paintMaterialId;

        public bool IsStainMaterial(Material material) =>
            material != null && (material == stainMaterial ||
                                 material.name.IndexOf("bloodstain", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                 (material.shader != null && material.shader.name.IndexOf("bloodstain", StringComparison.OrdinalIgnoreCase) >= 0));

        public void Reset()
        {
            manager = null;
            vertices = null;
            capturedCount = capturedVerts = -1;
            ClearHostStains();
        }

        private void ClearHostStains()
        {
            if (!stainsSent)
                return;
            ShaderPassThrough.SetStains(null, 0, null);
            stainsSent = false;
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
                else
                {
                    ClearHostStains();
                    capturedCount = capturedVerts = -1;
                }
                return;
            }

            bool overlay = Overlay != null && Overlay.Value && ShaderPassThrough.CanDrawStains;
            if (overlay != overlayActive)
            {
                overlayActive = overlay;
                capturedCount = capturedVerts = -1;
                vertices = null;
                ClearHostStains();
            }
            if (overlay)
            {
                if (!stainsSent || count != capturedCount || mesh.vertexCount != capturedVerts)
                {
                    int quads = ExtractHost(mesh);
                    var atlas = stainMaterial == null ? null
                        : stainMaterial.HasProperty("_SplatterAtlas") ? stainMaterial.GetTexture("_SplatterAtlas") : stainMaterial.mainTexture;
                    ShaderPassThrough.SetStains(stainVertices, quads, atlas);
                    stainsSent = quads > 0 && atlas != null;
                    capturedCount = count;
                    capturedVerts = mesh.vertexCount;
                }
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

            int materialId = PaintMaterial(logger, materials);
            if (materialId == 0)
                return;

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
                bakeFrame = version,
                categoryFlags = (uint)RemixAPI.remixapi_InstanceCategoryBit.REMIXAPI_INSTANCE_CATEGORY_BIT_DECAL_DYNAMIC,
                multiplyPaint = true
            });
        }

        // The paint's material (once per atlas): red where the game's atlas is opaque
        // (its stain shader's alpha == 1 test), transparent elsewhere; wet.
        private int PaintMaterial(ManualLogSource logger, RemixMaterialManager materials)
        {
            var atlas = stainMaterial == null ? null
                : stainMaterial.HasProperty("_SplatterAtlas") ? stainMaterial.GetTexture("_SplatterAtlas") : stainMaterial.mainTexture;
            if (atlas == null)
                return paintMaterialId;
            if (atlas == paintAtlas)
                return paintMaterialId;
            paintAtlas = atlas;
            var rt = RenderTexture.GetTemporary(atlas.width, atlas.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            var previous = RenderTexture.active;
            try
            {
                Graphics.Blit(atlas, rt);
                RenderTexture.active = rt;
                var read = new Texture2D(atlas.width, atlas.height, TextureFormat.RGBA32, false, true);
                read.ReadPixels(new Rect(0, 0, atlas.width, atlas.height), 0, 0, false);
                var pixels = read.GetPixels32();
                UnityEngine.Object.Destroy(read);
                for (int i = 0; i < pixels.Length; i++)
                    pixels[i] = pixels[i].a >= 254 ? new Color32(255, 0, 0, 255) : new Color32(255, 0, 0, 0);
                if (paintTexture == null || paintTexture.width != atlas.width || paintTexture.height != atlas.height)
                    paintTexture = new Texture2D(atlas.width, atlas.height, TextureFormat.RGBA32, false, true)
                    {
                        name = "UnityRTX blood paint",
                        filterMode = FilterMode.Point,
                        wrapMode = TextureWrapMode.Clamp
                    };
                paintTexture.SetPixels32(pixels);
                paintTexture.Apply(false, false);
                paintMaterialId = materials.RegisterCustomMaterial("UnityRTX blood paint " + atlas.GetInstanceID(), paintTexture,
                    roughness: PaintRoughness, metallic: 0f, alphaCutoff: 0.5f, paintFinish: true);
                logger.LogInfo($"[UltrakillBloodStains] blood paint from '{atlas.name}' {atlas.width}x{atlas.height}");
            }
            catch (Exception e)
            {
                logger.LogWarning($"[UltrakillBloodStains] reading the splatter atlas failed: {e.Message}");
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(rt);
            }
            return paintMaterialId;
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

        // The host-pass stains: each quad where it lies, mapped to its atlas cell. Quads.
        private int ExtractHost(Mesh mesh)
        {
            Vector3[] rawVerts = mesh.vertices;
            Vector2[] rawUvs = mesh.uv;
            if (rawVerts == null || rawVerts.Length < 4)
                return 0;
            int quadCount = rawVerts.Length / 4;
            bool hasUvs = rawUvs != null && rawUvs.Length == rawVerts.Length;
            int floats = quadCount * 4 * 5;
            if (stainVertices == null || stainVertices.Length < floats)
                stainVertices = new float[Mathf.NextPowerOfTwo(floats)];
            for (int s = 0; s < quadCount; s++)
            {
                // _SplatterAtlas holds 5 splatters side by side: cell (index % 5), as the game's
                // instanced shader picks it.
                float uOffset = (s % 5) * 0.2f;
                for (int k = 0; k < 4; k++)
                {
                    int v = s * 4 + k, o = v * 5;
                    Vector3 p = rawVerts[v];
                    Vector2 raw = hasUvs ? rawUvs[v] : new Vector2(k == 1 || k == 2 ? 1f : 0f, k >= 2 ? 1f : 0f);
                    stainVertices[o] = p.x;
                    stainVertices[o + 1] = p.y;
                    stainVertices[o + 2] = p.z;
                    stainVertices[o + 3] = uOffset + raw.x * 0.2f;
                    stainVertices[o + 4] = raw.y;
                }
            }
            return quadCount;
        }

        // One quad per stain: just above its surface (paint), wound to face out, mapped to its atlas cell.
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

                // The game stores each stain's normal into its surface (-hit.normal): out of it.
                Vector3 normal = hasNorms ? -rawNorms[vBase] : Vector3.zero;
                if (normal.sqrMagnitude < 0.001f)
                    normal = Vector3.Cross(v2 - v0, v1 - v0).normalized;
                if (normal.sqrMagnitude < 0.001f)
                    normal = Vector3.up;

                // In front of the surface it coats: the paint peel finds paint before its receiver.
                Vector3 offset = normal * PaintLift;

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
