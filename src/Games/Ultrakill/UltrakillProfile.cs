using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace UnityRemix.Games.Ultrakill
{
    internal sealed class UltrakillProfile : GameProfile
    {
        public override string Name => "ULTRAKILL";
        public override string[] ExecutableNames => new[] { "ULTRAKILL" };
        public override string DefaultsFolder => "Ultrakill";

        // Components whose objects move, spawn or die: drawn by the per-frame capture.
        private static readonly string[] dynamicTypeNames =
        {
            "EnemyIdentifier", "SpawnEffect", "SeasonalHats", "NewMovement", "PlayerTracker", "Projectile",
            "Coin", "Nail", "Grenade", "ItemIdentifier", "Skull", "BloodAbsorber"
        };
        // Spawn effects and the seasonal hats swapped onto enemies, by object name / parent name.
        private static readonly string[] dynamicObjectNames = { "SpawnEffect", "SeasonalHats", "Pumpkin", "SantaHat", "EasterBunny" };
        private static readonly string[] dynamicParentNames = { "SpawnEffect", "SeasonalHats", "Halloween", "Christmas", "Easter" };

        private Type[] dynamicTypes;
        private Type[] followTypes;
        private readonly UltrakillBloodStains bloodStains = new UltrakillBloodStains();
        private readonly UltrakillOilStains oilStains = new UltrakillOilStains();

        public override void Apply(Harmony harmony)
        {
            UltrakillPostProcessPatch.Apply(harmony, Logger);
            UltrakillGameState.Apply(harmony, Logger);
            UltrakillSpawnPatch.Apply(harmony, Logger);
            UltrakillOilStains.Apply(harmony, Logger);
            UltrakillStaticOptimizer.Apply(harmony, Logger);
        }

        public override bool GetOriginalGeometry(Renderer renderer, out Mesh mesh, out Material[] materials) =>
            UltrakillStaticOptimizer.TryGetOriginal(renderer, out mesh, out materials);

        // Weapons and arms are drawn by the main camera's "HUD Camera" child, at the weapon FOV.
        private Camera worldCamera, hudCamera;

        public override Camera FindViewModelCamera(Camera world)
        {
            if (world != worldCamera || (hudCamera == null && Time.frameCount % 60 == 0))
            {
                worldCamera = world;
                hudCamera = null;
                foreach (var cam in world.GetComponentsInChildren<Camera>(true))
                {
                    if (cam.name == "HUD Camera")
                    {
                        hudCamera = cam;
                        break;
                    }
                }
            }
            return hudCamera;
        }

        public override bool? OnRemixMenu(bool open) => UltrakillGameState.SetRemixMenuState(open, Logger);

        public override void CaptureFrame(RemixMaterialManager materials, RemixFrameCapture.FrameState state, int frameCount)
        {
            bloodStains.Capture(Logger, materials, state);
            oilStains.Capture(Logger, materials, state);
            lightning.Update(Logger, frameCount);
        }

        private readonly UltrakillLightning lightning = new UltrakillLightning();

        public override void OnSceneLoaded()
        {
            bloodStains.Reset();
            oilStains.Reset();
            lightning.Reset();
            limboWallsFrame = -100000; // rescan the Limbo walls
        }

        // Unity's built-in Default-Material appears only on helper shapes the game doesn't show:
        // crusher base boxes, skull altar placeholders, invisible walls.
        // ULTRAKILL/HeatWave: heat haze, a screen distortion (its texture only bends the view
        // behind it): not path-traced, passed through (GetPassThroughRenderers).
        // ULTRAKILL/Oilstain_Fallback: oil stains, path-traced as paint (UltrakillOilStains).
        public override bool IsHiddenMaterial(Material material) =>
            material.name.StartsWith("Default-Material", StringComparison.Ordinal) ||
            UltrakillOilStains.IsStainMaterial(material) ||
            IsHeatWave(material) ||
            (ShaderPassThrough.Installed && IsFresnelGlass(material));

        // Limbo's TV-screen walls (LIMBO_WALLS) show _LimboSky in screen space (times their own
        // texture, with glitches): the LimboSkybox camera's view of a miniature scene elsewhere
        // in the level. That camera ("fakeCam") turns like the player's, at fakeCamStart +
        // (player - playerStart) / 16 (y not below the start's with lockMinimumHeight): the
        // walls are a view portal into the miniature at scale 1/16. The exit region: around
        // fakeCamStart, within its camera's far plane and nearer it than the player's start.
        public override bool OpensViewPortal(Material material) => material.IsKeywordEnabled("LIMBO_WALLS");

        // The Limbo walls (LIMBO_WALLS): their shader passes through (its sky texture, _LimboSky,
        // becomes the path-traced frame), scanned on scene load and every 600 frames (a scan
        // walks every MeshRenderer in the scene), with the soul orbs and heat-haze meshes.
        private readonly List<Renderer> limboWalls = new List<Renderer>();
        private int limboWallsFrame = -1000;

        public override bool UsesShaderPassThrough => true;

        // _LimboSky: the material's, else the global the LimboSkybox camera sets.
        public override Texture PassThroughFrameTexture(Material material)
        {
            Texture sky = null;
            if (material != null && material.HasProperty("_LimboSky"))
                sky = material.GetTexture("_LimboSky");
            return sky != null ? sky : Shader.GetGlobalTexture("_LimboSky");
        }

        public override void GetPassThroughRenderers(Camera worldCamera, List<Renderer> renderers)
        {
            // One destroyed: its room was replaced (a checkpoint restores a clone of it), whose
            // copies, hidden from the path tracer by material, are drawn only once listed.
            bool replaced = false;
            foreach (var r in limboWalls)
                if (r == null)
                {
                    replaced = true;
                    break;
                }
            if (replaced || Time.frameCount - limboWallsFrame >= 600)
            {
                // Inactive ones too: soul orbs wait in rooms not yet reached.
                limboWallsFrame = Time.frameCount;
                limboWalls.Clear();
                foreach (var r in UnityCompat.FindSceneComponentsIncludingInactive<MeshRenderer>())
                    foreach (var m in r.sharedMaterials)
                        if (m != null && (m.IsKeywordEnabled("LIMBO_WALLS") || IsFresnelGlass(m) || IsHeatWave(m)))
                        {
                            // A soul orb's glow quad (its sibling, drawn just before it) too.
                            if (IsFresnelGlass(m) && r.transform.parent != null)
                                foreach (var sibling in r.transform.parent.GetComponentsInChildren<MeshRenderer>(true))
                                    if (sibling != r && IsOrbGlow(sibling) && !limboWalls.Contains(sibling))
                                        limboWalls.Add(sibling);
                            limboWalls.Add(r);
                            break;
                        }
                passedThroughGlows.Clear();
                foreach (var r in limboWalls)
                    if (r != null && IsOrbGlow(r))
                        passedThroughGlows.Add(r.GetInstanceID());
            }
            foreach (var r in limboWalls)
                if (r != null)
                    renderers.Add(r);
            // Heat haze particles (Greed), found a few times a second (they come with rooms).
            if (Time.frameCount - heatWavesFrame >= 30)
            {
                heatWavesFrame = Time.frameCount;
                heatWaves.Clear();
                foreach (var r in UnityEngine.Object.FindObjectsOfType<ParticleSystemRenderer>())
                    if (IsHeatWave(r.sharedMaterial))
                        heatWaves.Add(r);
            }
            foreach (var r in heatWaves)
                if (r != null)
                    renderers.Add(r);
        }

        // ULTRAKILL/Master's unlit see-through FRESNEL variant (soul orbs and other bonus
        // crystals): texture x _Color plus _FresnelColor towards the rim, more opaque there. It
        // passes through as the game draws it; path-traced, it was a dim grey lit sheet.
        // A soul orb's glow: a camera-facing quad beside it in Sprites/Default, drawn just before
        // it (queue 2999, no depth). Path-traced it covered the orb (the replay depth-tests against
        // it) and blended unlike the game; it passes through with the orb.
        private static bool IsOrbGlow(Renderer renderer)
        {
            var m = renderer.sharedMaterial;
            return m != null && m.shader != null && m.shader.name.EndsWith("Sprites/Default", StringComparison.Ordinal) &&
                   renderer.GetComponent<MeshFilter>() != null;
        }

        private readonly HashSet<int> passedThroughGlows = new HashSet<int>();

        public override bool IsHiddenRenderer(Renderer renderer)
        {
            if (!ShaderPassThrough.Installed)
                return false;
            if (Time.frameCount - limboWallsFrame >= 600)
                GetPassThroughRenderers(null, new List<Renderer>()); // the scan the replay uses
            return passedThroughGlows.Contains(renderer.GetInstanceID());
        }

        private static bool IsFresnelGlass(Material material) =>
            material != null && material.shader != null && material.shader.name == "ULTRAKILL/Master" &&
            material.IsKeywordEnabled("FRESNEL") && material.IsKeywordEnabled("TRANSPARENCY");

        // ULTRAKILL/HeatWave: heat haze, the view behind it bent by scrolling noise. Its pass 0
        // is a GrabPass (_GrabTexture), pass 1 the distortion, which the replay draws over the
        // path-traced frame. HeatWave_Camera (Greed's haze in front of the camera once its first
        // door opens) has the same passes, with _Scale and _Speed; path-traced it was
        // its noise texture drawn as an alpha sheet over the view.
        private static bool IsHeatWave(Material material) =>
            material != null && material.shader != null &&
            (material.shader.name == "ULTRAKILL/HeatWave" || material.shader.name == "ULTRAKILL/HeatWave_Camera");

        private readonly List<Renderer> heatWaves = new List<Renderer>();
        private int heatWavesFrame = -1000;

        public override int PassThroughShaderPass(Material material) => IsHeatWave(material) ? 1 : 0;

        // PostProcessV2_Handler's screen material (ULTRAKILL/PostProcessV2, postProcessV2_VSRM)
        // while it shows something: DEAD, the death sequence (DeathSequence; noise and blocks
        // ramping with _Deathness), or WICKED, Greed's distortion near a ScreenDistortionField
        // (ScreenDistortionController; its strength _RandomNoiseStrength). Its variants also
        // apply the material's palette, colour depth and vignette, as the game does. With
        // GamePostProcess, always: the graphics menu's dithering (_DitherStrength), colour
        // compression (_ColorPrecision), palette (PALETTIZE), gamma and downsampling.
        public static BepInEx.Configuration.ConfigEntry<bool> GamePostProcess;

        public override bool DrawSettings()
        {
            RemixImGui.TextColored(0.4f, 0.8f, 1.0f, 1.0f, "Screen");
            RemixImGui.Separator();
            if (GamePostProcess != null)
            {
                bool post = GamePostProcess.Value;
                if (RemixImGui.Checkbox("Game post-process", ref post))
                    GamePostProcess.Value = post;
                if (RemixImGui.IsItemHovered())
                    RemixImGui.SetTooltip("ULTRAKILL's own screen effects over the path-traced image, as its graphics menu sets them: " +
                                          "dithering, colour compression, custom palette, gamma, downscaling. " +
                                          "Off: only the death sequence and Greed's distortion.");
            }

            RemixImGui.Spacing();
            RemixImGui.TextColored(0.4f, 0.8f, 1.0f, 1.0f, "Lighting");
            RemixImGui.Separator();
            if (EmissiveExplosions != null)
            {
                bool emissive = EmissiveExplosions.Value;
                if (RemixImGui.Checkbox("Explosions light the scene", ref emissive))
                    EmissiveExplosions.Value = emissive;
                if (RemixImGui.IsItemHovered())
                    RemixImGui.SetTooltip("An explosion's fireball casts its own light, from its whole surface, fading with it, " +
                                          "instead of the game's point light at its centre. Applies to explosions from then on.");
                if (emissive && ExplosionBrightness != null)
                {
                    RemixImGui.Indent();
                    float brightness = ExplosionBrightness.Value;
                    if (RemixImGui.SliderFloat("Explosion brightness", ref brightness, 0.1f, 20f))
                        ExplosionBrightness.Value = brightness;
                    if (RemixImGui.IsItemHovered())
                        RemixImGui.SetTooltip("How many times brighter than the game draws it a fireball shows and lights (5). " +
                                              "Applies to explosions from then on.");
                    RemixImGui.Unindent();
                }
            }

            RemixImGui.Spacing();
            RemixImGui.TextColored(0.4f, 0.8f, 1.0f, 1.0f, "Blood and oil");
            RemixImGui.Separator();
            var overlay = UltrakillBloodStains.Overlay;
            if (overlay != null)
            {
                bool value = overlay.Value;
                if (RemixImGui.Checkbox("Blood stains as the game draws them", ref value))
                    overlay.Value = value;
                if (RemixImGui.IsItemHovered())
                    RemixImGui.SetTooltip("Composites stains over the image exactly as the game does (cheaper, not in reflections). " +
                                          "Off: path-traced wet paint (glossy, in reflections).");
            }
            var prune = UltrakillBloodStains.Prune;
            if (prune != null && (overlay == null || !overlay.Value))
            {
                bool value = prune.Value;
                if (RemixImGui.Checkbox("Leave out covered blood stains", ref value))
                    prune.Value = value;
                if (RemixImGui.IsItemHovered())
                    RemixImGui.SetTooltip("Path-traced stains that newer stains wholly cover are left out: the game shows the union of " +
                                          "its stains, so nothing changes, and deep piles of blood cost far less. Tested off the main thread.");
            }
            var oilFilm = UltrakillOilStains.Film;
            if (oilFilm != null)
            {
                bool value = oilFilm.Value;
                if (RemixImGui.Checkbox("Oil stains as a thin film", ref value))
                    oilFilm.Value = value;
                if (RemixImGui.IsItemHovered())
                    RemixImGui.SetTooltip("Firestarter oil on walls and floors coats the surface with a swirling thin film. " +
                                          "Off: the game's rainbow baked into the stain (for an OpenRemix runtime without paint films).");
            }
            return true;
        }
        private Material postProcessMaterial;
        private int postProcessFrame = -1000;

        // ConvertPaletteToLUT recomputes the palette's LUT (_LUT, a 3D render texture) in place
        // when the palette changes; Unity doesn't count a compute shader's writes as an update.
        private Component paletteConverter;
        private UnityEngine.Object lastPalette;
        private int paletteSearchFrame = -1000;

        private void WatchPalette()
        {
            if (paletteConverter == null && Time.frameCount - paletteSearchFrame >= 120)
            {
                paletteSearchFrame = Time.frameCount;
                var type = AccessTools.TypeByName("ConvertPaletteToLUT");
                if (type != null)
                    paletteConverter = UnityEngine.Object.FindObjectOfType(type) as Component;
            }
            if (paletteConverter == null)
                return;
            var type2 = paletteConverter.GetType();
            var palette = AccessTools.Field(type2, "lastPalette")?.GetValue(paletteConverter) as UnityEngine.Object;
            if (palette == lastPalette)
                return;
            lastPalette = palette;
            ShaderPassThrough.TextureChanged(AccessTools.Field(type2, "processedLUT")?.GetValue(paletteConverter) as Texture);
        }

        // Layers ULTRAKILL never shows: 16 Invisible (triggers, blockers, collision brushes),
        // 18 PlayerOnly (player barriers), 20 GroundCheck, 28 VirtualRender, 30 Portal. (19,
        // Virtual Screen, is drawn: the Virtual Camera shows it over the scene.)
        // The player's 'Point Light' (white, intensity 0.5, range 15, in every level): a subtle
        // fill in the game's vertex lighting, a glaring lamp at the camera when path traced.
        // With EmissiveExplosions an explosion's own light (a child of its Explosion,
        // brightening and widening as the fireball grows) is left out: the fireball lights
        // the scene itself.
        public override bool IsHiddenLight(Light light) =>
            (light.name == "Point Light" && light.transform.parent != null && light.transform.parent.name == "Player") ||
            (EmissiveExplosions != null && EmissiveExplosions.Value && ExplosionType != null &&
             light.GetComponentInParent(ExplosionType) != null);

        // An explosion's fireball (the sphere its Explosion component grows and fades, after a
        // first moment as a solid white sphere, its material named apart by
        // UltrakillSpawnPatch) casts the light it shows, with
        // EmissiveExplosions, ExplosionBrightness times as bright as the game draws it.
        public static BepInEx.Configuration.ConfigEntry<bool> EmissiveExplosions;
        public static BepInEx.Configuration.ConfigEntry<float> ExplosionBrightness;

        public override float SceneLightScale(Material material, Renderer renderer) =>
            EmissiveExplosions != null && EmissiveExplosions.Value && ExplosionType != null &&
            renderer.GetComponent(ExplosionType) != null
                ? Mathf.Clamp(ExplosionBrightness != null ? ExplosionBrightness.Value : 5f, 0.1f, 20f)
                : 0f;

        // An enemy projectile's orb (materials "Projectile", "Projectile 2", ...): ULTRAKILL/Master
        // with EMISSIVE off but _UseAlbedoAsEmissive on, lit in the game by the light at its
        // centre, which path tracing hides inside the orb. It shows its texture as light, five
        // times as bright as the game draws it to read as glowing; its own light still lights the
        // scene. Told by the material alone: it is converted once, by whichever capture path
        // meets it first, some without the renderer.
        public override float SelfIlluminationScale(Material material, Renderer renderer) =>
            material.name.StartsWith("Projectile", StringComparison.Ordinal) &&
            material.shader != null && material.shader.name == "ULTRAKILL/Master" &&
            material.HasProperty("_UseAlbedoAsEmissive") && material.GetFloat("_UseAlbedoAsEmissive") > 0.5f
                ? 5f
                : 0f;

        // ULTRAKILL/Master without VERTEX_LIGHTING is drawn unlit: light pillars, screens, the
        // laser grids and the Corpse of King Minos in 2-3, shown in their own colour. Path-traced
        // as lit surfaces they were dim. EMISSIVE ones keep their emission as converted.
        // ENEMY materials never carry VERTEX_LIGHTING (enemies are lit through the ENEMY path),
        // so for them only the fogless ones count: Minos's corpse (Minos, Minos2, MinosArmUnlit),
        // a backdrop the game draws in its own colour.
        public override bool IsUnlit(Material material) =>
            material.shader != null && material.shader.name == "ULTRAKILL/Master" &&
            !material.IsKeywordEnabled("VERTEX_LIGHTING") && !material.IsKeywordEnabled("EMISSIVE") &&
            (!material.IsKeywordEnabled("ENEMY") || material.IsKeywordEnabled("_FOG_OFF"));

        // ULTRAKILL's light shafts: the LightPillar materials (under windows and lamps, Greed's
        // skylights, 5-2's lighthouse), LightLighter(er) (7-2's windows), LightShaft (7-S) and
        // Minos's searchlights, additive meshes fading along their length.
        public override bool IsLightShaft(Renderer renderer)
        {
            var material = renderer.sharedMaterial;
            if (material == null)
                return false;
            string name = material.name;
            return name.IndexOf("LightPillar", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   name.IndexOf("LightLighter", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   name.IndexOf("LightShaft", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   name.IndexOf("SearchLight", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // ULTRAKILL/Master marks what it blends with TRANSPARENCY; without it the surface is
        // drawn solid, whatever its name (M_WaterCooler, WaterTank, WaterControl1). Fraud's
        // water cooler jug blends but is plastic; only M_WaterCooler_Water is its water.
        public override bool IsNeverLiquid(Material material) =>
            material.shader != null && material.shader.name == "ULTRAKILL/Master" &&
            (!material.IsKeywordEnabled("TRANSPARENCY") || material.name.IndexOf("_Jug", StringComparison.OrdinalIgnoreCase) >= 0);

        // Lust's city skyline around its levels (M_LustCityWalls, M_LustCityRooftops): lit
        // windows across tens of thousands of triangles, far outside the arena. As lights they
        // drew most local light samples (2-2: 67k emitters against a few lamps), leaving the
        // lamps' light noisy and dim.
        public override bool IsBackdropEmission(Material material) =>
            material.name.StartsWith("M_LustCity", StringComparison.Ordinal);

        private static Type explosionType;
        private static bool explosionTypeSearched;
        private static Type ExplosionType
        {
            get
            {
                if (!explosionTypeSearched)
                {
                    explosionTypeSearched = true;
                    explosionType = AccessTools.TypeByName("Explosion");
                    if (explosionType == null)
                        Debug.Log("[Ultrakill] Explosion type not found: explosions keep their own lights");
                }
                return explosionType;
            }
        }

        public override bool IsNeverRenderedLayer(int layer) =>
            layer == 16 || layer == 18 || layer == 20 || layer == 28 || layer == 30;

        // ULTRAKILL/Master and its particle shaders take _SrcBlend / _DstBlend (its psx
        // transparent shaders keep stale values and alpha blend).
        public override bool ReadsBlendProperties(string shaderName) =>
            shaderName == "ULTRAKILL/Master" || shaderName.StartsWith("ULTRAKILL/Particles/Standard", StringComparison.Ordinal);

        // The HUD's UnderwaterOverlay image (water, or red in blood), found again every few
        // seconds rather than searched for on each check.
        private UnityEngine.UI.Graphic underwaterOverlay;
        private float underwaterSearchTime = -100f;

        public override bool GetUnderwaterTint(out Color tint)
        {
            tint = default;
            if (underwaterOverlay == null && Time.unscaledTime - underwaterSearchTime > 3f)
            {
                underwaterSearchTime = Time.unscaledTime;
                var obj = GameObject.Find("UnderwaterOverlay");
                underwaterOverlay = obj != null ? obj.GetComponent<UnityEngine.UI.Graphic>() : null;
            }
            var image = underwaterOverlay;
            if (image == null || !image.isActiveAndEnabled || image.color.a <= 0.05f)
                return false;
            tint = image.color;
            return true;
        }

        public override Material PassThroughScreenEffect()
        {
            WatchPalette();
            if (postProcessMaterial == null && Time.frameCount - postProcessFrame >= 120)
            {
                postProcessFrame = Time.frameCount;
                var type = AccessTools.TypeByName("PostProcessV2_Handler");
                var field = type != null ? AccessTools.Field(type, "postProcessV2_VSRM") : null;
                var handler = type != null ? UnityEngine.Object.FindObjectOfType(type) : null;
                if (field != null && handler != null)
                    postProcessMaterial = field.GetValue(handler) as Material;
            }
            return postProcessMaterial != null &&
                   ((GamePostProcess != null && GamePostProcess.Value) ||
                    postProcessMaterial.IsKeywordEnabled("DEAD") || postProcessMaterial.IsKeywordEnabled("WICKED"))
                ? postProcessMaterial : null;
        }

        // The pixelization setting (_ResY): PostProcessV2_Handler renders mainTex at _VirtualRes.
        public override Vector2Int PassThroughScreenResolution(Material material)
        {
            if (material == null || material != postProcessMaterial || !material.HasProperty("_VirtualRes"))
                return Vector2Int.zero;
            Vector4 v = material.GetVector("_VirtualRes");
            return v.x >= 1f && v.y >= 1f ? new Vector2Int((int)v.x, (int)v.y) : Vector2Int.zero;
        }

        // LIMBO_WALLS samples the sky's red at +_ChromaticAberration / _LimboSkyWidth and its blue at
        // minus that, in screen UV: the sky is rendered at the screen's width over the skybox's
        // downscaleFactor (2), so the shift is _ChromaticAberration x 2 screen pixels.
        public override float ViewChromaticAberration(Material material)
        {
            if (!material.IsKeywordEnabled("LIMBO_WALLS") || !material.HasProperty("_ChromaticAberration"))
                return 0f;
            // Replayed with its own shader (ShaderPassThrough), which shifts the channels itself.
            if (ShaderPassThrough.Installed)
                return 0f;
            // LimboSkybox.downscaleFactor (2 in the game's levels), read from the skybox the view
            // portal found (GetRayPortalView).
            float downscale = 2f;
            if (limbo != null && limboType != null)
            {
                try
                {
                    var field = limboType.GetField("downscaleFactor");
                    if (field != null && field.GetValue(limbo) is float value && value > 0f)
                        downscale = value;
                }
                catch { }
            }
            return material.GetFloat("_ChromaticAberration") * downscale / Mathf.Max(1, Screen.width);
        }

        // V1's body (Player/GroundCheck/v1_combined: v1_mdl and the held weapons' models), on the
        // Portal layer (30), which only mirror and portal views draw.
        public override bool IsPlayerBody(Renderer renderer)
        {
            for (var t = renderer.transform; t != null; t = t.parent)
                if (t.name == "v1_combined")
                    return true;
            return false;
        }

        // ULTRAKILL.Portal.Portal (Fraud): entry and exit transforms (opening in their local XY
        // plane, +Z out), a PlaneShape (full width and height); travel exit (-1, 1, -1) entry^-1,
        // unscaled. Listed every 60 frames, read every frame.
        private Type portalType;
        private bool portalTypeSearched;
        private System.Reflection.FieldInfo portalEntry, portalExit, portalShape, shapeWidth, shapeHeight, portalMirror;
        private bool loggedPortalLayer;
        private readonly List<Behaviour> portalComponents = new List<Behaviour>();
        private int portalListFrame = int.MinValue / 2;

        public override void GetPortals(Camera worldCamera, List<PortalPair> portals)
        {
            if (!portalTypeSearched)
            {
                portalTypeSearched = true;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try { portalType = asm.GetType("ULTRAKILL.Portal.Portal"); } catch { }
                    if (portalType != null) break;
                }
                if (portalType != null)
                {
                    portalEntry = portalType.GetField("entry");
                    portalExit = portalType.GetField("exit");
                    portalShape = portalType.GetField("shape");
                    portalMirror = portalType.GetField("mirror");
                    var planeShape = portalType.Assembly.GetType("ULTRAKILL.Portal.Geometry.PlaneShape");
                    shapeWidth = planeShape?.GetField("width");
                    shapeHeight = planeShape?.GetField("height");
                }
                Logger.LogInfo($"[Portal] ULTRAKILL portals {(portalType != null && portalEntry != null && portalExit != null && shapeWidth != null ? "found" : "not found")}");
            }
            if (portalType == null || portalEntry == null || portalExit == null || portalShape == null || shapeWidth == null || shapeHeight == null || worldCamera == null)
                return;
            int frame = Time.frameCount;
            if (frame - portalListFrame >= 60)
            {
                portalListFrame = frame;
                portalComponents.Clear();
                foreach (var found in UnityEngine.Object.FindObjectsOfType(portalType))
                    if (found is Behaviour behaviour)
                        portalComponents.Add(behaviour);
            }
            Vector3 eye = worldCamera.transform.position;
            foreach (var portal in portalComponents)
            {
                if (portal == null || !portal.isActiveAndEnabled)
                    continue;
                var entry = portalEntry.GetValue(portal) as Transform;
                bool mirror = portalMirror != null && (bool)portalMirror.GetValue(portal);
                var exit = mirror ? entry : portalExit.GetValue(portal) as Transform;
                var shape = portalShape.GetValue(portal);
                if (entry == null || exit == null || shape == null || shape.GetType() != shapeWidth.DeclaringType)
                    continue;
                float width = (float)shapeWidth.GetValue(shape), height = (float)shapeHeight.GetValue(shape);
                if (width <= 0f || height <= 0f)
                    continue;
                LogNearbyPortal(portal, entry, exit, mirror, width, height, eye);
                if (mirror && !loggedPortalLayer)
                {
                    // What a mirror's view adds (canSeePortalLayer: the Portal layer, 30).
                    loggedPortalLayer = true;
                    int n = 0;
                    foreach (var r in UnityEngine.Object.FindObjectsOfType<Renderer>())
                    {
                        if (r.gameObject.layer != 30 || n++ >= 40)
                            continue;
                        string path = r.name;
                        for (var t = r.transform.parent; t != null; t = t.parent)
                            path = t.name + "/" + path;
                        Logger.LogInfo($"[Portal] layer 30 renderer '{path}' ({r.GetType().Name}), enabled {r.enabled}, at {r.bounds.center}");
                    }
                    Logger.LogInfo($"[Portal] {n} active renderers on layer 30");
                }
                portals.Add(new PortalPair
                {
                    lift = OpeningLift(portal, entry, exit, mirror, width, height),
                    id = portal.GetInstanceID(),
                    entry = Matrix4x4.TRS(entry.position, entry.rotation, Vector3.one),
                    exit = Matrix4x4.TRS(exit.position, exit.rotation, Vector3.one),
                    width = width,
                    height = height,
                    mirror = mirror
                });
            }
            // Remix has room for a few: those with an end in front of the camera first, nearest first.
            Vector3 look = worldCamera.transform.forward;
            portals.Sort((a, b) => Priority(a, eye, look).CompareTo(Priority(b, eye, look)));
        }

        // How far transparent surfaces over a portal's openings reach from their planes (a pool's
        // water sprite, a mirror's glass pane), once per portal: its openings stand past them.
        // Layers the game never draws are left out (the Archive mirror's OopsBlocker).
        private readonly Dictionary<int, float> openingLifts = new Dictionary<int, float>();

        private float OpeningLift(Behaviour portal, Transform entry, Transform exit, bool mirror, float width, float height)
        {
            int id = portal.GetInstanceID();
            if (openingLifts.TryGetValue(id, out float lift))
                return lift;
            lift = 0f;
            foreach (var end in mirror ? new[] { entry } : new[] { entry, exit })
            {
                var toLocal = Matrix4x4.TRS(end.position, end.rotation, Vector3.one).inverse;
                Vector3 normal = end.forward;
                foreach (var r in UnityEngine.Object.FindObjectsOfType<Renderer>())
                {
                    if (!(r is MeshRenderer) || !r.enabled || r.gameObject.layer == 16 || r.gameObject.layer == 18 ||
                        r.gameObject.layer == 20 || r.gameObject.layer == 30)
                        continue;
                    var m = r.sharedMaterial;
                    if (m == null || m.renderQueue < 2500)
                        continue;
                    var b = r.bounds;
                    Vector3 c = toLocal.MultiplyPoint3x4(b.center);
                    float depth = Mathf.Abs(b.extents.x * normal.x) + Mathf.Abs(b.extents.y * normal.y) + Mathf.Abs(b.extents.z * normal.z);
                    float reach = Mathf.Abs(c.z) + depth;
                    if (reach > 1f || Mathf.Abs(c.x) > width * 0.5f || Mathf.Abs(c.y) > height * 0.5f)
                        continue;
                    lift = Mathf.Max(lift, reach + 0.02f);
                }
            }
            openingLifts[id] = lift;
            if (lift > 0f)
                Logger.LogInfo($"[Portal] {portal.name} {id}: openings lifted {lift:F2} m past the transparent surfaces over them");
            return lift;
        }

        // Diagnostic: each portal within 30 m of the camera, once.
        private readonly HashSet<int> loggedNearbyPortals = new HashSet<int>();

        private void LogNearbyPortal(Behaviour portal, Transform entry, Transform exit, bool mirror, float width, float height, Vector3 eye)
        {
            if (Mathf.Min((entry.position - eye).sqrMagnitude, (exit.position - eye).sqrMagnitude) > 900f || !loggedNearbyPortals.Add(portal.GetInstanceID()))
                return;
            string path = portal.name;
            for (var t = portal.transform.parent; t != null; t = t.parent)
                path = t.name + "/" + path;
            Logger.LogInfo($"[Portal] near: {portal.GetInstanceID()} '{path}' {width:F2} x {height:F2}{(mirror ? " mirror" : "")}: " +
                           $"entry {entry.position} {entry.rotation.eulerAngles}, exit {exit.position} {exit.rotation.eulerAngles}");
            if (!mirror)
            {
                LogSeenThrough("past the exit's -Z side", exit, -1f);
                LogSeenThrough("past the exit's +Z side", exit, 1f);
                LogSeenThrough("past the entry's -Z side", entry, -1f);
                LogSeenThrough("past the entry's +Z side", entry, 1f);
            }
            LogOnOpening("entry", entry, width, height);
            if (!mirror)
                LogOnOpening("exit", exit, width, height);
        }

        // What lies on an opening: renderers whose bounds reach within 0.5 m of its plane inside
        // its rectangle (a pool's water over it), with their materials.
        private void LogOnOpening(string label, Transform end, float width, float height)
        {
            var toLocal = Matrix4x4.TRS(end.position, end.rotation, Vector3.one).inverse;
            int n = 0;
            foreach (var r in Resources.FindObjectsOfTypeAll<Renderer>())
            {
                if (r == null || !r.gameObject.scene.IsValid() || !(r is MeshRenderer || r is SkinnedMeshRenderer))
                    continue;
                Vector3 c = toLocal.MultiplyPoint3x4(r.bounds.center);
                Vector3 e = r.bounds.extents;
                float reach = e.magnitude;
                if (Mathf.Abs(c.z) > 0.5f + Mathf.Min(reach, 2f) || Mathf.Abs(c.x) > width * 0.5f + reach || Mathf.Abs(c.y) > height * 0.5f + reach)
                    continue;
                if (n++ >= 10)
                    break;
                string rpath = r.name;
                for (var t = r.transform.parent; t != null; t = t.parent)
                    rpath = t.name + "/" + rpath;
                var m = r.sharedMaterial;
                string mat = m == null ? "none" :
                    $"'{m.name}' shader '{(m.shader != null ? m.shader.name : "-")}' queue {m.renderQueue} keywords [{string.Join(" ", m.shaderKeywords)}]";
                Logger.LogInfo($"[Portal]   on the {label}: '{rpath}' local {c}, size {r.bounds.size}, layer {r.gameObject.layer}, active {r.gameObject.activeInHierarchy}, " +
                               $"enabled {r.enabled}: {mat}");
            }
        }

        // What a portal shows: renderers within 25 m on an end's -Z side (rays leave an end
        // that way), by layer and state, and the nearest few.
        private void LogSeenThrough(string label, Transform end, float side)
        {
            Vector3 origin = end.position, outward = side * end.forward;
            var found = new List<(float distance, Renderer renderer)>();
            foreach (var r in Resources.FindObjectsOfTypeAll<Renderer>())
            {
                if (r == null || !r.gameObject.scene.IsValid() || !(r is MeshRenderer || r is SkinnedMeshRenderer))
                    continue;
                Vector3 offset = r.bounds.center - origin;
                float distance = offset.magnitude;
                if (distance > 25f || Vector3.Dot(offset, outward) <= 0f)
                    continue;
                found.Add((distance, r));
            }
            var counts = new Dictionary<string, int>();
            foreach (var (_, r) in found)
            {
                string key = $"{LayerMask.LayerToName(r.gameObject.layer)}({r.gameObject.layer}) {(r.gameObject.activeInHierarchy ? "active" : "inactive")}{(r.enabled ? "" : " disabled")}{(r.forceRenderingOff ? " off" : "")}";
                counts.TryGetValue(key, out int n);
                counts[key] = n + 1;
            }
            var summary = new List<string>();
            foreach (var kv in counts)
                summary.Add($"{kv.Value} {kv.Key}");
            Logger.LogInfo($"[Portal]   seen {label}: {found.Count} renderers: {string.Join(", ", summary)}");
            found.Sort((a, b) => a.distance.CompareTo(b.distance));
            for (int i = 0; i < found.Count && i < 5; i++)
            {
                var r = found[i].renderer;
                string rpath = r.name;
                for (var t = r.transform.parent; t != null; t = t.parent)
                    rpath = t.name + "/" + rpath;
                Logger.LogInfo($"[Portal]     {found[i].distance:F1} m '{rpath}' layer {r.gameObject.layer}, active {r.gameObject.activeInHierarchy}, enabled {r.enabled}, " +
                               $"off {r.forceRenderingOff}, static batch {r.isPartOfStaticBatch}");
            }
        }

        private static float Priority(PortalPair p, Vector3 eye, Vector3 look)
        {
            float best = float.MaxValue;
            foreach (var end in new[] { p.entry, p.exit })
            {
                Vector3 offset = (Vector3)end.GetColumn(3) - eye;
                float distance = offset.magnitude;
                float radius = 0.5f * Mathf.Sqrt(p.width * p.width + p.height * p.height);
                // Behind the camera (its opening wholly so): after every end in front of it.
                bool behind = Vector3.Dot(offset, look) < -radius;
                best = Mathf.Min(best, distance + (behind ? 1e6f : 0f));
            }
            return best;
        }

        private Type limboType;
        private bool limboTypeSearched;
        private Behaviour limbo;
        private int limboSearchFrame = int.MinValue / 2;
        private System.Reflection.FieldInfo limboFakeCamStart, limboPlayerStartPos, limboLockMinimumHeight;
        private int loggedLimbo;

        public override bool GetRayPortalView(Camera worldCamera, out RayPortalView view)
        {
            view = default;
            if (!limboTypeSearched)
            {
                limboTypeSearched = true;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try { limboType = asm.GetType("LimboSkybox"); } catch { }
                    if (limboType != null) break;
                }
                if (limboType != null)
                {
                    const System.Reflection.BindingFlags all = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
                    limboFakeCamStart = limboType.GetField("fakeCamStart", all);
                    limboPlayerStartPos = limboType.GetField("playerStartPos", all);
                    limboLockMinimumHeight = limboType.GetField("lockMinimumHeight", all);
                }
            }
            if (limboType == null || limboFakeCamStart == null || limboPlayerStartPos == null || worldCamera == null)
                return false;
            int frame = Time.frameCount;
            if (limbo == null && frame - limboSearchFrame >= 120)
            {
                limboSearchFrame = frame;
                limbo = UnityEngine.Object.FindObjectOfType(limboType) as Behaviour;
            }
            if (limbo == null || !limbo.isActiveAndEnabled)
                return false;
            var fakeCamStart = limboFakeCamStart.GetValue(limbo) as Transform;
            if (fakeCamStart == null)
                return false;
            var playerStart = (Vector3)limboPlayerStartPos.GetValue(limbo);
            bool lockHeight = limboLockMinimumHeight == null || (bool)limboLockMinimumHeight.GetValue(limbo);
            Vector3 offset = (worldCamera.transform.position - playerStart) / 16f;
            if (lockHeight)
                offset.y = Mathf.Max(offset.y, 0f);
            Vector3 centre = fakeCamStart.position;
            var fakeCam = limbo.GetComponent<Camera>();
            float far = fakeCam != null ? fakeCam.farClipPlane : 1000f;
            view.exitCamera = centre + offset;
            view.scale = 1f / 16f;
            view.exitRegionCenter = centre;
            view.exitRegionRadius = Mathf.Min(far, Vector3.Distance(centre, playerStart) * 0.5f);
            if (loggedLimbo != limbo.GetInstanceID())
            {
                loggedLimbo = limbo.GetInstanceID();
                Logger.LogInfo($"[Limbo] view portal '{limbo.name}': fake start {centre}, player start {playerStart}, far {far:F0}, " +
                               $"exit region radius {view.exitRegionRadius:F1}, lock height {lockHeight}");
                if (fakeCam != null)
                {
                    var sky = fakeCam.GetComponent<Skybox>();
                    Logger.LogInfo($"[Limbo] fake camera: culling mask 0x{fakeCam.cullingMask:X8}, clear {fakeCam.clearFlags}, near {fakeCam.nearClipPlane}, " +
                                   $"skybox {(sky != null && sky.material != null ? sky.material.name : "none")}, scene skybox {(RenderSettings.skybox != null ? RenderSettings.skybox.name : "none")}");
                }
                // Diagnostic: every renderer about 100 m on each axis (a 50 m shell like the sky
                // sphere), whatever its material.
                foreach (var r in UnityEngine.Object.FindObjectsOfType<Renderer>())
                {
                    var e = r.bounds.extents;
                    if (Mathf.Min(e.x, Mathf.Min(e.y, e.z)) < 40f || Mathf.Max(e.x, Mathf.Max(e.y, e.z)) > 60f)
                        continue;
                    var m = r.sharedMaterial;
                    string mf = r.GetComponent<MeshFilter>()?.sharedMesh?.name ?? "?";
                    Logger.LogInfo($"[Limbo] 50 m shell '{r.name}' ({r.GetType().Name}, mesh '{mf}') layer {r.gameObject.layer}, enabled {r.enabled}, " +
                                   $"centre {r.bounds.center}, material '{m?.name}' shader '{m?.shader?.name}' queue {m?.renderQueue}, " +
                                   $"keywords [{(m != null ? string.Join(" ", m.shaderKeywords) : "")}]");
                }
                // Diagnostic: the sky meshes of the diorama (where they stand against its camera).
                foreach (var r in UnityEngine.Object.FindObjectsOfType<Renderer>())
                {
                    var m = r.sharedMaterial;
                    if (m == null || m.shader == null || !m.shader.name.StartsWith("Skybox/"))
                        continue;
                    var b = r.bounds;
                    Logger.LogInfo($"[Limbo] sky mesh '{r.name}' ({m.name}, {m.shader.name}) layer {r.gameObject.layer}, enabled {r.enabled}: " +
                                   $"bounds centre {b.center}, extents {b.extents}, camera {Vector3.Distance(b.center, centre):F1} m from its centre, " +
                                   $"position {r.transform.position}, scale {r.transform.lossyScale}");
                }
            }
            return true;
        }

        public override void OverrideMaterial(Material material, ref RemixMaterialManager.AlphaMode alphaMode, ref float alphaCutoff, ref Color albedoColor)
        {
            // Limbo's virtual trees' leaves (M_VirtualTree): ULTRAKILL/Master's TRANSPARENCY at
            // _Opacity 0.81, no depth writes. Blended, they are missing from the runtime's view
            // depth, so in front of a TV wall the replayed wall (ShaderPassThrough) takes them for
            // what it shows and shifts their colours; opaque, they occlude the wall as in the game,
            // where they draw after it. Their texture has no alpha.
            if (material.name.StartsWith("M_VirtualTree", StringComparison.Ordinal))
            {
                alphaMode = RemixMaterialManager.AlphaMode.Opaque;
                albedoColor.a = 1f;
                return;
            }
            if (!bloodStains.IsStainMaterial(material))
                return;
            // A level's own blood decals (BloodPlane, ULTRAKILL/Bloodstain_Fallback): the shader
            // keeps only texels of alpha 1 (discard otherwise) and writes the stain mask.
            if (IsBloodPlane(material))
            {
                alphaMode = RemixMaterialManager.AlphaMode.Cutout;
                alphaCutoff = 0.99f;
                albedoColor = new Color(UltrakillBloodStains.Tint.r, UltrakillBloodStains.Tint.g, UltrakillBloodStains.Tint.b, 1f);
                return;
            }
            alphaMode = RemixMaterialManager.AlphaMode.Blend;
            alphaCutoff = 0.05f;
            albedoColor = UltrakillBloodStains.Tint;
        }

        private static bool IsBloodPlane(Material material) =>
            material.shader != null && material.shader.name == "ULTRAKILL/Bloodstain_Fallback";

        // Bloodstain_Fallback samples its splatter atlas (five cells side by side) at the mesh's
        // own UVs times (0.2, 1): the first cell, whatever the material's tiling.
        public override void OverrideTextureTransform(Material material, ref Vector4 scaleOffset)
        {
            if (IsBloodPlane(material))
                scaleOffset = new Vector4(0.2f, 1f, 0f, 0f);
        }

        // Weapon colours (GunColorGetter): the weapon's materials are swapped for "colored" copies
        // (keyword CUSTOM_COLORS) and _CustomColor1/2/3 set through the renderer's property
        // block. ULTRAKILL/Master's CUSTOM_COLORS pixel shader (disassembled): the albedo is
        // _IDTex.a (a greyscale copy of the weapon; _MainTex is not read), lerped toward
        // itself x _CustomColorN.rgb by 0.8 x _IDTex.r/g/b in turn; the metal amount is
        // _IDTex.rgb x _CustomColorN.a, combined 1 - (1-a)(1-b)(1-c), and adds _CubeTex's
        // reflection tinted by the albedo: a metallic surface.
        private static readonly int IDTex = Shader.PropertyToID("_IDTex");
        private static readonly int[] CustomColors =
        {
            Shader.PropertyToID("_CustomColor1"), Shader.PropertyToID("_CustomColor2"), Shader.PropertyToID("_CustomColor3")
        };

        public override bool GetColorMask(Material material, MaterialPropertyBlock block, out RemixMaterialManager.ColorMask colorMask)
        {
            colorMask = default;
            if (!material.IsKeywordEnabled("CUSTOM_COLORS") || !material.HasProperty(IDTex) || !(material.GetTexture(IDTex) is Texture2D mask))
                return false;
            var colors = new Color[3];
            for (int i = 0; i < 3; i++)
            {
                Color c = block != null && !block.isEmpty ? block.GetColor(CustomColors[i]) : Color.clear;
                if (c == Color.clear)
                    c = material.HasProperty(CustomColors[i]) ? material.GetColor(CustomColors[i]) : Color.white;
                colors[i] = c;
            }
            colorMask = new RemixMaterialManager.ColorMask
            {
                mask = mask,
                color1 = colors[0], color2 = colors[1], color3 = colors[2],
                weight = 0.8f,
                baseInMaskAlpha = true,
                metalFromColorAlpha = true,
                metalRoughness = 0.2f
            };
            return true;
        }

        // Firestarter oil on an enemy: EnemyIdentifier.AddFlammable sets _OiledAmount (fuel / 5)
        // in each of its renderers' property blocks. ULTRAKILL/Master's ENEMY variant fills the
        // enemy with oil from the bottom up as it rises (with _SandTex noise), the oil 75% of the
        // colour: _OilSlick's interference ramp, black facing the camera and rainbow towards the
        // rim. Path-traced, that is a dark, smooth surface under a thin film.
        // Coverage: the oil coats where h - amount + 0.4 n < 0 (else the bare colour), h the
        // mesh's baked height (TEXCOORD2.y, -1 at the feet to 1 at the top) * 0.5 + 0.5, n
        // screen-space _SandTex noise in -1.5..0.5 (mean -0.5): in TEXCOORD2.y units, below
        // 2 amount - 0.6 give or take 0.8. The coated triangles are split off (partial film), in
        // tenths of the amount (one mesh per tenth); a full film where a mesh can't be split.
        private static readonly int OiledAmount = Shader.PropertyToID("_OiledAmount");

        // Sandified enemies (EnemySimplifier: _Sanded 1 in the block): ULTRAKILL/Master's ENEMY
        // pass projects the global _SandTex onto them from the dominant world axis, at a quarter
        // scale with a slow scroll, and blends it by a rim term m (0 face-on, 1 at the edges):
        // lerp(albedo, sand, 0.25 + 0.75 sqrt m) + 0.75 sqrt m x sand. Baked as a coat at the rim
        // term's middle (sqrt m = 0.5): 62.5% sand over the albedo, plus 0.375 x sand, tiled
        // twice over the enemy's UVs (about the shader's quarter scale on a body).
        private static readonly int Sanded = Shader.PropertyToID("_Sanded");
        private static readonly int SandTex = Shader.PropertyToID("_SandTex");

        public override bool GetCoat(Material material, MaterialPropertyBlock block, out RemixMaterialManager.Coat coat)
        {
            coat = default;
            if (block == null || block.isEmpty || block.GetFloat(Sanded) != 1f || !material.IsKeywordEnabled("ENEMY") ||
                material.shader == null || material.shader.name != "ULTRAKILL/Master")
                return false;
            coat = new RemixMaterialManager.Coat
            {
                texture = Shader.GetGlobalTexture(SandTex) as Texture2D,
                cover = 0.625f,
                add = 0.375f,
                tiling = 2f
            };
            return coat.texture != null;
        }

        public override bool GetIridescence(Material material, MaterialPropertyBlock block, out RemixMaterialManager.Iridescence film)
        {
            film = default;
            if (block == null || block.isEmpty || !material.IsKeywordEnabled("ENEMY"))
                return false;
            float amount = Mathf.Ceil(Mathf.Clamp01(block.GetFloat(OiledAmount)) * 10f) / 10f;
            if (amount <= 0f || material.shader == null || material.shader.name != "ULTRAKILL/Master")
                return false;
            // Stronger than a real oil film (index 1.45, a few percent reflectance), so the rainbow
            // reads as boldly as the game's ramp; the flow swirls the thickness through 100-900 nm.
            film = new RemixMaterialManager.Iridescence
            {
                strength = 1f,
                filmIndex = 1.9f,
                thickness = 500f,
                flowAmplitude = 400f,
                flowScale = 0.44f,
                flowSpeed = 0.3f,
                structural = 0.9f, // the rainbow in the diffuse colour too, as the game's unlit oil
                albedoScale = 0.28f,
                roughness = 0.05f,
                partial = amount < 1f,
                coverageChannel = 2,
                coverageAxis = 1,
                coverageBelow = 2f * amount - 0.6f,
                coverageNoise = 0.35f
            };
            return true;
        }

        public override bool IsDynamicObject(MeshFilter filter, Renderer renderer)
        {
            if (dynamicTypes == null)
            {
                var found = new List<Type>();
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    foreach (var name in dynamicTypeNames)
                    {
                        Type t = null;
                        try { t = asm.GetType(name); } catch { }
                        if (t != null && !found.Contains(t))
                            found.Add(t);
                    }
                }
                dynamicTypes = found.ToArray();
            }

            for (int i = 0; i < dynamicTypes.Length; i++)
            {
                if (renderer.GetComponentInParent(dynamicTypes[i]) != null)
                    return true;
            }

            if (ContainsAny(filter.gameObject.name, dynamicObjectNames))
                return true;
            var parent = filter.transform.parent;
            return parent != null && ContainsAny(parent.name, dynamicParentNames);
        }

        // Checkpoint graphics, the doors' "NoPass" skull locks and weapon pickups (a pickup
        // deactivates its root once taken) are toggled by deactivation.
        public override bool FollowsActiveState(MeshFilter filter, Renderer renderer)
        {
            if (followTypes == null)
            {
                var found = new List<Type>();
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    foreach (var name in followTypeNames)
                    {
                        Type t = null;
                        try { t = asm.GetType(name); } catch { }
                        if (t != null && !found.Contains(t))
                            found.Add(t);
                    }
                }
                followTypes = found.ToArray();
            }
            for (int i = 0; i < followTypes.Length; i++)
            {
                if (renderer.GetComponentInParent(followTypes[i]) != null)
                    return true;
            }

            var parent = filter.transform.parent;
            string parentName = parent != null ? parent.name : null;
            var material = renderer.sharedMaterial;
            // Additive glows (light shafts) come and go with the lights they belong to: Flicker
            // toggles its flickerDisableObjects with the lamp.
            if (material != null && IsAdditiveGlow(material))
                return true;
            return ContainsAny(filter.gameObject.name, followNames) || ContainsAny(parentName, followNames) ||
                   (material != null && ContainsAny(material.name, noPassNames));
        }

        private bool IsAdditiveGlow(Material material)
        {
            string shader = material.shader != null ? material.shader.name : "";
            if (shader.IndexOf("Additive", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            return ReadsBlendProperties(shader) && material.HasProperty("_DstBlend") &&
                   Mathf.RoundToInt(material.GetFloat("_DstBlend")) == (int)UnityEngine.Rendering.BlendMode.One;
        }

        private static readonly string[] followTypeNames = { "CheckPoint", "WeaponPickUp", "AltPickUp", "DualWieldPickup" };
        private static readonly string[] followNames = { "CheckPoint", "NoPass" };
        private static readonly string[] noPassNames = { "NoPass" };

        private static bool ContainsAny(string name, string[] parts)
        {
            if (string.IsNullOrEmpty(name)) return false;
            foreach (var part in parts)
                if (name.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            return false;
        }
    }
}
