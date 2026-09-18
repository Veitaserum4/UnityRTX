using System;
using System.Runtime.InteropServices;
using BepInEx.Logging;
using UnityEngine;

namespace UnityRemix
{
    /// <summary>
    /// Manages Win32 window creation and D3D9 device initialization for Remix
    /// </summary>
    public class RemixWindowManager
    {
        private readonly ManualLogSource logger;
        private RemixAPI.PFN_remixapi_dxvk_CreateD3D9 createD3D9Func;
        private RemixAPI.PFN_remixapi_Startup startupFunc;
        
        private IntPtr remixWindow = IntPtr.Zero;
        private int windowWidth = 1920;
        private int windowHeight = 1080;
        
        // Window management delegates and state
        private static WndProcDelegate wndProcDelegate;
        private static bool windowClassRegistered = false;
        private const string WINDOW_CLASS_NAME = "RemixWindowClass";
        
        private readonly BepInEx.Configuration.ConfigEntry<bool> configSingleWindow;
        private readonly BepInEx.Configuration.ConfigEntry<SingleWindowMethod> configSingleWindowMethod;
        private static RemixWindowManager instance;
        private IntPtr gameWindow = IntPtr.Zero;
        private bool isEmbedded = false;
        private static bool isEmbeddedStatic = false;
        private static volatile bool isRemixUIOpen = false;
        public static volatile bool ShouldHideCursor = false;

        [StructLayout(LayoutKind.Sequential)]
        public struct CURSORINFO
        {
            public int cbSize;
            public int flags;
            public IntPtr hCursor;
            public POINT ptScreenPos;
        }

        [DllImport("user32.dll")]
        public static extern bool GetCursorInfo(out CURSORINFO pci);

        public static string GetCursorDiagnosticString()
        {
            var ci = new CURSORINFO();
            ci.cbSize = Marshal.SizeOf<CURSORINFO>();
            if (GetCursorInfo(out ci))
            {
                string state = (ci.flags == 0) ? "HIDDEN(0)" : ((ci.flags == 1) ? "SHOWING(1)" : $"FLAG({ci.flags})");
                return $"{state}, hCursor=0x{ci.hCursor:X}, pos=({ci.ptScreenPos.x},{ci.ptScreenPos.y})";
            }
            return "GetCursorInfo failed";
        }

        [DllImport("user32.dll")]
        private static extern IntPtr CreateCursor(IntPtr hInst, int xHotSpot, int yHotSpot, int nWidth, int nHeight, byte[] pvANDPlane, byte[] pvXORPlane);

        [DllImport("user32.dll", EntryPoint = "SetClassLongPtrW")]
        private static extern IntPtr SetClassLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("user32.dll", EntryPoint = "SetClassLongW")]
        private static extern IntPtr SetClassLong32(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        public static IntPtr SetClassLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong)
        {
            if (hWnd == IntPtr.Zero || !IsWindow(hWnd)) return IntPtr.Zero;
            if (IntPtr.Size == 8)
                return SetClassLongPtr64(hWnd, nIndex, dwNewLong);
            else
                return SetClassLong32(hWnd, nIndex, dwNewLong);
        }

        private const int GCLP_HCURSOR = -12;

        private static IntPtr blankCursorHandle = IntPtr.Zero;
        public static IntPtr BlankCursor
        {
            get
            {
                if (blankCursorHandle == IntPtr.Zero)
                {
                    byte[] andMask = new byte[128];
                    for (int i = 0; i < 128; i++) andMask[i] = 0xFF; // Transparent
                    byte[] xorMask = new byte[128]; // 0x00
                    IntPtr hInst = GetModuleHandleW(null);
                    blankCursorHandle = CreateCursor(hInst != IntPtr.Zero ? hInst : IntPtr.Zero, 0, 0, 32, 32, andMask, xorMask);
                }
                return blankCursorHandle;
            }
        }

        [DllImport("user32.dll")]
        public static extern int ShowCursor(bool bShow);

        private static bool cursorStateInitialized = false;
        private static RECT lastClipRect;
        private static bool isCursorClipped = false;

        public static void UpdateCursorClipping(bool shouldClip)
        {
            if (shouldClip)
            {
                IntPtr gWnd = instance != null && instance.gameWindow != IntPtr.Zero ? instance.gameWindow : FindGameWindow();
                if (gWnd != IntPtr.Zero && GetClientRect(gWnd, out RECT rc) && rc.Width > 10 && rc.Height > 10)
                {
                    POINT topLeft = new POINT { x = rc.left, y = rc.top };
                    POINT bottomRight = new POINT { x = rc.right, y = rc.bottom };
                    ClientToScreen(gWnd, ref topLeft);
                    ClientToScreen(gWnd, ref bottomRight);

                    RECT clipRect = new RECT
                    {
                        left = topLeft.x + 4,
                        top = topLeft.y + 4,
                        right = Math.Max(topLeft.x + 5, bottomRight.x - 4),
                        bottom = Math.Max(topLeft.y + 5, bottomRight.y - 4)
                    };

                    if (!isCursorClipped || clipRect.left != lastClipRect.left || clipRect.top != lastClipRect.top ||
                        clipRect.right != lastClipRect.right || clipRect.bottom != lastClipRect.bottom)
                    {
                        ClipCursor(ref clipRect);
                        lastClipRect = clipRect;
                        isCursorClipped = true;
                    }
                    return;
                }
            }

            if (isCursorClipped)
            {
                ClipCursor(IntPtr.Zero);
                isCursorClipped = false;
                lastClipRect = default;
            }
        }

        public static void EnforceCursorHidden()
        {
            var ci = new CURSORINFO();
            ci.cbSize = Marshal.SizeOf<CURSORINFO>();
            if (GetCursorInfo(out ci) && (ci.flags & 1) == 0)
            {
                return;
            }

            int safety = 0;
            while (ShowCursor(false) >= 0 && safety++ < 1000) { }
        }

        public static void EnforceCursorVisible()
        {
            var ci = new CURSORINFO();
            ci.cbSize = Marshal.SizeOf<CURSORINFO>();
            if (GetCursorInfo(out ci) && (ci.flags & 1) != 0)
            {
                return;
            }

            int safety = 0;
            while (ShowCursor(true) < 0 && safety++ < 1000) { }
        }

        public static void UpdateCursorVisibility(bool shouldHide)
        {
            if (!cursorStateInitialized || ShouldHideCursor != shouldHide)
            {
                cursorStateInitialized = true;
                ShouldHideCursor = shouldHide;

                IntPtr targetCursor = shouldHide ? BlankCursor : LoadCursorW(IntPtr.Zero, IDC_ARROW);
                SetCursor(targetCursor);

                IntPtr gWnd = instance != null && instance.gameWindow != IntPtr.Zero ? instance.gameWindow : FindGameWindow();
                if (gWnd != IntPtr.Zero)
                {
                    SetClassLongPtr(gWnd, GCLP_HCURSOR, targetCursor);
                }
                if (instance != null && instance.remixWindow != IntPtr.Zero)
                {
                    SetClassLongPtr(instance.remixWindow, GCLP_HCURSOR, targetCursor);
                }
                if (RemixUIOverlay.Instance != null && RemixUIOverlay.Instance.OverlayWindow != IntPtr.Zero)
                {
                    SetClassLongPtr(RemixUIOverlay.Instance.OverlayWindow, GCLP_HCURSOR, targetCursor);
                }
            }

            if (shouldHide)
            {
                EnforceCursorHidden();
                UpdateCursorClipping(Cursor.lockState == CursorLockMode.Locked);
                SetCursor(BlankCursor);
            }
            else
            {
                EnforceCursorVisible();
                UpdateCursorClipping(false);
            }
        }

        public IntPtr RemixWindow => remixWindow;
        public IntPtr GameWindow => gameWindow;
        public bool IsEmbedded => isEmbedded;
        public int WindowWidth => windowWidth;
        public int WindowHeight => windowHeight;
        public static bool IsRemixUIOpen => isRemixUIOpen;
        public static void SetRemixUIOpen(bool open) => isRemixUIOpen = open;
        public static void ToggleRemixUI() => isRemixUIOpen = !isRemixUIOpen;

        /// <summary>
        /// Continuously queries the ground-truth UI state directly from the Remix runtime (RtxOptions::showUI).
        /// Prevents state inversion across scene loads, startup modals (e.g. preset selection), or mouse clicks on ImGui windows.
        /// </summary>
        public static void SyncUIStateWithRemix(ManualLogSource logger = null)
        {
            if (RemixAPI.GetUIStateFunc != null)
            {
                try
                {
                    var state = RemixAPI.GetUIStateFunc();
                    bool remixOpen = (state != RemixAPI.remixapi_UIState.REMIXAPI_UI_STATE_NONE);
                    if (remixOpen != isRemixUIOpen)
                    {
                        isRemixUIOpen = remixOpen;
                        logger?.LogInfo($"[RemixWindowManager] Synced isRemixUIOpen with Remix runtime: {isRemixUIOpen} (UIState={state})");
                        OnRemixUIStateChanged(isRemixUIOpen, logger);
                    }
                }
                catch (Exception ex)
                {
                    logger?.LogWarning($"[RemixWindowManager] Failed to query GetUIState: {ex.Message}");
                }
            }
        }

        public static void OnRemixUIStateChanged(bool open, ManualLogSource logger = null)
        {
            if (isEmbeddedStatic && instance != null && instance.remixWindow != IntPtr.Zero)
            {
                ReleaseCapture();
                EnableWindow(instance.remixWindow, open);
                if (open)
                {
                    UpdateCursorClipping(false);
                }
                else if (instance.gameWindow != IntPtr.Zero)
                {
                    SetForegroundWindow(instance.gameWindow);
                    SetActiveWindow(instance.gameWindow);
                    SetFocus(instance.gameWindow);
                }
                ReleaseCapture();
            }
            RemixGameStateHelper.SetRemixMenuState(open, logger);
        }

        public void HandleAltX()
        {
            if (RemixAPI.SetUIStateFunc != null && RemixAPI.GetUIStateFunc != null)
            {
                var curState = RemixAPI.GetUIStateFunc();
                var newState = (curState == RemixAPI.remixapi_UIState.REMIXAPI_UI_STATE_NONE)
                    ? RemixAPI.remixapi_UIState.REMIXAPI_UI_STATE_BASIC
                    : RemixAPI.remixapi_UIState.REMIXAPI_UI_STATE_NONE;
                RemixAPI.SetUIStateFunc(newState);
                SyncUIStateWithRemix(logger);
            }
            else
            {
                ToggleRemixUI();
                if (remixWindow != IntPtr.Zero)
                {
                    ReleaseCapture();
                    if (isEmbedded)
                    {
                        EnableWindow(remixWindow, isRemixUIOpen);
                    }

                    PostMessage(remixWindow, WM_SYSKEYDOWN, (IntPtr)0x58 /* VK_X */, (IntPtr)0x20000001);
                    PostMessage(remixWindow, WM_SYSKEYUP, (IntPtr)0x58 /* VK_X */, (IntPtr)unchecked((int)0xE0000001));

                    if (gameWindow != IntPtr.Zero)
                    {
                        SetForegroundWindow(gameWindow);
                        SetActiveWindow(gameWindow);
                        SetFocus(gameWindow);
                    }

                    UpdateCursorClipping(false);
                    ReleaseCapture();
                }

                RemixGameStateHelper.SetRemixMenuState(isRemixUIOpen, logger);
            }
        }
        
        #region Win32 API Declarations

        [DllImport("user32.dll")]
        private static extern bool EnableWindow(IntPtr hWnd, bool bEnable);

        [DllImport("user32.dll")]
        private static extern bool ClipCursor(IntPtr lpRect);

        [DllImport("user32.dll")]
        private static extern bool ClipCursor(ref RECT lpRect);

        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        private static extern IntPtr SetActiveWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr SetCursor(IntPtr hCursor);

        private const uint WM_SETCURSOR = 0x0020;
        private const uint WM_MOUSEMOVE = 0x0200;
        private const uint WM_LBUTTONDOWN = 0x0201;
        private const uint WM_LBUTTONUP = 0x0202;
        private const uint WM_RBUTTONDOWN = 0x0204;
        private const uint WM_RBUTTONUP = 0x0205;
        private const uint WM_MBUTTONDOWN = 0x0207;
        private const uint WM_MBUTTONUP = 0x0208;

        [DllImport("comctl32.dll", SetLastError = true)]
        private static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProc pfnSubclass, UIntPtr uIdSubclass, UIntPtr dwRefData);

        [DllImport("comctl32.dll", SetLastError = true)]
        private static extern bool RemoveWindowSubclass(IntPtr hWnd, SubclassProc pfnSubclass, UIntPtr uIdSubclass);

        [DllImport("comctl32.dll")]
        private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

        private delegate IntPtr SubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, UIntPtr uIdSubclass, UIntPtr dwRefData);
        private static SubclassProc gameWindowSubclassDelegate;
        private static bool gameWindowSubclassed = false;
        private const uint SUBCLASS_ID_GAME_WINDOW = 1001;


        private static IntPtr GameWindowSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, UIntPtr uIdSubclass, UIntPtr dwRefData)
        {
            if (ShouldHideCursor && !isRemixUIOpen)
            {
                if (uMsg == WM_MOUSEMOVE)
                {
                    SetCursor(BlankCursor);
                }
                else if (uMsg == WM_NCHITTEST)
                {
                    IntPtr hit = DefSubclassProc(hWnd, uMsg, wParam, lParam);
                    int hitCode = hit.ToInt32();
                    // 10=HTLEFT, 11=HTRIGHT, 12=HTTOP, 13=HTTOPLEFT, 14=HTTOPRIGHT, 15=HTBOTTOM, 16=HTBOTTOMLEFT, 17=HTBOTTOMRIGHT, 18=HTBORDER, 4=HTGROWBOX
                    if ((hitCode >= 10 && hitCode <= 18) || hitCode == 4)
                    {
                        return (IntPtr)HTCLIENT;
                    }
                    return hit;
                }
            }

            if (uMsg == WM_SETCURSOR)
            {
                if (isRemixUIOpen)
                {
                    SetCursor(LoadCursorW(IntPtr.Zero, IDC_ARROW));
                    return new IntPtr(1);
                }
                else if (ShouldHideCursor)
                {
                    SetCursor(BlankCursor);
                    return new IntPtr(1);
                }
            }
            return DefSubclassProc(hWnd, uMsg, wParam, lParam);
        }
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();
        
        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern IntPtr SetFocus(IntPtr hWnd);

        private const uint WM_SYSKEYDOWN = 0x0104;
        private const uint WM_SYSKEYUP = 0x0105;

        [DllImport("user32.dll")]
        private static extern IntPtr GetActiveWindow();
        
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

        [DllImport("user32.dll")]
        private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO pbmi, uint iUsage, out IntPtr ppvBits, IntPtr hSection, uint dwOffset);

        [DllImport("gdi32.dll")]
        private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);

        [DllImport("gdi32.dll")]
        private static extern bool BitBlt(IntPtr hdcDest, int nXDest, int nYDest, int nWidth, int nHeight, IntPtr hdcSrc, int nXSrc, int nYSrc, uint dwRop);

        [DllImport("user32.dll")]
        private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);
        
        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr CreateWindowExW(
            uint dwExStyle, 
            [MarshalAs(UnmanagedType.LPWStr)] string lpClassName,
            [MarshalAs(UnmanagedType.LPWStr)] string lpWindowName,
            uint dwStyle,
            int x, int y, int nWidth, int nHeight,
            IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);
        
        [DllImport("user32.dll")]
        private static extern bool DestroyWindow(IntPtr hWnd);
        
        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        
        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, 
            int X, int Y, int cx, int cy, uint uFlags);
        
        [DllImport("user32.dll")]
        private static extern int SetWindowLongW(IntPtr hWnd, int nIndex, int dwNewLong);
        
        [DllImport("user32.dll")]
        private static extern int GetWindowLongW(IntPtr hWnd, int nIndex);
        
        [DllImport("kernel32.dll")]
        private static extern IntPtr GetModuleHandleW([MarshalAs(UnmanagedType.LPWStr)] string lpModuleName);
        
        [DllImport("user32.dll")]
        private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hWnd);
        
        [DllImport("user32.dll", SetLastError = true)]
        private static extern ushort RegisterClassW(ref WNDCLASS lpWndClass);
        
        [DllImport("user32.dll")]
        private static extern bool UnregisterClassW([MarshalAs(UnmanagedType.LPWStr)] string lpClassName, IntPtr hInstance);
        
        [DllImport("user32.dll")]
        private static extern IntPtr LoadCursorW(IntPtr hInstance, int lpCursorName);
        
        [DllImport("user32.dll")]
        private static extern IntPtr BeginPaint(IntPtr hWnd, out PAINTSTRUCT lpPaint);
        
        [DllImport("user32.dll")]
        private static extern bool EndPaint(IntPtr hWnd, ref PAINTSTRUCT lpPaint);
        
        [DllImport("user32.dll")]
        private static extern bool PeekMessageW(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);
        
        [DllImport("user32.dll")]
        private static extern bool TranslateMessage(ref MSG lpMsg);
        
        [DllImport("user32.dll")]
        private static extern IntPtr DispatchMessageW(ref MSG lpMsg);
        
        [DllImport("user32.dll")]
        private static extern uint MsgWaitForMultipleObjectsEx(uint nCount, IntPtr[] pHandles, uint dwMilliseconds, uint dwWakeMask, uint dwFlags);
        
        private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        
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
        
        [StructLayout(LayoutKind.Sequential)]
        private struct PAINTSTRUCT
        {
            public IntPtr hdc;
            public bool fErase;
            public int rcPaint_left;
            public int rcPaint_top;
            public int rcPaint_right;
            public int rcPaint_bottom;
            public bool fRestore;
            public bool fIncUpdate;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
            public byte[] rgbReserved;
        }
        
        [StructLayout(LayoutKind.Sequential)]
        private struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public int pt_x;
            public int pt_y;
        }
        
        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int x;
            public int y;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int left;
            public int top;
            public int right;
            public int bottom;
            public int Width => right - left;
            public int Height => bottom - top;
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
        
        // Constants
        private const int IDC_ARROW = 32512;
        private const uint CS_HREDRAW = 0x0002;
        private const uint CS_VREDRAW = 0x0001;
        private const uint WM_PAINT = 0x000F;
        private const uint WM_ERASEBKGND = 0x0014;
        private const uint WM_NCHITTEST = 0x0084;
        private const int HTCLIENT = 1;
        private const int HTTRANSPARENT = -1;
        private const uint WM_MOUSEACTIVATE = 0x0021;
        private const int MA_ACTIVATE = 1;
        private const int MA_NOACTIVATE = 3;
        private const uint WS_OVERLAPPEDWINDOW = 0x00CF0000;
        private const uint WS_POPUP = 0x80000000;
        private const uint WS_CHILD = 0x40000000;
        private const uint WS_VISIBLE = 0x10000000;
        private const uint WS_CLIPSIBLINGS = 0x04000000;
        private const uint WS_CLIPCHILDREN = 0x02000000;
        private const uint WS_DISABLED = 0x08000000;
        private const uint WS_EX_LAYERED = 0x00080000;
        private const uint WS_EX_TRANSPARENT = 0x00000020;
        private const uint WS_EX_TOPMOST = 0x00000008;
        private const uint WS_EX_TOOLWINDOW = 0x00000080;
        private const uint WS_EX_APPWINDOW = 0x00040000;
        private const uint WS_EX_NOACTIVATE = 0x08000000;
        private const int GWL_STYLE = -16;
        private const int GWL_EXSTYLE = -20;
        private const int SW_HIDE = 0;
        private const int SW_SHOWNOACTIVATE = 4;
        private const int SW_SHOW = 5;
        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_FRAMECHANGED = 0x0020;
        private const uint SWP_SHOWWINDOW = 0x0040;
        private const uint PM_REMOVE = 0x0001;
        private const uint QS_ALLINPUT = 0x04FF;
        private const uint MWMO_INPUTAVAILABLE = 0x0004;
        private const uint WAIT_OBJECT_0 = 0;
        private const uint WAIT_TIMEOUT = 0x00000102;
        
        #endregion
        
        public RemixWindowManager(
            ManualLogSource logger,
            RemixAPI.remixapi_Interface remixInterface,
            BepInEx.Configuration.ConfigEntry<bool> singleWindow = null,
            BepInEx.Configuration.ConfigEntry<SingleWindowMethod> singleWindowMethod = null)
        {
            instance = this;
            this.logger = logger;
            this.configSingleWindow = singleWindow;
            this.configSingleWindowMethod = singleWindowMethod;
            
            // Cache delegates
            if (remixInterface.dxvk_CreateD3D9 != IntPtr.Zero)
            {
                createD3D9Func = Marshal.GetDelegateForFunctionPointer<RemixAPI.PFN_remixapi_dxvk_CreateD3D9>(
                    remixInterface.dxvk_CreateD3D9);
            }
            
            if (remixInterface.Startup != IntPtr.Zero)
            {
                startupFunc = Marshal.GetDelegateForFunctionPointer<RemixAPI.PFN_remixapi_Startup>(
                    remixInterface.Startup);
            }
        }
        
        /// <summary>
        /// Store window dimensions for later creation
        /// </summary>
        public void SetWindowDimensions(int width, int height)
        {
            windowWidth = width > 0 ? width : 1920;
            windowHeight = height > 0 ? height : 1080;
            logger.LogInfo($"Window dimensions set to {windowWidth}x{windowHeight}");
        }

        /// <summary>
        /// Finds the Unity game window handle for parenting / single-window presentation.
        /// </summary>
        public static IntPtr FindGameWindow()
        {
            try
            {
                IntPtr mainWnd = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;
                if (mainWnd != IntPtr.Zero && IsWindowVisible(mainWnd))
                    return mainWnd;
            }
            catch { }

            uint currentPid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
            IntPtr found = IntPtr.Zero;

            try
            {
                EnumWindows((hWnd, lParam) =>
                {
                    if (!IsWindowVisible(hWnd)) return true;

                    GetWindowThreadProcessId(hWnd, out uint pid);
                    if (pid == currentPid)
                    {
                        var sbClass = new System.Text.StringBuilder(256);
                        GetClassName(hWnd, sbClass, 256);
                        string className = sbClass.ToString();

                        if (className == "UnityWndClass")
                        {
                            found = hWnd;
                            return false;
                        }

                        if (found == IntPtr.Zero)
                        {
                            found = hWnd;
                        }
                    }
                    return true;
                }, IntPtr.Zero);
            }
            catch { }

            return found;
        }

        public void EnsureGameWindowSubclassed()
        {
            if (gameWindow == IntPtr.Zero)
            {
                gameWindow = FindGameWindow();
            }

            if (gameWindow != IntPtr.Zero && !gameWindowSubclassed)
            {
                gameWindowSubclassDelegate = GameWindowSubclassProc;
                gameWindowSubclassed = SetWindowSubclass(gameWindow, gameWindowSubclassDelegate, (UIntPtr)SUBCLASS_ID_GAME_WINDOW, UIntPtr.Zero);
                if (gameWindowSubclassed)
                {
                    logger?.LogInfo($"[RemixWindowManager] Subclassed gameWindow 0x{gameWindow:X} to intercept WM_SETCURSOR and eliminate cursor flicker.");
                }
            }
        }

        /// <summary>
        /// Synchronizes the embedded child window size and position with the parent game window.
        /// </summary>
        public void SyncWindowBounds()
        {
            EnsureGameWindowSubclassed();

            if (!isEmbedded || remixWindow == IntPtr.Zero || gameWindow == IntPtr.Zero)
                return;

            if (GetClientRect(gameWindow, out RECT rect))
            {
                if (rect.Width > 0 && rect.Height > 0 && (rect.Width != windowWidth || rect.Height != windowHeight))
                {
                    windowWidth = rect.Width;
                    windowHeight = rect.Height;
                    SetWindowPos(remixWindow, IntPtr.Zero, 0, 0, rect.Width, rect.Height, SWP_NOZORDER | SWP_NOACTIVATE);
                }
            }
        }
        
        /// <summary>
        /// Window procedure callback
        /// </summary>
        private static IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            switch (msg)
            {
                case WM_PAINT:
                    // CRITICAL: Must handle WM_PAINT or Windows marks window as frozen
                    PAINTSTRUCT ps;
                    BeginPaint(hWnd, out ps);
                    EndPaint(hWnd, ref ps);
                    return IntPtr.Zero;
                
                case WM_ERASEBKGND:
                    return new IntPtr(1);
                    
                case WM_LBUTTONDOWN:
                case WM_RBUTTONDOWN:
                case WM_MBUTTONDOWN:
                case WM_LBUTTONUP:
                case WM_RBUTTONUP:
                case WM_MBUTTONUP:
                    break;

                case WM_MOUSEACTIVATE:
                    if (isEmbeddedStatic)
                    {
                        return isRemixUIOpen ? new IntPtr(MA_ACTIVATE) : new IntPtr(MA_NOACTIVATE);
                    }
                    break;

                case WM_NCHITTEST:
                    if (isEmbeddedStatic)
                    {
                        // When Remix UI is not open, make child window transparent to mouse
                        // so all mouse clicks and movements pass through to Unity game window!
                        if (!isRemixUIOpen)
                            return new IntPtr(HTTRANSPARENT);
                    }
                    return DefWindowProcW(hWnd, msg, wParam, lParam);

                case WM_SETCURSOR:
                    if (isRemixUIOpen)
                    {
                        SetCursor(LoadCursorW(IntPtr.Zero, IDC_ARROW));
                        return new IntPtr(1);
                    }
                    else if (ShouldHideCursor)
                    {
                        SetCursor(BlankCursor);
                        return new IntPtr(1);
                    }
                    return DefWindowProcW(hWnd, msg, wParam, lParam);
            }
            
            return DefWindowProcW(hWnd, msg, wParam, lParam);
        }
        
        /// <summary>
        /// Create Remix window on render thread
        /// </summary>
        public bool CreateRemixWindow()
        {
            IntPtr hInstance = GetModuleHandleW(null);
            
            // Register window class if needed
            if (!windowClassRegistered)
            {
                wndProcDelegate = WndProc;
                
                var wc = new WNDCLASS
                {
                    style = CS_HREDRAW | CS_VREDRAW,
                    lpfnWndProc = Marshal.GetFunctionPointerForDelegate(wndProcDelegate),
                    cbClsExtra = 0,
                    cbWndExtra = 0,
                    hInstance = hInstance,
                    hIcon = IntPtr.Zero,
                    hCursor = IntPtr.Zero,
                    hbrBackground = IntPtr.Zero,
                    lpszMenuName = null,
                    lpszClassName = WINDOW_CLASS_NAME
                };
                
                ushort classAtom = RegisterClassW(ref wc);
                if (classAtom == 0)
                {
                    int error = Marshal.GetLastWin32Error();
                    logger.LogError($"Failed to register window class, error: {error}");
                    return false;
                }
                
                windowClassRegistered = true;
                logger.LogInfo("Window class registered successfully");
            }
            
            bool isSingleWindow = configSingleWindow != null && configSingleWindow.Value;
            SingleWindowMethod method = configSingleWindowMethod != null ? configSingleWindowMethod.Value : SingleWindowMethod.Embedded;
            
            if (isSingleWindow)
            {
                gameWindow = FindGameWindow();
                if (gameWindow == IntPtr.Zero)
                {
                    logger.LogWarning("[RemixWindowManager] SingleWindow mode enabled, but Unity game window not found! Falling back to standalone window.");
                    isSingleWindow = false;
                }
                else
                {
                    logger.LogInfo($"[RemixWindowManager] SingleWindow mode active: game window = 0x{gameWindow:X}, method = {method}");
                    EnsureGameWindowSubclassed();
                }
            }

            int posX = 100;
            int posY = 100;
            int width = windowWidth;
            int height = windowHeight;
            uint dwStyle = WS_OVERLAPPEDWINDOW | WS_VISIBLE;
            uint dwExStyle = WS_EX_APPWINDOW;
            IntPtr parentHwnd = IntPtr.Zero;

            if (isSingleWindow && method == SingleWindowMethod.Embedded)
            {
                if (GetClientRect(gameWindow, out RECT clientRect) && clientRect.Width > 0 && clientRect.Height > 0)
                {
                    width = clientRect.Width;
                    height = clientRect.Height;
                    windowWidth = width;
                    windowHeight = height;
                }
                posX = 0;
                posY = 0;
                dwStyle = WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS | WS_DISABLED;
                dwExStyle = WS_EX_TOOLWINDOW;
                parentHwnd = gameWindow;
                isEmbedded = true;
                isEmbeddedStatic = true;
            }
            else if (isSingleWindow && method == SingleWindowMethod.Copy)
            {
                // Hidden off-screen window for headless presentation
                posX = -32000;
                posY = -32000;
                dwStyle = WS_POPUP | WS_VISIBLE;
                dwExStyle = WS_EX_TOOLWINDOW;
                parentHwnd = IntPtr.Zero;
            }

            // Create window
            string windowTitle = $"{Application.productName} - RTX Remix - {BuildInfo.GitHash}";
            remixWindow = CreateWindowExW(
                dwExStyle,
                WINDOW_CLASS_NAME,
                windowTitle,
                dwStyle,
                posX, posY, width, height,
                parentHwnd, IntPtr.Zero, hInstance, IntPtr.Zero
            );

            // Fallback for Embedded mode if CreateWindowExW with parent fails
            if (remixWindow == IntPtr.Zero && isSingleWindow && method == SingleWindowMethod.Embedded)
            {
                logger.LogWarning("[RemixWindowManager] CreateWindowExW with parent failed; attempting SetParent fallback...");
                remixWindow = CreateWindowExW(
                    WS_EX_TOOLWINDOW,
                    WINDOW_CLASS_NAME,
                    windowTitle,
                    WS_POPUP | WS_VISIBLE,
                    0, 0, width, height,
                    IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero
                );
                if (remixWindow != IntPtr.Zero)
                {
                    SetParent(remixWindow, gameWindow);
                    int style = GetWindowLongW(remixWindow, GWL_STYLE);
                    style = (style & ~unchecked((int)WS_POPUP)) | (int)(WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS);
                    SetWindowLongW(remixWindow, GWL_STYLE, style);
                    SetWindowPos(remixWindow, IntPtr.Zero, 0, 0, width, height, SWP_NOZORDER | SWP_FRAMECHANGED | SWP_SHOWWINDOW);
                }
            }
            
            if (remixWindow == IntPtr.Zero)
            {
                int error = Marshal.GetLastWin32Error();
                logger.LogError($"Failed to create Remix window, error: {error}");
                return false;
            }
            
            logger.LogInfo($"Remix window created: 0x{remixWindow:X} (Embedded: {isEmbedded})");
            if (isSingleWindow && method == SingleWindowMethod.Copy)
            {
                ShowWindow(remixWindow, SW_HIDE);
            }
            else
            {
                ShowWindow(remixWindow, SW_SHOW);
            }

            if (isSingleWindow && isEmbedded && gameWindow != IntPtr.Zero)
            {
                // Ensure child window is initially disabled so it never intercepts mouse messages
                EnableWindow(remixWindow, false);

                SetForegroundWindow(gameWindow);
                SetFocus(gameWindow);
            }
            
            // Call Remix Startup
            logger.LogInfo("Calling Remix Startup...");
            var startupInfo = new RemixAPI.remixapi_StartupInfo
            {
                sType = RemixAPI.remixapi_StructType.REMIXAPI_STRUCT_TYPE_STARTUP_INFO,
                pNext = IntPtr.Zero,
                hwnd = remixWindow,
                disableSrgbConversionForOutput = 0,
                forceNoVkSwapchain = 0,
                editorModeEnabled = 0
            };
            
            var result = startupFunc(ref startupInfo);
            if (result != RemixAPI.remixapi_ErrorCode.REMIXAPI_ERROR_CODE_SUCCESS)
            {
                logger.LogError($"Remix Startup failed: {result}");
                DestroyWindow(remixWindow);
                remixWindow = IntPtr.Zero;
                return false;
            }
            
            logger.LogInfo("Remix Startup succeeded!");
            return true;
        }

        #region Framebuffer Capture (Copy Mode)

        private IntPtr captureHdc = IntPtr.Zero;
        private IntPtr captureDib = IntPtr.Zero;
        private IntPtr captureOldBmp = IntPtr.Zero;
        private IntPtr captureBits = IntPtr.Zero;
        private int captureWidth = 0;
        private int captureHeight = 0;
        private readonly object captureLock = new object();

        /// <summary>
        /// Captures the Remix framebuffer from the remixWindow into raw BGRA pixel bytes.
        /// </summary>
        public bool CaptureRemixFramebuffer(byte[] destination, int width, int height)
        {
            if (remixWindow == IntPtr.Zero || destination == null || width <= 0 || height <= 0 || !IsWindow(remixWindow) || !IsWindowVisible(remixWindow))
                return false;

            lock (captureLock)
            {
                if (captureHdc == IntPtr.Zero || captureWidth != width || captureHeight != height)
                {
                    CleanupCapture();

                    IntPtr screenDC = GetDC(IntPtr.Zero);
                    captureHdc = CreateCompatibleDC(screenDC);

                    var bmi = new BITMAPINFO();
                    bmi.bmiHeader.biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>();
                    bmi.bmiHeader.biWidth = width;
                    bmi.bmiHeader.biHeight = -height; // Top-down DIB
                    bmi.bmiHeader.biPlanes = 1;
                    bmi.bmiHeader.biBitCount = 32;
                    bmi.bmiHeader.biCompression = 0; // BI_RGB

                    captureDib = CreateDIBSection(captureHdc, ref bmi, 0, out captureBits, IntPtr.Zero, 0);
                    captureOldBmp = SelectObject(captureHdc, captureDib);
                    ReleaseDC(IntPtr.Zero, screenDC);

                    captureWidth = width;
                    captureHeight = height;
                }

                if (captureHdc == IntPtr.Zero || captureBits == IntPtr.Zero)
                    return false;

                bool ok = PrintWindow(remixWindow, captureHdc, 2 /* PW_RENDERFULLCONTENT */);
                if (!ok)
                {
                    IntPtr wndDC = GetDC(remixWindow);
                    if (wndDC != IntPtr.Zero)
                    {
                        ok = BitBlt(captureHdc, 0, 0, width, height, wndDC, 0, 0, 0x00CC0020 /* SRCCOPY */);
                        ReleaseDC(remixWindow, wndDC);
                    }
                }

                if (ok && captureBits != IntPtr.Zero)
                {
                    int bytesToCopy = Math.Min(destination.Length, width * height * 4);
                    Marshal.Copy(captureBits, destination, 0, bytesToCopy);
                    return true;
                }
            }

            return false;
        }

        private void CleanupCapture()
        {
            if (captureHdc != IntPtr.Zero)
            {
                if (captureOldBmp != IntPtr.Zero)
                {
                    SelectObject(captureHdc, captureOldBmp);
                    captureOldBmp = IntPtr.Zero;
                }
                DeleteDC(captureHdc);
                captureHdc = IntPtr.Zero;
            }
            if (captureDib != IntPtr.Zero)
            {
                DeleteObject(captureDib);
                captureDib = IntPtr.Zero;
            }
            captureBits = IntPtr.Zero;
        }

        #endregion
        
        /// <summary>
        /// Pump Windows messages to keep window responsive
        /// </summary>
        public void PumpWindowsMessages()
        {
            if (ShouldHideCursor)
                EnforceCursorHidden();
            else
                EnforceCursorVisible();

            MSG msg;
            while (PeekMessageW(out msg, IntPtr.Zero, 0, 0, PM_REMOVE))
            {
                TranslateMessage(ref msg);
                DispatchMessageW(ref msg);
            }
        }
        
        /// <summary>
        /// Wait for messages with timeout (for frame rate limiting)
        /// </summary>
        public bool WaitForMessages(uint milliseconds)
        {
            uint result = MsgWaitForMultipleObjectsEx(0, null, milliseconds, QS_ALLINPUT, MWMO_INPUTAVAILABLE);
            return result == WAIT_OBJECT_0; // Returns true if messages are available
        }
        
        /// <summary>
        /// Destroy Remix window
        /// </summary>
        public void DestroyRemixWindow()
        {
            UpdateCursorVisibility(false);
            EnforceCursorVisible();
            CleanupCapture();
            if (remixWindow != IntPtr.Zero)
            {
                DestroyWindow(remixWindow);
                remixWindow = IntPtr.Zero;
                logger.LogInfo("Remix window destroyed");
            }
        }
    }
}
