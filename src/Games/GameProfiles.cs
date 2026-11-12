using System;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityRemix.Games.Ultrakill;

namespace UnityRemix.Games
{
    /// <summary>
    /// Picks the running game's profile and forwards the bridge's hooks to it. A failing
    /// hook is logged once and then ignored, so a game update can't break the bridge.
    /// </summary>
    public static class GameProfiles
    {
        public const string Auto = "Auto", None = "None";

        // Every game with patches. Add new games here.
        private static readonly GameProfile[] all = { new UltrakillProfile() };

        public static GameProfile Active { get; private set; }

        private static ManualLogSource logger;
        private static bool hookFailed;

        public static string[] Names
        {
            get
            {
                var names = new string[all.Length];
                for (int i = 0; i < all.Length; i++) names[i] = all[i].Name;
                return names;
            }
        }

        // setting: Auto (by executable name), None, or a profile name.
        public static void Detect(string setting, ManualLogSource log)
        {
            logger = log;
            string exe = "";
            try { exe = System.Diagnostics.Process.GetCurrentProcess().ProcessName; } catch { }

            Active = null;
            if (string.IsNullOrEmpty(setting) || setting.Equals(Auto, StringComparison.OrdinalIgnoreCase))
            {
                foreach (var profile in all)
                    if (Array.Exists(profile.ExecutableNames, name => name.Equals(exe, StringComparison.OrdinalIgnoreCase)))
                        Active = profile;
            }
            else if (!setting.Equals(None, StringComparison.OrdinalIgnoreCase))
            {
                Active = Array.Find(all, profile => profile.Name.Equals(setting, StringComparison.OrdinalIgnoreCase));
                if (Active == null)
                    log.LogWarning($"[Games] Unknown GameProfile '{setting}' (known: {string.Join(", ", Names)}); no game patches");
            }

            Active?.Initialize(log);
            log.LogInfo($"[Games] Executable '{exe}', GameProfile={setting}: {(Active != null ? Active.Name + " patches" : "no game patches")}");
        }

        public static void Apply(Harmony harmony) => Run(nameof(Apply), p => p.Apply(harmony));

        public static void CaptureFrame(RemixMaterialManager materials, RemixFrameCapture.FrameState state, int frameCount) =>
            Run(nameof(CaptureFrame), p => p.CaptureFrame(materials, state, frameCount));

        public static void OnSceneLoaded() => Run(nameof(OnSceneLoaded), p => p.OnSceneLoaded());

        public static bool IsHiddenMaterial(Material material)
        {
            bool hidden = false;
            Run(nameof(IsHiddenMaterial), p => hidden = p.IsHiddenMaterial(material));
            return hidden;
        }

        public static bool FollowsActiveState(MeshFilter filter, Renderer renderer)
        {
            bool follows = false;
            Run(nameof(FollowsActiveState), p => follows = p.FollowsActiveState(filter, renderer));
            return follows;
        }

        public static bool IsDynamicObject(MeshFilter filter, Renderer renderer)
        {
            bool dynamic = false;
            Run(nameof(IsDynamicObject), p => dynamic = p.IsDynamicObject(filter, renderer));
            return dynamic;
        }

        public static void OverrideMaterial(Material material, ref RemixMaterialManager.AlphaMode alphaMode, ref float alphaCutoff, ref Color albedoColor)
        {
            var mode = alphaMode;
            float cutoff = alphaCutoff;
            var color = albedoColor;
            Run(nameof(OverrideMaterial), p => p.OverrideMaterial(material, ref mode, ref cutoff, ref color));
            alphaMode = mode;
            alphaCutoff = cutoff;
            albedoColor = color;
        }

        public static bool GetColorMask(Material material, MaterialPropertyBlock block, out RemixMaterialManager.ColorMask colorMask)
        {
            bool found = false;
            var mask = default(RemixMaterialManager.ColorMask);
            Run(nameof(GetColorMask), p => found = p.GetColorMask(material, block, out mask));
            colorMask = mask;
            return found;
        }

        public static bool GetRayPortalView(Camera worldCamera, out RayPortalView view)
        {
            bool found = false;
            var result = default(RayPortalView);
            Run(nameof(GetRayPortalView), p => found = p.GetRayPortalView(worldCamera, out result));
            view = result;
            return found;
        }

        public static void GetPortals(Camera worldCamera, System.Collections.Generic.List<PortalPair> portals)
        {
            portals.Clear();
            Run(nameof(GetPortals), p => p.GetPortals(worldCamera, portals));
        }

        public static bool IsPlayerBody(Renderer renderer)
        {
            bool body = false;
            Run(nameof(IsPlayerBody), p => body = p.IsPlayerBody(renderer));
            return body;
        }

        public static void GetPassThroughRenderers(Camera worldCamera, System.Collections.Generic.List<Renderer> renderers)
        {
            Run(nameof(GetPassThroughRenderers), p => p.GetPassThroughRenderers(worldCamera, renderers));
        }

        public static Texture PassThroughFrameTexture(Material material)
        {
            Texture texture = null;
            Run(nameof(PassThroughFrameTexture), p => texture = p.PassThroughFrameTexture(material));
            return texture;
        }

        public static bool OpensViewPortal(Material material)
        {
            bool opens = false;
            Run(nameof(OpensViewPortal), p => opens = p.OpensViewPortal(material));
            return opens;
        }

        public static float ViewChromaticAberration(Material material)
        {
            float aberration = 0f;
            Run(nameof(ViewChromaticAberration), p => aberration = p.ViewChromaticAberration(material));
            return aberration;
        }

        public static Camera FindViewModelCamera(Camera worldCamera)
        {
            Camera camera = null;
            Run(nameof(FindViewModelCamera), p => camera = p.FindViewModelCamera(worldCamera));
            return camera;
        }

        public static bool? OnRemixMenu(bool open)
        {
            bool? lockCursor = null;
            Run(nameof(OnRemixMenu), p => lockCursor = p.OnRemixMenu(open));
            return lockCursor;
        }

        private static void Run(string hook, Action<GameProfile> action)
        {
            var profile = Active;
            if (profile == null) return;
            try
            {
                action(profile);
            }
            catch (Exception ex)
            {
                if (hookFailed) return;
                hookFailed = true;
                logger?.LogError($"[Games] {profile.Name} {hook} failed (further failures not logged): {ex}");
            }
        }
    }
}
