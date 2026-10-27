using System;
using System.Runtime.InteropServices;
using BepInEx.Logging;

namespace UnityRemix
{
    /// <summary>
    /// P/Invoke bindings for the ImGui exports from remix.dll.
    /// All functions are resolved via GetProcAddress at initialization time.
    /// Call <see cref="Initialize"/> after RemixAPI has loaded the DLL.
    /// </summary>
    public static class RemixImGui
    {
        private static bool _initialized;
        private static ManualLogSource _log;

        // Callback delegate — must match PFN_remixapi_imgui_DrawCallback on native side
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate void DrawCallback(IntPtr userData);

        // Keep a reference so the GC doesn't collect the delegate while native holds the pointer
        private static DrawCallback _registeredCallback;

        #region Delegate Types

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void D_RegisterDrawCallback(IntPtr callback, IntPtr userData);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void D_Void();
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int D_Begin([MarshalAs(UnmanagedType.LPStr)] string name, IntPtr pOpen, int flags);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int D_BeginChild([MarshalAs(UnmanagedType.LPStr)] string strId, float w, float h, int border, int flags);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void D_SetNextWindowPos(float x, float y, int cond);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void D_SetNextWindowSize(float w, float h, int cond);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void D_SetNextWindowCollapsed(int collapsed, int cond);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void D_GetWindowVec2(out float x, out float y);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void D_SameLine(float offsetFromStartX, float spacing);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void D_Dummy(float w, float h);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void D_Indent(float indentW);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void D_GetContentRegionAvail(out float w, out float h);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void D_PushID_Str([MarshalAs(UnmanagedType.LPStr)] string strId);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void D_PushID_Int(int intId);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void D_Text([MarshalAs(UnmanagedType.LPStr)] string text);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void D_TextColored(float r, float g, float b, float a, [MarshalAs(UnmanagedType.LPStr)] string text);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void D_LabelText([MarshalAs(UnmanagedType.LPStr)] string label, [MarshalAs(UnmanagedType.LPStr)] string text);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int D_Button([MarshalAs(UnmanagedType.LPStr)] string label, float w, float h);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int D_SmallButton([MarshalAs(UnmanagedType.LPStr)] string label);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int D_Checkbox([MarshalAs(UnmanagedType.LPStr)] string label, ref int v);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int D_RadioButton([MarshalAs(UnmanagedType.LPStr)] string label, int active);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int D_SliderFloat([MarshalAs(UnmanagedType.LPStr)] string label, ref float v, float vMin, float vMax, [MarshalAs(UnmanagedType.LPStr)] string format);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int D_SliderInt([MarshalAs(UnmanagedType.LPStr)] string label, ref int v, int vMin, int vMax, [MarshalAs(UnmanagedType.LPStr)] string format);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int D_SliderFloatN([MarshalAs(UnmanagedType.LPStr)] string label, float[] v, float vMin, float vMax, [MarshalAs(UnmanagedType.LPStr)] string format);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int D_DragFloat([MarshalAs(UnmanagedType.LPStr)] string label, ref float v, float vSpeed, float vMin, float vMax, [MarshalAs(UnmanagedType.LPStr)] string format);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int D_DragInt([MarshalAs(UnmanagedType.LPStr)] string label, ref int v, float vSpeed, int vMin, int vMax, [MarshalAs(UnmanagedType.LPStr)] string format);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int D_InputFloat([MarshalAs(UnmanagedType.LPStr)] string label, ref float v, float step, float stepFast, [MarshalAs(UnmanagedType.LPStr)] string format);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int D_InputInt([MarshalAs(UnmanagedType.LPStr)] string label, ref int v, int step, int stepFast);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int D_InputText([MarshalAs(UnmanagedType.LPStr)] string label, IntPtr buf, uint bufSize, int flags);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int D_ColorEdit3([MarshalAs(UnmanagedType.LPStr)] string label, float[] col);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int D_ColorEdit4([MarshalAs(UnmanagedType.LPStr)] string label, float[] col, int flags);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int D_ColorPicker3([MarshalAs(UnmanagedType.LPStr)] string label, float[] col);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int D_BeginCombo([MarshalAs(UnmanagedType.LPStr)] string label, [MarshalAs(UnmanagedType.LPStr)] string previewValue, int flags);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int D_Selectable([MarshalAs(UnmanagedType.LPStr)] string label, int selected, int flags, float w, float h);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int D_CollapsingHeader([MarshalAs(UnmanagedType.LPStr)] string label, int flags);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int D_TreeNode([MarshalAs(UnmanagedType.LPStr)] string label);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int D_BeginTabBar([MarshalAs(UnmanagedType.LPStr)] string strId, int flags);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int D_BeginTabItem([MarshalAs(UnmanagedType.LPStr)] string label, IntPtr pOpen, int flags);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int D_BeginTable([MarshalAs(UnmanagedType.LPStr)] string strId, int column, int flags, float outerW, float outerH, float innerWidth);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void D_TableNextRow(int rowFlags, float minRowHeight);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int D_TableSetColumnIndex(int columnN);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void D_TableSetupColumn([MarshalAs(UnmanagedType.LPStr)] string label, int flags, float initWidthOrWeight, uint userId);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void D_SetTooltip([MarshalAs(UnmanagedType.LPStr)] string text);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void D_ProgressBar(float fraction, float w, float h, [MarshalAs(UnmanagedType.LPStr)] string overlay);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int D_IsItemHovered(int flags);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int D_IsItemClicked(int mouseButton);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void D_PushStyleColor(int idx, float r, float g, float b, float a);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void D_PopStyleColor(int count);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void D_PushStyleVar_Float(int idx, float val);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void D_PushStyleVar_Vec2(int idx, float x, float y);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void D_PopStyleVar(int count);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int D_PlotBeginPlot([MarshalAs(UnmanagedType.LPStr)] string titleId, float w, float h, int flags);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void D_PlotPlotLine([MarshalAs(UnmanagedType.LPStr)] string labelId, float[] values, int count);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void D_PlotPlotBars([MarshalAs(UnmanagedType.LPStr)] string labelId, float[] values, int count, double barSize);

        // DrawList delegates
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void D_DrawList_AddLine(float x1, float y1, float x2, float y2, uint col, float thickness);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void D_DrawList_AddRect(float x1, float y1, float x2, float y2, uint col, float rounding, float thickness);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void D_DrawList_AddRectFilled(float x1, float y1, float x2, float y2, uint col, float rounding);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void D_DrawList_AddText(float x, float y, uint col, [MarshalAs(UnmanagedType.LPStr)] string text);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void D_GetDisplaySize(out float w, out float h);

        #endregion

        #region Resolved Function Pointers

        private static D_RegisterDrawCallback _registerDrawCallback;
        private static D_Void _unregisterDrawCallback;
        private static D_RegisterDrawCallback _registerOverlayCallback;
        private static D_Void _unregisterOverlayCallback;
        private static D_Begin _begin;
        private static D_Void _end;
        private static D_BeginChild _beginChild;
        private static D_Void _endChild;
        private static D_SetNextWindowPos _setNextWindowPos;
        private static D_SetNextWindowSize _setNextWindowSize;
        private static D_SetNextWindowCollapsed _setNextWindowCollapsed;
        private static D_Void _setNextWindowFocus;
        private static D_GetWindowVec2 _getWindowSize;
        private static D_GetWindowVec2 _getWindowPos;
        private static D_SameLine _sameLine;
        private static D_Void _separator;
        private static D_Void _spacing;
        private static D_Dummy _dummy;
        private static D_Indent _indent;
        private static D_Indent _unindent;
        private static D_Void _newLine;
        private static D_Void _beginGroup;
        private static D_Void _endGroup;
        private static D_GetContentRegionAvail _getContentRegionAvail;
        private static D_PushID_Str _pushID_Str;
        private static D_PushID_Int _pushID_Int;
        private static D_Void _popID;
        private static D_Text _text;
        private static D_TextColored _textColored;
        private static D_Text _textWrapped;
        private static D_Text _bulletText;
        private static D_LabelText _labelText;
        private static D_Button _button;
        private static D_SmallButton _smallButton;
        private static D_Checkbox _checkbox;
        private static D_RadioButton _radioButton;
        private static D_SliderFloat _sliderFloat;
        private static D_SliderInt _sliderInt;
        private static D_SliderFloatN _sliderFloat2;
        private static D_SliderFloatN _sliderFloat3;
        private static D_DragFloat _dragFloat;
        private static D_DragInt _dragInt;
        private static D_InputFloat _inputFloat;
        private static D_InputInt _inputInt;
        private static D_InputText _inputText;
        private static D_ColorEdit3 _colorEdit3;
        private static D_ColorEdit4 _colorEdit4;
        private static D_ColorPicker3 _colorPicker3;
        private static D_BeginCombo _beginCombo;
        private static D_Void _endCombo;
        private static D_Selectable _selectable;
        private static D_CollapsingHeader _collapsingHeader;
        private static D_TreeNode _treeNode;
        private static D_Void _treePop;
        private static D_BeginTabBar _beginTabBar;
        private static D_Void _endTabBar;
        private static D_BeginTabItem _beginTabItem;
        private static D_Void _endTabItem;
        private static D_BeginTable _beginTable;
        private static D_Void _endTable;
        private static D_TableNextRow _tableNextRow;
        private static D_TableSetColumnIndex _tableSetColumnIndex;
        private static D_TableSetupColumn _tableSetupColumn;
        private static D_Void _tableHeadersRow;
        private static D_Void _beginTooltip;
        private static D_Void _endTooltip;
        private static D_SetTooltip _setTooltip;
        private static D_ProgressBar _progressBar;
        private static D_IsItemHovered _isItemHovered;
        private static D_IsItemClicked _isItemClicked;
        private static D_Void _setItemDefaultFocus;
        private static D_PushStyleColor _pushStyleColor;
        private static D_PopStyleColor _popStyleColor;
        private static D_PushStyleVar_Float _pushStyleVar_Float;
        private static D_PushStyleVar_Vec2 _pushStyleVar_Vec2;
        private static D_PopStyleVar _popStyleVar;
        private static D_PlotBeginPlot _plotBeginPlot;
        private static D_Void _plotEndPlot;
        private static D_PlotPlotLine _plotPlotLine;
        private static D_PlotPlotBars _plotPlotBars;

        // DrawList
        private static D_DrawList_AddLine _drawListAddLine;
        private static D_DrawList_AddRect _drawListAddRect;
        private static D_DrawList_AddRectFilled _drawListAddRectFilled;
        private static D_DrawList_AddText _drawListAddText;
        private static D_GetDisplaySize _getDisplaySize;

        // TableNextColumn returns int — need separate delegate
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int D_TableNextColumn_Ret();
        private static D_TableNextColumn_Ret _tableNextColumnRet;

        #endregion

        /// <summary>
        /// Resolve all ImGui exports from the loaded Remix DLL. Returns true if the core
        /// functions are available (the DLL has the exports). Non-critical functions that
        /// fail to resolve are silently skipped.
        /// </summary>
        public static bool Initialize(ManualLogSource log)
        {
            if (_initialized) return true;
            _log = log;

            IntPtr dll = RemixAPI.RemixDllHandle;
            if (dll == IntPtr.Zero)
            {
                log.LogWarning("[RemixImGui] Remix DLL not loaded — cannot resolve ImGui exports");
                return false;
            }

            // Helper: resolve or null
            T Resolve<T>(string name) where T : class
            {
                IntPtr ptr = RemixAPI.GetRemixProcAddress(name);
                if (ptr == IntPtr.Zero) return null;
                return Marshal.GetDelegateForFunctionPointer(ptr, typeof(T)) as T;
            }

            // Core — must resolve
            _registerDrawCallback = Resolve<D_RegisterDrawCallback>("remixapi_imgui_RegisterDrawCallback");
            _unregisterDrawCallback = Resolve<D_Void>("remixapi_imgui_UnregisterDrawCallback");
            _registerOverlayCallback = Resolve<D_RegisterDrawCallback>("remixapi_imgui_RegisterOverlayCallback");
            _unregisterOverlayCallback = Resolve<D_Void>("remixapi_imgui_UnregisterOverlayCallback");
            _begin = Resolve<D_Begin>("remixapi_imgui_Begin");
            _end = Resolve<D_Void>("remixapi_imgui_End");

            if (_registerDrawCallback == null || _begin == null || _end == null)
            {
                log.LogWarning("[RemixImGui] Core ImGui exports not found — is this a compatible Remix build?");
                return false;
            }

            // Non-critical — best effort
            _beginChild = Resolve<D_BeginChild>("remixapi_imgui_BeginChild");
            _endChild = Resolve<D_Void>("remixapi_imgui_EndChild");
            _setNextWindowPos = Resolve<D_SetNextWindowPos>("remixapi_imgui_SetNextWindowPos");
            _setNextWindowSize = Resolve<D_SetNextWindowSize>("remixapi_imgui_SetNextWindowSize");
            _setNextWindowCollapsed = Resolve<D_SetNextWindowCollapsed>("remixapi_imgui_SetNextWindowCollapsed");
            _setNextWindowFocus = Resolve<D_Void>("remixapi_imgui_SetNextWindowFocus");
            _getWindowSize = Resolve<D_GetWindowVec2>("remixapi_imgui_GetWindowSize");
            _getWindowPos = Resolve<D_GetWindowVec2>("remixapi_imgui_GetWindowPos");
            _sameLine = Resolve<D_SameLine>("remixapi_imgui_SameLine");
            _separator = Resolve<D_Void>("remixapi_imgui_Separator");
            _spacing = Resolve<D_Void>("remixapi_imgui_Spacing");
            _dummy = Resolve<D_Dummy>("remixapi_imgui_Dummy");
            _indent = Resolve<D_Indent>("remixapi_imgui_Indent");
            _unindent = Resolve<D_Indent>("remixapi_imgui_Unindent");
            _newLine = Resolve<D_Void>("remixapi_imgui_NewLine");
            _beginGroup = Resolve<D_Void>("remixapi_imgui_BeginGroup");
            _endGroup = Resolve<D_Void>("remixapi_imgui_EndGroup");
            _getContentRegionAvail = Resolve<D_GetContentRegionAvail>("remixapi_imgui_GetContentRegionAvail");
            _pushID_Str = Resolve<D_PushID_Str>("remixapi_imgui_PushID_Str");
            _pushID_Int = Resolve<D_PushID_Int>("remixapi_imgui_PushID_Int");
            _popID = Resolve<D_Void>("remixapi_imgui_PopID");
            _text = Resolve<D_Text>("remixapi_imgui_Text");
            _textColored = Resolve<D_TextColored>("remixapi_imgui_TextColored");
            _textWrapped = Resolve<D_Text>("remixapi_imgui_TextWrapped");
            _bulletText = Resolve<D_Text>("remixapi_imgui_BulletText");
            _labelText = Resolve<D_LabelText>("remixapi_imgui_LabelText");
            _button = Resolve<D_Button>("remixapi_imgui_Button");
            _smallButton = Resolve<D_SmallButton>("remixapi_imgui_SmallButton");
            _checkbox = Resolve<D_Checkbox>("remixapi_imgui_Checkbox");
            _radioButton = Resolve<D_RadioButton>("remixapi_imgui_RadioButton");
            _sliderFloat = Resolve<D_SliderFloat>("remixapi_imgui_SliderFloat");
            _sliderInt = Resolve<D_SliderInt>("remixapi_imgui_SliderInt");
            _sliderFloat2 = Resolve<D_SliderFloatN>("remixapi_imgui_SliderFloat2");
            _sliderFloat3 = Resolve<D_SliderFloatN>("remixapi_imgui_SliderFloat3");
            _dragFloat = Resolve<D_DragFloat>("remixapi_imgui_DragFloat");
            _dragInt = Resolve<D_DragInt>("remixapi_imgui_DragInt");
            _inputFloat = Resolve<D_InputFloat>("remixapi_imgui_InputFloat");
            _inputInt = Resolve<D_InputInt>("remixapi_imgui_InputInt");
            _inputText = Resolve<D_InputText>("remixapi_imgui_InputText");
            _colorEdit3 = Resolve<D_ColorEdit3>("remixapi_imgui_ColorEdit3");
            _colorEdit4 = Resolve<D_ColorEdit4>("remixapi_imgui_ColorEdit4");
            _colorPicker3 = Resolve<D_ColorPicker3>("remixapi_imgui_ColorPicker3");
            _beginCombo = Resolve<D_BeginCombo>("remixapi_imgui_BeginCombo");
            _endCombo = Resolve<D_Void>("remixapi_imgui_EndCombo");
            _selectable = Resolve<D_Selectable>("remixapi_imgui_Selectable");
            _collapsingHeader = Resolve<D_CollapsingHeader>("remixapi_imgui_CollapsingHeader");
            _treeNode = Resolve<D_TreeNode>("remixapi_imgui_TreeNode");
            _treePop = Resolve<D_Void>("remixapi_imgui_TreePop");
            _beginTabBar = Resolve<D_BeginTabBar>("remixapi_imgui_BeginTabBar");
            _endTabBar = Resolve<D_Void>("remixapi_imgui_EndTabBar");
            _beginTabItem = Resolve<D_BeginTabItem>("remixapi_imgui_BeginTabItem");
            _endTabItem = Resolve<D_Void>("remixapi_imgui_EndTabItem");
            _beginTable = Resolve<D_BeginTable>("remixapi_imgui_BeginTable");
            _endTable = Resolve<D_Void>("remixapi_imgui_EndTable");
            _tableNextRow = Resolve<D_TableNextRow>("remixapi_imgui_TableNextRow");
            _tableNextColumnRet = Resolve<D_TableNextColumn_Ret>("remixapi_imgui_TableNextColumn");
            _tableSetColumnIndex = Resolve<D_TableSetColumnIndex>("remixapi_imgui_TableSetColumnIndex");
            _tableSetupColumn = Resolve<D_TableSetupColumn>("remixapi_imgui_TableSetupColumn");
            _tableHeadersRow = Resolve<D_Void>("remixapi_imgui_TableHeadersRow");
            _beginTooltip = Resolve<D_Void>("remixapi_imgui_BeginTooltip");
            _endTooltip = Resolve<D_Void>("remixapi_imgui_EndTooltip");
            _setTooltip = Resolve<D_SetTooltip>("remixapi_imgui_SetTooltip");
            _progressBar = Resolve<D_ProgressBar>("remixapi_imgui_ProgressBar");
            _isItemHovered = Resolve<D_IsItemHovered>("remixapi_imgui_IsItemHovered");
            _isItemClicked = Resolve<D_IsItemClicked>("remixapi_imgui_IsItemClicked");
            _setItemDefaultFocus = Resolve<D_Void>("remixapi_imgui_SetItemDefaultFocus");
            _pushStyleColor = Resolve<D_PushStyleColor>("remixapi_imgui_PushStyleColor");
            _popStyleColor = Resolve<D_PopStyleColor>("remixapi_imgui_PopStyleColor");
            _pushStyleVar_Float = Resolve<D_PushStyleVar_Float>("remixapi_imgui_PushStyleVar_Float");
            _pushStyleVar_Vec2 = Resolve<D_PushStyleVar_Vec2>("remixapi_imgui_PushStyleVar_Vec2");
            _popStyleVar = Resolve<D_PopStyleVar>("remixapi_imgui_PopStyleVar");
            _plotBeginPlot = Resolve<D_PlotBeginPlot>("remixapi_imgui_PlotBeginPlot");
            _plotEndPlot = Resolve<D_Void>("remixapi_imgui_PlotEndPlot");
            _plotPlotLine = Resolve<D_PlotPlotLine>("remixapi_imgui_PlotPlotLine");
            _plotPlotBars = Resolve<D_PlotPlotBars>("remixapi_imgui_PlotPlotBars");

            // DrawList (non-critical — graceful fallback if old remix.dll)
            _drawListAddLine = Resolve<D_DrawList_AddLine>("remixapi_imgui_DrawList_AddLine");
            _drawListAddRect = Resolve<D_DrawList_AddRect>("remixapi_imgui_DrawList_AddRect");
            _drawListAddRectFilled = Resolve<D_DrawList_AddRectFilled>("remixapi_imgui_DrawList_AddRectFilled");
            _drawListAddText = Resolve<D_DrawList_AddText>("remixapi_imgui_DrawList_AddText");
            _getDisplaySize = Resolve<D_GetDisplaySize>("remixapi_imgui_GetDisplaySize");

            _initialized = true;
            log.LogInfo("[RemixImGui] Initialized — all core exports resolved");
            if (HasDrawListSupport)
                log.LogInfo("[RemixImGui] DrawList exports resolved — 3D debug overlay available");
            else
                log.LogWarning("[RemixImGui] DrawList exports NOT found — 3D debug boxes disabled (old remix.dll?)");
            return true;
        }

        /// <summary>
        /// Register a managed callback that will be invoked each frame inside ImGui's
        /// NewFrame/Render pair. Only one callback can be active at a time.
        /// </summary>
        public static void RegisterDrawCallback(DrawCallback callback)
        {
            if (!_initialized || _registerDrawCallback == null) return;
            _registeredCallback = callback;
            IntPtr fnPtr = Marshal.GetFunctionPointerForDelegate(callback);
            _registerDrawCallback(fnPtr, IntPtr.Zero);
        }

        public static void UnregisterDrawCallback()
        {
            if (!_initialized || _unregisterDrawCallback == null) return;
            _unregisterDrawCallback();
            _registeredCallback = null;
        }

        /// <summary>
        /// Register an overlay callback invoked every frame regardless of developer menu state.
        /// Returns false if the Remix build does not support overlay callbacks.
        /// </summary>
        public static bool RegisterOverlayCallback(DrawCallback callback)
        {
            if (!_initialized || _registerOverlayCallback == null) return false;
            _registeredOverlayCallback = callback;
            IntPtr fnPtr = Marshal.GetFunctionPointerForDelegate(callback);
            _registerOverlayCallback(fnPtr, IntPtr.Zero);
            return true;
        }

        public static void UnregisterOverlayCallback()
        {
            if (!_initialized || _unregisterOverlayCallback == null) return;
            _unregisterOverlayCallback();
            _registeredOverlayCallback = null;
        }

        public static bool HasOverlaySupport => _registerOverlayCallback != null;

        // prevent GC of overlay delegate
        private static DrawCallback _registeredOverlayCallback;

        #region Public API — Windows

        public static bool Begin(string name, ref bool open, int flags = 0)
        {
            if (_begin == null) return false;
            int o = open ? 1 : 0;
            unsafe
            {
                int* pOpen = &o;
                int result = _begin(name, (IntPtr)pOpen, flags);
                open = o != 0;
                return result != 0;
            }
        }

        public static bool Begin(string name, int flags = 0)
        {
            if (_begin == null) return false;
            return _begin(name, IntPtr.Zero, flags) != 0;
        }

        public static void End() => _end?.Invoke();

        public static bool BeginChild(string strId, float w = 0, float h = 0, bool border = false, int flags = 0)
        {
            if (_beginChild == null) return false;
            return _beginChild(strId, w, h, border ? 1 : 0, flags) != 0;
        }

        public static void EndChild() => _endChild?.Invoke();

        public static void SetNextWindowPos(float x, float y, int cond = 0) => _setNextWindowPos?.Invoke(x, y, cond);
        public static void SetNextWindowSize(float w, float h, int cond = 0) => _setNextWindowSize?.Invoke(w, h, cond);
        public static void SetNextWindowCollapsed(bool collapsed, int cond = 0) => _setNextWindowCollapsed?.Invoke(collapsed ? 1 : 0, cond);
        public static void SetNextWindowFocus() => _setNextWindowFocus?.Invoke();

        public static void GetWindowSize(out float w, out float h) { w = 0; h = 0; _getWindowSize?.Invoke(out w, out h); }
        public static void GetWindowPos(out float x, out float y) { x = 0; y = 0; _getWindowPos?.Invoke(out x, out y); }

        #endregion

        #region Public API — Layout

        public static void SameLine(float offsetFromStartX = 0, float spacing = -1) => _sameLine?.Invoke(offsetFromStartX, spacing);
        public static void Separator() => _separator?.Invoke();
        public static void Spacing() => _spacing?.Invoke();
        public static void Dummy(float w, float h) => _dummy?.Invoke(w, h);
        public static void Indent(float indentW = 0) => _indent?.Invoke(indentW);
        public static void Unindent(float indentW = 0) => _unindent?.Invoke(indentW);
        public static void NewLine() => _newLine?.Invoke();
        public static void BeginGroup() => _beginGroup?.Invoke();
        public static void EndGroup() => _endGroup?.Invoke();
        public static void GetContentRegionAvail(out float w, out float h) { w = 0; h = 0; _getContentRegionAvail?.Invoke(out w, out h); }

        #endregion

        #region Public API — ID Stack

        public static void PushID(string strId) => _pushID_Str?.Invoke(strId);
        public static void PushID(int intId) => _pushID_Int?.Invoke(intId);
        public static void PopID() => _popID?.Invoke();

        #endregion

        #region Public API — Text

        public static void Text(string text) => _text?.Invoke(text);
        public static void TextColored(float r, float g, float b, float a, string text) => _textColored?.Invoke(r, g, b, a, text);
        public static void TextWrapped(string text) => _textWrapped?.Invoke(text);
        public static void BulletText(string text) => _bulletText?.Invoke(text);
        public static void LabelText(string label, string text) => _labelText?.Invoke(label, text);

        #endregion

        #region Public API — Controls

        public static bool Button(string label, float w = 0, float h = 0) => _button?.Invoke(label, w, h) != 0;
        public static bool SmallButton(string label) => _smallButton?.Invoke(label) != 0;

        public static bool Checkbox(string label, ref bool v)
        {
            if (_checkbox == null) return false;
            int iv = v ? 1 : 0;
            int result = _checkbox(label, ref iv);
            v = iv != 0;
            return result != 0;
        }

        public static bool RadioButton(string label, bool active) => _radioButton?.Invoke(label, active ? 1 : 0) != 0;
        public static bool SliderFloat(string label, ref float v, float min, float max, string format = null) => _sliderFloat?.Invoke(label, ref v, min, max, format) != 0;
        public static bool SliderInt(string label, ref int v, int min, int max, string format = null) => _sliderInt?.Invoke(label, ref v, min, max, format) != 0;
        public static bool SliderFloat2(string label, float[] v, float min, float max, string format = null) => _sliderFloat2?.Invoke(label, v, min, max, format) != 0;
        public static bool SliderFloat3(string label, float[] v, float min, float max, string format = null) => _sliderFloat3?.Invoke(label, v, min, max, format) != 0;
        public static bool DragFloat(string label, ref float v, float speed = 1, float min = 0, float max = 0, string format = null) => _dragFloat?.Invoke(label, ref v, speed, min, max, format) != 0;
        public static bool DragInt(string label, ref int v, float speed = 1, int min = 0, int max = 0, string format = null) => _dragInt?.Invoke(label, ref v, speed, min, max, format) != 0;
        public static bool InputFloat(string label, ref float v, float step = 0, float stepFast = 0, string format = null) => _inputFloat?.Invoke(label, ref v, step, stepFast, format) != 0;
        public static bool InputInt(string label, ref int v, int step = 1, int stepFast = 100) => _inputInt?.Invoke(label, ref v, step, stepFast) != 0;
        public static bool ColorEdit3(string label, float[] col) => _colorEdit3?.Invoke(label, col) != 0;
        public static bool ColorEdit4(string label, float[] col, int flags = 0) => _colorEdit4?.Invoke(label, col, flags) != 0;
        public static bool ColorPicker3(string label, float[] col) => _colorPicker3?.Invoke(label, col) != 0;

        public static bool InputText(string label, byte[] buf, int flags = 0)
        {
            if (_inputText == null) return false;
            var pin = System.Runtime.InteropServices.GCHandle.Alloc(buf, System.Runtime.InteropServices.GCHandleType.Pinned);
            try
            {
                return _inputText(label, pin.AddrOfPinnedObject(), (uint)buf.Length, flags) != 0;
            }
            finally { pin.Free(); }
        }

        #endregion

        #region Public API — Combo

        public static bool BeginCombo(string label, string previewValue, int flags = 0) => _beginCombo?.Invoke(label, previewValue, flags) != 0;
        public static void EndCombo() => _endCombo?.Invoke();
        public static bool Selectable(string label, bool selected = false, int flags = 0, float w = 0, float h = 0) => _selectable?.Invoke(label, selected ? 1 : 0, flags, w, h) != 0;

        #endregion

        #region Public API — Trees

        public static bool CollapsingHeader(string label, int flags = 0) => _collapsingHeader?.Invoke(label, flags) != 0;
        public static bool TreeNode(string label) => _treeNode?.Invoke(label) != 0;
        public static void TreePop() => _treePop?.Invoke();

        #endregion

        #region Public API — Tabs

        public static bool BeginTabBar(string strId, int flags = 0) => _beginTabBar?.Invoke(strId, flags) != 0;
        public static void EndTabBar() => _endTabBar?.Invoke();
        public static bool BeginTabItem(string label, int flags = 0) => _beginTabItem?.Invoke(label, IntPtr.Zero, flags) != 0;
        public static void EndTabItem() => _endTabItem?.Invoke();

        #endregion

        #region Public API — Tables

        public static bool BeginTable(string strId, int columns, int flags = 0, float outerW = 0, float outerH = 0, float innerWidth = 0)
            => _beginTable?.Invoke(strId, columns, flags, outerW, outerH, innerWidth) != 0;
        public static void EndTable() => _endTable?.Invoke();
        public static void TableNextRow(int rowFlags = 0, float minRowHeight = 0) => _tableNextRow?.Invoke(rowFlags, minRowHeight);
        public static bool TableNextColumn() => _tableNextColumnRet?.Invoke() != 0;
        public static bool TableSetColumnIndex(int columnN) => _tableSetColumnIndex?.Invoke(columnN) != 0;
        public static void TableSetupColumn(string label, int flags = 0, float initWidthOrWeight = 0, uint userId = 0) => _tableSetupColumn?.Invoke(label, flags, initWidthOrWeight, userId);
        public static void TableHeadersRow() => _tableHeadersRow?.Invoke();

        #endregion

        #region Public API — Tooltips

        public static void BeginTooltip() => _beginTooltip?.Invoke();
        public static void EndTooltip() => _endTooltip?.Invoke();
        public static void SetTooltip(string text) => _setTooltip?.Invoke(text);

        #endregion

        #region Public API — Misc

        public static void ProgressBar(float fraction, float w = -1, float h = 0, string overlay = null) => _progressBar?.Invoke(fraction, w, h, overlay);
        public static bool IsItemHovered(int flags = 0) => _isItemHovered?.Invoke(flags) != 0;
        public static bool IsItemClicked(int mouseButton = 0) => _isItemClicked?.Invoke(mouseButton) != 0;
        public static void SetItemDefaultFocus() => _setItemDefaultFocus?.Invoke();

        #endregion

        #region Public API — Style

        public static void PushStyleColor(int idx, float r, float g, float b, float a) => _pushStyleColor?.Invoke(idx, r, g, b, a);
        public static void PopStyleColor(int count = 1) => _popStyleColor?.Invoke(count);
        public static void PushStyleVar(int idx, float val) => _pushStyleVar_Float?.Invoke(idx, val);
        public static void PushStyleVar(int idx, float x, float y) => _pushStyleVar_Vec2?.Invoke(idx, x, y);
        public static void PopStyleVar(int count = 1) => _popStyleVar?.Invoke(count);

        #endregion

        #region Public API — Plotting (ImPlot)

        public static bool PlotBeginPlot(string titleId, float w = -1, float h = 0, int flags = 0) => _plotBeginPlot?.Invoke(titleId, w, h, flags) != 0;
        public static void PlotEndPlot() => _plotEndPlot?.Invoke();
        public static void PlotPlotLine(string labelId, float[] values) => _plotPlotLine?.Invoke(labelId, values, values?.Length ?? 0);
        public static void PlotPlotBars(string labelId, float[] values, double barSize = 0.67) => _plotPlotBars?.Invoke(labelId, values, values?.Length ?? 0, barSize);

        #endregion

        #region Public API — DrawList (ForegroundDrawList screen-space primitives)

        public static bool HasDrawListSupport => _drawListAddLine != null;

        public static void DrawList_AddLine(float x1, float y1, float x2, float y2, uint col, float thickness = 1.0f)
            => _drawListAddLine?.Invoke(x1, y1, x2, y2, col, thickness);

        public static void DrawList_AddRect(float x1, float y1, float x2, float y2, uint col, float rounding = 0, float thickness = 1.0f)
            => _drawListAddRect?.Invoke(x1, y1, x2, y2, col, rounding, thickness);

        public static void DrawList_AddRectFilled(float x1, float y1, float x2, float y2, uint col, float rounding = 0)
            => _drawListAddRectFilled?.Invoke(x1, y1, x2, y2, col, rounding);

        public static void DrawList_AddText(float x, float y, uint col, string text)
            => _drawListAddText?.Invoke(x, y, col, text);

        public static void GetDisplaySize(out float w, out float h)
        {
            if (_getDisplaySize != null) { _getDisplaySize(out w, out h); }
            else { w = 1920; h = 1080; }
        }

        /// <summary>Pack RGBA floats (0-1) into ImGui's packed uint32 (ABGR byte order).</summary>
        public static uint ImColor(float r, float g, float b, float a = 1.0f)
        {
            byte rb = (byte)(Math.Max(0f, Math.Min(1f, r)) * 255f);
            byte gb = (byte)(Math.Max(0f, Math.Min(1f, g)) * 255f);
            byte bb = (byte)(Math.Max(0f, Math.Min(1f, b)) * 255f);
            byte ab = (byte)(Math.Max(0f, Math.Min(1f, a)) * 255f);
            return (uint)(rb | (gb << 8) | (bb << 16) | (ab << 24));
        }

        #endregion

        #region ImGui Flag Constants

        // Window flags
        public const int WindowFlags_None = 0;
        public const int WindowFlags_NoTitleBar = 1 << 0;
        public const int WindowFlags_NoResize = 1 << 1;
        public const int WindowFlags_NoMove = 1 << 2;
        public const int WindowFlags_NoScrollbar = 1 << 3;
        public const int WindowFlags_NoScrollWithMouse = 1 << 4;
        public const int WindowFlags_NoCollapse = 1 << 5;
        public const int WindowFlags_AlwaysAutoResize = 1 << 6;
        public const int WindowFlags_NoBackground = 1 << 7;
        public const int WindowFlags_NoSavedSettings = 1 << 8;
        public const int WindowFlags_MenuBar = 1 << 10;
        public const int WindowFlags_HorizontalScrollbar = 1 << 11;
        public const int WindowFlags_NoFocusOnAppearing = 1 << 12;
        public const int WindowFlags_NoBringToFrontOnFocus = 1 << 13;
        public const int WindowFlags_AlwaysVerticalScrollbar = 1 << 14;
        public const int WindowFlags_AlwaysHorizontalScrollbar = 1 << 15;

        // Cond
        public const int Cond_Always = 1 << 0;
        public const int Cond_Once = 1 << 1;
        public const int Cond_FirstUseEver = 1 << 2;
        public const int Cond_Appearing = 1 << 3;

        // TreeNode flags
        public const int TreeNodeFlags_DefaultOpen = 1 << 5;
        public const int TreeNodeFlags_Framed = 1 << 2;

        // Table flags
        public const int TableFlags_Borders = 1 << 6 | 1 << 7 | 1 << 8 | 1 << 9;
        public const int TableFlags_RowBg = 1 << 4;
        public const int TableFlags_Resizable = 1 << 0;
        public const int TableFlags_Sortable = 1 << 3;
        public const int TableFlags_SizingStretchProp = 3 << 13;

        // Style colors
        public const int Col_Text = 0;
        public const int Col_WindowBg = 2;
        public const int Col_FrameBg = 7;
        public const int Col_Button = 21;
        public const int Col_ButtonHovered = 22;
        public const int Col_ButtonActive = 23;
        public const int Col_Header = 24;
        public const int Col_HeaderHovered = 25;
        public const int Col_HeaderActive = 26;
        public const int Col_Separator = 27;

        // Style vars
        public const int StyleVar_WindowPadding = 1;
        public const int StyleVar_WindowRounding = 2;
        public const int StyleVar_FramePadding = 4;
        public const int StyleVar_FrameRounding = 5;
        public const int StyleVar_ItemSpacing = 7;
        public const int StyleVar_ItemInnerSpacing = 8;
        public const int StyleVar_IndentSpacing = 9;

        #endregion
    }
}
