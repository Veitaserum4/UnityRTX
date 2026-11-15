using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.Rendering;

namespace UnityRemix
{
    /// <summary>
    /// Passing a Unity game's own D3D11 shaders through to OpenRemix (host passes), as SourcePT
    /// does for Source's D3D9 ones. UnityRTXHostPass.dll (native/hostpass11) hooks Unity's D3D11
    /// device; each frame a hidden helper camera, matching the game's, renders the renderers the
    /// game profile asks for (GameProfile.GetPassThroughRenderers) through a command buffer that
    /// brackets each draw with plugin events, and the hooks record those draws with their state:
    /// shaders (DXBC compiled to SPIR-V), constant buffers, vertices, textures and samplers.
    /// UnityRTX suppresses the game's own world cameras, so these draws are the only ones of
    /// those renderers Unity makes.
    /// </summary>
    public static class ShaderPassThrough
    {
        private const string Dll = "UnityRTXHostPass";
        private const int BeginEvent = 0x55520000, EndEvent = 0x5552ffff;
        private const int ScreenTag = 0xfffe; // a screen effect's draw (hostpass.h kScreenTag)

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr LoadLibraryW(string path);
        [DllImport(Dll)] private static extern int UnityRTXHostPass_Install(IntPtr device);
        [DllImport(Dll)] private static extern IntPtr UnityRTXHostPass_RenderEvent();
        [DllImport(Dll)] private static extern int UnityRTXHostPass_Report(StringBuilder buffer, int bytes);
        [DllImport(Dll)] private static extern int UnityRTXHostPass_BindRemix(IntPtr remix);
        [DllImport(Dll)] private static extern void UnityRTXHostPass_SetFrameTexture(IntPtr resource);
        [DllImport(Dll)] private static extern void UnityRTXHostPass_SetGrabTexture(IntPtr resource);
        [DllImport(Dll)] private static extern void UnityRTXHostPass_SetScreenSource(IntPtr resource, int width, int height, int frameWidth, int frameHeight);
        [DllImport(Dll)] private static extern void UnityRTXHostPass_SetProjection(float p22, float p23, int reversedZ);
        [DllImport(Dll)] private static extern void UnityRTXHostPass_SetTexture(IntPtr resource, int width, int height, int depth, int vkFormat, IntPtr rgba8);
        [DllImport(Dll)] private static extern int UnityRTXHostPass_MissingTextures([Out] IntPtr[] resources, [Out] int[] sizes, int max);
        [DllImport(Dll)] private static extern void UnityRTXHostPass_SetStains(float[] vertices, int quads, IntPtr atlas);
        [DllImport(Dll)] private static extern void UnityRTXHostPass_SetStainView(float[] rows);
        [DllImport(Dll)] private static extern int UnityRTXHostPass_Submit();
        [DllImport(Dll)] private static extern void UnityRTXHostPass_SetLayersView(int view);

        /// <summary>[Debug] PassThroughLayersView: what the walls' sheet composition reads, shown.</summary>
        public static BepInEx.Configuration.ConfigEntry<int> LayersView;
        private static bool layersViewAvailable = true;
        [DllImport(Dll)] private static extern void UnityRTXHostPass_ClearCapture();

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void GetDeviceD(IntPtr self, out IntPtr device);

        private static ManualLogSource logger;
        private static bool installed;
        private static IntPtr renderEvent;
        private static Camera helper;
        private static RenderTexture target;
        private static CommandBuffer commands;
        private static readonly List<Renderer> renderers = new List<Renderer>();
        // The profile's screen effect this frame (GameProfile.PassThroughScreenEffect), blitted from
        // screenSource, the texture the replay gives it the composed frame for.
        private static Material screenEffect;
        private static RenderTexture screenSource;
        private static IntPtr screenSourcePtr;
        private static int lastReportFrame = -100000;
        private static int lastDrawCount = -1;
        private static int reports;
        private static readonly Plane[] frustum = new Plane[6];
        private static bool captureCleared;
        private static Texture frameTexture;
        private static IntPtr frameTexturePtr;
        // Bound as _GrabTexture for the draws (their GrabPass is not drawn): the replay samples
        // the path-traced frame in its place.
        private static RenderTexture grabStandIn;
        private static IntPtr grabStandInPtr;
        private static readonly int GrabTextureId = Shader.PropertyToID("_GrabTexture");
        // Main-thread cost of Capture, for the report.
        private static readonly System.Diagnostics.Stopwatch captureTime = new System.Diagnostics.Stopwatch();
        private static int capturedFrames, culledFrames;
        private static bool bound, bindFailed;
        private static bool stainsSet;
        private static Texture stainAtlas;
        private static IntPtr stainAtlasPtr;
        private static readonly float[] stainView = new float[16];
        // Textures sent to the replay (by native pointer), and those no texture was found for.
        private static readonly HashSet<IntPtr> sentTextures = new HashSet<IntPtr>();
        private static readonly HashSet<IntPtr> unmatchedTextures = new HashSet<IntPtr>();
        private static readonly IntPtr[] missing = new IntPtr[32];
        private static readonly int[] missingSizes = new int[96]; // (width, height, depth) each
        // What was sent of each texture: re-sent when Unity updates it (a palette's LUT recomputed).
        private static readonly Dictionary<IntPtr, (Texture texture, uint version)> sentVersions =
            new Dictionary<IntPtr, (Texture, uint)>();
        // The screen effect as blitted: a copy of the game's material (its keywords and values each
        // frame) with _MainTex the stand-in source, whatever the game's material holds there.
        private static Material screenCopy;

        /// <summary>The hooks are in: the profile's pass-through renderers are replayed with their own shaders.</summary>
        public static bool Installed => installed;

        /// <summary>Hooks Unity's D3D11 device, before the game creates the shaders it passes through.</summary>
        public static void Install(ManualLogSource log)
        {
            logger = log;
            if (installed || SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D11)
                return;
            // Loaded by path (the plugin folder's): the P/Invokes then bind to it by name.
            string library = InstallLayout.NativeLibrary(Dll + ".dll");
            if (library == null || LoadLibraryW(library) == IntPtr.Zero)
            {
                log.LogInfo($"[ShaderPassThrough] {Dll}.dll not found in {InstallLayout.PluginDirectory}: shader pass-through off");
                return;
            }
            try
            {
                // Any texture's native pointer is an ID3D11Resource: ID3D11DeviceChild::GetDevice (slot 3).
                IntPtr resource = Texture2D.whiteTexture.GetNativeTexturePtr();
                if (resource == IntPtr.Zero)
                    return;
                IntPtr vtable = Marshal.ReadIntPtr(resource);
                var getDevice = Marshal.GetDelegateForFunctionPointer<GetDeviceD>(Marshal.ReadIntPtr(vtable, 3 * IntPtr.Size));
                getDevice(resource, out IntPtr device);
                if (device == IntPtr.Zero)
                    return;
                installed = UnityRTXHostPass_Install(device) != 0;
                Marshal.Release(device);
                renderEvent = UnityRTXHostPass_RenderEvent();
                log.LogInfo($"[ShaderPassThrough] D3D11 hooks {(installed ? "installed" : "failed")} (device 0x{device.ToInt64():X})");
            }
            catch (DllNotFoundException)
            {
                log.LogInfo("[ShaderPassThrough] UnityRTXHostPass.dll not found beside the game: shader pass-through off");
            }
            catch (Exception e)
            {
                log.LogWarning($"[ShaderPassThrough] install failed: {e.Message}");
            }
        }

        /// <summary>
        /// The capture hooks see the immediate context's draws only: with graphics jobs on, Unity
        /// records draws on worker threads' deferred contexts. Turns them off in the game's
        /// boot.config (backed up once as boot.config.unityrtx-backup); Unity reads it at
        /// startup, so a change takes effect from the next launch.
        /// </summary>
        public static void RequireImmediateRendering(ManualLogSource log)
        {
            string path = System.IO.Path.Combine(Application.dataPath, "boot.config");
            try
            {
                if (!System.IO.File.Exists(path))
                    return;
                var lines = System.IO.File.ReadAllLines(path);
                bool changed = false;
                for (int i = 0; i < lines.Length; i++)
                {
                    int eq = lines[i].IndexOf('=');
                    if (eq < 0)
                        continue;
                    string key = lines[i].Substring(0, eq).Trim();
                    if ((key == "gfx-enable-gfx-jobs" || key == "gfx-enable-native-gfx-jobs") && lines[i].Substring(eq + 1).Trim() != "0")
                    {
                        lines[i] = key + "=0";
                        changed = true;
                    }
                }
                if (!changed)
                    return;
                string backup = path + ".unityrtx-backup";
                if (!System.IO.File.Exists(backup))
                    System.IO.File.Copy(path, backup);
                System.IO.File.WriteAllText(path, string.Join("\n", lines) + "\n");
                log.LogWarning("[ShaderPassThrough] turned Unity's graphics jobs off in boot.config (backup: boot.config.unityrtx-backup); " +
                               "shader pass-through works from the next launch");
            }
            catch (Exception e)
            {
                log.LogWarning($"[ShaderPassThrough] could not turn graphics jobs off in {path}: {e.Message}; shader pass-through will capture nothing while they are on");
            }
        }

        /// <summary>
        /// Main thread, once a frame: renders the profile's pass-through renderers from the
        /// game camera's view, through the hooks' capture window.
        /// </summary>
        public static void Capture(Camera main, int frame)
        {
            if (!installed || main == null)
                return;
            captureTime.Start();
            try { CaptureVisible(main, frame); }
            finally { captureTime.Stop(); }
            if (frame - lastReportFrame >= 600)
                Report(frame);
        }

        // The profile's pass-through renderers the game camera sees, drawn through the capture
        // window; none in view: nothing is drawn, and the replay submits nothing.
        private static void CaptureVisible(Camera main, int frame)
        {
            renderers.Clear();
            Games.GameProfiles.GetPassThroughRenderers(main, renderers);
            GeometryUtility.CalculateFrustumPlanes(main, frustum);
            int kept = 0;
            for (int i = 0; i < renderers.Count; i++)
            {
                var r = renderers[i];
                if (r != null && r.enabled && r.gameObject.activeInHierarchy && GeometryUtility.TestPlanesAABB(frustum, r.bounds))
                    renderers[kept++] = r;
            }
            renderers.RemoveRange(kept, renderers.Count - kept);
            screenEffect = Games.GameProfiles.PassThroughScreenEffect();
            if (renderers.Count == 0 && screenEffect == null)
            {
                ++culledFrames;
                if (!captureCleared)
                {
                    UnityRTXHostPass_ClearCapture();
                    captureCleared = true;
                }
                return;
            }
            captureCleared = false;
            ++capturedFrames;
            EnsureHelper(main);
            helper.CopyFrom(main);
            helper.enabled = false;
            helper.cullingMask = 0;
            helper.clearFlags = CameraClearFlags.SolidColor;
            helper.backgroundColor = Color.clear;
            helper.targetTexture = target;

            commands.Clear();
            commands.SetGlobalTexture(GrabTextureId, grabStandIn);
            for (int i = 0; i < renderers.Count; i++)
            {
                var r = renderers[i];
                if (r == null || !r.enabled || !r.gameObject.activeInHierarchy)
                    continue;
                var materials = r.sharedMaterials;
                for (int s = 0; s < materials.Length; s++)
                {
                    if (materials[s] == null)
                        continue;
                    commands.IssuePluginEvent(renderEvent, BeginEvent | (i & 0xffff));
                    commands.DrawRenderer(r, materials[s], s, Games.GameProfiles.PassThroughShaderPass(materials[s]));
                }
            }
            if (screenEffect != null)
            {
                // Its own shaders over the whole frame: Blit sets _MainTex to the source, which
                // the replay replaces with the composed frame.
                EnsureScreenSource();
                if (screenCopy == null || screenCopy.shader != screenEffect.shader)
                    screenCopy = new Material(screenEffect) { name = screenEffect.name + " (UnityRTX)", hideFlags = HideFlags.HideAndDontSave };
                else
                    screenCopy.CopyPropertiesFromMaterial(screenEffect);
                screenCopy.shaderKeywords = screenEffect.shaderKeywords;
                screenCopy.SetTexture("_MainTex", screenSource);
                commands.IssuePluginEvent(renderEvent, BeginEvent | ScreenTag);
                commands.Blit(screenSource, new RenderTargetIdentifier(target), screenCopy);
            }
            commands.IssuePluginEvent(renderEvent, EndEvent);
            helper.Render();

            if (Bind())
                PrepareReplay();
        }

        // remix.dll's host passes, once it is loaded.
        private static bool Bind()
        {
            if (!bound && !bindFailed && installed && RemixAPI.RemixDllHandle != IntPtr.Zero)
            {
                bound = UnityRTXHostPass_BindRemix(RemixAPI.RemixDllHandle) != 0;
                bindFailed = !bound;
                logger?.LogInfo($"[ShaderPassThrough] replay {(bound ? "bound to remix.dll's host passes" : "unavailable: remix.dll has no host passes")}");
            }
            return bound;
        }

        /// <summary>
        /// Main thread: whether blood stains can be drawn over the frame (SetStains), as the game
        /// composites them, instead of as geometry.
        /// </summary>
        public static bool CanDrawStains => Bind();

        /// <summary>
        /// Main thread, when they change: the stains drawn over the frame, quads of (x, y, z, u, v)
        /// vertices (world space, atlas coordinates as Unity samples it) over <paramref name="atlas"/>,
        /// whose pixels are sent once. Null or no quads: none.
        /// </summary>
        public static void SetStains(float[] vertices, int quads, Texture atlas)
        {
            if (!Bind())
                return;
            IntPtr resource = IntPtr.Zero;
            if (atlas != null && quads > 0 && vertices != null)
            {
                // GetNativeTexturePtr waits for Unity's render thread: only when the atlas changes.
                if (atlas != stainAtlas)
                {
                    stainAtlas = atlas;
                    stainAtlasPtr = atlas.GetNativeTexturePtr();
                }
                resource = stainAtlasPtr;
                if (resource != IntPtr.Zero && !sentTextures.Contains(resource) && SendTexture(resource, atlas))
                    sentTextures.Add(resource);
            }
            bool any = resource != IntPtr.Zero;
            if (!any && !stainsSet)
                return;
            UnityRTXHostPass_SetStains(any ? vertices : null, any ? quads : 0, resource);
            stainsSet = any;
        }

        /// <summary>
        /// The presenting thread, before Submit: the path-traced camera of the frame being
        /// presented (as given to Remix), for the stains' view-projection.
        /// </summary>
        public static void SetStainView(in RemixFrameCapture.CameraData camera)
        {
            if (!bound || !stainsSet)
                return;
            if (!camera.valid)
            {
                UnityRTXHostPass_SetStainView(null);
                return;
            }
            // D3D clip space: x right, y up, z 0 at the near plane and w at the far one, w the
            // distance along the camera's axis.
            float tanY = Mathf.Tan(camera.fov * 0.5f * Mathf.Deg2Rad);
            float sx = 1f / (tanY * camera.aspect), sy = 1f / tanY;
            float a = camera.farPlane / (camera.farPlane - camera.nearPlane), b = -camera.nearPlane * a;
            Vector3 p = camera.position, r = camera.right, u = camera.up, f = camera.forward;
            void Row(int i, Vector3 axis, float scale, float add)
            {
                stainView[i * 4 + 0] = axis.x * scale;
                stainView[i * 4 + 1] = axis.y * scale;
                stainView[i * 4 + 2] = axis.z * scale;
                stainView[i * 4 + 3] = -Vector3.Dot(axis, p) * scale + add;
            }
            Row(0, r, sx, 0);
            Row(1, u, sy, 0);
            Row(2, f, a, b);
            Row(3, f, 1, 0);
            UnityRTXHostPass_SetStainView(stainView);
        }

        /// <summary>
        /// What the replay needs from Unity: the capture camera's projection (for the frame's
        /// depth), the texture the frame stands in for, and the pixels of the others the draws
        /// sample (read back once each, through a blit: they need not be readable).
        /// </summary>
        private static void PrepareReplay()
        {
            var projection = GL.GetGPUProjectionMatrix(helper.projectionMatrix, true);
            UnityRTXHostPass_SetProjection(projection.m22, projection.m23, SystemInfo.usesReversedZBuffer ? 1 : 0);

            Material material = null;
            for (int i = 0; i < renderers.Count && material == null; i++)
                if (renderers[i] != null)
                    material = renderers[i].sharedMaterial;
            // GetNativeTexturePtr waits for Unity's render thread: only when the texture changes.
            var frame = Games.GameProfiles.PassThroughFrameTexture(material);
            if (frame != frameTexture)
            {
                frameTexture = frame;
                frameTexturePtr = frame != null ? frame.GetNativeTexturePtr() : IntPtr.Zero;
            }
            UnityRTXHostPass_SetFrameTexture(frameTexturePtr);
            UnityRTXHostPass_SetGrabTexture(grabStandInPtr);
            UnityRTXHostPass_SetScreenSource(screenEffect != null ? screenSourcePtr : IntPtr.Zero,
                screenEffect != null ? screenSource.width : 0, screenEffect != null ? screenSource.height : 0,
                target.width, target.height);

            // Textures Unity has written since they were sent (sentVersions): again.
            if (sentVersions.Count > 0)
            {
                List<IntPtr> stale = null;
                foreach (var entry in sentVersions)
                    if (entry.Value.texture == null)
                        (stale ??= new List<IntPtr>()).Add(entry.Key);
                    else if (entry.Value.texture.updateCount != entry.Value.version)
                        (stale ??= new List<IntPtr>()).Add(entry.Key);
                if (stale != null)
                    foreach (var resource in stale)
                    {
                        var texture = sentVersions[resource].texture;
                        sentVersions.Remove(resource);
                        if (texture != null)
                            SendTexture(resource, texture);
                        else
                            sentTextures.Remove(resource);
                    }
            }

            int count = UnityRTXHostPass_MissingTextures(missing, missingSizes, missing.Length);
            for (int i = 0; i < count; i++)
            {
                IntPtr resource = missing[i];
                if (sentTextures.Contains(resource) || unmatchedTextures.Contains(resource))
                    continue;
                int width = missingSizes[3 * i], height = missingSizes[3 * i + 1], depth = missingSizes[3 * i + 2];
                var texture = FindTexture(resource, width, height, depth);
                if (texture == null)
                {
                    unmatchedTextures.Add(resource);
                    logger?.LogWarning($"[ShaderPassThrough] no texture found for resource 0x{resource.ToInt64():X} ({width}x{height}x{depth})");
                    continue;
                }
                if (SendTexture(resource, texture))
                    sentTextures.Add(resource);
                else
                    unmatchedTextures.Add(resource);
            }
        }

        // The texture whose native pointer is `resource`: the pass-through materials' own first,
        // then any loaded texture of its size.
        private static Texture FindTexture(IntPtr resource, int width, int height, int depth)
        {
            var names = new List<string>();
            var materials = new List<Material>();
            foreach (var r in renderers)
                if (r != null)
                    materials.AddRange(r.sharedMaterials);
            if (screenEffect != null)
                materials.Add(screenEffect);
            {
                foreach (var m in materials)
                {
                    if (m == null)
                        continue;
                    names.Clear();
                    m.GetTexturePropertyNames(names);
                    foreach (var name in names)
                    {
                        var t = m.GetTexture(name);
                        if (t != null && t.width == width && t.height == height && Depth(t) == depth && t.GetNativeTexturePtr() == resource)
                        {
                            logger?.LogInfo($"[ShaderPassThrough] resource 0x{resource.ToInt64():X} is {m.name}.{name} ('{t.name}')");
                            return t;
                        }
                    }
                }
            }
            foreach (var t in Resources.FindObjectsOfTypeAll<Texture>())
                if (t != null && t.width == width && t.height == height && Depth(t) == depth && t.GetNativeTexturePtr() == resource)
                {
                    logger?.LogInfo($"[ShaderPassThrough] resource 0x{resource.ToInt64():X} is texture '{t.name}' (not a material property)");
                    return t;
                }
            return null;
        }

        private static int Depth(Texture t) =>
            t is Texture3D volume ? volume.depth :
            t is RenderTexture rt && rt.dimension == TextureDimension.Tex3D ? rt.volumeDepth : 1;

        private static bool SendTexture(IntPtr resource, Texture texture)
        {
            bool srgb = UnityEngine.Experimental.Rendering.GraphicsFormatUtility.IsSRGBFormat(texture.graphicsFormat);
            uint version = texture.updateCount;
            int depth = Depth(texture);
            if (depth > 1)
            {
                if (!SendVolume(resource, texture, depth, srgb))
                    return false;
                sentVersions[resource] = (texture, version);
                return true;
            }
            var rt = RenderTexture.GetTemporary(texture.width, texture.height, 0, RenderTextureFormat.ARGB32,
                srgb ? RenderTextureReadWrite.sRGB : RenderTextureReadWrite.Linear);
            var previous = RenderTexture.active;
            try
            {
                Graphics.Blit(texture, rt);
                RenderTexture.active = rt;
                var copy = new Texture2D(texture.width, texture.height, TextureFormat.RGBA32, false, !srgb);
                copy.ReadPixels(new Rect(0, 0, texture.width, texture.height), 0, 0, false);
                var pixels = copy.GetRawTextureData();
                UnityEngine.Object.Destroy(copy);
                var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
                try
                {
                    // VK_FORMAT_R8G8B8A8_SRGB 43, _UNORM 37: sampled as the game's view of it was.
                    UnityRTXHostPass_SetTexture(resource, texture.width, texture.height, 1, srgb ? 43 : 37, handle.AddrOfPinnedObject());
                }
                finally { handle.Free(); }
                logger?.LogInfo($"[ShaderPassThrough] sent '{texture.name}' {texture.width}x{texture.height}{(srgb ? " sRGB" : "")}");
                sentVersions[resource] = (texture, version);
                return true;
            }
            catch (Exception e)
            {
                logger?.LogWarning($"[ShaderPassThrough] reading '{texture.name}' back failed: {e.Message}");
                return false;
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(rt);
            }
        }

        // A volume (a palette's 3D LUT, often a compute shader's render texture): read back whole
        // through the GPU, slices in order.
        private static bool SendVolume(IntPtr resource, Texture texture, int depth, bool srgb)
        {
            try
            {
                var request = AsyncGPUReadback.Request(texture, 0, 0, texture.width, 0, texture.height, 0, depth, TextureFormat.RGBA32);
                request.WaitForCompletion();
                if (request.hasError)
                {
                    logger?.LogWarning($"[ShaderPassThrough] reading volume '{texture.name}' back failed");
                    return false;
                }
                int slice = texture.width * texture.height * 4;
                var pixels = new byte[(long)slice * depth];
                if (request.layerCount >= depth)
                {
                    for (int z = 0; z < depth; z++)
                        Unity.Collections.NativeArray<byte>.Copy(request.GetData<byte>(z), 0, pixels, z * slice, slice);
                }
                else
                {
                    var data = request.GetData<byte>(0);
                    if (data.Length < pixels.Length)
                    {
                        logger?.LogWarning($"[ShaderPassThrough] volume '{texture.name}' read back {data.Length} of {pixels.Length} bytes");
                        return false;
                    }
                    Unity.Collections.NativeArray<byte>.Copy(data, pixels, pixels.Length);
                }
                var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
                try
                {
                    UnityRTXHostPass_SetTexture(resource, texture.width, texture.height, depth, srgb ? 43 : 37, handle.AddrOfPinnedObject());
                }
                finally { handle.Free(); }
                logger?.LogInfo($"[ShaderPassThrough] sent volume '{texture.name}' {texture.width}x{texture.height}x{depth}{(srgb ? " sRGB" : "")}");
                return true;
            }
            catch (Exception e)
            {
                logger?.LogWarning($"[ShaderPassThrough] reading volume '{texture.name}' back failed: {e.Message}");
                return false;
            }
        }

        /// <summary>
        /// Main thread: a texture the replay samples has new contents Unity does not count as an
        /// update (a compute shader's render texture): sent again before it is next sampled.
        /// </summary>
        public static void TextureChanged(Texture texture)
        {
            if (texture == null)
                return;
            foreach (var entry in new List<KeyValuePair<IntPtr, (Texture texture, uint version)>>(sentVersions))
                if (entry.Value.texture == texture)
                    sentVersions[entry.Key] = (texture, unchecked(texture.updateCount + 1));
        }

        /// <summary>The presenting thread, before each present: the last capture as host passes.</summary>
        public static void Submit()
        {
            if (!bound)
                return;
            if (layersViewAvailable)
            {
                // An older UnityRTXHostPass.dll has no such export.
                try { UnityRTXHostPass_SetLayersView(LayersView?.Value ?? 0); }
                catch (EntryPointNotFoundException) { layersViewAvailable = false; }
            }
            try { UnityRTXHostPass_Submit(); }
            catch (Exception e)
            {
                bound = false;
                logger?.LogWarning($"[ShaderPassThrough] submit failed, replay off: {e.Message}");
            }
        }

        // The screen effect's source: the size the game renders the frame it reads at (its
        // _MainTex_TexelSize; the replay scales the frame to it), contents unused.
        private static void EnsureScreenSource()
        {
            var size = Games.GameProfiles.PassThroughScreenResolution(screenEffect);
            int width = size.x > 0 ? Mathf.Min(size.x, target.width) : target.width;
            int height = size.y > 0 ? Mathf.Min(size.y, target.height) : target.height;
            if (screenSource != null && screenSource.width == width && screenSource.height == height)
                return;
            if (screenSource != null)
                screenSource.Release();
            screenSource = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32) { name = "UnityRTX screen source" };
            screenSource.Create();
            screenSourcePtr = screenSource.GetNativeTexturePtr();
        }

        private static void EnsureHelper(Camera main)
        {
            // The presented frame's size: the game camera's own target may be smaller (a game
            // rendering at a lower resolution, as ULTRAKILL's downsampling does).
            int width = Mathf.Max(1, Screen.width), height = Mathf.Max(1, Screen.height);
            if (target == null || target.width != width || target.height != height)
            {
                if (target != null)
                    target.Release();
                target = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32) { name = "UnityRTX pass-through" };
                target.Create();
            }
            if (grabStandIn == null)
            {
                grabStandIn = new RenderTexture(4, 4, 0, RenderTextureFormat.ARGB32) { name = "UnityRTX grab stand-in" };
                grabStandIn.Create();
                grabStandInPtr = grabStandIn.GetNativeTexturePtr();
            }
            if (helper == null)
            {
                var go = new GameObject("UnityRTX pass-through camera") { hideFlags = HideFlags.HideAndDontSave };
                UnityEngine.Object.DontDestroyOnLoad(go);
                helper = go.AddComponent<Camera>();
                helper.enabled = false;
                commands = new CommandBuffer { name = "UnityRTX pass-through" };
                helper.AddCommandBuffer(CameraEvent.AfterForwardOpaque, commands);
            }
        }

        private static void Report(int frame)
        {
            lastReportFrame = frame;
            var text = new StringBuilder(1 << 16);
            UnityRTXHostPass_Report(text, text.Capacity);
            var report = text.ToString();
            // The first reports always (diagnostics), then only when the draws change.
            int draws = report.Split('\n').Length;
            if (++reports > 5 && draws == lastDrawCount)
                return;
            lastDrawCount = draws;
            int frames = capturedFrames + culledFrames;
            logger?.LogInfo($"[ShaderPassThrough] {renderers.Count} renderers in view; capture {captureTime.Elapsed.TotalMilliseconds / Math.Max(1, frames):F2} ms a frame " +
                            $"over {frames} frames ({culledFrames} with none in view):\n{report}");
            captureTime.Reset();
            capturedFrames = culledFrames = 0;
        }
    }
}
