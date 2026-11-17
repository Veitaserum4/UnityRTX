using System;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityRemix.Games.Ultrakill;
using UnityRemix.Games.Atlyss;

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
        private static readonly GameProfile[] all =
        {
            new UltrakillProfile(),
            new AtlyssProfile(),
            new DefaultsOnlyProfile("DUSK", "Dusk", "Dusk"),
            new DefaultsOnlyProfile("Getting Over It", "GettingOverIt", "GettingOverIt"),
            new DefaultsOnlyProfile("PEAK", "Peak", "PEAK"),
            new DefaultsOnlyProfile("R.E.P.O.", "Repo", "REPO"),
            new DefaultsOnlyProfile("Risk of Rain 2", "RiskOfRain2", "Risk of Rain 2"),
            new DefaultsOnlyProfile("White Knuckle", "WhiteKnuckle", "White Knuckle"),
            new DefaultsOnlyProfile("Gloomwood", "Gloomwood", "Gloomwood"),
        };

        /// <summary>The profile for this executable (before Detect, which honours the setting).</summary>
        public static GameProfile ForExecutable()
        {
            string exe = "";
            try { exe = System.Diagnostics.Process.GetCurrentProcess().ProcessName; } catch { }
            foreach (var profile in all)
                if (Array.Exists(profile.ExecutableNames, name => name.Equals(exe, StringComparison.OrdinalIgnoreCase)))
                    return profile;
            return null;
        }

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
            log.LogInfo($"[Games] Executable '{exe}', GameProfile={setting}: {(Active != null ? Active.Name : "none")}");
        }

        public static void Apply(Harmony harmony) => Run(nameof(Apply), p => p.Apply(harmony));

        public static void CaptureFrame(RemixMaterialManager materials, RemixFrameCapture.FrameState state, int frameCount) =>
            Run(nameof(CaptureFrame), p => p.CaptureFrame(materials, state, frameCount));

        public static void OnSceneLoaded() => Run(nameof(OnSceneLoaded), p => p.OnSceneLoaded());

        public static bool IsHiddenRenderer(Renderer renderer)
        {
            bool hidden = false;
            Run(nameof(IsHiddenRenderer), p => hidden = p.IsHiddenRenderer(renderer));
            return hidden;
        }

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

        public static bool GetIridescence(Material material, MaterialPropertyBlock block, out RemixMaterialManager.Iridescence film)
        {
            bool found = false;
            var result = default(RemixMaterialManager.Iridescence);
            Run(nameof(GetIridescence), p => found = p.GetIridescence(material, block, out result));
            film = result;
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

        public static bool IsNeverRenderedLayer(int layer)
        {
            bool never = false;
            Run(nameof(IsNeverRenderedLayer), p => never = p.IsNeverRenderedLayer(layer));
            return never;
        }

        public static void OverrideTextureTransform(Material material, ref Vector4 scaleOffset)
        {
            var st = scaleOffset;
            Run(nameof(OverrideTextureTransform), p => p.OverrideTextureTransform(material, ref st));
            scaleOffset = st;
        }

        public static bool IsHiddenLight(Light light)
        {
            bool hidden = false;
            Run(nameof(IsHiddenLight), p => hidden = p.IsHiddenLight(light));
            return hidden;
        }

        public static float SceneLightScale(Material material, Renderer renderer)
        {
            float scale = 0f;
            Run(nameof(SceneLightScale), p => scale = p.SceneLightScale(material, renderer));
            return scale;
        }

        public static float SelfIlluminationScale(Material material, Renderer renderer)
        {
            float scale = 0f;
            Run(nameof(SelfIlluminationScale), p => scale = p.SelfIlluminationScale(material, renderer));
            return scale;
        }

        public static bool IsBackdropEmission(Material material)
        {
            bool backdrop = false;
            Run(nameof(IsBackdropEmission), p => backdrop = p.IsBackdropEmission(material));
            return backdrop;
        }

        public static bool ReadsBlendProperties(string shaderName)
        {
            bool reads = false;
            Run(nameof(ReadsBlendProperties), p => reads = p.ReadsBlendProperties(shaderName));
            return reads;
        }

        public static bool GetUnderwaterTint(out Color tint)
        {
            bool shown = false;
            Color found = default;
            Run(nameof(GetUnderwaterTint), p => shown = p.GetUnderwaterTint(out found));
            tint = found;
            return shown;
        }

        public static Material PassThroughScreenEffect()
        {
            Material material = null;
            Run(nameof(PassThroughScreenEffect), p => material = p.PassThroughScreenEffect());
            return material;
        }

        public static Vector2Int PassThroughScreenResolution(Material material)
        {
            Vector2Int size = Vector2Int.zero;
            Run(nameof(PassThroughScreenResolution), p => size = p.PassThroughScreenResolution(material));
            return size;
        }

        public static int PassThroughShaderPass(Material material)
        {
            int pass = 0;
            Run(nameof(PassThroughShaderPass), p => pass = p.PassThroughShaderPass(material));
            return pass;
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

        public static float WorldFogDensity(float density)
        {
            var profile = Active;
            if (profile == null) return density;
            try
            {
                return profile.WorldFogDensity(density);
            }
            catch (Exception ex)
            {
                if (!hookFailed)
                {
                    hookFailed = true;
                    logger?.LogError($"[Games] {profile.Name} {nameof(WorldFogDensity)} failed (further failures not logged): {ex}");
                }
                return density;
            }
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
