using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;

namespace UnityRemix
{
    /// <summary>
    /// Coordinates Single-Window mode presentation and in-engine rendering suppression.
    /// Uses RemixUIDetector to automatically preserve and render UI/HUD cameras and Canvases on top
    /// of the RTX Remix ray-traced viewport.
    /// </summary>
    public class RemixFramebufferPresenter
    {
        private ManualLogSource logger;
        private RemixWindowManager windowManager;
        private RemixCameraHandler cameraHandler;
        private RemixUIDetector uiDetector;
        private RemixUIOverlay uiOverlay;

        // Config entries
        private ConfigEntry<bool> configSingleWindow;
        private ConfigEntry<bool> configDisableInEngineRendering;
        private ConfigEntry<bool> configAutoDetectUI;
        private ConfigEntry<string> configUICameraNames;
        private ConfigEntry<bool> configSingleWindowUIOverlay;
        private ConfigEntry<int> configUIOverlayFPS;
        private ConfigEntry<bool> configHideUIOnRemixMenu;
        private ConfigEntry<bool> configUIOverlayClearBlack;

        // Tracking suppressed world cameras
        private readonly Dictionary<Camera, int> originalCullingMasks = new Dictionary<Camera, int>();
        private readonly Dictionary<Camera, CameraClearFlags> originalClearFlags = new Dictionary<Camera, CameraClearFlags>();
        private bool inEngineRenderingSuppressed = false;
        private int sceneRefreshCounter = 0;

        private bool isSingleWindowActive = false;

        public RemixUIDetector UIDetector => uiDetector;
        public static bool IsSingleWindowUIActive { get; private set; }

        public void Initialize(
            ManualLogSource logger,
            RemixWindowManager windowManager,
            RemixCameraHandler cameraHandler,
            ConfigEntry<bool> singleWindow,
            ConfigEntry<bool> disableInEngineRendering,
            ConfigEntry<bool> autoDetectUI,
            ConfigEntry<string> uiCameraNames,
            ConfigEntry<bool> singleWindowUIOverlay,
            ConfigEntry<int> uiOverlayFPS = null,
            ConfigEntry<bool> hideUIOnRemixMenu = null,
            ConfigEntry<bool> uiOverlayClearBlack = null)
        {
            this.logger = logger;
            this.windowManager = windowManager;
            this.cameraHandler = cameraHandler;
            this.configSingleWindow = singleWindow;
            this.configDisableInEngineRendering = disableInEngineRendering;
            this.configAutoDetectUI = autoDetectUI;
            this.configUICameraNames = uiCameraNames;
            this.configSingleWindowUIOverlay = singleWindowUIOverlay;
            this.configUIOverlayFPS = uiOverlayFPS;
            this.configHideUIOnRemixMenu = hideUIOnRemixMenu;
            this.configUIOverlayClearBlack = uiOverlayClearBlack;
            this.isSingleWindowActive = singleWindow != null && singleWindow.Value;

            uiDetector = new RemixUIDetector(
                logger,
                autoDetectUI,
                uiCameraNames,
                null
            );

            UpdateSingleWindowUIActive();
            Application.runInBackground = true;
            logger?.LogInfo($"[RemixFramebufferPresenter] Initialized (SingleWindow: {singleWindow.Value}, SuppressInEngine: {disableInEngineRendering.Value}, AutoDetectUI: {autoDetectUI.Value})");
        }

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern IntPtr SetFocus(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        [DllImport("user32.dll")]
        private static extern IntPtr SetCursor(IntPtr hCursor);

        private const int VK_MENU = 0x12;   // Alt key
        private const int VK_X = 0x58;      // 'X' key
        private const int VK_ESCAPE = 0x1B; // Escape key
        private const int VK_F8 = 0x77;     // F8 key
        private bool wasAltXPressed = false;
        private bool wasF8Pressed = false;

        private void UpdateSingleWindowUIActive()
        {
            IsSingleWindowUIActive = isSingleWindowActive;
        }

        private int lastCameraCount = -1;
        private int lastCanvasCount = -1;
        private int lastActiveCanvasCount = -1;
        private int lastScreenWidth = -1;
        private int lastScreenHeight = -1;

        public void OnSceneLoaded(UnityEngine.SceneManagement.Scene scene)
        {
            sceneRefreshCounter = 15; // Re-evaluate suppression over the next 15 frames to catch async objects
            lastCameraCount = -1;
            lastCanvasCount = -1;
            lastActiveCanvasCount = -1;
            lastScreenWidth = -1;
            lastScreenHeight = -1;
            RemixWindowManager.SyncUIStateWithRemix(logger);
        }

        public void Update(int frameCount)
        {
            if (configSingleWindow == null) return;

            RemixWatchdog.BeatMain("Presenter.Update");
            bool isSingle = isSingleWindowActive;

            using (RemixTracy.Zone("Presenter_Update"))
            {
                UpdateSingleWindowUIActive();

                if (isSingle)
                {
                    Camera worldCam = cameraHandler?.CurrentCamera ?? Camera.main;

                    // Ensure UI Presentation window is active if missing
                    if (uiOverlay == null)
                    {
                        SetupEmbeddedUIOverlay();
                    }

                    // Check for keypress diagnostics (F8)
                    bool escapePressed = (GetAsyncKeyState(VK_ESCAPE) & 0x8000) != 0;
                    bool f8Pressed = (GetAsyncKeyState(VK_F8) & 0x8000) != 0;
                    if (f8Pressed && !wasF8Pressed)
                    {
                        uiDetector.DumpUIState("F8 Key Pressed (Manual UI Diagnostic)");
                    }
                    wasF8Pressed = f8Pressed;

                    // Handle 3D in-engine camera suppression & camera detection
                    bool shouldSuppress = configDisableInEngineRendering != null && configDisableInEngineRendering.Value;

                    int currentCameraCount = Camera.allCamerasCount;
                    var allCanvases = UnityEngine.Object.FindObjectsOfType<Canvas>(true);
                    int currentCanvasCount = allCanvases.Length;
                    int currentActiveCanvasCount = 0;
                    for (int i = 0; i < allCanvases.Length; i++)
                    {
                        if (allCanvases[i] != null && allCanvases[i].isActiveAndEnabled)
                            currentActiveCanvasCount++;
                    }

                    int currentW = Screen.width;
                    int currentH = Screen.height;
                    bool resolutionChanged = (lastScreenWidth > 0 && lastScreenHeight > 0) &&
                                             (currentW != lastScreenWidth || currentH != lastScreenHeight);
                    bool cameraCountChanged = (currentCameraCount != lastCameraCount);
                    bool canvasCountChanged = (currentCanvasCount != lastCanvasCount) || (currentActiveCanvasCount != lastActiveCanvasCount);
                    bool shouldCheck = (shouldSuppress != inEngineRenderingSuppressed) || cameraCountChanged || canvasCountChanged || resolutionChanged || (sceneRefreshCounter > 0) || escapePressed || (frameCount % 180 == 0);

                    if (shouldCheck)
                    {
                        if (sceneRefreshCounter > 0) sceneRefreshCounter--;
                        if (canvasCountChanged)
                        {
                            logger?.LogInfo($"[RemixFramebufferPresenter] Canvas count changed: total={lastCanvasCount}->{currentCanvasCount}, active={lastActiveCanvasCount}->{currentActiveCanvasCount}. Re-evaluating UI detection.");
                        }

                        lastCameraCount = currentCameraCount;
                        lastCanvasCount = currentCanvasCount;
                        lastActiveCanvasCount = currentActiveCanvasCount;
                        lastScreenWidth = currentW;
                        lastScreenHeight = currentH;

                        // 1. ALWAYS refresh UI detector to categorize World and UI cameras
                        uiDetector.Refresh(worldCam);

                        // 2. Handle 3D in-engine camera suppression
                        if (shouldSuppress)
                            ApplyCameraSuppressionInternal();
                        else
                            RestoreCameraSuppression();

                        // 3. Configure UI presentation with newly detected UI cameras
                        SetupEmbeddedUIOverlay();

                        // 4. Route overlay canvases to dedicated UI camera
                        if (uiDetector.UICameras.Count > 0)
                        {
                            var targetCam = uiDetector.DedicatedUICamera ?? uiDetector.UICameras[0];
                            uiDetector.RouteOverlayCanvasesToCamera(targetCam);
                            uiDetector.RouteVideoPlayersToCamera(targetCam);
                            Canvas.ForceUpdateCanvases();
                        }
                    }
                    else
                    {
                        lastScreenWidth = currentW;
                        lastScreenHeight = currentH;
                    }

                    // Sync dedicated UI camera transform with active world camera every frame
                    if (worldCam != null)
                    {
                        uiDetector.SyncDedicatedUICameraTransform(worldCam);
                    }

                    // Sync embedded window bounds
                    if (windowManager != null)
                    {
                        RemixWatchdog.BeatMain("Presenter.Update.SyncBounds");
                        windowManager.SyncWindowBounds();
                    }
                }
                else
                {
                    if (inEngineRenderingSuppressed)
                        RestoreCameraSuppression();
                    TearDownUIOverlay();
                }

            }

            RemixWatchdog.BeatMain("Presenter.Update.AltX");
            // Handle Alt+X detection for Remix ImGui using direct hardware query so it never drops even when window focus changes
            bool altHeld = (GetAsyncKeyState(VK_MENU) & 0x8000) != 0;
            bool xHeld = (GetAsyncKeyState(VK_X) & 0x8000) != 0;
            bool altXPressed = altHeld && xHeld;

            if (altXPressed && !wasAltXPressed)
            {
                wasAltXPressed = true;
                windowManager?.HandleAltX();
                logger?.LogInfo($"[RemixFramebufferPresenter] Alt+X triggered (Win32 GetAsyncKeyState), RemixUIOpen: {RemixWindowManager.IsRemixUIOpen}");
            }
            else if (!altXPressed)
            {
                wasAltXPressed = false;
            }

            // Continuously query ground truth UI state directly from Remix runtime
            RemixWatchdog.BeatMain("Presenter.Update.SyncUIState");
            RemixWindowManager.SyncUIStateWithRemix(logger);

            // Determine whether cursor should be hidden (in-game gameplay) or visible (menus/Remix UI/tabbed out)
            RemixWatchdog.BeatMain("Presenter.Update.Cursor");
            bool shouldHide = Application.isFocused && (!isSingleWindowActive || !RemixWindowManager.IsRemixUIOpen) && (!Cursor.visible || Cursor.lockState == CursorLockMode.Locked);
            RemixWindowManager.UpdateCursorVisibility(shouldHide);

            // While Remix UI is open in Single Window mode, guarantee cursor is unlocked and visible
            if (isSingleWindowActive && RemixWindowManager.IsRemixUIOpen)
            {
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
            }
            else if (shouldHide)
            {
                SetCursor(RemixWindowManager.BlankCursor);
            }

            // Foreground safety net: On Main Thread, if gameplay is active but remixWindow was activated, focus gameWindow safely
            if (isSingleWindowActive && !RemixWindowManager.IsRemixUIOpen && windowManager != null)
            {
                IntPtr fg = GetForegroundWindow();
                IntPtr rw = windowManager.RemixWindow;
                IntPtr gw = windowManager.GameWindow;
                if (rw != IntPtr.Zero && fg == rw && gw != IntPtr.Zero)
                {
                    SetForegroundWindow(gw);
                    SetFocus(gw);
                }
            }

            // Ensure game window retains activation and focus during startup
            if ((frameCount == 15 || frameCount == 60) && isSingle)
            {
                IntPtr gameWnd = windowManager != null && windowManager.GameWindow != IntPtr.Zero
                    ? windowManager.GameWindow
                    : RemixWindowManager.FindGameWindow();
                if (gameWnd != IntPtr.Zero)
                {
                    SetForegroundWindow(gameWnd);
                    SetFocus(gameWnd);
                    logger?.LogInfo($"[RemixFramebufferPresenter] Enforced foreground focus on gameWindow 0x{gameWnd:X} at frame #{frameCount}");
                }
            }
            RemixWatchdog.BeatMain("Presenter.Update.End");
        }

        public void OnEndOfFrame()
        {
            if (isSingleWindowActive && uiOverlay != null)
            {
                uiOverlay.UpdateOverlay();
            }
        }

        /// <summary>
        /// Suppresses Unity's 3D scene rasterization passes on World Cameras while keeping UI/HUD cameras active.
        /// </summary>
        public void ApplyCameraSuppression(Camera worldCam = null)
        {
            if (uiDetector == null) return;

            if (worldCam == null)
                worldCam = cameraHandler?.CurrentCamera ?? Camera.main;
            uiDetector.Refresh(worldCam);
            ApplyCameraSuppressionInternal();
        }

        private void ApplyCameraSuppressionInternal()
        {
            // Suppress 3D World Cameras
            int suppressedCount = 0;
            foreach (var cam in uiDetector.WorldCameras)
            {
                if (cam == null) continue;

                if (!originalCullingMasks.ContainsKey(cam))
                {
                    originalCullingMasks[cam] = cam.cullingMask;
                    originalClearFlags[cam] = cam.clearFlags;
                }

                cam.cullingMask = 0;
                cam.clearFlags = CameraClearFlags.Nothing;
                suppressedCount++;
            }

            inEngineRenderingSuppressed = true;
            logger?.LogInfo($"[RemixFramebufferPresenter] In-engine 3D rendering suppressed on {suppressedCount} World Cameras.");
        }

        public void ApplyInEngineRenderingSuppression()
        {
            Camera worldCam = cameraHandler?.CurrentCamera ?? Camera.main;
            ApplyCameraSuppression(worldCam);
            SetupEmbeddedUIOverlay();
        }

        private void SetupEmbeddedUIOverlay()
        {
            if (configSingleWindowUIOverlay == null || !configSingleWindowUIOverlay.Value)
                return;

            IntPtr gameWnd = windowManager != null && windowManager.GameWindow != IntPtr.Zero 
                ? windowManager.GameWindow 
                : RemixWindowManager.FindGameWindow();

            if (uiOverlay == null && gameWnd != IntPtr.Zero)
            {
                uiOverlay = new RemixUIOverlay(logger, gameWnd, configUIOverlayFPS, configHideUIOnRemixMenu, configUIOverlayClearBlack);
                if (!uiOverlay.Initialize())
                {
                    uiOverlay = null;
                    return;
                }
            }

            if (uiOverlay != null && uiDetector.UICameras.Count > 0)
            {
                uiOverlay.ConfigureUICameras(uiDetector.UICameras);
                var targetCam = uiDetector.DedicatedUICamera ?? uiDetector.UICameras[0];
                uiDetector.RouteOverlayCanvasesToCamera(targetCam);
                uiDetector.RouteVideoPlayersToCamera(targetCam);
                lastCanvasCount = UnityEngine.Object.FindObjectsOfType<Canvas>(true).Length;
            }
        }

        /// <summary>
        /// Restores original camera culling masks and clear flags on World Cameras.
        /// </summary>
        public void RestoreCameraSuppression()
        {
            if (originalCullingMasks.Count > 0 || inEngineRenderingSuppressed)
            {
                foreach (var kvp in originalCullingMasks)
                {
                    var cam = kvp.Key;
                    if (cam != null)
                    {
                        cam.cullingMask = kvp.Value;
                        if (originalClearFlags.TryGetValue(cam, out var flags))
                        {
                            cam.clearFlags = flags;
                        }
                    }
                }

                originalCullingMasks.Clear();
                originalClearFlags.Clear();
                inEngineRenderingSuppressed = false;
                logger?.LogInfo("[RemixFramebufferPresenter] Restored in-engine camera rendering.");
            }
        }

        /// <summary>
        /// Returns the original clear flags of the camera before in-engine suppression was applied,
        /// or the current clearFlags if unsuppressed.
        /// </summary>
        public CameraClearFlags GetOriginalClearFlags(Camera cam)
        {
            if (cam != null && originalClearFlags.TryGetValue(cam, out var flags))
            {
                return flags;
            }
            return cam != null ? cam.clearFlags : CameraClearFlags.Skybox;
        }

        private void TearDownUIOverlay()
        {
            if (uiOverlay != null)
            {
                uiOverlay.RestoreUICameras();
                uiOverlay.Destroy();
                uiOverlay = null;
            }

            uiDetector?.RestoreCanvases();
        }

        public void RestoreInEngineRendering()
        {
            RestoreCameraSuppression();
            TearDownUIOverlay();
        }

        public void Cleanup()
        {
            RemixWindowManager.UpdateCursorVisibility(false);
            RestoreInEngineRendering();
        }
    }
}
