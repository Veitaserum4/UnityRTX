using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using BepInEx.Logging;
using UnityEngine;

namespace UnityRemix
{
    // Capturing a Unity material: albedo colour and texture, emission, alpha, translucency, the game's overrides.
    public partial class RemixMaterialManager
    {
        /// <summary>
        /// Check if a color has perceptible chromatic saturation (distinguishing tinted colors from neutral white/grey/black).
        /// </summary>
        private static bool IsSaturatedColor(Color c)
        {
            float max = Mathf.Max(c.r, Mathf.Max(c.g, c.b));
            if (max < 0.01f) return false;
            float min = Mathf.Min(c.r, Mathf.Min(c.g, c.b));
            return ((max - min) / max) > 0.08f;
        }

        /// <summary>
        /// Intelligently resolve the intended albedo/tint color of a material.
        /// Prioritizes chromatic (saturated) colors over default neutral white/grey fallbacks,
        /// and aligns candidate property order with the matched texture channel (_Color1 for _Texture1, _Tint for base textures, etc.).
        /// </summary>
        private static Color ResolveMaterialAlbedoColor(Material material, string matchedTexProp)
        {
            string[] candidates;
            if (matchedTexProp == "_Texture1" || matchedTexProp == "_TextureSample1")
                candidates = new string[] { "_Color1", "_BaseColor", "_Tint", "_Color", "_TintColor", "_ColorTint" };
            else if (matchedTexProp == "_Texture2" || matchedTexProp == "_TextureSample2")
                candidates = new string[] { "_Color2", "_Color1", "_BaseColor", "_Tint", "_Color", "_ColorTint" };
            else if (matchedTexProp == "_Texture3" || matchedTexProp == "_TextureSample3")
                candidates = new string[] { "_Color3", "_Color1", "_BaseColor", "_Tint", "_Color", "_ColorTint" };
            else
                candidates = new string[] { "_Tint", "_BaseColor", "_Color", "_Color1", "_TintColor", "_ColorTint", "_ColorTint2", "_ColorTint3" };

            // Pass 1: find first saturated / non-neutral color
            for (int i = 0; i < candidates.Length; i++)
            {
                string p = candidates[i];
                if (material.HasProperty(p))
                {
                    Color c = material.GetColor(p);
                    if (IsSaturatedColor(c))
                        return c;
                }
            }


            // Pass 3: fallback to first non-black candidate property
            for (int i = 0; i < candidates.Length; i++)
            {
                string p = candidates[i];
                if (material.HasProperty(p))
                {
                    Color c = material.GetColor(p);
                    if (c.r > 0.01f || c.g > 0.01f || c.b > 0.01f)
                        return c;
                }
            }

            return Color.white;
        }

        /// <summary>
        /// Apply Hue, Saturation, Brightness, Contrast (HSBC) adjustments to a color.
        /// Standard HSBC color transformation using Rodrigues rotation formula for hue.
        /// </summary>
        private static Color ApplyHsbcToColor(Color col, Vector4 hsbc)
        {
            float r = col.r;
            float g = col.g;
            float b = col.b;

            float h = hsbc.x;
            float br = hsbc.y;
            float c = hsbc.z;
            float s = hsbc.w;

            if (Mathf.Abs(h) > 0.001f)
            {
                float angleDeg = Mathf.Abs(h) <= 1.0f ? h * 360f : h;
                float angleRad = angleDeg * Mathf.Deg2Rad;
                float cosA = Mathf.Cos(angleRad);
                float sinA = Mathf.Sin(angleRad);
                const float k = 0.577350269f;
                float dotKV = (r + g + b) * k;
                float omc = 1.0f - cosA;
                float nr = r * cosA + k * (b - g) * sinA + k * dotKV * omc;
                float ng = g * cosA + k * (r - b) * sinA + k * dotKV * omc;
                float nb = b * cosA + k * (g - r) * sinA + k * dotKV * omc;
                r = nr;
                g = ng;
                b = nb;
            }

            if (Mathf.Abs(c - 1f) > 0.001f)
            {
                r = (r - 0.5f) * c + 0.5f;
                g = (g - 0.5f) * c + 0.5f;
                b = (b - 0.5f) * c + 0.5f;
            }

            if (Mathf.Abs(br) > 0.001f)
            {
                r += br;
                g += br;
                b += br;
            }

            if (Mathf.Abs(s - 1f) > 0.001f)
            {
                float lum = 0.299f * r + 0.587f * g + 0.114f * b;
                r = lum + (r - lum) * s;
                g = lum + (g - lum) * s;
                b = lum + (b - lum) * s;
            }

            return new Color(Mathf.Clamp01(r), Mathf.Clamp01(g), Mathf.Clamp01(b), col.a);
        }

        /// <summary>
        /// Capture textures from a Unity material, with optional per-renderer property overrides
        /// </summary>
        // Materials registered as ray portals by id (RayPortals' own), else by game profile.
        private readonly Dictionary<int, int> rayPortalIndices = new Dictionary<int, int>();

        public void SetRayPortalIndex(int materialId, int index) => rayPortalIndices[materialId] = index;

        // Unity's unlit alpha-blended shaders, which show their texture times their tint as is.
        private static bool IsUnlitSheetShader(string shaderName) =>
            shaderName == "Sprites/Default" || shaderName.EndsWith("/Sprites/Default", StringComparison.Ordinal) ||
            shaderName == "UI/Default" || shaderName.StartsWith("Unlit/Transparent", StringComparison.Ordinal);

        public void CaptureMaterialTextures(Material material, int materialId, Color? mpbEmissiveColor = null, float? mpbEmissiveIntensity = null, Texture2D mpbMainTex = null, Color? mpbColor = null, Renderer renderer = null, bool suppressLightGeneration = false, ColorMask? colorMask = null)
        {
            if (material == null)
                return;

            // Check debug toggle
            if (!captureMaterials.Value)
                return;
            
            // Check if material already exists and has been created or is pending
            if (materialTextureData.ContainsKey(materialId))
            {
                // Material data already captured, don't overwrite it
                return;
            }

            string shaderName = material.shader != null ? material.shader.name : "null";
            bool isParticleOrTrailRenderer = renderer is ParticleSystemRenderer || renderer is TrailRenderer || renderer is LineRenderer;
            // An additive glow on a mesh (an explosion's sphere, a light shaft): drawn as additive
            // particles are (UploadParticleTexture: its brightness as coverage), emitting.
            Color glow = Color.black;
            bool additiveGlow = !isParticleOrTrailRenderer && TryGetAdditiveGlow(material, out glow);
            float glowMax = Mathf.Max(glow.r, Mathf.Max(glow.g, glow.b));
            // (One that adds nothing covers nothing: the texture's smallest alpha scale.)
            float glowCoverage = !additiveGlow ? 0f : glowMax <= 0.001f ? 1f / 255f : Mathf.Clamp(glowMax, 0.02f, 1f);
            bool autoSuppressLight = additiveGlow || suppressLightGeneration || isParticleOrTrailRenderer ||
                shaderName.IndexOf("Particle", StringComparison.OrdinalIgnoreCase) >= 0 ||
                (shaderName.IndexOf("TextMeshPro", StringComparison.OrdinalIgnoreCase) >= 0 && shaderName.IndexOf("Distance Field", StringComparison.OrdinalIgnoreCase) >= 0) ||
                (material.name != null && (material.name.IndexOf("Particle", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                           material.name.IndexOf("Trail", StringComparison.OrdinalIgnoreCase) >= 0));

            var matData = new MaterialTextureData
            {
                materialName = material.name,
                albedoColor = mpbColor ?? Color.white,
                albedoHandle = IntPtr.Zero,
                normalHandle = IntPtr.Zero,
                albedoTextureHash = 0,
                normalTextureHash = 0,
                remixMaterialHandle = IntPtr.Zero,
                alphaMode = AlphaMode.Opaque,
                alphaCutoff = 0.5f,
                wrapModeU = 1, // MDL Repeat
                wrapModeV = 1,
                filterMode = 1, // MDL Linear
                mainTexST = new Vector4(1, 1, 0, 0),
                emissiveHandle = IntPtr.Zero,
                emissiveTextureHash = 0,
                emissiveColor = Color.black,
                emissiveIntensity = 0f,
                suppressLightGeneration = autoSuppressLight,
                glowColor = additiveGlow ? glow * (1f / glowCoverage) : Color.black,
                glowCoverage = glowCoverage,
                glowStrength = additiveGlow ? Mathf.Max(glow.r, Mathf.Max(glow.g, glow.b)) : 0f,
                rayPortalIndex = rayPortalIndices.TryGetValue(materialId, out int portalIndex) ? portalIndex
                               : Games.GameProfiles.OpensViewPortal(material) ? RayPortals.EntranceIndex : -1,
                rayPortalStandalone = rayPortalIndices.ContainsKey(materialId),
                viewChromaticAberration = Games.GameProfiles.ViewChromaticAberration(material),
                metallic = 0.0f,
                roughness = 0.7f,
                translucentKind = TranslucentKind.None,
                refractiveIndex = 1.0f,
                transmittanceDistance = 0.0f,
                transmittanceColor = Color.white
            };
            
            // Match albedo texture first so color resolution can be aligned to the matched texture channel
            Texture2D albedoTex = mpbMainTex;
            Texture2D tex = mpbMainTex;
            string matchedTexProp = null;

            if (captureTextures.Value)
            {
                if (tex != null)
                    matchedTexProp = "_MainTex";
                else
                    tex = FindAlbedoTexture(material, out matchedTexProp);
            }

            // Layered stylized materials: their textures are masks; the composited layers are the albedo.
            vertexColorWeights[materialId] = LayeredMaterialCompositor.VertexColorWeight(material);
            bool layeredComposite = false;
            if (captureTextures.Value && mpbMainTex == null && !(renderer is ParticleSystemRenderer) &&
                LayeredMaterialCompositor.IsLayered(material))
            {
                var composite = GetLayeredComposite(material);
                if (composite != null)
                {
                    tex = composite;
                    matchedTexProp = null;
                    layeredComposite = true;
                }
            }

            // Check for HSV / Brightness / Contrast adjustments (e.g. Unlit/ColorAdjust, stylized character customization)
            Vector4? hsbc = null;
            try
            {
                float hue = 0f;
                float brightness = 0f;
                float contrast = 1f;
                float saturation = 1f;
                bool hasHsbcProp = false;

                if (material.HasProperty("_Hue")) { hue = material.GetFloat("_Hue"); hasHsbcProp = true; }
                else if (material.HasProperty("_HueShift")) { hue = material.GetFloat("_HueShift"); hasHsbcProp = true; }
                else if (material.HasProperty("_HueAdjust")) { hue = material.GetFloat("_HueAdjust"); hasHsbcProp = true; }

                if (material.HasProperty("_Brightness")) { brightness = material.GetFloat("_Brightness"); hasHsbcProp = true; }
                if (material.HasProperty("_Contrast")) { contrast = material.GetFloat("_Contrast"); hasHsbcProp = true; }
                if (material.HasProperty("_Saturation")) { saturation = material.GetFloat("_Saturation"); hasHsbcProp = true; }

                if (material.HasProperty("_HSBC"))
                {
                    Vector4 v = material.GetVector("_HSBC");
                    hue = v.x;
                    saturation = v.y;
                    brightness = v.z;
                    contrast = v.w;
                    hasHsbcProp = true;
                }

                if (hasHsbcProp && (Mathf.Abs(hue) > 0.001f || Mathf.Abs(brightness) > 0.001f || Mathf.Abs(contrast - 1f) > 0.001f || Mathf.Abs(saturation - 1f) > 0.001f))
                {
                    hsbc = new Vector4(hue, brightness, contrast, saturation);
                }
            }
            catch { }

            // Get albedo color if not overridden by MPB (smart priority aligned with matched texture)
            if (!mpbColor.HasValue)
            {
                matData.albedoColor = layeredComposite ? Color.white : ResolveMaterialAlbedoColor(material, matchedTexProp);
            }

            // Extract PBR metallic: prioritize _BaseMetallic if > 0 (designer value in stylized shaders like PEAK)
            float metallic = 0.0f;
            bool hasExplicitMetallic = false;
            if (material.HasProperty("_BaseMetallic") && material.GetFloat("_BaseMetallic") > 0f) { metallic = material.GetFloat("_BaseMetallic"); hasExplicitMetallic = true; }
            else if (material.HasProperty("_Metallic")) { metallic = material.GetFloat("_Metallic"); hasExplicitMetallic = true; }
            else if (material.HasProperty("_Metalic")) { metallic = material.GetFloat("_Metalic"); hasExplicitMetallic = true; }

            float smoothness = 0.5f;
            bool hasSmoothness = false;
            if (material.HasProperty("_BaseSmooth")) { smoothness = material.GetFloat("_BaseSmooth"); hasSmoothness = true; }
            else if (material.HasProperty("_Smoothness")) { smoothness = material.GetFloat("_Smoothness"); hasSmoothness = true; }
            else if (material.HasProperty("_Smooth")) { smoothness = material.GetFloat("_Smooth"); hasSmoothness = true; }
            else if (material.HasProperty("_Glossiness")) { smoothness = material.GetFloat("_Glossiness"); hasSmoothness = true; }
            else if (material.HasProperty("_GlossMapScale")) { smoothness = material.GetFloat("_GlossMapScale"); hasSmoothness = true; }

            float roughness;
            bool hasExplicitRoughness = false;
            if (material.HasProperty("_Roughness")) { roughness = material.GetFloat("_Roughness"); hasExplicitRoughness = true; }
            else if (material.HasProperty("_Rough")) { roughness = material.GetFloat("_Rough"); hasExplicitRoughness = true; }
            else if (hasSmoothness) { roughness = 1.0f - Mathf.Clamp01(smoothness); hasExplicitRoughness = true; }
            else roughness = 0.7f;

            DetectUnityPbrMaps(material, shaderName, ref matData);
            if (matData.pbrMapHandle != IntPtr.Zero || matData.smoothnessInAlbedoAlpha)
            {
                // The maps' values replace the sliders; the constants stay as their typical value.
                if (matData.pbrMapMetallic) { metallic = matData.pbrMetalBias + matData.pbrMetalScale * 0.5f; hasExplicitMetallic = true; }
                if (matData.pbrMapSmoothness || matData.smoothnessInAlbedoAlpha) { roughness = matData.pbrRoughBias + matData.pbrRoughScale * 0.5f; hasExplicitRoughness = true; }
                if (verboseTextureLogging.Value)
                    logger.LogInfo($"[PbrMaps] '{material.name}': map={(matData.pbrMapHandle != IntPtr.Zero)} metallic={matData.pbrMapMetallic} ({matData.pbrMetalBias:F2}+{matData.pbrMetalScale:F2}x) " +
                                   $"smoothness={(matData.pbrMapSmoothness ? "map alpha" : matData.smoothnessInAlbedoAlpha ? "albedo alpha" : "none")} (rough {matData.pbrRoughBias:F2}+{matData.pbrRoughScale:F2}(1-a))");
            }

            matData.baseMetallic = Mathf.Clamp01(metallic);
            matData.baseRoughness = Mathf.Clamp(roughness, 0.04f, 1.0f);
            matData.hasInferredPbr = false;
            matData.autoNormalScale = 1f;
            matData.inferredMetallic = matData.baseMetallic;
            matData.inferredRoughness = matData.baseRoughness;

            // Auto PBR Heuristics (Experimental): infer physical metallic & roughness for non-PBR surfaces
            if (!hasExplicitMetallic || !hasExplicitRoughness)
            {
                if (InferPbrProperties(material, tex, renderer, shaderName, out float infMetal, out float infRough, out string matchedCat, out float normalScale))
                {
                    matData.hasInferredPbr = true;
                    matData.autoNormalScale = normalScale;
                    matData.inferMetallic = !hasExplicitMetallic;
                    matData.inferRoughness = !hasExplicitRoughness;
                    matData.categoryMetallic = infMetal;
                    matData.categoryRoughness = infRough;
                    matData.inferredMetallic = Mathf.Clamp01(!hasExplicitMetallic ? infMetal : metallic);
                    matData.inferredRoughness = Mathf.Clamp(!hasExplicitRoughness ? infRough : roughness, 0.04f, 1.0f);
                    if (ConfigAutoPBR != null && ConfigAutoPBR.Value)
                    {
                        RefineInferredPbr(ref matData, tex);
                        if (!hasExplicitMetallic) metallic = matData.inferredMetallic;
                        if (!hasExplicitRoughness) roughness = matData.inferredRoughness;
                        autoPbrMaterialCount++;
                        if (verboseTextureLogging.Value)
                            logger.LogInfo($"[AutoPBR] Inferred '{material.name}': cat={matchedCat} metal={metallic:F2} rough={roughness:F2}" +
                                           (matData.hasRoughnessDetail ? $" detail={matData.roughnessDetailBias:F2}+{matData.roughnessDetailScale:F2}x" : ""));
                    }
                }
            }

            matData.metallic = Mathf.Clamp01(metallic);
            matData.roughness = Mathf.Clamp(roughness, 0.04f, 1.0f);
            
            // Explicit maps: the category's normal strength still applies to generated normals.
            if (!(!hasExplicitMetallic || !hasExplicitRoughness) &&
                InferPbrProperties(material, tex, renderer, shaderName, out _, out _, out _, out float explicitNormalScale))
                matData.autoNormalScale = explicitNormalScale;

            // Detect alpha mode from shader keywords, _Mode property, and render queue
            var (detectedMode, detectionReason) = DetectAlphaModeWithReason(material);
            matData.alphaMode = detectedMode;
            if (material.HasProperty("_Cutoff"))
            {
                matData.alphaCutoff = material.GetFloat("_Cutoff");
            }
            if (matData.suppressLightGeneration && !shaderName.Contains("TextMeshPro"))
            {
                matData.alphaMode = AlphaMode.Blend;
            }
            // A layered material's _Shape alpha cuts out its leaves and blades when it alpha tests.
            if (layeredComposite && material.HasProperty("_Shape") && material.GetTexture("_Shape") != null &&
                (material.IsKeywordEnabled("_ALPHATEST_ON") || (material.HasProperty("_AlphaClip") && material.GetFloat("_AlphaClip") > 0f)))
            {
                matData.alphaMode = AlphaMode.Cutout;
                matData.alphaCutoff = material.HasProperty("_AlphaClip") && material.GetFloat("_AlphaClip") > 0f ? material.GetFloat("_AlphaClip") : 0.5f;
            }
            Games.GameProfiles.OverrideMaterial(material, ref matData.alphaMode, ref matData.alphaCutoff, ref matData.albedoColor);

            // Detect translucent kind (Water vs Glass vs None) for OpenRemix
            var (transKind, ior, dist, transColor, transReason) = DetectTranslucentKind(
                material, material.name, shaderName, renderer, matData.alphaMode, matData.albedoColor, matData.roughness, matData.suppressLightGeneration);
            matData.translucentKind = transKind;
            // A surface opening a view portal over its own look (a screen showing the view): it
            // must stay on the opaque path, where the portal extension attaches (OpenRemix allows
            // a portal over an opaque material only), so blending becomes an alpha cutout (its
            // shape kept) and translucency is dropped.
            if (matData.rayPortalIndex >= 0 && !matData.rayPortalStandalone)
            {
                var capturedAlpha = matData.alphaMode;
                var capturedTranslucent = matData.translucentKind;
                if (matData.alphaMode == AlphaMode.Blend)
                {
                    matData.alphaMode = AlphaMode.Cutout;
                    matData.alphaCutoff = 0.5f;
                }
                matData.translucentKind = TranslucentKind.None;
                logger.LogInfo($"[Portal] '{material.name}' ({shaderName}) opens the view portal: captured alpha {capturedAlpha}, translucent {capturedTranslucent}; " +
                               $"drawn {matData.alphaMode}, opaque, without emission");
            }
            matData.refractiveIndex = ior;
            matData.transmittanceDistance = dist;
            matData.transmittanceColor = transColor;
            
            // Detailed diagnostic log for every material
            if (verboseTextureLogging.Value)
            {
                string transStr = transKind != TranslucentKind.None ? $" transKind={transKind}({transReason},IOR={ior:F2},dist={dist:F1}m)" : "";
                logger.LogInfo($"[MaterialDiag] '{material.name}' shader='{material.shader?.name}' queue={material.renderQueue} " +
                    $"alphaMode={matData.alphaMode} reason={detectionReason}{transStr} " +
                    $"color=({matData.albedoColor.r:F3},{matData.albedoColor.g:F3},{matData.albedoColor.b:F3},{matData.albedoColor.a:F3}) " +
                    $"metallic={matData.metallic:F2} roughness={matData.roughness:F2} cutoff={matData.alphaCutoff:F3}");
            }

            // Upload albedo texture (with pre-tinting and remapping)
            albedoTex = tex;
            if (captureTextures.Value && tex != null)
            {
                // If a valid diffuse texture exists but captured color is pure black (0,0,0)
                // due to an uninitialized/unused property in ShaderGraph, default to white so the texture isn't crushed
                if (matData.albedoColor.r == 0f && matData.albedoColor.g == 0f && matData.albedoColor.b == 0f)
                {
                    matData.albedoColor = Color.white;
                }

                // Read Unity wrap/filter modes from texture and convert to MDL values
                matData.wrapModeU = UnityWrapToMdl(tex.wrapModeU);
                matData.wrapModeV = UnityWrapToMdl(tex.wrapModeV);
                matData.filterMode = (byte)(tex.filterMode == FilterMode.Point ? 0 : 1);
                
                int texId = tex.GetInstanceID();

                // Check for stylized texture value remapping curves (_Remap1, _Remap2, _Remap3)
                Vector2? remap = null;
                if (matchedTexProp == "_Texture1" && material.HasProperty("_Remap1"))
                {
                    Vector4 r = material.GetVector("_Remap1");
                    if (r.y > r.x && (r.x > 0.01f || r.y < 0.99f)) remap = new Vector2(r.x, r.y);
                }
                else if (matchedTexProp == "_Texture2" && material.HasProperty("_Remap2"))
                {
                    Vector4 r = material.GetVector("_Remap2");
                    if (r.y > r.x && (r.x > 0.01f || r.y < 0.99f)) remap = new Vector2(r.x, r.y);
                }
                else if (matchedTexProp == "_Texture3" && material.HasProperty("_Remap3"))
                {
                    Vector4 r = material.GetVector("_Remap3");
                    if (r.y > r.x && (r.x > 0.01f || r.y < 0.99f)) remap = new Vector2(r.x, r.y);
                }

                Color effectiveTint = mpbColor ?? matData.albedoColor;
                bool isTinted = effectiveTint.r < 0.98f || effectiveTint.g < 0.98f || effectiveTint.b < 0.98f;
                if (matData.suppressLightGeneration && !shaderName.Contains("TextMeshPro"))
                {
                    // Procedural particle shaders: the material drawn by Unity, not its noise texture.
                    var snapshot = renderer is ParticleSystemRenderer ? GetParticleSnapshot(material) : null;
                    if (snapshot != null)
                    {
                        tex = snapshot;
                        albedoTex = snapshot;
                        matData.wrapModeU = matData.wrapModeV = 0; // MDL Clamp
                        matData.mainTexST = new Vector4(1f, 1f, 0f, 0f);
                    }
                    var blendKind = matData.glowCoverage > 0f ? ParticleBlendKind.Additive : ParticleLook.KindOf(material);
                    var (partHandle, partHash) = UploadParticleTexture(tex, material, effectiveTint,
                        matData.glowCoverage > 0f ? matData.glowCoverage : 1f, blendKind);
                    matData.albedoHandle = partHandle;
                    matData.albedoTextureHash = partHash;
                    if (blendKind == ParticleBlendKind.Multiply || blendKind == ParticleBlendKind.MultiplyDouble)
                        matData.albedoColor = new Color(0f, 0f, 0f, matData.albedoColor.a); // darkening only
                    else if (isTinted)
                    {
                        Color linTint = (QualitySettings.activeColorSpace == ColorSpace.Linear) ? effectiveTint : effectiveTint.linear;
                        matData.albedoColor = new Color(linTint.r, linTint.g, linTint.b, matData.albedoColor.a);
                    }
                    else
                    {
                        matData.albedoColor = new Color(1f, 1f, 1f, matData.albedoColor.a);
                    }
                }
                else if (colorMask.HasValue && colorMask.Value.mask != null)
                {
                    var cm = colorMask.Value;
                    var (maskedHandle, maskedHash, metalHandle) = UploadMaskTintedAlbedoTexture(tex, cm, matData.roughness);
                    matData.albedoHandle = maskedHandle;
                    matData.albedoTextureHash = maskedHash;
                    if (isTinted)
                    {
                        Color linTint = (QualitySettings.activeColorSpace == ColorSpace.Linear) ? effectiveTint : effectiveTint.linear;
                        matData.albedoColor = new Color(linTint.r, linTint.g, linTint.b, matData.albedoColor.a);
                    }
                    else
                    {
                        matData.albedoColor = new Color(1f, 1f, 1f, matData.albedoColor.a);
                    }
                    // Per-texel metal (red) and smoothness (alpha) from the recolour.
                    if (metalHandle != IntPtr.Zero)
                    {
                        matData.pbrMapHandle = metalHandle;
                        matData.pbrMapMetallic = matData.pbrMapSmoothness = true;
                        matData.smoothnessInAlbedoAlpha = false;
                        matData.pbrMetalBias = 0f; matData.pbrMetalScale = 1f;
                        matData.pbrRoughBias = 0f; matData.pbrRoughScale = 1f;
                    }
                }
                else if (remap.HasValue || hsbc.HasValue)
                {
                    var (tintedHandle, tintedHash) = UploadTintedAlbedoTexture(tex, effectiveTint, remap, hsbc);
                    matData.albedoHandle = tintedHandle;
                    matData.albedoTextureHash = tintedHash;
                    matData.albedoColor = new Color(1f, 1f, 1f, matData.albedoColor.a);
                }
                else
                {
                    matData.albedoHandle = UploadUnityTexture(tex);
                    if (matData.albedoHandle != IntPtr.Zero)
                    {
                        if (textureHashCache.TryGetValue(texId, out ulong hash))
                        {
                            matData.albedoTextureHash = hash;
                        }
                    }
                    if (isTinted)
                    {
                        // OpenRemix natively multiplies s.tint (opaqueExt.albedoConstant) in linear space in scene.glsl
                        Color linTint = (QualitySettings.activeColorSpace == ColorSpace.Linear) ? effectiveTint : effectiveTint.linear;
                        matData.albedoColor = new Color(linTint.r, linTint.g, linTint.b, matData.albedoColor.a);
                    }
                    else
                    {
                        matData.albedoColor = new Color(1f, 1f, 1f, matData.albedoColor.a);
                    }
                }
                
                if (matData.albedoHandle != IntPtr.Zero)
                {
                    // Fallback: if shader metadata says Opaque but the texture has genuine cutout
                    // transparency (large near-zero alpha regions), upgrade to Cutout.
                    // Uses texturesWithCutoutAlpha (strict: >=10% pixels at alpha<16) instead of
                    // texturesWithAlpha (loose: any pixel<250) to avoid false positives from
                    // smoothness-as-alpha in Standard shader Opaque mode.
                    if (matData.alphaMode == AlphaMode.Opaque && texturesWithCutoutAlpha.Contains(texId)
                        && (shaderName == null || shaderName.IndexOf("Opaque", StringComparison.OrdinalIgnoreCase) < 0))
                    {
                        matData.alphaMode = AlphaMode.Cutout;
                        if (!material.HasProperty("_Cutoff"))
                            matData.alphaCutoff = 0.5f;
                        if (verboseTextureLogging.Value)
                            logger.LogInfo($"[AlphaFallback] '{material.name}': texture has alpha content, upgrading Opaque -> Cutout (cutoff={matData.alphaCutoff:F2})");
                    }
                }
            }
            
            // Capture texture tiling/offset safely without throwing on missing _MainTex
            if (albedoTex != null && particleSnapshots.ContainsValue(albedoTex))
            {
                // a particle snapshot covers the quad exactly
            }
            else if (layeredComposite)
            {
                // the composite is laid out in the UV-mapped base texture's space
                if (material.HasProperty("_BaseTexture") && material.GetTexture("_BaseTexture") != null)
                {
                    Vector2 scale = material.GetTextureScale("_BaseTexture");
                    Vector2 offset = material.GetTextureOffset("_BaseTexture");
                    matData.mainTexST = new Vector4(scale.x, scale.y, offset.x, offset.y);
                }
                else
                {
                    matData.mainTexST = new Vector4(1f, 1f, 0f, 0f);
                }
            }
            else if (matchedTexProp != null)
            {
                try
                {
                    Vector2 scale = material.GetTextureScale(matchedTexProp);
                    Vector2 offset = material.GetTextureOffset(matchedTexProp);
                    matData.mainTexST = new Vector4(scale.x, scale.y, offset.x, offset.y);
                }
                catch
                {
                    if (material.HasProperty("_MainTex"))
                    {
                        try
                        {
                            matData.mainTexST = new Vector4(
                                material.mainTextureScale.x,
                                material.mainTextureScale.y,
                                material.mainTextureOffset.x,
                                material.mainTextureOffset.y);
                        }
                        catch { }
                    }
                }
            }
            else if (material.HasProperty("_MainTex"))
            {
                try
                {
                    matData.mainTexST = new Vector4(
                        material.mainTextureScale.x,
                        material.mainTextureScale.y,
                        material.mainTextureOffset.x,
                        material.mainTextureOffset.y);
                }
                catch { }
            }

            Games.GameProfiles.OverrideTextureTransform(material, ref matData.mainTexST);

            // Fallback: no albedo texture but material has a color — create a 1x1 solid-color texture
            // so Remix renders the surface with the correct color instead of the debug checkerboard.
            if (matData.albedoHandle == IntPtr.Zero && (mpbColor.HasValue || material.HasProperty("_Color") || material.HasProperty("_BaseColor") || material.HasProperty("_Tint") || material.HasProperty("_TintColor") || material.HasProperty("_Color1") || hsbc.HasValue))
            {
                if (hsbc.HasValue)
                {
                    matData.albedoColor = ApplyHsbcToColor(matData.albedoColor, hsbc.Value);
                }
                matData.albedoHandle = GetOrCreateSolidColorTexture(matData.albedoColor);
                if (matData.albedoHandle != IntPtr.Zero)
                {
                    ulong h = (ulong)matData.albedoHandle.ToInt64();
                    matData.albedoTextureHash = h;
                }
            }
            
            // Dump shader properties once per shader to discover emission/color property names (via reflection for pre-2019 compatibility)
            if (verboseTextureLogging.Value && material.shader != null && !loggedShaderProperties.Contains(shaderName))
            {
                loggedShaderProperties.Add(shaderName);
                DumpShaderProperties(material.shader, shaderName);
            }
            
            // Upload emission — three shader paths:
            // 1. Standard/URP: _EMISSION keyword gates emission; _EmissionColor + _EmissionMap
            // 2. ULTRAKILL/Master: _EmissiveColor + _EmissiveTex + _EmissiveIntensity + EMISSIVE toggle
            // 3. Generic custom: _EmissionColor + _EmissionMultiplier (e.g. Dark Machine/SHDR_Base)
            bool hasEmission = false;
            if (captureTextures.Value)
            {
                // Standard shader path — require _EMISSION keyword or an actual _EmissionMap texture.
                // HasProperty("_EmissionColor") alone is NOT sufficient: most shaders define it
                // even when emission is disabled, often with a default white value that would
                // cause every surface to glow.
                bool hasActiveEmissionMap = material.HasProperty("_EmissionMap") && material.GetTexture("_EmissionMap") != null;
                if (material.IsKeywordEnabled("_EMISSION") || hasActiveEmissionMap)
                {
                    if (material.HasProperty("_EmissionColor"))
                    {
                        matData.emissiveColor = material.GetColor("_EmissionColor");
                        float maxChannel = Mathf.Max(matData.emissiveColor.r, Mathf.Max(matData.emissiveColor.g, matData.emissiveColor.b));
                        // Store 1.0 as intensity — CreateRemixMaterialSimple already folds maxChannel into
                        // emCombinedIntensity (= maxCh * emissiveIntensity), so storing maxChannel here
                        // would double it. The HDR brightness lives in the color, not the intensity.
                        matData.emissiveIntensity = maxChannel > 0f ? 1.0f : 0f;
                    }
                    if (hasActiveEmissionMap)
                    {
                        var emTex = material.GetTexture("_EmissionMap") as Texture2D;
                        if (emTex != null)
                        {
                            var (emHandle, emHash) = UploadTintedEmissiveTexture(emTex, matData.emissiveColor);
                            matData.emissiveHandle = emHandle;
                            matData.emissiveTextureHash = emHash;
                        }
                    }
                    hasEmission = matData.emissiveIntensity > 0f || matData.emissiveHandle != IntPtr.Zero;
                }
                
                // Custom shader path: _EmissiveColor, _EmissiveTex, _EmissiveIntensity, or MPB override
                bool hasMpbOverride = mpbEmissiveColor.HasValue || mpbColor.HasValue;
                if (!hasEmission && (material.HasProperty("_EmissiveColor") || hasMpbOverride))
                {
                    bool emissiveToggle = true;
                    if (material.HasProperty("EMISSIVE"))
                        emissiveToggle = material.GetFloat("EMISSIVE") > 0.5f;
                    
                    // A dedicated _EmissiveTex overrides the toggle (artist assigned a glow map)
                    bool hasEmissiveTex = false;
                    if (material.HasProperty("_EmissiveTex"))
                        hasEmissiveTex = material.GetTexture("_EmissiveTex") != null;
                    
                    if (emissiveToggle || hasEmissiveTex || hasMpbOverride)
                    {
                        Color defaultCol = material.HasProperty("_EmissiveColor") ? material.GetColor("_EmissiveColor") : Color.black;
                        matData.emissiveColor = mpbEmissiveColor ?? (mpbColor.HasValue ? mpbColor.Value : defaultCol);
                        
                        if (mpbEmissiveIntensity.HasValue)
                            matData.emissiveIntensity = mpbEmissiveIntensity.Value;
                        else if (material.HasProperty("_EmissiveIntensity"))
                            matData.emissiveIntensity = material.GetFloat("_EmissiveIntensity");
                        else
                        {
                            float maxCh = Mathf.Max(matData.emissiveColor.r, Mathf.Max(matData.emissiveColor.g, matData.emissiveColor.b));
                            matData.emissiveIntensity = maxCh > 0f ? maxCh : 1.0f;
                        }
                        
                        // Upload _EmissiveTex if present, pre-tinted by emission color
                        if (hasEmissiveTex)
                        {
                            var emTex = material.GetTexture("_EmissiveTex") as Texture2D;
                            if (emTex != null)
                            {
                                var (emHandle, emHash) = UploadTintedEmissiveTexture(emTex, matData.emissiveColor);
                                matData.emissiveHandle = emHandle;
                                matData.emissiveTextureHash = emHash;
                            }
                        }
                        
                        // _UseAlbedoAsEmissive when toggle is on or when MPB display override is present
                        if ((emissiveToggle || hasMpbOverride) && matData.emissiveHandle == IntPtr.Zero
                            && ((material.HasProperty("_UseAlbedoAsEmissive") && material.GetFloat("_UseAlbedoAsEmissive") > 0.5f) || hasMpbOverride)
                            && matData.albedoHandle != IntPtr.Zero)
                        {
                            if (matData.suppressLightGeneration)
                            {
                                // Reuse the de-fringed particle albedo texture directly; OpenRemix multiplies emissiveColorConstant in the shader
                                matData.emissiveHandle = matData.albedoHandle;
                                matData.emissiveTextureHash = matData.albedoTextureHash;
                            }
                            else if (albedoTex != null)
                            {
                                var (emHandle, emHash) = UploadTintedEmissiveTexture(albedoTex, matData.emissiveColor);
                                matData.emissiveHandle = emHandle;
                                matData.emissiveTextureHash = emHash;
                            }
                            else
                            {
                                matData.emissiveHandle = matData.albedoHandle;
                                matData.emissiveTextureHash = matData.albedoTextureHash;
                            }
                        }
                        
                        hasEmission = matData.emissiveIntensity > 0f && matData.emissiveHandle != IntPtr.Zero;
                    }
                }
                
                // Skybox/Panoramic on a mesh (a sky sphere around a diorama; its UVs from the
                // vertex directions, RemixMeshConverter.MapPanoramicSkies): it shows its texture
                // unlit, times _Tint (gamma, doubled: unity_ColorSpaceDouble) and _Exposure. Black
                // albedo: nothing lights it. It is no light either, and keeps its blending (a
                // queue-3001 sheet): the sun and sky still reach what it surrounds, as when Unity
                // draws it behind everything.
                if (!hasEmission && IsPanoramicSky(material) && matData.albedoHandle != IntPtr.Zero)
                {
                    Color tint = material.HasProperty("_Tint") ? material.GetColor("_Tint") : new Color(.5f, .5f, .5f, 1f);
                    float exposure = material.HasProperty("_Exposure") ? material.GetFloat("_Exposure") : 1f;
                    matData.emissiveColor = new Color(tint.r * 2f, tint.g * 2f, tint.b * 2f, 1f);
                    matData.emissiveIntensity = exposure;
                    matData.emissiveHandle = matData.albedoHandle;
                    matData.emissiveTextureHash = matData.albedoTextureHash;
                    matData.albedoColor = new Color(0f, 0f, 0f, 1f);
                    matData.autoNormalScale = 0f;
                    matData.suppressLightGeneration = true;
                    matData.emissionAsShown = true;
                    hasEmission = exposure > 0f;
                }

                // Generic custom shader path: _EmissionMultiplier + _Emission texture (e.g. Dark Machine/SHDR_Base)
                // Requires an actual texture — the multiplier alone with default _EmissionColor is not
                // a reliable gate since many shaders default _EmissionMultiplier to 1.0.
                if (!hasEmission && material.HasProperty("_EmissionMultiplier") && material.HasProperty("_Emission"))
                {
                    var emTex = material.GetTexture("_Emission") as Texture2D;
                    float multiplier = material.GetFloat("_EmissionMultiplier");
                    if (emTex != null && multiplier > 0f)
                    {
                        matData.emissiveColor = material.HasProperty("_EmissionColor")
                            ? material.GetColor("_EmissionColor")
                            : Color.white;
                        matData.emissiveIntensity = multiplier;
                        
                        var (emHandle, emHash) = UploadTintedEmissiveTexture(emTex, matData.emissiveColor);
                        matData.emissiveHandle = emHandle;
                        matData.emissiveTextureHash = emHash;
                        
                        hasEmission = true;
                    }
                }
                
                // Stanley Parable custom emissive shaders: emission texture in _TextureSample1 or _TextureSample2,
                // intensity in _Emission (Range), with optional _Useemissionmaponly toggle.
                // These shaders have "Emissive" in the name but use non-standard property names.
                if (!hasEmission && shaderName.IndexOf("Emissive", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    // Find emission texture: _TextureSample1, _TextureSample2, or fall back to albedo
                    Texture2D stanleyEmTex = null;
                    string emTexProp = null;
                    foreach (var prop in new[] { "_TextureSample1", "_TextureSample2" })
                    {
                        if (material.HasProperty(prop))
                        {
                            stanleyEmTex = material.GetTexture(prop) as Texture2D;
                            if (stanleyEmTex != null) { emTexProp = prop; break; }
                        }
                    }
                    
                    // Read intensity from _Emission (Range) if present, default to 1.0
                    float emIntensity = 1.0f;
                    if (material.HasProperty("_Emission"))
                        emIntensity = material.GetFloat("_Emission");
                    
                    if (emIntensity > 0f)
                    {
                        matData.emissiveColor = Color.white;
                        matData.emissiveIntensity = emIntensity;
                        
                        if (stanleyEmTex != null)
                        {
                            matData.emissiveHandle = UploadUnityTexture(stanleyEmTex);
                            if (matData.emissiveHandle != IntPtr.Zero)
                            {
                                int emTexId = stanleyEmTex.GetInstanceID();
                                if (textureHashCache.TryGetValue(emTexId, out ulong emHash))
                                    matData.emissiveTextureHash = emHash;
                            }
                        }
                        else if (albedoTex != null)
                        {
                            // No dedicated emission texture — use albedo as emission source
                            matData.emissiveHandle = matData.albedoHandle;
                            matData.emissiveTextureHash = matData.albedoTextureHash;
                        }
                        
                        hasEmission = matData.emissiveHandle != IntPtr.Zero;
                        if (hasEmission && verboseTextureLogging.Value)
                            logger.LogInfo($"[StanleyEmission] '{material.name}': tex={emTexProp ?? "albedo"} intensity={emIntensity:F3}");
                    }
                }
                
                // TextMeshPro Distance Field: self-lit UI text. The atlas holds a distance field
                // (Alpha8: RGB black, the field in alpha; the glyph edge at 0.5): it is converted
                // to white glyph shapes with that edge as their alpha, and alpha tested there.
                // Otherwise each glyph's whole quad shows, filled with the field's soft spread.
                // Colour: _FaceColor times the caller's colour (a UI graphic's vertex colour) as
                // constants. Intensity: the caller's (UI screens), else bright self-lit text.
                // This replaces any emission found above (a UI caller's colour override would
                // otherwise emit the raw atlas, its quads as squares).
                if (shaderName.Contains("TextMeshPro") && shaderName.Contains("Distance Field"))
                {
                    Color faceColor = material.HasProperty("_FaceColor")
                        ? material.GetColor("_FaceColor")
                        : Color.white;
                    if (mpbEmissiveColor.HasValue)
                        faceColor *= mpbEmissiveColor.Value;
                    float maxCh = Mathf.Max(faceColor.r, Mathf.Max(faceColor.g, faceColor.b));
                    if (maxCh > 0f && albedoTex != null)
                    {
                        var (emHandle, emHash) = UploadSDFEmissiveTexture(albedoTex, Color.white);
                        if (emHandle != IntPtr.Zero)
                        {
                            matData.emissiveColor = faceColor;
                            matData.emissiveIntensity = mpbEmissiveIntensity ?? 20.0f;
                            matData.emissiveHandle = emHandle;
                            matData.emissiveTextureHash = emHash;
                            hasEmission = true;

                            // The glyph shapes as albedo too (the readback is black).
                            matData.albedoHandle = emHandle;
                            matData.albedoTextureHash = emHash;
                            Color linFace = QualitySettings.activeColorSpace == ColorSpace.Linear ? faceColor : faceColor.linear;
                            matData.albedoColor = new Color(linFace.r, linFace.g, linFace.b, 1f);
                            matData.useEmissiveBlend = true;
                            matData.alphaMode = AlphaMode.Cutout;
                            matData.alphaCutoff = 0.5f;
                        }
                    }
                }
                
                // A view portal's screen emits nothing of its own: where it opens the view replaces
                // it, and its emission stayed added over the view (a white haze, its texture's dots).
                if (matData.rayPortalIndex >= 0 && !matData.rayPortalStandalone)
                    hasEmission = false;

                // An additive glow: black (nothing lights it; it hides what is behind only by its
                // coverage), emitting its texture times the glow over the coverage scale, so
                // coverage x emission is what Unity adds.
                if (matData.glowCoverage > 0f && matData.albedoHandle != IntPtr.Zero)
                {
                    matData.alphaMode = AlphaMode.Blend;
                    matData.emissiveHandle = matData.albedoHandle;
                    matData.emissiveTextureHash = matData.albedoTextureHash;
                    matData.emissiveColor = matData.glowColor;
                    matData.emissiveIntensity = 1f;
                    matData.albedoColor = new Color(0f, 0f, 0f, 1f);
                    matData.translucentKind = TranslucentKind.None;
                    hasEmission = true;
                    logger.LogInfo($"[Glow] '{material.name}' ({shaderName}) on {(renderer != null ? renderer.name : "?")}: additive, " +
                                   $"coverage x{matData.glowCoverage:F2}, emission ({matData.glowColor.r:F2},{matData.glowColor.g:F2},{matData.glowColor.b:F2})");
                }
                else if (!isParticleOrTrailRenderer && matData.alphaMode == AlphaMode.Blend &&
                         matData.albedoHandle != IntPtr.Zero && IsUnlitSheetShader(shaderName))
                {
                    // An unlit see-through sheet on a mesh (Sprites/Default, Unlit/Transparent,
                    // UI/Default): Unity shows its own colour whatever lights it. Black (nothing
                    // lights it), emitting its texture times its tint, covering by its alpha:
                    // composed as Unity alpha-blends it. Lit, a blue glow quad came out grey.
                    matData.emissiveHandle = matData.albedoHandle;
                    matData.emissiveTextureHash = matData.albedoTextureHash;
                    matData.emissiveColor = new Color(matData.albedoColor.r, matData.albedoColor.g, matData.albedoColor.b, 1f);
                    matData.emissiveIntensity = 1f;
                    matData.albedoColor = new Color(0f, 0f, 0f, matData.albedoColor.a);
                    matData.translucentKind = TranslucentKind.None;
                    matData.suppressLightGeneration = true;
                    // (Its emission is what Unity shows, not in the scene's light units.)
                    matData.unlitSheet = true;
                    hasEmission = true;
                }
                else if (matData.glowCoverage > 0f && matData.glowStrength <= 0f)
                {
                    // An untextured additive material that adds nothing: fully transparent.
                    matData.alphaMode = AlphaMode.Blend;
                    matData.albedoColor = new Color(0f, 0f, 0f, 0f);
                    matData.translucentKind = TranslucentKind.None;
                }

                // Safety net: if no path confirmed real emission, ensure emissive state is clean.
                // Prevents stale values from a path that read properties but decided not to emit.
                if (!hasEmission)
                {
                    matData.emissiveColor = Color.black;
                    matData.emissiveIntensity = 0f;
                    matData.emissiveHandle = IntPtr.Zero;
                    matData.emissiveTextureHash = 0;
                }
                else if (verboseTextureLogging.Value)
                {
                    logger.LogInfo($"[Emission] '{material.name}': color=({matData.emissiveColor.r:F3},{matData.emissiveColor.g:F3},{matData.emissiveColor.b:F3}) " +
                        $"intensity={matData.emissiveIntensity:F3} texHash=0x{matData.emissiveTextureHash:X16}");
                }
            }
            
            // Upload normal map
            if (captureTextures.Value)
            {
                for (int p = 0; p < NormalTextureProps.Length; p++)
                {
                    string prop = NormalTextureProps[p];
                    if (material.HasProperty(prop))
                    {
                        var normTex = material.GetTexture(prop) as Texture2D;
                        if (normTex != null)
                        {
                            matData.normalHandle = UploadUnityTexture(normTex, isNormalMap: true);
                            if (matData.normalHandle != IntPtr.Zero)
                            {
                                int texId = normTex.GetInstanceID();
                                if (textureHashCache.TryGetValue(texId, out ulong hash))
                                {
                                    matData.normalTextureHash = hash;
                                }
                                if (verboseTextureLogging.Value)
                                    logger.LogInfo($"Captured normal texture for material '{material.name}' using '{prop}' (hash: 0x{matData.normalTextureHash:X16})");
                            }
                            break;
                        }
                    }
                }

                matData.originalNormalHandle = matData.normalHandle;
                matData.originalNormalTextureHash = matData.normalTextureHash;
                matData.albedoTex = albedoTex;

                if (matData.suppressLightGeneration || matData.translucentKind != TranslucentKind.None || matData.alphaMode == AlphaMode.Blend)
                    matData.autoNormalScale = 0f;

                // Auto Normal Map Generation (Experimental): Sobel height filter on albedo texture
                if (matData.normalHandle == IntPtr.Zero && albedoTex != null && matData.autoNormalScale > 0f
                    && ConfigAutoNormalMaps != null && ConfigAutoNormalMaps.Value)
                {
                    float strength = (ConfigAutoNormalStrength != null ? ConfigAutoNormalStrength.Value : 1.0f) * matData.autoNormalScale;
                    var (genHandle, genHash) = GetOrCreateAutoNormalMap(albedoTex, strength);
                    if (genHandle != IntPtr.Zero)
                    {
                        matData.normalHandle = genHandle;
                        matData.normalTextureHash = genHash;
                        autoNormalMapCount++;
                        if (verboseTextureLogging.Value)
                            logger.LogInfo($"[AutoNormal] Generated normal map for '{material.name}' from '{albedoTex.name}' (hash: 0x{genHash:X16})");
                    }
                }
            }
            
            // A particle-like blended material on a mesh (an additive glow, a particle shader's
            // sphere): its coverage needs a blended instance, as particles have (opacity 1,
            // RemixMeshConverter.DrawMeshInstance), else it stands opaque.
            matData.blendedInstance = !isParticleOrTrailRenderer && matData.suppressLightGeneration &&
                                      matData.alphaMode == AlphaMode.Blend && !matData.emissionAsShown &&
                                      !shaderName.Contains("TextMeshPro") && matData.rayPortalIndex < 0;

            // Store for later use
            materialTextureData[materialId] = matData;

            // World-projected layered materials: meshes give them box-projected UVs.
            if (layeredComposite)
            {
                float projection = LayeredMaterialCompositor.ProjectionScale(material);
                if (projection > 0f)
                    projectionScales[materialId] = projection;
            }
            // Upward-facing surfaces of a top-colored layered material: a variant with the top albedo.
            if (layeredComposite && LayeredMaterialCompositor.UsesTopColor(material, out float topThreshold))
                RegisterTopVariant(material, materialId, matData, topThreshold);

            // The first materials of each shader, described once: what the shader offers and what Remix got.
            if (material.shader != null)
            {
                describedShaderMaterials.TryGetValue(shaderName, out int described);
                if (described < 2)
                {
                    describedShaderMaterials[shaderName] = described + 1;
                    try
                    {
                        TryDescribeMaterial(materialId, out string result);
                        logger.LogInfo($"[MaterialInfo] '{material.name}' shader '{shaderName}' queue {material.renderQueue}, albedo from {matchedTexProp ?? "none"} | " +
                                       $"{DescribeMaterialProperties(material)} | Remix {result}");
                    }
                    catch (Exception) { } // Unity without Shader.GetPropertyCount (before 2019.3)
                }
            }

            // DISABLED: Async material creation causes deadlocks with Remix API
            // Materials are now created on-demand on the render thread during mesh creation
            // This prevents deadlocks between material thread and render thread competing for Remix device lock
            
            if (verboseTextureLogging != null && verboseTextureLogging.Value)
            {
                string albedoPath = GetTexturePathFromHandle(matData.albedoHandle);
                string normalPath = GetTexturePathFromHandle(matData.normalHandle);
                logger.LogInfo($"[MatCapture] '{material.name}' shader='{material.shader?.name}' albedo={albedoPath ?? "NONE"} normal={normalPath ?? "none"}");
            }
        }
    }
}
