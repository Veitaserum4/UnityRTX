using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.Rendering;

namespace UnityRemix
{
    /// <summary>
    /// Manages native OpenRemix UI overlay rendering for SingleWindow Embedded mode.
    /// Captures the autodetected UI cameras to a RenderTexture and presents them with per-pixel alpha
    /// directly inside the OpenRemix Vulkan swapchain using asynchronous GPU readbacks and DrawScreenOverlay.
    /// </summary>
    public class RemixUIOverlay
    {
        private readonly ManualLogSource logger;

        private readonly BepInEx.Configuration.ConfigEntry<bool> configHideUIOnRemixMenu;
        private readonly BepInEx.Configuration.ConfigEntry<bool> configUIOverlayClearBlack;
        private static readonly object overlayApiLock = new object();
        private RenderTexture uiRenderTexture;
        private bool isReadbackPending = false;
        private bool isProcessingOverlay = false;
        private bool isOverlayVisible = false;
        private static volatile bool isAppFocused = true;
        private int lastPresentedNonZero = -1;
        private int currentWidth = 0;
        private int currentHeight = 0;

        // Camera capture state
        private readonly Dictionary<Camera, SavedCameraState> originalCameraStates = new Dictionary<Camera, SavedCameraState>();

        private struct SavedCameraState
        {
            public RenderTexture targetTexture;
            public CameraClearFlags clearFlags;
            public Color backgroundColor;
        }

        private byte[] processPixels;
        private byte[] nativeProcessPixels;

        public static RemixUIOverlay Instance { get; private set; }
        private readonly HashSet<Camera> managedCameras = new HashSet<Camera>();

        public static bool IsManagedUICamera(Camera cam)
        {
            if (cam == null || Instance == null) return false;
            return Instance.managedCameras.Contains(cam);
        }

        public RenderTexture UIRenderTexture => uiRenderTexture;

        public static bool TryGetOriginalCameraDimensions(Camera cam, out int width, out int height)
        {
            width = 0;
            height = 0;
            if (cam == null || Instance == null) return false;
            if (Instance.originalCameraStates.TryGetValue(cam, out var state) && state.targetTexture != null)
            {
                width = state.targetTexture.width;
                height = state.targetTexture.height;
                return width > 0 && height > 0;
            }
            return false;
        }

        public static RenderTexture GetOriginalTargetTexture(Camera cam)
        {
            if (cam == null) return null;
            if (Instance != null && Instance.originalCameraStates.TryGetValue(cam, out var state))
            {
                return state.targetTexture;
            }
            if (Instance != null && Instance.uiRenderTexture != null && cam.targetTexture == Instance.uiRenderTexture)
            {
                return null;
            }
            return cam.targetTexture;
        }

        public void RebindAllUICameras()
        {
            if (uiRenderTexture == null) return;
            int uiLayer = LayerMask.NameToLayer("UI");
            int uiLayerBit = uiLayer >= 0 ? (1 << uiLayer) : (1 << 5);
            int threeDMask = RemixUIDetector.CurrentThreeDLayerMask & ~uiLayerBit;

            var sortedCams = managedCameras.Where(c => c != null).OrderBy(c => c.depth).ToList();
            bool isFirst = true;
            foreach (var cam in sortedCams)
            {
                var targetClear = isFirst ? CameraClearFlags.SolidColor : CameraClearFlags.Depth;
                var targetBg = new Color(0, 0, 0, 0);

                var hook = cam.GetComponent<RemixUICameraHook>();
                if (hook != null)
                {
                    hook.clearFlags = targetClear;
                    hook.backgroundColor = targetBg;
                    hook.targetTexture = uiRenderTexture;
                }

                cam.targetTexture = uiRenderTexture;
                cam.clearFlags = targetClear;
                cam.backgroundColor = targetBg;
                if (cam.name == "UnityRemix_DedicatedUICamera")
                {
                    cam.cullingMask = uiLayerBit;
                }
                else
                {
                    cam.cullingMask &= ~threeDMask; // Ensure layers containing 3D meshes are never rendered by UI camera
                    cam.cullingMask &= ~1; // Strip Default (0)
                    cam.cullingMask |= uiLayerBit;
                }
                isFirst = false;
            }
        }

        public RemixUIOverlay(
            ManualLogSource logger,
            BepInEx.Configuration.ConfigEntry<bool> configHideUIOnRemixMenu = null,
            BepInEx.Configuration.ConfigEntry<bool> configUIOverlayClearBlack = null)
        {
            this.logger = logger;
            this.configHideUIOnRemixMenu = configHideUIOnRemixMenu;
            this.configUIOverlayClearBlack = configUIOverlayClearBlack;
            Instance = this;
        }

        public bool Initialize()
        {
            if (RemixAPI.DrawScreenOverlayFunc == null)
            {
                logger?.LogWarning("[RemixUIOverlay] Cannot initialize: OpenRemix DrawScreenOverlay is not available.");
                return false;
            }

            logger?.LogInfo("[RemixUIOverlay] Initialized OpenRemix native DrawScreenOverlay.");
            SubscribeSRPEvents();
            return true;
        }

        private bool srpEventSubscribed = false;

        private void SubscribeSRPEvents()
        {
            if (!srpEventSubscribed)
            {
                UnityEngine.Rendering.RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;
                UnityEngine.Rendering.RenderPipelineManager.endCameraRendering += OnEndCameraRendering;
                srpEventSubscribed = true;
            }
        }

        private void UnsubscribeSRPEvents()
        {
            if (srpEventSubscribed)
            {
                UnityEngine.Rendering.RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
                UnityEngine.Rendering.RenderPipelineManager.endCameraRendering -= OnEndCameraRendering;
                srpEventSubscribed = false;
            }
        }

        private static int srpLogCount = 0;

        private void OnBeginCameraRendering(UnityEngine.Rendering.ScriptableRenderContext context, Camera cam)
        {
            if (cam != null && managedCameras.Contains(cam))
            {
                var hook = cam.GetComponent<RemixUICameraHook>();
                if (hook != null)
                {
                    hook.EnsureConfigured("SRP_beginCameraRendering");
                }
                else
                {
                    cam.targetTexture = uiRenderTexture;
                }

                if (srpLogCount < 5)
                {
                    srpLogCount++;
                    string rtInfo = cam.targetTexture != null
                        ? $"{cam.targetTexture.width}x{cam.targetTexture.height} ({cam.targetTexture.format})"
                        : "null";
                    string addDataInfo = "";
                    var addData = cam.GetComponent("UniversalAdditionalCameraData");
                    if (addData != null)
                    {
                        var renderPostType = addData.GetType().GetProperty("renderPostProcessing");
                        if (renderPostType != null)
                        {
                            addDataInfo = $", URP_renderPostProcessing={renderPostType.GetValue(addData, null)}";
                        }
                    }
                    logger?.LogInfo($"[RemixUIOverlay] SRP beginCameraRendering: cam='{cam.name}', depth={cam.depth}, clear={cam.clearFlags}, bg=RGBA({cam.backgroundColor.r:F2},{cam.backgroundColor.g:F2},{cam.backgroundColor.b:F2},{cam.backgroundColor.a:F2}), mask=0x{cam.cullingMask:X}, targetTex={rtInfo}{addDataInfo}");
                }
            }
        }

        private void OnEndCameraRendering(UnityEngine.Rendering.ScriptableRenderContext context, Camera cam)
        {
            if (cam != null && managedCameras.Contains(cam))
            {
                // In SRP/URP, when a managed UI camera finishes rendering, trigger overlay presentation immediately
                UpdateOverlay();
            }
        }

        /// <summary>
        /// Directs autodetected UI cameras to render into the transparent UI RenderTexture.
        /// </summary>
        public void ConfigureUICameras(IReadOnlyList<Camera> uiCameras)
        {
            if (uiCameras == null || uiCameras.Count == 0) return;

            int width = Screen.width > 0 ? Screen.width : 1920;
            int height = Screen.height > 0 ? Screen.height : 1080;

            EnsureRenderTexture(width, height);

            // Restore any cameras that were previously managed but are no longer in uiCameras
            var unmanagedCams = new List<Camera>();
            foreach (var kvp in originalCameraStates)
            {
                if (kvp.Key != null && !uiCameras.Contains(kvp.Key))
                {
                    unmanagedCams.Add(kvp.Key);
                }
            }

            foreach (var cam in unmanagedCams)
            {
                if (originalCameraStates.TryGetValue(cam, out var savedState))
                {
                    var hook = cam.GetComponent<RemixUICameraHook>();
                    if (hook != null)
                    {
                        UnityEngine.Object.Destroy(hook);
                    }

                    cam.targetTexture = savedState.targetTexture;
                    cam.clearFlags = savedState.clearFlags;
                    cam.backgroundColor = savedState.backgroundColor;
                    originalCameraStates.Remove(cam);
                    managedCameras.Remove(cam);
                    logger?.LogInfo($"[RemixUIOverlay] Restored unmanaged camera '{cam.name}' to original targetTexture ({savedState.targetTexture?.name ?? "null"}).");
                }
            }

            managedCameras.Clear();
            var sortedCams = uiCameras.Where(c => c != null).OrderBy(c => c.depth).ToList();
            bool isFirst = true;
            foreach (var cam in sortedCams)
            {
                managedCameras.Add(cam);

                if (!originalCameraStates.ContainsKey(cam))
                {
                    originalCameraStates[cam] = new SavedCameraState
                    {
                        targetTexture = cam.targetTexture,
                        clearFlags = cam.clearFlags,
                        backgroundColor = cam.backgroundColor
                    };
                }

                var targetClear = isFirst ? CameraClearFlags.SolidColor : CameraClearFlags.Depth;
                var targetBg = new Color(0, 0, 0, 0);

                // Attach or update hook to ensure targetTexture and clear flags persist even if game hooks onPreRender
                var hook = cam.GetComponent<RemixUICameraHook>();
                if (hook == null)
                {
                    hook = cam.gameObject.AddComponent<RemixUICameraHook>();
                }
                hook.logger = logger;
                hook.targetTexture = uiRenderTexture;
                hook.clearFlags = targetClear;
                hook.backgroundColor = targetBg;

                cam.targetTexture = uiRenderTexture;
                cam.clearFlags = targetClear;
                cam.backgroundColor = targetBg;
                isFirst = false;
            }

            logger?.LogInfo($"[RemixUIOverlay] Configured {uiCameras.Count} UI cameras to render to UI RenderTexture.");
        }

        public void OnRemixUIStateChanged(bool open)
        {
            if (RemixAPI.DrawScreenOverlayFunc == null) return;

            bool hideOnMenu = configHideUIOnRemixMenu != null && configHideUIOnRemixMenu.Value;

            if (open)
            {
                if (hideOnMenu && isOverlayVisible)
                {
                    RemixAPI.DrawScreenOverlayFunc(IntPtr.Zero, 0, 0, RemixAPI.remixapi_Format.REMIXAPI_FORMAT_R8G8B8A8_UNORM, 0.0f);
                    isOverlayVisible = false;
                    logger?.LogInfo("[RemixUIOverlay] Hidden UI overlay for Remix menu.");
                }
            }
            else
            {
                lastPresentedNonZero = -1;
                isOverlayVisible = true;
                logger?.LogInfo("[RemixUIOverlay] Restored UI overlay after Remix menu closed.");
            }
        }

        public void HideOverlayImmediate()
        {
            if (isOverlayVisible)
            {
                if (RemixAPI.DrawScreenOverlayFunc != null)
                {
                    RemixAPI.DrawScreenOverlayFunc(IntPtr.Zero, 0, 0, RemixAPI.remixapi_Format.REMIXAPI_FORMAT_R8G8B8A8_UNORM, 0.0f);
                }
                isOverlayVisible = false;
            }
        }

        public void RestoreOverlayImmediate()
        {
            if (!isOverlayVisible && !RemixWindowManager.IsRemixUIOpen)
            {
                lastPresentedNonZero = -1;
                isOverlayVisible = true;
            }
        }

        /// <summary>
        /// Updates the OpenRemix native screen overlay with the contents of the UI RenderTexture.
        /// Uses non-blocking AsyncGPUReadback to eliminate GPU stalls and Parallel.For for scanline conversion.
        /// </summary>
        public void UpdateOverlay()
        {
            if (RemixAPI.DrawScreenOverlayFunc == null) return;

            RemixWatchdog.BeatMain("UIOverlay.UpdateOverlay");
            isAppFocused = Application.isFocused;

            // If game is unfocused and Remix menu is not open, hide the overlay so it doesn't render over background
            if (!isAppFocused && !RemixWindowManager.IsRemixUIOpen)
            {
                if (isOverlayVisible)
                {
                    lock (overlayApiLock)
                    {
                        RemixAPI.DrawScreenOverlayFunc(IntPtr.Zero, 0, 0, RemixAPI.remixapi_Format.REMIXAPI_FORMAT_R8G8B8A8_UNORM, 0.0f);
                    }
                    isOverlayVisible = false;
                }
                return;
            }

            // If user enabled HideUIOnRemixMenu, hide the game UI overlay while Remix Alt+X menu is open
            bool hideOnMenu = configHideUIOnRemixMenu != null && configHideUIOnRemixMenu.Value;
            if (hideOnMenu && RemixWindowManager.IsRemixUIOpen)
            {
                if (isOverlayVisible)
                {
                    lock (overlayApiLock)
                    {
                        RemixAPI.DrawScreenOverlayFunc(IntPtr.Zero, 0, 0, RemixAPI.remixapi_Format.REMIXAPI_FORMAT_R8G8B8A8_UNORM, 0.0f);
                    }
                    isOverlayVisible = false;
                }
                return;
            }
            else if (!isOverlayVisible && Application.isFocused && (!hideOnMenu || !RemixWindowManager.IsRemixUIOpen))
            {
                lastPresentedNonZero = -1;
                isOverlayVisible = true;
            }

            // uiRenderTexture MUST match Unity engine Screen resolution (Screen.width x Screen.height)
            // so that Unity's Canvas scaling and Input.mousePosition match 1:1 without coordinate distortion.
            int targetW = Screen.width > 0 ? Screen.width : 1920;
            int targetH = Screen.height > 0 ? Screen.height : 1080;

            if (targetW > 0 && targetH > 0 && (currentWidth != targetW || currentHeight != targetH))
            {
                if (!isReadbackPending && !isProcessingOverlay)
                {
                    logger?.LogInfo($"[RemixUIOverlay] Screen resolution changed: {currentWidth}x{currentHeight} -> {targetW}x{targetH}. Resizing UI RenderTexture.");
                    EnsureRenderTexture(targetW, targetH);
                    Canvas.ForceUpdateCanvases();
                }
            }

            if (uiRenderTexture == null || !uiRenderTexture.IsCreated()) return;

            if (!isReadbackPending && !isProcessingOverlay)
            {
                isReadbackPending = true;
                AsyncGPUReadback.Request(uiRenderTexture, 0, TextureFormat.RGBA32, OnAsyncReadbackCompleted);
            }
        }

        private int readbackCompletionCount = 0;
        private int readbackErrorCount = 0;

        private void OnAsyncReadbackCompleted(AsyncGPUReadbackRequest request)
        {
            isReadbackPending = false;
            readbackCompletionCount++;

            if (request.hasError)
            {
                readbackErrorCount++;
                if (readbackErrorCount <= 5 || readbackErrorCount % 180 == 0)
                {
                    logger?.LogWarning($"[RemixUIOverlay] AsyncGPUReadback request error! (totalErrors={readbackErrorCount})");
                }
                return;
            }

            if (RemixAPI.DrawScreenOverlayFunc == null || uiRenderTexture == null || isProcessingOverlay)
                return;

            int width = request.width;
            int height = request.height;
            int currentFrame = Time.frameCount;

            if (readbackCompletionCount <= 5)
            {
                logger?.LogInfo($"[RemixUIOverlay] Readback success #{readbackCompletionCount}: res={width}x{height}, frame={currentFrame}");
            }

            var rawData = request.GetData<byte>();
            if (!rawData.IsCreated || rawData.Length < width * height * 4) return;

            if (processPixels == null || processPixels.Length != rawData.Length)
            {
                processPixels = new byte[rawData.Length];
            }
            rawData.CopyTo(processPixels);

            isProcessingOverlay = true;
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    ProcessAndPresentOverlay(width, height, currentFrame);
                }
                catch (Exception ex)
                {
                    logger?.LogError($"[RemixUIOverlay] Background native overlay update failed: {ex.Message}");
                }
                finally
                {
                    isProcessingOverlay = false;
                }
            });
        }

        private void ProcessAndPresentOverlay(int width, int height, int currentFrame)
        {
            using (RemixTracy.Zone("ProcessAndPresentOverlay"))
            {
                var totalSw = System.Diagnostics.Stopwatch.StartNew();

                if (RemixAPI.DrawScreenOverlayFunc == null || processPixels == null)
                    return;

                if (configHideUIOnRemixMenu != null && configHideUIOnRemixMenu.Value && RemixWindowManager.IsRemixUIOpen)
                    return;

                int byteCount = width * height * 4;
                if (nativeProcessPixels == null || nativeProcessPixels.Length != byteCount)
                {
                    nativeProcessPixels = new byte[byteCount];
                }

                int totalPixels = width * height;
                int nonZeroPixelCount = 0;
                int opaquePixelCount = 0;
                int blackOpaqueCount = 0;
                int colorOpaqueCount = 0;
                int colorSemiCount = 0;
                int colorZeroACount = 0;

                unsafe
                {
                    fixed (byte* pSrc = processPixels)
                    fixed (byte* pDst = nativeProcessPixels)
                    {
                        IntPtr srcPtr = (IntPtr)pSrc;
                        IntPtr dstPtr = (IntPtr)pDst;
                        bool clearBlack = configUIOverlayClearBlack != null && configUIOverlayClearBlack.Value;

                        // Parallel vertical flip + pixel conversion:
                        // Unity AsyncGPUReadback row 0 is at the bottom of the image.
                        // OpenRemix / Vulkan DrawScreenOverlay row 0 is at the top of the image.
                        // So destination row y comes from source row (height - 1 - y).
                        System.Threading.Tasks.Parallel.For(0, height, y =>
                        {
                            int srcRowOffset = (height - 1 - y) * width * 4;
                            int dstRowOffset = y * width * 4;
                            byte* s = (byte*)srcPtr + srcRowOffset;
                            byte* d = (byte*)dstPtr + dstRowOffset;

                            int localNonZero = 0;
                            int localOpaque = 0;
                            int localBlackOpaque = 0;
                            int localColorOpaque = 0;
                            int localColorSemi = 0;
                            int localColorZeroA = 0;

                            for (int x = 0; x < width; x++)
                            {
                                uint px = *(uint*)s;
                                // Fast path for transparent empty pixels (typically >90% of screen)
                                if (px == 0)
                                {
                                    *(uint*)d = 0;
                                    s += 4;
                                    d += 4;
                                    continue;
                                }

                                byte r = s[0];
                                byte g = s[1];
                                byte b = s[2];
                                byte a = s[3];

                                bool isColor = (r > 0 || g > 0 || b > 0);

                                if (!isColor)
                                {
                                    // If RGB is black (0,0,0) and alpha is 255:
                                    // When clearBlack is true (e.g. URP games where target clears alpha to 1.0), treat as transparent.
                                    // Otherwise (e.g. ULTRAKILL), preserve opaque black so UI boxes/panels remain solid.
                                    if (a == 0 || (clearBlack && a == 255))
                                    {
                                        *(uint*)d = 0;
                                        s += 4;
                                        d += 4;
                                        continue;
                                    }

                                    localNonZero++;
                                    if (a == 255)
                                    {
                                        localOpaque++;
                                        localBlackOpaque++;
                                    }
                                    d[0] = 0;
                                    d[1] = 0;
                                    d[2] = 0;
                                    d[3] = a;
                                    s += 4;
                                    d += 4;
                                    continue;
                                }

                                // Fallback for additive / unlit UI shaders that output color with a == 0
                                byte effA = a;
                                if (effA == 0)
                                {
                                    effA = (byte)Math.Max(r, Math.Max(g, b));
                                    localColorZeroA++;
                                }

                                localNonZero++;
                                if (effA == 255)
                                {
                                    localOpaque++;
                                    localColorOpaque++;
                                }
                                else
                                {
                                    localColorSemi++;
                                }

                                // OpenRemix uses straight alpha (VK_BLEND_FACTOR_SRC_ALPHA) and REMIXAPI_FORMAT_R8G8B8A8_UNORM
                                d[0] = r;
                                d[1] = g;
                                d[2] = b;
                                d[3] = effA;

                                s += 4;
                                d += 4;
                            }

                            if (localNonZero > 0) System.Threading.Interlocked.Add(ref nonZeroPixelCount, localNonZero);
                            if (localOpaque > 0) System.Threading.Interlocked.Add(ref opaquePixelCount, localOpaque);
                            if (localBlackOpaque > 0) System.Threading.Interlocked.Add(ref blackOpaqueCount, localBlackOpaque);
                            if (localColorOpaque > 0) System.Threading.Interlocked.Add(ref colorOpaqueCount, localColorOpaque);
                            if (localColorSemi > 0) System.Threading.Interlocked.Add(ref colorSemiCount, localColorSemi);
                            if (localColorZeroA > 0) System.Threading.Interlocked.Add(ref colorZeroACount, localColorZeroA);
                        });

                        if (readbackCompletionCount <= 5 || currentFrame % 180 == 0)
                        {
                            logger?.LogInfo($"[RemixUIOverlay] Overlay stats (frame {currentFrame}): total={totalPixels}, nonZero={nonZeroPixelCount}, opaque={opaquePixelCount}, blackOpaque={blackOpaqueCount}, colorOpaque={colorOpaqueCount}, colorSemi={colorSemiCount}, colorZeroA={colorZeroACount}, res={width}x{height}");
                        }

                        if (nonZeroPixelCount == 0)
                        {
                            if (lastPresentedNonZero == 0)
                            {
                                // Already cleared; skip redundant call
                                return;
                            }
                            lastPresentedNonZero = 0;
                            lock (overlayApiLock)
                            {
                                RemixAPI.DrawScreenOverlayFunc(IntPtr.Zero, 0, 0, RemixAPI.remixapi_Format.REMIXAPI_FORMAT_R8G8B8A8_UNORM, 0.0f);
                            }
                            return;
                        }

                        lastPresentedNonZero = nonZeroPixelCount;

                        double drawOverlayMs = 0;
                        using (RemixTracy.Zone("DrawScreenOverlay"))
                        {
                            var sw = System.Diagnostics.Stopwatch.StartNew();
                            RemixAPI.remixapi_ErrorCode status;
                            lock (overlayApiLock)
                            {
                                status = RemixAPI.DrawScreenOverlayFunc((IntPtr)pDst, (uint)width, (uint)height, RemixAPI.remixapi_Format.REMIXAPI_FORMAT_R8G8B8A8_UNORM, 1.0f);
                            }
                            drawOverlayMs = sw.Elapsed.TotalMilliseconds;
                            if (status != RemixAPI.remixapi_ErrorCode.REMIXAPI_ERROR_CODE_SUCCESS)
                            {
                                logger?.LogError($"[RemixUIOverlay] DrawScreenOverlay failed: {status}");
                            }
                        }

                        RemixProfiler.RecordOverlayThread(totalSw.Elapsed.TotalMilliseconds, drawOverlayMs);
                    }
                }
            }
        }

        private void EnsureRenderTexture(int width, int height)
        {
            if (uiRenderTexture != null && (currentWidth != width || currentHeight != height))
            {
                uiRenderTexture.Release();
                UnityEngine.Object.Destroy(uiRenderTexture);
                uiRenderTexture = null;
            }

            if (uiRenderTexture == null)
            {
                uiRenderTexture = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32)
                {
                    name = "UnityRemix_UITarget",
                    filterMode = FilterMode.Point
                };
                uiRenderTexture.Create();
                currentWidth = width;
                currentHeight = height;

                // Update hooks and target textures on all active managed cameras
                foreach (var cam in managedCameras)
                {
                    if (cam != null)
                    {
                        var hook = cam.GetComponent<RemixUICameraHook>();
                        if (hook != null)
                        {
                            hook.targetTexture = uiRenderTexture;
                        }
                        cam.targetTexture = uiRenderTexture;
                    }
                }
            }
        }

        public void RestoreUICameras()
        {
            foreach (var kvp in originalCameraStates)
            {
                var cam = kvp.Key;
                if (cam != null)
                {
                    var hook = cam.GetComponent<RemixUICameraHook>();
                    if (hook != null) UnityEngine.Object.Destroy(hook);

                    cam.targetTexture = kvp.Value.targetTexture;
                    cam.clearFlags = kvp.Value.clearFlags;
                    cam.backgroundColor = kvp.Value.backgroundColor;
                }
            }

            originalCameraStates.Clear();
            managedCameras.Clear();
            logger?.LogInfo("[RemixUIOverlay] Restored original UI camera settings.");
        }

        public void Destroy()
        {
            UnsubscribeSRPEvents();
            RestoreUICameras();

            if (RemixAPI.DrawScreenOverlayFunc != null)
            {
                try
                {
                    RemixAPI.DrawScreenOverlayFunc(IntPtr.Zero, 0, 0, RemixAPI.remixapi_Format.REMIXAPI_FORMAT_R8G8B8A8_UNORM, 0.0f);
                }
                catch { }
            }

            if (Instance == this)
            {
                Instance = null;
            }

            if (uiRenderTexture != null)
            {
                uiRenderTexture.Release();
                UnityEngine.Object.Destroy(uiRenderTexture);
                uiRenderTexture = null;
            }
        }
    }

    /// <summary>
    /// Attached to active UI Cameras to ensure their render target is locked to the UI RenderTexture
    /// with transparent solid clear, executing in MonoBehaviour.OnPreRender right before camera culling.
    /// </summary>
    public class RemixUICameraHook : MonoBehaviour
    {
        private static int lastClearedFrame = -1;
        private static Camera lastClearingCamera = null;

        public RenderTexture targetTexture;
        public CameraClearFlags clearFlags = CameraClearFlags.Depth;
        public Color backgroundColor = new Color(0, 0, 0, 0);
        public ManualLogSource logger;
        private Camera cam;

        void Awake()
        {
            cam = GetComponent<Camera>();
        }

        void OnPreCull()
        {
            EnsureConfigured("OnPreCull");
        }

        void OnPreRender()
        {
            EnsureConfigured("OnPreRender");
        }

        public void EnsureConfigured(string stage)
        {
            if (cam == null) cam = GetComponent<Camera>();
            if (cam != null && targetTexture != null)
            {
                cam.targetTexture = targetTexture;

                // Dynamically ensure the first UI camera rendering on any given frame clears color to transparent (0,0,0,0),
                // while any subsequent UI cameras rendering on the same frame clear only Depth to preserve previous UI elements.
                if (lastClearedFrame != Time.frameCount)
                {
                    lastClearedFrame = Time.frameCount;
                    lastClearingCamera = cam;
                    cam.clearFlags = CameraClearFlags.SolidColor;
                    cam.backgroundColor = new Color(0, 0, 0, 0);
                }
                else if (lastClearingCamera == cam)
                {
                    cam.clearFlags = CameraClearFlags.SolidColor;
                    cam.backgroundColor = new Color(0, 0, 0, 0);
                }
                else
                {
                    cam.clearFlags = CameraClearFlags.Depth;
                }

                int uiLayer = LayerMask.NameToLayer("UI");
                int uiLayerBit = uiLayer >= 0 ? (1 << uiLayer) : (1 << 5);
                int threeDMask = RemixUIDetector.CurrentThreeDLayerMask & ~uiLayerBit;

                if (cam.name == "UnityRemix_DedicatedUICamera")
                {
                    cam.cullingMask = uiLayerBit;
                }
                else
                {
                    cam.cullingMask &= ~threeDMask; // Ensure layers containing 3D meshes are never rendered by UI camera
                    cam.cullingMask &= ~1; // Ensure layer 0 (Default / 3D game scene) is never rendered by UI camera
                    cam.cullingMask |= uiLayerBit;
                }
            }
        }

        void OnPostRender()
        {
        }
    }
}
