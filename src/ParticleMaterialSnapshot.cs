using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace UnityRemix
{
    /// <summary>
    /// Particle materials whose shape and color the shader computes (shader-graph smoke and fire:
    /// a radial falloff over a tiling noise texture, color ramps) have no texture Remix could use;
    /// their only texture is the noise. Unity draws such a material once on a quad, over black and
    /// over white, and the two images give its straight color and alpha. Main thread only.
    /// </summary>
    internal static class ParticleMaterialSnapshot
    {
        private static Mesh quad;
        private static readonly int CameraDepthTexture = Shader.PropertyToID("_CameraDepthTexture");

        /// <summary>
        /// Whether the material's texture cannot be the particle's image: none at all, or one whose
        /// border is opaque and not black (a tiling noise, not a sprite fading out at its edges or an
        /// additive sprite on black).
        /// </summary>
        public static bool ShouldSnapshot(Texture source, out string reason)
        {
            if (source == null)
            {
                reason = "no texture";
                return true;
            }
            const int size = 32;
            var pixels = Read(source, size);
            float alpha = 0f, brightness = 0f;
            int count = 0;
            for (int i = 0; i < size; i++)
            {
                foreach (int index in new[] { i, (size - 1) * size + i, i * size, i * size + size - 1 })
                {
                    var c = pixels[index];
                    alpha += c.a;
                    brightness += Mathf.Max(c.r, Mathf.Max(c.g, c.b));
                    count++;
                }
            }
            alpha /= count;
            brightness /= count;
            reason = $"'{source.name}' border alpha {alpha:F2}, brightness {brightness:F2}";
            return alpha > 0.5f && brightness > 0.1f;
        }

        /// <summary>
        /// The material drawn on a quad covering a size x size texture, or null when it draws
        /// nothing (a pass this cannot run, or a shader that needs the scene).
        /// </summary>
        public static Texture2D Render(Material material, int size, out string result)
        {
            int pass = FindColorPass(material);
            if (pass < 0)
            {
                result = "no color pass";
                return null;
            }
            // Particle shaders read particle age, random values or custom data from the extra
            // texture coordinate channels: zero first (no custom streams), then mid-life values.
            Color32[] output = null;
            float maxAlpha = 0f;
            float stream = 0f;
            foreach (float candidate in new[] { 0f, 0.5f, 1f })
            {
                output = Compose(material, pass, size, candidate, out maxAlpha);
                stream = candidate;
                if (maxAlpha >= 0.02f)
                    break;
            }
            if (maxAlpha < 0.02f)
            {
                result = $"pass {pass} drew nothing";
                return null;
            }
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false, false)
            {
                name = "ParticleSnapshot_" + material.name,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };
            texture.SetPixels32(output);
            texture.Apply(false, false);
            result = $"pass {pass} '{PassName(material, pass)}', streams {stream:F1}, max alpha {maxAlpha:F2}";
            return texture;
        }

        private static Color32[] Compose(Material material, int pass, int size, float stream, out float maxAlpha)
        {
            SetStreams(stream);
            var overBlack = Draw(material, pass, size, Color.clear);
            var overWhite = Draw(material, pass, size, Color.white);

            var output = new Color32[size * size];
            maxAlpha = 0f;
            bool linear = QualitySettings.activeColorSpace == ColorSpace.Linear;
            for (int i = 0; i < output.Length; i++)
            {
                Color b = overBlack[i], w = overWhite[i];
                // Over black: color * alpha (or the color, additive); over white also 1 - alpha:
                // the difference is the background left visible. Per channel for additive shaders.
                float a = 0f;
                for (int ch = 0; ch < 3; ch++)
                    a = Mathf.Max(a, 1f - Mathf.Clamp01(w[ch] - b[ch]));
                a = Mathf.Clamp01(a);
                maxAlpha = Mathf.Max(maxAlpha, a);
                Color c = a > 1f / 255f ? new Color(b.r / a, b.g / a, b.b / a) : Color.black;
                if (linear)
                    c = new Color(Mathf.LinearToGammaSpace(Mathf.Clamp01(c.r)), Mathf.LinearToGammaSpace(Mathf.Clamp01(c.g)), Mathf.LinearToGammaSpace(Mathf.Clamp01(c.b)));
                output[i] = new Color32((byte)(Mathf.Clamp01(c.r) * 255f + 0.5f), (byte)(Mathf.Clamp01(c.g) * 255f + 0.5f),
                                        (byte)(Mathf.Clamp01(c.b) * 255f + 0.5f), (byte)(a * 255f + 0.5f));
            }
            return output;
        }

        // TEXCOORD0.zw and TEXCOORD1-2 of the quad: where particle custom vertex streams arrive.
        private static void SetStreams(float value)
        {
            EnsureQuad();
            var uv0 = new List<Vector4> { new Vector4(0, 0, value, value), new Vector4(1, 0, value, value), new Vector4(1, 1, value, value), new Vector4(0, 1, value, value) };
            var extra = new List<Vector4> { new Vector4(value, value, value, value), new Vector4(value, value, value, value), new Vector4(value, value, value, value), new Vector4(value, value, value, value) };
            quad.SetUVs(0, uv0);
            quad.SetUVs(1, extra);
            quad.SetUVs(2, extra);
        }

        // The first pass that draws color: not shadows, depth, motion vectors, meta or picking.
        private static int FindColorPass(Material material)
        {
            for (int i = 0; i < material.passCount; i++)
            {
                string name = PassName(material, i).ToLowerInvariant();
                if (name.Contains("shadow") || name.Contains("depth") || name.Contains("motion") || name.Contains("meta") ||
                    name.Contains("picking") || name.Contains("selection") || name.Contains("normals") || name.Contains("gbuffer"))
                    continue;
                return i;
            }
            return material.passCount > 0 ? 0 : -1;
        }

        private static string PassName(Material material, int pass)
        {
            try { return material.GetPassName(pass) ?? ""; }
            catch { return ""; }
        }

        private static void EnsureQuad()
        {
            if (quad != null)
                return;
            quad = new Mesh { name = "ParticleSnapshotQuad" };
            quad.SetVertices(new List<Vector3> { new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, -0.5f, 0f), new Vector3(0.5f, 0.5f, 0f), new Vector3(-0.5f, 0.5f, 0f) });
            quad.SetUVs(0, new List<Vector4> { new Vector4(0, 0, 0, 0), new Vector4(1, 0, 0, 0), new Vector4(1, 1, 0, 0), new Vector4(0, 1, 0, 0) });
            quad.SetColors(new List<Color32> { new Color32(255, 255, 255, 255), new Color32(255, 255, 255, 255), new Color32(255, 255, 255, 255), new Color32(255, 255, 255, 255) });
            quad.SetNormals(new List<Vector3> { Vector3.back, Vector3.back, Vector3.back, Vector3.back });
            quad.SetTriangles(new[] { 0, 2, 1, 0, 3, 2 }, 0);
            quad.RecalculateBounds();
        }

        private static Color[] Draw(Material material, int pass, int size, Color background)
        {
            EnsureQuad();
            var target = RenderTexture.GetTemporary(size, size, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);
            // Depth fade against the scene: put the scene at the far plane.
            var savedDepth = Shader.GetGlobalTexture(CameraDepthTexture);
            Shader.SetGlobalTexture(CameraDepthTexture, SystemInfo.usesReversedZBuffer ? Texture2D.blackTexture : Texture2D.whiteTexture);
            var previous = RenderTexture.active;
            try
            {
                using (var cmd = new CommandBuffer { name = "ParticleMaterialSnapshot" })
                {
                    cmd.SetRenderTarget(target);
                    cmd.ClearRenderTarget(true, true, background);
                    // A camera at z = -1 looking along +Z at the quad, which fills the view
                    // (view space looks down -Z, as Camera.worldToCameraMatrix).
                    var view = Matrix4x4.Scale(new Vector3(1f, 1f, -1f)) * Matrix4x4.Translate(new Vector3(0f, 0f, 1f));
                    var projection = Matrix4x4.Ortho(-0.5f, 0.5f, -0.5f, 0.5f, 0.01f, 10f);
                    cmd.SetViewProjectionMatrices(view, projection);
                    cmd.DrawMesh(quad, Matrix4x4.identity, material, 0, pass);
                    Graphics.ExecuteCommandBuffer(cmd);
                }
                RenderTexture.active = target;
                var readable = new Texture2D(size, size, TextureFormat.RGBAHalf, false, true);
                readable.ReadPixels(new Rect(0, 0, size, size), 0, 0, false);
                readable.Apply(false, false);
                var pixels = readable.GetPixels();
                UnityEngine.Object.Destroy(readable);
                return pixels;
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(target);
                Shader.SetGlobalTexture(CameraDepthTexture, savedDepth);
            }
        }

        internal static Color[] Read(Texture source, int size)
        {
            var target = RenderTexture.GetTemporary(size, size, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            var previous = RenderTexture.active;
            try
            {
                Graphics.Blit(source, target);
                RenderTexture.active = target;
                var readable = new Texture2D(size, size, TextureFormat.RGBA32, false, true);
                readable.ReadPixels(new Rect(0, 0, size, size), 0, 0, false);
                readable.Apply(false, false);
                var pixels = readable.GetPixels();
                UnityEngine.Object.Destroy(readable);
                return pixels;
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(target);
            }
        }
    }
}
