using System;
using System.Linq;
using System.Runtime.InteropServices;
using UnityRemix;
using static UnityRemix.RemixAPI;

var currentFields = typeof(remixapi_Interface).GetFields();
int size = NativeInterfacePointerCount * IntPtr.Size;
var success = remixapi_ErrorCode.REMIXAPI_ERROR_CODE_SUCCESS;
var incompatible = remixapi_ErrorCode.REMIXAPI_ERROR_CODE_INCOMPATIBLE_VERSION;

void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

IntPtr Pointer(int index) => new IntPtr(0x1000 + index * 16);
void CheckEmpty(remixapi_Interface table) => Check(currentFields.All(f => (IntPtr)f.GetValue(table) == IntPtr.Zero), "Failed initialization leaked pointers");

// openremix's 0.1003 Windows table: every function UnityRTX calls must map to its slot.
string[] nativeFields = currentFields.Take(29).Select(f => f.Name).Concat(new[] {
    "GetUIState", "SetUIState", "RegisterCallbacks", "AutoInstancePersistentLights",
    "UpdateLightDefinition", "DrawScreenOverlay", "SetGameValue", "RequestVramCompaction",
    "GetVramStats", "RequestTextureVramFree", "GetGameValue", "SetFogState", "SetScreenTint",
    "RegisterUITexture", "FreeUITexture", "SubmitUIDrawList", "RequestPresentedScreenshot",
    "SetCameraMediumMaterial", "PumpEvents", "GetWindowState", "PollMouseState", "IsKeyDown",
    "SetMouseGrabbed", "SetCursorPosition", "SetFullscreen", "GetFeatureAvailability"
}).ToArray();
Check(nativeFields.Length == size / IntPtr.Size, "Wrong 0.1003 Windows table size");

int calls = 0;
var result = InitializeRemixInterface((ref remixapi_InitializeLibraryInfo info, IntPtr buffer) =>
{
    calls++;
    Check(info.sType == remixapi_StructType.REMIXAPI_STRUCT_TYPE_INITIALIZE_LIBRARY_INFO && info.pNext == IntPtr.Zero, "Invalid initialization header");
    Check(info.version == REMIXAPI_VERSION_MAKE(0, 1003, 0), "Wrong API version requested");
    for (int i = 0; i < size; i++) Check(Marshal.ReadByte(buffer, i) == 0, "Table buffer was not cleared");
    for (int i = 0; i < nativeFields.Length; i++) Marshal.WriteIntPtr(buffer, i * IntPtr.Size, Pointer(i));
    return success;
}, out var table, out var version);
Check(result == success && calls == 1 && version == "0.1003.0 (openremix)", "Initialization failed");
foreach (var field in currentFields)
    Check((IntPtr)field.GetValue(table) == Pointer(Array.IndexOf(nativeFields, field.Name)), "Wrong native mapping: " + field.Name);

foreach (var error in new[] { incompatible, remixapi_ErrorCode.REMIXAPI_ERROR_CODE_INVALID_ARGUMENTS })
{
    calls = 0;
    result = InitializeRemixInterface((ref remixapi_InitializeLibraryInfo info, IntPtr buffer) =>
    {
        calls++;
        for (int i = 0; i < nativeFields.Length; i++) Marshal.WriteIntPtr(buffer, i * IntPtr.Size, Pointer(i));
        return error;
    }, out table, out version);
    Check(calls == 1 && result == error && version == null, "Changed an initialization error: " + error);
    CheckEmpty(table);
}

Check(Marshal.SizeOf<remixapi_MaterialInfoTranslucentEXT_Raw>() == 56, "remixapi_MaterialInfoTranslucentEXT_Raw size must be 56 bytes");
Check(Marshal.OffsetOf<remixapi_MaterialInfoTranslucentEXT_Raw>("sType") == (IntPtr)0, "sType offset");
Check(Marshal.OffsetOf<remixapi_MaterialInfoTranslucentEXT_Raw>("pNext") == (IntPtr)8, "pNext offset");
Check(Marshal.OffsetOf<remixapi_MaterialInfoTranslucentEXT_Raw>("transmittanceTexture") == (IntPtr)16, "transmittanceTexture offset");
Check(Marshal.OffsetOf<remixapi_MaterialInfoTranslucentEXT_Raw>("refractiveIndex") == (IntPtr)24, "refractiveIndex offset");
Check(Marshal.OffsetOf<remixapi_MaterialInfoTranslucentEXT_Raw>("transmittanceColor") == (IntPtr)28, "transmittanceColor offset");
Check(Marshal.OffsetOf<remixapi_MaterialInfoTranslucentEXT_Raw>("transmittanceMeasurementDistance") == (IntPtr)40, "transmittanceMeasurementDistance offset");
Check(Marshal.OffsetOf<remixapi_MaterialInfoTranslucentEXT_Raw>("thinWallThickness_hasvalue") == (IntPtr)44, "thinWallThickness_hasvalue offset");
Check(Marshal.OffsetOf<remixapi_MaterialInfoTranslucentEXT_Raw>("thinWallThickness_value") == (IntPtr)48, "thinWallThickness_value offset");
Check(Marshal.OffsetOf<remixapi_MaterialInfoTranslucentEXT_Raw>("useDiffuseLayer") == (IntPtr)52, "useDiffuseLayer offset");

Check(Marshal.SizeOf<remixapi_CameraMediumEXT>() == 40, "remixapi_CameraMediumEXT size must be 40 bytes");
Check(Marshal.OffsetOf<remixapi_CameraMediumEXT>("sType") == (IntPtr)0, "sType offset");
Check(Marshal.OffsetOf<remixapi_CameraMediumEXT>("pNext") == (IntPtr)8, "pNext offset");
Check(Marshal.OffsetOf<remixapi_CameraMediumEXT>("inside") == (IntPtr)16, "inside offset");
Check(Marshal.OffsetOf<remixapi_CameraMediumEXT>("transmittanceColor") == (IntPtr)20, "transmittanceColor offset");
Check(Marshal.OffsetOf<remixapi_CameraMediumEXT>("transmittanceMeasurementDistance") == (IntPtr)32, "transmittanceMeasurementDistance offset");

Check(Marshal.SizeOf<remixapi_MaterialPbrChannelEXT>() == 24, "remixapi_MaterialPbrChannelEXT size must be 24 bytes");
Check(Marshal.SizeOf<remixapi_MaterialPbrRemapEXT>() == 64, "remixapi_MaterialPbrRemapEXT size must be 64 bytes");
Check(Marshal.OffsetOf<remixapi_MaterialPbrRemapEXT>("roughness") == (IntPtr)16, "roughness offset");
Check(Marshal.OffsetOf<remixapi_MaterialPbrRemapEXT>("metallic") == (IntPtr)40, "metallic offset");

Console.WriteLine("Passed: openremix API negotiation, pointer mapping, buffer clearing, error propagation, and struct layouts.");
