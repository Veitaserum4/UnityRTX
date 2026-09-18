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
        private readonly HashSet<int> loggedRoutedCanvases = new HashSet<int>();
        private readonly HashSet<int> loggedSanitizedCanvases = new HashSet<int>();
        private int lastLoggedWorldCamCount = -1;
        private int lastLoggedUICamCount = -1;

        // UI keywords: checked via token matching or substring for longer keywords
        private static readonly string[] UIKeywords = new string[]
        {
            "ui", "hud", "canvas", "menu", "gui", "overlay", "interface",
            "crosshair", "reticle", "cursor", "inventory",
            "text", "subtitles", "scoreboard", "minimap", "radar", "dialogue", "chat"
        };

        /// <summary>
        /// Bitmask of layers currently containing active 3D geometry (MeshRenderer or SkinnedMeshRenderer).
        /// Updated during Refresh() so UI cameras can exclude 3D geometry from the 2D overlay texture.
        /// </summary>
        public static int CurrentThreeDLayerMask { get; private set; }

        public static int ComputeThreeDLayerMask()
        {
            int mask = 0;
            var mrs = UnityEngine.Object.FindObjectsOfType<MeshRenderer>();
            for (int i = 0; i < mrs.Length; i++)
            {
                var r = mrs[i];
                if (r != null && r.enabled && r.gameObject.activeInHierarchy)
                {
                    mask |= (1 << r.gameObject.layer);
                }
            }

            var smrs = UnityEngine.Object.FindObjectsOfType<SkinnedMeshRenderer>();
            for (int i = 0; i < smrs.Length; i++)
            {
                var sr = smrs[i];
                if (sr != null && sr.enabled && sr.gameObject.activeInHierarchy)
                {
                    mask |= (1 << sr.gameObject.layer);
                }
            }

            return mask;
        }

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

        private bool hasDumpedInitialUIState = false;

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
                string targetTexStr = cam.targetTexture != null
                    ? $"{cam.targetTexture.name} ({cam.targetTexture.width}x{cam.targetTexture.height}, fmt={cam.targetTexture.format})"
                    : "none";
                string urpInfo = "";
                try
                {
                    var addDataCamType = Type.GetType("UnityEngine.Rendering.Universal.UniversalAdditionalCameraData, Unity.RenderPipelines.Universal.Runtime");
                    if (addDataCamType != null)
                    {
                        var comp = cam.GetComponent(addDataCamType);
                        if (comp != null)
                        {
                            var rType = addDataCamType.GetProperty("renderType")?.GetValue(comp, null);
                            var post = addDataCamType.GetProperty("renderPostProcessing")?.GetValue(comp, null);
                            var aa = addDataCamType.GetProperty("antialiasing")?.GetValue(comp, null);
                            urpInfo = $", URP(renderType={rType}, post={post}, aa={aa})";
                        }
                    }
                }
                catch { }

                logger?.LogInfo($"[RemixUIDetector]   Camera '{cam.name}' [{GetHierarchyPath(cam.transform)}]: class={classification}, depth={cam.depth}, clear={cam.clearFlags}, bg=RGBA({cam.backgroundColor.r:F2},{cam.backgroundColor.g:F2},{cam.backgroundColor.b:F2},{cam.backgroundColor.a:F2}), mask=0x{cam.cullingMask:X} ({GetLayerNames(cam.cullingMask)}), target='{targetTexStr}', active={cam.gameObject.activeInHierarchy}, enabled={cam.enabled}{urpInfo}");
            }

            var allCanvases = Resources.FindObjectsOfTypeAll<Canvas>();
            var activeCanvases = UnityEngine.Object.FindObjectsOfType<Canvas>();
            logger?.LogInfo($"[RemixUIDetector] --- Canvases ({allCanvases.Length} loaded in scene/assets, {activeCanvases.Length} active) ---");
            foreach (var canvas in allCanvases)
            {
                if (canvas == null) continue;
                if (string.IsNullOrEmpty(canvas.gameObject.scene.name)) continue;

                var activeGraphics = canvas.GetComponentsInChildren<UnityEngine.UI.Graphic>(false);
                int totalGraphics = canvas.GetComponentsInChildren<UnityEngine.UI.Graphic>(true).Length;
                logger?.LogInfo($"[RemixUIDetector]   Canvas '{canvas.name}' [{GetHierarchyPath(canvas.transform)}]: scene='{canvas.gameObject.scene.name}', layer={LayerMask.LayerToName(canvas.gameObject.layer)}({canvas.gameObject.layer}), mode={canvas.renderMode}, cam='{canvas.worldCamera?.name ?? "none"}', planeDist={canvas.planeDistance:F2}, order={canvas.sortingOrder}, activeInHierarchy={canvas.gameObject.activeInHierarchy}, enabled={canvas.enabled}, activeGraphics={activeGraphics.Length}/{totalGraphics}");

                if (canvas.gameObject.activeInHierarchy && canvas.enabled)
                {
                    foreach (var g in activeGraphics)
                    {
                        if (g == null) continue;
                        var rt = g.rectTransform;
                        string dim = rt != null ? $"{rt.rect.width:F0}x{rt.rect.height:F0}" : "unknown";
                        string anchors = rt != null ? $"anchors=({rt.anchorMin.x:F2},{rt.anchorMin.y:F2})-({rt.anchorMax.x:F2},{rt.anchorMax.y:F2})" : "";
                        string colorStr = $"RGBA({g.color.r:F2},{g.color.g:F2},{g.color.b:F2},{g.color.a:F2})";
                        string shaderName = g.material != null && g.material.shader != null ? g.material.shader.name : "Default";

                        bool isFullScreen = false;
                        if (rt != null && (rt.rect.width >= Screen.width * 0.8f && rt.rect.height >= Screen.height * 0.8f))
                        {
                            isFullScreen = true;
                        }
                        else if (rt != null && rt.anchorMin == Vector2.zero && rt.anchorMax == Vector2.one)
                        {
                            isFullScreen = true;
                        }

                        string flag = isFullScreen ? " [*** POTENTIAL FULLSCREEN BACKGROUND/OVERLAY ***]" : "";
                        logger?.LogInfo($"[RemixUIDetector]       Graphic '{g.name}' [{g.GetType().Name}]: size={dim}, {anchors}, color={colorStr}, shader='{shaderName}'{flag}");
                    }
                }
            }
            logger?.LogInfo($"[RemixUIDetector] ==========================================================================");
        }

        private string lastDumpedScene = null;

        /// <summary>
        /// Scans all active cameras in the scene and categorizes them into World and UI cameras.
        /// </summary>
        public void Refresh(Camera preferredWorldCamera = null)
        {
            uiCameras.Clear();
            worldCameras.Clear();

            string curScene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
            if (curScene != lastDumpedScene && !string.IsNullOrEmpty(curScene))
            {
                lastDumpedScene = curScene;
                DumpUIState($"Scene Transition '{curScene}'");
            }
            else if (!hasDumpedInitialUIState)
            {
                var activeCams = Camera.allCameras;
                var activeCanvs = UnityEngine.Object.FindObjectsOfType<Canvas>();
                if ((activeCams != null && activeCams.Length > 0) || (activeCanvs != null && activeCanvs.Length > 0))
                {
                    hasDumpedInitialUIState = true;
                    DumpUIState("Initial UI Discovery");
                }
            }

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
            var canvases = UnityEngine.Object.FindObjectsOfType<Canvas>(true);
            var canvasCameras = new HashSet<Camera>();

            foreach (var canvas in canvases)
            {
                if (canvas == null) continue;

                if (canvas.renderMode == RenderMode.ScreenSpaceCamera && canvas.worldCamera != null)
                {
                    canvasCameras.Add(canvas.worldCamera);
                }
            }

            CurrentThreeDLayerMask = ComputeThreeDLayerMask();
            int uiLayer = LayerMask.NameToLayer("UI");
            int uiLayerBit = uiLayer >= 0 ? (1 << uiLayer) : 0x20;
            int nonUIThreeDMask = CurrentThreeDLayerMask & ~uiLayerBit;

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
                    continue;
                }

                // If camera is explicitly the configured 3D main camera, classify as World
                if (cam == primaryWorld)
                {
                    worldCameras.Add(cam);
                    continue;
                }

                // If camera matches excluded keywords (post-processing, virtual, shadow), classify as World
                if (MatchesExcludedKeyword(camName))
                {
                    worldCameras.Add(cam);
                    continue;
                }

                // If camera renders 3D meshes and has NO UI canvases in or associated with it, classify as World
                bool renders3D = (cam.cullingMask & nonUIThreeDMask) != 0;
                bool hasAssociatedCanvas = canvasCameras.Contains(cam) || cam.GetComponentInChildren<Canvas>() != null;
                if (renders3D && !hasAssociatedCanvas)
                {
                    worldCameras.Add(cam);
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
                }
                else
                {
                    worldCameras.Add(cam);
                }
            }

            // Check if any native UI cameras were detected (e.g. HUD Camera in ULTRAKILL, UICamera in White Knuckle)
            bool hasNativeUICameras = uiCameras.Count > 0;

            if (!hasNativeUICameras)
            {
                // Single-camera or URP game (e.g. PEAK): No native UI cameras exist.
                // Dedicated UI camera is required to render ScreenSpaceOverlay canvases cleanly.
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
                    dedicatedUICamera.cullingMask = uiLayerBit; // UI layer only! Never Default (0) or 3D world layers!
                    ConfigureSRPRenderData(dedicatedUICamera);
                    logger?.LogInfo("[RemixUIDetector] Created dedicated UI camera for overlay canvases.");
                }
                dedicatedUICamera.enabled = true;
                dedicatedUICamera.cullingMask = uiLayerBit;
                if (primaryWorld != null)
                {
                    SyncDedicatedUICameraTransform(primaryWorld);
                }
                if (!uiCameras.Contains(dedicatedUICamera))
                {
                    uiCameras.Add(dedicatedUICamera);
                }
            }
            else
            {
                // Multi-camera game with native UI camera: Disable dedicated UI camera to prevent duplicate HUD/overlay rendering!
                if (dedicatedUICamera != null)
                {
                    dedicatedUICamera.enabled = false;
                    uiCameras.Remove(dedicatedUICamera);
                }
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
            if (worldCameras.Count != lastLoggedWorldCamCount || uiCameras.Count != lastLoggedUICamCount)
            {
                lastLoggedWorldCamCount = worldCameras.Count;
                lastLoggedUICamCount = uiCameras.Count;
                logger?.LogInfo($"[RemixUIDetector] Scan complete: {worldCameras.Count} World Cameras, {uiCameras.Count} UI Cameras detected.");
            }
        }

        public Camera DedicatedUICamera => (dedicatedUICamera != null && dedicatedUICamera.enabled) ? dedicatedUICamera : null;
        private Camera dedicatedUICamera;

        /// <summary>
        /// Synchronizes the dedicated UI camera's spatial transform and projection matrix with the active
        /// world/scene camera. This ensures ScreenSpaceCamera and WorldSpace HUD canvases project accurately
        /// matching the player's view frustum, aspect ratio, and field of view.
        /// </summary>
        public void SyncDedicatedUICameraTransform(Camera sourceCam)
        {
            if (dedicatedUICamera == null || sourceCam == null) return;

            var destT = dedicatedUICamera.transform;
            var srcT = sourceCam.transform;

            if (destT.position != srcT.position)
                destT.position = srcT.position;
            if (destT.rotation != srcT.rotation)
                destT.rotation = srcT.rotation;

            if (dedicatedUICamera.orthographic != sourceCam.orthographic)
                dedicatedUICamera.orthographic = sourceCam.orthographic;

            if (sourceCam.orthographic)
            {
                if (dedicatedUICamera.orthographicSize != sourceCam.orthographicSize)
                    dedicatedUICamera.orthographicSize = sourceCam.orthographicSize;
            }
            else
            {
                if (dedicatedUICamera.fieldOfView != sourceCam.fieldOfView)
                    dedicatedUICamera.fieldOfView = sourceCam.fieldOfView;
            }

            if (dedicatedUICamera.nearClipPlane != sourceCam.nearClipPlane)
                dedicatedUICamera.nearClipPlane = sourceCam.nearClipPlane;
            if (dedicatedUICamera.farClipPlane != sourceCam.farClipPlane)
                dedicatedUICamera.farClipPlane = sourceCam.farClipPlane;
            if (dedicatedUICamera.rect != sourceCam.rect)
                dedicatedUICamera.rect = sourceCam.rect;

            // Ensure dedicated UI camera depth renders on top of the world camera
            if (dedicatedUICamera.depth <= sourceCam.depth)
            {
                dedicatedUICamera.depth = sourceCam.depth + 10f;
            }
        }

        /// <summary>
        /// Configures SRP / URP camera data via reflection so that dedicated UI camera renders cleanly
        /// under Universal Render Pipeline without drawing shadows, post-processing, or volume overrides.
        /// </summary>
        public static void ConfigureSRPRenderData(Camera cam)
        {
            if (cam == null) return;
            try
            {
                var addDataCamType = Type.GetType("UnityEngine.Rendering.Universal.UniversalAdditionalCameraData, Unity.RenderPipelines.Universal.Runtime");
                if (addDataCamType != null)
                {
                    var comp = cam.GetComponent(addDataCamType) ?? cam.gameObject.AddComponent(addDataCamType);
                    if (comp != null)
                    {
                        // renderType = CameraRenderType.Base (0)
                        var renderTypeProp = addDataCamType.GetProperty("renderType");
                        if (renderTypeProp != null)
                        {
                            var enumVal = Enum.ToObject(renderTypeProp.PropertyType, 0);
                            renderTypeProp.SetValue(comp, enumVal);
                        }

                        // renderShadows = false
                        var shadowsProp = addDataCamType.GetProperty("renderShadows");
                        shadowsProp?.SetValue(comp, false);

                        // renderPostProcessing = false
                        var postProp = addDataCamType.GetProperty("renderPostProcessing");
                        postProp?.SetValue(comp, false);

                        // requiresDepthTexture = true (Ensure URP allocates and binds depth buffer for render target)
                        var depthTexProp = addDataCamType.GetProperty("requiresDepthTexture");
                        depthTexProp?.SetValue(comp, true);

                        // requiresColorTexture = false
                        var colorTexProp = addDataCamType.GetProperty("requiresColorTexture");
                        colorTexProp?.SetValue(comp, false);

                        // volumeLayerMask = 0 (don't evaluate world volume post-processing on UI)
                        var volMaskProp = addDataCamType.GetProperty("volumeLayerMask");
                        if (volMaskProp != null)
                        {
                            volMaskProp.SetValue(comp, (LayerMask)0);
                        }

                        // Explicitly select default renderer index 0
                        var setRendererMethod = addDataCamType.GetMethod("SetRenderer", new Type[] { typeof(int) });
                        setRendererMethod?.Invoke(comp, new object[] { 0 });
                    }
                }
            }
            catch (Exception)
            {
                // Silently ignore if not running URP or if reflection fails
            }
        }

        /// <summary>
        /// Routes ScreenSpaceOverlay Canvases to render through the dedicated UI camera in ScreenSpaceCamera mode
        /// so their contents can be captured into a transparent UI texture.
        /// </summary>
        public void RouteOverlayCanvasesToCamera(Camera targetCam = null)
        {
            Camera uiCamera = targetCam ?? DedicatedUICamera ?? (uiCameras.Count > 0 ? uiCameras[0] : null);
            if (uiCamera == null) return;

            var canvases = UnityEngine.Object.FindObjectsOfType<Canvas>(true);
            foreach (var canvas in canvases)
            {
                if (canvas == null) continue;

                // Skip loading blockers whose sole purpose is full-screen blackout during load transitions
                if (canvas.name.Equals("Loading Blocker", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // Skip canvases attached to 3D meshes (e.g. ammo counter on weapon bone) — they belong to 3D model geometry
                if (canvas.GetComponentInParent<SkinnedMeshRenderer>() != null || 
                    canvas.GetComponentInParent<MeshRenderer>() != null)
                {
                    continue;
                }

                // If this is a WorldSpace canvas attached to a camera or HUD hierarchy (e.g. ULTRAKILL's GunCanvas/StyleCanvas),
                // sanitize its elements to UI layer so the UI camera captures it cleanly without capturing 3D viewmodels.
                if (canvas.renderMode == RenderMode.WorldSpace)
                {
                    bool isCameraAttached = canvas.GetComponentInParent<Camera>() != null || HasCameraOrHUDInParent(canvas.transform);
                    if (isCameraAttached)
                    {
                        SanitizeAndIncludeCanvasLayers(uiCamera, canvas.gameObject);
                        if (loggedSanitizedCanvases.Add(canvas.GetInstanceID()))
                        {
                            logger?.LogInfo($"[RemixUIDetector] Sanitized camera-attached HUD Canvas '{canvas.name}' [{GetHierarchyPath(canvas.transform)}] to UI layer");
                        }
                    }
                    continue;
                }

                bool isOverlay = canvas.renderMode == RenderMode.ScreenSpaceOverlay;
                bool needsRebinding = canvas.renderMode == RenderMode.ScreenSpaceCamera && 
                    (canvas.worldCamera == null || !uiCameras.Contains(canvas.worldCamera) || !canvas.worldCamera.enabled || !canvas.worldCamera.gameObject.activeInHierarchy);

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

                    // Ensure plane distance is at standard healthy distance within camera frustum
                    // 100.0f aligns perfectly with GraphicRaycaster and CanvasScaler
                    float minPlane = uiCamera.nearClipPlane + 1.0f;
                    float maxPlane = Mathf.Max(minPlane + 1.0f, uiCamera.farClipPlane - 10.0f);
                    if (canvas.planeDistance < minPlane || canvas.planeDistance > maxPlane)
                    {
                        canvas.planeDistance = Mathf.Clamp(100.0f, minPlane, maxPlane);
                    }

                    // Ensure GraphicRaycaster does not block clicks with 3D scene physics colliders
                    var raycaster = canvas.GetComponent<UnityEngine.UI.GraphicRaycaster>();
                    if (raycaster != null)
                    {
                        raycaster.blockingObjects = UnityEngine.UI.GraphicRaycaster.BlockingObjects.None;
                    }

                    if (loggedRoutedCanvases.Add(canvas.GetInstanceID()))
                    {
                        logger?.LogInfo($"[RemixUIDetector] Routed Canvas '{canvas.name}' [{GetHierarchyPath(canvas.transform)}] to ScreenSpaceCamera (cam: '{uiCamera.name}', planeDist: {canvas.planeDistance:F2}, mask: 0x{uiCamera.cullingMask:X})");
                    }
                }
                else if (canvas.renderMode == RenderMode.ScreenSpaceCamera && canvas.worldCamera == uiCamera)
                {
                    // Canvas is already bound to uiCamera, but ensure all children (newly instantiated or toggled) are on UI layer
                    SanitizeAndIncludeCanvasLayers(uiCamera, canvas.gameObject);
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

        private static bool HasCameraOrHUDInParent(Transform t)
        {
            Transform cur = t;
            while (cur != null)
            {
                if (cur.GetComponent<Camera>() != null) return true;
                string n = cur.name.ToLowerInvariant();
                if (n.Contains("camera") || n.Contains("hud")) return true;
                cur = cur.parent;
            }
            return false;
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
            loggedSanitizedCanvases.Clear();
            loggedRoutedCanvases.Clear();
            lastLoggedWorldCamCount = -1;
            lastLoggedUICamCount = -1;
        }

        private static int CountBits(uint v)
        {
            v = v - ((v >> 1) & 0x55555555);
            v = (v & 0x33333333) + ((v >> 2) & 0x33333333);
            return (int)((((v + (v >> 4)) & 0x0F0F0F0F) * 0x01010101) >> 24);
        }
    }
}
