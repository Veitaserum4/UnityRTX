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
        public static ConfigEntry<bool> Shadows;

        // A fitted mesh, in its object space: the near cap's centre, the axis to the far cap's,
        // the half extents across it (u, v) at both caps, the vertex brightness at both.
        private sealed class Fit
        {
            public Vector3 origin, axis, u, v;
            public float nearU, nearV, farU, farV, nearBright, farBright;
            public string source;
        }

        private static readonly Dictionary<int, Fit> fits = new Dictionary<int, Fit>();
        private static readonly Dictionary<int, (Vector3[] positions, int[][] indices)> gpuMeshes =
            new Dictionary<int, (Vector3[], int[][])>();
        private static readonly Dictionary<int, bool> replaced = new Dictionary<int, bool>();
        private static readonly List<Renderer> shafts = new List<Renderer>();
        private static ManualLogSource log;

        public static void Bind(ConfigFile config, ManualLogSource logger)
        {
            log = logger;
            Enabled = config.Bind("Rendering", "LightShaftVolumes", true,
                "Draw the fake light shafts a game's profile names (additive cones and boxes under windows and lamps) " +
                "as glowing air: brighter seen through more of them, cut softly by walls. Off: the game's sheets.");
            Brightness = config.Bind("Rendering", "LightShaftBrightness", 2f,
                "Light shaft volumes' strength, 1 matching the game's sheet seen side-on. Long beams (searchlights, " +
                "lighthouses) keep the game's strength.");
            Shadows = config.Bind("Rendering", "LightShaftShadows", true,
                "What stands in a light shaft casts a shadow down it, from the window or lamp it starts at (a shadow ray per sample).");
            Softness = config.Bind("Rendering", "LightShaftSoftness", 0.5f,
                "The part of a light shaft's half width over which it fades out at its sides (0 to 1).");
        }

        /// <summary>Shafts are drawn as volumes (and their meshes not path-traced): on, and OpenRemix can.</summary>
        public static bool Active => Enabled != null && Enabled.Value && RemixAPI.DrawVolumeShaftsEXTFunc != null;

        /// <summary>
        /// Whether the renderer is a light shaft OpenRemix can draw as a volume (while Active, its mesh
        /// is not path-traced). Decided once per renderer, as renderers are met; a shaft is then drawn
        /// each frame by Capture.
        /// </summary>
        public static bool IsShaft(Renderer renderer)
        {
            if (renderer == null || RemixAPI.DrawVolumeShaftsEXTFunc == null || !(renderer is MeshRenderer))
                return false;
            int id = renderer.GetInstanceID();
            if (replaced.TryGetValue(id, out bool known))
                return known;
            bool yes = false;
            try
            {
                if (Games.GameProfiles.IsLightShaft(renderer))
                    yes = GetFit(renderer, out _) != null && RemixMaterialManager.TryGetAdditiveGlow(renderer.sharedMaterial, out _);
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
                {
                    var fit = GetFit(renderer, out bool world);
                    log?.LogInfo($"[LightShafts] '{renderer.name}' ({renderer.sharedMaterial.name}) drawn as a volume: " +
                                 $"{fit.source}{(world ? " (world space)" : "")}, length {fit.axis.magnitude:F2}, near {fit.nearU:F2}x{fit.nearV:F2}, " +
                                 $"far {fit.farU:F2}x{fit.farV:F2}, brightness {fit.nearBright:F2} to {fit.farBright:F2}" +
                                 (IsBeam(fit) ? ", a beam: the game's strength" : ""));
                }
            }
            return yes;
        }

        public static void Reset()
        {
            shafts.Clear();
            replaced.Clear();
            fits.Clear();
            gpuMeshes.Clear();
        }

        /// <summary>This frame's shafts, into the frame state (main thread).</summary>
        public static void Capture(RemixFrameCapture.FrameState state)
        {
            state.shafts.Clear();
            if (!Active)
                return;
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
                var fit = GetFit(r, out bool world);
                if (fit == null || !RemixMaterialManager.TryGetAdditiveGlow(r.sharedMaterial, out Color glow))
                    continue;
                state.shafts.Add(ToRemix(fit, world ? Matrix4x4.identity : r.transform.localToWorldMatrix, glow, r.sharedMaterial));
            }
            if (VerboseLog.On && state.frameCount - lastReport >= 600)
            {
                lastReport = state.frameCount;
                string first = "";
                if (state.shafts.Count > 0)
                {
                    var s = state.shafts[0];
                    first = $"; first: origin ({s.origin.x:F1},{s.origin.y:F1},{s.origin.z:F1}) axis ({s.axis.x:F1},{s.axis.y:F1},{s.axis.z:F1}) " +
                            $"half u ({s.halfU.x:F2},{s.halfU.y:F2},{s.halfU.z:F2}) v ({s.halfV.x:F2},{s.halfV.y:F2},{s.halfV.z:F2}) " +
                            $"far scale {s.farScaleU:F2}/{s.farScaleV:F2} radiance ({s.radiance.x:F4},{s.radiance.y:F4},{s.radiance.z:F4}) fade {s.farFade:F2}";
                }
                log?.LogInfo($"[LightShafts] {shafts.Count} known, {state.shafts.Count} drawn this frame, last submit {lastResult}{first}");
            }
        }

        private static int lastReport = -100000;
        private static volatile string lastResult = "none";

        private static RemixAPI.remixapi_VolumeShaftEXT ToRemix(Fit fit, Matrix4x4 m, Color glow, Material material)
        {
            Vector3 origin = m.MultiplyPoint3x4(fit.origin);
            Vector3 axis = m.MultiplyVector(fit.axis);
            Vector3 u = m.MultiplyVector(fit.u * fit.nearU), v = m.MultiplyVector(fit.v * fit.nearV);
            float scaleU = fit.farU / fit.nearU, scaleV = fit.farV / fit.nearV;
            float softness = Mathf.Clamp01(Softness.Value);
            // What the game adds through the sheet seen side-on: the glow at each face drawn (both
            // without culling), over the thickness seen through at the near cap (OpenRemix thins it
            // as the shaft widens, so a crossing glows alike all along), the sides' fade made up for.
            float faces = material.HasProperty("_CullMode") && Mathf.RoundToInt(material.GetFloat("_CullMode")) == 0 ? 2f : 1f;
            float thickness = u.magnitude + v.magnitude;
            // A beam (a searchlight's, a lighthouse's: far longer than its source is wide) keeps the
            // game's strength. Boosted like a window's shaft, it reads as a solid slab against the
            // dark it usually crosses; the strength is for room-sized shafts.
            float strength = IsBeam(fit) ? 1f : Brightness.Value;
            float scale = faces * fit.nearBright / Mathf.Max(thickness, 1e-3f) / (1f - 0.5f * softness) * strength;
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
                shape = RemixAPI.REMIXAPI_VOLUME_SHAFT_BOX_EXT,
                flags = Shadows.Value ? RemixAPI.REMIXAPI_VOLUME_SHAFT_SHADOWED_EXT : 0u
            };
        }

        // Longer than 8 times its near cap's width (u + v half extents): a lighthouse's beam is about
        // 24, the shafts under windows and lamps 1 to 3.
        private static bool IsBeam(Fit fit) => fit.axis.magnitude > 8f * (fit.nearU + fit.nearV);

        // Unity Y-up to Remix Z-up, as lights are.
        private static RemixAPI.remixapi_Float3D ToRemix(Vector3 p) => new RemixAPI.remixapi_Float3D(p.x, p.z, p.y);

        // The renderer's own geometry: the mesh the game rewrote for its batching
        // (GameProfile.GetOriginalGeometry), else, in a static batch, its slice of the combined mesh
        // (world space: `world`), else its mesh. Fitted once per mesh and slice.
        private static Fit GetFit(Renderer renderer, out bool world)
        {
            world = false;
            var filter = renderer.GetComponent<MeshFilter>();
            var mesh = filter != null ? filter.sharedMesh : null;
            int subMesh = -1;
            if (Games.GameProfiles.GetOriginalGeometry(renderer, out var original, out _))
                mesh = original;
            else if (renderer.isPartOfStaticBatch && renderer is MeshRenderer meshRenderer)
            {
                subMesh = meshRenderer.subMeshStartIndex;
                world = true;
            }
            if (mesh == null)
                return null;
            int id = mesh.GetInstanceID() * 397 ^ subMesh;
            if (fits.TryGetValue(id, out var cached))
                return cached;
            Fit fit = null;
            Vector3[] vertices = null;
            Color32[] colors = null;
            int[][] gpuIndices = null;
            if (subMesh >= mesh.subMeshCount)
                vertices = null;
            else if (mesh.isReadable)
            {
                vertices = mesh.vertices;
                colors = mesh.colors32;
                if (colors != null && colors.Length != vertices.Length)
                    colors = null;
            }
            else
            {
                // Not readable (a static batch's combined mesh): positions and indices read back
                // from the GPU, once per mesh for all its slices. No vertex colours this way.
                int meshId = mesh.GetInstanceID();
                if (!gpuMeshes.TryGetValue(meshId, out var read))
                {
                    read = NativeMeshReader.ReadMeshFromGPU(mesh, out var positions, out _, out _, out var subIndices)
                        ? (positions, subIndices) : (null, null);
                    gpuMeshes[meshId] = read;
                }
                vertices = read.positions;
                gpuIndices = read.indices;
                if (vertices != null)
                {
                    try { colors = ReadGpuColors(mesh, vertices.Length); }
                    catch (Exception) { colors = null; } // Unity before 2019.3: no vertex attribute queries
                }
                if (vertices != null && subMesh < 0)
                    subMesh = -2; // the whole mesh, from its index lists
            }
            if (vertices != null)
            {
                if (subMesh >= 0 || subMesh == -2)
                {
                    // Only the slice's vertices (the batch is the whole level's).
                    IEnumerable<int> indices = subMesh == -2
                        ? (IEnumerable<int>)System.Linq.Enumerable.SelectMany(gpuIndices ?? new int[0][], x => x)
                        : gpuIndices != null ? (subMesh < gpuIndices.Length ? gpuIndices[subMesh] : new int[0])
                        : mesh.GetIndices(subMesh);
                    var used = new HashSet<int>(indices);
                    var sliceVertices = new List<Vector3>(used.Count);
                    var sliceColors = colors != null ? new List<Color32>(used.Count) : null;
                    foreach (int i in used)
                    {
                        if (i < 0 || i >= vertices.Length)
                            continue;
                        sliceVertices.Add(vertices[i]);
                        sliceColors?.Add(colors[i]);
                    }
                    vertices = sliceVertices.ToArray();
                    colors = sliceColors?.ToArray();
                }
                fit = FitShaft(vertices, colors);
                if (fit != null)
                    fit.source = $"mesh '{mesh.name}'{(subMesh >= 0 ? $" submesh {subMesh}" : "")}{(gpuIndices != null ? " (read from the GPU)" : "")}, {vertices.Length} vertices";
            }
            fits[id] = fit;
            return fit;
        }

        // A non-readable mesh's vertex colours from its GPU vertex buffer (8-bit or float RGBA),
        // its fade along the shaft. Null without them or in another format.
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static Color32[] ReadGpuColors(Mesh mesh, int count)
        {
#if NET472
            return null; // no GPU vertex buffer queries before Unity 2021.2
#else
            var color = UnityEngine.Rendering.VertexAttribute.Color;
            if (!mesh.HasVertexAttribute(color))
                return null;
            int stream = mesh.GetVertexAttributeStream(color), offset = mesh.GetVertexAttributeOffset(color);
            var format = mesh.GetVertexAttributeFormat(color);
            int stride = mesh.GetVertexBufferStride(stream);
            if (!NativeMeshReader.ReadVertexBuffer(mesh, stream, out var bytes) || bytes == null || stride <= 0)
                return null;
            int n = Math.Min(count, bytes.Length / stride);
            var colors = new Color32[count];
            for (int i = 0; i < n; i++)
            {
                int at = i * stride + offset;
                if (format == UnityEngine.Rendering.VertexAttributeFormat.UNorm8 && at + 4 <= bytes.Length)
                    colors[i] = new Color32(bytes[at], bytes[at + 1], bytes[at + 2], bytes[at + 3]);
                else if (format == UnityEngine.Rendering.VertexAttributeFormat.Float32 && at + 16 <= bytes.Length)
                    colors[i] = new Color(BitConverter.ToSingle(bytes, at), BitConverter.ToSingle(bytes, at + 4),
                                          BitConverter.ToSingle(bytes, at + 8), BitConverter.ToSingle(bytes, at + 12));
                else
                    return null;
            }
            return colors;
#endif
        }

        private static float VertexBrightness(Color32 c) => Mathf.Max(c.r, Mathf.Max(c.g, c.b)) / 255f;

        // A shaft fading along its length (vertex colours bright at one end, dark at the other):
        // from the bright vertices' centre to the dark ones', its caps their extents across that
        // axis. Its longest side may well be across it (a lamp's wide, short cone; fins about an axis).
        private static Fit FitByFade(Vector3[] vertices, Color32[] colors, float threshold)
        {
            Vector3 brightMin = Vector3.positiveInfinity, brightMax = Vector3.negativeInfinity;
            Vector3 darkMin = Vector3.positiveInfinity, darkMax = Vector3.negativeInfinity;
            float brightSum = 0, darkSum = 0;
            int brightCount = 0, darkCount = 0;
            for (int i = 0; i < vertices.Length; i++)
            {
                float b = VertexBrightness(colors[i]);
                if (b >= threshold)
                {
                    brightMin = Vector3.Min(brightMin, vertices[i]);
                    brightMax = Vector3.Max(brightMax, vertices[i]);
                    brightSum += b;
                    brightCount++;
                }
                else
                {
                    darkMin = Vector3.Min(darkMin, vertices[i]);
                    darkMax = Vector3.Max(darkMax, vertices[i]);
                    darkSum += b;
                    darkCount++;
                }
            }
            if (brightCount == 0 || darkCount == 0)
                return null;
            Vector3 near = (brightMin + brightMax) * 0.5f, far = (darkMin + darkMax) * 0.5f;
            Vector3 axis = far - near;
            if (axis.magnitude <= 1e-4f)
                return null;
            Vector3 along = axis.normalized;
            // Across: the coordinate axis least along the shaft, made perpendicular to it.
            Vector3 seed = Mathf.Abs(along.x) <= Mathf.Abs(along.y) && Mathf.Abs(along.x) <= Mathf.Abs(along.z) ? Vector3.right
                         : Mathf.Abs(along.y) <= Mathf.Abs(along.z) ? Vector3.up : Vector3.forward;
            Vector3 u = (seed - Vector3.Dot(seed, along) * along).normalized, v = Vector3.Cross(along, u);
            float nearU = 0, nearV = 0, farU = 0, farV = 0;
            // How far each end reaches along the axis past its centre: a window's shaft starts on the
            // window's plane and ends on the floor's, neither square to it.
            float back = 0, ahead = 0;
            for (int i = 0; i < vertices.Length; i++)
            {
                bool bright = VertexBrightness(colors[i]) >= threshold;
                Vector3 d = vertices[i] - (bright ? near : far);
                float x = Mathf.Abs(Vector3.Dot(d, u)), y = Mathf.Abs(Vector3.Dot(d, v));
                if (bright)
                {
                    nearU = Mathf.Max(nearU, x);
                    nearV = Mathf.Max(nearV, y);
                    back = Mathf.Max(back, -Vector3.Dot(d, along));
                }
                else
                {
                    farU = Mathf.Max(farU, x);
                    farV = Mathf.Max(farV, y);
                    ahead = Mathf.Max(ahead, Vector3.Dot(d, along));
                }
            }
            // The caps are square to the axis, so they are moved out to the furthest corner of each
            // end, the taper carried on to them: square at the ends' centres, they cut the shaft short
            // by a window on one side and above the floor on the other. What they now reach past
            // the window's wall and into the floor is hidden by them (the glow is seen in front of
            // the first surface).
            float length = axis.magnitude;
            Vector2 Half(Vector2 a, Vector2 b, float t) => Vector2.Max(a + (b - a) * t, 0.01f * Vector2.Max(a, b));
            Vector2 nearHalf = new Vector2(nearU, nearV), farHalf = new Vector2(farU, farV);
            Vector2 start = Half(nearHalf, farHalf, -back / length), end = Half(nearHalf, farHalf, 1f + ahead / length);
            var fit = new Fit { origin = near - along * back, axis = axis + along * (back + ahead), u = u, v = v,
                                nearBright = brightSum / brightCount, farBright = darkSum / darkCount };
            SetCaps(fit, start, end);
            return fit;
        }

        // The caps' half extents. A sheet (a cap flat across one side) is as deep as it is wide:
        // seen from the side the game drew it to be seen from, the volume then glows as it did.
        // A cap that is a point (a cone's apex) keeps a sliver of the other's width.
        private static void SetCaps(Fit fit, Vector2 nearHalf, Vector2 farHalf)
        {
            Vector2 Thicken(Vector2 h) =>
                h.x < 0.1f * h.y ? new Vector2(h.y, h.y) : h.y < 0.1f * h.x ? new Vector2(h.x, h.x) : h;
            nearHalf = Thicken(nearHalf);
            farHalf = Thicken(farHalf);
            fit.nearU = Mathf.Max(nearHalf.x, farHalf.x * 0.01f, 1e-4f);
            fit.nearV = Mathf.Max(nearHalf.y, farHalf.y * 0.01f, 1e-4f);
            fit.farU = Mathf.Max(farHalf.x, nearHalf.x * 0.01f, 1e-4f);
            fit.farV = Mathf.Max(farHalf.y, nearHalf.y * 0.01f, 1e-4f);
        }

        // Fading along its length: FitByFade. Else along the longest side of the mesh's bounds, the
        // vertices within a tenth of each end its caps.
        private static Fit FitShaft(Vector3[] vertices, Color32[] colors)
        {
            if (vertices == null || vertices.Length < 4)
                return null;
            if (colors != null)
            {
                float lo = 1, hi = 0;
                foreach (var colour in colors)
                {
                    float shade = VertexBrightness(colour);
                    lo = Mathf.Min(lo, shade);
                    hi = Mathf.Max(hi, shade);
                }
                if (hi - lo > 0.25f)
                {
                    var faded = FitByFade(vertices, colors, (lo + hi) * 0.5f);
                    if (faded != null)
                        return faded;
                }
            }
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
            SetCaps(fit, (ends[near].max - ends[near].min) * 0.5f, (ends[far].max - ends[far].min) * 0.5f);
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
            try { lastResult = $"{count}: {draw(handle.AddrOfPinnedObject(), (uint)count)}"; }
            finally { handle.Free(); }
        }
    }
}
