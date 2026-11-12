# dxbc, standalone

DXVK 2.6.2's D3D11 shader compiler (`src/dxbc`), SPIR-V builder (`src/spirv`) and the parts of `src/util` they use,
vendored so UnityRTX can turn a Unity game's own D3D11 vertex and pixel shaders (DXBC, shader model 4-5) into
SPIR-V that OpenRemix replays as host passes (`remixapi_SubmitHostPassesEXT`). The D3D11 counterpart of SourcePT's
`deps/dxso` (DXVK's D3D9 compiler), whose `src/util`, `src/spirv`, `include` and `src/dxvk/dxvk_shader.h` shim it
starts from. Licence: zlib (LICENSE); SPIR-V headers under include/spirv (KhronosGroup/SPIRV-Headers,
include/spirv/LICENSE); DXVK's native Windows headers under include/native.

`src/dxbc` is DXVK 2.6.2's, verbatim but for:

- `dxbc_options.cpp`: the default constructor only (DXVK's device constructor reads the device's features).
- `dxbc_compiler.cpp`: SPIR-V 1.5 instead of 1.6 (OpenRemix creates a Vulkan 1.2 device), and `discard` as `OpKill`
  instead of `OpDemoteToHelperInvocation` (core only from SPIR-V 1.6; a screen pass reads no derivatives past it).
- `spirv_module.{h,cpp}`: an `opKill()` for the above.

Shims replace the DXVK internals the compiler includes, keeping their names: `src/dxvk/dxvk_shader.h` (the compiled
shader: SPIR-V and its bindings; `DxvkAccessOp` and `DxvkBindingInfo` as DXVK 2.6.2 declares them),
`src/dxvk/dxvk_device.h` (no device; the basic headers it brought along), `src/d3d11/d3d11_options.h`.

`tools/dxbc_tool.cpp` compiles one shader to SPIR-V and prints its interface:
`dxbc_tool <shader.dxbc> <out.spv>`. Build: `meson setup _build -Dvulkan_include=<Vulkan headers>; ninja -C _build`
(clang-cl or MSVC; OpenRemix ships the Vulkan headers under include/vulkan/include).
