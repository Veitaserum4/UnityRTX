# UnityRTXHostPass.dll: D3D11 capture of the draws UnityRTX marks (render
# thread plugin events), compiled to SPIR-V for OpenRemix host passes.
openremix_inc = include_directories(get_option('vulkan_include') / '..' / '..' / '..' / 'public' / 'include')
# The replay's own shaders (shaders/), SPIR-V as C arrays.
glslang = find_program('glslangValidator')
own_shaders = []
foreach shader : ['fullscreen.vert', 'frame_depth.frag']
  own_shaders += custom_target(shader + '.inc',
    input: 'shaders' / shader, output: shader + '.inc',
    command: [glslang, '-V', '--vn', shader.replace('.', '_'), '-o', '@OUTPUT@', '@INPUT@'])
endforeach
shared_library('UnityRTXHostPass',
  'src/hooks.cpp', 'src/shaders.cpp', 'src/capture.cpp', 'src/replay.cpp', 'src/api.cpp', own_shaders,
  include_directories: [include_directories('src'), openremix_inc],
  dependencies: [dxbc_dep],
  cpp_args: ['-DNOMINMAX', '-D_CRT_SECURE_NO_WARNINGS', '-DWIN32_LEAN_AND_MEAN'],
  link_args: ['d3d11.lib'],
  name_prefix: '',
)
