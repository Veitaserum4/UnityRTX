using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using BepInEx.Logging;
using UnityEngine;

namespace UnityRemix
{
    /// <summary>
    /// Manages a transparent Win32 layered overlay window for SingleWindow Embedded mode.
    /// Captures the autodetected UI cameras to a RenderTexture and presents them with per-pixel alpha
    /// directly on top of the embedded Remix viewport.
    /// </summary>
    public class RemixUIOverlay
    {
        private readonly ManualLogSource logger;
        private readonly IntPtr gameWindow;
        private IntPtr overlayWindow = IntPtr.Zero;

        // UI rendering state
        private RenderTexture uiRenderTexture;
        private Texture2D readbackTexture;
        private byte[] rawPixels;
        private int currentWidth = 0;
        private int currentHeight = 0;

        // Win32 DIB state for UpdateLayeredWindow
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
        private const uint WS_EX_LAYERED = 0x00080000;
        private const uint WS_EX_TRANSPARENT = 0x00000020;
        private const uint WS_EX_TOOLWINDOW = 0x00000080;

        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_SHOWWINDOW = 0x0040;

        private const int SW_HIDE = 0;
        private const int SW_SHOWNOACTIVATE = 4;

        private const byte AC_SRC_OVER = 0x00;
        private const byte AC_SRC_ALPHA = 0x01;
        private const uint ULW_ALPHA = 0x00000002;

        private bool isOverlayVisible = true;
        private bool hasLoggedOpaqueWarning = false;
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
            foreach (var cam in managedCameras)
            {
                if (cam == null) continue;
                cam.targetTexture = uiRenderTexture;
                cam.SetTargetBuffers(uiRenderTexture.colorBuffer, uiRenderTexture.depthBuffer);
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0, 0, 0, 0);
                cam.cullingMask &= ~1;
            }
        }

        public RemixUIOverlay(ManualLogSource logger, IntPtr gameWindow)
        {
            this.logger = logger;
            this.gameWindow = gameWindow;
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
                WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW,
                "STATIC",
                "UnityRemix_UIOverlay",
                WS_POPUP | WS_VISIBLE,
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
        /// Should be called after cameras have rendered (e.g. in LateUpdate or OnPostRender).
        /// </summary>
        public void UpdateOverlay()
        {
            if (overlayWindow == IntPtr.Zero || uiRenderTexture == null) return;

            SyncWindowBounds();

            int width = uiRenderTexture.width;
            int height = uiRenderTexture.height;

            EnsureDIB(width, height);
            if (overlayHdc == IntPtr.Zero || overlayBits == IntPtr.Zero) return;

            bool diagLog = (updateLogCounter++ < 20) || (updateLogCounter % 120 == 0);

            // Readback from RenderTexture
            if (readbackTexture == null || readbackTexture.width != width || readbackTexture.height != height)
            {
                if (readbackTexture != null) UnityEngine.Object.Destroy(readbackTexture);
                readbackTexture = new Texture2D(width, height, TextureFormat.RGBA32, false);
                rawPixels = new byte[width * height * 4];
            }

            var prevActive = RenderTexture.active;
            RenderTexture.active = uiRenderTexture;
            readbackTexture.ReadPixels(new UnityEngine.Rect(0, 0, width, height), 0, 0, false);
            readbackTexture.Apply(false, false);
            RenderTexture.active = prevActive;

            // Extract RGBA bytes
            var pixelData = readbackTexture.GetRawTextureData<byte>();
            pixelData.CopyTo(rawPixels);

            int opaquePixelCount = 0;
            int nonZeroPixelCount = 0;
            int totalPixels = width * height;
            byte maxA = 0;

            // Convert RGBA to BGRA with premultiplied alpha for UpdateLayeredWindow
            unsafe
            {
                fixed (byte* pSrc = rawPixels)
                {
                    byte* pDst = (byte*)overlayBits;
                    byte* s = pSrc;
                    byte* d = pDst;

                    for (int i = 0; i < totalPixels; i++)
                    {
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
                        // Pitch black with a == 255 in an overlay context is an opaque blackout quad or background fill.
                        // Treat as transparent so the 3D raytraced world shows through cleanly behind the UI.
                        else if (effA == 255 && r == 0 && g == 0 && b == 0)
                        {
                            effA = 0;
                        }

                        if (effA > 0 || r > 0 || g > 0 || b > 0) nonZeroPixelCount++;
                        if (effA > maxA) maxA = effA;
                        if (effA > 200) opaquePixelCount++;

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
                            d[0] = (byte)((b * effA) / 255);
                            d[1] = (byte)((g * effA) / 255);
                            d[2] = (byte)((r * effA) / 255);
                            d[3] = effA;
                        }

                        s += 4;
                        d += 4;
                    }
                }
            }

            float opaqueRatio = (float)opaquePixelCount / totalPixels;

            if (diagLog)
            {
                int centerIdx = (height / 2 * width + width / 2) * 4;
                logger?.LogInfo($"[RemixUIOverlay] Frame #{updateLogCounter}: {width}x{height}, nonZero={nonZeroPixelCount}, opaque={opaquePixelCount} ({opaqueRatio:P2}), maxAlpha={maxA}, center=(R={rawPixels[centerIdx]},G={rawPixels[centerIdx+1]},B={rawPixels[centerIdx+2]},A={rawPixels[centerIdx+3]}), visible={isOverlayVisible}");
            }

            if (opaqueRatio > 0.98f)
            {
                if (!hasLoggedOpaqueWarning)
                {
                    logger?.LogWarning($"[RemixUIOverlay] UI camera output is {opaqueRatio * 100:F1}% opaque! Suppressing overlay to prevent black screen.");
                    hasLoggedOpaqueWarning = true;
                }
                if (isOverlayVisible)
                {
                    ShowWindow(overlayWindow, SW_HIDE);
                    isOverlayVisible = false;
                }
                return;
            }
            else
            {
                hasLoggedOpaqueWarning = false;
            }

            // If completely empty (no UI pixels rendered at all), hide overlay
            if (nonZeroPixelCount == 0)
            {
                if (diagLog)
                {
                    logger?.LogInfo($"[RemixUIOverlay] Frame #{updateLogCounter}: Overlay hidden because nonZeroPixelCount == 0 (no UI drawn into RenderTexture).");
                }
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

            bool ok = UpdateLayeredWindow(
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

            if (!ok)
            {
                int err = Marshal.GetLastWin32Error();
                logger?.LogError($"[RemixUIOverlay] UpdateLayeredWindow failed! Win32 Error: {err}");
            }
            else if (diagLog)
            {
                logger?.LogInfo($"[RemixUIOverlay] UpdateLayeredWindow succeeded at ({ptDst.x},{ptDst.y},{sizeDst.cx},{sizeDst.cy})");
            }

            if (!isOverlayVisible)
            {
                ShowWindow(overlayWindow, SW_SHOWNOACTIVATE);
                BringWindowToTop(overlayWindow);
                isOverlayVisible = true;
            }
        }

        public void SyncWindowBounds()
        {
            if (overlayWindow == IntPtr.Zero || gameWindow == IntPtr.Zero) return;

            if (GetClientRect(gameWindow, out RECT clientRect) && clientRect.Width > 0 && clientRect.Height > 0)
            {
                var pt = new POINT { x = 0, y = 0 };
                ClientToScreen(gameWindow, ref pt);

                SetWindowPos(
                    overlayWindow,
                    HWND_TOP,
                    pt.x, pt.y, clientRect.Width, clientRect.Height,
                    SWP_NOACTIVATE | SWP_SHOWWINDOW
                );
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
            }
        }

        private void EnsureDIB(int width, int height)
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

        private void CleanupDIB()
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

            if (readbackTexture != null)
            {
                UnityEngine.Object.Destroy(readbackTexture);
                readbackTexture = null;
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
                cam.cullingMask &= ~1; // Ensure layer 0 (Default / 3D game scene) is never rendered by UI camera
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
