using System;
using UnityEngine;
using BlendMode = UnityEngine.Rendering.BlendMode;

namespace UnityRemix
{
    // How Unity blends a particle, trail or line material over what is behind it.
    internal enum ParticleBlendKind
    {
        Alpha,          // SrcAlpha, OneMinusSrcAlpha (or premultiplied by the shader itself)
        Premultiplied,  // One, OneMinusSrcAlpha on a premultiplied texture
        Additive,       // (SrcAlpha or One), One
        Multiply,       // DstColor, Zero: darkens by its colour
        MultiplyDouble, // DstColor, SrcColor: 2 x its colour (grey is no change)
    }

    // A particle material as OpenRemix's alpha-over blending shows it. Its draws' vertex colours
    // are the particles' own colours (Vertex), its material colour the material's tint
    // (MaterialColor); the texture is converted to match (UploadParticleTexture). An additive
    // particle adds light rather than covering: its brightness is its coverage, its hue at full
    // brightness its colour (and emission).
    internal struct ParticleLook
    {
        public ParticleBlendKind kind;
        public Color tint;       // the material colour (Additive: its hue, the brightest channel 1)
        public float alphaScale; // vertex alpha scale (Additive: what a white texel and vertex add)
        public bool srcAlpha;    // Additive: alpha weighs what it adds (SrcAlpha, One)

        public static readonly ParticleLook Plain = new ParticleLook
        { kind = ParticleBlendKind.Alpha, tint = Color.white, alphaScale = 1f, srcAlpha = true };

        public bool IsAdditive => kind == ParticleBlendKind.Additive;
        public bool IsMultiply => kind == ParticleBlendKind.Multiply || kind == ParticleBlendKind.MultiplyDouble;

        // The material's colour in Remix (gamma, as Unity colours are): a multiply darkens only.
        public Color MaterialColor => IsMultiply ? Color.black : new Color(tint.r, tint.g, tint.b, 1f);

        // snapshot: the material is replaced by Unity's drawing of it, its tint included.
        public static ParticleLook Of(Material mat, bool snapshot = false)
        {
            var look = Plain;
            if (mat == null)
                return look;
            look.kind = KindOf(mat, out look.srcAlpha);
            if (look.IsMultiply)
                return look;

            Color t = Color.white;
            float scale = 1f;
            if (!snapshot)
            {
                string shader = mat.shader != null ? mat.shader.name : "";
                // The legacy particle shaders: 2 x _TintColor x vertex x texel (grey 0.5 is none).
                if (mat.HasProperty("_TintColor") && shader.IndexOf("Particles/", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    shader.IndexOf("Standard", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    t = mat.GetColor("_TintColor");
                    scale = 2f;
                }
                else if (shader.StartsWith("Mobile/", StringComparison.Ordinal))
                    t = Color.white; // no tint
                else if (mat.HasProperty("_Color"))
                    t = mat.GetColor("_Color");
                else if (mat.HasProperty("_BaseColor"))
                    t = mat.GetColor("_BaseColor");
                if (mat.HasProperty("_Opacity"))
                    t.a *= mat.GetFloat("_Opacity");
            }

            if (look.IsAdditive)
            {
                float a = look.srcAlpha ? Mathf.Clamp01(t.a * scale) : 1f;
                float r = t.r * scale * a, g = t.g * scale * a, b = t.b * scale * a;
                float m = Mathf.Max(r, Mathf.Max(g, b));
                look.tint = m > 1e-4f ? new Color(r / m, g / m, b / m, 1f) : Color.white;
                look.alphaScale = m;
            }
            else
            {
                look.tint = new Color(Mathf.Clamp01(t.r * scale), Mathf.Clamp01(t.g * scale), Mathf.Clamp01(t.b * scale), 1f);
                look.alphaScale = t.a * scale;
            }
            return look;
        }

        public static ParticleBlendKind KindOf(Material mat) => KindOf(mat, out _);

        private static ParticleBlendKind KindOf(Material mat, out bool srcAlpha)
        {
            srcAlpha = true;
            string s = mat.shader != null ? mat.shader.name : "";
            // Before Multiply: "Alpha Blended Premultiply" contains it, and as a multiply its
            // particles (Unity's Default-Particle) were drawn black.
            if (s.IndexOf("Premultipl", StringComparison.OrdinalIgnoreCase) >= 0)
                return ParticleBlendKind.Premultiplied;
            if (s.IndexOf("Multiply", StringComparison.OrdinalIgnoreCase) >= 0)
                return s.IndexOf("Double", StringComparison.OrdinalIgnoreCase) >= 0 ? ParticleBlendKind.MultiplyDouble : ParticleBlendKind.Multiply;
            if (s.IndexOf("Additive", StringComparison.OrdinalIgnoreCase) >= 0)
                return ParticleBlendKind.Additive;
            // Shaders whose blend is set by _SrcBlend / _DstBlend (others, the psx ones among
            // them, keep stale values there).
            bool blendProperties = s == "Standard" || s.StartsWith("Particles/Standard", StringComparison.Ordinal) ||
                                   Games.GameProfiles.ReadsBlendProperties(s);
            if (blendProperties && mat.HasProperty("_SrcBlend") && mat.HasProperty("_DstBlend"))
            {
                int src = Mathf.RoundToInt(mat.GetFloat("_SrcBlend")), dst = Mathf.RoundToInt(mat.GetFloat("_DstBlend"));
                if (dst == (int)BlendMode.One)
                {
                    srcAlpha = src != (int)BlendMode.One;
                    return ParticleBlendKind.Additive;
                }
                if (src == (int)BlendMode.DstColor && dst == (int)BlendMode.SrcColor)
                    return ParticleBlendKind.MultiplyDouble;
                if ((src == (int)BlendMode.DstColor && dst == (int)BlendMode.Zero) ||
                    (src == (int)BlendMode.Zero && dst == (int)BlendMode.SrcColor))
                    return ParticleBlendKind.Multiply;
            }
            return ParticleBlendKind.Alpha;
        }

        // A particle's vertex colour (Unity's: its colour, alpha and fade) as drawn in Remix:
        // texel x vertex x material colour.
        public Color32 Vertex(Color32 c)
        {
            switch (kind)
            {
                case ParticleBlendKind.Additive:
                {
                    int m = Math.Max(c.r, Math.Max(c.g, c.b));
                    float bright = m / 255f * (srcAlpha ? c.a / 255f : 1f) * alphaScale;
                    if (m == 0 || bright <= 0f)
                        return new Color32(255, 255, 255, 0);
                    return new Color32((byte)(c.r * 255 / m), (byte)(c.g * 255 / m), (byte)(c.b * 255 / m),
                                       (byte)Mathf.Clamp(Mathf.RoundToInt(bright * 255f), 0, 255));
                }
                case ParticleBlendKind.Multiply:
                case ParticleBlendKind.MultiplyDouble:
                    // The texture's darkening (its alpha), faded by the particle's.
                    return new Color32(255, 255, 255, c.a);
                default:
                    if (alphaScale > 0.999f && alphaScale < 1.001f)
                        return c;
                    return new Color32(c.r, c.g, c.b, (byte)Mathf.Clamp(Mathf.RoundToInt(c.a * alphaScale), 0, 255));
            }
        }

        public void ApplyTo(Color32[] colors, int count)
        {
            if (colors == null)
                return;
            if (kind == ParticleBlendKind.Alpha && alphaScale > 0.999f && alphaScale < 1.001f)
                return;
            count = Math.Min(count, colors.Length);
            for (int i = 0; i < count; i++)
                colors[i] = Vertex(colors[i]);
        }

        // An emissive particle's emission colour (emission is not tinted by vertex colours): the
        // particles' colour, tinted; an additive one's at full brightness (its brightness is its coverage).
        public Color EmissionColor(Color particleColor)
        {
            Color c = new Color(particleColor.r * tint.r, particleColor.g * tint.g, particleColor.b * tint.b, 1f);
            if (IsAdditive)
            {
                float m = Mathf.Max(c.r, Mathf.Max(c.g, c.b));
                c = m > 1e-3f ? new Color(c.r / m, c.g / m, c.b / m, 1f) : new Color(tint.r, tint.g, tint.b, 1f);
            }
            return c;
        }
    }
}
