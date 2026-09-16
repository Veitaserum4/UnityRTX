using System;
using System.Collections.Generic;
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

        // UI rendering state
        private readonly BepInEx.Configuration.ConfigEntry<int> configUIOverlayFPS;
        private RenderTexture uiRenderTexture;
        private bool isReadbackPending = false;
        private int currentWidth = 0;
        private int currentHeight = 0;
        private float lastReadbackRequestTime = 0f;
        private volatile bool isProcessingOverlay = false;
        private byte[] processPixels = null;
        private int lastOverlayX = -9999;
        private int lastOverlayY = -9999;
        private int lastOverlayW = -9999;
        private int lastOverlayH = -9999;

        // Win32 DIB state for UpdateLayeredWindow
        private readonly object dibLock = new object();
        private IntPtr overlayHdc = IntPtr.Zero;
        private IntPtr overlayDib = IntPtr.Zero;
        private IntPtr overlayOldBmp = IntPtr.Zero;
        private IntPtr overlayBits = IntPtr.Zero;
        private int dibWidth = 0;
        private int dibHeight = 0;

        // Original camera settings to restore on disable/unload
        private struct SavedCameraState
        {
            public RenderTexture targetTexture;
            public CameraClearFlags clearFlags;
            public Color backgroundColor;
        }
        private readonly Dictionary<Camera, SavedCameraState> originalCameraStates = new Dictionary<Camera, SavedCameraState>();

        #region Win32 Constants and P/Invoke

        private const uint WS_POPUP = 0x80000000;
        private const uint WS_VISIBLE = 0x10000000;
        private const uint WS_DISABLED = 0x08000000;
        private const uint WS_EX_LAYERED = 0x00080000;
        private const uint WS_EX_TRANSPARENT = 0x00000020;
        private const uint WS_EX_TOOLWINDOW = 0x00000080;
        private const uint WS_EX_NOACTIVATE = 0x08000000;

        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_SHOWWINDOW = 0x0040;

        private const int SW_HIDE = 0;
        private const int SW_SHOWNOACTIVATE = 4;

        private const byte AC_SRC_OVER = 0x00;
        private const byte AC_SRC_ALPHA = 0x01;
        private const uint ULW_ALPHA = 0x00000002;

        private bool isOverlayVisible = true;
        private int updateLogCounter = 0;
        private static readonly IntPtr HWND_TOP = IntPtr.Zero;

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool BringWindowToTop(IntPtr hWnd);

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
            public uint bmiColors;
        }

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateWindowExW(
            uint dwExStyle,
            string lpClassName,
            string lpWindowName,
            uint dwStyle,
            int x, int y, int nWidth, int nHeight,
            IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
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

        [DllImport("gdi32.dll")]
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

        #endregion

        public static RemixUIOverlay Instance { get; private set; }
        private readonly HashSet<Camera> managedCameras = new HashSet<Camera>();

        public static bool IsManagedUICamera(Camera cam)
        {
            if (cam == null || Instance == null) return false;
            return Instance.managedCameras.Contains(cam);
        }

        public void RebindAllUICameras()
        {
            if (uiRenderTexture == null) return;
            int alwaysOnTopLayer = LayerMask.NameToLayer("AlwaysOnTop");
            int alwaysOnTopMask = alwaysOnTopLayer >= 0 ? (1 << alwaysOnTopLayer) : 0;
            int uiLayer = LayerMask.NameToLayer("UI");
            int uiLayerBit = uiLayer >= 0 ? (1 << uiLayer) : (1 << 5);

            foreach (var cam in managedCameras)
            {
                if (cam == null) continue;
                cam.targetTexture = uiRenderTexture;
                cam.SetTargetBuffers(uiRenderTexture.colorBuffer, uiRenderTexture.depthBuffer);
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0, 0, 0, 0);
                cam.cullingMask &= ~1; // Strip Default (0)
                if (alwaysOnTopMask != 0) cam.cullingMask &= ~alwaysOnTopMask; // Strip AlwaysOnTop if present
                cam.cullingMask |= uiLayerBit;
            }
        }

        public RemixUIOverlay(ManualLogSource logger, IntPtr gameWindow, BepInEx.Configuration.ConfigEntry<int> configUIOverlayFPS = null)
        {
            this.logger = logger;
            this.gameWindow = gameWindow;
            this.configUIOverlayFPS = configUIOverlayFPS;
            Instance = this;
        }

        public bool Initialize()
        {
            if (gameWindow == IntPtr.Zero)
            {
                logger?.LogWarning("[RemixUIOverlay] Cannot initialize: gameWindow is Zero");
                return false;
            }

            GetClientRect(gameWindow, out RECT clientRect);
            int width = Math.Max(clientRect.Width, 100);
            int height = Math.Max(clientRect.Height, 100);

            var pt = new POINT { x = 0, y = 0 };
            ClientToScreen(gameWindow, ref pt);

            // Create transparent, click-through layered popup owned by gameWindow
            overlayWindow = CreateWindowExW(
                WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
                "STATIC",
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
            return true;
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
            bool isFirst = true;
            foreach (var cam in uiCameras)
            {
                if (cam == null) continue;
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
                if (isFirst)
                {
                    cam.backgroundColor = targetBg;
                    isFirst = false;
                }
            }

            logger?.LogInfo($"[RemixUIOverlay] Configured {uiCameras.Count} UI cameras to render to UI RenderTexture.");
        }

        /// <summary>
        /// Updates the transparent Win32 layered overlay with the contents of the UI RenderTexture.
        /// Uses non-blocking AsyncGPUReadback to eliminate GPU stalls and Parallel.For for scanline conversion.
        /// </summary>
        public void UpdateOverlay()
        {
            if (overlayWindow == IntPtr.Zero || uiRenderTexture == null || !uiRenderTexture.IsCreated()) return;

            SyncWindowBounds();

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

        private void OnAsyncReadbackCompleted(AsyncGPUReadbackRequest request)
        {
            isReadbackPending = false;

            if (request.hasError || overlayWindow == IntPtr.Zero || uiRenderTexture == null || isProcessingOverlay)
                return;

            int width = request.width;
            int height = request.height;
            EnsureDIB(width, height);
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
                    ProcessAndPresentOverlay(width, height);
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

        private void ProcessAndPresentOverlay(int width, int height)
        {
            using (RemixTracy.Zone("ProcessAndPresentOverlay"))
            {
                var totalSw = System.Diagnostics.Stopwatch.StartNew();
                double updateLayeredMs = 0;

                lock (dibLock)
                {
                    if (overlayWindow == IntPtr.Zero || overlayBits == IntPtr.Zero || processPixels == null)
                        return;

                bool diagLog = (updateLogCounter++ < 20) || (updateLogCounter % 120 == 0);
                int totalPixels = width * height;
                int nonZeroPixelCount = 0;
                int opaquePixelCount = 0;

                unsafe
                {
                    fixed (byte* pSrc = processPixels)
                    {
                        IntPtr srcPtr = (IntPtr)pSrc;
                        IntPtr dstPtr = overlayBits;

                        using (RemixTracy.Zone("UI_ScanlineConversion"))
                        {
                            System.Threading.Tasks.Parallel.For(0, height, y =>
                            {
                                int rowOffset = y * width * 4;
                                byte* s = (byte*)srcPtr + rowOffset;
                                byte* d = (byte*)dstPtr + rowOffset;
                                int localNonZero = 0;
                                int localOpaque = 0;

                                for (int x = 0; x < width; x++)
                                {
                                    uint px = *(uint*)s;
                                    // Fast path for transparent empty pixels (96% of the screen in ULTRAKILL HUD)
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

                                    // Fallback for additive / unlit UI shaders that output color with a == 0
                                    byte effA = a;
                                    if (effA == 0 && (r > 0 || g > 0 || b > 0))
                                    {
                                        effA = (byte)Math.Max(r, Math.Max(g, b));
                                    }

                                    if (effA > 0 || r > 0 || g > 0 || b > 0)
                                    {
                                        localNonZero++;
                                    }
                                    if (effA == 255) localOpaque++;

                                    if (effA == 255)
                                    {
                                        d[0] = b;
                                        d[1] = g;
                                        d[2] = r;
                                        d[3] = 255;
                                    }
                                    else if (effA == 0)
                                    {
                                        *(uint*)d = 0;
                                    }
                                    else
                                    {
                                        // Windows AC_SRC_ALPHA requires premultiplied alpha: R <= A, G <= A, B <= A.
                                        // Unity UI blending into the (0,0,0,0) target already premultiplies RGB by A.
                                        // Clamping to effA ensures valid premultiplied format without double-multiplying.
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
                            });
                        }
                    }
                }

                float opaqueRatio = (float)opaquePixelCount / totalPixels;

                if (diagLog)
                {
                    int centerIdx = (height / 2 * width + width / 2) * 4;
                    logger?.LogInfo($"[RemixUIOverlay] AsyncFrame #{updateLogCounter}: {width}x{height}, nonZero={nonZeroPixelCount}, opaque={opaquePixelCount} ({opaqueRatio:P2}), center=(R={processPixels[centerIdx]},G={processPixels[centerIdx+1]},B={processPixels[centerIdx+2]},A={processPixels[centerIdx+3]}), visible={isOverlayVisible}");
                }



                // If completely empty (no UI pixels rendered at all), hide overlay
                if (nonZeroPixelCount == 0)
                {
                    if (isOverlayVisible)
                    {
                        ShowWindow(overlayWindow, SW_HIDE);
                        isOverlayVisible = false;
                    }
                    return;
                }

                // Update Win32 Layered Window
                var ptDst = new POINT { x = 0, y = 0 };
                ClientToScreen(gameWindow, ref ptDst);
                var sizeDst = new SIZE { cx = width, cy = height };
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
                    SetWindowPos(overlayWindow, HWND_TOP, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
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
                        ShowWindow(overlayWindow, SW_HIDE);
                        isOverlayVisible = false;
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
                        cam.SetTargetBuffers(uiRenderTexture.colorBuffer, uiRenderTexture.depthBuffer);
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
        public RenderTexture targetTexture;
        public CameraClearFlags clearFlags = CameraClearFlags.Depth;
        public Color backgroundColor = new Color(0, 0, 0, 0);
        public ManualLogSource logger;

        private Camera cam;
        private int hookCount = 0;

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

        private void EnsureConfigured(string stage)
        {
            if (cam == null) cam = GetComponent<Camera>();
            if (cam != null && targetTexture != null)
            {
                if (stage == "OnPreRender" && (hookCount++ < 15 || hookCount % 180 == 0))
                {
                    logger?.LogInfo($"[RemixUICameraHook] #{hookCount} {stage} on '{cam.name}' - preTarget='{cam.targetTexture?.name ?? "null"}', preClear={cam.clearFlags}, preMask=0x{cam.cullingMask:X}, assigning target '{targetTexture.name}'");
                }

                cam.targetTexture = targetTexture;
                cam.SetTargetBuffers(targetTexture.colorBuffer, targetTexture.depthBuffer);
                cam.clearFlags = clearFlags;
                cam.backgroundColor = backgroundColor;

                int alwaysOnTopLayer = LayerMask.NameToLayer("AlwaysOnTop");
                int alwaysOnTopMask = alwaysOnTopLayer >= 0 ? (1 << alwaysOnTopLayer) : 0;
                int uiLayer = LayerMask.NameToLayer("UI");
                int uiLayerBit = uiLayer >= 0 ? (1 << uiLayer) : (1 << 5);

                cam.cullingMask &= ~1; // Ensure layer 0 (Default / 3D game scene) is never rendered by UI camera
                if (alwaysOnTopMask != 0) cam.cullingMask &= ~alwaysOnTopMask; // Ensure AlwaysOnTop (if present) is never rendered by UI camera
                cam.cullingMask |= uiLayerBit;
            }
        }

        void OnPostRender()
        {
            if (hookCount <= 15 || hookCount % 180 == 0)
            {
                logger?.LogInfo($"[RemixUICameraHook] #{hookCount} OnPostRender on '{cam?.name}' finished.");
            }
        }
    }
}
