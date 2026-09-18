using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.Rendering;

namespace UnityRemix
{
    /// <summary>
    /// Manages a transparent Win32 layered overlay window for SingleWindow Embedded mode.
    /// Captures the autodetected UI cameras to a RenderTexture and presents them with per-pixel alpha
    /// directly on top of the embedded Remix viewport using asynchronous GPU readbacks.
    /// </summary>
    public class RemixUIOverlay
    {
        private readonly ManualLogSource logger;
        private readonly IntPtr gameWindow;
        private IntPtr overlayWindow = IntPtr.Zero;
        public IntPtr OverlayWindow => overlayWindow;

        // UI rendering state
        private readonly BepInEx.Configuration.ConfigEntry<int> configUIOverlayFPS;
        private readonly BepInEx.Configuration.ConfigEntry<bool> configHideUIOnRemixMenu;
        private readonly BepInEx.Configuration.ConfigEntry<bool> configUIOverlayClearBlack;
        private RenderTexture uiRenderTexture;
        private bool isReadbackPending = false;
        private bool isProcessingOverlay = false;
        private bool isOverlayVisible = false;
        private int lastPresentedNonZero = -1;
        private int currentWidth = 0;
        private int currentHeight = 0;
        private float lastReadbackRequestTime = 0f;

        // Overlay window geometry tracking
        private int lastOverlayX = -1;
        private int lastOverlayY = -1;
        private int lastOverlayW = -1;
        private int lastOverlayH = -1;

        // Camera capture state
        private readonly Dictionary<Camera, SavedCameraState> originalCameraStates = new Dictionary<Camera, SavedCameraState>();

        private struct SavedCameraState
        {
            public RenderTexture targetTexture;
            public CameraClearFlags clearFlags;
            public Color backgroundColor;
        }

        // Win32 DIB presentation state
        private IntPtr overlayHdc = IntPtr.Zero;
        private IntPtr overlayDib = IntPtr.Zero;
        private IntPtr overlayOldBmp = IntPtr.Zero;
        private IntPtr overlayBits = IntPtr.Zero;
        private int dibWidth = 0;
        private int dibHeight = 0;
        private readonly object dibLock = new object();
        private byte[] processPixels;

        #region Win32 Constants and P/Invoke

        private const int WS_EX_LAYERED = 0x00080000;
        private const int WS_EX_TRANSPARENT = 0x00000020;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const int WS_POPUP = unchecked((int)0x80000000);
        private const int WS_VISIBLE = 0x10000000;
        private const int WS_DISABLED = 0x08000000;

        private const uint ULW_ALPHA = 0x00000002;
        private const byte AC_SRC_OVER = 0x00;
        private const byte AC_SRC_ALPHA = 0x01;

        private static readonly IntPtr HWND_TOP = IntPtr.Zero;
        private static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_SHOWWINDOW = 0x0040;

        private const int SW_HIDE = 0;
        private const int SW_SHOWNOACTIVATE = 4;

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int x;
            public int y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SIZE
        {
            public int cx;
            public int cy;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct BLENDFUNCTION
        {
            public byte BlendOp;
            public byte BlendFlags;
            public byte SourceConstantAlpha;
            public byte AlphaFormat;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
            public int Width => Right - Left;
            public int Height => Bottom - Top;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFOHEADER
        {
            public uint biSize;
            public int biWidth;
            public int biHeight;
            public ushort biPlanes;
            public ushort biBitCount;
            public uint biCompression;
            public uint biSizeImage;
            public int biXPelsPerMeter;
            public int biYPelsPerMeter;
            public uint biClrUsed;
            public uint biClrImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFO
        {
            public BITMAPINFOHEADER bmiHeader;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr CreateWindowExW(
            int dwExStyle,
            [MarshalAs(UnmanagedType.LPWStr)] string lpClassName,
            [MarshalAs(UnmanagedType.LPWStr)] string lpWindowName,
            int dwStyle,
            int x, int y, int nWidth, int nHeight,
            IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll")]
        private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UpdateLayeredWindow(
            IntPtr hwnd,
            IntPtr hdcDst,
            ref POINT pptDst,
            ref SIZE psize,
            IntPtr hdcSrc,
            ref POINT pptSrc,
            uint crKey,
            ref BLENDFUNCTION pblend,
            uint dwFlags);


        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteDC(IntPtr hdc);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern IntPtr CreateDIBSection(
            IntPtr hdc,
            ref BITMAPINFO pbmi,
            uint iUsage,
            out IntPtr ppvBits,
            IntPtr hSection,
            uint dwOffset);

        [DllImport("gdi32.dll")]
        private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);

        private const uint WM_SETCURSOR = 0x0020;

        [DllImport("user32.dll")]
        private static extern IntPtr SetCursor(IntPtr hCursor);

        [DllImport("user32.dll")]
        private static extern IntPtr LoadCursorW(IntPtr hInstance, int lpCursorName);

        [DllImport("user32.dll")]
        private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern ushort RegisterClassW(ref WNDCLASS lpWndClass);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetModuleHandleW([MarshalAs(UnmanagedType.LPWStr)] string lpModuleName);

        [StructLayout(LayoutKind.Sequential)]
        private struct WNDCLASS
        {
            public uint style;
            public IntPtr lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string lpszMenuName;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string lpszClassName;
        }

        private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        private static WndProcDelegate overlayWndProcDelegate;
        private static bool overlayClassRegistered = false;
        private const string OVERLAY_CLASS_NAME = "UnityRemix_UIOverlay_Class";


        private static IntPtr OverlayWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (msg == WM_SETCURSOR)
            {
                if (RemixWindowManager.IsRemixUIOpen)
                {
                    SetCursor(LoadCursorW(IntPtr.Zero, 32512 /* IDC_ARROW */));
                    return new IntPtr(1);
                }
                else if (RemixWindowManager.ShouldHideCursor)
                {
                    SetCursor(RemixWindowManager.BlankCursor);
                    return new IntPtr(1);
                }
            }
            return DefWindowProcW(hWnd, msg, wParam, lParam);
        }

        #endregion

        public static RemixUIOverlay Instance { get; private set; }
        private readonly HashSet<Camera> managedCameras = new HashSet<Camera>();

        public static bool IsManagedUICamera(Camera cam)
        {
            if (cam == null || Instance == null) return false;
            return Instance.managedCameras.Contains(cam);
        }

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
            IntPtr gameWindow,
            BepInEx.Configuration.ConfigEntry<int> configUIOverlayFPS = null,
            BepInEx.Configuration.ConfigEntry<bool> configHideUIOnRemixMenu = null,
            BepInEx.Configuration.ConfigEntry<bool> configUIOverlayClearBlack = null)
        {
            this.logger = logger;
            this.gameWindow = gameWindow;
            this.configUIOverlayFPS = configUIOverlayFPS;
            this.configHideUIOnRemixMenu = configHideUIOnRemixMenu;
            this.configUIOverlayClearBlack = configUIOverlayClearBlack;
            Instance = this;
        }

        public bool Initialize()
        {
            if (gameWindow == IntPtr.Zero)
            {
                logger?.LogWarning("[RemixUIOverlay] Cannot initialize: gameWindow is Zero");
                return false;
            }

            if (!overlayClassRegistered)
            {
                overlayWndProcDelegate = OverlayWndProc;
                IntPtr hInstance = GetModuleHandleW(null);
                var wc = new WNDCLASS
                {
                    style = 0,
                    lpfnWndProc = Marshal.GetFunctionPointerForDelegate(overlayWndProcDelegate),
                    cbClsExtra = 0,
                    cbWndExtra = 0,
                    hInstance = hInstance,
                    hIcon = IntPtr.Zero,
                    hCursor = IntPtr.Zero,
                    hbrBackground = IntPtr.Zero,
                    lpszMenuName = null,
                    lpszClassName = OVERLAY_CLASS_NAME
                };
                RegisterClassW(ref wc);
                overlayClassRegistered = true;
            }

            GetClientRect(gameWindow, out RECT clientRect);
            int width = Math.Max(clientRect.Width, 100);
            int height = Math.Max(clientRect.Height, 100);

            var pt = new POINT { x = 0, y = 0 };
            ClientToScreen(gameWindow, ref pt);

            // Create transparent, click-through layered popup owned by gameWindow
            overlayWindow = CreateWindowExW(
                WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
                OVERLAY_CLASS_NAME,
                "UnityRemix_UIOverlay",
                WS_POPUP | WS_VISIBLE | WS_DISABLED,
                pt.x, pt.y, width, height,
                gameWindow,
                IntPtr.Zero,
                IntPtr.Zero,
                IntPtr.Zero
            );

            if (overlayWindow == IntPtr.Zero)
            {
                logger?.LogError($"[RemixUIOverlay] Failed to create overlay window! Win32 Error: {Marshal.GetLastWin32Error()}");
                return false;
            }

            logger?.LogInfo($"[RemixUIOverlay] Created transparent UI overlay window: 0x{overlayWindow:X} ({width}x{height})");
            SyncWindowBounds();
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

        private void OnBeginCameraRendering(UnityEngine.Rendering.ScriptableRenderContext context, Camera cam)
        {
            if (cam != null && managedCameras.Contains(cam))
            {
                if (Time.frameCount % 180 == 1 || Time.frameCount <= 30)
                {
                    string rtInfo = cam.targetTexture != null
                        ? $"{cam.targetTexture.name} ({cam.targetTexture.width}x{cam.targetTexture.height}, fmt={cam.targetTexture.format}, gfxFmt={cam.targetTexture.graphicsFormat})"
                        : "null";
                    string addDataInfo = "";
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
                                var reqDepth = addDataCamType.GetProperty("requiresDepthTexture")?.GetValue(comp, null);
                                var reqColor = addDataCamType.GetProperty("requiresColorTexture")?.GetValue(comp, null);
                                addDataInfo = $", URP(renderType={rType}, post={post}, aa={aa}, reqDepth={reqDepth}, reqColor={reqColor})";
                            }
                        }
                    }
                    catch { }

                    logger?.LogInfo($"[RemixUIOverlay] SRP beginCameraRendering: cam='{cam.name}', depth={cam.depth}, clear={cam.clearFlags}, bg=RGBA({cam.backgroundColor.r:F2},{cam.backgroundColor.g:F2},{cam.backgroundColor.b:F2},{cam.backgroundColor.a:F2}), mask=0x{cam.cullingMask:X}, targetTex={rtInfo}{addDataInfo}");
                }

                var hook = cam.GetComponent<RemixUICameraHook>();
                if (hook != null)
                {
                    hook.EnsureConfigured("SRP_beginCameraRendering");
                }
                else if (uiRenderTexture != null)
                {
                    cam.targetTexture = uiRenderTexture;
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

        /// <summary>
        /// Updates the transparent Win32 layered overlay with the contents of the UI RenderTexture.
        /// Uses non-blocking AsyncGPUReadback to eliminate GPU stalls and Parallel.For for scanline conversion.
        /// </summary>
        public void UpdateOverlay()
        {
            if (overlayWindow == IntPtr.Zero) return;

            // If user enabled HideUIOnRemixMenu, hide the game UI overlay while Remix Alt+X menu is open.
            if (configHideUIOnRemixMenu != null && configHideUIOnRemixMenu.Value && RemixWindowManager.IsRemixUIOpen)
            {
                if (isOverlayVisible)
                {
                    ShowWindow(overlayWindow, SW_HIDE);
                    isOverlayVisible = false;
                }
                return;
            }

            SyncWindowBounds();

            // uiRenderTexture MUST match Unity engine Screen resolution (Screen.width x Screen.height)
            // so that Unity's Canvas scaling and Input.mousePosition match 1:1 without coordinate distortion.
            int targetW = Screen.width > 0 ? Screen.width : 1920;
            int targetH = Screen.height > 0 ? Screen.height : 1080;

            if (targetW > 0 && targetH > 0 && (currentWidth != targetW || currentHeight != targetH))
            {
                if (!isReadbackPending && !isProcessingOverlay)
                {
                    logger?.LogInfo($"[RemixUIOverlay] Screen/Window resolution changed: {currentWidth}x{currentHeight} -> {targetW}x{targetH}. Resizing UI RenderTexture.");
                    EnsureRenderTexture(targetW, targetH);
                    Canvas.ForceUpdateCanvases();
                }
            }

            if (uiRenderTexture == null || !uiRenderTexture.IsCreated()) return;

            if (!isReadbackPending && !isProcessingOverlay)
            {
                float now = Time.unscaledTime;
                int targetFps = configUIOverlayFPS != null ? Mathf.Clamp(configUIOverlayFPS.Value, 10, 300) : 60;
                float minInterval = 1.0f / targetFps;
                if (now - lastReadbackRequestTime < minInterval) return;
                lastReadbackRequestTime = now;

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

            if (overlayWindow == IntPtr.Zero || uiRenderTexture == null || isProcessingOverlay)
                return;

            int width = request.width;
            int height = request.height;
            int currentFrame = Time.frameCount;

            if (readbackCompletionCount <= 5)
            {
                logger?.LogInfo($"[RemixUIOverlay] Readback success #{readbackCompletionCount}: res={width}x{height}, frame={currentFrame}");
            }
            // Layered window and DIB must match physical gameWindow client area so overlay covers entire window
            int destWidth = width;
            int destHeight = height;
            if (GetClientRect(gameWindow, out RECT clientRect) && clientRect.Width > 0 && clientRect.Height > 0)
            {
                destWidth = clientRect.Width;
                destHeight = clientRect.Height;
            }

            EnsureDIB(destWidth, destHeight);
            if (overlayHdc == IntPtr.Zero || overlayBits == IntPtr.Zero) return;

            var rawData = request.GetData<byte>();
            if (!rawData.IsCreated || rawData.Length < width * height * 4) return;

            if (processPixels == null || processPixels.Length != rawData.Length)
            {
                processPixels = new byte[rawData.Length];
            }
            rawData.CopyTo(processPixels);

            // Offload scanline processing and UpdateLayeredWindow to background thread pool!
            // Unity's main thread returns immediately (~0.2ms), completely eliminating slow-motion stutters!
            isProcessingOverlay = true;
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    ProcessAndPresentOverlay(width, height, destWidth, destHeight, currentFrame);
                }
                catch (Exception ex)
                {
                    logger?.LogError($"[RemixUIOverlay] Background overlay update failed: {ex.Message}");
                }
                finally
                {
                    isProcessingOverlay = false;
                }
            });
        }

        private void ProcessAndPresentOverlay(int srcWidth, int srcHeight, int destWidth, int destHeight, int currentFrame)
        {
            using (RemixTracy.Zone("ProcessAndPresentOverlay"))
            {
                var totalSw = System.Diagnostics.Stopwatch.StartNew();
                double updateLayeredMs = 0;

                lock (dibLock)
                {
                    if (overlayWindow == IntPtr.Zero || overlayBits == IntPtr.Zero || processPixels == null)
                        return;

                int totalPixels = destWidth * destHeight;
                int nonZeroPixelCount = 0;
                int opaquePixelCount = 0;
                int blackOpaqueCount = 0;
                int colorOpaqueCount = 0;
                int colorSemiCount = 0;
                int colorZeroACount = 0;

                unsafe
                {
                    fixed (byte* pSrc = processPixels)
                    {
                        IntPtr srcPtr = (IntPtr)pSrc;
                        IntPtr dstPtr = overlayBits;

                        using (RemixTracy.Zone("UI_ScanlineConversion"))
                        {
                            bool clearBlack = configUIOverlayClearBlack != null && configUIOverlayClearBlack.Value;

                            if (srcWidth == destWidth && srcHeight == destHeight)
                            {
                                // Direct 1:1 fast path (no scaling required)
                                System.Threading.Tasks.Parallel.For(0, destHeight, y =>
                                {
                                    int rowOffset = y * destWidth * 4;
                                    byte* s = (byte*)srcPtr + rowOffset;
                                    byte* d = (byte*)dstPtr + rowOffset;
                                    int localNonZero = 0;
                                    int localOpaque = 0;
                                    int localBlackOpaque = 0;
                                    int localColorOpaque = 0;
                                    int localColorSemi = 0;
                                    int localColorZeroA = 0;

                                    for (int x = 0; x < destWidth; x++)
                                    {
                                        uint px = *(uint*)s;
                                        // Fast path for transparent empty pixels (96% of the screen in HUD)
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
                                            // If RGB is completely black (0,0,0) and alpha is 255 (opaque):
                                            // When UIOverlayClearBlack is enabled (e.g. in URP games like PEAK where
                                            // render targets clear alpha to 1.0), treat opaque black as transparent.
                                            // Otherwise (e.g. ULTRAKILL), preserve opaque black so UI boxes/panels remain solid.
                                            if (a == 0 || (clearBlack && a == 255))
                                            {
                                                *(uint*)d = 0;
                                                s += 4;
                                                d += 4;
                                                continue;
                                            }

                                            if (a == 255)
                                            {
                                                localNonZero++;
                                                localOpaque++;
                                                localBlackOpaque++;
                                                d[0] = 0;
                                                d[1] = 0;
                                                d[2] = 0;
                                                d[3] = 255;
                                                s += 4;
                                                d += 4;
                                                continue;
                                            }

                                            // Semi-transparent black (e.g. drop shadow)
                                            localNonZero++;
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
                                            d[0] = b;
                                            d[1] = g;
                                            d[2] = r;
                                            d[3] = 255;
                                        }
                                        else
                                        {
                                            localColorSemi++;
                                            // Windows AC_SRC_ALPHA requires premultiplied alpha: R <= A, G <= A, B <= A.
                                            d[0] = b <= effA ? b : effA;
                                            d[1] = g <= effA ? g : effA;
                                            d[2] = r <= effA ? r : effA;
                                            d[3] = effA;
                                        }

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
                            }
                            else
                            {
                                // Resolution scaling path (e.g. in-game 640x480 or 1080p scaled to physical window size)
                                System.Threading.Tasks.Parallel.For(0, destHeight, y =>
                                {
                                    int srcY = (int)((long)y * srcHeight / destHeight);
                                    if (srcY >= srcHeight) srcY = srcHeight - 1;

                                    byte* srcRow = (byte*)srcPtr + (srcY * srcWidth * 4);
                                    byte* dstRow = (byte*)dstPtr + (y * destWidth * 4);
                                    int localNonZero = 0;
                                    int localOpaque = 0;
                                    int localBlackOpaque = 0;
                                    int localColorOpaque = 0;
                                    int localColorSemi = 0;
                                    int localColorZeroA = 0;

                                    for (int x = 0; x < destWidth; x++)
                                    {
                                        int srcX = (int)((long)x * srcWidth / destWidth);
                                        if (srcX >= srcWidth) srcX = srcWidth - 1;

                                        byte* s = srcRow + (srcX * 4);
                                        byte* d = dstRow + (x * 4);

                                        uint px = *(uint*)s;
                                        if (px == 0)
                                        {
                                            *(uint*)d = 0;
                                            continue;
                                        }

                                        byte r = s[0];
                                        byte g = s[1];
                                        byte b = s[2];
                                        byte a = s[3];

                                        bool isColor = (r > 0 || g > 0 || b > 0);

                                        if (!isColor)
                                        {
                                            if (a == 0 || (clearBlack && a == 255))
                                            {
                                                *(uint*)d = 0;
                                                continue;
                                            }

                                            if (a == 255)
                                            {
                                                localNonZero++;
                                                localOpaque++;
                                                localBlackOpaque++;
                                                d[0] = 0;
                                                d[1] = 0;
                                                d[2] = 0;
                                                d[3] = 255;
                                                continue;
                                            }

                                            localNonZero++;
                                            d[0] = 0;
                                            d[1] = 0;
                                            d[2] = 0;
                                            d[3] = a;
                                            continue;
                                        }

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
                                            d[0] = b;
                                            d[1] = g;
                                            d[2] = r;
                                            d[3] = 255;
                                        }
                                        else
                                        {
                                            localColorSemi++;
                                            d[0] = b <= effA ? b : effA;
                                            d[1] = g <= effA ? g : effA;
                                            d[2] = r <= effA ? r : effA;
                                            d[3] = effA;
                                        }
                                    }

                                    if (localNonZero > 0) System.Threading.Interlocked.Add(ref nonZeroPixelCount, localNonZero);
                                    if (localOpaque > 0) System.Threading.Interlocked.Add(ref opaquePixelCount, localOpaque);
                                    if (localBlackOpaque > 0) System.Threading.Interlocked.Add(ref blackOpaqueCount, localBlackOpaque);
                                    if (localColorOpaque > 0) System.Threading.Interlocked.Add(ref colorOpaqueCount, localColorOpaque);
                                    if (localColorSemi > 0) System.Threading.Interlocked.Add(ref colorSemiCount, localColorSemi);
                                    if (localColorZeroA > 0) System.Threading.Interlocked.Add(ref colorZeroACount, localColorZeroA);
                                });
                            }
                        }
                    }
                }

                if (readbackCompletionCount <= 5 || currentFrame % 180 == 0)
                {
                    string sampleInfo = "";
                    unsafe
                    {
                        fixed (byte* pSrc = processPixels)
                        {
                            int[] sampleCoords = new int[] {
                                0, 0,
                                10, 10,
                                destWidth / 2, destHeight / 2,
                                destWidth / 4, destHeight / 4,
                                destWidth * 3 / 4, destHeight * 3 / 4
                            };
                            var sb = new System.Text.StringBuilder();
                            for (int i = 0; i < sampleCoords.Length; i += 2)
                            {
                                int sx = Math.Min(sampleCoords[i], srcWidth - 1);
                                int sy = Math.Min(sampleCoords[i + 1], srcHeight - 1);
                                int offset = (sy * srcWidth + sx) * 4;
                                byte sr = pSrc[offset];
                                byte sg = pSrc[offset + 1];
                                byte sb_val = pSrc[offset + 2];
                                byte sa = pSrc[offset + 3];
                                sb.Append($"({sx},{sy}):[R={sr},G={sg},B={sb_val},A={sa}] ");
                            }
                            sampleInfo = sb.ToString();
                        }
                    }

                    logger?.LogInfo($"[RemixUIOverlay] Overlay stats (frame {currentFrame}): total={totalPixels}, nonZero={nonZeroPixelCount}, opaque={opaquePixelCount}, blackOpaque={blackOpaqueCount}, colorOpaque={colorOpaqueCount}, colorSemi={colorSemiCount}, colorZeroA={colorZeroACount}, res={destWidth}x{destHeight}. Samples: {sampleInfo}");
                }

                // If completely empty (no UI pixels rendered at all), present transparent once and skip redundant updates
                if (nonZeroPixelCount == 0)
                {
                    if (lastPresentedNonZero == 0)
                    {
                        // Already presented fully transparent buffer; skip redundant DWM update
                        return;
                    }
                }
                lastPresentedNonZero = nonZeroPixelCount;

                // Update Win32 Layered Window
                var ptDst = new POINT { x = 0, y = 0 };
                ClientToScreen(gameWindow, ref ptDst);
                var sizeDst = new SIZE { cx = destWidth, cy = destHeight };
                var ptSrc = new POINT { x = 0, y = 0 };

                var blend = new BLENDFUNCTION
                {
                    BlendOp = AC_SRC_OVER,
                    BlendFlags = 0,
                    SourceConstantAlpha = 255,
                    AlphaFormat = AC_SRC_ALPHA
                };

                bool ok;
                using (RemixTracy.Zone("UpdateLayeredWindow"))
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    ok = UpdateLayeredWindow(
                        overlayWindow,
                        IntPtr.Zero,
                        ref ptDst,
                        ref sizeDst,
                        overlayHdc,
                        ref ptSrc,
                        0,
                        ref blend,
                        ULW_ALPHA
                    );
                    updateLayeredMs = sw.Elapsed.TotalMilliseconds;
                }

                if (!ok)
                {
                    int err = Marshal.GetLastWin32Error();
                    logger?.LogError($"[RemixUIOverlay] UpdateLayeredWindow failed! Win32 Error: {err}");
                }

                if (!isOverlayVisible)
                {
                    ShowWindow(overlayWindow, SW_SHOWNOACTIVATE);
                    isOverlayVisible = true;
                }
                }

                RemixProfiler.RecordOverlayThread(totalSw.Elapsed.TotalMilliseconds, updateLayeredMs);
            }
        }

        public void SyncWindowBounds()
        {
            using (RemixTracy.Zone("SyncWindowBounds"))
            {
                if (overlayWindow == IntPtr.Zero || gameWindow == IntPtr.Zero) return;

                if (IsIconic(gameWindow) || !IsWindowVisible(gameWindow))
                {
                    if (isOverlayVisible)
                    {
                        lock (dibLock)
                        {
                            ShowWindow(overlayWindow, SW_HIDE);
                            isOverlayVisible = false;
                        }
                    }
                    return;
                }

                if (GetClientRect(gameWindow, out RECT clientRect) && clientRect.Width > 0 && clientRect.Height > 0)
                {
                    var pt = new POINT { x = 0, y = 0 };
                    ClientToScreen(gameWindow, ref pt);

                    if (pt.x != lastOverlayX || pt.y != lastOverlayY || clientRect.Width != lastOverlayW || clientRect.Height != lastOverlayH)
                    {
                        lastOverlayX = pt.x;
                        lastOverlayY = pt.y;
                        lastOverlayW = clientRect.Width;
                        lastOverlayH = clientRect.Height;

                        lock (dibLock)
                        {
                            SetWindowPos(
                                overlayWindow,
                                HWND_TOP,
                                pt.x, pt.y, clientRect.Width, clientRect.Height,
                                SWP_NOACTIVATE | SWP_SHOWWINDOW
                            );
                        }
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

        private void EnsureDIB(int width, int height)
        {
            lock (dibLock)
            {
                if (overlayHdc == IntPtr.Zero || dibWidth != width || dibHeight != height)
                {
                    CleanupDIB();

                    IntPtr screenDC = GetDC(IntPtr.Zero);
                    overlayHdc = CreateCompatibleDC(screenDC);

                    var bmi = new BITMAPINFO();
                    bmi.bmiHeader.biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>();
                    bmi.bmiHeader.biWidth = width;
                    bmi.bmiHeader.biHeight = height; // Bottom-up DIB (matches Unity Texture2D.ReadPixels row 0 at bottom)
                    bmi.bmiHeader.biPlanes = 1;
                    bmi.bmiHeader.biBitCount = 32;
                    bmi.bmiHeader.biCompression = 0; // BI_RGB

                    overlayDib = CreateDIBSection(overlayHdc, ref bmi, 0, out overlayBits, IntPtr.Zero, 0);
                    overlayOldBmp = SelectObject(overlayHdc, overlayDib);
                    ReleaseDC(IntPtr.Zero, screenDC);

                    dibWidth = width;
                    dibHeight = height;
                }
            }
        }

        private void CleanupDIB()
        {
            lock (dibLock)
            {
                if (overlayHdc != IntPtr.Zero)
                {
                    if (overlayOldBmp != IntPtr.Zero)
                    {
                        SelectObject(overlayHdc, overlayOldBmp);
                        overlayOldBmp = IntPtr.Zero;
                    }
                    DeleteDC(overlayHdc);
                    overlayHdc = IntPtr.Zero;
                }
                if (overlayDib != IntPtr.Zero)
                {
                    DeleteObject(overlayDib);
                    overlayDib = IntPtr.Zero;
                }
                overlayBits = IntPtr.Zero;
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
            CleanupDIB();

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

            if (overlayWindow != IntPtr.Zero)
            {
                DestroyWindow(overlayWindow);
                overlayWindow = IntPtr.Zero;
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
