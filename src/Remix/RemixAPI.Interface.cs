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
            result.SetFogState = Marshal.ReadIntPtr(buffer, 40 * IntPtr.Size);
            result.SetCameraMediumMaterial = Marshal.ReadIntPtr(buffer, 46 * IntPtr.Size);
            return result;
        }

        /// <summary>
        /// Requests openremix's 0.1003 interface table. A failed call leaves the table empty.
        /// </summary>
        internal static remixapi_ErrorCode InitializeRemixInterface(
            PFN_remixapi_InitializeLibrary initialize,
            out remixapi_Interface remixInterface,
            out string apiVersion)
        {
            remixInterface = default;
            apiVersion = null;

            int size = Math.Max(Marshal.SizeOf<remixapi_Interface>(), NativeInterfacePointerCount * IntPtr.Size);
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.Copy(new byte[size], 0, buffer, size);
                var info = new remixapi_InitializeLibraryInfo
                {
                    sType = remixapi_StructType.REMIXAPI_STRUCT_TYPE_INITIALIZE_LIBRARY_INFO,
                    version = REMIXAPI_VERSION_MAKE(REMIXAPI_VERSION_MAJOR, REMIXAPI_VERSION_MINOR, REMIXAPI_VERSION_PATCH)
                };
                var result = initialize(ref info, buffer);
                if (result == remixapi_ErrorCode.REMIXAPI_ERROR_CODE_SUCCESS)
                {
                    remixInterface = ReadNativeInterface(buffer);
                    apiVersion = "0.1003.0 (openremix)";
                }
                return result;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }
}
