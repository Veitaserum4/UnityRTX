using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using BepInEx.Logging;
using UnityEngine;

namespace UnityRemix
{
    // Generated PBR (experimental): roughness and metalness inferred from names and albedo, normal maps from albedo.
    public partial class RemixMaterialManager
    {
        #region Auto PBR & Normal Map Generation (Experimental)

        private struct PbrTaxonomyEntry
        {
            public string categoryName;
            public float metallic;
            public float roughness;
            public float normalStrength; // scales generated normal maps (polished: weak, masonry: strong)
            public string[] keywords;

            public PbrTaxonomyEntry(string categoryName, float metallic, float roughness, float normalStrength, params string[] keywords)
            {
                this.categoryName = categoryName;
                this.metallic = metallic;
                this.roughness = roughness;
                this.normalStrength = normalStrength;
                this.keywords = keywords;
            }
        }

        private static readonly PbrTaxonomyEntry[] PbrTaxonomy = new PbrTaxonomyEntry[]
        {
            // 1. Precious & Polished Metals
            new PbrTaxonomyEntry("Chrome/Mirror", 1.0f, 0.05f, 0.25f,
                "chrome", "mirror", "silver", "polished_metal", "specular_metal"),
            new PbrTaxonomyEntry("Gold", 1.0f, 0.15f, 0.4f,
                "gold", "golden", "gilded", "brass", "bronze", "copper"),

            // 2. Structural & Industrial Metals
            new PbrTaxonomyEntry("Smooth Metal/Steel", 1.0f, 0.25f, 0.5f,
                "steel", "iron", "aluminum", "metal_smooth", "metallic", "metal_trim", "gunmetal"),
            new PbrTaxonomyEntry("Standard Metal/Machinery", 1.0f, 0.38f, 0.7f,
                "metal", "machinery", "machine", "armor", "armour", "chassis", "engine", "hull",
                "pipe", "pipeline", "barrel", "rebar", "grate", "grating", "girder", "beam",
                "scaffold", "vent", "panel_metal", "sheet_metal", "weapon", "sword", "blade", "bullet"),
            new PbrTaxonomyEntry("Rusted/Oxidized Metal", 0.35f, 0.75f, 1.2f,
                "rust", "rusted", "rusty", "corrosion", "corroded", "oxidized"),

            // 3. High-Gloss Dielectrics
            new PbrTaxonomyEntry("Marble/Polished Stone", 0.0f, 0.12f, 0.3f,
                "marble", "porcelain", "polished_stone", "polished_rock", "granite_polished"),
            new PbrTaxonomyEntry("Ceramic/Tile", 0.0f, 0.18f, 0.5f,
                "ceramic", "tile", "tiles", "tiling", "mosaic", "glazed"),
            new PbrTaxonomyEntry("Glossy Plastic/Car Paint/Glassy", 0.0f, 0.22f, 0.3f,
                "plastic", "shiny", "glossy", "varnish", "acrylic", "lacquer", "enamel", "polymer"),
            new PbrTaxonomyEntry("Crystal/Gemstone", 0.0f, 0.10f, 0.2f,
                "crystal", "gem", "gemstone", "diamond", "ruby", "emerald", "sapphire", "amethyst", "obsidian"),

            // 4. Semi-Gloss & Organic Surfaces
            new PbrTaxonomyEntry("Polished Wood", 0.0f, 0.28f, 0.5f,
                "wood_polished", "parquet", "varnished_wood", "lacquered_wood", "wood_shiny"),
            new PbrTaxonomyEntry("Leather/Rubber", 0.0f, 0.55f, 0.8f,
                "leather", "rubber", "tire", "vinyl", "tarp", "latex"),
            new PbrTaxonomyEntry("Flesh/Skin", 0.0f, 0.42f, 0.7f,
                "skin", "flesh", "meat", "organ", "muscle", "guts", "viscera", "tongue"),

            // 5. Standard Matte & Rough Surfaces
            new PbrTaxonomyEntry("Rough Wood/Bark", 0.0f, 0.65f, 1.1f,
                "wood", "plank", "planks", "timber", "bark", "log", "branch", "lumber", "fence", "crate", "barrel_wood"),
            new PbrTaxonomyEntry("Cardboard/Paper", 0.0f, 0.82f, 0.5f,
                "paper", "cardboard", "carton", "book", "scroll", "poster", "document"),
            new PbrTaxonomyEntry("Foliage/Vegetation", 0.0f, 0.60f, 0.8f,
                "grass", "leaf", "leaves", "foliage", "moss", "plant", "bush", "vine", "tree", "flora"),
            new PbrTaxonomyEntry("Stone/Rock/Masonry", 0.0f, 0.85f, 1.4f,
                "stone", "rock", "boulder", "cliff", "cobble", "cobblestone", "masonry", "slate", "granite", "monument"),
            new PbrTaxonomyEntry("Concrete/Brick/Asphalt", 0.0f, 0.88f, 1.3f,
                "concrete", "brick", "bricks", "asphalt", "cement", "pavement", "sidewalk", "plaster", "stucco"),
            new PbrTaxonomyEntry("Fabric/Cloth/Carpet", 0.0f, 0.92f, 0.9f,
                "fabric", "cloth", "carpet", "rug", "canvas", "curtain", "flag", "banner", "linen", "wool",
                "cotton", "towel", "blanket", "cushion", "upholstery", "velvet", "silk"),
            new PbrTaxonomyEntry("Dirt/Mud/Sand/Ground", 0.0f, 0.95f, 1.2f,
                "dirt", "soil", "mud", "sand", "ground", "earth", "gravel", "dust", "terrain", "clay")
        };

        private bool InferPbrProperties(Material material, Texture2D albedoTex, Renderer renderer, string shaderName, out float inferredMetallic, out float inferredRoughness, out string matchedCategory, out float normalStrength)
        {
            inferredMetallic = 0.0f;
            inferredRoughness = 0.7f;
            matchedCategory = null;
            normalStrength = 1f;

            var names = new[]
            {
                NameTokens(material != null ? material.name : null),
                NameTokens(albedoTex != null ? albedoTex.name : null),
                NameTokens(renderer != null ? renderer.gameObject.name : null),
                NameTokens(shaderName)
            };

            for (int i = 0; i < PbrTaxonomy.Length; i++)
            {
                var entry = PbrTaxonomy[i];
                foreach (var tokens in names)
                {
                    if (!MatchesPbrKeyword(tokens, entry.keywords))
                        continue;
                    inferredMetallic = entry.metallic;
                    inferredRoughness = entry.roughness;
                    matchedCategory = entry.categoryName;
                    normalStrength = entry.normalStrength;
                    return true;
                }
            }

            return false;
        }

        // Every taxonomy keyword, and words that commonly follow one in a compound name
        // (METALFLOOR, woodplank): a keyword prefix followed by one of these still matches.
        private static readonly HashSet<string> PbrCompoundTails = BuildPbrCompoundTails();

        private static HashSet<string> BuildPbrCompoundTails()
        {
            var tails = new HashSet<string> { "floor", "wall", "plate", "panel", "trim", "door", "roof", "ceiling",
                "pillar", "block", "base", "top", "side", "dark", "light", "old", "new", "detail", "mat", "material", "tex", "texture" };
            foreach (var entry in PbrTaxonomy)
                foreach (var keyword in entry.keywords)
                    if (keyword.IndexOf('_') < 0)
                        tails.Add(keyword);
            return tails;
        }

        /// <summary>
        /// A name's lower-case words: split at separators, digits and camelCase humps
        /// ("M_MetalFloor02" -> m, metal, floor).
        /// </summary>
        private static List<string> NameTokens(string name)
        {
            var tokens = new List<string>();
            if (string.IsNullOrEmpty(name))
                return tokens;
            var current = new System.Text.StringBuilder();
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                bool boundary = !char.IsLetter(c) ||
                                (current.Length > 0 && char.IsUpper(c) && char.IsLower(name[i - 1])) ||
                                (current.Length > 0 && char.IsUpper(c) && i + 1 < name.Length && char.IsLower(name[i + 1]) && char.IsUpper(name[i - 1]));
                if (boundary && current.Length > 0)
                {
                    tokens.Add(current.ToString().ToLowerInvariant());
                    current.Clear();
                }
                if (char.IsLetter(c))
                    current.Append(c);
            }
            if (current.Length > 0)
                tokens.Add(current.ToString().ToLowerInvariant());
            return tokens;
        }

        /// <summary>
        /// Whole-word keyword match, so "projectile" is no tile and "street" no tree. A word
        /// matches a keyword it equals, its plural, a keyword of five or more letters it starts
        /// with ("metallic"), or a keyword followed by another material word ("metalfloor").
        /// Keywords with underscores match consecutive words.
        /// </summary>
        private static bool MatchesPbrKeyword(List<string> tokens, string[] keywords)
        {
            if (tokens.Count == 0)
                return false;
            foreach (var keyword in keywords)
            {
                if (keyword.IndexOf('_') >= 0)
                {
                    var parts = keyword.Split('_');
                    for (int i = 0; i + parts.Length <= tokens.Count; i++)
                    {
                        int k = 0;
                        while (k < parts.Length && tokens[i + k] == parts[k]) k++;
                        if (k == parts.Length)
                            return true;
                    }
                    continue;
                }
                foreach (var token in tokens)
                {
                    if (!token.StartsWith(keyword, StringComparison.Ordinal))
                        continue;
                    string rest = token.Substring(keyword.Length);
                    if (rest.Length == 0 || rest == "s" || rest == "es" || keyword.Length >= 5 || PbrCompoundTails.Contains(rest))
                        return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Unity's own metallic and smoothness maps, honoured per texel through OpenRemix's PBR
        /// remap: Standard and URP _MetallicGlossMap (red metallic, alpha smoothness x
        /// _GlossMapScale / URP _Smoothness), specular setup _SpecGlossMap (alpha smoothness),
        /// smoothness in the albedo's alpha (_SmoothnessTextureChannel 1), and HDRP _MaskMap (red
        /// metallic, alpha smoothness, within their remap ranges).
        /// </summary>
        private void DetectUnityPbrMaps(Material material, string shaderName, ref MaterialTextureData d)
        {
            d.pbrMapHandle = IntPtr.Zero;
            d.pbrMapMetallic = d.pbrMapSmoothness = d.smoothnessInAlbedoAlpha = false;
            d.pbrMetalBias = 0f; d.pbrMetalScale = 1f;
            d.pbrRoughBias = 0f; d.pbrRoughScale = 1f;
            try
            {
                float Get(string name, float fallback) => material.HasProperty(name) ? material.GetFloat(name) : fallback;
                Texture2D Tex(string name) => material.HasProperty(name) ? material.GetTexture(name) as Texture2D : null;
                float smoothMin = 0f, smoothMax;
                Texture2D map = null;

                var mask = Tex("_MaskMap");
                if (mask != null)
                {
                    map = mask;
                    d.pbrMapMetallic = d.pbrMapSmoothness = true;
                    float metalMin = Get("_MetallicRemapMin", 0f), metalMax = Get("_MetallicRemapMax", 1f);
                    d.pbrMetalBias = metalMin;
                    d.pbrMetalScale = metalMax - metalMin;
                    smoothMin = Get("_SmoothnessRemapMin", 0f);
                    smoothMax = Get("_SmoothnessRemapMax", 1f);
                }
                else
                {
                    bool urp = shaderName.IndexOf("Universal Render Pipeline", StringComparison.OrdinalIgnoreCase) >= 0;
                    smoothMax = urp && material.HasProperty("_Smoothness") ? material.GetFloat("_Smoothness") : Get("_GlossMapScale", 1f);
                    bool specularSetup = material.IsKeywordEnabled("_SPECULAR_SETUP") ||
                                         shaderName.IndexOf("Specular", StringComparison.OrdinalIgnoreCase) >= 0;
                    var metallicMap = specularSetup ? null : Tex("_MetallicGlossMap");
                    var specMap = specularSetup ? Tex("_SpecGlossMap") : null;
                    if (metallicMap != null)
                    {
                        map = metallicMap;
                        d.pbrMapMetallic = true;
                    }
                    else if (specMap != null)
                    {
                        map = specMap;
                    }
                    if (material.HasProperty("_SmoothnessTextureChannel") && material.GetFloat("_SmoothnessTextureChannel") > 0.5f)
                        d.smoothnessInAlbedoAlpha = true;
                    else if (map != null)
                        d.pbrMapSmoothness = true;
                }

                // roughness = 1 - smoothness = 1 - (min + (max - min) * a) = (1 - max) + (max - min) * (1 - a)
                d.pbrRoughBias = 1f - smoothMax;
                d.pbrRoughScale = smoothMax - smoothMin;
                if (map != null)
                    d.pbrMapHandle = UploadUnityTexture(map, linearData: true);
                if (d.pbrMapHandle == IntPtr.Zero)
                    d.pbrMapMetallic = d.pbrMapSmoothness = false;
            }
            catch (Exception ex)
            {
                logger.LogWarning($"[PbrMaps] '{material.name}': {ex.Message}");
                d.pbrMapHandle = IntPtr.Zero;
                d.pbrMapMetallic = d.pbrMapSmoothness = d.smoothnessInAlbedoAlpha = false;
            }
        }

        /// <summary>Albedo statistics from a 64x64 downsample (sRGB bytes / 255 unless noted).</summary>
        private struct AlbedoStats
        {
            public bool valid;
            public float luminance;   // mean, linear
            public float saturation;  // mean HSV saturation
            public float detail;      // mean |luminance - its 4 neighbours' mean|
            public float greenMean, greenStd, greenLow, greenHigh; // green channel: mean, deviation, 2nd/98th percentile
        }

        private AlbedoStats GetAlbedoStats(Texture2D tex)
        {
            int id = tex.GetInstanceID();
            if (albedoStatsCache.TryGetValue(id, out var cached))
                return cached;
            var stats = new AlbedoStats();
            try
            {
                const int size = 64;
                RenderTexture tmp = RenderTexture.GetTemporary(size, size, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                RenderTexture prev = RenderTexture.active;
                Graphics.Blit(tex, tmp);
                RenderTexture.active = tmp;
                var readable = new Texture2D(size, size, TextureFormat.RGBA32, false, false);
                readable.ReadPixels(new Rect(0, 0, size, size), 0, 0);
                readable.Apply();
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(tmp);
                Color32[] px = readable.GetPixels32();
                UnityEngine.Object.Destroy(readable);

                int n = px.Length;
                var lum = new float[n];
                var histogram = new int[256];
                double lumSum = 0, satSum = 0, gSum = 0, gSq = 0;
                for (int i = 0; i < n; i++)
                {
                    var c = px[i];
                    lum[i] = (c.r * 0.299f + c.g * 0.587f + c.b * 0.114f) / 255f;
                    lumSum += Mathf.Pow(lum[i], 2.2f);
                    int max = Math.Max(c.r, Math.Max(c.g, c.b)), min = Math.Min(c.r, Math.Min(c.g, c.b));
                    satSum += max > 0 ? (max - min) / (float)max : 0f;
                    gSum += c.g / 255.0;
                    gSq += (c.g / 255.0) * (c.g / 255.0);
                    histogram[c.g]++;
                }
                double detailSum = 0;
                for (int y = 0; y < size; y++)
                {
                    for (int x = 0; x < size; x++)
                    {
                        float around = (lum[y * size + (x + 1) % size] + lum[y * size + (x + size - 1) % size] +
                                        lum[((y + 1) % size) * size + x] + lum[((y + size - 1) % size) * size + x]) * 0.25f;
                        detailSum += Mathf.Abs(lum[y * size + x] - around);
                    }
                }
                float Percentile(float q)
                {
                    int target = (int)(q * n), count = 0;
                    for (int v = 0; v < 256; v++)
                    {
                        count += histogram[v];
                        if (count > target)
                            return v / 255f;
                    }
                    return 1f;
                }
                stats.valid = true;
                stats.luminance = (float)(lumSum / n);
                stats.saturation = (float)(satSum / n);
                stats.detail = (float)(detailSum / n);
                stats.greenMean = (float)(gSum / n);
                stats.greenStd = Mathf.Sqrt(Mathf.Max(0f, (float)(gSq / n) - stats.greenMean * stats.greenMean));
                stats.greenLow = Percentile(0.02f);
                stats.greenHigh = Percentile(0.98f);
            }
            catch (Exception ex)
            {
                logger.LogWarning($"[AutoPBR] Could not read '{tex.name}' for statistics: {ex.Message}");
            }
            albedoStatsCache[id] = stats;
            return stats;
        }

        /// <summary>
        /// Varies AutoPBR's keyword-table values by the albedo, so materials in one category (a
        /// level of materials named METAL) differ: saturated "metal" is painted (a dielectric),
        /// near-black metal is roughened, busy textures are rougher, and each texture gets its own
        /// stable offset from its name's hash (AutoPbrVariation scales it): roughness by up to
        /// +/-0.18, a metal's metalness down by up to 0.15. A level's textures of one kind
        /// (MetalDeco1, MetalDeco2, ...) otherwise read as one material. Inferred roughness also varies across the surface: the albedo's green
        /// channel serves as the roughness map, brighter texels smoother. AutoPbrRoughnessBias and
        /// AutoPbrMetallicBias then shift every inferred value.
        /// </summary>
        private void RefineInferredPbr(ref MaterialTextureData d, Texture2D tex)
        {
            d.pbrRefined = true;
            d.pbrTuning = AutoPbrTuning;
            d.hasRoughnessDetail = false;
            float metal = d.categoryMetallic, rough = d.categoryRoughness;
            var stats = tex != null ? GetAlbedoStats(tex) : default;
            if (!stats.valid)
            {
                ApplyInferredPbr(ref d, metal, rough);
                return;
            }

            if (metal > 0.5f)
            {
                float painted = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.25f, 0.55f, stats.saturation));
                metal = Mathf.Lerp(metal, 0.05f, painted);
                rough = Mathf.Lerp(rough, 0.45f, painted);
                float dark = 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.03f, 0.15f, stats.luminance));
                rough = Mathf.Lerp(rough, Mathf.Max(rough, 0.55f), dark);
            }
            rough += Mathf.Clamp((stats.detail - 0.05f) * 1.5f, -0.08f, 0.15f);
            // Two independent offsets from the name's hash, the second from its high bits mixed.
            uint hash = (uint)HashUtils.HashStringFNV(tex.name);
            float roughOffset = (hash & 0xFF) / 255f - 0.5f;
            float metalOffset = ((hash * 0x9E3779B1u) >> 24) / 255f;
            rough += roughOffset * 0.36f * d.pbrTuning.x;
            if (metal > 0.5f)
                metal -= metalOffset * 0.15f * d.pbrTuning.x;
            ApplyInferredPbr(ref d, metal, rough);
            if (!d.inferRoughness)
                return;
            rough = d.inferredRoughness;

            // value = bias + scale * (1 - green): the mean green gives rough, two deviations either
            // side give rough -/+ spread; the 2nd..98th percentile stays within 0.04..1.
            if (stats.greenStd < 0.02f)
                return;
            float spread = Mathf.Lerp(0.08f, 0.2f, rough);
            float scale = spread / (2f * stats.greenStd);
            if (stats.greenHigh > stats.greenMean)
                scale = Mathf.Min(scale, (rough - 0.04f) / (stats.greenHigh - stats.greenMean));
            if (stats.greenMean > stats.greenLow)
                scale = Mathf.Min(scale, (1f - rough) / (stats.greenMean - stats.greenLow));
            if (scale <= 0.01f)
                return;
            d.hasRoughnessDetail = true;
            d.roughnessDetailScale = scale;
            d.roughnessDetailBias = rough - scale * (1f - stats.greenMean);
        }

        // The inferred values with the biases (AutoPbrTuning y: roughness, z: metallic), for
        // whichever of them the material leaves to AutoPBR.
        private static void ApplyInferredPbr(ref MaterialTextureData d, float metal, float rough)
        {
            if (d.inferMetallic)
                d.inferredMetallic = Mathf.Clamp01(metal + d.pbrTuning.z);
            if (d.inferRoughness)
                d.inferredRoughness = Mathf.Clamp(rough + d.pbrTuning.y, 0.04f, 1f);
        }

        /// <summary>
        /// Generates a tangent-space normal map from an albedo texture via a Sobel height filter on luminance.
        /// Caches generated normal maps by texture instance ID to prevent redundant GPU/CPU work.
        /// </summary>
        private (IntPtr handle, ulong hash) GetOrCreateAutoNormalMap(Texture2D albedoTex, float bumpStrength)
        {
            if (albedoTex == null || createTextureFunc == null)
                return (IntPtr.Zero, 0);

            int texId = albedoTex.GetInstanceID();
            long cacheKey = ((long)texId << 32) | (uint)(Mathf.RoundToInt(bumpStrength * 100f) & 0xFFFFFFFF);
            lock (autoNormalCache)
            {
                if (autoNormalCache.TryGetValue(cacheKey, out var cached))
                    return cached;
            }

            try
            {
                Color32[] pixels;
                int w = albedoTex.width;
                int h = albedoTex.height;

                // Clamp processing resolution to at most 1024x1024 for instant processing (<1ms)
                int maxDim = 1024;
                if (w > maxDim || h > maxDim || !UnityCompat.IsReadable(albedoTex))
                {
                    int targetW = Mathf.Min(w, maxDim);
                    int targetH = Mathf.Min(h, maxDim);
                    RenderTexture tmp = RenderTexture.GetTemporary(targetW, targetH, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                    RenderTexture prev = RenderTexture.active;
                    Graphics.Blit(albedoTex, tmp);
                    RenderTexture.active = tmp;
                    Texture2D readable = new Texture2D(targetW, targetH, TextureFormat.RGBA32, false, false);
                    readable.ReadPixels(new Rect(0, 0, targetW, targetH), 0, 0);
                    readable.Apply();
                    RenderTexture.active = prev;
                    RenderTexture.ReleaseTemporary(tmp);
                    pixels = readable.GetPixels32();
                    w = readable.width;
                    h = readable.height;
                    UnityEngine.Object.Destroy(readable);
                }
                else
                {
                    pixels = albedoTex.GetPixels32();
                }

                int numPixels = w * h;
                if (pixels == null || pixels.Length < numPixels)
                    return (IntPtr.Zero, 0);

                // 1. Precalculate luminance for Sobel convolution
                byte[] lum = new byte[numPixels];
                for (int i = 0; i < numPixels; i++)
                {
                    lum[i] = (byte)((pixels[i].r * 77 + pixels[i].g * 150 + pixels[i].b * 29) >> 8);
                }

                // 2. Sobel 3x3 filter to calculate tangent-space normal vectors (X, Y, Z)
                byte[] normalPixelData = new byte[numPixels * 4];
                float scale = (Mathf.Clamp(bumpStrength, 0.1f, 5.0f) * 2.5f) / 1020f;

                for (int y = 0; y < h; y++)
                {
                    int yPrev = (y > 0 ? y - 1 : h - 1) * w;
                    int yCurr = y * w;
                    int yNext = (y < h - 1 ? y + 1 : 0) * w;

                    for (int x = 0; x < w; x++)
                    {
                        int xPrev = (x > 0 ? x - 1 : w - 1);
                        int xNext = (x < w - 1 ? x + 1 : 0);

                        int tl = lum[yPrev + xPrev];
                        int tc = lum[yPrev + x];
                        int tr = lum[yPrev + xNext];

                        int ml = lum[yCurr + xPrev];
                        int mr = lum[yCurr + xNext];

                        int bl = lum[yNext + xPrev];
                        int bc = lum[yNext + x];
                        int br = lum[yNext + xNext];

                        // Sobel gradients
                        int dX = (tr + (mr << 1) + br) - (tl + (ml << 1) + bl);
                        int dY = (bl + (bc << 1) + br) - (tl + (tc << 1) + tr);

                        // Tangent-space normal vector
                        float nx = -dX * scale;
                        float ny = -dY * scale;
                        float nz = 1.0f;

                        float invLen = 1.0f / Mathf.Sqrt(nx * nx + ny * ny + 1.0f);
                        nx *= invLen;
                        ny *= invLen;
                        nz *= invLen;

                        int idx = (yCurr + x) * 4;
                        normalPixelData[idx + 0] = (byte)((nx * 0.5f + 0.5f) * 255f); // R = Tangent X
                        normalPixelData[idx + 1] = (byte)((ny * 0.5f + 0.5f) * 255f); // G = Tangent Y
                        normalPixelData[idx + 2] = (byte)((nz * 0.5f + 0.5f) * 255f); // B = Tangent Z
                        normalPixelData[idx + 3] = 255;                                // A
                    }
                }

                // 3. Compute hash and queue for deferred upload on render thread
                ulong hash = XXHash64.ComputeHash(normalPixelData, 0, normalPixelData.Length);
                if (hash == 0) hash = 1;

                lock (pendingTextureLock)
                {
                    pendingTextureUploads.Enqueue(new PendingTextureUpload
                    {
                        texId = -1,
                        tintedCacheKey = 0,
                        pixelData = normalPixelData,
                        hash = hash,
                        width = (uint)w,
                        height = (uint)h,
                        mipLevels = 1,
                        format = RemixAPI.remixapi_Format.REMIXAPI_FORMAT_R8G8B8A8_UNORM
                    });
                }

                var result = (new IntPtr((long)hash), hash);
                lock (autoNormalCache)
                {
                    autoNormalCache[cacheKey] = result;
                }
                return result;
            }
            catch (Exception ex)
            {
                logger.LogError($"Failed to generate auto normal map for '{albedoTex.name}': {ex.Message}");
                return (IntPtr.Zero, 0);
            }
        }

        /// <summary>
        /// Updates all captured materials to apply or remove auto PBR heuristics and auto normal maps live.
        /// Resets remixMaterialHandle for altered materials so Remix creates new material instances.
        /// </summary>
        // The AutoPBR settings the materials were last updated for: a change to any of them (the
        // UI, the config file) is applied on the main thread's next frame (AutoPbrSettingsChanged).
        private (bool pbr, bool normals, float normalStrength, Vector3 tuning) appliedAutoPbr;
        private bool appliedAutoPbrKnown;

        private (bool, bool, float, Vector3) CurrentAutoPbrSettings() => (
            ConfigAutoPBR != null && ConfigAutoPBR.Value,
            ConfigAutoNormalMaps != null && ConfigAutoNormalMaps.Value,
            ConfigAutoNormalStrength != null ? ConfigAutoNormalStrength.Value : 1.0f,
            AutoPbrTuning);

        public bool AutoPbrSettingsChanged()
        {
            var current = CurrentAutoPbrSettings();
            if (!appliedAutoPbrKnown)
            {
                // The materials captured so far were made with these.
                appliedAutoPbr = current;
                appliedAutoPbrKnown = true;
                return false;
            }
            return !current.Equals(appliedAutoPbr);
        }

        public void UpdateAllAutoPbrMaterials()
        {
            appliedAutoPbr = CurrentAutoPbrSettings();
            appliedAutoPbrKnown = true;
            bool pbrEnabled = ConfigAutoPBR != null && ConfigAutoPBR.Value;
            bool normalEnabled = ConfigAutoNormalMaps != null && ConfigAutoNormalMaps.Value;
            float normalStrength = ConfigAutoNormalStrength != null ? ConfigAutoNormalStrength.Value : 1.0f;

            int newPbrCount = 0;
            int newNormalCount = 0;
            var changedIds = new List<int>();

            foreach (var kvp in materialTextureData.ToArray())
            {
                var matData = kvp.Value;
                bool changed = false;

                // 1. Update Metallic & Roughness
                float targetMetallic = matData.baseMetallic;
                float targetRoughness = matData.baseRoughness;

                if (pbrEnabled && matData.hasInferredPbr)
                {
                    if (!matData.pbrRefined || matData.pbrTuning != AutoPbrTuning)
                    {
                        RefineInferredPbr(ref matData, matData.albedoTex);
                        changed = true;
                    }
                    targetMetallic = matData.inferredMetallic;
                    targetRoughness = matData.inferredRoughness;
                    newPbrCount++;
                }

                // The roughness detail map follows the AutoPBR toggle (CreateRemixMaterialSimple).
                if (matData.hasRoughnessDetail)
                    changed = true;

                if (Mathf.Abs(matData.metallic - targetMetallic) > 0.001f ||
                    Mathf.Abs(matData.roughness - targetRoughness) > 0.001f)
                {
                    matData.metallic = targetMetallic;
                    matData.roughness = targetRoughness;
                    changed = true;
                }

                // 2. Update Normal Map
                IntPtr targetNormalHandle = matData.originalNormalHandle;
                ulong targetNormalHash = matData.originalNormalTextureHash;

                if (normalEnabled && matData.originalNormalHandle == IntPtr.Zero && matData.albedoTex != null && matData.autoNormalScale > 0f)
                {
                    var (genHandle, genHash) = GetOrCreateAutoNormalMap(matData.albedoTex, normalStrength * matData.autoNormalScale);
                    if (genHandle != IntPtr.Zero)
                    {
                        targetNormalHandle = genHandle;
                        targetNormalHash = genHash;
                        newNormalCount++;
                    }
                }

                if (matData.normalHandle != targetNormalHandle || matData.normalTextureHash != targetNormalHash)
                {
                    matData.normalHandle = targetNormalHandle;
                    matData.normalTextureHash = targetNormalHash;
                    changed = true;
                }

                if (changed)
                {
                    materialTextureData[kvp.Key] = matData;
                    changedIds.Add(kvp.Key);
                }
            }

            // Recreate the changed materials now, under their hashes: OpenRemix replaces each in
            // place and the meshes and retained instances using it pick it up, so no mesh is
            // rebuilt. Newly generated normal maps are uploaded first.
            ProcessPendingTextureUploads();
            foreach (int id in changedIds)
            {
                if (!materialTextureData.TryGetValue(id, out var matData))
                    continue;
                IntPtr handle = CreateRemixMaterialSimple(ref matData, id, replace: true);
                matData.remixMaterialHandle = handle;
                materialTextureData[id] = matData;
            }

            autoPbrMaterialCount = newPbrCount;
            autoNormalMapCount = newNormalCount;

            logger?.LogInfo($"[AutoPBR] Live update completed: PBR={pbrEnabled} ({autoPbrMaterialCount} active), AutoNormal={normalEnabled} ({autoNormalMapCount} active, strength={normalStrength:F2})");
        }

        #endregion
    }
}
