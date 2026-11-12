# DXVK 2.6.2's D3D11 shader compiler (dxbc, zlib licence: LICENSE) standing
# alone: DXBC (shader model 4-5) to SPIR-V, with DXVK's own util and SPIR-V
# builder, and shims in src/dxvk and src/d3d11 in place of the DXVK internals
# it was written against (see README.md). The same arrangement as SourcePT's
# deps/dxso (DXVK's D3D9 compiler), whose util, SPIR-V builder and shims it
# shares.
dxbc_inc = include_directories('src', 'include')
vulkan_inc = include_directories(get_option('vulkan_include'))
dxbc_src = files(
  'src/dxbc/dxbc_analysis.cpp', 'src/dxbc/dxbc_chunk_isgn.cpp', 'src/dxbc/dxbc_chunk_shex.cpp',
  'src/dxbc/dxbc_common.cpp', 'src/dxbc/dxbc_compiler.cpp', 'src/dxbc/dxbc_decoder.cpp', 'src/dxbc/dxbc_defs.cpp',
  'src/dxbc/dxbc_header.cpp', 'src/dxbc/dxbc_module.cpp', 'src/dxbc/dxbc_names.cpp', 'src/dxbc/dxbc_options.cpp',
  'src/dxbc/dxbc_reader.cpp', 'src/dxbc/dxbc_util.cpp',
  'src/spirv/spirv_code_buffer.cpp', 'src/spirv/spirv_compression.cpp', 'src/spirv/spirv_module.cpp',
  'src/util/log/log.cpp', 'src/util/util_string.cpp', 'src/util/util_env.cpp', 'src/util/dxbc_standalone.cpp',
)
dxbc_lib = static_library('dxbc', dxbc_src,
  include_directories: [dxbc_inc, vulkan_inc],
  cpp_args: ['-w', '-DNOMINMAX', '-D_CRT_SECURE_NO_WARNINGS'],
)
dxbc_dep = declare_dependency(link_with: dxbc_lib, include_directories: [dxbc_inc, vulkan_inc])
executable('dxbc_tool', 'tools/dxbc_tool.cpp', dependencies: dxbc_dep, cpp_args: ['-DNOMINMAX'])
