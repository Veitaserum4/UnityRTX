// Vtable hooks on Unity's D3D11 device and immediate context. The device's
// creation calls keep what the capture needs later (shader bytecode, input
// layouts, constant buffer contents); the context's state calls are tracked
// on the immediate context; its draws are recorded inside a capture window.
#include "hostpass.h"
#include <cstdarg>
#include <cstdio>
#include <cstring>
#include <windows.h>

namespace urtx {
  namespace {
    std::mutex g_lock;
    State g_state;
    std::unordered_map<void *, ShaderPtr> g_shaders;
    std::unordered_map<void *, std::vector<InputElement>> g_layouts;
    struct BufferShadow {
      D3D11_BUFFER_DESC desc{};
      std::vector<uint8_t> data;
      bool valid = false;
      void *mapped = nullptr; // a WRITE map in progress
    };
    std::unordered_map<ID3D11Buffer *, BufferShadow> g_buffers;
    FILE *g_log = nullptr;

    // Patches vtable[index] to `hook`, returning the original.
    template <class T> T patch(void *object, unsigned index, T hook) {
      void **vtable = *reinterpret_cast<void ***>(object);
      DWORD old = 0;
      VirtualProtect(&vtable[index], sizeof(void *), PAGE_EXECUTE_READWRITE, &old);
      T original = reinterpret_cast<T>(vtable[index]);
      if (vtable[index] != reinterpret_cast<void *>(hook))
        vtable[index] = reinterpret_cast<void *>(hook);
      else
        original = nullptr; // already ours: never chain to ourselves
      VirtualProtect(&vtable[index], sizeof(void *), old, &old);
      return original;
    }

    // ID3D11Device slots.
    using CreateBufferFn = HRESULT(STDMETHODCALLTYPE *)(ID3D11Device *, const D3D11_BUFFER_DESC *,
                                                        const D3D11_SUBRESOURCE_DATA *, ID3D11Buffer **);
    using CreateInputLayoutFn = HRESULT(STDMETHODCALLTYPE *)(ID3D11Device *, const D3D11_INPUT_ELEMENT_DESC *, UINT,
                                                             const void *, SIZE_T, ID3D11InputLayout **);
    using CreateVertexShaderFn = HRESULT(STDMETHODCALLTYPE *)(ID3D11Device *, const void *, SIZE_T,
                                                              ID3D11ClassLinkage *, ID3D11VertexShader **);
    using CreatePixelShaderFn = HRESULT(STDMETHODCALLTYPE *)(ID3D11Device *, const void *, SIZE_T,
                                                             ID3D11ClassLinkage *, ID3D11PixelShader **);
    CreateBufferFn o_CreateBuffer;
    CreateInputLayoutFn o_CreateInputLayout;
    CreateVertexShaderFn o_CreateVertexShader;
    CreatePixelShaderFn o_CreatePixelShader;

    // ID3D11DeviceContext slots.
    using SetBuffersFn = void(STDMETHODCALLTYPE *)(ID3D11DeviceContext *, UINT, UINT, ID3D11Buffer *const *);
    using SetSrvFn = void(STDMETHODCALLTYPE *)(ID3D11DeviceContext *, UINT, UINT,
                                               ID3D11ShaderResourceView *const *);
    using SetSamplersFn = void(STDMETHODCALLTYPE *)(ID3D11DeviceContext *, UINT, UINT, ID3D11SamplerState *const *);
    using SetPsFn = void(STDMETHODCALLTYPE *)(ID3D11DeviceContext *, ID3D11PixelShader *,
                                              ID3D11ClassInstance *const *, UINT);
    using SetVsFn = void(STDMETHODCALLTYPE *)(ID3D11DeviceContext *, ID3D11VertexShader *,
                                              ID3D11ClassInstance *const *, UINT);
    using DrawIndexedFn = void(STDMETHODCALLTYPE *)(ID3D11DeviceContext *, UINT, UINT, INT);
    using DrawFn = void(STDMETHODCALLTYPE *)(ID3D11DeviceContext *, UINT, UINT);
    using MapFn = HRESULT(STDMETHODCALLTYPE *)(ID3D11DeviceContext *, ID3D11Resource *, UINT, D3D11_MAP, UINT,
                                               D3D11_MAPPED_SUBRESOURCE *);
    using UnmapFn = void(STDMETHODCALLTYPE *)(ID3D11DeviceContext *, ID3D11Resource *, UINT);
    using SetLayoutFn = void(STDMETHODCALLTYPE *)(ID3D11DeviceContext *, ID3D11InputLayout *);
    using SetVbFn = void(STDMETHODCALLTYPE *)(ID3D11DeviceContext *, UINT, UINT, ID3D11Buffer *const *,
                                              const UINT *, const UINT *);
    using SetIbFn = void(STDMETHODCALLTYPE *)(ID3D11DeviceContext *, ID3D11Buffer *, DXGI_FORMAT, UINT);
    using DrawIndexedInstancedFn = void(STDMETHODCALLTYPE *)(ID3D11DeviceContext *, UINT, UINT, UINT, INT, UINT);
    using DrawInstancedFn = void(STDMETHODCALLTYPE *)(ID3D11DeviceContext *, UINT, UINT, UINT, UINT);
    using SetTopologyFn = void(STDMETHODCALLTYPE *)(ID3D11DeviceContext *, D3D11_PRIMITIVE_TOPOLOGY);
    using SetViewportsFn = void(STDMETHODCALLTYPE *)(ID3D11DeviceContext *, UINT, const D3D11_VIEWPORT *);
    using UpdateSubresourceFn = void(STDMETHODCALLTYPE *)(ID3D11DeviceContext *, ID3D11Resource *, UINT,
                                                          const D3D11_BOX *, const void *, UINT, UINT);
    using CopyResourceFn = void(STDMETHODCALLTYPE *)(ID3D11DeviceContext *, ID3D11Resource *, ID3D11Resource *);
    SetBuffersFn o_VSSetConstantBuffers, o_PSSetConstantBuffers;
    SetSrvFn o_PSSetShaderResources;
    SetSamplersFn o_PSSetSamplers;
    SetPsFn o_PSSetShader;
    SetVsFn o_VSSetShader;
    DrawIndexedFn o_DrawIndexed;
    DrawFn o_Draw;
    MapFn o_Map;
    UnmapFn o_Unmap;
    SetLayoutFn o_IASetInputLayout;
    SetVbFn o_IASetVertexBuffers;
    SetIbFn o_IASetIndexBuffer;
    DrawIndexedInstancedFn o_DrawIndexedInstanced;
    DrawInstancedFn o_DrawInstanced;
    SetTopologyFn o_IASetPrimitiveTopology;
    SetViewportsFn o_RSSetViewports;
    UpdateSubresourceFn o_UpdateSubresource;
    CopyResourceFn o_CopyResource; // not hooked: the readback's own copy

    bool immediate(ID3D11DeviceContext *c) { return c == g_state.context; }

    HRESULT STDMETHODCALLTYPE hCreateBuffer(ID3D11Device *d, const D3D11_BUFFER_DESC *desc,
                                            const D3D11_SUBRESOURCE_DATA *init, ID3D11Buffer **out) {
      HRESULT hr = o_CreateBuffer(d, desc, init, out);
      if (SUCCEEDED(hr) && out && *out && desc && (desc->BindFlags & D3D11_BIND_CONSTANT_BUFFER)) {
        std::lock_guard l(g_lock);
        auto &b = g_buffers[*out];
        b = {};
        b.desc = *desc;
        b.data.assign(desc->ByteWidth, 0);
        if (init && init->pSysMem)
          std::memcpy(b.data.data(), init->pSysMem, desc->ByteWidth);
        b.valid = true;
      }
      return hr;
    }
    HRESULT STDMETHODCALLTYPE hCreateInputLayout(ID3D11Device *d, const D3D11_INPUT_ELEMENT_DESC *elements, UINT count,
                                                 const void *code, SIZE_T bytes, ID3D11InputLayout **out) {
      HRESULT hr = o_CreateInputLayout(d, elements, count, code, bytes, out);
      if (SUCCEEDED(hr) && out && *out) {
        std::vector<InputElement> layout;
        uint32_t offsets[D3D11_IA_VERTEX_INPUT_RESOURCE_SLOT_COUNT] = {};
        for (UINT i = 0; i < count; ++i) {
          InputElement e;
          e.semantic = elements[i].SemanticName ? elements[i].SemanticName : "";
          e.semanticIndex = elements[i].SemanticIndex;
          e.format = elements[i].Format;
          e.slot = elements[i].InputSlot;
          e.offset = elements[i].AlignedByteOffset == D3D11_APPEND_ALIGNED_ELEMENT ? offsets[e.slot]
                                                                                   : elements[i].AlignedByteOffset;
          // Formats Unity's meshes use: 32-bit floats, 8-bit and 16-bit normalized.
          uint32_t size = 0;
          switch (elements[i].Format) {
          case DXGI_FORMAT_R32G32B32A32_FLOAT: size = 16; break;
          case DXGI_FORMAT_R32G32B32_FLOAT: size = 12; break;
          case DXGI_FORMAT_R32G32_FLOAT: case DXGI_FORMAT_R16G16B16A16_FLOAT: case DXGI_FORMAT_R16G16B16A16_UNORM:
          case DXGI_FORMAT_R16G16B16A16_SNORM: size = 8; break;
          default: size = 4; break;
          }
          offsets[e.slot] = e.offset + size;
          layout.push_back(std::move(e));
        }
        std::lock_guard l(g_lock);
        g_layouts[*out] = std::move(layout);
      }
      return hr;
    }
    HRESULT STDMETHODCALLTYPE hCreateVertexShader(ID3D11Device *d, const void *code, SIZE_T bytes,
                                                  ID3D11ClassLinkage *linkage, ID3D11VertexShader **out) {
      HRESULT hr = o_CreateVertexShader(d, code, bytes, linkage, out);
      if (SUCCEEDED(hr) && out && *out) {
        auto shader = registerShader(code, bytes, true);
        std::lock_guard l(g_lock);
        g_shaders[*out] = shader;
      }
      return hr;
    }
    HRESULT STDMETHODCALLTYPE hCreatePixelShader(ID3D11Device *d, const void *code, SIZE_T bytes,
                                                 ID3D11ClassLinkage *linkage, ID3D11PixelShader **out) {
      HRESULT hr = o_CreatePixelShader(d, code, bytes, linkage, out);
      if (SUCCEEDED(hr) && out && *out) {
        auto shader = registerShader(code, bytes, false);
        std::lock_guard l(g_lock);
        g_shaders[*out] = shader;
      }
      return hr;
    }

    void STDMETHODCALLTYPE hVSSetConstantBuffers(ID3D11DeviceContext *c, UINT start, UINT n, ID3D11Buffer *const *b) {
      o_VSSetConstantBuffers(c, start, n, b);
      if (immediate(c) && b)
        for (UINT i = 0; i < n && start + i < D3D11_COMMONSHADER_CONSTANT_BUFFER_API_SLOT_COUNT; ++i)
          g_state.vsCb[start + i] = b[i];
    }
    void STDMETHODCALLTYPE hPSSetConstantBuffers(ID3D11DeviceContext *c, UINT start, UINT n, ID3D11Buffer *const *b) {
      o_PSSetConstantBuffers(c, start, n, b);
      if (immediate(c) && b)
        for (UINT i = 0; i < n && start + i < D3D11_COMMONSHADER_CONSTANT_BUFFER_API_SLOT_COUNT; ++i)
          g_state.psCb[start + i] = b[i];
    }
    void STDMETHODCALLTYPE hPSSetShaderResources(ID3D11DeviceContext *c, UINT start, UINT n,
                                                 ID3D11ShaderResourceView *const *v) {
      o_PSSetShaderResources(c, start, n, v);
      if (immediate(c) && v)
        for (UINT i = 0; i < n && start + i < 32; ++i)
          g_state.psSrv[start + i] = v[i];
    }
    void STDMETHODCALLTYPE hPSSetSamplers(ID3D11DeviceContext *c, UINT start, UINT n, ID3D11SamplerState *const *s) {
      o_PSSetSamplers(c, start, n, s);
      if (immediate(c) && s)
        for (UINT i = 0; i < n && start + i < D3D11_COMMONSHADER_SAMPLER_SLOT_COUNT; ++i)
          g_state.psSampler[start + i] = s[i];
    }
    void STDMETHODCALLTYPE hPSSetShader(ID3D11DeviceContext *c, ID3D11PixelShader *s, ID3D11ClassInstance *const *ci,
                                        UINT n) {
      o_PSSetShader(c, s, ci, n);
      if (immediate(c))
        g_state.ps = s;
    }
    void STDMETHODCALLTYPE hVSSetShader(ID3D11DeviceContext *c, ID3D11VertexShader *s,
                                        ID3D11ClassInstance *const *ci, UINT n) {
      o_VSSetShader(c, s, ci, n);
      if (immediate(c))
        g_state.vs = s;
    }
    void STDMETHODCALLTYPE hIASetInputLayout(ID3D11DeviceContext *c, ID3D11InputLayout *l) {
      o_IASetInputLayout(c, l);
      if (immediate(c))
        g_state.layout = l;
    }
    void STDMETHODCALLTYPE hIASetVertexBuffers(ID3D11DeviceContext *c, UINT start, UINT n, ID3D11Buffer *const *b,
                                               const UINT *strides, const UINT *offsets) {
      o_IASetVertexBuffers(c, start, n, b, strides, offsets);
      if (immediate(c) && b)
        for (UINT i = 0; i < n && start + i < 4; ++i) {
          g_state.vb[start + i] = b[i];
          g_state.vbStride[start + i] = strides ? strides[i] : 0;
          g_state.vbOffset[start + i] = offsets ? offsets[i] : 0;
        }
    }
    void STDMETHODCALLTYPE hIASetIndexBuffer(ID3D11DeviceContext *c, ID3D11Buffer *b, DXGI_FORMAT f, UINT o) {
      o_IASetIndexBuffer(c, b, f, o);
      if (immediate(c)) {
        g_state.ib = b;
        g_state.ibFormat = f;
        g_state.ibOffset = o;
      }
    }
    void STDMETHODCALLTYPE hIASetPrimitiveTopology(ID3D11DeviceContext *c, D3D11_PRIMITIVE_TOPOLOGY t) {
      o_IASetPrimitiveTopology(c, t);
      if (immediate(c))
        g_state.topology = t;
    }
    void STDMETHODCALLTYPE hRSSetViewports(ID3D11DeviceContext *c, UINT n, const D3D11_VIEWPORT *v) {
      o_RSSetViewports(c, n, v);
      if (immediate(c) && n && v)
        g_state.viewport = v[0];
    }
    HRESULT STDMETHODCALLTYPE hMap(ID3D11DeviceContext *c, ID3D11Resource *r, UINT sub, D3D11_MAP type, UINT flags,
                                   D3D11_MAPPED_SUBRESOURCE *mapped) {
      HRESULT hr = o_Map(c, r, sub, type, flags, mapped);
      if (SUCCEEDED(hr) && mapped && type != D3D11_MAP_READ) {
        std::lock_guard l(g_lock);
        auto it = g_buffers.find(reinterpret_cast<ID3D11Buffer *>(r));
        if (it != g_buffers.end())
          it->second.mapped = mapped->pData;
      }
      return hr;
    }
    void STDMETHODCALLTYPE hUnmap(ID3D11DeviceContext *c, ID3D11Resource *r, UINT sub) {
      {
        std::lock_guard l(g_lock);
        auto it = g_buffers.find(reinterpret_cast<ID3D11Buffer *>(r));
        if (it != g_buffers.end() && it->second.mapped) {
          auto &b = it->second;
          if (b.desc.BindFlags & D3D11_BIND_CONSTANT_BUFFER) {
            std::memcpy(b.data.data(), b.mapped, b.data.size());
            b.valid = true;
          } else {
            b.valid = false; // read back again when a capture needs it
          }
          b.mapped = nullptr;
        }
      }
      o_Unmap(c, r, sub);
    }
    void STDMETHODCALLTYPE hUpdateSubresource(ID3D11DeviceContext *c, ID3D11Resource *r, UINT sub, const D3D11_BOX *box,
                                              const void *data, UINT rowPitch, UINT depthPitch) {
      o_UpdateSubresource(c, r, sub, box, data, rowPitch, depthPitch);
      std::lock_guard l(g_lock);
      auto it = g_buffers.find(reinterpret_cast<ID3D11Buffer *>(r));
      if (it == g_buffers.end() || !data)
        return;
      auto &b = it->second;
      if (b.desc.BindFlags & D3D11_BIND_CONSTANT_BUFFER) {
        const uint32_t from = box ? box->left : 0, to = box ? box->right : uint32_t(b.data.size());
        if (to <= b.data.size() && from < to)
          std::memcpy(b.data.data() + from, data, to - from);
        b.valid = true;
      } else {
        b.valid = false;
      }
    }
    void STDMETHODCALLTYPE hDrawIndexed(ID3D11DeviceContext *c, UINT count, UINT start, INT base) {
      if (immediate(c) && capturing())
        recordDraw(c, count, start, base, 0, 0, true);
      o_DrawIndexed(c, count, start, base);
    }
    void STDMETHODCALLTYPE hDraw(ID3D11DeviceContext *c, UINT count, UINT start) {
      if (immediate(c) && capturing())
        recordDraw(c, 0, 0, 0, count, start, false);
      o_Draw(c, count, start);
    }
    void STDMETHODCALLTYPE hDrawIndexedInstanced(ID3D11DeviceContext *c, UINT count, UINT instances, UINT start, INT base,
                                                 UINT startInstance) {
      if (immediate(c) && capturing() && instances == 1)
        recordDraw(c, count, start, base, 0, 0, true);
      o_DrawIndexedInstanced(c, count, instances, start, base, startInstance);
    }
    void STDMETHODCALLTYPE hDrawInstanced(ID3D11DeviceContext *c, UINT count, UINT instances, UINT start,
                                          UINT startInstance) {
      if (immediate(c) && capturing() && instances == 1)
        recordDraw(c, 0, 0, 0, count, start, false);
      o_DrawInstanced(c, count, instances, start, startInstance);
    }
  } // namespace

  State &state() { return g_state; }
  std::mutex &lock() { return g_lock; }

  ShaderPtr shaderOf(void *shader) {
    std::lock_guard l(g_lock);
    auto it = g_shaders.find(shader);
    return it == g_shaders.end() ? nullptr : it->second;
  }
  const std::vector<InputElement> *layoutOf(void *layout) {
    std::lock_guard l(g_lock);
    auto it = g_layouts.find(layout);
    return it == g_layouts.end() ? nullptr : &it->second;
  }

  bool bufferData(ID3D11DeviceContext *context, ID3D11Buffer *buffer, std::vector<uint8_t> &out) {
    if (!buffer)
      return false;
    {
      std::lock_guard l(g_lock);
      auto it = g_buffers.find(buffer);
      if (it != g_buffers.end() && it->second.valid) {
        out = it->second.data;
        return true;
      }
    }
    // Read back through a staging copy (once, until the buffer changes).
    D3D11_BUFFER_DESC desc{};
    buffer->GetDesc(&desc);
    ID3D11Device *device = nullptr;
    buffer->GetDevice(&device);
    if (!device)
      return false;
    D3D11_BUFFER_DESC staging{desc.ByteWidth, D3D11_USAGE_STAGING, 0, D3D11_CPU_ACCESS_READ, 0, 0};
    ID3D11Buffer *copy = nullptr;
    HRESULT hr = o_CreateBuffer(device, &staging, nullptr, &copy);
    device->Release();
    if (FAILED(hr) || !copy)
      return false;
    o_CopyResource(context, copy, buffer);
    D3D11_MAPPED_SUBRESOURCE mapped{};
    bool ok = SUCCEEDED(o_Map(context, copy, 0, D3D11_MAP_READ, 0, &mapped));
    if (ok) {
      out.assign(static_cast<const uint8_t *>(mapped.pData), static_cast<const uint8_t *>(mapped.pData) + desc.ByteWidth);
      o_Unmap(context, copy, 0);
      std::lock_guard l(g_lock);
      auto &b = g_buffers[buffer];
      b.desc = desc;
      b.data = out;
      b.valid = true;
    }
    copy->Release();
    return ok;
  }

  bool install(ID3D11Device *device) {
    if (!device)
      return false;
    ID3D11DeviceContext *context = nullptr;
    device->GetImmediateContext(&context);
    if (!context)
      return false;
    g_state.context = context;
    context->Release(); // Unity owns it; the pointer only identifies it
    void **contextVtable = *reinterpret_cast<void ***>(context);
    o_CopyResource = reinterpret_cast<CopyResourceFn>(contextVtable[47]);
    // ID3D11Device: 3 CreateBuffer, 11 CreateInputLayout, 12 CreateVertexShader, 15 CreatePixelShader.
    o_CreateBuffer = patch(device, 3, &hCreateBuffer);
    o_CreateInputLayout = patch(device, 11, &hCreateInputLayout);
    o_CreateVertexShader = patch(device, 12, &hCreateVertexShader);
    o_CreatePixelShader = patch(device, 15, &hCreatePixelShader);
    // ID3D11DeviceContext: 7 VSSetConstantBuffers, 8 PSSetShaderResources, 9 PSSetShader,
    // 10 PSSetSamplers, 11 VSSetShader, 12 DrawIndexed, 13 Draw, 14 Map, 15 Unmap,
    // 16 PSSetConstantBuffers, 17 IASetInputLayout, 18 IASetVertexBuffers,
    // 19 IASetIndexBuffer, 20 DrawIndexedInstanced, 21 DrawInstanced,
    // 24 IASetPrimitiveTopology, 44 RSSetViewports, 48 UpdateSubresource.
    o_VSSetConstantBuffers = patch(context, 7, &hVSSetConstantBuffers);
    o_PSSetShaderResources = patch(context, 8, &hPSSetShaderResources);
    o_PSSetShader = patch(context, 9, &hPSSetShader);
    o_PSSetSamplers = patch(context, 10, &hPSSetSamplers);
    o_VSSetShader = patch(context, 11, &hVSSetShader);
    o_DrawIndexed = patch(context, 12, &hDrawIndexed);
    o_Draw = patch(context, 13, &hDraw);
    o_Map = patch(context, 14, &hMap);
    o_Unmap = patch(context, 15, &hUnmap);
    o_PSSetConstantBuffers = patch(context, 16, &hPSSetConstantBuffers);
    o_IASetInputLayout = patch(context, 17, &hIASetInputLayout);
    o_IASetVertexBuffers = patch(context, 18, &hIASetVertexBuffers);
    o_IASetIndexBuffer = patch(context, 19, &hIASetIndexBuffer);
    o_DrawIndexedInstanced = patch(context, 20, &hDrawIndexedInstanced);
    o_DrawInstanced = patch(context, 21, &hDrawInstanced);
    o_IASetPrimitiveTopology = patch(context, 24, &hIASetPrimitiveTopology);
    o_RSSetViewports = patch(context, 44, &hRSSetViewports);
    o_UpdateSubresource = patch(context, 48, &hUpdateSubresource);
    const bool ok = o_CreateBuffer && o_CreateInputLayout && o_CreateVertexShader && o_CreatePixelShader &&
                    o_DrawIndexed && o_Draw && o_Map && o_Unmap;
    log("installed on device %p, immediate context %p: %s", device, context, ok ? "ok" : "already hooked");
    return ok;
  }

  void log(const char *format, ...) {
    if (!g_log)
      g_log = std::fopen("UnityRTXHostPass.log", "w");
    if (!g_log)
      return;
    va_list args;
    va_start(args, format);
    std::vfprintf(g_log, format, args);
    va_end(args);
    std::fputc('\n', g_log);
    std::fflush(g_log);
  }

} // namespace urtx
