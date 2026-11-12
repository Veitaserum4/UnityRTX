// The capture window: draws between beginCapture and endCapture are recorded
// with the state they were drawn with, copied (constant buffers, the vertex
// and index data they read, the textures' identities and sampler states).
#include "hostpass.h"
#include <algorithm>
#include <cstdio>
#include <cstring>

namespace urtx {
  namespace {
    bool g_capturing = false;
    uint32_t g_tag = 0;
    std::vector<Draw> g_draws, g_last;
    uint64_t g_captures = 0;

    std::vector<ConstantBuffer> constants(ID3D11DeviceContext *context, ID3D11Buffer *const *bound,
                                          const ShaderPtr &shader) {
      std::vector<ConstantBuffer> out;
      if (!shader)
        return out;
      for (const auto &r : shader->resources) {
        if (r.type != 0 || r.bindPoint >= D3D11_COMMONSHADER_CONSTANT_BUFFER_API_SLOT_COUNT || !bound[r.bindPoint])
          continue;
        ConstantBuffer cb;
        cb.slot = r.bindPoint;
        if (bufferData(context, bound[r.bindPoint], cb.data))
          out.push_back(std::move(cb));
      }
      return out;
    }
  } // namespace

  void beginCapture(uint32_t tag) {
    ++counters().begins;
    repatchContexts();
    std::lock_guard l(lock());
    if (!g_capturing)
      g_draws.clear();
    g_capturing = true;
    g_tag = tag;
  }
  void endCapture() {
    ++counters().ends;
    std::lock_guard l(lock());
    if (!g_capturing)
      return;
    g_capturing = false;
    g_last = std::move(g_draws);
    g_draws.clear();
    ++g_captures;
  }
  bool capturing() { return g_capturing; }

  void recordDraw(ID3D11DeviceContext *context, uint32_t indexCount, uint32_t startIndex, int32_t baseVertex,
                  uint32_t vertexCount, uint32_t startVertex, bool indexed) {
    auto &s = state();
    Draw d;
    d.tag = g_tag;
    d.vs = shaderOf(s.vs);
    d.ps = shaderOf(s.ps);
    if (!d.vs || !d.ps) {
      log("draw tag %u: shaders created before the hooks (vs %p ps %p); skipped", g_tag, s.vs, s.ps);
      return;
    }
    d.topology = s.topology;
    d.viewport = s.viewport;
    d.vsConstants = constants(context, s.vsCb, d.vs);
    d.psConstants = constants(context, s.psCb, d.ps);
    if (const auto *layout = layoutOf(s.layout))
      d.layout = *layout;
    // The vertices the draw reads: indices rebased to the first vertex used.
    uint32_t first = startVertex, last = startVertex + vertexCount;
    if (indexed) {
      std::vector<uint8_t> ib;
      if (!bufferData(context, s.ib, ib))
        return;
      const bool wide = s.ibFormat == DXGI_FORMAT_R32_UINT;
      const size_t stride = wide ? 4 : 2, from = s.ibOffset + size_t(startIndex) * stride;
      if (from + size_t(indexCount) * stride > ib.size())
        return;
      d.indices.resize(indexCount);
      first = UINT32_MAX;
      last = 0;
      for (uint32_t i = 0; i < indexCount; ++i) {
        uint32_t v = 0;
        std::memcpy(&v, ib.data() + from + i * stride, stride);
        v = uint32_t(int64_t(v) + baseVertex);
        d.indices[i] = v;
        first = std::min(first, v);
        last = std::max(last, v + 1);
      }
      for (auto &i : d.indices)
        i -= first;
    } else {
      d.indices.resize(vertexCount);
      for (uint32_t i = 0; i < vertexCount; ++i)
        d.indices[i] = i;
    }
    d.vertexCount = last > first ? last - first : 0;
    for (uint32_t slot = 0; slot < 4; ++slot) {
      if (!s.vb[slot] || !s.vbStride[slot])
        continue;
      std::vector<uint8_t> vb;
      if (!bufferData(context, s.vb[slot], vb))
        continue;
      VertexStream stream;
      stream.slot = slot;
      stream.stride = s.vbStride[slot];
      const size_t from = s.vbOffset[slot] + size_t(first) * stream.stride,
                   to = from + size_t(d.vertexCount) * stream.stride;
      if (to > vb.size())
        continue;
      stream.data.assign(vb.begin() + from, vb.begin() + to);
      d.streams.push_back(std::move(stream));
    }
    for (const auto &r : d.ps->resources) {
      if (r.type == 2 && r.bindPoint < 32 && s.psSrv[r.bindPoint]) {
        Texture t;
        t.slot = r.bindPoint;
        ID3D11Resource *resource = nullptr;
        s.psSrv[r.bindPoint]->GetResource(&resource);
        if (resource) {
          t.resource = resource;
          ID3D11Texture2D *texture = nullptr;
          if (SUCCEEDED(resource->QueryInterface(__uuidof(ID3D11Texture2D), reinterpret_cast<void **>(&texture)))) {
            D3D11_TEXTURE2D_DESC desc{};
            texture->GetDesc(&desc);
            t.width = desc.Width;
            t.height = desc.Height;
            t.format = desc.Format;
            texture->Release();
          }
          resource->Release();
        }
        d.textures.push_back(t);
      } else if (r.type == 3 && r.bindPoint < D3D11_COMMONSHADER_SAMPLER_SLOT_COUNT && s.psSampler[r.bindPoint]) {
        Sampler sm;
        sm.slot = r.bindPoint;
        s.psSampler[r.bindPoint]->GetDesc(&sm.desc);
        d.samplers.push_back(sm);
      }
    }
    compile(*d.vs);
    compile(*d.ps);
    std::lock_guard l(lock());
    g_draws.push_back(std::move(d));
  }

  std::vector<Draw> lastCapture() {
    std::lock_guard l(lock());
    return g_last;
  }

  std::string report() {
    std::lock_guard l(lock());
    std::string out;
    char line[512];
    const auto &c = counters();
    std::snprintf(line, sizeof(line),
                  "%llu captures; the last: %zu draws | events: %llu begins, %llu ends | draws in the window: %llu "
                  "immediate, %llu other contexts | all draws: %llu immediate, %llu other contexts | hooked calls %llu, draw "
                  "rehooks %llu\n",
                  (unsigned long long)g_captures, g_last.size(), (unsigned long long)c.begins,
                  (unsigned long long)c.ends, (unsigned long long)c.windowImmediate, (unsigned long long)c.windowOther,
                  (unsigned long long)c.drawsImmediate, (unsigned long long)c.drawsOther,
                  (unsigned long long)stateCalls(), (unsigned long long)rehooks());
    out += line;
    for (const auto &d : g_last) {
      std::snprintf(line, sizeof(line),
                    "draw tag %u: vs %016llx (%s, %zu bytes SPIR-V) ps %016llx (%s, %zu bytes), %zu indices, %u vertices, "
                    "%zu streams, topology %d, viewport %.0fx%.0f\n",
                    d.tag, (unsigned long long)d.vs->hash, d.vs->compiled ? "compiled" : d.vs->error.c_str(),
                    d.vs->spirv.size() * 4, (unsigned long long)d.ps->hash,
                    d.ps->compiled ? "compiled" : d.ps->error.c_str(), d.ps->spirv.size() * 4, d.indices.size(),
                    d.vertexCount, d.streams.size(), int(d.topology), d.viewport.Width, d.viewport.Height);
      out += line;
      for (const auto *shader : {d.vs.get(), d.ps.get()})
        for (const auto &r : shader->resources) {
          std::snprintf(line, sizeof(line), "  %s %s %s register %u -> binding %u\n", shader->vertex ? "vs" : "ps",
                        r.type == 0 ? "cbuffer" : r.type == 2 ? "texture" : r.type == 3 ? "sampler" : "other",
                        r.name.c_str(), r.bindPoint, r.binding);
          out += line;
        }
      for (const auto &cb : d.vsConstants) {
        std::snprintf(line, sizeof(line), "  vs constants slot %u: %zu bytes\n", cb.slot, cb.data.size());
        out += line;
      }
      for (const auto &cb : d.psConstants) {
        std::snprintf(line, sizeof(line), "  ps constants slot %u: %zu bytes\n", cb.slot, cb.data.size());
        out += line;
      }
      for (const auto &t : d.textures) {
        std::snprintf(line, sizeof(line), "  texture slot %u: %ux%u format %u\n", t.slot, t.width, t.height, t.format);
        out += line;
      }
      for (const auto &e : d.layout) {
        std::snprintf(line, sizeof(line), "  input %s%u format %u slot %u offset %u\n", e.semantic.c_str(),
                      e.semanticIndex, e.format, e.slot, e.offset);
        out += line;
      }
      for (const auto &in : d.vs->inputs) {
        std::snprintf(line, sizeof(line), "  vs reads %s%u at location %u\n", in.semantic.c_str(), in.semanticIndex,
                      in.reg);
        out += line;
      }
    }
    return out;
  }

} // namespace urtx
