using System;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using UnityRemix;
using static UnityRemix.RemixAPI;

// Native table order independently checked against UnityRTX v0.2's released DLL.
string[] releaseFields = {
    "Shutdown", "CreateMaterial", "DestroyMaterial", "CreateMesh", "CreateMeshBatched",
    "DestroyMesh", "SetupCamera", "DrawInstance", "CreateLight", "CreateLightBatched",
    "DestroyLight", "DrawLightInstance", "SetConfigVariable", "AddTextureHash", "RemoveTextureHash",
    "CreateTexture", "DestroyTexture", "dxvk_CreateD3D9", "dxvk_RegisterD3D9Device",
    "dxvk_GetExternalSwapchain", "dxvk_GetVkImage", "dxvk_CopyRenderingOutput",
    "dxvk_SetDefaultOutput", "dxvk_GetTextureHash", "pick_RequestObjectPicking",
    "pick_HighlightObjects", "Startup", "Present", "GetUIState", "SetUIState",
    "RegisterCallbacks", "AutoInstancePersistentLights", "UpdateLightDefinition"
};
var currentFields = typeof(remixapi_Interface).GetFields();
int size = NativeInterfacePointerCount * IntPtr.Size;
ulong currentVersion = REMIXAPI_VERSION_MAKE(0, 1000, 0);
ulong releaseVersion = REMIXAPI_VERSION_MAKE(0, 6, 1);
var success = remixapi_ErrorCode.REMIXAPI_ERROR_CODE_SUCCESS;
var incompatible = remixapi_ErrorCode.REMIXAPI_ERROR_CODE_INCOMPATIBLE_VERSION;

void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

IntPtr Pointer(int index) => new IntPtr(0x1000 + index * 16);
IntPtr Export(string name)
{
    int index = Array.IndexOf(releaseFields, name.Substring("remixapi_".Length));
    return index < 0 ? IntPtr.Zero : Pointer(index);
}
void CheckEmpty(remixapi_Interface table) => Check(currentFields.All(f => (IntPtr)f.GetValue(table) == IntPtr.Zero), "Failed initialization leaked pointers");

int calls = 0;
var result = InitializeRemixInterface((ref remixapi_InitializeLibraryInfo info, IntPtr buffer) =>
{
    calls++;
    Check(info.version == currentVersion, "Current runtime received the wrong version");
    for (int i = 0; i < currentFields.Length; i++) Marshal.WriteIntPtr(buffer, i * IntPtr.Size, Pointer(i));
    return success;
}, _ => throw new Exception("Successful current runtime must not trigger a fallback"), out var table, out var version);
Check(result == success && calls == 1 && version == "0.1000.0", "Current initialization failed");
for (int i = 0; i < currentFields.Length; i++)
    Check((IntPtr)currentFields[i].GetValue(table) == Pointer(i), "Current table changed: " + currentFields[i].Name);

calls = 0;
result = InitializeRemixInterface((ref remixapi_InitializeLibraryInfo info, IntPtr buffer) =>
{
    calls++;
    Check(info.sType == remixapi_StructType.REMIXAPI_STRUCT_TYPE_INITIALIZE_LIBRARY_INFO && info.pNext == IntPtr.Zero, "Invalid initialization header");
    if (calls == 1)
    {
        Check(info.version == currentVersion, "Try the current API first");
        for (int i = 0; i < size; i++) Marshal.WriteByte(buffer, i, 0xff);
        return incompatible;
    }
    Check(info.version == releaseVersion, "Fallback requested the wrong API version");
    for (int i = 0; i < size; i++) Check(Marshal.ReadByte(buffer, i) == 0, "Retry buffer was not cleared");
    for (int i = 0; i < releaseFields.Length; i++) Marshal.WriteIntPtr(buffer, i * IntPtr.Size, Pointer(i));
    return success;
}, Export, out table, out version);
Check(result == success && calls == 2 && version == "0.6.1 (UnityRTX v0.2)", "Release fallback failed");
foreach (var field in currentFields)
{
    int index = Array.IndexOf(releaseFields, field.Name);
    Check((IntPtr)field.GetValue(table) == (index < 0 ? IntPtr.Zero : Pointer(index)), "Wrong release mapping: " + field.Name);
}

calls = 0;
result = InitializeRemixInterface((ref remixapi_InitializeLibraryInfo info, IntPtr buffer) =>
{
    calls++;
    Marshal.WriteIntPtr(buffer, Pointer(0));
    return remixapi_ErrorCode.REMIXAPI_ERROR_CODE_INVALID_ARGUMENTS;
}, _ => throw new Exception("Non-version errors must not trigger a fallback"), out table, out version);
Check(calls == 1 && result == remixapi_ErrorCode.REMIXAPI_ERROR_CODE_INVALID_ARGUMENTS && version == null, "Changed a non-version error");
CheckEmpty(table);

calls = 0;
result = InitializeRemixInterface((ref remixapi_InitializeLibraryInfo info, IntPtr buffer) =>
{
    calls++;
    return incompatible;
}, _ => IntPtr.Zero, out table, out version);
Check(calls == 1 && result == incompatible && version == null, "Unrecognized runtime should not be retried");
CheckEmpty(table);

// Each of these named exports must be in its verified release slot. Other
// forks can accept 0.6.1 while returning a different, unsafe table layout.
foreach (string shiftedField in new[] { "GetUIState", "SetUIState", "RegisterCallbacks", "UpdateLightDefinition" })
{
    result = InitializeRemixInterface((ref remixapi_InitializeLibraryInfo info, IntPtr buffer) =>
    {
        if (info.version == currentVersion) return incompatible;
        for (int i = 0; i < releaseFields.Length; i++) Marshal.WriteIntPtr(buffer, i * IntPtr.Size, Pointer(i));
        Marshal.WriteIntPtr(buffer, Array.IndexOf(releaseFields, shiftedField) * IntPtr.Size, new IntPtr(0xdead));
        return success;
    }, Export, out table, out version);
    Check(result == incompatible && version == null, "Accepted an incompatible table: " + shiftedField);
    CheckEmpty(table);
}

result = InitializeRemixInterface((ref remixapi_InitializeLibraryInfo info, IntPtr buffer) => incompatible,
    Export, out table, out version);
Check(result == incompatible && version == null, "Both versions rejected should fail");
CheckEmpty(table);

// openremix clears all 55 slots even on a rejected version. Exercise that write
// before negotiating 0.1003 and check every function UnityRTX will call.
string[] nativeFields = currentFields.Take(29).Select(f => f.Name).Concat(new[] {
    "GetUIState", "SetUIState", "RegisterCallbacks", "AutoInstancePersistentLights",
    "UpdateLightDefinition", "DrawScreenOverlay", "SetGameValue", "RequestVramCompaction",
    "GetVramStats", "RequestTextureVramFree", "GetGameValue", "SetFogState", "SetScreenTint",
    "RegisterUITexture", "FreeUITexture", "SubmitUIDrawList", "RequestPresentedScreenshot",
    "SetCameraMediumMaterial", "PumpEvents", "GetWindowState", "PollMouseState", "IsKeyDown",
    "SetMouseGrabbed", "SetCursorPosition", "SetFullscreen", "GetFeatureAvailability"
}).ToArray();
Check(nativeFields.Length == 55, "Wrong 0.1003 Windows table size");
IntPtr NativeExport(string name) => name == "remixapi_GetBackendCapabilitiesEXT" ? Pointer(100) :
    name == "remixapi_PumpEvents" ? Pointer(47) : IntPtr.Zero;
calls = 0;
result = InitializeRemixInterface((ref remixapi_InitializeLibraryInfo info, IntPtr buffer) =>
{
    calls++;
    for (int i = 0; i < nativeFields.Length; i++) Marshal.WriteIntPtr(buffer, i * IntPtr.Size, IntPtr.Zero);
    if (info.version == currentVersion) return incompatible;
    Check(info.version == REMIXAPI_VERSION_MAKE(0, 1003, 0), "Wrong native API version");
    for (int i = 0; i < nativeFields.Length; i++) Marshal.WriteIntPtr(buffer, i * IntPtr.Size, Pointer(i));
    return success;
}, NativeExport, out table, out version);
Check(result == success && calls == 2 && version == "0.1003.0 (openremix)", "Native negotiation failed");
foreach (var field in currentFields)
    Check((IntPtr)field.GetValue(table) == Pointer(Array.IndexOf(nativeFields, field.Name)), "Wrong native mapping: " + field.Name);

result = InitializeRemixInterface((ref remixapi_InitializeLibraryInfo info, IntPtr buffer) => incompatible,
    NativeExport, out table, out version);
Check(result == incompatible && version == null, "Rejected native API should fail");
CheckEmpty(table);

// Optional integration check: compare against the user's actual working plugin
// without executing it or requiring Unity assemblies. No release DLL is vendored.
if (args.Length == 1)
{
    var context = new AssemblyLoadContext("release", isCollectible: true);
    var releaseApi = context.LoadFromAssemblyPath(System.IO.Path.GetFullPath(args[0])).GetType("UnityRemix.RemixAPI", true);
    Check((uint)releaseApi.GetField("REMIXAPI_VERSION_MINOR").GetRawConstantValue() == 6 &&
        (uint)releaseApi.GetField("REMIXAPI_VERSION_PATCH").GetRawConstantValue() == 1, "Expected the 0.6.1 release plugin");
    var nativeTable = releaseApi.GetNestedType("remixapi_Interface");
    Check(nativeTable.GetFields().Select(f => f.Name).SequenceEqual(releaseFields), "Release table differs from the fixture");
    Check(Marshal.SizeOf(nativeTable) == Marshal.SizeOf<RemixInterface061>(), "Release table size mismatch");
    foreach (var type in releaseApi.GetNestedTypes())
    {
        var currentType = typeof(RemixAPI).GetNestedType(type.Name);
        if (currentType == null || type == nativeTable) continue;
        if (type.IsEnum)
        {
            foreach (string name in Enum.GetNames(type).Intersect(Enum.GetNames(currentType)))
                Check(Convert.ToInt64(Enum.Parse(type, name)) == Convert.ToInt64(Enum.Parse(currentType, name)), "Enum mismatch: " + type.Name + "." + name);
        }
        else if (type.IsValueType)
        {
            Check(Marshal.SizeOf(type) == Marshal.SizeOf(currentType), "Struct size mismatch: " + type.Name);
            foreach (var field in type.GetFields())
                Check(Marshal.OffsetOf(type, field.Name) == Marshal.OffsetOf(currentType, field.Name), "Field offset mismatch: " + type.Name + "." + field.Name);
        }
    }
    context.Unload();
    Console.WriteLine("Release DLL verified: table order, struct sizes, field offsets, and shared enum values.");
}

Console.WriteLine("Passed: current, legacy and openremix API negotiation and pointer mapping, retry clearing, error propagation, and incompatible-layout rejection.");
