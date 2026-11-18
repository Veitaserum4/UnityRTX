using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;

namespace UnityRemix
{
    /// <summary>
    /// Fake light shafts (an additive cone or box a game draws for god rays, GameProfile.IsLightShaft)
    /// drawn as glowing air (remixapi_DrawVolumeShaftsEXT) instead of as their sheets: brighter seen
    /// through more of them, cut softly by the walls they meet. The mesh is fitted once to a tapered
    /// box along its longest side, from its brighter (else narrower) end; its vertex colours' fade
    /// along that side is the shaft's. The mesh itself is then not path-traced.
    /// </summary>
    internal static class LightShafts
    {
        public static ConfigEntry<bool> Enabled;
        public static ConfigEntry<float> Brightness;
        public static ConfigEntry<float> Softness;

        // A fitted mesh, in its object space: the near cap's centre, the axis to the far cap's,
        // the half extents across it (u, v) at both caps, the vertex brightness at both.
        private sealed class Fit
        {
            public Vector3 origin, axis, u, v;
            public float nearU, nearV, farU, farV, nearBright, farBright;
        }

        private static readonly Dictionary<int, Fit> fits = new Dictionary<int, Fit>();
        private static readonly Dictionary<int, bool> replaced = new Dictionary<int, bool>();
        private static readonly List<Renderer> shafts = new List<Renderer>();
        private static ManualLogSource log;

        public static void Bind(ConfigFile config, ManualLogSource logger)
        {
            log = logger;
            Enabled = config.Bind("Rendering", "LightShaftVolumes", true,
                "Draw the fake light shafts a game's profile names (additive cones and boxes under windows and lamps) " +
                "as glowing air: brighter seen through more of them, cut softly by walls. Takes effect on the next level load.");
            Brightness = config.Bind("Rendering", "LightShaftBrightness", 1f,
                "Light shaft volumes' brightness, 1 matching the game's sheet seen side-on.");
            Softness = config.Bind("Rendering", "LightShaftSoftness", 0.5f,
                "The part of a light shaft's half width over which it fades out at its sides (0 to 1).");
        }

        /// <summary>
        /// Whether the renderer is a light shaft drawn as a volume (not path-traced as a mesh). Asked
        /// as renderers are met; a shaft is then drawn each frame by Capture.
        /// </summary>
        public static bool Replaces(Renderer renderer)
        {
            if (renderer == null || Enabled == null || !Enabled.Value || RemixAPI.DrawVolumeShaftsEXTFunc == null ||
                !(renderer is MeshRenderer))
                return false;
            int id = renderer.GetInstanceID();
            if (replaced.TryGetValue(id, out bool known))
                return known;
            bool yes = false;
            try
            {
                if (Games.GameProfiles.IsLightShaft(renderer))
                {
                    var filter = renderer.GetComponent<MeshFilter>();
                    yes = filter != null && GetFit(filter.sharedMesh) != null &&
                          RemixMaterialManager.TryGetAdditiveGlow(renderer.sharedMaterial, out _);
                }
            }
            catch (Exception e)
            {
                log?.LogWarning($"[LightShafts] '{renderer.name}': {e.Message}");
            }
            replaced[id] = yes;
            if (yes)
            {
                shafts.Add(renderer);
                if (VerboseLog.On)
                    log?.LogInfo($"[LightShafts] '{renderer.name}' ({renderer.sharedMaterial.name}) drawn as a volume");
            }
            return yes;
        }

        public static void Reset()
        {
            shafts.Clear();
            replaced.Clear();
            fits.Clear();
        }

        /// <summary>This frame's shafts, into the frame state (main thread).</summary>
        public static void Capture(RemixFrameCapture.FrameState state)
        {
            state.shafts.Clear();
            for (int i = shafts.Count - 1; i >= 0; i--)
            {
                var r = shafts[i];
                if (r == null)
                {
                    shafts.RemoveAt(i);
                    continue;
                }
                if (!r.enabled || !r.gameObject.activeInHierarchy || UnityCompat.IsForceRenderingOff(r))
                    continue;
                var filter = r.GetComponent<MeshFilter>();
                var fit = filter != null ? GetFit(filter.sharedMesh) : null;
                if (fit == null || !RemixMaterialManager.TryGetAdditiveGlow(r.sharedMaterial, out Color glow))
                    continue;
                state.shafts.Add(ToRemix(fit, r.transform.localToWorldMatrix, glow, r.sharedMaterial));
            }
        }

        private static RemixAPI.remixapi_VolumeShaftEXT ToRemix(Fit fit, Matrix4x4 m, Color glow, Material material)
        {
            Vector3 origin = m.MultiplyPoint3x4(fit.origin);
            Vector3 axis = m.MultiplyVector(fit.axis);
            Vector3 u = m.MultiplyVector(fit.u * fit.nearU), v = m.MultiplyVector(fit.v * fit.nearV);
            float scaleU = fit.farU / fit.nearU, scaleV = fit.farV / fit.nearV;
            float softness = Mathf.Clamp01(Softness.Value);
            // What the game adds through the sheet seen side-on: the glow at each face drawn (both
            // without culling), over the thickness seen through, the sides' fade made up for.
            float faces = material.HasProperty("_CullMode") && Mathf.RoundToInt(material.GetFloat("_CullMode")) == 0 ? 2f : 1f;
            float thickness = 0.5f * (u.magnitude * (1 + scaleU) + v.magnitude * (1 + scaleV));
            float scale = faces * fit.nearBright / Mathf.Max(thickness, 1e-3f) / (1f - 0.5f * softness) * Brightness.Value;
            if (RemixMaterialManager.EmissionInLightUnits == null || RemixMaterialManager.EmissionInLightUnits.Value)
                scale *= RemixMaterialManager.UnlitRadianceScale;
            Color linear = QualitySettings.activeColorSpace == ColorSpace.Linear ? glow : glow.linear;
            return new RemixAPI.remixapi_VolumeShaftEXT
            {
                sType = RemixAPI.REMIXAPI_STRUCT_TYPE_VOLUME_SHAFT_EXT,
                origin = ToRemix(origin),
                axis = ToRemix(axis),
                halfU = ToRemix(u),
                halfV = ToRemix(v),
                farScaleU = scaleU,
                farScaleV = scaleV,
                radiance = new RemixAPI.remixapi_Float3D(linear.r * scale, linear.g * scale, linear.b * scale),
                farFade = fit.nearBright > 0 ? fit.farBright / fit.nearBright : 1f,
                softness = softness,
                shape = RemixAPI.REMIXAPI_VOLUME_SHAFT_BOX_EXT
            };
        }

        // Unity Y-up to Remix Z-up, as lights are.
        private static RemixAPI.remixapi_Float3D ToRemix(Vector3 p) => new RemixAPI.remixapi_Float3D(p.x, p.z, p.y);

        private static Fit GetFit(Mesh mesh)
        {
            if (mesh == null)
                return null;
            int id = mesh.GetInstanceID();
            if (fits.TryGetValue(id, out var cached))
                return cached;
            Fit fit = null;
            if (mesh.isReadable)
            {
                var vertices = mesh.vertices;
                var colors = mesh.colors32;
                fit = FitShaft(vertices, colors != null && colors.Length == vertices.Length ? colors : null);
            }
            fits[id] = fit;
            return fit;
        }

        // Along the longest side of the mesh's bounds: the vertices within a tenth of each end are its caps.
        private static Fit FitShaft(Vector3[] vertices, Color32[] colors)
        {
            if (vertices == null || vertices.Length < 4)
                return null;
            Vector3 min = vertices[0], max = vertices[0];
            foreach (var p in vertices)
            {
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
            }
            Vector3 size = max - min;
            int a = size.x >= size.y && size.x >= size.z ? 0 : size.y >= size.z ? 1 : 2;
            int b = (a + 1) % 3, c = (a + 2) % 3;
            float span = size[a];
            if (span <= 1e-4f)
                return null;
            var ends = new End[2];
            for (int e = 0; e < 2; e++)
                ends[e] = new End { min = new Vector2(float.MaxValue, float.MaxValue), max = new Vector2(float.MinValue, float.MinValue) };
            for (int i = 0; i < vertices.Length; i++)
            {
                var p = vertices[i];
                float t = (p[a] - min[a]) / span;
                int e = t <= 0.1f ? 0 : t >= 0.9f ? 1 : -1;
                if (e < 0)
                    continue;
                ends[e].min = Vector2.Min(ends[e].min, new Vector2(p[b], p[c]));
                ends[e].max = Vector2.Max(ends[e].max, new Vector2(p[b], p[c]));
                ends[e].bright += colors != null ? Mathf.Max(colors[i].r, Mathf.Max(colors[i].g, colors[i].b)) / 255f : 1f;
                ends[e].count++;
            }
            if (ends[0].count == 0 || ends[1].count == 0)
                return null;
            for (int e = 0; e < 2; e++)
                ends[e].bright /= ends[e].count;
            float width0 = (ends[0].max - ends[0].min).sqrMagnitude, width1 = (ends[1].max - ends[1].min).sqrMagnitude;
            // The near cap: the brighter end, or the narrower (a lamp's) when they glow alike.
            int near = Mathf.Abs(ends[0].bright - ends[1].bright) > 0.05f ? (ends[0].bright > ends[1].bright ? 0 : 1)
                                                                         : (width0 <= width1 ? 0 : 1);
            int far = 1 - near;
            Vector3 Centre(int e)
            {
                var p = Vector3.zero;
                p[a] = e == 0 ? min[a] : max[a];
                p[b] = (ends[e].min.x + ends[e].max.x) * 0.5f;
                p[c] = (ends[e].min.y + ends[e].max.y) * 0.5f;
                return p;
            }
            var fit = new Fit { origin = Centre(near) };
            fit.axis = Centre(far) - fit.origin;
            fit.u = Vector3.zero;
            fit.u[b] = 1;
            fit.v = Vector3.zero;
            fit.v[c] = 1;
            Vector2 nearHalf = (ends[near].max - ends[near].min) * 0.5f, farHalf = (ends[far].max - ends[far].min) * 0.5f;
            // A cap that is a point (a cone's apex) keeps a sliver of the other's width.
            fit.nearU = Mathf.Max(nearHalf.x, farHalf.x * 0.01f, 1e-4f);
            fit.nearV = Mathf.Max(nearHalf.y, farHalf.y * 0.01f, 1e-4f);
            fit.farU = Mathf.Max(farHalf.x, nearHalf.x * 0.01f, 1e-4f);
            fit.farV = Mathf.Max(farHalf.y, nearHalf.y * 0.01f, 1e-4f);
            fit.nearBright = ends[near].bright;
            fit.farBright = ends[far].bright;
            return fit;
        }

        private struct End
        {
            public Vector2 min, max;
            public float bright;
            public int count;
        }

        // Render thread: the frame's shafts to OpenRemix (none clears last frame's).
        private static RemixAPI.remixapi_VolumeShaftEXT[] buffer = new RemixAPI.remixapi_VolumeShaftEXT[64];

        public static void Submit(List<RemixAPI.remixapi_VolumeShaftEXT> frameShafts)
        {
            var draw = RemixAPI.DrawVolumeShaftsEXTFunc;
            if (draw == null || frameShafts == null)
                return;
            int count = frameShafts.Count;
            if (count == 0)
                return; // none drawn this frame: OpenRemix clears them at Present
            if (buffer.Length < count)
                buffer = new RemixAPI.remixapi_VolumeShaftEXT[Mathf.NextPowerOfTwo(count)];
            frameShafts.CopyTo(buffer);
            var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            try { draw(handle.AddrOfPinnedObject(), (uint)count); }
            finally { handle.Free(); }
        }
    }
}
