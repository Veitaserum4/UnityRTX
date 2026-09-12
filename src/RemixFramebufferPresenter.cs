using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;

namespace UnityRemix
{
    public enum SingleWindowMethod
    {
        Embedded = 0,
        Copy = 1
    }

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
        private ConfigEntry<SingleWindowMethod> configSingleWindowMethod;
        private ConfigEntry<bool> configDisableInEngineRendering;
        private ConfigEntry<bool> configAutoDetectUI;
        private ConfigEntry<string> configUICameraNames;
        private ConfigEntry<bool> configSingleWindowUIOverlay;

        // Tracking suppressed world cameras
        private readonly Dictionary<Camera, int> originalCullingMasks = new Dictionary<Camera, int>();
        private readonly Dictionary<Camera, CameraClearFlags> originalClearFlags = new Dictionary<Camera, CameraClearFlags>();
        private bool inEngineRenderingSuppressed = false;
        private int sceneRefreshCounter = 0;

        // Copy mode blitter reference
        private RemixCameraBlitter currentCameraBlitter;

        public RemixUIDetector UIDetector => uiDetector;
        public static bool IsSingleWindowUIActive { get; private set; }

        public void Initialize(
            ManualLogSource logger,
            RemixWindowManager windowManager,
            RemixCameraHandler cameraHandler,
            ConfigEntry<bool> singleWindow,
            ConfigEntry<SingleWindowMethod> singleWindowMethod,
            ConfigEntry<bool> disableInEngineRendering,
            ConfigEntry<bool> autoDetectUI,
            ConfigEntry<string> uiCameraNames,
            ConfigEntry<bool> singleWindowUIOverlay)
        {
            this.logger = logger;
            this.windowManager = windowManager;
            this.cameraHandler = cameraHandler;
            this.configSingleWindow = singleWindow;
            this.configSingleWindowMethod = singleWindowMethod;
            this.configDisableInEngineRendering = disableInEngineRendering;
            this.configAutoDetectUI = autoDetectUI;
            this.configUICameraNames = uiCameraNames;
            this.configSingleWindowUIOverlay = singleWindowUIOverlay;

            uiDetector = new RemixUIDetector(
                logger,
                autoDetectUI,
                uiCameraNames,
                null
            );

            UpdateSingleWindowUIActive();
            Application.runInBackground = true;
            logger?.LogInfo($"[RemixFramebufferPresenter] Initialized (SingleWindow: {singleWindow.Value}, Method: {singleWindowMethod.Value}, SuppressInEngine: {disableInEngineRendering.Value}, AutoDetectUI: {autoDetectUI.Value})");
        }

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr SetFocus(IntPtr hWnd);

        private void UpdateSingleWindowUIActive()
        {
            bool isSingle = configSingleWindow != null && configSingleWindow.Value;
            bool isEmbedded = configSingleWindowMethod != null && configSingleWindowMethod.Value == SingleWindowMethod.Embedded;
            bool isSuppressed = configDisableInEngineRendering != null && configDisableInEngineRendering.Value;
            IsSingleWindowUIActive = isSingle && isEmbedded && isSuppressed;
        }

        private int lastCameraCount = -1;
        private int lastCanvasCount = -1;

        public void OnSceneLoaded(UnityEngine.SceneManagement.Scene scene)
        {
            sceneRefreshCounter = 15; // Re-evaluate suppression over the next 15 frames to catch async objects
            lastCameraCount = -1;
            lastCanvasCount = -1;
        }

        public void Update(int frameCount)
        {
            if (configSingleWindow == null) return;

            UpdateSingleWindowUIActive();

            bool isSingle = configSingleWindow.Value;
            bool shouldSuppress = isSingle && configDisableInEngineRendering.Value;

            int currentCameraCount = Camera.allCamerasCount;
            bool cameraCountChanged = (currentCameraCount != lastCameraCount);
            bool periodicCheck = (frameCount % 60 == 0);
            bool shouldCheck = (shouldSuppress != inEngineRenderingSuppressed) || cameraCountChanged || periodicCheck || (sceneRefreshCounter > 0);

            if (shouldCheck)
            {
                if (sceneRefreshCounter > 0) sceneRefreshCounter--;

                int currentCanvasCount = UnityEngine.Object.FindObjectsOfType<Canvas>().Length;
                bool countsChanged = cameraCountChanged || (currentCanvasCount != lastCanvasCount);

                if (shouldSuppress != inEngineRenderingSuppressed || countsChanged)
                {
                    lastCameraCount = currentCameraCount;
                    lastCanvasCount = currentCanvasCount;

                    if (shouldSuppress)
                        ApplyInEngineRenderingSuppression();
                    else
                        RestoreInEngineRendering();
                }

                if (periodicCheck && uiDetector.UICameras.Count > 0)
                {
                    uiDetector.RouteVideoPlayersToCamera(uiDetector.UICameras[0]);
                }
            }

            if (frameCount % 300 == 0 && isSingle)
            {
                logger?.LogInfo($"[RemixFramebufferPresenter] Frame #{frameCount} Status: SingleWindow={isSingle}, Suppressed={inEngineRenderingSuppressed}, WorldCams={uiDetector.WorldCameras.Count}, UICams={uiDetector.UICameras.Count}, Canvases={lastCanvasCount}, OverlayActive={(uiOverlay != null)}");
            }

            // Sync embedded window bounds
            if (isSingle && configSingleWindowMethod.Value == SingleWindowMethod.Embedded && windowManager != null)
            {
                windowManager.SyncWindowBounds();
            }

            // Handle Alt+X detection for Remix ImGui
            bool altPressed = Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);
            if (altPressed && Input.GetKeyDown(KeyCode.X))
            {
                windowManager?.HandleAltX();
                logger?.LogInfo($"[RemixFramebufferPresenter] Alt+X pressed, RemixUIOpen: {RemixWindowManager.IsRemixUIOpen}");
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

            // Input diagnostic: verify mouse clicks reach Unity
            if (Input.GetMouseButtonDown(0))
            {
                var hovered = UnityEngine.EventSystems.EventSystem.current?.currentSelectedGameObject;
                logger?.LogInfo($"[InputDiag] Mouse click at {Input.mousePosition}, isFocused={Application.isFocused}, selected='{hovered?.name ?? "none"}'");
            }
        }

        public void OnEndOfFrame()
        {
            if (configSingleWindow != null && configSingleWindow.Value &&
                configSingleWindowMethod.Value == SingleWindowMethod.Embedded &&
                uiOverlay != null)
            {
                uiOverlay.UpdateOverlay();
            }
        }

        /// <summary>
        /// Suppresses Unity's 3D scene rasterization passes on World Cameras while keeping UI/HUD cameras active.
        /// </summary>
        public void ApplyInEngineRenderingSuppression()
        {
            if (uiDetector == null) return;

            Camera worldCam = cameraHandler?.CurrentCamera ?? Camera.main;
            uiDetector.Refresh(worldCam);

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

            logger?.LogInfo($"[RemixFramebufferPresenter] In-engine 3D rendering suppressed on {suppressedCount} World Cameras.");

            // Setup UI Presentation depending on single-window method
            if (configSingleWindowMethod.Value == SingleWindowMethod.Embedded)
            {
                SetupEmbeddedUIOverlay();
            }
            else if (configSingleWindowMethod.Value == SingleWindowMethod.Copy)
            {
                SetupCopyModeBlitter(worldCam);
            }

            inEngineRenderingSuppressed = true;
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
                uiOverlay = new RemixUIOverlay(logger, gameWnd);
                if (!uiOverlay.Initialize())
                {
                    uiOverlay = null;
                    return;
                }
            }

            if (uiOverlay != null && uiDetector.UICameras.Count > 0)
            {
                uiOverlay.ConfigureUICameras(uiDetector.UICameras);
                uiDetector.RouteOverlayCanvasesToCamera(uiDetector.UICameras[0]);
                uiDetector.RouteVideoPlayersToCamera(uiDetector.UICameras[0]);
            }
        }

        private void SetupCopyModeBlitter(Camera worldCam)
        {
            if (worldCam == null) worldCam = Camera.main;
            if (worldCam == null) return;

            currentCameraBlitter = worldCam.GetComponent<RemixCameraBlitter>();
            if (currentCameraBlitter == null)
            {
                currentCameraBlitter = worldCam.gameObject.AddComponent<RemixCameraBlitter>();
            }

            currentCameraBlitter.Initialize(windowManager, logger);
            currentCameraBlitter.SetBlitEnabled(true);
        }

        /// <summary>
        /// Restores original camera culling masks and clear flags.
        /// </summary>
        public void RestoreInEngineRendering()
        {
            if (uiOverlay != null)
            {
                uiOverlay.RestoreUICameras();
                uiOverlay.Destroy();
                uiOverlay = null;
            }

            uiDetector?.RestoreCanvases();

            if (currentCameraBlitter != null)
            {
                currentCameraBlitter.SetBlitEnabled(false);
            }

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

        public void Cleanup()
        {
            RestoreInEngineRendering();
        }
    }
}
