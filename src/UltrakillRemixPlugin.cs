using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace UnityRemix
{
    /// <summary>
    /// Main BepInEx plugin - orchestrates all Remix components
    /// Refactored from 3309 lines to ~350 lines of orchestration code
    /// </summary>
    [BepInPlugin(PluginGUID, PluginName, PluginVersion)]
    [DefaultExecutionOrder(int.MaxValue)]
    public class UnityRemixPlugin : BaseUnityPlugin
    {
        public const string PluginGUID = "com.Unity.remix";
        public const string PluginName = "Unity RTX Remix";
        public const string PluginVersion = "1.0.0";

        // Configuration entries
        private ConfigEntry<string> configCameraName;
        private ConfigEntry<string> configCameraTag;
        private ConfigEntry<bool> configListCameras;
        private ConfigEntry<bool> configUseGameGeometry;
        private ConfigEntry<bool> configUseDistanceCulling;
        private ConfigEntry<float> configMaxRenderDistance;
        private ConfigEntry<bool> configUseVisibilityCulling;
        private ConfigEntry<int> configRendererCacheDuration;
        private ConfigEntry<int> configDebugLogInterval;
        private ConfigEntry<bool> configEnableLights;
        private ConfigEntry<float> configLightIntensityMultiplier;
        private ConfigEntry<int> configTargetFPS;

        // Debug Toggles
        private ConfigEntry<bool> configCaptureStaticMeshes;
        private ConfigEntry<bool> configCaptureSkinnedMeshes;
        private ConfigEntry<bool> configHardwareSkinning;
        private ConfigEntry<bool> configCaptureTextures;
        private ConfigEntry<bool> configCaptureMaterials;
        private ConfigEntry<bool> configVerboseTextureLogging;
        
        // Scene mesh scanner settings
        private ConfigEntry<bool> configEnableSceneScan;
        private ConfigEntry<bool> configSceneScanActiveOnly;
        private ConfigEntry<string> configDisabledLayers;
        private ConfigEntry<bool> configPersistDisabledRenderers;
        
        // Single Window settings
        private ConfigEntry<bool> configSingleWindow;
        private ConfigEntry<SingleWindowMethod> configSingleWindowMethod;
        private ConfigEntry<bool> configDisableInEngineRendering;
        private ConfigEntry<bool> configAutoDetectUI;
        private ConfigEntry<string> configUICameraNames;
        private ConfigEntry<bool> configSingleWindowUIOverlay;
        private ConfigEntry<int> configUIOverlayFPS;
        private ConfigEntry<bool> configHideUIOnRemixMenu;
        private ConfigEntry<bool> configUIOverlayClearBlack;

        // Performance & Throttling
        private ConfigEntry<int> configEngineFPSLimit;
        private ConfigEntry<bool> configPreventSlowMotion;
        private ConfigEntry<int> configStaticMeshFrameSkip;
        private int lastAppliedEngineFPSLimit = -1;
        
        public static ManualLogSource LogSource { get; private set; }
        private RemixAPI.remixapi_Interface remixInterface;
        private IntPtr remixDll = IntPtr.Zero;
        private bool remixInitialized = false;
        private bool deviceRegistered = false;
        
        // COMPONENTS - All functionality delegated to these
        private RemixWindowManager windowManager;
        private RemixFramebufferPresenter framebufferPresenter;
        private RemixCameraHandler cameraHandler;
        private RemixLightConverter lightConverter;
        private RemixMaterialManager materialManager;
        private RemixMeshConverter meshConverter;
        private RemixFrameCapture frameCapture;
        private RemixRenderThread renderThread;
        private TextureCategoryManager textureCategoryManager;
        private SceneMeshScanner sceneMeshScanner;
        private RemixSettingsUI settingsUI;
        private RemixDebugHUD debugHUD;
        
        private int frameCount = 0;
        private static bool isQuitting = false;
        private static System.Diagnostics.Stopwatch interFrameSw;
        
        // Shared lock for all Remix API calls to prevent deadlocks
        private static readonly object remixApiLock = new object();
        
        void Awake()
        {
            LogSource = Logger;
            LogSource.LogInfo($"Plugin {PluginName} v{PluginVersion} is loading!");

            // Suppress conflicting Vulkan implicit layers (Steam overlay, OBS capture) for DXVK/Remix
            try
            {
                Environment.SetEnvironmentVariable("DISABLE_VK_LAYER_VALVE_steam_overlay_1", "1");
                Environment.SetEnvironmentVariable("DISABLE_VULKAN_OBS_CAPTURE", "1");
            }
            catch { }

            // Initialize configuration
            InitializeConfig();
            
            // Ensure game loop, audio, and video players never pause or throttle when focus shifts
            Application.runInBackground = true;

            // Persist across scenes
            DontDestroyOnLoad(this.gameObject);
            hideFlags = HideFlags.HideAndDontSave;
            
            LogSource.LogInfo($"GameObject: {gameObject.name}, Active: {gameObject.activeSelf}, Enabled: {enabled}");
            
            // Apply Harmony patches for instant dynamic object tracking and UI overlay protection
            try
            {
                var harmony = new Harmony(PluginGUID);
                DynamicSpawnPatch.Apply(harmony, LogSource);
                UltrakillPostProcessPatch.Apply(harmony, LogSource);
                RemixGameStateHelper.Apply(harmony, LogSource);
                RemixCameraViewportPatch.Apply(harmony, LogSource);
            }
            catch (Exception ex)
            {
                LogSource.LogError($"Failed to apply Harmony patches: {ex}");
            }

            // Subscribe to scene events
            UnityEngine.SceneManagement.SceneManager.sceneLoaded += OnSceneLoaded;
            
            // Load Remix API
            try
            {
                LogSource.LogInfo("Loading Remix API interface...");
                LoadRemixInterface();
            }
            catch (Exception ex)
            {
                LogSource.LogError($"Failed to load Remix interface: {ex}");
            }
        }
        
        private void InitializeConfig()
        {
            // Camera Settings
            configCameraName = Config.Bind("Camera", "CameraName", "",
                "Specific camera name to use for RTX Remix rendering. Leave empty to use auto-detection.");
            
            configCameraTag = Config.Bind("Camera", "CameraTag", "MainCamera",
                "Camera tag to search for if CameraName is not set.");
            
            configListCameras = Config.Bind("Camera", "ListCamerasOnSceneLoad", true,
                "Log all available cameras when a scene loads to help identify the correct camera name.");
            
            // Rendering Settings
            configUseGameGeometry = Config.Bind("Rendering", "EnableGameGeometry", true,
                "Enable rendering of game geometry through RTX Remix.");
            
            configUseDistanceCulling = Config.Bind("Rendering", "EnableDistanceCulling", false,
                "Enable distance-based culling of objects.");
            
            configMaxRenderDistance = Config.Bind("Rendering", "MaxRenderDistance", 500f,
                new ConfigDescription("Maximum render distance in Unity units.",
                    new AcceptableValueRange<float>(10f, 10000f)));
            
            configUseVisibilityCulling = Config.Bind("Rendering", "UseVisibilityCulling", false,
                "Use Unity's renderer.isVisible check to filter out invisible renderers. May cause visual issues in some games - disable if you see missing geometry.");
            
            configRendererCacheDuration = Config.Bind("Performance", "RendererCacheDuration", 300,
                new ConfigDescription("Number of frames to cache renderer list before refreshing.",
                    new AcceptableValueRange<int>(10, 3600)));
            
            configDebugLogInterval = Config.Bind("Debug", "DetailedLogInterval", 0,
                new ConfigDescription("Number of frames between detailed diagnostic logs (skinned dumps, prune reasons, mesh failures). 0 = disabled.",
                    new AcceptableValueRange<int>(0, 10800)));
            
            // Lighting Settings
            configEnableLights = Config.Bind("Lighting", "EnableLights", true,
                "Convert Unity lights to RTX Remix lights.");
            
            configLightIntensityMultiplier = Config.Bind("Lighting", "IntensityMultiplier", 1.0f,
                new ConfigDescription("Global multiplier for all light intensities.",
                    new AcceptableValueRange<float>(0.01f, 100f)));
            
            // Performance Settings
            configTargetFPS = Config.Bind("Performance", "TargetFPS", 0,
                new ConfigDescription("Target FPS for the Remix render thread. Set to 0 for uncapped.",
                    new AcceptableValueRange<int>(0, 500)));

            // Debug Toggles
            configCaptureStaticMeshes = Config.Bind("Debug", "CaptureStaticMeshes", true,
                "Enable capturing and rendering of static meshes.");
            
            configCaptureSkinnedMeshes = Config.Bind("Debug", "CaptureSkinnedMeshes", true,
                "Enable capturing and rendering of skinned meshes.");
            
            configHardwareSkinning = Config.Bind("Performance", "HardwareSkinning", false,
                "Use GPU hardware skinning for animated meshes. When off, uses CPU BakeMesh fallback.");
            
            configCaptureTextures = Config.Bind("Debug", "CaptureTextures", true,
                "Enable texture capturing and uploading.");
            
            configCaptureMaterials = Config.Bind("Debug", "CaptureMaterials", true,
                "Enable material capturing and creation.");
            
            configVerboseTextureLogging = Config.Bind("Debug", "VerboseTextureLogging", false,
                "Log detailed texture/material diagnostics (hashes, shader props, emission details). Disable to reduce log spam.");
            
            // Scene scan settings
            configEnableSceneScan = Config.Bind("SceneScan", "EnableSceneScan", true,
                "Enable runtime scene scanning to find all static level geometry (including inactive objects). No external bake tool needed.");

            configSceneScanActiveOnly = Config.Bind("SceneScan", "ActiveRenderersOnly", false,
                "Only scan and draw renderers that are currently active. Prevents ghost geometry from inactive scene variants (e.g. The Stanley Parable). Disable for games where inactive geometry should remain visible.");

            configPersistDisabledRenderers = Config.Bind("Rendering", "PersistDisabledRenderers", false,
                "Keep drawing static meshes after their renderer is deactivated by the game. Enable for games that temporarily deactivate visible geometry (e.g. ULTRAKILL CyberGrind).");

            configDisabledLayers = Config.Bind("Rendering", "DisabledLayers", "",
                "Comma-separated list of Unity layer indices to disable (e.g. '8,13,21'). Managed by the in-game UI.");
            
            // Single Window Settings
            configSingleWindow = Config.Bind("Window", "SingleWindow", false,
                "Render RTX Remix inside a single window instead of separate game and Remix windows.");

            configSingleWindowMethod = Config.Bind("Window", "SingleWindowMethod", SingleWindowMethod.Embedded,
                "Method used for single window mode: Embedded (zero performance loss, native child window) or Copy (blits framebuffer into engine).");

            configDisableInEngineRendering = Config.Bind("Window", "DisableInEngineRendering", false,
                "Suppresses Unity's 3D scene rasterization passes when single window mode is enabled (experimental; may affect animation culling).");

            configAutoDetectUI = Config.Bind("Window", "AutoDetectUI", true,
                "Automatically detect UI/HUD cameras and Canvases, keeping them active while suppressing 3D scene rendering.");

            configUICameraNames = Config.Bind("Window", "UICameraNames", "",
                "Comma-separated list of camera names to treat as UI cameras (e.g. 'HUD Camera, Virtual Camera'). Extends auto-detection.");

            configSingleWindowUIOverlay = Config.Bind("Window", "SingleWindowUIOverlay", true,
                "For Embedded mode, renders autodetected UI onto a transparent layered overlay window sitting on top of the Remix viewport.");

            configUIOverlayFPS = Config.Bind("Window", "UIOverlayFPS", 60,
                new ConfigDescription("Target refresh rate (FPS) for the transparent UI overlay window in SingleWindow mode. Supports up to 300 FPS.",
                    new AcceptableValueRange<int>(10, 300)));

            configHideUIOnRemixMenu = Config.Bind("Window", "HideUIOnRemixMenu", false,
                "Hides the game UI overlay when the Remix Alt+X menu is opened in Single Window mode, preventing HUD/crosshair from obstructing the Remix menu.");

            configUIOverlayClearBlack = Config.Bind("Window", "UIOverlayClearBlack", false,
                "When enabled, treats pure opaque black pixels (RGB=0, A=255) as transparent. Required for games (such as URP games like PEAK) where the camera clear or render pipeline fills the alpha channel with 1.0.");

            // Performance & Frame Throttling
            configEngineFPSLimit = Config.Bind("Performance", "EngineFPSLimit", 60,
                "Target frame rate for the Unity engine main loop in Single Window mode. 0 = Uncapped. Presets: 30 (heavy scenes), 60 (recommended/balanced), 90, 120, 0 (uncapped). Remix continues rendering at full speed.");

            configPreventSlowMotion = Config.Bind("Performance", "PreventSlowMotion", true,
                "Prevents physics and game logic from slowing down when frame times spike by raising Time.maximumDeltaTime.");

            configStaticMeshFrameSkip = Config.Bind("Performance", "StaticMeshFrameSkip", 1,
                new ConfigDescription("Reuses cached static mesh instances across N frames instead of iterating thousands of renderers every frame. 1 = every frame (no skip), 2 = skip every other frame (50% CPU savings), etc.",
                    new AcceptableValueRange<int>(1, 4)));

            LogSource.LogInfo("Configuration loaded:");
            LogSource.LogInfo($"  Camera Name: '{configCameraName.Value}' (empty = auto-detect)");
            LogSource.LogInfo($"  Camera Tag: '{configCameraTag.Value}'");
            LogSource.LogInfo($"  Game Geometry: {configUseGameGeometry.Value}");
            LogSource.LogInfo($"  Target FPS: {(configTargetFPS.Value == 0 ? "Uncapped" : configTargetFPS.Value.ToString())}");
            LogSource.LogInfo($"  Single Window: {configSingleWindow.Value} (Method: {configSingleWindowMethod.Value}, SuppressInEngine: {configDisableInEngineRendering.Value}, AutoDetectUI: {configAutoDetectUI.Value}, EngineFPSLimit: {configEngineFPSLimit.Value})");
        }
        
        private void OnSceneLoaded(UnityEngine.SceneManagement.Scene scene, UnityEngine.SceneManagement.LoadSceneMode mode)
        {
            LogSource.LogInfo($"Scene loaded: {scene.name}, mode: {mode}");
            
            framebufferPresenter?.OnSceneLoaded(scene);

            // Reset camera tracking
            cameraHandler?.ResetTracking();
            
            // List cameras if enabled
            if (configListCameras.Value && cameraHandler != null)
            {
                cameraHandler.ListAvailableCameras();
            }
            
            // Invalidate caches
            frameCapture?.InvalidateCaches();
            lightConverter?.ClearCache();
            sceneMeshScanner?.ClearData();
            
            // Trigger scene scan
            if (configEnableSceneScan.Value)
                sceneMeshScanner?.OnSceneLoaded(scene);
            
            // Refresh light cache on scene load
            lightConverter?.RefreshLightCache();
            
            // Refresh camera snapshots for UI
            cameraHandler?.RefreshCameraSnapshots();
            
            // Keep device registration progressing across scene transitions.
            AdvanceDeviceRegistration();
            
            // Capture initial data
            if (configUseGameGeometry.Value && deviceRegistered && frameCapture != null)
            {
                var initialState = new RemixFrameCapture.FrameState();
                frameCapture.CaptureStaticMeshes(initialState, frameCount);
                renderThread?.UpdateFrameState(initialState);
            }
        }
        
        private void LoadRemixInterface()
        {
            LogSource.LogInfo("Loading Remix API interface...");
            
            // Find d3d9.dll
            string gamePath = Application.dataPath;
            string dllPath = System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(gamePath),
                "d3d9.dll"
            );
            
            LogSource.LogInfo($"Looking for Remix DLL at: {dllPath}");
            
            if (!System.IO.File.Exists(dllPath))
            {
                LogSource.LogError($"Remix DLL not found at {dllPath}");
                LogSource.LogInfo("Please place the RTX Remix d3d9.dll in the game root folder.");
                return;
            }
            
            // Load API
            var result = RemixAPI.InitializeRemixAPI(dllPath, out remixInterface, out remixDll);
            if (result != RemixAPI.remixapi_ErrorCode.REMIXAPI_ERROR_CODE_SUCCESS)
            {
                LogSource.LogError($"Failed to load Remix API: {result}");
                return;
            }
            
            LogSource.LogInfo("Remix API loaded successfully!");
            LogSource.LogInfo($"Interface pointers - CreateMesh: {remixInterface.CreateMesh}, DrawInstance: {remixInterface.DrawInstance}");
            
            remixInitialized = true;
            
            // Initialize components
            InitializeComponents();
            
            // Device registration is advanced from Update/UpdateFromPersistent so it survives
            // plugin recreation during scene transitions.
        }
        
        private void InitializeComponents()
        {
            LogSource.LogInfo("Initializing components...");
            
            // Create all components with dependencies
            textureCategoryManager = new TextureCategoryManager();
            
            windowManager = new RemixWindowManager(LogSource, remixInterface, configSingleWindow, configSingleWindowMethod);
            
            cameraHandler = new RemixCameraHandler(
                LogSource,
                configCameraName,
                configCameraTag,
                configListCameras,
                remixInterface
            );

            framebufferPresenter = new RemixFramebufferPresenter();
            framebufferPresenter.Initialize(
                LogSource,
                windowManager,
                cameraHandler,
                configSingleWindow,
                configSingleWindowMethod,
                configDisableInEngineRendering,
                configAutoDetectUI,
                configUICameraNames,
                configSingleWindowUIOverlay,
                configUIOverlayFPS,
                configHideUIOnRemixMenu,
                configUIOverlayClearBlack
            );
            
            lightConverter = new RemixLightConverter(
                LogSource,
                configEnableLights,
                configLightIntensityMultiplier,
                configDebugLogInterval,
                remixInterface,
                remixApiLock
            );
            
            materialManager = new RemixMaterialManager(
                LogSource,
                textureCategoryManager,
                configCaptureTextures,
                configCaptureMaterials,
                configVerboseTextureLogging,
                remixInterface,
                remixApiLock
            );
            
            meshConverter = new RemixMeshConverter(
                LogSource,
                materialManager,
                configDebugLogInterval,
                remixInterface,
                remixApiLock
            );
            
            frameCapture = new RemixFrameCapture(
                LogSource,
                cameraHandler,
                meshConverter,
                materialManager,
                configUseDistanceCulling,
                configMaxRenderDistance,
                configUseVisibilityCulling,
                configRendererCacheDuration,
                configDebugLogInterval,
                configCaptureStaticMeshes,
                configCaptureSkinnedMeshes,
                configHardwareSkinning,
                configPersistDisabledRenderers,
                configStaticMeshFrameSkip
            );
            frameCapture.LoadDisabledLayersString(configDisabledLayers.Value);
            
            renderThread = new RemixRenderThread(
                LogSource,
                windowManager,
                cameraHandler,
                meshConverter,
                lightConverter,
                frameCapture,
                configTargetFPS,
                configDebugLogInterval,
                configEnableLights,
                configUseGameGeometry,
                remixInterface
            );
            
            sceneMeshScanner = new SceneMeshScanner(
                LogSource,
                meshConverter,
                materialManager,
                remixApiLock,
                frameCapture.IsLayerDisabled,
                configSceneScanActiveOnly.Value
            );
            
            // Give render thread access to scene mesh scanner
            renderThread.SetSceneMeshScanner(sceneMeshScanner);
            
            // Initialize ImGui overlay
            if (RemixImGui.Initialize(LogSource))
            {
                settingsUI = new RemixSettingsUI(LogSource, this);
                RemixImGui.RegisterDrawCallback(new RemixImGui.DrawCallback(settingsUI.Draw));
                LogSource.LogInfo("ImGui settings overlay registered");

                // Register always-on debug HUD overlay
                debugHUD = new RemixDebugHUD(LogSource, this);
                if (RemixImGui.RegisterOverlayCallback(new RemixImGui.DrawCallback(debugHUD.Draw)))
                    LogSource.LogInfo("Debug HUD overlay registered (toggle with F3)");
                else
                    LogSource.LogWarning("Remix build does not support overlay callbacks — debug HUD unavailable");
            }

            // Initialize profiler
            RemixProfiler.Logger = LogSource;
            RemixTracy.SetThreadName("UnityMainThread");

            LogSource.LogInfo("All components initialized");
        }
        
        private bool renderThreadStarted = false;

        private void AdvanceDeviceRegistration()
        {
            if (!remixInitialized || deviceRegistered)
                return;

            // Phase 1: start the render thread (once)
            if (!renderThreadStarted)
            {
                LogSource.LogInfo("Device registration: starting render thread...");
                try
                {
                    InitializeRemixDevice();
                    renderThreadStarted = true;
                }
                catch (Exception ex)
                {
                    LogSource.LogError($"Failed while starting render thread: {ex}");
                }
                return;
            }
            
            // Phase 2: wait for the render thread to confirm device is ready
            if (renderThread != null && renderThread.DeviceReady)
            {
                deviceRegistered = true;
                LogSource.LogInfo("Device registered — render thread confirmed ready");
            }
        }
        
        private void InitializeRemixDevice()
        {
            // Set window dimensions
            int width = Screen.width > 0 ? Screen.width : 1920;
            int height = Screen.height > 0 ? Screen.height : 1080;
            
            if (windowManager != null)
            {
                windowManager.SetWindowDimensions(width, height);
            }
            
            // Start render thread — it will create the window and D3D9 device.
            // deviceRegistered stays false until the render thread confirms DeviceReady.
            LogSource.LogInfo("Starting render thread...");
            renderThread?.Start();
        }
        
        void Update()
        {
            frameCount++;

            // Engine frame throttling and DeltaTime slow-motion prevention
            if (configSingleWindow.Value)
            {
                if (configEngineFPSLimit.Value > 0)
                {
                    if (Application.targetFrameRate != configEngineFPSLimit.Value)
                        Application.targetFrameRate = configEngineFPSLimit.Value;
                    if (QualitySettings.vSyncCount != 0)
                        QualitySettings.vSyncCount = 0;
                    lastAppliedEngineFPSLimit = configEngineFPSLimit.Value;
                }
                else if (lastAppliedEngineFPSLimit > 0)
                {
                    Application.targetFrameRate = -1;
                    lastAppliedEngineFPSLimit = 0;
                }

                if (configPreventSlowMotion.Value && Time.maximumDeltaTime < 0.15f)
                {
                    Time.maximumDeltaTime = 0.15f;
                }
            }

            AdvanceDeviceRegistration();
            
            // Rescan for async-loaded meshes (Addressables, etc.)
            if (configEnableSceneScan.Value)
                sceneMeshScanner?.Update(Time.unscaledDeltaTime);

            // Visibility filtering for scanned instances is done in UpdateFromPersistent()
            // after the camera has been resolved by CaptureStaticMeshes
        }
        
        void LateUpdate()
        {
            // Capture frame data on main thread
            UpdateFromPersistent();
        }
        
        void OnApplicationQuit()
        {
            LogSource.LogInfo("Application quitting...");
            RemixWindowManager.UpdateCursorVisibility(false);
            isQuitting = true;
        }
        
        public void UpdateFromPersistent()
        {
            if (!remixInitialized)
                return;

            AdvanceDeviceRegistration();
            if (!deviceRegistered)
                return;
            
            frameCount++;

            double engineMs = 0;
            if (interFrameSw != null)
            {
                engineMs = interFrameSw.Elapsed.TotalMilliseconds;
            }

            RemixTracy.FrameMark();
            using (RemixTracy.Zone("UnityMainThread_Frame"))
            {
                var totalSw = System.Diagnostics.Stopwatch.StartNew();
                double lightsMs = 0, staticMs = 0, skinnedMs = 0, dynamicMs = 0, scannerMs = 0, overlayMs = 0;

                if (configUseGameGeometry.Value && frameCapture != null && renderThread != null)
                {
                    var nextState = new RemixFrameCapture.FrameState();
                    nextState.frameCount = frameCount;

                    // Refresh light cache every frame to capture transient lights (explosions, muzzle flashes)
                    if (configEnableLights.Value && lightConverter != null)
                    {
                        using (RemixTracy.Zone("RefreshLightCache"))
                        {
                            var sw = System.Diagnostics.Stopwatch.StartNew();
                            lightConverter.RefreshLightCache();
                            lightsMs = sw.Elapsed.TotalMilliseconds;
                        }
                    }

                    // Refresh camera snapshot for the UI
                    if (frameCount % configRendererCacheDuration.Value == 0 && cameraHandler != null)
                    {
                        using (RemixTracy.Zone("RefreshCameraSnapshots"))
                        {
                            cameraHandler.RefreshCameraSnapshots();
                        }
                    }

                    // Capture static meshes and camera
                    using (RemixTracy.Zone("CaptureStaticMeshes"))
                    {
                        try
                        {
                            var sw = System.Diagnostics.Stopwatch.StartNew();
                            frameCapture.CaptureStaticMeshes(nextState, frameCount);
                            staticMs = sw.Elapsed.TotalMilliseconds;
                        }
                        catch (Exception ex)
                        {
                            LogSource.LogError($"CaptureStaticMeshes exception: {ex}");
                        }
                    }

                    // Capture skinned meshes
                    using (RemixTracy.Zone("CaptureSkinnedMeshes"))
                    {
                        try
                        {
                            var sw = System.Diagnostics.Stopwatch.StartNew();
                            frameCapture.CaptureSkinnedMeshes(nextState, frameCount);
                            skinnedMs = sw.Elapsed.TotalMilliseconds;
                        }
                        catch (Exception ex)
                        {
                            LogSource.LogError($"CaptureSkinnedMeshes exception: {ex}");
                        }
                    }

                    // Capture dynamic non-skinned effects (weapon screens, line/trail beams, sprites, blood decals, particles)
                    using (RemixTracy.Zone("CaptureDynamicEffects"))
                    {
                        try
                        {
                            var sw = System.Diagnostics.Stopwatch.StartNew();
                            frameCapture.CaptureDynamicEffects(nextState, frameCount);
                            dynamicMs = sw.Elapsed.TotalMilliseconds;
                        }
                        catch (Exception ex)
                        {
                            LogSource.LogError($"CaptureDynamicEffects exception: {ex}");
                        }
                    }

                    // Update scene scan visibility with the camera position resolved by CaptureStaticMeshes
                    if (sceneMeshScanner != null)
                    {
                        using (RemixTracy.Zone("SceneMeshScanner_UpdateVisibility"))
                        {
                            try
                            {
                                var sw = System.Diagnostics.Stopwatch.StartNew();
                                Vector3 camPos = nextState.camera.valid ? nextState.camera.position : Vector3.zero;
                                sceneMeshScanner.UpdateVisibility(
                                    camPos,
                                    configUseDistanceCulling.Value,
                                    configMaxRenderDistance.Value,
                                    configUseVisibilityCulling.Value
                                );
                                scannerMs = sw.Elapsed.TotalMilliseconds;
                            }
                            catch (Exception ex)
                            {
                                LogSource.LogError($"sceneMeshScanner UpdateVisibility exception: {ex}");
                            }
                        }
                    }

                    // Send to render thread (mesh creation moved to render thread to avoid deadlocks)
                    renderThread.UpdateFrameState(nextState);
                }

                // Update SingleWindow framebuffer presenter & UI overlay
                using (RemixTracy.Zone("UpdateOverlay"))
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    framebufferPresenter?.Update(frameCount);
                    overlayMs = sw.Elapsed.TotalMilliseconds;
                }

                // Update debug HUD snapshot after all frame data is captured
                double hudMs = 0;
                using (RemixTracy.Zone("DebugHUD_UpdateSnapshot"))
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    debugHUD?.UpdateSnapshot();
                    hudMs = sw.Elapsed.TotalMilliseconds;
                }

                RemixProfiler.RecordMainThread(
                    totalSw.Elapsed.TotalMilliseconds,
                    engineMs,
                    lightsMs,
                    staticMs,
                    scannerMs,
                    skinnedMs,
                    dynamicMs,
                    overlayMs,
                    hudMs
                );
            }

            interFrameSw = System.Diagnostics.Stopwatch.StartNew();
        }

        void Start()
        {
            StartCoroutine(EndOfFrameLoop());
            LogSource.LogInfo("UnityRemixPlugin Start: EndOfFrameLoop started");
        }

        private System.Collections.IEnumerator EndOfFrameLoop()
        {
            var wait = new WaitForEndOfFrame();
            while (true)
            {
                yield return wait;
                OnEndOfFrame();
            }
        }

        private int lastEndOfFrameNumber = -1;

        public void OnEndOfFrame()
        {
            if (lastEndOfFrameNumber == frameCount) return;
            lastEndOfFrameNumber = frameCount;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            using (RemixTracy.Zone("OnEndOfFrame"))
            {
                framebufferPresenter?.OnEndOfFrame();
            }
            RemixProfiler.RecordEndOfFrame(sw.Elapsed.TotalMilliseconds);
        }
        
        void OnDestroy()
        {
            if (!isQuitting)
            {
                LogSource.LogWarning("OnDestroy called but app not quitting - recreating plugin...");
                
                var newGo = new GameObject("UnityRemix_Persistent");
                GameObject.DontDestroyOnLoad(newGo);
                newGo.hideFlags = HideFlags.HideAndDontSave;
                var newBehaviour = newGo.AddComponent<RemixPersistentBehaviour>();
                newBehaviour.Initialize(this);
                
                return;
            }
            
            LogSource.LogInfo("OnDestroy called during quit - cleaning up...");
            UnityEngine.SceneManagement.SceneManager.sceneLoaded -= OnSceneLoaded;
            CleanupRemix();
        }
        
        public static void SetQuitting()
        {
            isQuitting = true;
        }
        
        private void CleanupRemix()
        {
            if (!remixInitialized) return;
            
            LogSource.LogInfo("Cleaning up Remix...");
            
            // Unregister ImGui callback
            RemixImGui.UnregisterDrawCallback();
            RemixImGui.UnregisterOverlayCallback();
            
            // Stop render thread
            renderThread?.Stop();
            
            // Cleanup all components
            framebufferPresenter?.Cleanup();
            materialManager?.Cleanup();
            meshConverter?.Cleanup();
            frameCapture?.Cleanup();
            lightConverter?.ClearCache();
            windowManager?.DestroyRemixWindow();
            
            // Shutdown Remix API
            RemixAPI.ShutdownAndUnloadRemixDll(ref remixInterface, remixDll);
            remixDll = IntPtr.Zero;
            remixInitialized = false;
            
            LogSource.LogInfo("Remix cleanup complete");
        }

        #region Config Accessors (for ImGui settings UI)

        public bool GetConfigBool(string key)
        {
            switch (key)
            {
                case "EnableGameGeometry": return configUseGameGeometry.Value;
                case "EnableDistanceCulling": return configUseDistanceCulling.Value;
                case "UseVisibilityCulling": return configUseVisibilityCulling.Value;
                case "EnableLights": return configEnableLights.Value;
                case "CaptureStaticMeshes": return configCaptureStaticMeshes.Value;
                case "CaptureSkinnedMeshes": return configCaptureSkinnedMeshes.Value;
                case "HardwareSkinning": return configHardwareSkinning.Value;
                case "CaptureTextures": return configCaptureTextures.Value;
                case "CaptureMaterials": return configCaptureMaterials.Value;
                case "EnableSceneScan": return configEnableSceneScan.Value;
                case "ActiveRenderersOnly": return configSceneScanActiveOnly.Value;
                case "PersistDisabledRenderers": return configPersistDisabledRenderers.Value;
                case "SingleWindow": return configSingleWindow.Value;
                case "DisableInEngineRendering": return configDisableInEngineRendering.Value;
                case "AutoDetectUI": return configAutoDetectUI.Value;
                case "SingleWindowUIOverlay": return configSingleWindowUIOverlay.Value;
                case "PreventSlowMotion": return configPreventSlowMotion.Value;
                case "HideUIOnRemixMenu": return configHideUIOnRemixMenu.Value;
                case "UIOverlayClearBlack": return configUIOverlayClearBlack.Value;
                default: return false;
            }
        }

        public float GetConfigFloat(string key)
        {
            switch (key)
            {
                case "MaxRenderDistance": return configMaxRenderDistance.Value;
                case "IntensityMultiplier": return configLightIntensityMultiplier.Value;
                default: return 0f;
            }
        }

        public string GetConfigString(string key)
        {
            switch (key)
            {
                case "CameraName": return configCameraName.Value;
                case "DisabledLayers": return configDisabledLayers.Value;
                case "SingleWindowMethod": return configSingleWindowMethod.Value.ToString();
                case "UICameraNames": return configUICameraNames.Value;
                default: return "";
            }
        }

        public void SetConfig(string key, string value)
        {
            switch (key)
            {
                case "CameraName": configCameraName.Value = value; break;
                case "DisabledLayers": configDisabledLayers.Value = value; break;
                case "UICameraNames": configUICameraNames.Value = value; break;
                case "SingleWindowMethod":
                    if (Enum.TryParse<SingleWindowMethod>(value, true, out var method))
                        configSingleWindowMethod.Value = method;
                    break;
            }
        }

        public int GetConfigInt(string key)
        {
            switch (key)
            {
                case "TargetFPS": return configTargetFPS.Value;
                case "EngineFPSLimit": return configEngineFPSLimit.Value;
                case "StaticMeshFrameSkip": return configStaticMeshFrameSkip.Value;
                case "UIOverlayFPS": return configUIOverlayFPS.Value;
                default: return 0;
            }
        }

        public void SetConfig(string key, bool value)
        {
            switch (key)
            {
                case "EnableGameGeometry": configUseGameGeometry.Value = value; break;
                case "EnableDistanceCulling": configUseDistanceCulling.Value = value; break;
                case "UseVisibilityCulling": configUseVisibilityCulling.Value = value; break;
                case "EnableLights": configEnableLights.Value = value; break;
                case "CaptureStaticMeshes": configCaptureStaticMeshes.Value = value; break;
                case "CaptureSkinnedMeshes": configCaptureSkinnedMeshes.Value = value; break;
                case "HardwareSkinning": configHardwareSkinning.Value = value; break;
                case "CaptureTextures": configCaptureTextures.Value = value; break;
                case "CaptureMaterials": configCaptureMaterials.Value = value; break;
                case "EnableSceneScan": configEnableSceneScan.Value = value; break;
                case "ActiveRenderersOnly":
                    configSceneScanActiveOnly.Value = value;
                    if (sceneMeshScanner != null) sceneMeshScanner.ScanActiveOnly = value;
                    break;
                case "PersistDisabledRenderers": configPersistDisabledRenderers.Value = value; break;
                case "SingleWindow": configSingleWindow.Value = value; break;
                case "DisableInEngineRendering": configDisableInEngineRendering.Value = value; break;
                case "AutoDetectUI": configAutoDetectUI.Value = value; break;
                case "SingleWindowUIOverlay": configSingleWindowUIOverlay.Value = value; break;
                case "PreventSlowMotion": configPreventSlowMotion.Value = value; break;
                case "HideUIOnRemixMenu": configHideUIOnRemixMenu.Value = value; break;
                case "UIOverlayClearBlack": configUIOverlayClearBlack.Value = value; break;
            }
        }

        public void SetConfig(string key, float value)
        {
            switch (key)
            {
                case "MaxRenderDistance": configMaxRenderDistance.Value = value; break;
                case "IntensityMultiplier": configLightIntensityMultiplier.Value = value; break;
            }
        }

        public void SetConfig(string key, int value)
        {
            switch (key)
            {
                case "TargetFPS": configTargetFPS.Value = value; break;
                case "EngineFPSLimit": configEngineFPSLimit.Value = value; break;
                case "StaticMeshFrameSkip": configStaticMeshFrameSkip.Value = value; break;
                case "UIOverlayFPS": configUIOverlayFPS.Value = value; break;
            }
        }

        public void SaveConfig() => Config.Save();

        public RemixCameraHandler CameraHandler => cameraHandler;

        public RemixFrameCapture FrameCapture => frameCapture;

        public SceneMeshScanner SceneMeshScanner => sceneMeshScanner;

        public RemixMeshConverter MeshConverter => meshConverter;

        public RemixMaterialManager MaterialManager => materialManager;

        public RemixLightConverter LightConverter => lightConverter;

        public RemixFramebufferPresenter FramebufferPresenter => framebufferPresenter;

        #endregion
    }
}
