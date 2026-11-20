using System;
using BepInEx.Logging;

namespace UnityRemix
{
    /// <summary>
    /// Renders plugin settings inside the Remix developer menu "Plugin" tab.
    /// Registered as a draw callback via <see cref="RemixImGui"/>.
    ///
    /// Sub-tabs, settings first and read-outs last:
    ///   Game                  the active game profile's own options
    ///   Scene                 what is captured from Unity, culling, particles, camera
    ///   Lighting & Materials  lights, sky, fog, water and glass, generated PBR
    ///   Window & Performance  single-window presentation, frame pacing
    ///   Diagnostics           what was detected: cameras, layers, particles, fog, water
    /// Labels ending in "(restart)" or "(scene reload)" apply only then.
    /// </summary>
    public class RemixSettingsUI
    {
        private readonly ManualLogSource _log;
        private readonly UnityRemixPlugin _plugin;
        private bool _initialized;

        // Cached state for ImGui controls (avoids per-frame alloc)
        private float _maxRenderDistance;
        private float _particleMaxDistance;
        private bool _enableParticleDistanceCulling;
        private bool _rasterParticles;
        private bool _enableSkybox;
        private int _skyboxFiltering;
        private float _lightIntensityMultiplier;
        private float _distantLightIntensityMultiplier;
        private bool _particleLights;
        private float _particleLightIntensity;
        private bool _particleLightPerParticle;
        private int _maxParticleLights;
        private int _targetFPS;
        private bool _enableGameGeometry;
        private bool _enableDistanceCulling;
        private bool _enableVisibilityCulling;
        private bool _detectScrollingTextures = true;
        private bool _enableLights;
        private bool _captureStaticMeshes;
        private bool _captureSkinnedMeshes;
        private bool _captureParticles;
        private bool _hardwareSkinning;
        private bool _captureTextures;
        private bool _captureMaterials;
        private bool _enableSceneScan;
        private bool _sceneScanActiveOnly;
        private bool _persistDisabledRenderers;
        private bool _singleWindow;
        private bool _disableInEngineRendering;
        private bool _autoDetectUI;
        private bool _singleWindowUIOverlay;
        private bool _hideUIOnRemixMenu;
        private bool _uiOverlayClearBlack;
        private string _selectedCameraName;
        private int _engineFPSLimit;
        private bool _preventSlowMotion;
        private int _staticMeshFrameSkip;

        private const int TableFlags = RemixImGui.TableFlags_Borders | RemixImGui.TableFlags_RowBg | RemixImGui.TableFlags_SizingStretchProp;

        public RemixSettingsUI(ManualLogSource log, UnityRemixPlugin plugin)
        {
            _log = log;
            _plugin = plugin;
        }

        /// <summary>
        /// Called from the Remix render thread when the "Plugin" tab is active.
        /// Draws directly into the tab content area — no Begin/End window needed.
        /// </summary>
        public void Draw(IntPtr userData)
        {
            if (!_initialized)
            {
                SyncFromConfig();
                _initialized = true;
            }

            if (RemixImGui.BeginTabBar("##unityrtx_tabs"))
            {
                var game = Games.GameProfiles.Active;
                if (game != null && RemixImGui.BeginTabItem(game.Name))
                {
                    DrawGameTab(game);
                    RemixImGui.EndTabItem();
                }
                if (RemixImGui.BeginTabItem("Scene"))
                {
                    DrawSceneTab();
                    RemixImGui.EndTabItem();
                }
                if (RemixImGui.BeginTabItem("Lighting & Materials"))
                {
                    DrawLightingTab();
                    RemixImGui.EndTabItem();
                }
                if (RemixImGui.BeginTabItem("Window & Performance"))
                {
                    DrawWindowTab();
                    RemixImGui.EndTabItem();
                }
                if (RemixImGui.BeginTabItem("Diagnostics"))
                {
                    DrawDiagnosticsTab();
                    RemixImGui.EndTabItem();
                }
                RemixImGui.EndTabBar();
            }

            RemixImGui.Separator();
            RemixImGui.Spacing();
            if (RemixImGui.Button("Save Settings", 130, 0))
                _plugin.SaveConfig();
            if (RemixImGui.IsItemHovered())
                RemixImGui.SetTooltip("Writes the settings to the plugin's config file.");
            RemixImGui.SameLine();
            if (RemixImGui.Button("Reload", 80, 0))
                SyncFromConfig();
            if (RemixImGui.IsItemHovered())
                RemixImGui.SetTooltip("Shows the values currently in effect again (after editing the config file).");
        }

        // --- Helpers -------------------------------------------------------------------------

        private static void Tooltip(string text)
        {
            if (RemixImGui.IsItemHovered())
                RemixImGui.SetTooltip(text);
        }

        private static void Note(string text) => RemixImGui.TextColored(0.7f, 0.7f, 0.7f, 1.0f, text);

        private void Toggle(string label, string key, ref bool value, string tooltip = null)
        {
            if (RemixImGui.Checkbox(label, ref value))
                _plugin.SetConfig(key, value);
            if (tooltip != null)
                Tooltip(tooltip);
        }

        private static void Section(string title)
        {
            RemixImGui.Spacing();
            RemixImGui.TextColored(0.4f, 0.8f, 1.0f, 1.0f, title);
            RemixImGui.Separator();
        }

        // --- Game ----------------------------------------------------------------------------

        private void DrawGameTab(Games.GameProfile game)
        {
            if (!game.DrawSettings())
                Note($"{game.Name} has no options of its own.");
        }

        // --- Scene ---------------------------------------------------------------------------

        private void DrawSceneTab()
        {
            Section("Geometry");
            Toggle("Game geometry", "EnableGameGeometry", ref _enableGameGeometry,
                "Draws the game's meshes. Off: only lights, sky and effects (for comparisons).");
            Toggle("Static meshes", "CaptureStaticMeshes", ref _captureStaticMeshes);
            Toggle("Skinned meshes", "CaptureSkinnedMeshes", ref _captureSkinnedMeshes);
            Toggle("Hardware skinning (scene reload)", "HardwareSkinning", ref _hardwareSkinning,
                "Skins characters on the GPU from their bones rather than baking their meshes each frame.");
            Toggle("Scene scan", "EnableSceneScan", ref _enableSceneScan,
                "Finds the level's geometry once at load rather than every frame.");
            if (_enableSceneScan)
            {
                RemixImGui.Indent();
                Toggle("Active renderers only (scene reload)", "ActiveRenderersOnly", ref _sceneScanActiveOnly,
                    "Only geometry from active renderers. Prevents ghost geometry\nfrom inactive scene variants (e.g. Stanley Parable).");
                RemixImGui.Unindent();
            }
            Toggle("Persist disabled renderers", "PersistDisabledRenderers", ref _persistDisabledRenderers,
                "Keeps drawing meshes after the game deactivates them.\nFor games that temporarily hide visible geometry (e.g. ULTRAKILL CyberGrind).");
            Toggle("Detect scrolling textures", "DetectScrollingTextures", ref _detectScrollingTextures,
                "Captures renderers with animated or scrolling UVs (water, conveyors, lava) with their live offsets.\nOff: treated as ordinary static geometry.");
            Toggle("Textures", "CaptureTextures", ref _captureTextures, "Off: untextured (for debugging).");
            Toggle("Materials", "CaptureMaterials", ref _captureMaterials, "Off: a neutral material everywhere (for debugging).");

            Section("Culling");
            Toggle("Visibility culling", "UseVisibilityCulling", ref _enableVisibilityCulling,
                "Skips renderers Unity reports invisible. Off: off-screen geometry still casts shadows and shows in reflections.");
            Toggle("Distance culling", "EnableDistanceCulling", ref _enableDistanceCulling);
            if (_enableDistanceCulling)
            {
                RemixImGui.Indent();
                if (RemixImGui.SliderFloat("Max distance", ref _maxRenderDistance, 10f, 10000f))
                    _plugin.SetConfig("MaxRenderDistance", _maxRenderDistance);
                RemixImGui.Unindent();
            }

            Section("Particles");
            Toggle("Particle systems", "CaptureParticles", ref _captureParticles,
                "Captures Unity particle systems and trails.");
            if (_captureParticles)
            {
                RemixImGui.Indent();
                Toggle("Raster particles", "RasterParticles", ref _rasterParticles,
                    "Draws particles with OpenRemix's raster backend over the path-traced image.\nCheaper with many particles; they are not lit, reflected or shadowed by the path tracer.");
                Toggle("Distance culling##particles", "EnableParticleDistanceCulling", ref _enableParticleDistanceCulling,
                    "Culls particle systems farther away than the max distance.");
                if (_enableParticleDistanceCulling)
                {
                    RemixImGui.Indent();
                    if (RemixImGui.SliderFloat("Max distance##particles", ref _particleMaxDistance, 5f, 500f, "%.0f"))
                        _plugin.SetConfig("ParticleMaxDistance", _particleMaxDistance);
                    RemixImGui.Unindent();
                }
                RemixImGui.Unindent();
            }

            Section("Camera");
            DrawCameraPicker();
        }

        private void DrawCameraPicker()
        {
            var camHandler = _plugin.CameraHandler;
            if (camHandler == null)
            {
                Note("Camera handler not initialized.");
                return;
            }

            bool isAuto = string.IsNullOrEmpty(_selectedCameraName);
            if (RemixImGui.RadioButton("Auto-detect", isAuto))
            {
                _selectedCameraName = "";
                _plugin.SetConfig("CameraName", "");
            }
            Tooltip("The camera the path tracer follows: the game's main camera, or one picked below.");

            var snapshots = camHandler.CameraSnapshots;
            if (snapshots.Length == 0)
            {
                Note("No cameras found.");
                return;
            }

            if (RemixImGui.BeginTable("##cameras", 3, TableFlags))
            {
                RemixImGui.TableSetupColumn("", 0, 0.05f);
                RemixImGui.TableSetupColumn("Name", 0, 0.75f);
                RemixImGui.TableSetupColumn("Depth", 0, 0.2f);
                RemixImGui.TableHeadersRow();
                for (int i = 0; i < snapshots.Length; i++)
                {
                    var snap = snapshots[i];
                    bool selected = !isAuto && _selectedCameraName == snap.Name;
                    RemixImGui.TableNextRow();
                    RemixImGui.TableSetColumnIndex(0);
                    if (RemixImGui.RadioButton($"##cam_{i}", selected))
                    {
                        _selectedCameraName = snap.Name;
                        _plugin.SetConfig("CameraName", snap.Name);
                    }
                    RemixImGui.TableSetColumnIndex(1);
                    RemixImGui.Text(snap.Name);
                    RemixImGui.TableSetColumnIndex(2);
                    RemixImGui.Text(snap.Depth.ToString("F0"));
                }
                RemixImGui.EndTable();
            }
        }

        // --- Lighting & Materials ------------------------------------------------------------

        private void DrawLightingTab()
        {
            Section("Lights");
            Toggle("Game lights", "EnableLights", ref _enableLights, "Converts the game's Unity lights.");
            if (RemixImGui.SliderFloat("Intensity multiplier", ref _lightIntensityMultiplier, 0.01f, 100f))
                _plugin.SetConfig("IntensityMultiplier", _lightIntensityMultiplier);
            Tooltip("Scales every converted light but the distant ones.");
            if (RemixImGui.SliderFloat("Distant light multiplier", ref _distantLightIntensityMultiplier, 0.01f, 100f))
                _plugin.SetConfig("DistantIntensityMultiplier", _distantLightIntensityMultiplier);
            Tooltip("Scales the game's directional lights (sun, moon) in place of the intensity multiplier.");
            Toggle("Particle lights", "ParticleLights", ref _particleLights,
                "With raster particles, gives each additive particle system (flames, glows) a point light so it lights the scene.");
            if (_particleLights)
            {
                if (RemixImGui.SliderFloat("Particle light intensity", ref _particleLightIntensity, 0f, 20f))
                    _plugin.SetConfig("ParticleLightIntensity", _particleLightIntensity);
                Tooltip("Scales the particle lights.");
                Toggle("Light per particle", "ParticleLightPerParticle", ref _particleLightPerParticle,
                    "A light for each particle (the strongest and nearest, up to the most below) rather than one for each system.");
                if (RemixImGui.SliderInt("Most particle lights", ref _maxParticleLights, 1, 1024))
                    _plugin.SetConfig("MaxParticleLights", _maxParticleLights);
                Tooltip("Most particle lights drawn a frame: the strongest by intensity over squared distance.");
            }

            Section("Sky");
            Toggle("Skybox (scene reload)", "EnableSkybox", ref _enableSkybox,
                "Detects the Unity skybox (cubemap, panoramic or solid colour) and renders it as a dome light.");
            if (_enableSkybox)
            {
                RemixImGui.Indent();
                string[] filterNames = { "Linear (smooth)", "Closest (pixelated / retro)" };
                string preview = _skyboxFiltering >= 0 && _skyboxFiltering < filterNames.Length ? filterNames[_skyboxFiltering] : filterNames[0];
                if (RemixImGui.BeginCombo("Filtering", preview))
                {
                    for (int i = 0; i < filterNames.Length; i++)
                        if (RemixImGui.Selectable(filterNames[i], _skyboxFiltering == i))
                        {
                            _skyboxFiltering = i;
                            _plugin.SetConfig("SkyboxFiltering", _skyboxFiltering);
                        }
                    RemixImGui.EndCombo();
                }
                Tooltip("Linear: smooth bilinear interpolation.\nClosest: keeps sharp retro pixels.");
                if (_plugin.SkyboxManager != null)
                    Note($"Status: {_plugin.SkyboxManager.StatusText}");
                RemixImGui.Unindent();
            }

            Section("Fog");
            var fog = _plugin.FogManager;
            if (fog == null)
                Note("Fog manager not initialized.");
            else
            {
                bool enableFog = fog.ConfigEnableFog.Value;
                if (RemixImGui.Checkbox("Game fog as volumetrics", ref enableFog))
                {
                    fog.ConfigEnableFog.Value = enableFog;
                    fog.MarkDirty();
                }
                Tooltip("Follows Unity's fog (RenderSettings.fog) with OpenRemix's path-traced volumetrics.");
                if (enableFog)
                {
                    RemixImGui.Indent();
                    float densMult = fog.ConfigFogDensityMultiplier.Value;
                    if (RemixImGui.SliderFloat("Density multiplier", ref densMult, 0.01f, 10.0f))
                    {
                        fog.ConfigFogDensityMultiplier.Value = densMult;
                        fog.MarkDirty();
                    }
                    Tooltip("Thickens or thins the game's fog. Exponential fog: its density (1 = Unity's x0.05). " +
                            "Linear fog (start/end distances): its ramp from start to fully fogged, divided by this.");
                    float distMult = fog.ConfigFogDistanceMultiplier.Value;
                    if (RemixImGui.SliderFloat("Distance multiplier", ref distMult, 0.1f, 5.0f))
                    {
                        fog.ConfigFogDistanceMultiplier.Value = distMult;
                        fog.MarkDirty();
                    }
                    Tooltip("Scales linear fog start and end distances.");
                    RemixImGui.Unindent();
                }
            }

            Section("Light shafts");
            if (LightShafts.Enabled == null)
                Note("Light shafts not initialized.");
            else if (RemixAPI.DrawVolumeShaftsEXTFunc == null)
                Note("This remix.dll cannot draw light shafts as volumes (remixapi_DrawVolumeShaftsEXT).");
            else
            {
                bool shafts = LightShafts.Enabled.Value;
                if (RemixImGui.Checkbox("Light shafts as volumes", ref shafts))
                    LightShafts.Enabled.Value = shafts;
                Tooltip("The game's fake light shafts (additive cones and boxes under windows and lamps) as glowing air: " +
                        "brighter seen through more of them, cut softly by walls. Off: the game's sheets.");
                if (shafts)
                {
                    RemixImGui.Indent();
                    float strength = LightShafts.Brightness.Value;
                    if (RemixImGui.SliderFloat("Strength##shafts", ref strength, 0f, 10f))
                        LightShafts.Brightness.Value = strength;
                    Tooltip("How brightly the shafts glow; 1 matches the game's sheet seen side-on.");
                    float softness = LightShafts.Softness.Value;
                    if (RemixImGui.SliderFloat("Edge softness##shafts", ref softness, 0f, 1f))
                        LightShafts.Softness.Value = softness;
                    Tooltip("The part of a shaft's half width over which it fades out at its sides.");
                    bool shadows = LightShafts.Shadows.Value;
                    if (RemixImGui.Checkbox("Shadows##shafts", ref shadows))
                        LightShafts.Shadows.Value = shadows;
                    Tooltip("What stands in a shaft casts a shadow down it, from the window or lamp it starts at. " +
                            "Costs a shadow ray per sample (8 along each camera ray through a shaft).");
                    RemixImGui.Unindent();
                }
            }

            Section("Water & glass");
            var water = _plugin.WaterManager;
            if (water == null)
                Note("Water manager not initialized.");
            else
            {
                bool enableTrans = water.ConfigEnableTranslucents.Value;
                if (RemixImGui.Checkbox("Translucent materials", ref enableTrans))
                    water.ConfigEnableTranslucents.Value = enableTrans;
                Tooltip("Physical translucency: water with Snell's window, total internal reflection and absorption;\nglass with specular transmission and Fresnel reflection.");

                bool enableWater = water.ConfigEnableWater.Value;
                if (RemixImGui.Checkbox("Water", ref enableWater))
                    water.ConfigEnableWater.Value = enableWater;
                Tooltip("Detects water surfaces (layer 4, shader keywords, names) and makes them translucent liquid.");
                if (enableWater)
                {
                    RemixImGui.Indent();
                    float waterIOR = water.ConfigWaterIOR.Value;
                    if (RemixImGui.SliderFloat("Refractive index##water", ref waterIOR, 1.0f, 2.0f))
                        water.ConfigWaterIOR.Value = waterIOR;
                    Tooltip("Water's index of refraction (1.333).");
                    float clarity = water.ConfigWaterClarity.Value;
                    if (RemixImGui.SliderFloat("Clarity distance (m)", ref clarity, 1.0f, 100.0f))
                        water.ConfigWaterClarity.Value = clarity;
                    Tooltip("Distance over which water absorbs its colour (15 m).");
                    float bloodClarity = water.ConfigBloodClarity.Value;
                    if (RemixImGui.SliderFloat("Blood clarity distance (m)", ref bloodClarity, 0.2f, 20.0f))
                        water.ConfigBloodClarity.Value = bloodClarity;
                    Tooltip("Likewise for blood and other dense fluids (2 m); lower is a deeper crimson.");
                    bool enableMedium = water.ConfigEnableUnderwaterMedium.Value;
                    if (RemixImGui.Checkbox("Underwater medium", ref enableMedium))
                        water.ConfigEnableUnderwaterMedium.Value = enableMedium;
                    Tooltip("Volumetric absorption and Snell's window while the camera is underwater.");
                    RemixImGui.Unindent();
                }

                bool enableGlass = water.ConfigEnableGlass.Value;
                if (RemixImGui.Checkbox("Glass", ref enableGlass))
                    water.ConfigEnableGlass.Value = enableGlass;
                Tooltip("Detects glass, windows, visors and transparent glossy materials as thin transmissive sheets.");
                if (enableGlass)
                {
                    RemixImGui.Indent();
                    float glassIOR = water.ConfigGlassIOR.Value;
                    if (RemixImGui.SliderFloat("Refractive index##glass", ref glassIOR, 1.0f, 2.5f))
                        water.ConfigGlassIOR.Value = glassIOR;
                    Tooltip("Glass's index of refraction (1.52).");
                    RemixImGui.Unindent();
                }
            }

            Section("Generated PBR (experimental)");
            var matMgr = _plugin.MaterialManager;
            if (matMgr == null)
                Note("Material manager not initialized.");
            else
            {
                bool autoPbr = (matMgr.ConfigAutoPBR != null && matMgr.ConfigAutoPBR.Value) ||
                               (matMgr.ConfigAutoNormalMaps != null && matMgr.ConfigAutoNormalMaps.Value);
                if (RemixImGui.Checkbox("Infer roughness, metalness and normal maps", ref autoPbr))
                {
                    if (matMgr.ConfigAutoPBR != null)
                        matMgr.ConfigAutoPBR.Value = autoPbr;
                    if (matMgr.ConfigAutoNormalMaps != null)
                        matMgr.ConfigAutoNormalMaps.Value = autoPbr;
                    _plugin.OnAutoPbrSettingChanged();
                }
                Tooltip("For surfaces without PBR maps (retro and non-PBR games); materials update live.");
                if (autoPbr)
                {
                    RemixImGui.Indent();
                    float strength = matMgr.ConfigAutoNormalStrength != null ? matMgr.ConfigAutoNormalStrength.Value : 1.0f;
                    if (RemixImGui.SliderFloat("Normal strength", ref strength, 0.1f, 3.0f))
                    {
                        if (matMgr.ConfigAutoNormalStrength != null)
                            matMgr.ConfigAutoNormalStrength.Value = strength;
                        _plugin.OnAutoPbrSettingChanged();
                    }
                    Tooltip("Bump depth of the generated normal maps (1.0).");
                    float variation = matMgr.ConfigAutoPbrVariation != null ? matMgr.ConfigAutoPbrVariation.Value : 1.0f;
                    if (RemixImGui.SliderFloat("Variation", ref variation, 0f, 2f))
                    {
                        if (matMgr.ConfigAutoPbrVariation != null)
                            matMgr.ConfigAutoPbrVariation.Value = variation;
                        _plugin.OnAutoPbrSettingChanged();
                    }
                    Tooltip("How much materials of one kind differ, each texture by its own stable amount (1.0):\n" +
                            "roughness up to +/-0.18, metals' metalness down to 0.15 less.");
                    float roughBias = matMgr.ConfigAutoPbrRoughnessBias != null ? matMgr.ConfigAutoPbrRoughnessBias.Value : 0f;
                    if (RemixImGui.SliderFloat("Roughness bias", ref roughBias, -1f, 1f))
                    {
                        if (matMgr.ConfigAutoPbrRoughnessBias != null)
                            matMgr.ConfigAutoPbrRoughnessBias.Value = roughBias;
                        _plugin.OnAutoPbrSettingChanged();
                    }
                    Tooltip("Added to every generated roughness (0): above 0 rougher, below 0 glossier.");
                    float metalBias = matMgr.ConfigAutoPbrMetallicBias != null ? matMgr.ConfigAutoPbrMetallicBias.Value : 0f;
                    if (RemixImGui.SliderFloat("Metallic bias", ref metalBias, -1f, 1f))
                    {
                        if (matMgr.ConfigAutoPbrMetallicBias != null)
                            matMgr.ConfigAutoPbrMetallicBias.Value = metalBias;
                        _plugin.OnAutoPbrSettingChanged();
                    }
                    Tooltip("Added to every generated metalness (0): above 0 more metallic, below 0 less.");
                    RemixImGui.Unindent();
                }
            }
        }

        // --- Window & Performance ------------------------------------------------------------

        private void DrawWindowTab()
        {
            Section("Window");
            Toggle("Hide game UI with the Remix menu", "HideUIOnRemixMenu", ref _hideUIOnRemixMenu,
                "Hides the game's UI while this menu (Alt+X) is open.");
            Toggle("Single window (restart)", "SingleWindow", ref _singleWindow,
                "Shows Remix inside the game's window instead of a window of its own.");
            if (_singleWindow)
            {
                RemixImGui.Indent();
                Toggle("Stop Unity rendering the 3D scene (restart)", "DisableInEngineRendering", ref _disableInEngineRendering,
                    "Unity skips its own rasterization of the world, which Remix replaces.");
                Toggle("Auto-detect UI (restart)", "AutoDetectUI", ref _autoDetectUI,
                    "Finds the game's UI and HUD cameras and canvases.");
                Toggle("UI overlay (restart)", "SingleWindowUIOverlay", ref _singleWindowUIOverlay,
                    "Draws the detected UI over the Remix image with OpenRemix's overlay.");
                if (_singleWindowUIOverlay)
                {
                    RemixImGui.Indent();
                    Toggle("Black is transparent", "UIOverlayClearBlack", ref _uiOverlayClearBlack,
                        "Pure opaque black UI pixels show the Remix image instead. For games whose UI camera " +
                        "clears to opaque black; leave off otherwise, or black text and outlines disappear.");
                    RemixImGui.Unindent();
                }
                RemixImGui.Unindent();
            }

            Section("Frame pacing");
            if (RemixImGui.DragInt("Remix frame cap", ref _targetFPS, 1, 0, 500))
                _plugin.SetConfig("TargetFPS", _targetFPS);
            Tooltip("Frame rate cap for Remix's render thread; 0 uncapped.");
            if (_singleWindow)
            {
                string previewFps = _engineFPSLimit == 0 ? "Uncapped" : $"{_engineFPSLimit} FPS";
                if (RemixImGui.BeginCombo("Unity update cap (restart)", previewFps))
                {
                    int[] fpsOptions = { 0, 30, 60, 90, 120 };
                    string[] fpsLabels = { "Uncapped", "30 FPS (heavy scenes)", "60 FPS (recommended)", "90 FPS", "120 FPS" };
                    for (int i = 0; i < fpsOptions.Length; i++)
                        if (RemixImGui.Selectable(fpsLabels[i], _engineFPSLimit == fpsOptions[i]))
                        {
                            _engineFPSLimit = fpsOptions[i];
                            _plugin.SetConfig("EngineFPSLimit", _engineFPSLimit);
                        }
                    RemixImGui.EndCombo();
                }
                Tooltip("Caps Unity's own update rate in single-window mode; Remix keeps rendering at full speed.\nLower caps prevent CPU bottlenecks and slowdowns.");
            }
            Toggle("Prevent slow motion", "PreventSlowMotion", ref _preventSlowMotion,
                "Raises Time.maximumDeltaTime so physics and game logic don't slow down when frame times spike.");
            if (RemixImGui.SliderInt("Static mesh rescan interval", ref _staticMeshFrameSkip, 1, 4))
                _plugin.SetConfig("StaticMeshFrameSkip", _staticMeshFrameSkip);
            Tooltip("Rescans static renderers every N frames rather than every frame (1: every frame).");
        }

        // --- Diagnostics ---------------------------------------------------------------------

        private void DrawDiagnosticsTab()
        {
            if (RemixImGui.CollapsingHeader("Renderers & layers"))
                DrawRendererTable();
            if (RemixImGui.CollapsingHeader("Particles"))
                DrawParticleStats();
            if (RemixImGui.CollapsingHeader("UI cameras"))
                DrawUICameras();
            if (RemixImGui.CollapsingHeader("Fog"))
                DrawFogState();
            if (RemixImGui.CollapsingHeader("Water, glass & materials"))
                DrawMaterialStats();
        }

        private void DrawRendererTable()
        {
            var fc = _plugin.FrameCapture;
            if (fc == null)
            {
                Note("Frame capture not initialized.");
                return;
            }

            var layers = fc.LayerSnapshots;
            var renderers = fc.RendererSnapshots;
            var scanner = _plugin.SceneMeshScanner;
            var scannedCounts = scanner?.GetLayerCounts();
            var stats = fc.GetStaticGeometryStats(scanner);

            RemixImGui.Text($"{layers.Length} layers, {renderers.Length} active renderers");
            RemixImGui.Text($"Static meshes: raw {stats.RawStaticMeshes}, visible {stats.DedupedStaticMeshes}, suppressed {stats.SuppressedStaticMeshes}");
            RemixImGui.Text($"Renderers: raw {stats.RawStaticRenderers}, unique {stats.DedupedStaticRenderers}, duplicates {stats.SuppressedStaticRenderers}");
            RemixImGui.Text($"Scene scan: raw {stats.RawSceneScanInstances}, visible {stats.DedupedSceneScanInstances}, suppressed {stats.SuppressedSceneScanInstances}");
            RemixImGui.Text($"Unique visible static objects: {stats.DedupedVisibleStaticTotal}");
            Note("Untick a layer or renderer to hide it.");

            if (layers.Length == 0)
                return;

            if (RemixImGui.BeginTable("##layers", 4, TableFlags, 0, 300))
            {
                RemixImGui.TableSetupColumn("On", 0, 0.06f);
                RemixImGui.TableSetupColumn("Layer", 0, 0.50f);
                RemixImGui.TableSetupColumn("Active", 0, 0.22f);
                RemixImGui.TableSetupColumn("Scanned", 0, 0.22f);
                RemixImGui.TableHeadersRow();

                for (int i = 0; i < layers.Length; i++)
                {
                    var layer = layers[i];
                    bool enabled = !layer.UserDisabled;
                    int scannedCount = 0;
                    scannedCounts?.TryGetValue(layer.LayerIndex, out scannedCount);
                    int activeCount = layer.StaticCount + layer.SkinnedCount;

                    RemixImGui.TableNextRow();
                    RemixImGui.TableSetColumnIndex(0);
                    if (RemixImGui.Checkbox($"##ly_{layer.LayerIndex}", ref enabled))
                    {
                        fc.SetLayerDisabled(layer.LayerIndex, !enabled);
                        _plugin.SetConfig("DisabledLayers", fc.GetDisabledLayersString());
                    }

                    RemixImGui.TableSetColumnIndex(1);
                    string label = string.IsNullOrEmpty(layer.LayerName)
                        ? $"Layer {layer.LayerIndex}"
                        : $"{layer.LayerName} ({layer.LayerIndex})";
                    bool expanded = RemixImGui.TreeNode($"{label}##ly_{layer.LayerIndex}");

                    RemixImGui.TableSetColumnIndex(2);
                    RemixImGui.Text($"{activeCount}");
                    RemixImGui.TableSetColumnIndex(3);
                    RemixImGui.Text(scannedCount > 0 ? $"{scannedCount}" : "-");

                    if (expanded)
                    {
                        for (int j = 0; j < renderers.Length; j++)
                        {
                            if (renderers[j].Layer != layer.LayerIndex)
                                continue;
                            bool rEnabled = !fc.IsRendererDisabled(renderers[j].InstanceId);
                            RemixImGui.TableNextRow();
                            RemixImGui.TableSetColumnIndex(0);
                            if (RemixImGui.Checkbox($"##r_{renderers[j].InstanceId}", ref rEnabled))
                                fc.SetRendererDisabled(renderers[j].InstanceId, !rEnabled);
                            RemixImGui.TableSetColumnIndex(1);
                            RemixImGui.Text($"  {renderers[j].Name}");
                            RemixImGui.TableSetColumnIndex(2);
                            RemixImGui.Text(renderers[j].Type);
                            RemixImGui.TableSetColumnIndex(3);
                        }
                        RemixImGui.TreePop();
                    }
                }
                RemixImGui.EndTable();
            }
        }

        private void DrawParticleStats()
        {
            var pStats = _plugin.FrameCapture?.LatestParticleStats;
            if (pStats == null)
            {
                Note("No particle statistics yet.");
                return;
            }
            if (!_captureParticles)
                Note($"Capture off: {pStats.TotalTrackedSystems} particle systems and {pStats.TotalTrackedTrails} trails in the scene.");
            RemixImGui.Text($"Systems: {pStats.TotalTrackedSystems} tracked ({pStats.ActiveSystems} active, {pStats.DistanceCulledSystems} distance culled)");
            RemixImGui.Text($"Particles: {pStats.RenderedParticles:N0} rendered, {pStats.CulledParticles:N0} culled ({pStats.EmittingSystems} emitting)");
            RemixImGui.Text($"GPU meshes: {pStats.BatchedDrawCalls} batched billboards, {pStats.BakedMeshes} baked");
            RemixImGui.Text($"Trails: {pStats.ActiveTrails} active / {pStats.TotalTrackedTrails} tracked ({pStats.DistanceCulledTrails} culled)");
            if (!string.IsNullOrEmpty(pStats.FurthestSystemName))
                RemixImGui.TextColored(1.0f, 0.75f, 0.3f, 1.0f, $"Furthest active: '{pStats.FurthestSystemName}' ({pStats.FurthestSystemDistance:F1}m)");

            if (pStats.Emitters == null || pStats.Emitters.Length == 0)
                return;
            if (RemixImGui.TreeNode($"Particle systems ({pStats.Emitters.Length})##ps_inspector"))
            {
                if (RemixImGui.BeginTable("##particle_table", 4, TableFlags, 0, 250))
                {
                    RemixImGui.TableSetupColumn("Name", 0, 0.50f);
                    RemixImGui.TableSetupColumn("Distance", 0, 0.18f);
                    RemixImGui.TableSetupColumn("Particles", 0, 0.14f);
                    RemixImGui.TableSetupColumn("Status", 0, 0.18f);
                    RemixImGui.TableHeadersRow();
                    for (int e = 0; e < pStats.Emitters.Length; e++)
                    {
                        var em = pStats.Emitters[e];
                        RemixImGui.TableNextRow();
                        RemixImGui.TableSetColumnIndex(0);
                        RemixImGui.Text(em.Name);
                        RemixImGui.TableSetColumnIndex(1);
                        if (em.Distance > 200f)
                            RemixImGui.TextColored(1.0f, 0.4f, 0.3f, 1.0f, $"{em.Distance:F1}m (!)");
                        else if (em.Distance > 60f)
                            RemixImGui.TextColored(1.0f, 0.8f, 0.3f, 1.0f, $"{em.Distance:F1}m");
                        else
                            RemixImGui.Text($"{em.Distance:F1}m");
                        RemixImGui.TableSetColumnIndex(2);
                        RemixImGui.Text(em.ParticleCount > 0 ? $"{em.ParticleCount}" : "-");
                        RemixImGui.TableSetColumnIndex(3);
                        if (em.Status == "Culled")
                            RemixImGui.TextColored(1.0f, 0.4f, 0.4f, 1.0f, em.Status);
                        else if (em.Status.StartsWith("Rendered"))
                            RemixImGui.TextColored(0.4f, 1.0f, 0.4f, 1.0f, em.Status);
                        else
                            RemixImGui.TextColored(0.6f, 0.6f, 0.6f, 1.0f, em.Status);
                    }
                    RemixImGui.EndTable();
                }
                RemixImGui.TreePop();
            }
        }

        private void DrawUICameras()
        {
            var detector = _plugin.FramebufferPresenter?.UIDetector;
            if (detector == null)
            {
                Note("Single window UI detection is off.");
                return;
            }
            var uiCams = detector.UICameras;
            RemixImGui.Text($"UI cameras ({uiCams.Count}):");
            for (int i = 0; i < uiCams.Count; i++)
                if (uiCams[i] != null)
                    RemixImGui.Text($"  {uiCams[i].name} (depth {uiCams[i].depth})");
            var auxCams = detector.AuxiliaryCameras;
            if (auxCams != null && auxCams.Count > 0)
            {
                RemixImGui.Text($"Render-texture cameras ({auxCams.Count}):");
                for (int i = 0; i < auxCams.Count; i++)
                    if (auxCams[i] != null)
                        RemixImGui.Text($"  {auxCams[i].name} (target {auxCams[i].targetTexture?.name ?? "none"})");
            }
        }

        private void DrawFogState()
        {
            var fog = _plugin.FogManager;
            if (fog == null)
            {
                Note("Fog manager not initialized.");
                return;
            }
            Note($"Status: {fog.StatusText}");
            RemixImGui.Text($"RenderSettings.fog: {(fog.GameFogEnabled ? "on" : "off")}");
            if (!fog.GameFogEnabled)
                return;
            RemixImGui.Text($"Mode: {fog.GameFogMode}");
            var c = fog.GameFogColor;
            RemixImGui.Text($"Colour: R={c.r:F2}, G={c.g:F2}, B={c.b:F2}");
            if (fog.GameFogMode == UnityEngine.FogMode.Linear)
                RemixImGui.Text($"Start {fog.GameFogStart:F1} m, end {fog.GameFogEnd:F1} m");
            else
                RemixImGui.Text($"Density {fog.GameFogDensity:F5}");
        }

        private void DrawMaterialStats()
        {
            var water = _plugin.WaterManager;
            if (water != null)
            {
                RemixImGui.Text($"Detected: {water.WaterMaterialCount} water materials, {water.GlassMaterialCount} glass materials");
                RemixImGui.Text($"Water surfaces: {water.ActiveWaterSurfaceCount}; camera underwater: {(water.IsCameraUnderwater ? "yes" : "no")}");
            }
            var matMgr = _plugin.MaterialManager;
            if (matMgr != null)
                RemixImGui.Text($"Generated PBR: {matMgr.AutoPbrMaterialCount} materials, {matMgr.AutoNormalMapCount} normal maps");
        }

        private void SyncFromConfig()
        {
            _enableGameGeometry = _plugin.GetConfigBool("EnableGameGeometry");
            _enableDistanceCulling = _plugin.GetConfigBool("EnableDistanceCulling");
            _maxRenderDistance = _plugin.GetConfigFloat("MaxRenderDistance");
            _particleMaxDistance = _plugin.GetConfigFloat("ParticleMaxDistance");
            _enableParticleDistanceCulling = _plugin.GetConfigBool("EnableParticleDistanceCulling");
            _rasterParticles = _plugin.GetConfigBool("RasterParticles");
            _enableSkybox = _plugin.GetConfigBool("EnableSkybox");
            _skyboxFiltering = _plugin.GetConfigInt("SkyboxFiltering");
            _enableVisibilityCulling = _plugin.GetConfigBool("UseVisibilityCulling");
            _detectScrollingTextures = _plugin.GetConfigBool("DetectScrollingTextures");
            _enableSceneScan = _plugin.GetConfigBool("EnableSceneScan");
            _sceneScanActiveOnly = _plugin.GetConfigBool("ActiveRenderersOnly");
            _persistDisabledRenderers = _plugin.GetConfigBool("PersistDisabledRenderers");
            _enableLights = _plugin.GetConfigBool("EnableLights");
            _lightIntensityMultiplier = _plugin.GetConfigFloat("IntensityMultiplier");
            _distantLightIntensityMultiplier = _plugin.GetConfigFloat("DistantIntensityMultiplier");
            _particleLights = _plugin.GetConfigBool("ParticleLights");
            _particleLightIntensity = _plugin.GetConfigFloat("ParticleLightIntensity");
            _particleLightPerParticle = _plugin.GetConfigBool("ParticleLightPerParticle");
            _maxParticleLights = _plugin.GetConfigInt("MaxParticleLights");
            _targetFPS = _plugin.GetConfigInt("TargetFPS");
            _captureStaticMeshes = _plugin.GetConfigBool("CaptureStaticMeshes");
            _captureSkinnedMeshes = _plugin.GetConfigBool("CaptureSkinnedMeshes");
            _captureParticles = _plugin.GetConfigBool("CaptureParticles");
            _hardwareSkinning = _plugin.GetConfigBool("HardwareSkinning");
            _captureTextures = _plugin.GetConfigBool("CaptureTextures");
            _captureMaterials = _plugin.GetConfigBool("CaptureMaterials");
            _singleWindow = _plugin.GetConfigBool("SingleWindow");
            _disableInEngineRendering = _plugin.GetConfigBool("DisableInEngineRendering");
            _autoDetectUI = _plugin.GetConfigBool("AutoDetectUI");
            _singleWindowUIOverlay = _plugin.GetConfigBool("SingleWindowUIOverlay");
            _hideUIOnRemixMenu = _plugin.GetConfigBool("HideUIOnRemixMenu");
            _uiOverlayClearBlack = _plugin.GetConfigBool("UIOverlayClearBlack");
            _selectedCameraName = _plugin.GetConfigString("CameraName");
            _engineFPSLimit = _plugin.GetConfigInt("EngineFPSLimit");
            _preventSlowMotion = _plugin.GetConfigBool("PreventSlowMotion");
            _staticMeshFrameSkip = _plugin.GetConfigInt("StaticMeshFrameSkip");
        }

    }
}
