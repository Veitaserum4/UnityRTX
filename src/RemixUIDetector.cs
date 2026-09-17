using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;

namespace UnityRemix
{
    /// <summary>
    /// Automatically detects and classifies scene Cameras and Canvases into
    /// World Cameras (3D scene to suppress in-engine) and UI Cameras (HUD, menus,
    /// viewmodels, and overlays to keep active and render on top).
    /// </summary>
    public class RemixUIDetector
    {
        private readonly ManualLogSource logger;
        private readonly ConfigEntry<bool> configAutoDetectUI;
        private readonly ConfigEntry<string> configUICameraNames;
        private readonly ConfigEntry<string> configCameraName;

        private readonly List<Camera> uiCameras = new List<Camera>();
        private readonly List<Camera> worldCameras = new List<Camera>();
        private readonly Dictionary<Canvas, RenderMode> originalCanvasRenderModes = new Dictionary<Canvas, RenderMode>();
        private readonly Dictionary<Canvas, Camera> originalCanvasCameras = new Dictionary<Canvas, Camera>();

        // UI keywords: checked via token matching or substring for longer keywords
        private static readonly string[] UIKeywords = new string[]
        {
            "ui", "hud", "canvas", "menu", "gui", "overlay", "interface",
            "crosshair", "reticle", "cursor", "viewmodel", "gun", "weapon",
            "text", "subtitles", "scoreboard", "minimap", "radar", "dialogue", "chat"
        };

        // Explicitly excluded camera name patterns (post-processing, blit, utility, physics cameras)
        private static readonly string[] ExcludedKeywords = new string[]
        {
            "virtual", "postprocess", "post-process", "blit", "effect",
            "final", "downscale", "pixel", "shadow", "depth", "normal",
            "skybox", "reflection", "portal", "water"
        };

        public static bool MatchesExcludedKeyword(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            string lower = name.ToLowerInvariant();
            for (int i = 0; i < ExcludedKeywords.Length; i++)
            {
                if (lower.Contains(ExcludedKeywords[i])) return true;
            }
            return false;
        }

        public static bool MatchesUIKeyword(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            string lower = name.ToLowerInvariant();

            if (MatchesExcludedKeyword(name)) return false;

            char[] delims = new char[] { ' ', '_', '-', '/', '.', ':', '(', ')' };
            string[] tokens = lower.Split(delims, StringSplitOptions.RemoveEmptyEntries);

            for (int i = 0; i < tokens.Length; i++)
            {
                string token = tokens[i];
                for (int j = 0; j < UIKeywords.Length; j++)
                {
                    if (token == UIKeywords[j]) return true;
                }
            }

            for (int j = 0; j < UIKeywords.Length; j++)
            {
                string kw = UIKeywords[j];
                if (kw.Length > 2 && lower.Contains(kw)) return true;
            }

            return false;
        }

        public IReadOnlyList<Camera> UICameras => uiCameras;
        public IReadOnlyList<Camera> WorldCameras => worldCameras;

        public RemixUIDetector(
            ManualLogSource logger,
            ConfigEntry<bool> autoDetectUI,
            ConfigEntry<string> uiCameraNames,
            ConfigEntry<string> cameraName)
        {
            this.logger = logger;
            this.configAutoDetectUI = autoDetectUI;
            this.configUICameraNames = uiCameraNames;
            this.configCameraName = cameraName;
        }

        private static string GetLayerNames(int mask)
        {
            var layers = new List<string>();
            for (int i = 0; i < 32; i++)
            {
                if ((mask & (1 << i)) != 0)
                {
                    string name = LayerMask.LayerToName(i);
                    layers.Add(string.IsNullOrEmpty(name) ? i.ToString() : $"{name}({i})");
                }
            }
            return layers.Count > 0 ? string.Join(",", layers) : "Nothing(0)";
        }

        public static string GetHierarchyPath(Transform t)
        {
            if (t == null) return "null";
            string path = t.name;
            while (t.parent != null)
            {
                t = t.parent;
                path = t.name + "/" + path;
            }
            return path;
        }

        public void DumpUIState(string triggerReason)
        {
            logger?.LogInfo($"[RemixUIDetector] ==================== UI STATE DUMP ({triggerReason}) ====================");
            
            var allCameras = Camera.allCameras;
            if (allCameras == null || allCameras.Length == 0)
                allCameras = UnityEngine.Object.FindObjectsOfType<Camera>();

            logger?.LogInfo($"[RemixUIDetector] --- Cameras ({allCameras.Length} active/enabled) ---");
            foreach (var cam in allCameras)
            {
                if (cam == null) continue;
                bool isUI = uiCameras.Contains(cam);
                bool isWorld = worldCameras.Contains(cam);
                string classification = isUI ? "UI" : (isWorld ? "World" : "Unclassified");
                logger?.LogInfo($"[RemixUIDetector]   Camera '{cam.name}' [{GetHierarchyPath(cam.transform)}]: class={classification}, depth={cam.depth}, clear={cam.clearFlags}, mask=0x{cam.cullingMask:X} ({GetLayerNames(cam.cullingMask)}), target='{cam.targetTexture?.name ?? "none"}', active={cam.gameObject.activeInHierarchy}, enabled={cam.enabled}");
            }

            var allCanvases = Resources.FindObjectsOfTypeAll<Canvas>();
            var activeCanvases = UnityEngine.Object.FindObjectsOfType<Canvas>();
            logger?.LogInfo($"[RemixUIDetector] --- Canvases ({allCanvases.Length} loaded in scene/assets, {activeCanvases.Length} active) ---");
            foreach (var canvas in allCanvases)
            {
                if (canvas == null) continue;
                if (string.IsNullOrEmpty(canvas.gameObject.scene.name)) continue;

                int graphicCount = canvas.GetComponentsInChildren<UnityEngine.UI.Graphic>(true).Length;
                logger?.LogInfo($"[RemixUIDetector]   Canvas '{canvas.name}' [{GetHierarchyPath(canvas.transform)}]: scene='{canvas.gameObject.scene.name}', layer={LayerMask.LayerToName(canvas.gameObject.layer)}({canvas.gameObject.layer}), mode={canvas.renderMode}, cam='{canvas.worldCamera?.name ?? "none"}', planeDist={canvas.planeDistance:F2}, order={canvas.sortingOrder}, activeInHierarchy={canvas.gameObject.activeInHierarchy}, enabled={canvas.enabled}, graphics={graphicCount}");
            }
            logger?.LogInfo($"[RemixUIDetector] ==========================================================================");
        }

        /// <summary>
        /// Scans all active cameras in the scene and categorizes them into World and UI cameras.
        /// </summary>
        public void Refresh(Camera preferredWorldCamera = null)
        {
            uiCameras.Clear();
            worldCameras.Clear();

            var allCameras = Camera.allCameras;
            if (allCameras == null || allCameras.Length == 0)
            {
                allCameras = UnityEngine.Object.FindObjectsOfType<Camera>();
            }

            // User manual override camera names
            var manualUINames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(configUICameraNames?.Value))
            {
                var parts = configUICameraNames.Value.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var p in parts)
                {
                    manualUINames.Add(p.Trim());
                }
            }

            // Find known canvases and their worldCameras
            var canvases = UnityEngine.Object.FindObjectsOfType<Canvas>();
            var canvasCameras = new HashSet<Camera>();

            logger?.LogInfo($"[RemixUIDetector] --- Scan Started ({allCameras.Length} cameras, {canvases.Length} active canvases) ---");

            foreach (var canvas in canvases)
            {
                if (canvas == null) continue;
                logger?.LogInfo($"[RemixUIDetector]   Canvas '{canvas.name}' [{GetHierarchyPath(canvas.transform)}, layer={LayerMask.LayerToName(canvas.gameObject.layer)}({canvas.gameObject.layer}), mode={canvas.renderMode}, cam='{canvas.worldCamera?.name ?? "none"}', planeDist={canvas.planeDistance:F2}, order={canvas.sortingOrder}, active={canvas.gameObject.activeInHierarchy}, enabled={canvas.enabled}]");

                if (canvas.renderMode == RenderMode.ScreenSpaceCamera && canvas.worldCamera != null)
                {
                    canvasCameras.Add(canvas.worldCamera);
                }
            }

            foreach (var cam in allCameras)
            {
                if (cam == null || cam == dedicatedUICamera) continue;
                logger?.LogInfo($"[RemixUIDetector]   Camera '{cam.name}' [{GetHierarchyPath(cam.transform)}, depth={cam.depth}, clear={cam.clearFlags}, cullingMask=0x{cam.cullingMask:X} ({GetLayerNames(cam.cullingMask)}), targetTex='{cam.targetTexture?.name ?? "none"}', near={cam.nearClipPlane:F2}, far={cam.farClipPlane:F2}, parent='{cam.transform.parent?.name ?? "root"}', active={cam.gameObject.activeInHierarchy}, enabled={cam.enabled}]");
            }

            int uiLayer = LayerMask.NameToLayer("UI");
            int uiLayerBit = uiLayer >= 0 ? (1 << uiLayer) : 0x20;

            // Determine primary 3D world camera
            Camera primaryWorld = preferredWorldCamera ?? Camera.main;
            if (primaryWorld == null && !string.IsNullOrEmpty(configCameraName?.Value))
            {
                primaryWorld = allCameras.FirstOrDefault(c =>
                    c != null && c.name.Equals(configCameraName.Value, StringComparison.OrdinalIgnoreCase));
            }

            foreach (var cam in allCameras)
            {
                if (cam == null || cam == dedicatedUICamera) continue;

                string camName = cam.name ?? "";

                // 1. Manual override from config takes highest priority
                if (manualUINames.Contains(camName))
                {
                    uiCameras.Add(cam);
                    logger?.LogInfo($"[RemixUIDetector] Camera '{camName}' classified as UI (manual override)");
                    continue;
                }

                // If camera is explicitly the configured 3D main camera, classify as World
                if (cam == primaryWorld)
                {
                    worldCameras.Add(cam);
                    logger?.LogInfo($"[RemixUIDetector] Camera '{camName}' classified as World (primary 3D camera)");
                    continue;
                }

                // If camera matches excluded keywords (post-processing, virtual, shadow), classify as World
                if (MatchesExcludedKeyword(camName))
                {
                    worldCameras.Add(cam);
                    logger?.LogInfo($"[RemixUIDetector] Camera '{camName}' classified as World (matches excluded postprocess/utility keyword)");
                    continue;
                }

                if (configAutoDetectUI != null && !configAutoDetectUI.Value)
                {
                    // Auto-detect disabled: all non-manual cameras treated as World
                    worldCameras.Add(cam);
                    continue;
                }

                // 2. Name-based heuristics with tokenized matching
                bool nameMatch = MatchesUIKeyword(camName);

                // 3. Canvas association
                bool isCanvasCam = canvasCameras.Contains(cam);

                // 4. Culling mask heuristics: does it render the UI layer or avoid layer 0 (Default)?
                bool rendersUILayer = (cam.cullingMask & uiLayerBit) != 0;
                bool avoidsDefaultLayer = (cam.cullingMask & 1) == 0; // Layer 0 = Default

                // Count set bits in cullingMask
                int bitCount = CountBits((uint)cam.cullingMask);
                bool hasRestrictedLayers = bitCount <= 3 && avoidsDefaultLayer;

                // 5. ClearFlags heuristics: overlay cameras commonly use Depth or Nothing
                bool isOverlayClear = cam.clearFlags == CameraClearFlags.Depth || cam.clearFlags == CameraClearFlags.Nothing;
                bool higherDepth = primaryWorld != null && cam.depth > primaryWorld.depth;

                // Decision logic: A UI camera must either match UI name, have a Canvas, or render exclusively UI layers
                if (nameMatch || isCanvasCam || (rendersUILayer && avoidsDefaultLayer))
                {
                    uiCameras.Add(cam);
                    logger?.LogInfo($"[RemixUIDetector] Camera '{camName}' classified as UI (name:{nameMatch}, canvas:{isCanvasCam}, uiLayer:{rendersUILayer}, depth:{cam.depth})");
                }
                else
                {
                    worldCameras.Add(cam);
                    logger?.LogInfo($"[RemixUIDetector] Camera '{camName}' classified as World (cullingMask:0x{cam.cullingMask:X}, depth:{cam.depth})");
                }
            }

            // Ensure dedicated UI camera exists to render ScreenSpaceOverlay canvases cleanly
            if (dedicatedUICamera == null)
            {
                var go = new GameObject("UnityRemix_DedicatedUICamera");
                UnityEngine.Object.DontDestroyOnLoad(go);
                dedicatedUICamera = go.AddComponent<Camera>();
                dedicatedUICamera.depth = 100;
                dedicatedUICamera.clearFlags = CameraClearFlags.SolidColor;
                dedicatedUICamera.backgroundColor = new Color(0, 0, 0, 0);
                dedicatedUICamera.nearClipPlane = 0.1f;
                dedicatedUICamera.farClipPlane = 1000f;
                dedicatedUICamera.cullingMask = uiLayerBit; // UI layer only! Never Default (0)
                logger?.LogInfo("[RemixUIDetector] Created dedicated UI camera for overlay canvases.");
            }
            dedicatedUICamera.enabled = true;
            if (!uiCameras.Contains(dedicatedUICamera))
            {
                uiCameras.Add(dedicatedUICamera);
            }

            // Strictly isolate UI cameras: never render layer 0 (Default 3D world scene)
            foreach (var cam in uiCameras)
            {
                if (cam != null && cam != dedicatedUICamera)
                {
                    cam.cullingMask &= ~1; // Strip Default (0)
                    cam.cullingMask |= uiLayerBit; // Include UI (5)
                }
            }

            // Order UI cameras ascending by depth so they render in natural sequence
            uiCameras.Sort((a, b) => a.depth.CompareTo(b.depth));
            logger?.LogInfo($"[RemixUIDetector] Scan complete: {worldCameras.Count} World Cameras, {uiCameras.Count} UI Cameras detected.");
        }

        public Camera DedicatedUICamera => dedicatedUICamera;
        private Camera dedicatedUICamera;

        /// <summary>
        /// Routes ScreenSpaceOverlay Canvases to render through the dedicated UI camera in ScreenSpaceCamera mode
        /// so their contents can be captured into a transparent UI texture.
        /// </summary>
        public void RouteOverlayCanvasesToCamera(Camera targetCam = null)
        {
            Camera uiCamera = targetCam ?? dedicatedUICamera ?? (uiCameras.Count > 0 ? uiCameras[0] : null);
            if (uiCamera == null) return;

            var canvases = UnityEngine.Object.FindObjectsOfType<Canvas>();
            foreach (var canvas in canvases)
            {
                if (canvas == null) continue;

                // Skip loading blockers whose sole purpose is full-screen blackout during load transitions
                if (canvas.name.Equals("Loading Blocker", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // Never modify WorldSpace canvases (they belong in 3D level geometry)
                if (canvas.renderMode == RenderMode.WorldSpace)
                {
                    continue;
                }

                bool isOverlay = canvas.renderMode == RenderMode.ScreenSpaceOverlay;
                bool needsRebinding = canvas.renderMode == RenderMode.ScreenSpaceCamera && 
                    (canvas.worldCamera == null || worldCameras.Contains(canvas.worldCamera) || !canvas.worldCamera.enabled || !canvas.worldCamera.gameObject.activeInHierarchy);

                if (isOverlay || needsRebinding)
                {
                    SanitizeAndIncludeCanvasLayers(uiCamera, canvas.gameObject);

                    if (!originalCanvasRenderModes.ContainsKey(canvas))
                    {
                        originalCanvasRenderModes[canvas] = canvas.renderMode;
                        originalCanvasCameras[canvas] = canvas.worldCamera;
                    }

                    canvas.renderMode = RenderMode.ScreenSpaceCamera;
                    canvas.worldCamera = uiCamera;

                    // Ensure plane distance is at standard healthy distance (not pressed against near clip)
                    if (canvas.planeDistance < 10.0f || canvas.planeDistance > 500.0f)
                    {
                        canvas.planeDistance = 100.0f;
                    }

                    // Ensure GraphicRaycaster does not block clicks with 3D scene physics colliders
                    var raycaster = canvas.GetComponent<UnityEngine.UI.GraphicRaycaster>();
                    if (raycaster != null)
                    {
                        raycaster.blockingObjects = UnityEngine.UI.GraphicRaycaster.BlockingObjects.None;
                    }

                    logger?.LogInfo($"[RemixUIDetector] Routed Canvas '{canvas.name}' [{GetHierarchyPath(canvas.transform)}] to ScreenSpaceCamera (cam: '{uiCamera.name}', planeDist: {canvas.planeDistance:F2}, mask: 0x{uiCamera.cullingMask:X})");
                }
            }
        }

        /// <summary>
        /// Routes VideoPlayer components (e.g. intro cutscenes) targeting suppressed world cameras
        /// to render through the UI camera so they display on the transparent UI overlay.
        /// </summary>
        public void RouteVideoPlayersToCamera(Camera uiCamera)
        {
            if (uiCamera == null) return;

            try
            {
                var vpType = Type.GetType("UnityEngine.Video.VideoPlayer, UnityEngine.VideoModule");
                if (vpType == null) return;

                var videoPlayers = UnityEngine.Object.FindObjectsOfType(vpType);
                if (videoPlayers == null || videoPlayers.Length == 0) return;

                var targetCamProp = vpType.GetProperty("targetCamera");
                var renderModeProp = vpType.GetProperty("renderMode");

                foreach (var vp in videoPlayers)
                {
                    if (vp == null) continue;
                    var modeObj = renderModeProp?.GetValue(vp);
                    if (modeObj != null)
                    {
                        int mode = (int)modeObj;
                        // 0 = CameraFarPlane, 1 = CameraNearPlane
                        if (mode == 0 || mode == 1)
                        {
                            var curCam = targetCamProp?.GetValue(vp) as Camera;
                            if (curCam != uiCamera)
                            {
                                targetCamProp?.SetValue(vp, uiCamera);
                                renderModeProp?.SetValue(vp, Enum.ToObject(renderModeProp.PropertyType, 1));
                                logger?.LogInfo($"[RemixUIDetector] Routed VideoPlayer '{(vp as Component)?.name}' to UI camera '{uiCamera.name}'");
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                logger?.LogWarning($"[RemixUIDetector] Failed to route VideoPlayers: {ex.Message}");
            }
        }

        private static void SanitizeAndIncludeCanvasLayers(Camera cam, GameObject root)
        {
            if (cam == null || root == null) return;
            int uiLayer = LayerMask.NameToLayer("UI");
            if (uiLayer < 0) uiLayer = 5;

            // For ScreenSpaceOverlay canvases, ensure all UI elements are on UI layer so UI camera draws them
            root.layer = uiLayer;

            var transforms = root.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < transforms.Length; i++)
            {
                transforms[i].gameObject.layer = uiLayer;
            }

            // Ensure UI layer is included in camera culling mask
            cam.cullingMask |= (1 << uiLayer);
        }

        /// <summary>
        /// Restores original Canvas render modes and world cameras.
        /// </summary>
        public void RestoreCanvases()
        {
            foreach (var kvp in originalCanvasRenderModes)
            {
                var canvas = kvp.Key;
                if (canvas != null)
                {
                    canvas.renderMode = kvp.Value;
                    if (originalCanvasCameras.TryGetValue(canvas, out var cam))
                    {
                        canvas.worldCamera = cam;
                    }
                }
            }

            originalCanvasRenderModes.Clear();
            originalCanvasCameras.Clear();
        }

        private static int CountBits(uint v)
        {
            v = v - ((v >> 1) & 0x55555555);
            v = (v & 0x33333333) + ((v >> 2) & 0x33333333);
            return (int)((((v + (v >> 4)) & 0x0F0F0F0F) * 0x01010101) >> 24);
        }
    }
}
