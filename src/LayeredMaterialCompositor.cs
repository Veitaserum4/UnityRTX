using System;
using System.Collections.Generic;
using UnityEngine;

namespace UnityRemix
{
    /// <summary>
    /// Stylized "layered" materials (PEAK's W/ and GD/ shaders, and shaders built the same way):
    /// a base color, then color layers each masked by a grayscale texture through a remap range
    /// (_ColorN with its alpha as strength, _TextureN, _RemapN), then a tint. Their textures are
    /// masks, not albedo. The layers are composited into one albedo texture: layers mapped by UV
    /// keep their pattern, triplanar (world-projected) layers, which the mesh UVs cannot reproduce,
    /// contribute their average coverage. Alpha comes from a _Shape texture when there is one.
    /// Main thread only.
    /// </summary>
    internal static class LayeredMaterialCompositor
    {
        private struct Layer
        {
            public Color color;
            public Texture texture;
            public Vector2 remap;
            public bool triplanar;
            public float scale;
        }

        public static bool IsLayered(Material material)
        {
            return material.HasProperty("_BaseColor") && material.HasProperty("_Color1") &&
                   material.HasProperty("_Texture1") && material.HasProperty("_Remap1");
        }

        /// <summary>
        /// How much of the mesh's vertex colors is color: _VertexColorAmount when the shader has it,
        /// nothing for a layered material without it (there they are masks), else all of them.
        /// </summary>
        public static float VertexColorWeight(Material material)
        {
            if (material.HasProperty("_VertexColorAmount"))
                return Mathf.Clamp01(material.GetFloat("_VertexColorAmount"));
            return IsLayered(material) ? 0f : 1f;
        }

        /// <summary>
        /// Whether upward-facing surfaces take a separate top color (_USETOPCOLOR_ON), and the
        /// threshold on a face's up component above which they do.
        /// </summary>
        public static bool UsesTopColor(Material material, out float threshold)
        {
            threshold = 0.5f;
            if (!IsLayered(material) || !material.IsKeywordEnabled("_USETOPCOLOR_ON") || !material.HasProperty("_TopColor"))
                return false;
            if (material.HasProperty("_TopColorAmount") && material.GetFloat("_TopColorAmount") <= 0.01f)
                return false;
            if (material.HasProperty("_TopColorRemap"))
            {
                var remap = material.GetVector("_TopColorRemap");
                if (remap.y > remap.x)
                    threshold = Mathf.Clamp((remap.x + remap.y) * 0.5f, 0.05f, 0.95f);
            }
            return true;
        }

        /// <summary>
        /// The material's albedo, or with top the color of its upward-facing surfaces: the top
        /// layers (_TopColor, _TopTex/_TopTex2 masks) blended over the albedo by _TopColorAmount.
        /// </summary>
        public static Texture2D Compose(Material material, int size, bool top, out string description)
        {
            var layers = new List<Layer>();
            AddLayer(material, layers, "_Color1", "_Texture1", "_Remap1", "_Triplanar1On", "_Triplanar1", "_UV1");
            AddLayer(material, layers, material.HasProperty("_Color21") ? "_Color21" : "_Color2", "_Texture2", "_Remap2", "_Triplanar2On", "_Triplanar2", "_UV2");
            AddLayer(material, layers, "_Color3", "_Texture3", "_Remap3", "_Triplanar3On", "_Triplanar3", "_UV3");
            var topLayers = new List<Layer>();
            if (top)
            {
                AddLayer(material, topLayers, "_TopColor1", "_TopTex", "_TopRemap1", "_TopTriplanar1On", "_TopTriplanar1", "_TopUV1");
                AddLayer(material, topLayers, "_TopColor2", "_TopTex2", "_TopRemap2", "_TopTriplanar2On", "_TopTriplanar2", "_TopUV2");
            }

            // Material colors are sRGB; textures read back linear: compose in linear.
            bool linear = QualitySettings.activeColorSpace == ColorSpace.Linear;
            Color Lin(Color c) => linear ? c.linear : c;
            for (int l = 0; l < layers.Count; l++) { var layer = layers[l]; layer.color = Lin(layer.color); layers[l] = layer; }
            for (int l = 0; l < topLayers.Count; l++) { var layer = topLayers[l]; layer.color = Lin(layer.color); topLayers[l] = layer; }
            Color baseColor = Lin(material.GetColor("_BaseColor"));
            Color tint = material.HasProperty("_Tint") ? Lin(material.GetColor("_Tint")) : Color.white;
            Color topColor = top ? Lin(material.GetColor("_TopColor")) : Color.white;
            float topAmount = top && material.HasProperty("_TopColorAmount") ? Mathf.Clamp01(material.GetFloat("_TopColorAmount")) : 1f;
            Texture shape = material.HasProperty("_Shape") ? material.GetTexture("_Shape") : null;
            // The UV-mapped base texture (props), scaled by its own tiling.
            Texture baseTexture = material.HasProperty("_BaseTexture") ? material.GetTexture("_BaseTexture") : null;
            float baseAmount = material.HasProperty("_BaseTexAmount") ? Mathf.Clamp01(material.GetFloat("_BaseTexAmount")) : 1f;
            Color[] basePixels = baseTexture != null ? ParticleMaterialSnapshot.Read(baseTexture, size) : null;

            // Each layer's mask over the texture, and its mean (what a triplanar layer contributes).
            ReadMasks(layers, size, out var masks, out var means);
            ReadMasks(topLayers, size, out var topMasks, out var topMeans);
            var alpha = shape != null ? ParticleMaterialSnapshot.Read(shape, size) : null;

            var output = new Color32[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    int i = y * size + x;
                    Color c = baseColor;
                    if (basePixels != null)
                        c *= Color.Lerp(Color.white, basePixels[i], baseAmount);
                    c = ApplyLayers(c, layers, masks, means, x, y, size);
                    if (top)
                        c = Color.Lerp(c, ApplyLayers(topColor, topLayers, topMasks, topMeans, x, y, size), topAmount);
                    c *= tint;
                    float a = alpha != null ? alpha[i].a : 1f;
                    if (linear)
                        c = new Color(Mathf.LinearToGammaSpace(Mathf.Clamp01(c.r)), Mathf.LinearToGammaSpace(Mathf.Clamp01(c.g)), Mathf.LinearToGammaSpace(Mathf.Clamp01(c.b)));
                    output[i] = new Color32((byte)(Mathf.Clamp01(c.r) * 255f + 0.5f), (byte)(Mathf.Clamp01(c.g) * 255f + 0.5f),
                                            (byte)(Mathf.Clamp01(c.b) * 255f + 0.5f), (byte)(Mathf.Clamp01(a) * 255f + 0.5f));
                }
            }
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, true, false)
            {
                name = "Layered_" + material.name,
                wrapMode = TextureWrapMode.Repeat,
                filterMode = FilterMode.Bilinear
            };
            texture.SetPixels32(output);
            texture.Apply(true, false);

            description = $"base ({baseColor.r:F2},{baseColor.g:F2},{baseColor.b:F2}){(baseTexture != null ? $" x '{baseTexture.name}' ({baseAmount:F2})" : "")} tint ({tint.r:F2},{tint.g:F2},{tint.b:F2}), " +
                          $"layers [{DescribeLayers(layers, means)}]{(shape != null ? $", alpha from '{shape.name}'" : "")}" +
                          (top ? $"; top ({topColor.r:F2},{topColor.g:F2},{topColor.b:F2}) x{topAmount:F2} layers [{DescribeLayers(topLayers, topMeans)}]" : "") +
                          " (linear)";
            return texture;
        }

        private static string DescribeLayers(List<Layer> layers, float[] means)
        {
            var parts = new List<string>();
            for (int l = 0; l < layers.Count; l++)
                parts.Add($"({layers[l].color.r:F2},{layers[l].color.g:F2},{layers[l].color.b:F2},{layers[l].color.a:F2}) {(layers[l].texture != null ? layers[l].texture.name : "none")} " +
                          $"{(layers[l].triplanar ? "triplanar" : "uv x" + layers[l].scale.ToString("G3"))} mean {means[l]:F2}");
            return string.Join("; ", parts);
        }

        // Each layer's mask over the texture, and its mean (what a triplanar layer contributes).
        private static void ReadMasks(List<Layer> layers, int size, out float[][] masks, out float[] means)
        {
            masks = new float[layers.Count][];
            means = new float[layers.Count];
            for (int l = 0; l < layers.Count; l++)
            {
                var layer = layers[l];
                if (layer.texture == null)
                {
                    means[l] = 1f; // a layer without a mask covers everything
                    continue;
                }
                var pixels = ParticleMaterialSnapshot.Read(layer.texture, size);
                var mask = new float[pixels.Length];
                double sum = 0;
                for (int i = 0; i < pixels.Length; i++)
                {
                    float v = Remap(pixels[i].r, layer.remap);
                    mask[i] = v;
                    sum += v;
                }
                means[l] = (float)(sum / pixels.Length);
                masks[l] = mask;
            }
        }

        private static Color ApplyLayers(Color c, List<Layer> layers, float[][] masks, float[] means, int x, int y, int size)
        {
            for (int l = 0; l < layers.Count; l++)
            {
                var layer = layers[l];
                float m;
                if (masks[l] == null || layer.triplanar)
                    m = means[l];
                else
                {
                    // the layer tiles _UVn times across the texture's 0..1
                    int sx = (int)((x * layer.scale) % size), sy = (int)((y * layer.scale) % size);
                    m = masks[l][Mathf.Clamp(sy, 0, size - 1) * size + Mathf.Clamp(sx, 0, size - 1)];
                }
                c = Color.Lerp(c, layer.color, Mathf.Clamp01(m * layer.color.a));
            }
            return c;
        }

        private static void AddLayer(Material material, List<Layer> layers, string colorProp, string textureProp, string remapProp,
                                     string triplanarOnProp, string triplanarProp, string scaleProp)
        {
            if (!material.HasProperty(colorProp))
                return;
            var color = material.GetColor(colorProp);
            if (color.a <= 0.001f)
                return; // no strength: the layer does not show
            var remap = material.HasProperty(remapProp) ? (Vector2)material.GetVector(remapProp) : new Vector2(0f, 1f);
            bool triplanar = (material.HasProperty(triplanarOnProp) && material.GetFloat(triplanarOnProp) > 0.5f) ||
                             (material.HasProperty(triplanarProp) && material.GetFloat(triplanarProp) > 0.5f);
            float scale = material.HasProperty(scaleProp) ? Mathf.Max(0.01f, material.GetFloat(scaleProp)) : 1f;
            layers.Add(new Layer
            {
                color = color,
                texture = material.HasProperty(textureProp) ? material.GetTexture(textureProp) : null,
                remap = remap,
                triplanar = triplanar,
                scale = scale
            });
        }

        private static float Remap(float value, Vector2 range)
        {
            if (range.y <= range.x + 1e-4f)
                return value >= range.x ? 1f : 0f;
            float t = Mathf.Clamp01((value - range.x) / (range.y - range.x));
            return t * t * (3f - 2f * t); // smoothstep
        }
    }
}
