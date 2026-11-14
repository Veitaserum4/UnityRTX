using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace UnityRemix.Games.Ultrakill
{
    /// <summary>
    /// Firestarter oil on walls and floors (GasolineStain). With compute shaders on, the stains
    /// are drawn by StainVoxelManager from an offscreen CommandBuffer; with them off, each
    /// stain's own MeshRenderer (ULTRAKILL/Oilstain_Fallback) is enabled. Either way the game
    /// multiplies the scene by its colour: Oilstain_Fallback (disassembled) keeps the texels of
    /// its splatter atlas cell (_Index) where alpha exceeds red by 0.01 and multiplies by
    /// _OilSlick (an interference ramp) at (noise + 2 (1 - |N.V|)^2, noise x 5), the noise
    /// _SandTex sampled triplanar in world space, times red squared, plus (0.32, 0, 0.55):
    /// a dark purple stain with a rainbow sheen.
    ///
    /// Every stain (GasolineStain.AttachTo, in both paths) is path-traced as OpenRemix
    /// multiplicative paint, as the blood stains are (UltrakillBloodStains): one quad per stain
    /// just above its surface, its texture that multiplier with the ramp baked in at an average
    /// view (a noise read across each atlas cell standing in for the world-space one), and a
    /// finish that makes the surface under it glossy. With Film, the paint carries a thin film
    /// instead (remixapi_MaterialIridescenceEXT, which OpenRemix's paint finish hands to the
    /// surface it coats) and the ramp's mean colour replaces the baked ramp: the rainbow is
    /// the film's, seen in the reflection and swirling. The fallback renderers are hidden
    /// (IsStainMaterial).
    /// </summary>
    internal sealed class UltrakillOilStains
    {
        private const int MeshId = 0x7B100D02;
        private const ulong MeshHash = 0x7B100D0000000002UL;
        private const float PaintRoughness = 0.08f;
        private const float PaintLift = 0.012f;
        // The paint texture: the 5-cell atlas at this many texels per cell side.
        private const int CellSize = 128;
        // 2 (1 - |N.V|)^2 at an average view: where the ramp is read beside the noise.
        private const float AverageGrazing = 0.15f;

        /// <summary>Config: the paint carries a thin film (needs OpenRemix's paint film) instead of the baked ramp.</summary>
        public static BepInEx.Configuration.ConfigEntry<bool> Film;

        // The film over a stain: an oil slick's swirling second-order colours (flow in metres:
        // the stain mesh is in world space).
        private static readonly RemixMaterialManager.Iridescence StainFilm = new RemixMaterialManager.Iridescence
        {
            strength = 1f,
            filmIndex = 1.7f,
            thickness = 450f,
            flowAmplitude = 350f,
            flowScale = 0.75f,
            flowSpeed = 0.15f,
            structural = 0f,
            albedoScale = 1f,
            roughness = PaintRoughness
        };

        private static readonly HashSet<Component> stains = new HashSet<Component>();
        private static readonly List<Component> stainList = new List<Component>();
        private static bool stainsChanged;

        private static readonly int SplatterAtlas = Shader.PropertyToID("_SplatterAtlas");
        private static readonly int OilSlick = Shader.PropertyToID("_OilSlick");
        private static readonly int SandTex = Shader.PropertyToID("_SandTex");
        private static readonly int StainIndex = Shader.PropertyToID("_Index");

        public static void Apply(Harmony harmony, ManualLogSource logger)
        {
            var type = AccessTools.TypeByName("GasolineStain");
            var method = type?.GetMethod("AttachTo", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (method == null)
            {
                logger.LogInfo("[UltrakillOilStains] GasolineStain.AttachTo not found: oil stains not path-traced");
                return;
            }
            try
            {
                harmony.Patch(method, postfix: new HarmonyMethod(typeof(UltrakillOilStains), nameof(OnAttached)));
            }
            catch (Exception e)
            {
                logger.LogWarning($"[UltrakillOilStains] patching GasolineStain.AttachTo failed: {e.Message}");
            }
        }

        private static void OnAttached(Component __instance)
        {
            if (__instance != null && stains.Add(__instance))
                stainsChanged = true;
        }

        public static bool IsStainMaterial(Material material) =>
            material != null && material.shader != null && material.shader.name == "ULTRAKILL/Oilstain_Fallback";

        private readonly List<Matrix4x4> matrices = new List<Matrix4x4>();
        private readonly List<int> cells = new List<int>();
        private int version;
        private Vector3[] vertices, normals;
        private Vector2[] uvs;
        private Color32[] colors;
        private int[] triangles;

        private Mesh quadMesh;
        private Vector3[] quadVertices;
        private Vector2[] quadUvs;
        private int[] quadTriangles;

        private Texture paintAtlas;
        private bool paintFilm;
        private Texture2D paintTexture;
        private int paintMaterialId;
        private bool paintFailed;

        // The last level's stains are destroyed with it and drop out of the set on the next
        // capture; the set is kept, as stains can attach while the new scene loads.
        public void Reset()
        {
            matrices.Clear();
            cells.Clear();
            vertices = null;
        }

        public void Capture(ManualLogSource logger, RemixMaterialManager materials, RemixFrameCapture.FrameState state)
        {
            if (stains.Count == 0)
                return;

            if (stainsChanged)
            {
                stainsChanged = false;
                stainList.Clear();
                stainList.AddRange(stains);
            }

            // Burnt or cleared stains are destroyed; pooled ones go inactive.
            bool changed = false;
            int live = 0;
            Material stainMaterial = null;
            for (int i = 0; i < stainList.Count; i++)
            {
                var stain = stainList[i];
                if (stain == null)
                {
                    stains.Remove(stain);
                    continue;
                }
                if (!stain.gameObject.activeInHierarchy)
                    continue;
                if (quadMesh == null && !ReadQuad(stain))
                    continue;
                var m = stain.transform.localToWorldMatrix;
                if (live >= matrices.Count)
                {
                    matrices.Add(m);
                    cells.Add(Cell(stain, ref stainMaterial));
                    changed = true;
                }
                else if (matrices[live] != m)
                {
                    matrices[live] = m;
                    cells[live] = Cell(stain, ref stainMaterial);
                    changed = true;
                }
                if (stainMaterial == null && stain.TryGetComponent<MeshRenderer>(out var r))
                    stainMaterial = r.sharedMaterial;
                live++;
            }
            if (stainList.RemoveAll(s => s == null) > 0)
                changed = true;
            if (live != matrices.Count)
            {
                matrices.RemoveRange(live, matrices.Count - live);
                cells.RemoveRange(live, cells.Count - live);
                changed = true;
            }
            if (live == 0 || quadMesh == null)
                return;

            int materialId = PaintMaterial(logger, materials, stainMaterial);
            if (materialId == 0)
                return;

            if (changed || vertices == null)
            {
                Build();
                version++;
            }

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

        // The atlas cell the game draws: the fallback renderer's _Index, else one fixed per stain.
        private MaterialPropertyBlock cellBlock;
        private int Cell(Component stain, ref Material stainMaterial)
        {
            if (stain.TryGetComponent<MeshRenderer>(out var r) && r.enabled && r.HasPropertyBlock())
            {
                if (cellBlock == null)
                    cellBlock = new MaterialPropertyBlock();
                r.GetPropertyBlock(cellBlock);
                int index = cellBlock.GetInt(StainIndex);
                if (index >= 0 && index < 5)
                    return index;
            }
            return (int)((uint)stain.GetInstanceID() % 5u);
        }

        // The stain prefab's quad (BloodPlane), in the stain's space.
        private bool ReadQuad(Component stain)
        {
            if (!stain.TryGetComponent<MeshFilter>(out var filter) || filter.sharedMesh == null)
                return false;
            var mesh = filter.sharedMesh;
            if (!mesh.isReadable)
                return false;
            quadVertices = mesh.vertices;
            quadUvs = mesh.uv;
            quadTriangles = mesh.triangles;
            if (quadVertices == null || quadVertices.Length == 0 || quadTriangles == null || quadTriangles.Length == 0)
                return false;
            if (quadUvs == null || quadUvs.Length != quadVertices.Length)
                quadUvs = new Vector2[quadVertices.Length];
            quadMesh = mesh;
            return true;
        }

        private void Build()
        {
            int count = matrices.Count, qv = quadVertices.Length, qt = quadTriangles.Length;
            int vertexCount = count * qv;
            if (vertices == null || vertices.Length != vertexCount)
            {
                vertices = new Vector3[vertexCount];
                normals = new Vector3[vertexCount];
                uvs = new Vector2[vertexCount];
                colors = new Color32[vertexCount];
                for (int i = 0; i < vertexCount; i++) colors[i] = new Color32(255, 255, 255, 255);
                triangles = new int[count * qt];
            }
            else
            {
                // A new array: the render thread may still be reading the last frame's.
                vertices = (Vector3[])vertices.Clone();
                normals = (Vector3[])normals.Clone();
                uvs = (Vector2[])uvs.Clone();
                triangles = (int[])triangles.Clone();
            }
            for (int s = 0; s < count; s++)
            {
                var m = matrices[s];
                // The stain's forward points into its surface (GasolineStain.CalculateDot).
                Vector3 normal = -m.MultiplyVector(Vector3.forward).normalized;
                if (normal.sqrMagnitude < 0.5f) normal = Vector3.up;
                Vector3 lift = normal * PaintLift;
                float uOffset = cells[s] * 0.2f;
                int vBase = s * qv;
                for (int k = 0; k < qv; k++)
                {
                    vertices[vBase + k] = m.MultiplyPoint3x4(quadVertices[k]) + lift;
                    normals[vBase + k] = normal;
                    Vector2 uv = quadUvs[k];
                    uvs[vBase + k] = new Vector2(uOffset + Mathf.Clamp01(uv.x) * 0.2f, uv.y);
                }
                // Facing out of the surface, whichever way the quad was wound.
                Vector3 a = vertices[vBase + quadTriangles[0]], b = vertices[vBase + quadTriangles[1]], c = vertices[vBase + quadTriangles[2]];
                bool flip = Vector3.Dot(Vector3.Cross(b - a, c - a), normal) < 0f;
                int tBase = s * qt;
                for (int t = 0; t < qt; t += 3)
                {
                    triangles[tBase + t] = vBase + quadTriangles[t];
                    triangles[tBase + t + 1] = vBase + quadTriangles[flip ? t + 2 : t + 1];
                    triangles[tBase + t + 2] = vBase + quadTriangles[flip ? t + 1 : t + 2];
                }
            }
        }

        // The paint's material (once per atlas): Oilstain_Fallback's multiplier, the ramp read
        // at an average view, opaque where the stain is kept (alpha - red > 0.01).
        private int PaintMaterial(ManualLogSource logger, RemixMaterialManager materials, Material stainMaterial)
        {
            var atlas = stainMaterial != null && stainMaterial.HasProperty(SplatterAtlas) ? stainMaterial.GetTexture(SplatterAtlas) : null;
            bool film = Film == null || Film.Value;
            if (atlas == null || (atlas == paintAtlas && film == paintFilm) || paintFailed)
                return paintMaterialId;
            var slick = Shader.GetGlobalTexture(OilSlick);
            var sand = Shader.GetGlobalTexture(SandTex);
            if (slick == null || sand == null)
                return paintMaterialId;
            paintAtlas = atlas;
            paintFilm = film;
            try
            {
                var atlasPixels = Readable(atlas);
                var slickPixels = Readable(slick);
                var sandPixels = Readable(sand);
                bool linearSpace = QualitySettings.activeColorSpace == ColorSpace.Linear;
                // With the film, one flat colour for every stain texel: the ramp's mean at the
                // mean coverage. The film draws the rainbow itself, and overlapping stains (which
                // tint once, by the darkest) then show no stain's outline inside another.
                Color meanRamp = film ? Mean(slickPixels) : Color.clear;
                float meanCover = film ? MeanCover(atlasPixels) : 0f;
                int width = CellSize * 5, height = CellSize;
                var pixels = new Color32[width * height];
                // The texels outside a stain (alpha 0) carry the stain's colour where it is flat,
                // so filtering at a stain's edge blends no white into it (a light rim, which
                // showed inside overlapping stains).
                var outside = new Color32(255, 255, 255, 0);
                if (film)
                {
                    var flat = new Color(Mathf.Clamp01(meanRamp.r * meanCover + 0.32f), Mathf.Clamp01(meanRamp.g * meanCover),
                                         Mathf.Clamp01(meanRamp.b * meanCover + 0.55f), 1f);
                    outside = linearSpace ? (Color32)flat.gamma : (Color32)flat;
                    outside.a = 0;
                }
                for (int y = 0; y < height; y++)
                {
                    float v = (y + 0.5f) / height;
                    for (int x = 0; x < width; x++)
                    {
                        float u = (x + 0.5f) / width;
                        int cell = x / CellSize;
                        Color splat = atlasPixels.GetPixelBilinear(u, v);
                        if (splat.a - splat.r <= 0.01f)
                        {
                            pixels[y * width + x] = outside;
                            continue;
                        }
                        float lu = (x % CellSize + 0.5f) / CellSize;
                        float n = sandPixels.GetPixelBilinear(lu * 0.35f + cell * 0.37f, v * 0.35f + cell * 0.61f).r;
                        Color ramp = film ? meanRamp : slickPixels.GetPixelBilinear(n + AverageGrazing, n * 5f);
                        float cover = film ? meanCover : splat.r * splat.r;
                        var m = new Color(
                            Mathf.Clamp01(ramp.r * cover + 0.32f),
                            Mathf.Clamp01(ramp.g * cover),
                            Mathf.Clamp01(ramp.b * cover + 0.55f), 1f);
                        // OpenRemix reads the texture as sRGB. In a gamma project the game's
                        // multiply happens on gamma values, which that decode turns linear.
                        pixels[y * width + x] = linearSpace ? (Color32)m.gamma : (Color32)m;
                    }
                }
                UnityEngine.Object.Destroy(atlasPixels);
                UnityEngine.Object.Destroy(slickPixels);
                UnityEngine.Object.Destroy(sandPixels);
                if (paintTexture == null)
                    paintTexture = new Texture2D(width, height, TextureFormat.RGBA32, false, false)
                    {
                        name = "UnityRTX oil paint",
                        filterMode = FilterMode.Bilinear,
                        wrapMode = TextureWrapMode.Clamp
                    };
                paintTexture.SetPixels32(pixels);
                paintTexture.Apply(false, false);
                paintMaterialId = materials.RegisterCustomMaterial((film ? "UnityRTX oil film paint " : "UnityRTX oil paint ") + atlas.GetInstanceID(),
                    paintTexture, roughness: PaintRoughness, metallic: 0f, alphaCutoff: 0.5f, paintFinish: true,
                    film: film ? StainFilm : (RemixMaterialManager.Iridescence?)null);
                logger.LogInfo($"[UltrakillOilStains] oil paint from '{atlas.name}', ramp '{slick.name}', noise '{sand.name}', film {(film ? "on" : "off")}");
            }
            catch (Exception e)
            {
                paintFailed = true;
                logger.LogWarning($"[UltrakillOilStains] baking the oil paint failed: {e.Message}");
            }
            return paintMaterialId;
        }

        // The mean red squared over the atlas texels a stain keeps (alpha - red > 0.01).
        private static float MeanCover(Texture2D atlas)
        {
            var pixels = atlas.GetPixels();
            float sum = 0f;
            int kept = 0;
            for (int i = 0; i < pixels.Length; i++)
            {
                if (pixels[i].a - pixels[i].r <= 0.01f) continue;
                sum += pixels[i].r * pixels[i].r;
                kept++;
            }
            return kept > 0 ? sum / kept : 0f;
        }

        private static Color Mean(Texture2D texture)
        {
            var pixels = texture.GetPixels();
            Color sum = Color.clear;
            for (int i = 0; i < pixels.Length; i++) sum += pixels[i];
            return pixels.Length > 0 ? sum / pixels.Length : Color.grey;
        }

        // A CPU copy of a texture as the shader reads it (a linear target: no conversion on write),
        // repeating as the game's samplers do.
        private static Texture2D Readable(Texture texture)
        {
            var rt = RenderTexture.GetTemporary(texture.width, texture.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            var previous = RenderTexture.active;
            try
            {
                Graphics.Blit(texture, rt);
                RenderTexture.active = rt;
                var read = new Texture2D(texture.width, texture.height, TextureFormat.RGBA32, false, true)
                {
                    wrapMode = TextureWrapMode.Repeat,
                    filterMode = FilterMode.Bilinear
                };
                read.ReadPixels(new Rect(0, 0, texture.width, texture.height), 0, 0, false);
                read.Apply(false, false);
                return read;
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(rt);
            }
        }
    }
}
