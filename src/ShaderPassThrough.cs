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

        [DllImport(Dll)] private static extern int UnityRTXHostPass_Install(IntPtr device);
        [DllImport(Dll)] private static extern IntPtr UnityRTXHostPass_RenderEvent();
        [DllImport(Dll)] private static extern int UnityRTXHostPass_Report(StringBuilder buffer, int bytes);
        [DllImport(Dll)] private static extern int UnityRTXHostPass_BindRemix(IntPtr remix);
        [DllImport(Dll)] private static extern void UnityRTXHostPass_SetFrameTexture(IntPtr resource);
        [DllImport(Dll)] private static extern void UnityRTXHostPass_SetProjection(float p22, float p23, int reversedZ);
        [DllImport(Dll)] private static extern void UnityRTXHostPass_SetTexture(IntPtr resource, int width, int height, int vkFormat, IntPtr rgba8);
        [DllImport(Dll)] private static extern int UnityRTXHostPass_MissingTextures([Out] IntPtr[] resources, [Out] int[] sizes, int max);
        [DllImport(Dll)] private static extern int UnityRTXHostPass_Submit();

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void GetDeviceD(IntPtr self, out IntPtr device);

        private static ManualLogSource logger;
        private static bool installed;
        private static IntPtr renderEvent;
        private static Camera helper;
        private static RenderTexture target;
        private static CommandBuffer commands;
        private static readonly List<Renderer> renderers = new List<Renderer>();
        private static int lastReportFrame = -100000;
        private static int lastDrawCount = -1;
        private static int reports;
        private static bool bound, bindFailed;
        // Textures sent to the replay (by native pointer), and those no texture was found for.
        private static readonly HashSet<IntPtr> sentTextures = new HashSet<IntPtr>();
        private static readonly HashSet<IntPtr> unmatchedTextures = new HashSet<IntPtr>();
        private static readonly IntPtr[] missing = new IntPtr[32];
        private static readonly int[] missingSizes = new int[64];

        /// <summary>The hooks are in: the profile's pass-through renderers are replayed with their own shaders.</summary>
        public static bool Installed => installed;

        /// <summary>Hooks Unity's D3D11 device, before the game creates the shaders it passes through.</summary>
        public static void Install(ManualLogSource log)
        {
            logger = log;
            if (installed || SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D11)
                return;
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
            renderers.Clear();
            Games.GameProfiles.GetPassThroughRenderers(main, renderers);
            if (renderers.Count == 0)
                return;
            EnsureHelper(main);
            helper.CopyFrom(main);
            helper.enabled = false;
            helper.cullingMask = 0;
            helper.clearFlags = CameraClearFlags.SolidColor;
            helper.backgroundColor = Color.clear;
            helper.targetTexture = target;

            commands.Clear();
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
                    commands.DrawRenderer(r, materials[s], s, 0);
                }
            }
            commands.IssuePluginEvent(renderEvent, EndEvent);
            helper.Render();

            if (!bound && !bindFailed && RemixAPI.RemixDllHandle != IntPtr.Zero)
            {
                bound = UnityRTXHostPass_BindRemix(RemixAPI.RemixDllHandle) != 0;
                bindFailed = !bound;
                logger?.LogInfo($"[ShaderPassThrough] replay {(bound ? "bound to remix.dll's host passes" : "unavailable: remix.dll has no host passes")}");
            }
            if (bound)
                PrepareReplay();

            if (frame - lastReportFrame >= 600)
                Report(frame);
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
            var frame = Games.GameProfiles.PassThroughFrameTexture(material);
            UnityRTXHostPass_SetFrameTexture(frame != null ? frame.GetNativeTexturePtr() : IntPtr.Zero);

            int count = UnityRTXHostPass_MissingTextures(missing, missingSizes, missing.Length);
            for (int i = 0; i < count; i++)
            {
                IntPtr resource = missing[i];
                if (sentTextures.Contains(resource) || unmatchedTextures.Contains(resource))
                    continue;
                int width = missingSizes[2 * i], height = missingSizes[2 * i + 1];
                var texture = FindTexture(resource, width, height);
                if (texture == null)
                {
                    unmatchedTextures.Add(resource);
                    logger?.LogWarning($"[ShaderPassThrough] no texture found for resource 0x{resource.ToInt64():X} ({width}x{height})");
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
        private static Texture FindTexture(IntPtr resource, int width, int height)
        {
            var names = new List<string>();
            foreach (var r in renderers)
            {
                if (r == null)
                    continue;
                foreach (var m in r.sharedMaterials)
                {
                    if (m == null)
                        continue;
                    names.Clear();
                    m.GetTexturePropertyNames(names);
                    foreach (var name in names)
                    {
                        var t = m.GetTexture(name);
                        if (t != null && t.width == width && t.height == height && t.GetNativeTexturePtr() == resource)
                        {
                            logger?.LogInfo($"[ShaderPassThrough] resource 0x{resource.ToInt64():X} is {m.name}.{name} ('{t.name}')");
                            return t;
                        }
                    }
                }
            }
            foreach (var t in Resources.FindObjectsOfTypeAll<Texture>())
                if (t != null && t.width == width && t.height == height && t.GetNativeTexturePtr() == resource)
                {
                    logger?.LogInfo($"[ShaderPassThrough] resource 0x{resource.ToInt64():X} is texture '{t.name}' (not a material property)");
                    return t;
                }
            return null;
        }

        private static bool SendTexture(IntPtr resource, Texture texture)
        {
            bool srgb = UnityEngine.Experimental.Rendering.GraphicsFormatUtility.IsSRGBFormat(texture.graphicsFormat);
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
                    UnityRTXHostPass_SetTexture(resource, texture.width, texture.height, srgb ? 43 : 37, handle.AddrOfPinnedObject());
                }
                finally { handle.Free(); }
                logger?.LogInfo($"[ShaderPassThrough] sent '{texture.name}' {texture.width}x{texture.height}{(srgb ? " sRGB" : "")}");
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

        /// <summary>The presenting thread, before each present: the last capture as host passes.</summary>
        public static void Submit()
        {
            if (!bound)
                return;
            try { UnityRTXHostPass_Submit(); }
            catch (Exception e)
            {
                bound = false;
                logger?.LogWarning($"[ShaderPassThrough] submit failed, replay off: {e.Message}");
            }
        }

        private static void EnsureHelper(Camera main)
        {
            int width = Mathf.Max(1, main.pixelWidth), height = Mathf.Max(1, main.pixelHeight);
            if (target == null || target.width != width || target.height != height)
            {
                if (target != null)
                    target.Release();
                target = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32) { name = "UnityRTX pass-through" };
                target.Create();
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
            logger?.LogInfo($"[ShaderPassThrough] {renderers.Count} renderers passed through; capture:\n{report}");
        }
    }
}
