using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using BepInEx.Logging;
using UnityEngine;

namespace UnityRemix
{
    // Alpha and translucency detection: blend, cutout, water, blood, glass and their colours.
    public partial class RemixMaterialManager
    {
        /// <summary>
        /// Sample an uncompressed RGBA pixel buffer to check if the alpha channel has non-trivial content.
        /// Checks a sparse grid of pixels for performance (avoids scanning entire textures).
        /// </summary>
        private static bool SampleAlphaRGBA(byte[] pixelData, int alphaOffset, int stride)
        {
            int pixelCount = pixelData.Length / stride;
            if (pixelCount == 0) return false;
            
            // Sample up to 256 evenly-spaced pixels
            int step = Math.Max(1, pixelCount / 256);
            for (int i = 0; i < pixelCount; i += step)
            {
                int idx = i * stride + alphaOffset;
                if (idx < pixelData.Length && pixelData[idx] < 250)
                    return true;
            }
            return false;
        }
        
        /// <summary>
        /// Check if uncompressed RGBA pixels have genuine cutout transparency: a significant
        /// fraction of pixels with near-zero alpha (< 16). This distinguishes real transparency
        /// (decal backgrounds, cutout shapes) from smoothness-as-alpha packing where values
        /// are typically 50-230.
        /// </summary>
        private static bool SampleCutoutAlphaRGBA(byte[] pixelData, int alphaOffset, int stride)
        {
            int pixelCount = pixelData.Length / stride;
            if (pixelCount == 0) return false;
            
            int sampleCount = Math.Min(pixelCount, 1024);
            int step = Math.Max(1, pixelCount / sampleCount);
            int nearZeroCount = 0;
            int totalSampled = 0;
            
            for (int i = 0; i < pixelCount; i += step)
            {
                int idx = i * stride + alphaOffset;
                if (idx >= pixelData.Length) break;
                totalSampled++;
                if (pixelData[idx] < 16)
                    nearZeroCount++;
            }
            
            // >= 10% of sampled pixels are near-transparent → genuine cutout
            return totalSampled > 0 && nearZeroCount * 10 >= totalSampled;
        }
        
        /// <summary>
        /// Check if a BC3/DXT5 texture has any non-trivial alpha by scanning alpha endpoint
        /// bytes in each 16-byte block. Returns true if any block has an endpoint below 250.
        /// </summary>
        private static bool SampleAlphaBC3(byte[] rawData)
        {
            int blockCount = rawData.Length / 16;
            int step = Math.Max(1, blockCount / 256);
            for (int b = 0; b < blockCount; b += step)
            {
                int offset = b * 16;
                byte a0 = rawData[offset];
                byte a1 = rawData[offset + 1];
                if (a0 < 250 || a1 < 250)
                    return true;
            }
            return false;
        }
        
        /// <summary>
        /// Check if a BC3/DXT5 texture has genuine cutout transparency by scanning alpha
        /// endpoints. Returns true if a significant fraction of blocks have near-zero alpha
        /// endpoints (< 16), indicating real transparency rather than smoothness packing.
        /// </summary>
        private static bool SampleCutoutAlphaBC3(byte[] rawData)
        {
            int blockCount = rawData.Length / 16;
            if (blockCount == 0) return false;
            
            int sampleCount = Math.Min(blockCount, 1024);
            int step = Math.Max(1, blockCount / sampleCount);
            int nearZeroCount = 0;
            int totalSampled = 0;
            
            for (int b = 0; b < blockCount; b += step)
            {
                int offset = b * 16;
                byte a0 = rawData[offset];
                byte a1 = rawData[offset + 1];
                totalSampled++;
                if (a0 < 16 || a1 < 16)
                    nearZeroCount++;
            }
            
            // >= 10% of sampled blocks have near-zero alpha → genuine cutout
            return totalSampled > 0 && nearZeroCount * 10 >= totalSampled;
        }
        
        /// <summary>
        /// Detect alpha mode from Unity material properties, shader keywords, and render queue.
        /// Covers Standard shader (_Mode), URP/HDRP (_Surface), and keyword-based detection.
        /// Returns (mode, reason) for diagnostic logging.
        /// </summary>
        private static (AlphaMode mode, string reason) DetectAlphaModeWithReason(Material material)
        {
            // 1. Shader keywords (most reliable, works across Standard/URP/HDRP/custom)
            if (material.IsKeywordEnabled("_ALPHATEST_ON"))
                return (AlphaMode.Cutout, "keyword:_ALPHATEST_ON");
            if (material.IsKeywordEnabled("_ALPHABLEND_ON"))
                return (AlphaMode.Blend, "keyword:_ALPHABLEND_ON");
            if (material.IsKeywordEnabled("_ALPHAPREMULTIPLY_ON"))
                return (AlphaMode.Blend, "keyword:_ALPHAPREMULTIPLY_ON");
            
            // 2. Standard shader _Mode property (0=Opaque, 1=Cutout, 2=Fade, 3=Transparent)
            if (material.HasProperty("_Mode"))
            {
                int mode = (int)material.GetFloat("_Mode");
                if (mode == 1) return (AlphaMode.Cutout, $"_Mode={mode}");
                if (mode >= 2) return (AlphaMode.Blend, $"_Mode={mode}");
            }
            
            // 3. URP/HDRP _Surface property (0=Opaque, 1=Transparent)
            if (material.HasProperty("_Surface"))
            {
                int surface = (int)material.GetFloat("_Surface");
                if (surface == 1)
                {
                    if (material.HasProperty("_AlphaClip") && material.GetFloat("_AlphaClip") > 0.5f)
                        return (AlphaMode.Cutout, $"_Surface=1,_AlphaClip=1");
                    return (AlphaMode.Blend, $"_Surface=1");
                }
            }
            
            // 4. Shader name heuristics (particles, transparents, additive, cutout)
            if (material.shader != null)
            {
                string sName = material.shader.name;
                if (!string.IsNullOrEmpty(sName))
                {
                    if (sName.IndexOf("Cutout", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        sName.IndexOf("AlphaTest", StringComparison.OrdinalIgnoreCase) >= 0)
                        return (AlphaMode.Cutout, $"shader:{sName}");

                    if (sName.IndexOf("Particle", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        sName.IndexOf("Additive", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        sName.IndexOf("Transparent", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        sName.IndexOf("Fade", StringComparison.OrdinalIgnoreCase) >= 0)
                        return (AlphaMode.Blend, $"shader:{sName}");
                }
            }

            // 5. Material name heuristics
            string mName = material.name;
            if (!string.IsNullOrEmpty(mName))
            {
                if (mName.IndexOf("Cutout", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    mName.IndexOf("AlphaTest", StringComparison.OrdinalIgnoreCase) >= 0)
                    return (AlphaMode.Cutout, $"matName:{mName}");

                if (mName.IndexOf("Particle", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    mName.IndexOf("Additive", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    mName.IndexOf("Trail", StringComparison.OrdinalIgnoreCase) >= 0)
                    return (AlphaMode.Blend, $"matName:{mName}");
            }

            // 6. Tint color alpha heuristics
            if (material.HasProperty("_TintColor"))
            {
                Color tc = material.GetColor("_TintColor");
                if (tc.a < 0.99f) return (AlphaMode.Blend, $"_TintColor.a={tc.a:F2}");
            }
            if (material.HasProperty("_Color"))
            {
                Color c = material.GetColor("_Color");
                if (c.a < 0.99f) return (AlphaMode.Blend, $"_Color.a={c.a:F2}");
            }
            
            // 7. Render queue heuristic (check both material and shader queue)
            int queue = material.renderQueue;
            if (queue < 0 && material.shader != null)
                queue = material.shader.renderQueue;

            if (queue >= 2450 && queue < 3000) return (AlphaMode.Cutout, $"renderQueue={queue}");
            if (queue >= 3000) return (AlphaMode.Blend, $"renderQueue={queue}");
            
            return (AlphaMode.Opaque, "default");
        }

        /// <summary>
        /// Detect if a material represents a physical translucent material (Water body or Glass thin wall) for OpenRemix.
        /// </summary>
        private (TranslucentKind kind, float ior, float distance, Color color, string reason) DetectTranslucentKind(
            Material material, string matName, string shaderName, Renderer renderer, AlphaMode alphaMode, Color albedoColor, float roughness, bool isParticleOrTrail = false)
        {
            if (waterManager != null && !waterManager.ConfigEnableTranslucents.Value)
                return (TranslucentKind.None, 1.0f, 0f, Color.white, "disabled");

            if (isParticleOrTrail || renderer is ParticleSystemRenderer || renderer is TrailRenderer || renderer is LineRenderer)
                return (TranslucentKind.None, 1.0f, 0f, Color.white, "particle");

            int layer = renderer != null ? renderer.gameObject.layer : -1;
            string layerName = layer >= 0 ? LayerMask.LayerToName(layer) : null;
            string goName = renderer != null ? renderer.gameObject.name : null;
            string mName = matName ?? "";
            string sName = shaderName ?? "";

            // Non-rendered or invisible materials (e.g. InvisibleFace triggers, blockers) and particle/decal/splash effects must never be converted to translucent water or glass
            if (SceneMeshScanner.IsMaterialOrShaderInvisible(material) ||
                MatchesAnyKeyword(mName, "invisible", "nodraw", "trigger", "collisiononly", "zerostencilbuffer", "portalocclusion",
                    "particle", "trail", "drop", "drip", "splash", "spurt", "stain", "splat", "spark", "mist", "bubble", "pool", "fallback", "giblet", "chunk", "warning", "shockwave", "flash", "beam") ||
                MatchesAnyKeyword(sName, "invisible", "nodraw", "clear", "particle") ||
                (goName != null && MatchesAnyKeyword(goName, "particle", "trail", "drop", "drip", "splash", "spurt", "stain", "splat", "spark", "giblet", "chunk", "warning")))
            {
                return (TranslucentKind.None, 1.0f, 0f, Color.white, "excluded");
            }

            bool enableWater = waterManager == null || waterManager.ConfigEnableWater.Value;
            bool enableGlass = waterManager == null || waterManager.ConfigEnableGlass.Value;

            // 1. Water & Fluid Body Detection (Translucent Body with refraction, TIR, Snell's window, and volumetric depth fog)
            if (enableWater)
            {
                bool isLiquid = false;
                string reason = null;

                // Built-in Unity Water layer (Layer 4)
                if (layer == 4 || (layerName != null && layerName.IndexOf("water", StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    isLiquid = true;
                    reason = $"layer:{layerName ?? layer.ToString()}";
                }
                // Shader name heuristics
                else if (MatchesAnyKeyword(sName, "water", "ocean", "river", "lake", "pond", "waterfall", "fluid", "aquatic", "puddle", "stream",
                         "blood", "mulch", "gore", "slime", "toxic", "acid", "sewer", "poison"))
                {
                    isLiquid = true;
                    reason = $"shader:{sName}";
                }
                // Material name heuristics
                else if (MatchesAnyKeyword(mName, "water", "ocean", "river", "lake", "pond", "waterfall", "liquid", "aquatic", "puddle", "stream",
                         "blood", "mulch", "gore", "flesh", "slime", "toxic", "acid", "sewer", "poison") &&
                         !MatchesAnyKeyword(mName, "watermark", "resistwater", "resistWater", "waterproof", "decal", "splatter", "spray", "particle", "drop", "splash", "stain", "pool", "fallback", "trail"))
                {
                    isLiquid = true;
                    reason = $"matName:{mName}";
                }
                // GameObject name heuristics
                else if (goName != null && MatchesAnyKeyword(goName, "waterfall", "water_plane", "waterplane", "ocean", "river", "pond", "lake",
                         "blood", "mulch", "gore", "slime", "toxic", "acid") &&
                         !MatchesAnyKeyword(goName, "watermark", "resistwater", "resistWater", "underwateroverlay", "decal", "splatter", "spray", "particle", "drop", "splash", "stain", "pool", "fallback", "trail"))
                {
                    isLiquid = true;
                    reason = $"goName:{goName}";
                }
                // Shader properties
                else if (material != null && (material.HasProperty("_WaterColor") || material.HasProperty("_DepthColor") || material.HasProperty("_WaveSpeed") || material.HasProperty("_WaveScale") || material.HasProperty("_BloodColor")))
                {
                    isLiquid = true;
                    reason = "liquidProperties";
                }

                if (isLiquid)
                {
                    // Classify specific liquid subtype: Blood/Mulch, Acid/Slime, or Clean Water
                    bool isBlood = MatchesAnyKeyword(mName, "blood", "mulch", "gore", "flesh", "bleed") ||
                                   MatchesAnyKeyword(sName, "blood", "mulch", "gore", "flesh") ||
                                   (goName != null && MatchesAnyKeyword(goName, "blood", "mulch", "gore", "flesh"));

                    bool isAcid = !isBlood && (MatchesAnyKeyword(mName, "acid", "slime", "toxic", "poison", "ooze") ||
                                   MatchesAnyKeyword(sName, "acid", "slime", "toxic", "poison", "ooze") ||
                                   (goName != null && MatchesAnyKeyword(goName, "acid", "slime", "toxic", "poison", "ooze")));

                    float ior;
                    float dist;
                    Color liquidCol;

                    if (isBlood)
                    {
                        ior = waterManager != null ? waterManager.ConfigBloodIOR.Value : 1.34f;
                        dist = waterManager != null ? waterManager.ConfigBloodClarity.Value : 2.0f;
                        liquidCol = ExtractBloodColor(material, albedoColor);
                        reason += ":blood";
                    }
                    else if (isAcid)
                    {
                        ior = 1.35f;
                        dist = 2.5f;
                        liquidCol = ExtractToxicColor(material, albedoColor);
                        reason += ":acid";
                    }
                    else
                    {
                        ior = waterManager != null ? waterManager.ConfigWaterIOR.Value : 1.333f;
                        dist = waterManager != null ? waterManager.ConfigWaterClarity.Value : 15.0f;
                        liquidCol = ExtractWaterColor(material, albedoColor);
                    }

                    if (renderer != null && waterManager != null)
                    {
                        waterManager.RegisterWaterSurface(renderer, liquidCol, dist);
                    }
                    if (waterManager != null)
                    {
                        waterManager.WaterMaterialCount++;
                    }

                    return (TranslucentKind.Water, ior, dist, liquidCol, reason);
                }
            }

            // 2. Glass Detection (Thin Wall specular transmission sheet with Fresnel reflection)
            if (enableGlass)
            {
                bool isGlass = false;
                string reason = null;

                // Layer heuristics (e.g. ULTRAKILL Layer 17 'BrokenGlass')
                if (layerName != null && layerName.IndexOf("glass", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    isGlass = true;
                    reason = $"layer:{layerName}";
                }
                // Shader name heuristics
                else if (MatchesAnyKeyword(sName, "glass", "window", "windshield", "visor", "lens", "refractive", "stainedglass", "crystal", "ice"))
                {
                    isGlass = true;
                    reason = $"shader:{sName}";
                }
                // Material name heuristics
                else if (MatchesAnyKeyword(mName, "glass", "window", "windshield", "visor", "lens", "bottle", "jar", "stainedglass", "crystal", "ice") &&
                         !MatchesAnyKeyword(mName, "hourglass", "sunglasses"))
                {
                    isGlass = true;
                    reason = $"matName:{mName}";
                }
                // GameObject name heuristics (e.g. ULTRAKILL 'SandCan_Glass_Low')
                else if (goName != null && MatchesAnyKeyword(goName, "glass", "window", "windshield", "visor", "lens", "bottle", "jar") &&
                         !MatchesAnyKeyword(goName, "hourglass"))
                {
                    isGlass = true;
                    reason = $"goName:{goName}";
                }
                // PBR Glass heuristic: transparent queue + low alpha + smooth (not particle/smoke/blood/fire)
                else if (material != null)
                {
                    int queue = material.renderQueue;
                    if (queue >= 2900 && alphaMode == AlphaMode.Blend)
                    {
                        if (roughness <= 0.25f && albedoColor.a <= 0.75f)
                        {
                            if (!MatchesAnyKeyword(mName, "particle", "smoke", "fire", "blood", "mulch", "gore", "flesh", "decal", "glow", "halo", "trail", "spark", "explosion") &&
                                !MatchesAnyKeyword(sName, "particle", "smoke", "fire", "blood", "mulch", "gore", "flesh", "decal", "glow", "halo", "trail", "spark", "explosion"))
                            {
                                isGlass = true;
                                reason = $"pbrGlass(queue={queue},rough={roughness:F2},alpha={albedoColor.a:F2})";
                            }
                        }
                    }
                }

                if (isGlass)
                {
                    float ior = waterManager != null ? waterManager.ConfigGlassIOR.Value : 1.52f;
                    Color glassCol = ExtractGlassColor(material, albedoColor);

                    if (waterManager != null)
                    {
                        waterManager.GlassMaterialCount++;
                    }

                    return (TranslucentKind.Glass, ior, 0.0f, glassCol, reason);
                }
            }

            return (TranslucentKind.None, 1.0f, 0f, Color.white, "none");
        }

        private static bool MatchesAnyKeyword(string text, params string[] keywords)
        {
            if (string.IsNullOrEmpty(text)) return false;
            for (int i = 0; i < keywords.Length; i++)
            {
                if (text.IndexOf(keywords[i], StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            return false;
        }

        private static Color ExtractWaterColor(Material mat, Color fallbackColor)
        {
            if (mat != null)
            {
                string[] waterColorProps = { "_WaterColor", "_DepthColor", "_ShallowColor", "_UnderwaterColor", "_Color", "_BaseColor" };
                for (int i = 0; i < waterColorProps.Length; i++)
                {
                    if (mat.HasProperty(waterColorProps[i]))
                    {
                        Color c = mat.GetColor(waterColorProps[i]);
                        if (c.maxColorComponent > 0.05f && (c.r < 0.95f || c.g < 0.95f || c.b < 0.95f))
                        {
                            return new Color(c.r, c.g, c.b, 1.0f);
                        }
                    }
                }

                if (mat.HasProperty("_MainTex") && mat.mainTexture is Texture2D tex && tex.isReadable)
                {
                    try
                    {
                        Color px = tex.GetPixel(tex.width / 2, tex.height / 2);
                        if (px.maxColorComponent > 0.05f && (px.r < 0.95f || px.g < 0.95f || px.b < 0.95f))
                        {
                            return new Color(px.r, px.g, px.b, 1.0f);
                        }
                    }
                    catch { }
                }
            }
            if (fallbackColor.maxColorComponent > 0.05f && (fallbackColor.r < 0.95f || fallbackColor.g < 0.95f || fallbackColor.b < 0.95f))
            {
                return new Color(fallbackColor.r, fallbackColor.g, fallbackColor.b, 1.0f);
            }
            // Natural clean water cyan/blue
            return new Color(0.20f, 0.70f, 0.90f, 1.0f);
        }

        private static Color ExtractBloodColor(Material mat, Color fallbackColor)
        {
            Color candidate = Color.clear;
            if (mat != null)
            {
                string[] bloodProps = { "_BloodColor", "_Color", "_BaseColor", "_TintColor", "_WaterColor", "_DepthColor" };
                for (int i = 0; i < bloodProps.Length; i++)
                {
                    if (mat.HasProperty(bloodProps[i]))
                    {
                        Color c = mat.GetColor(bloodProps[i]);
                        if (c.r > 0.30f && c.r > (c.g + c.b) * 0.65f)
                        {
                            candidate = c;
                            break;
                        }
                    }
                }

                if (candidate == Color.clear && mat.HasProperty("_MainTex") && mat.mainTexture is Texture2D tex && tex.isReadable)
                {
                    try
                    {
                        Color px = tex.GetPixel(tex.width / 2, tex.height / 2);
                        if (px.r > 0.25f && px.r > (px.g + px.b) * 0.6f)
                        {
                            candidate = px;
                        }
                    }
                    catch { }
                }
            }

            if (candidate == Color.clear && fallbackColor.r > 0.30f && fallbackColor.r > (fallbackColor.g + fallbackColor.b) * 0.65f)
            {
                candidate = fallbackColor;
            }

            if (candidate != Color.clear)
            {
                // Enforce physical blood absorption: red transmits, green and blue are heavily absorbed
                float r = Mathf.Clamp(candidate.r, 0.65f, 0.95f);
                float g = Mathf.Clamp(candidate.g * 0.1f, 0.01f, 0.05f);
                float b = Mathf.Clamp(candidate.b * 0.1f, 0.01f, 0.05f);
                return new Color(r, g, b, 1.0f);
            }

            // Default deep visceral blood red transmittance (Beer-Lambert: green and blue absorbed within ~2m)
            return new Color(0.85f, 0.02f, 0.02f, 1.0f);
        }

        private static Color ExtractToxicColor(Material mat, Color fallbackColor)
        {
            if (mat != null)
            {
                string[] toxicProps = { "_AcidColor", "_SlimeColor", "_ToxicColor", "_Color", "_BaseColor" };
                for (int i = 0; i < toxicProps.Length; i++)
                {
                    if (mat.HasProperty(toxicProps[i]))
                    {
                        Color c = mat.GetColor(toxicProps[i]);
                        if (c.g > 0.30f && c.g > (c.r + c.b) * 0.65f)
                        {
                            return new Color(c.r * 0.15f, Mathf.Clamp(c.g, 0.7f, 0.95f), c.b * 0.15f, 1.0f);
                        }
                    }
                }
            }
            return new Color(0.15f, 0.85f, 0.15f, 1.0f);
        }

        private static Color ExtractGlassColor(Material mat, Color fallbackColor)
        {
            if (fallbackColor.a < 0.95f && (fallbackColor.r < 0.95f || fallbackColor.g < 0.95f || fallbackColor.b < 0.95f))
            {
                return new Color(fallbackColor.r, fallbackColor.g, fallbackColor.b, 1.0f);
            }
            if (mat != null)
            {
                if (mat.HasProperty("_Color"))
                {
                    Color c = mat.GetColor("_Color");
                    if (c.r < 0.98f || c.g < 0.98f || c.b < 0.98f)
                        return new Color(c.r, c.g, c.b, 1.0f);
                }
                if (mat.HasProperty("_TintColor"))
                {
                    Color c = mat.GetColor("_TintColor");
                    if (c.r < 0.98f || c.g < 0.98f || c.b < 0.98f)
                        return new Color(c.r, c.g, c.b, 1.0f);
                }
            }
            return Color.white;
        }
    }
}
