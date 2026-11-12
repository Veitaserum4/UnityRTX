# UnityRTX's native side: passing a Unity game's own D3D11 shaders through to
# OpenRemix as host passes (remixapi_SubmitHostPassesEXT), as SourcePT does for
# Source's D3D9 ones. deps/dxbc compiles their DXBC to SPIR-V; hostpass11 hooks
# Unity's D3D11 device and records the draws the plugin asks for.
project('unityrtx-native', 'cpp', default_options: ['cpp_std=c++20', 'warning_level=1', 'b_vscrt=mt'])
subdir('deps/dxbc')
subdir('hostpass11')
