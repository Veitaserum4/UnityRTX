using System;
using System.Runtime.InteropServices;

namespace UnityRemix
{
    public static partial class RemixAPI
    {
        // Windows API 0.1003 has 55 function pointers (openremix's checked ABI).
        // It clears the entire table even when it rejects the requested version.
        internal const int NativeInterfacePointerCount = 55;

        private static remixapi_Interface ReadNativeInterface(IntPtr buffer)
        {
            // Slots 0..28, through Present, match the 0.1000 Windows layout.
            var result = Marshal.PtrToStructure<remixapi_Interface>(buffer);
            result.GetUIState = Marshal.ReadIntPtr(buffer, 29 * IntPtr.Size);
            result.SetUIState = Marshal.ReadIntPtr(buffer, 30 * IntPtr.Size);
            result.RegisterCallbacks = Marshal.ReadIntPtr(buffer, 31 * IntPtr.Size);
            result.AutoInstancePersistentLights = Marshal.ReadIntPtr(buffer, 32 * IntPtr.Size);
            result.UpdateLightDefinition = Marshal.ReadIntPtr(buffer, 33 * IntPtr.Size);
            result.DrawScreenOverlay = Marshal.ReadIntPtr(buffer, 34 * IntPtr.Size);
            result.SetGameValue = Marshal.ReadIntPtr(buffer, 35 * IntPtr.Size);
            result.RequestVramCompaction = Marshal.ReadIntPtr(buffer, 36 * IntPtr.Size);
            result.GetVramStats = Marshal.ReadIntPtr(buffer, 37 * IntPtr.Size);
            result.RequestTextureVramFree = Marshal.ReadIntPtr(buffer, 38 * IntPtr.Size);
            result.GetGameValue = Marshal.ReadIntPtr(buffer, 39 * IntPtr.Size);
            result.SetCameraMediumMaterial = Marshal.ReadIntPtr(buffer, 46 * IntPtr.Size);
            return result;
        }

        // Exact 33-pointer layout used by UnityRTX v0.2's 0.6.1 runtime.
        // Later runtimes inserted fields before Startup and GetUIState; passing
        // their table straight to the current bindings calls the wrong functions.
        [StructLayout(LayoutKind.Sequential)]
        internal struct RemixInterface061
        {
            public IntPtr Shutdown;
            public IntPtr CreateMaterial;
            public IntPtr DestroyMaterial;
            public IntPtr CreateMesh;
            public IntPtr CreateMeshBatched;
            public IntPtr DestroyMesh;
            public IntPtr SetupCamera;
            public IntPtr DrawInstance;
            public IntPtr CreateLight;
            public IntPtr CreateLightBatched;
            public IntPtr DestroyLight;
            public IntPtr DrawLightInstance;
            public IntPtr SetConfigVariable;
            public IntPtr AddTextureHash;
            public IntPtr RemoveTextureHash;
            public IntPtr CreateTexture;
            public IntPtr DestroyTexture;
            public IntPtr dxvk_CreateD3D9;
            public IntPtr dxvk_RegisterD3D9Device;
            public IntPtr dxvk_GetExternalSwapchain;
            public IntPtr dxvk_GetVkImage;
            public IntPtr dxvk_CopyRenderingOutput;
            public IntPtr dxvk_SetDefaultOutput;
            public IntPtr dxvk_GetTextureHash;
            public IntPtr pick_RequestObjectPicking;
            public IntPtr pick_HighlightObjects;
            public IntPtr Startup;
            public IntPtr Present;
            public IntPtr GetUIState;
            public IntPtr SetUIState;
            public IntPtr RegisterCallbacks;
            public IntPtr AutoInstancePersistentLights;
            public IntPtr UpdateLightDefinition;

            public remixapi_Interface ToCurrentInterface()
            {
                return new remixapi_Interface
                {
                    Shutdown = Shutdown,
                    CreateMaterial = CreateMaterial,
                    DestroyMaterial = DestroyMaterial,
                    CreateMesh = CreateMesh,
                    CreateMeshBatched = CreateMeshBatched,
                    DestroyMesh = DestroyMesh,
                    SetupCamera = SetupCamera,
                    DrawInstance = DrawInstance,
                    CreateLight = CreateLight,
                    CreateLightBatched = CreateLightBatched,
                    DestroyLight = DestroyLight,
                    DrawLightInstance = DrawLightInstance,
                    SetConfigVariable = SetConfigVariable,
                    AddTextureHash = AddTextureHash,
                    RemoveTextureHash = RemoveTextureHash,
                    CreateTexture = CreateTexture,
                    DestroyTexture = DestroyTexture,
                    dxvk_CreateD3D9 = dxvk_CreateD3D9,
                    dxvk_RegisterD3D9Device = dxvk_RegisterD3D9Device,
                    dxvk_GetExternalSwapchain = dxvk_GetExternalSwapchain,
                    dxvk_GetVkImage = dxvk_GetVkImage,
                    dxvk_CopyRenderingOutput = dxvk_CopyRenderingOutput,
                    dxvk_SetDefaultOutput = dxvk_SetDefaultOutput,
                    dxvk_GetTextureHash = dxvk_GetTextureHash,
                    pick_RequestObjectPicking = pick_RequestObjectPicking,
                    pick_HighlightObjects = pick_HighlightObjects,
                    Startup = Startup,
                    Present = Present,
                    GetUIState = GetUIState,
                    SetUIState = SetUIState,
                    RegisterCallbacks = RegisterCallbacks,
                    AutoInstancePersistentLights = AutoInstancePersistentLights,
                    UpdateLightDefinition = UpdateLightDefinition,
                    // Functions absent from 0.6.1 stay null.
                };
            }
        }

        internal static remixapi_ErrorCode InitializeRemixInterface(
            PFN_remixapi_InitializeLibrary initialize,
            Func<string, IntPtr> getExport,
            out remixapi_Interface remixInterface,
            out string apiVersion)
        {
            remixInterface = default;
            apiVersion = null;

            // Both attempts receive a cleared buffer large enough for the current
            // table. A rejected call must not leave stale pointers for a retry.
            int size = Math.Max(Marshal.SizeOf<remixapi_Interface>(), NativeInterfacePointerCount * IntPtr.Size);
            byte[] empty = new byte[size];
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.Copy(empty, 0, buffer, size);
                var info = new remixapi_InitializeLibraryInfo
                {
                    sType = remixapi_StructType.REMIXAPI_STRUCT_TYPE_INITIALIZE_LIBRARY_INFO,
                    version = REMIXAPI_VERSION_MAKE(REMIXAPI_VERSION_MAJOR, REMIXAPI_VERSION_MINOR, REMIXAPI_VERSION_PATCH)
                };
                var result = initialize(ref info, buffer);
                if (result == remixapi_ErrorCode.REMIXAPI_ERROR_CODE_SUCCESS)
                {
                    remixInterface = Marshal.PtrToStructure<remixapi_Interface>(buffer);
                    apiVersion = "0.1000.0";
                    return result;
                }
                if (result != remixapi_ErrorCode.REMIXAPI_ERROR_CODE_INCOMPATIBLE_VERSION)
                    return result;

                if (getExport("remixapi_GetBackendCapabilitiesEXT") != IntPtr.Zero &&
                    getExport("remixapi_PumpEvents") != IntPtr.Zero)
                {
                    Marshal.Copy(empty, 0, buffer, size);
                    info.version = REMIXAPI_VERSION_MAKE(0, 1003, 0);
                    result = initialize(ref info, buffer);
                    if (result == remixapi_ErrorCode.REMIXAPI_ERROR_CODE_SUCCESS)
                    {
                        remixInterface = ReadNativeInterface(buffer);
                        apiVersion = "0.1003.0 (openremix)";
                    }
                    return result;
                }

                // 0.6.x does not distinguish incompatible fork layouts by patch
                // version. Require the release's named exports, then verify their
                // actual table positions before using its function pointers.
                IntPtr getUIState = getExport("remixapi_GetUIState");
                IntPtr setUIState = getExport("remixapi_SetUIState");
                IntPtr registerCallbacks = getExport("remixapi_RegisterCallbacks");
                IntPtr updateLight = getExport("remixapi_UpdateLightDefinition");
                if (getUIState == IntPtr.Zero || setUIState == IntPtr.Zero ||
                    registerCallbacks == IntPtr.Zero || updateLight == IntPtr.Zero)
                    return result;

                Marshal.Copy(empty, 0, buffer, size);
                info.version = REMIXAPI_VERSION_MAKE(0, 6, 1);
                result = initialize(ref info, buffer);
                if (result != remixapi_ErrorCode.REMIXAPI_ERROR_CODE_SUCCESS)
                    return result;

                var legacy = Marshal.PtrToStructure<RemixInterface061>(buffer);
                if (legacy.GetUIState != getUIState || legacy.SetUIState != setUIState ||
                    legacy.RegisterCallbacks != registerCallbacks || legacy.UpdateLightDefinition != updateLight)
                    return remixapi_ErrorCode.REMIXAPI_ERROR_CODE_INCOMPATIBLE_VERSION;

                remixInterface = legacy.ToCurrentInterface();
                apiVersion = "0.6.1 (UnityRTX v0.2)";
                return remixapi_ErrorCode.REMIXAPI_ERROR_CODE_SUCCESS;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }
}
