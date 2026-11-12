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

            if (frame - lastReportFrame >= 600)
                Report(frame);
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
