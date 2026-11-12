// dxbc_tool <shader.dxbc> <out.spv>: compiles a D3D11 shader (DXBC, shader
// model 4-5) to SPIR-V with the standalone dxbc and prints its interface: the
// descriptor bindings (set 0, as DXVK's D3D11 layout numbers them) and the
// push constant block.
#include "dxbc/dxbc_module.h"
#include "dxbc/dxbc_modinfo.h"
#include "dxbc/dxbc_reader.h"
#include <cstdio>
#include <fstream>
#include <vector>

static const char *typeName(VkDescriptorType t) {
  switch (t) {
  case VK_DESCRIPTOR_TYPE_SAMPLER: return "sampler";
  case VK_DESCRIPTOR_TYPE_SAMPLED_IMAGE: return "sampled image";
  case VK_DESCRIPTOR_TYPE_UNIFORM_BUFFER: return "uniform buffer";
  case VK_DESCRIPTOR_TYPE_STORAGE_BUFFER: return "storage buffer";
  case VK_DESCRIPTOR_TYPE_STORAGE_IMAGE: return "storage image";
  case VK_DESCRIPTOR_TYPE_UNIFORM_TEXEL_BUFFER: return "uniform texel buffer";
  case VK_DESCRIPTOR_TYPE_STORAGE_TEXEL_BUFFER: return "storage texel buffer";
  default: return "other";
  }
}

int main(int argc, char **argv) {
  if (argc < 3) { std::fprintf(stderr, "usage: dxbc_tool <shader.dxbc> <out.spv>\n"); return 2; }
  std::ifstream in(argv[1], std::ios::binary | std::ios::ate);
  if (!in) { std::fprintf(stderr, "cannot read %s\n", argv[1]); return 1; }
  std::vector<char> data(size_t(in.tellg()));
  in.seekg(0); in.read(data.data(), data.size());
  try {
    dxvk::DxbcReader reader(data.data(), data.size());
    dxvk::DxbcModule module(reader);
    dxvk::DxbcModuleInfo info = {};
    info.options = dxvk::DxbcOptions();
    auto shader = module.compile(info, argv[1]);
    const auto &code = shader->code();
    std::ofstream(argv[2], std::ios::binary).write(reinterpret_cast<const char *>(code.data()), code.size());
    const auto &i = shader->info();
    std::printf("%s: %zu bytes of SPIR-V, stage %x, inputs %08x outputs %08x, push %u bytes, bindings:\n", argv[1],
                code.size(), unsigned(i.stage), i.inputMask, i.outputMask, i.pushConstSize);
    for (const auto &b : shader->bindings())
      std::printf("  binding %u: %s (view type %d)\n", b.resourceBinding, typeName(b.descriptorType), int(b.viewType));
  } catch (const dxvk::DxvkError &e) {
    std::fprintf(stderr, "compile failed: %s\n", e.message().c_str());
    return 1;
  }
  return 0;
}
