using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Rendering;
using Unity.Collections.LowLevel.Unsafe;

namespace UnityRemix
{
    // Renderer materials: colour masks, skinned material facts, material resolution.
    public partial class RemixFrameCapture
    {
        private RemixMaterialManager.ColorMask? ResolveColorMask(Renderer renderer, Material material, ref int matId)
        {
            if (renderer == null || material == null)
                return null;
            if (colorMaskBlock == null)
                colorMaskBlock = new MaterialPropertyBlock();
            colorMaskBlock.Clear();
            if (renderer.HasPropertyBlock())
                renderer.GetPropertyBlock(colorMaskBlock);
            if (!Games.GameProfiles.GetColorMask(material, colorMaskBlock, out var colorMask))
                return null;
            matId = HashCombine(matId, colorMask.Hash());
            return colorMask;
        }
        
        /// <summary>
        /// Resolve the best material for a skinned mesh renderer. Returns material instance ID.
        /// </summary>
        // ResolveMaterial runs per skinned renderer per frame: property IDs, one reused property
        // block, and each material's property set (fixed by its shader) looked up once.
        private static readonly int[] SkinnedMpbColorProps =
        {
            PropColor, PropBaseColor, Shader.PropertyToID("_TintColor"), Shader.PropertyToID("_Tint"),
            Shader.PropertyToID("_Color1"), Shader.PropertyToID("_Color2"), Shader.PropertyToID("_ColorTint"),
            Shader.PropertyToID("_BodyColor"), Shader.PropertyToID("_SkinColor"), Shader.PropertyToID("_FurColor"),
            Shader.PropertyToID("_HairColor"), Shader.PropertyToID("_ClothColor"), Shader.PropertyToID("_ClothingColor"),
            Shader.PropertyToID("_EyeColor")
        };
        private static readonly int PropEmissiveIntensity = Shader.PropertyToID("_EmissiveIntensity");
        private static readonly int PropColorTint = Shader.PropertyToID("_ColorTint");
        private static readonly int PropHue = Shader.PropertyToID("_Hue");
        private static readonly int PropHueShift = Shader.PropertyToID("_HueShift");
        private static readonly int PropHueAdjust = Shader.PropertyToID("_HueAdjust");
        private static readonly int PropBrightness = Shader.PropertyToID("_Brightness");
        private static readonly int PropContrast = Shader.PropertyToID("_Contrast");
        private static readonly int PropSaturation = Shader.PropertyToID("_Saturation");
        private static readonly int PropHSBC = Shader.PropertyToID("_HSBC");
        private MaterialPropertyBlock skinnedMpb;

        private struct SkinnedMaterialFacts
        {
            public Shader shader;
            public bool emissiveIntensity, colorTint, hue, hueShift, hueAdjust, brightness, contrast, saturation, hsbc;
        }
        private readonly Dictionary<int, SkinnedMaterialFacts> skinnedMaterialFacts = new Dictionary<int, SkinnedMaterialFacts>();

        private SkinnedMaterialFacts GetSkinnedMaterialFacts(Material mat)
        {
            int id = mat.GetInstanceID();
            var shader = mat.shader;
            if (skinnedMaterialFacts.TryGetValue(id, out var facts) && ReferenceEquals(facts.shader, shader))
                return facts;
            facts = new SkinnedMaterialFacts
            {
                shader = shader,
                emissiveIntensity = mat.HasProperty(PropEmissiveIntensity),
                colorTint = mat.HasProperty(PropColorTint),
                hue = mat.HasProperty(PropHue),
                hueShift = mat.HasProperty(PropHueShift),
                hueAdjust = mat.HasProperty(PropHueAdjust),
                brightness = mat.HasProperty(PropBrightness),
                contrast = mat.HasProperty(PropContrast),
                saturation = mat.HasProperty(PropSaturation),
                hsbc = mat.HasProperty(PropHSBC)
            };
            skinnedMaterialFacts[id] = facts;
            return facts;
        }

        private int ResolveMaterial(SkinnedMeshRenderer skinned, Material targetMat = null, bool doLog = false)
        {
            int matId = 0;
            Material bestMaterial = targetMat;
            
            if (bestMaterial == null)
            {
                var materials = skinned.sharedMaterials;
                if (materials == null || materials.Length == 0)
                    return 0;
                
                string[] textureProps = { "_MainTex", "_BaseMap", "_BaseTexture", "_Texture1", "_Texture", "_BaseColorMap", "_AlbedoTex", "_Albedo", "_Diffuse", "_TopTex" };
                
                foreach (var mat in materials)
                {
                    if (mat == null) continue;
                    bool hasTexture = false;
                    try
                    {
                        if (mat.HasProperty("_MainTex"))
                            hasTexture = mat.mainTexture != null;
                        if (!hasTexture)
                        {
                            foreach (var prop in textureProps)
                            {
                                if (mat.HasProperty(prop) && mat.GetTexture(prop) != null)
                                { hasTexture = true; break; }
                            }
                        }
                    }
                    catch { }
                    if (hasTexture) { bestMaterial = mat; break; }
                }
                
                if (bestMaterial == null)
                {
                    foreach (var mat in materials)
                    {
                        if (mat != null) { bestMaterial = mat; break; }
                    }
                }
            }
            
            if (bestMaterial != null)
            {
                matId = StaticGeometryDedupe.GetCanonicalMaterialId(bestMaterial);
                
                // Check for MaterialPropertyBlock overrides (used by games for dynamic per-renderer colors/emission)
                Color? mpbColor = null;
                Color? mpbEmissiveColor = null;
                float? mpbEmissiveIntensity = null;
                Texture2D mpbMainTex = null;
                var facts = GetSkinnedMaterialFacts(bestMaterial);
                try
                {
                    if (skinnedMpb == null)
                        skinnedMpb = new MaterialPropertyBlock();
                    var mpb = skinnedMpb;
                    if (skinned.HasPropertyBlock())
                        skinned.GetPropertyBlock(mpb);
                    else
                        mpb.Clear();
                    if (!mpb.isEmpty)
                    {
                        // Check for main texture override
                        Texture tex = mpb.GetTexture(PropMainTex);
                        if (tex == null) tex = mpb.GetTexture(PropBaseMap);
                        if (tex == null) tex = mpb.GetTexture(PropDiffuse);
                        if (tex == null) tex = mpb.GetTexture(PropTexture);
                        if (tex != null && tex is Texture2D t2d)
                        {
                            mpbMainTex = t2d;
                            matId = HashCombine(matId, tex.GetInstanceID());
                        }

                        // Capture albedo/diffuse color overrides (ATLYSS character skin/hair/eye colors)
                        foreach (var cp in SkinnedMpbColorProps)
                        {
                            var c = mpb.GetColor(cp);
                            // GetColor returns Color.clear (0,0,0,0) if the property is missing from the MPB.
                            // Ignore missing properties, and ignore pure white (no tint).
                            if (c.a > 0.0f && (c.r < 0.99f || c.g < 0.99f || c.b < 0.99f || c.a < 0.99f))
                            {
                                mpbColor = c;
                                // Generate unique material ID for this color override
                                Color32 c32 = c;
                                int colInt = (c32.r << 24) | (c32.g << 16) | (c32.b << 8) | c32.a;
                                matId = HashCombine(matId, colInt);
                                break;
                            }
                        }

                        var emC = mpb.GetColor(PropEmissiveColor);
                        if (emC.a > 0.0f && (emC.r > 0.01f || emC.g > 0.01f || emC.b > 0.01f))
                        {
                            mpbEmissiveColor = emC;
                            Color32 e32 = emC;
                            int emisInt = (e32.r << 24) | (e32.g << 16) | (e32.b << 8) | e32.a;
                            matId = HashCombine(matId, emisInt);
                        }
                        if (facts.emissiveIntensity)
                        {
                            float v = mpb.GetFloat(PropEmissiveIntensity);
                            if (v > 0) mpbEmissiveIntensity = v;
                        }
                    }
                }
                catch { }

                // Check for per-material HSV or ColorTint customizations
                if (!mpbColor.HasValue)
                {
                    try
                    {
                        if (facts.colorTint)
                        {
                            Color ct = bestMaterial.GetColor(PropColorTint);
                            if (ct.a > 0f && (ct.r < 0.98f || ct.g < 0.98f || ct.b < 0.98f))
                            {
                                Color32 c32 = ct;
                                int colInt = (c32.r << 24) | (c32.g << 16) | (c32.b << 8) | c32.a;
                                matId = HashCombine(matId, colInt);
                            }
                        }

                        if (facts.hue || facts.hueShift || facts.hueAdjust ||
                            facts.brightness || facts.contrast || facts.saturation ||
                            facts.hsbc)
                        {
                            float h = facts.hue ? bestMaterial.GetFloat(PropHue) :
                                      (facts.hueShift ? bestMaterial.GetFloat(PropHueShift) :
                                      (facts.hueAdjust ? bestMaterial.GetFloat(PropHueAdjust) : 0f));
                            float b = facts.brightness ? bestMaterial.GetFloat(PropBrightness) : 0f;
                            float c = facts.contrast ? bestMaterial.GetFloat(PropContrast) : 1f;
                            float s = facts.saturation ? bestMaterial.GetFloat(PropSaturation) : 1f;
                            if (facts.hsbc)
                            {
                                Vector4 v = bestMaterial.GetVector(PropHSBC);
                                h = v.x; s = v.y; b = v.z; c = v.w;
                            }

                            if (Mathf.Abs(h) > 0.001f || Mathf.Abs(b) > 0.001f || Mathf.Abs(c - 1f) > 0.001f || Mathf.Abs(s - 1f) > 0.001f)
                            {
                                int hInt = Mathf.RoundToInt(h * 1000f);
                                int bInt = Mathf.RoundToInt(b * 1000f);
                                int cInt = Mathf.RoundToInt(c * 1000f);
                                int sInt = Mathf.RoundToInt(s * 1000f);
                                int hsbcHash = HashCombine(HashCombine(hInt, bInt), HashCombine(cInt, sInt));
                                matId = HashCombine(matId, hsbcHash);
                            }
                        }
                    }
                    catch { }
                }

                var colorMask = ResolveColorMask(skinned, bestMaterial, ref matId);
                materialManager.CaptureMaterialTextures(bestMaterial, matId, mpbEmissiveColor, mpbEmissiveIntensity, mpbMainTex, mpbColor, colorMask: colorMask);
                

            }
            
            return matId;
        }
    }
}
