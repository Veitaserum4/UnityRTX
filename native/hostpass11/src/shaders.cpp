// Shaders as Unity creates them: the DXBC, its resource names by register
// (the RDEF chunk) and input signature (ISGN), and its SPIR-V from the
// standalone dxbc on first use, with each resource's binding as DXVK's D3D11
// layout numbers it (dxbc_util.h computeConstantBufferBinding and the like).
#include "hostpass.h"
#include "dxbc/dxbc_module.h"
#include "dxbc/dxbc_modinfo.h"
#include "dxbc/dxbc_reader.h"
#include "dxbc/dxbc_util.h"
#include <remix/remix_native_ext.h>
#include <algorithm>
#include <cstring>

namespace urtx {
  namespace {
    uint64_t fnv(const uint8_t *data, size_t bytes) {
      uint64_t h = 1469598103934665603ull;
      for (size_t i = 0; i < bytes; ++i)
        h = (h ^ data[i]) * 1099511628211ull;
      return h;
    }
    template <class T> T read(const std::vector<uint8_t> &d, size_t at) {
      T v{};
      if (at + sizeof(T) <= d.size())
        std::memcpy(&v, d.data() + at, sizeof(T));
      return v;
    }
    std::string string(const std::vector<uint8_t> &d, size_t at) {
      std::string s;
      while (at < d.size() && d[at])
        s.push_back(char(d[at++]));
      return s;
    }
    // The chunk with FourCC `tag`: its data's offset and size (0 when absent).
    bool chunk(const std::vector<uint8_t> &d, const char tag[4], size_t &at, size_t &size) {
      const uint32_t count = read<uint32_t>(d, 28);
      for (uint32_t i = 0; i < count && i < 64; ++i) {
        const uint32_t offset = read<uint32_t>(d, 32 + 4 * i);
        if (offset + 8 <= d.size() && std::memcmp(d.data() + offset, tag, 4) == 0) {
          at = offset + 8;
          size = read<uint32_t>(d, offset + 4);
          return at + size <= d.size();
        }
      }
      return false;
    }
    // RDEF: resource bindings (name, type, bind point, count).
    void parseResources(Shader &s) {
      size_t at = 0, size = 0;
      if (!chunk(s.dxbc, "RDEF", at, size))
        return;
      const uint32_t count = read<uint32_t>(s.dxbc, at + 8), offset = read<uint32_t>(s.dxbc, at + 12);
      for (uint32_t i = 0; i < count && i < 256; ++i) {
        const size_t r = at + offset + 32 * i; // D3D11_SHADER_INPUT_BIND_DESC as stored: 8 dwords
        ShaderResource res;
        res.name = string(s.dxbc, at + read<uint32_t>(s.dxbc, r));
        res.type = read<uint32_t>(s.dxbc, r + 4);
        res.bindPoint = read<uint32_t>(s.dxbc, r + 20);
        res.bindCount = read<uint32_t>(s.dxbc, r + 24);
        const auto stage = s.vertex ? dxvk::DxbcProgramType::VertexShader : dxvk::DxbcProgramType::PixelShader;
        if (res.type == 0)
          res.binding = dxvk::computeConstantBufferBinding(stage, res.bindPoint);
        else if (res.type == 3)
          res.binding = dxvk::computeSamplerBinding(stage, res.bindPoint);
        else if (res.type == 2)
          res.binding = dxvk::computeSrvBinding(stage, res.bindPoint);
        s.resources.push_back(std::move(res));
      }
    }
    // Without RDEF (Unity strips it from built shaders): the resources the
    // shader program declares (SHDR/SHEX dcl_constantbuffer, dcl_resource,
    // dcl_sampler), named by register (cb0, t0, s0). The textures' identities
    // the capture keeps let the plugin match them to material properties.
    void parseDeclarations(Shader &s) {
      size_t at = 0, size = 0;
      if (!chunk(s.dxbc, "SHEX", at, size) && !chunk(s.dxbc, "SHDR", at, size))
        return;
      if (((read<uint32_t>(s.dxbc, at) >> 4) & 0xf) == 5 && (read<uint32_t>(s.dxbc, at) & 0xf) == 1)
        return; // shader model 5.1 declares ranges differently; Unity's D3D11 shaders are 5.0 at most
      const size_t end = at + std::min<size_t>(size, size_t(read<uint32_t>(s.dxbc, at + 4)) * 4);
      const auto stage = s.vertex ? dxvk::DxbcProgramType::VertexShader : dxvk::DxbcProgramType::PixelShader;
      for (size_t i = at + 8; i + 4 <= end;) {
        const uint32_t token = read<uint32_t>(s.dxbc, i), opcode = token & 0x7ff;
        uint32_t length = (token >> 24) & 0x7f;
        if (opcode == 0x35) // customdata: the length follows
          length = read<uint32_t>(s.dxbc, i + 4);
        if (!length)
          break;
        // 0x59 dcl_constantbuffer, 0x58 dcl_resource, 0x5a dcl_sampler: the
        // operand's first index (an immediate) is the register.
        if (opcode == 0x59 || opcode == 0x58 || opcode == 0x5a) {
          size_t o = i + 4;
          // Extended tokens, each flagging (bit 31) whether another follows.
          for (bool more = token & 0x80000000u; more && o + 4 <= end; o += 4)
            more = read<uint32_t>(s.dxbc, o) & 0x80000000u;
          const uint32_t operand = read<uint32_t>(s.dxbc, o);
          o += 4;
          for (bool more = operand & 0x80000000u; more && o + 4 <= end; o += 4)
            more = read<uint32_t>(s.dxbc, o) & 0x80000000u;
          ShaderResource res;
          res.bindPoint = read<uint32_t>(s.dxbc, o);
          if (opcode == 0x59) {
            res.type = 0;
            res.name = "cb" + std::to_string(res.bindPoint);
            res.binding = dxvk::computeConstantBufferBinding(stage, res.bindPoint);
          } else if (opcode == 0x58) {
            res.type = 2;
            res.name = "t" + std::to_string(res.bindPoint);
            res.binding = dxvk::computeSrvBinding(stage, res.bindPoint);
          } else {
            res.type = 3;
            res.name = "s" + std::to_string(res.bindPoint);
            res.binding = dxvk::computeSamplerBinding(stage, res.bindPoint);
          }
          s.resources.push_back(std::move(res));
        }
        i += size_t(length) * 4;
      }
    }
    // ISGN: semantic, index and register of each input.
    void parseInputs(Shader &s) {
      size_t at = 0, size = 0;
      if (!chunk(s.dxbc, "ISGN", at, size))
        return;
      const uint32_t count = read<uint32_t>(s.dxbc, at);
      for (uint32_t i = 0; i < count && i < 32; ++i) {
        const size_t e = at + 8 + 24 * i;
        ShaderInput in;
        in.semantic = string(s.dxbc, at + read<uint32_t>(s.dxbc, e));
        in.semanticIndex = read<uint32_t>(s.dxbc, e + 4);
        in.reg = read<uint32_t>(s.dxbc, e + 16);
        s.inputs.push_back(std::move(in));
      }
    }
    std::mutex g_registry;
    std::unordered_map<uint64_t, ShaderPtr> g_byHash;
  } // namespace

  ShaderPtr registerShader(const void *bytecode, size_t bytes, bool vertex) {
    if (!bytecode || bytes < 32)
      return nullptr;
    const auto *b = static_cast<const uint8_t *>(bytecode);
    const uint64_t hash = fnv(b, bytes) ^ (vertex ? 1 : 0);
    std::lock_guard l(g_registry);
    auto &shader = g_byHash[hash];
    if (!shader) {
      shader = std::make_shared<Shader>();
      shader->vertex = vertex;
      shader->hash = hash;
      shader->dxbc.assign(b, b + bytes);
      parseResources(*shader);
      if (shader->resources.empty())
        parseDeclarations(*shader);
      if (vertex)
        parseInputs(*shader);
    }
    return shader;
  }

  bool compile(Shader &shader) {
    if (shader.compiled || shader.failed)
      return shader.compiled;
    try {
      dxvk::DxbcReader reader(reinterpret_cast<const char *>(shader.dxbc.data()), shader.dxbc.size());
      dxvk::DxbcModule module(reader);
      dxvk::DxbcModuleInfo info = {};
      info.options = dxvk::DxbcOptions();
      auto compiled = module.compile(info, shader.vertex ? "vs" : "ps");
      const auto &code = compiled->code();
      shader.spirv.assign(reinterpret_cast<const uint32_t *>(code.data()),
                          reinterpret_cast<const uint32_t *>(code.data()) + code.size() / 4);
      for (const auto &b : compiled->bindings()) {
        uint32_t type = REMIXAPI_HOST_BINDING_UNIFORM_BUFFER_EXT;
        if (b.descriptorType == VK_DESCRIPTOR_TYPE_SAMPLER)
          type = REMIXAPI_HOST_BINDING_SAMPLER_STATE_EXT;
        else if (b.descriptorType == VK_DESCRIPTOR_TYPE_SAMPLED_IMAGE)
          type = REMIXAPI_HOST_BINDING_SAMPLED_IMAGE_EXT;
        else if (b.descriptorType != VK_DESCRIPTOR_TYPE_UNIFORM_BUFFER) {
          shader.failed = true;
          shader.error = "unsupported descriptor type " + std::to_string(int(b.descriptorType));
          return false;
        }
        shader.bindings.push_back({b.resourceBinding, type});
      }
      shader.pushBytes = compiled->info().pushConstSize;
      shader.compiled = true;
    } catch (const dxvk::DxvkError &e) {
      shader.failed = true;
      shader.error = e.message();
    }
    return shader.compiled;
  }

} // namespace urtx
