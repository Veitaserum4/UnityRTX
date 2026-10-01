using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
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
        private RemixAPI.PFN_remixapi_SetConfigVariable setConfigVariableFunc;
        private readonly BepInEx.Configuration.ConfigEntry<string> configNativeBackend;
        private readonly BepInEx.Configuration.ConfigEntry<bool> configOpenRemixAsyncPresent;
        private readonly BepInEx.Configuration.ConfigEntry<bool> configOpenRemixShadowEarlyOut;
        private readonly BepInEx.Configuration.ConfigEntry<bool> configOpenRemixKeepCompressed;
        private readonly BepInEx.Configuration.ConfigEntry<bool> configOpenRemixRefreshInPlace;
        private readonly BepInEx.Configuration.ConfigEntry<int> configOpenRemixBounces;
        private readonly BepInEx.Configuration.ConfigEntry<int> configOpenRemixSamples;
        private RemixAPI.PFN_remixapi_Startup startupFunc;
        private RemixAPI.PFN_remixapi_GetUIState getUIStateFunc;
        private RemixAPI.PFN_remixapi_SetUIState setUIStateFunc;
        private readonly uint processId = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
        private bool settingsKeyWasDown;
        private int nativeSettingsToggleRequested;
        
        private IntPtr remixWindow = IntPtr.Zero;
        private int windowWidth = 1920;
        private int windowHeight = 1080;
        
        // Window management delegates and state
        private static WndProcDelegate wndProcDelegate;
        private static bool windowClassRegistered = false;
        private const string WINDOW_CLASS_NAME = "RemixWindowClass";
        
        private readonly BepInEx.Configuration.ConfigEntry<bool> configSingleWindow;
        private static RemixWindowManager instance;
        private IntPtr gameWindow = IntPtr.Zero;
        private bool isEmbedded = false;
        private bool isRemixWindowVisible = true;
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
        private static bool isCursorClipped = false;

        public static void UpdateCursorClipping(bool shouldClip)
        {
            if (isCursorClipped && !shouldClip)
            {
                ClipCursor(IntPtr.Zero);
                isCursorClipped = false;
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

        private static bool lastSyncedRemixOpen = false;

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
                    if (remixOpen != lastSyncedRemixOpen)
                    {
                        lastSyncedRemixOpen = remixOpen;
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

        private static MethodInfo resetInputAxesMethod;
        private static bool resetInputAxesInitialized = false;

        public static void ResetUnityInputAxes()
        {
            if (!resetInputAxesInitialized)
            {
                resetInputAxesInitialized = true;
                try
                {
                    var inputType = Type.GetType("UnityEngine.Input, UnityEngine.InputLegacyModule") 
                                 ?? Type.GetType("UnityEngine.Input, UnityEngine");
                    resetInputAxesMethod = inputType?.GetMethod("ResetInputAxes", BindingFlags.Public | BindingFlags.Static);
                }
                catch { }
            }
            try
            {
                resetInputAxesMethod?.Invoke(null, null);
            }
            catch { }
        }


        public static void OnRemixUIStateChanged(bool open, ManualLogSource logger = null)
        {
            if (instance != null)
            {
                ReleaseCapture();

                if (open)
                {
                    UpdateCursorClipping(false);
                    if (instance.remixWindow != IntPtr.Zero)
                    {
                        SetActiveWindow(instance.remixWindow);
                        SetFocus(instance.remixWindow);
                    }
                    ResetUnityInputAxes();
                }
                else if (instance.gameWindow != IntPtr.Zero)
                {
                    SetForegroundWindow(instance.gameWindow);
                    SetActiveWindow(instance.gameWindow);
                    SetFocus(instance.gameWindow);
                    ResetUnityInputAxes();
                }
                ReleaseCapture();
            }
            if (isEmbeddedStatic)
            {
                RemixGameStateHelper.SetRemixMenuState(open, logger);
            }
            RemixUIOverlay.Instance?.OnRemixUIStateChanged(open);
        }

        public void HandleAltX()
        {
            if (RemixAPI.IsOpenRemix)
            {
                // Unity detects Alt+X on its main thread; apply it on the render thread.
                System.Threading.Interlocked.Exchange(ref nativeSettingsToggleRequested, 1);
                return;
            }
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

                    PostMessage(remixWindow, WM_SYSKEYDOWN, (IntPtr)0x58 /* VK_X */, (IntPtr)0x20000001);
                    PostMessage(remixWindow, WM_SYSKEYUP, (IntPtr)0x58 /* VK_X */, (IntPtr)unchecked((int)0xE0000001));

                    if (gameWindow != IntPtr.Zero)
                    {
                        if (isRemixUIOpen)
                        {
                            SetActiveWindow(remixWindow);
                            SetFocus(remixWindow);
                        }
                        else
                        {
                            SetForegroundWindow(gameWindow);
                            SetActiveWindow(gameWindow);
                            SetFocus(gameWindow);
                        }
                    }

                    UpdateCursorClipping(false);
                    ReleaseCapture();
                }

                if (isEmbedded)
                {
                    RemixGameStateHelper.SetRemixMenuState(isRemixUIOpen, logger);
                }
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

        private const uint WM_SIZE = 0x0005;
        private const uint WM_ACTIVATE = 0x0006;
        private const uint WM_SETFOCUS = 0x0007;
        private const uint WM_KILLFOCUS = 0x0008;
        private const uint WM_CLOSE = 0x0010;
        private const uint WM_ACTIVATEAPP = 0x001C;
        private const uint WM_SETCURSOR = 0x0020;
        private const uint WM_MOUSEMOVE = 0x0200;
        private const uint WM_LBUTTONDOWN = 0x0201;
        private const uint WM_LBUTTONUP = 0x0202;
        private const uint WM_RBUTTONDOWN = 0x0204;
        private const uint WM_RBUTTONUP = 0x0205;
        private const uint WM_MBUTTONDOWN = 0x0207;
        private const uint WM_MBUTTONUP = 0x0208;
        private const uint WM_MOUSEWHEEL = 0x020A;
        private const uint WM_XBUTTONDOWN = 0x020B;
        private const uint WM_XBUTTONUP = 0x020C;
        private const uint WM_MOUSEHWHEEL = 0x020E;

        private const uint WM_KEYDOWN = 0x0100;
        private const uint WM_KEYUP = 0x0101;
        private const uint WM_CHAR = 0x0102;
        private const uint WM_SYSKEYDOWN = 0x0104;
        private const uint WM_SYSKEYUP = 0x0105;
        private const uint WM_UNICHAR = 0x0109;

        private const int VK_MENU = 0x12;
        private const int VK_X = 0x58;

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

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

        private static SubclassProc remixWindowSubclassDelegate;
        private static bool remixWindowSubclassed = false;
        private const uint SUBCLASS_ID_REMIX_WINDOW = 1002;
        private const uint WM_REQUEST_ACTIVATE = 0x8000 + 101; // WM_APP + 101: Thread-safe activation request

        private static IntPtr GameWindowSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, UIntPtr uIdSubclass, UIntPtr dwRefData)
        {
            if (uMsg == WM_REQUEST_ACTIVATE)
            {
                instance?.logger?.LogInfo("[GameWindowSubclassProc] Processing WM_REQUEST_ACTIVATE on Main Thread");
                SetForegroundWindow(hWnd);
                SetActiveWindow(hWnd);
                SetFocus(hWnd);
                return IntPtr.Zero;
            }

            if (isEmbeddedStatic && isRemixUIOpen)
            {
                // When Remix UI is open, forward keyboard input to remixWindow and suppress from Unity
                if (uMsg >= WM_KEYDOWN && uMsg <= WM_UNICHAR)
                {
                    // If Alt+X, let Presenter's GetAsyncKeyState handle the toggle; swallow from Unity
                    bool isAltHeld = (GetAsyncKeyState(VK_MENU) & 0x8000) != 0;
                    bool isXKey = wParam.ToInt32() == VK_X;
                    if (isAltHeld && isXKey)
                    {
                        return IntPtr.Zero;
                    }

                    if (instance != null && instance.remixWindow != IntPtr.Zero)
                    {
                        PostMessage(instance.remixWindow, uMsg, wParam, lParam);
                    }
                    return IntPtr.Zero;
                }

                // Suppress mouse click & wheel messages from reaching Unity to prevent firing/weapon-switching.
                // NOTE: Do NOT re-post WM_MOUSEWHEEL to remixWindow! DefWindowProc in a child window automatically
                // forwards unhandled WM_MOUSEWHEEL to its parent (gameWindow). Re-posting it back creates an
                // infinite recursive loop between the child and parent, causing a stack overflow crash!
                if (uMsg == WM_MOUSEWHEEL)
                {
                    return IntPtr.Zero;
                }

                if (uMsg >= WM_LBUTTONDOWN && uMsg <= WM_MBUTTONUP)
                {
                    if (instance != null && instance.remixWindow != IntPtr.Zero)
                    {
                        SetFocus(instance.remixWindow);
                    }
                    return IntPtr.Zero;
                }

                // If gameWindow receives WM_SETFOCUS while Remix UI is open, transfer focus to remixWindow
                if (uMsg == WM_SETFOCUS)
                {
                    if (instance != null && instance.remixWindow != IntPtr.Zero)
                    {
                        SetFocus(instance.remixWindow);
                        return IntPtr.Zero;
                    }
                }
            }

            if (ShouldHideCursor && (!isEmbeddedStatic || !isRemixUIOpen))
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
                if (isEmbeddedStatic && isRemixUIOpen)
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

            if (uMsg == WM_ACTIVATE)
            {
                int wa = (int)(wParam.ToInt64() & 0xFFFF);
                instance?.logger?.LogInfo($"[GameWindowSubclassProc] WM_ACTIVATE: state={(wa == 0 ? "INACTIVE" : (wa == 1 ? "ACTIVE" : "CLICKACTIVE"))}");
                if (wa == 0)
                {
                    RemixUIOverlay.Instance?.HideOverlayImmediate();
                }
                else
                {
                    RemixUIOverlay.Instance?.RestoreOverlayImmediate();
                }
            }
            else if (uMsg == 0x001C /* WM_ACTIVATEAPP */)
            {
                bool active = (wParam != IntPtr.Zero);
                instance?.logger?.LogInfo($"[GameWindowSubclassProc] WM_ACTIVATEAPP: active={active}");
                if (!active)
                {
                    RemixUIOverlay.Instance?.HideOverlayImmediate();
                }
                else
                {
                    RemixUIOverlay.Instance?.RestoreOverlayImmediate();
                }
            }
            else if (uMsg == WM_SETFOCUS)
            {
                instance?.logger?.LogInfo("[GameWindowSubclassProc] WM_SETFOCUS");
                RemixUIOverlay.Instance?.RestoreOverlayImmediate();
            }
            else if (uMsg == WM_KILLFOCUS)
            {
                instance?.logger?.LogInfo("[GameWindowSubclassProc] WM_KILLFOCUS");
                if (!isRemixUIOpen)
                {
                    RemixUIOverlay.Instance?.HideOverlayImmediate();
                }
            }
            else if (uMsg == WM_SIZE)
            {
                int sizeType = wParam.ToInt32();
                if (sizeType == 1 /* SIZE_MINIMIZED */)
                {
                    instance?.logger?.LogInfo("[GameWindowSubclassProc] WM_SIZE: SIZE_MINIMIZED");
                    RemixUIOverlay.Instance?.HideOverlayImmediate();
                    if (instance != null && instance.remixWindow != IntPtr.Zero)
                    {
                        ShowWindow(instance.remixWindow, SW_HIDE);
                        instance.isRemixWindowVisible = false;
                    }
                }
                else if (sizeType == 0 /* SIZE_RESTORED */ || sizeType == 2 /* SIZE_MAXIMIZED */)
                {
                    instance?.logger?.LogInfo($"[GameWindowSubclassProc] WM_SIZE: {(sizeType == 0 ? "SIZE_RESTORED" : "SIZE_MAXIMIZED")}");
                    RemixUIOverlay.Instance?.RestoreOverlayImmediate();
                    if (instance != null && instance.remixWindow != IntPtr.Zero)
                    {
                        ShowWindow(instance.remixWindow, SW_SHOWNOACTIVATE);
                        instance.isRemixWindowVisible = true;
                    }
                }
            }

            return DefSubclassProc(hWnd, uMsg, wParam, lParam);
        }

        private static IntPtr RemixWindowSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, UIntPtr uIdSubclass, UIntPtr dwRefData)
        {
            if (isEmbeddedStatic && !isRemixUIOpen)
            {
                if (uMsg == WM_NCHITTEST)
                {
                    // Pass hit-testing directly to gameWindow beneath us on the same thread!
                    // Windows automatically delivers all mouse movement, clicks, and wheel events natively to gameWindow.
                    return (IntPtr)HTTRANSPARENT;
                }

                if (uMsg == WM_MOUSEACTIVATE)
                {
                    // Never steal activation from gameWindow
                    return (IntPtr)MA_NOACTIVATE;
                }

                if (uMsg == WM_SETCURSOR)
                {
                    if (ShouldHideCursor)
                    {
                        SetCursor(BlankCursor);
                        return new IntPtr(1);
                    }
                    else
                    {
                        SetCursor(LoadCursorW(IntPtr.Zero, IDC_ARROW));
                        return new IntPtr(1);
                    }
                }
            }

            return DefSubclassProc(hWnd, uMsg, wParam, lParam);
        }

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();
        
        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern IntPtr SetFocus(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern IntPtr GetFocus();

        [DllImport("user32.dll")]
        public static extern IntPtr GetActiveWindow();
        
        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

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
            BepInEx.Configuration.ConfigEntry<string> nativeBackend = null,
            BepInEx.Configuration.ConfigEntry<bool> asyncPresent = null,
            BepInEx.Configuration.ConfigEntry<bool> shadowEarlyOut = null,
            BepInEx.Configuration.ConfigEntry<bool> keepCompressed = null,
            BepInEx.Configuration.ConfigEntry<bool> refreshInPlace = null,
            BepInEx.Configuration.ConfigEntry<int> bounces = null,
            BepInEx.Configuration.ConfigEntry<int> samples = null)
        {
            instance = this;
            this.logger = logger;
            this.configSingleWindow = singleWindow;
            this.configNativeBackend = nativeBackend;
            this.configOpenRemixAsyncPresent = asyncPresent;
            this.configOpenRemixShadowEarlyOut = shadowEarlyOut;
            this.configOpenRemixKeepCompressed = keepCompressed;
            this.configOpenRemixRefreshInPlace = refreshInPlace;
            this.configOpenRemixBounces = bounces;
            this.configOpenRemixSamples = samples;
            if (remixInterface.SetConfigVariable != IntPtr.Zero)
                setConfigVariableFunc = Marshal.GetDelegateForFunctionPointer<RemixAPI.PFN_remixapi_SetConfigVariable>(remixInterface.SetConfigVariable);
            if (RemixAPI.IsOpenRemix)
            {
                getUIStateFunc = Marshal.GetDelegateForFunctionPointer<RemixAPI.PFN_remixapi_GetUIState>(remixInterface.GetUIState);
                setUIStateFunc = Marshal.GetDelegateForFunctionPointer<RemixAPI.PFN_remixapi_SetUIState>(remixInterface.SetUIState);
            }
            
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

        public void EnsureRemixWindowSubclassed()
        {
            if (remixWindow != IntPtr.Zero && isEmbedded && !remixWindowSubclassed)
            {
                remixWindowSubclassDelegate = RemixWindowSubclassProc;
                remixWindowSubclassed = SetWindowSubclass(remixWindow, remixWindowSubclassDelegate, (UIntPtr)SUBCLASS_ID_REMIX_WINDOW, UIntPtr.Zero);
                if (remixWindowSubclassed)
                {
                    logger?.LogInfo($"[RemixWindowManager] Subclassed remixWindow 0x{remixWindow:X} for mouse input routing.");
                }
            }
        }

        private int lastRemixX = -1;
        private int lastRemixY = -1;

        /// <summary>
        /// Synchronizes the embedded child window size and position with the parent game window.
        /// Called from Unity Main Thread. Direct Win32 call since remixWindow was created on Main Thread!
        /// </summary>
        public void SyncWindowBounds()
        {
            EnsureGameWindowSubclassed();
            EnsureRemixWindowSubclassed();

            if (!isEmbedded || remixWindow == IntPtr.Zero || gameWindow == IntPtr.Zero)
                return;

            bool isGameIconic = IsIconic(gameWindow) || !IsWindowVisible(gameWindow);
            bool shouldBeVisible = !isGameIconic;

            if (shouldBeVisible != isRemixWindowVisible)
            {
                ShowWindow(remixWindow, shouldBeVisible ? SW_SHOWNOACTIVATE : SW_HIDE);
                isRemixWindowVisible = shouldBeVisible;
            }

            if (shouldBeVisible && GetClientRect(gameWindow, out RECT rect))
            {
                var pt = new POINT { x = 0, y = 0 };
                ClientToScreen(gameWindow, ref pt);

                if (rect.Width > 0 && rect.Height > 0 &&
                    (rect.Width != windowWidth || rect.Height != windowHeight || pt.x != lastRemixX || pt.y != lastRemixY))
                {
                    windowWidth = rect.Width;
                    windowHeight = rect.Height;
                    lastRemixX = pt.x;
                    lastRemixY = pt.y;
                    SetWindowPos(remixWindow, IntPtr.Zero, pt.x, pt.y, rect.Width, rect.Height, SWP_NOZORDER | SWP_NOACTIVATE | SWP_SHOWWINDOW);
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
                    if (isEmbeddedStatic && isRemixUIOpen)
                    {
                        SetFocus(hWnd);
                    }
                    break;
                case WM_LBUTTONUP:
                case WM_RBUTTONUP:
                case WM_MBUTTONUP:
                    break;

                case WM_MOUSEWHEEL:
                    if (isEmbeddedStatic && isRemixUIOpen)
                    {
                        // Consume mouse wheel on child window so DefWindowProc does not forward to parent
                        return IntPtr.Zero;
                    }
                    break;

                case WM_MOUSEACTIVATE:
                    if (isEmbeddedStatic)
                    {
                        if (isRemixUIOpen)
                        {
                            SetFocus(hWnd);
                            return new IntPtr(MA_ACTIVATE);
                        }
                        return new IntPtr(MA_NOACTIVATE);
                    }
                    break;

                case WM_NCHITTEST:
                    if (isEmbeddedStatic && !isRemixUIOpen)
                    {
                        return (IntPtr)HTTRANSPARENT;
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
        /// Create Remix window on the calling thread (Unity Main Thread)
        /// </summary>
        public bool CreateRemixWindow()
        {
            if (remixWindow != IntPtr.Zero)
                return true;

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
                    logger.LogInfo($"[RemixWindowManager] SingleWindow mode active (Embedded): game window = 0x{gameWindow:X}");
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

            if (isSingleWindow)
            {
                if (GetClientRect(gameWindow, out RECT clientRect) && clientRect.Width > 0 && clientRect.Height > 0)
                {
                    width = clientRect.Width;
                    height = clientRect.Height;
                    windowWidth = width;
                    windowHeight = height;
                }
                var pt = new POINT { x = 0, y = 0 };
                ClientToScreen(gameWindow, ref pt);
                posX = pt.x;
                posY = pt.y;
                lastRemixX = pt.x;
                lastRemixY = pt.y;
                dwStyle = WS_POPUP | WS_VISIBLE | WS_CLIPSIBLINGS;
                dwExStyle = WS_EX_TOOLWINDOW;
                // Setting parentHwnd = gameWindow makes remixWindow an owned popup,
                // guaranteeing it stays permanently positioned in front of gameWindow in Z-order.
                parentHwnd = gameWindow;
                isEmbedded = true;
                isEmbeddedStatic = true;
            }

            // Create window
            string rendererName = RemixAPI.IsOpenRemix ? "openremix" : "RTX Remix";
            string windowTitle = $"{Application.productName} - {rendererName} - {BuildInfo.GitHash}";
            remixWindow = CreateWindowExW(
                dwExStyle,
                WINDOW_CLASS_NAME,
                windowTitle,
                dwStyle,
                posX, posY, width, height,
                parentHwnd, IntPtr.Zero, hInstance, IntPtr.Zero
            );

            // Fallback for Embedded mode if initial CreateWindowExW fails
            if (remixWindow == IntPtr.Zero && isSingleWindow)
            {
                logger.LogWarning("[RemixWindowManager] CreateWindowExW with parent failed; attempting standalone popup fallback...");
                remixWindow = CreateWindowExW(
                    WS_EX_TOOLWINDOW,
                    WINDOW_CLASS_NAME,
                    windowTitle,
                    WS_POPUP | WS_VISIBLE | WS_CLIPSIBLINGS,
                    posX, posY, width, height,
                    IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero
                );
            }
            
            if (remixWindow == IntPtr.Zero)
            {
                int error = Marshal.GetLastWin32Error();
                logger.LogError($"Failed to create Remix window, error: {error}");
                return false;
            }
            
            logger.LogInfo($"Remix window created: 0x{remixWindow:X} (Embedded: {isEmbedded})");
            ShowWindow(remixWindow, SW_SHOW);

            if (isSingleWindow && isEmbedded && gameWindow != IntPtr.Zero)
            {
                PostMessage(gameWindow, WM_REQUEST_ACTIVATE, IntPtr.Zero, IntPtr.Zero);
                EnsureRemixWindowSubclassed();
            }
            
            return true;
        }

        /// <summary>
        /// Initialize Remix API / Startup on render thread
        /// </summary>
        public bool InitializeRemixAPI()
        {
            if (remixWindow == IntPtr.Zero)
            {
                logger.LogWarning("[RemixWindowManager] InitializeRemixAPI called before CreateRemixWindow; creating window fallback...");
                if (!CreateRemixWindow())
                {
                    logger.LogError("[RemixWindowManager] Failed to create remixWindow fallback");
                    return false;
                }
            }

            // Call Remix Startup
            logger.LogInfo("Calling Remix Startup on render thread...");
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
                DestroyRemixWindow();
                return false;
            }
            
            logger.LogInfo("Remix Startup succeeded!");
            EnsureRemixWindowSubclassed();
            if (RemixAPI.IsOpenRemix && setConfigVariableFunc != null)
            {
                string backend = configNativeBackend?.Value ?? "raster";
                bool asyncPresent = configOpenRemixAsyncPresent?.Value ?? true;
                bool shadowEarlyOut = configOpenRemixShadowEarlyOut?.Value ?? true;
                bool keepCompressed = configOpenRemixKeepCompressed?.Value ?? true;
                bool refreshInPlace = configOpenRemixRefreshInPlace?.Value ?? true;
                int bounces = configOpenRemixBounces?.Value ?? 2;
                int samples = configOpenRemixSamples?.Value ?? 1;

                var settings = new (string key, string value)[]
                {
                    ("rtx.native.backend", backend),
                    ("rtx.native.vsync", "false"),
                    ("rtx.native.asyncPresent", asyncPresent ? "true" : "false"),
                    ("rtx.native.pathtrace.shadowEarlyOut", shadowEarlyOut ? "true" : "false"),
                    ("rtx.native.textures.keepCompressed", keepCompressed ? "true" : "false"),
                    ("rtx.native.meshes.refreshInPlace", refreshInPlace ? "true" : "false"),
                    ("rtx.native.pathtrace.samples", samples.ToString()),
                    ("rtx.native.pathtrace.bounces", bounces.ToString()),
                    ("rtx.native.pathtrace.accumulate", "false"),
                    ("rtx.native.pathtrace.taa", "true"),
                    ("rtx.native.pathtrace.denoiser", "reblur-sh"),
                    ("rtx.native.pathtrace.sharc", "false"),
                    ("rtx.native.pathtrace.sigma", "false"),
                    ("rtx.native.gpuPassTimers", "true")
                };

                foreach (var (key, val) in settings)
                {
                    var res = setConfigVariableFunc(key, val);
                    logger.LogInfo($"[OpenRemix Config] {key} = {val} -> {res}");
                }
                logger.LogInfo("openremix settings: press F12 in the game or openremix window; choose Backend > Path trace.");
            }
            return true;
        }

        /// <summary>
        /// Pump Windows messages to keep window responsive (called from Render Thread)
        /// </summary>
        public void PumpWindowsMessages()
        {
            // SDL event pumping belongs to the same thread as Startup/Present.
            RemixWatchdog.BeatRender("PumpMessages.SDLPumpEvents");
            RemixAPI.NativePumpEvents?.Invoke();

            if (RemixAPI.IsOpenRemix)
            {
                RemixWatchdog.BeatRender("PumpMessages.NativeSettings");
                UpdateNativeSettings();
            }
            RemixWatchdog.BeatRender("PumpMessages.End");
        }

        /// <summary>
        /// Pump Windows messages for remixWindow on Unity Main Thread
        /// </summary>
        public static void PumpMainThreadMessages()
        {
            if (instance != null && instance.remixWindow != IntPtr.Zero)
            {
                MSG msg;
                while (PeekMessageW(out msg, instance.remixWindow, 0, 0, PM_REMOVE))
                {
                    TranslateMessage(ref msg);
                    DispatchMessageW(ref msg);
                }
            }
        }

        private void UpdateNativeSettings()
        {
            // Poll on the render thread so either window can receive F12, even
            // when Unity is unfocused. Use the held bit, not the shared press bit.
            bool keyDown = (GetAsyncKeyState(0x7B /* VK_F12 */) & 0x8000) != 0;
            bool pressed = keyDown && !settingsKeyWasDown;
            settingsKeyWasDown = keyDown;
            GetWindowThreadProcessId(GetForegroundWindow(), out uint foregroundProcess);
            bool toggle = System.Threading.Interlocked.Exchange(ref nativeSettingsToggleRequested, 0) != 0;
            toggle |= pressed && foregroundProcess == processId;

            var state = getUIStateFunc();
            if (toggle)
            {
                var next = state == RemixAPI.remixapi_UIState.REMIXAPI_UI_STATE_NONE
                    ? RemixAPI.remixapi_UIState.REMIXAPI_UI_STATE_ADVANCED
                    : RemixAPI.remixapi_UIState.REMIXAPI_UI_STATE_NONE;
                var result = setUIStateFunc(next);
                if (result == RemixAPI.remixapi_ErrorCode.REMIXAPI_ERROR_CODE_SUCCESS)
                {
                    state = next;
                    logger.LogInfo($"openremix settings {(state == RemixAPI.remixapi_UIState.REMIXAPI_UI_STATE_NONE ? "closed" : "opened")}");
                    if (state != RemixAPI.remixapi_UIState.REMIXAPI_UI_STATE_NONE)
                    {
                        SetForegroundWindow(remixWindow);
                        SetFocus(remixWindow);
                    }
                    else if (gameWindow != IntPtr.Zero)
                    {
                        PostMessage(gameWindow, WM_REQUEST_ACTIVATE, IntPtr.Zero, IntPtr.Zero);
                    }
                }
                else
                    logger.LogWarning($"Could not toggle openremix settings: {result}");
            }
            // Reflect the panel's open/close state. Do not call OnRemixUIStateChanged here on the Render Thread!
            // Unity Main Thread polls SyncUIStateWithRemix in Presenter.Update each frame where Unity GameObject/Camera APIs are safe.
            bool newOpen = (state != RemixAPI.remixapi_UIState.REMIXAPI_UI_STATE_NONE);
            if (isRemixUIOpen != newOpen)
            {
                isRemixUIOpen = newOpen;
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
            isRemixUIOpen = false;
            lastSyncedRemixOpen = false;
            UpdateCursorVisibility(false);
            EnforceCursorVisible();
            if (remixWindow != IntPtr.Zero)
            {
                IntPtr wnd = remixWindow;
                remixWindow = IntPtr.Zero;
                if (remixWindowSubclassed)
                {
                    RemoveWindowSubclass(wnd, remixWindowSubclassDelegate, (UIntPtr)SUBCLASS_ID_REMIX_WINDOW);
                    remixWindowSubclassed = false;
                }
                uint windowThread = GetWindowThreadProcessId(wnd, out _);
                if (GetCurrentThreadId() == windowThread)
                {
                    DestroyWindow(wnd);
                }
                else
                {
                    PostMessage(wnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                }
                logger.LogInfo("Remix window destroyed/closed");
            }
        }
    }
}
