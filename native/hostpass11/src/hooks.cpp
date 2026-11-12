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

    // Originals by vtable and slot: the runtime implements a context's (and a
    // device's) interface versions with more than one vtable, and a call
    // through any of them reaches the hook, which forwards it to that vtable's
    // own original.
    std::unordered_map<void **, void *[64]> g_originals;

    // Patches vtable[index] of `object` to `hook` (once per vtable), recording the original.
    void patch(void *object, unsigned index, void *hook) {
      void **vtable = *reinterpret_cast<void ***>(object);
      auto &originals = g_originals[vtable];
      if (vtable[index] == hook)
        return; // already ours
      originals[index] = vtable[index];
      DWORD old = 0;
      VirtualProtect(&vtable[index], sizeof(void *), PAGE_EXECUTE_READWRITE, &old);
      vtable[index] = hook;
      VirtualProtect(&vtable[index], sizeof(void *), old, &old);
    }
    // The original of `slot` for the vtable `self` was called through.
    template <class T> T original(void *self, unsigned slot) {
      return reinterpret_cast<T>(g_originals.at(*reinterpret_cast<void ***>(self))[slot]);
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

    Counters g_counters;
    // The immediate context's interface pointers (ID3D11DeviceContext to ID3D11DeviceContext4).
    void *g_contexts[5] = {};
    uint64_t g_stateCalls = 0;
    bool immediate(ID3D11DeviceContext *c) {
      ++g_stateCalls;
      for (void *p : g_contexts)
        if (p && p == c)
          return true;
      return false;
    }
    // Counts a draw (diagnostics); true when it is to be recorded.
    bool counted(ID3D11DeviceContext *c) {
      const bool mine = immediate(c), window = capturing();
      ++(mine ? g_counters.drawsImmediate : g_counters.drawsOther);
      if (window)
        ++(mine ? g_counters.windowImmediate : g_counters.windowOther);
      return mine && window;
    }

    void rehookDraws(void *c);

    HRESULT STDMETHODCALLTYPE hCreateBuffer(ID3D11Device *d, const D3D11_BUFFER_DESC *desc,
                                            const D3D11_SUBRESOURCE_DATA *init, ID3D11Buffer **out) {
      HRESULT hr = original<CreateBufferFn>(d, 3)(d, desc, init, out);
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
      HRESULT hr = original<CreateInputLayoutFn>(d, 11)(d, elements, count, code, bytes, out);
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
      HRESULT hr = original<CreateVertexShaderFn>(d, 12)(d, code, bytes, linkage, out);
      if (SUCCEEDED(hr) && out && *out) {
        auto shader = registerShader(code, bytes, true);
        std::lock_guard l(g_lock);
        g_shaders[*out] = shader;
      }
      return hr;
    }
    HRESULT STDMETHODCALLTYPE hCreatePixelShader(ID3D11Device *d, const void *code, SIZE_T bytes,
                                                 ID3D11ClassLinkage *linkage, ID3D11PixelShader **out) {
      HRESULT hr = original<CreatePixelShaderFn>(d, 15)(d, code, bytes, linkage, out);
      if (SUCCEEDED(hr) && out && *out) {
        auto shader = registerShader(code, bytes, false);
        std::lock_guard l(g_lock);
        g_shaders[*out] = shader;
      }
      return hr;
    }

    void STDMETHODCALLTYPE hVSSetConstantBuffers(ID3D11DeviceContext *c, UINT start, UINT n, ID3D11Buffer *const *b) {
      original<SetBuffersFn>(c, 7)(c, start, n, b);
      rehookDraws(c);
      if (immediate(c) && b)
        for (UINT i = 0; i < n && start + i < D3D11_COMMONSHADER_CONSTANT_BUFFER_API_SLOT_COUNT; ++i)
          g_state.vsCb[start + i] = b[i];
    }
    void STDMETHODCALLTYPE hPSSetConstantBuffers(ID3D11DeviceContext *c, UINT start, UINT n, ID3D11Buffer *const *b) {
      original<SetBuffersFn>(c, 16)(c, start, n, b);
      rehookDraws(c);
      if (immediate(c) && b)
        for (UINT i = 0; i < n && start + i < D3D11_COMMONSHADER_CONSTANT_BUFFER_API_SLOT_COUNT; ++i)
          g_state.psCb[start + i] = b[i];
    }
    void STDMETHODCALLTYPE hPSSetShaderResources(ID3D11DeviceContext *c, UINT start, UINT n,
                                                 ID3D11ShaderResourceView *const *v) {
      original<SetSrvFn>(c, 8)(c, start, n, v);
      rehookDraws(c);
      if (immediate(c) && v)
        for (UINT i = 0; i < n && start + i < 32; ++i)
          g_state.psSrv[start + i] = v[i];
    }
    void STDMETHODCALLTYPE hPSSetSamplers(ID3D11DeviceContext *c, UINT start, UINT n, ID3D11SamplerState *const *s) {
      original<SetSamplersFn>(c, 10)(c, start, n, s);
      rehookDraws(c);
      if (immediate(c) && s)
        for (UINT i = 0; i < n && start + i < D3D11_COMMONSHADER_SAMPLER_SLOT_COUNT; ++i)
          g_state.psSampler[start + i] = s[i];
    }
    void STDMETHODCALLTYPE hPSSetShader(ID3D11DeviceContext *c, ID3D11PixelShader *s, ID3D11ClassInstance *const *ci,
                                        UINT n) {
      original<SetPsFn>(c, 9)(c, s, ci, n);
      rehookDraws(c);
      if (immediate(c))
        g_state.ps = s;
    }
    void STDMETHODCALLTYPE hVSSetShader(ID3D11DeviceContext *c, ID3D11VertexShader *s,
                                        ID3D11ClassInstance *const *ci, UINT n) {
      original<SetVsFn>(c, 11)(c, s, ci, n);
      rehookDraws(c);
      if (immediate(c))
        g_state.vs = s;
    }
    void STDMETHODCALLTYPE hIASetInputLayout(ID3D11DeviceContext *c, ID3D11InputLayout *l) {
      original<SetLayoutFn>(c, 17)(c, l);
      rehookDraws(c);
      if (immediate(c))
        g_state.layout = l;
    }
    void STDMETHODCALLTYPE hIASetVertexBuffers(ID3D11DeviceContext *c, UINT start, UINT n, ID3D11Buffer *const *b,
                                               const UINT *strides, const UINT *offsets) {
      original<SetVbFn>(c, 18)(c, start, n, b, strides, offsets);
      rehookDraws(c);
      if (immediate(c) && b)
        for (UINT i = 0; i < n && start + i < 4; ++i) {
          g_state.vb[start + i] = b[i];
          g_state.vbStride[start + i] = strides ? strides[i] : 0;
          g_state.vbOffset[start + i] = offsets ? offsets[i] : 0;
        }
    }
    void STDMETHODCALLTYPE hIASetIndexBuffer(ID3D11DeviceContext *c, ID3D11Buffer *b, DXGI_FORMAT f, UINT o) {
      original<SetIbFn>(c, 19)(c, b, f, o);
      rehookDraws(c);
      if (immediate(c)) {
        g_state.ib = b;
        g_state.ibFormat = f;
        g_state.ibOffset = o;
      }
    }
    void STDMETHODCALLTYPE hIASetPrimitiveTopology(ID3D11DeviceContext *c, D3D11_PRIMITIVE_TOPOLOGY t) {
      original<SetTopologyFn>(c, 24)(c, t);
      rehookDraws(c);
      if (immediate(c))
        g_state.topology = t;
    }
    void STDMETHODCALLTYPE hRSSetViewports(ID3D11DeviceContext *c, UINT n, const D3D11_VIEWPORT *v) {
      original<SetViewportsFn>(c, 44)(c, n, v);
      rehookDraws(c);
      if (immediate(c) && n && v)
        g_state.viewport = v[0];
    }
    HRESULT STDMETHODCALLTYPE hMap(ID3D11DeviceContext *c, ID3D11Resource *r, UINT sub, D3D11_MAP type, UINT flags,
                                   D3D11_MAPPED_SUBRESOURCE *mapped) {
      HRESULT hr = original<MapFn>(c, 14)(c, r, sub, type, flags, mapped);
      rehookDraws(c);
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
      original<UnmapFn>(c, 15)(c, r, sub);
      rehookDraws(c);
    }
    void STDMETHODCALLTYPE hUpdateSubresource(ID3D11DeviceContext *c, ID3D11Resource *r, UINT sub, const D3D11_BOX *box,
                                              const void *data, UINT rowPitch, UINT depthPitch) {
      original<UpdateSubresourceFn>(c, 48)(c, r, sub, box, data, rowPitch, depthPitch);
      rehookDraws(c);
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
      if (counted(c))
        recordDraw(c, count, start, base, 0, 0, true);
      original<DrawIndexedFn>(c, 12)(c, count, start, base);
    }
    void STDMETHODCALLTYPE hDraw(ID3D11DeviceContext *c, UINT count, UINT start) {
      if (counted(c))
        recordDraw(c, 0, 0, 0, count, start, false);
      original<DrawFn>(c, 13)(c, count, start);
    }
    void STDMETHODCALLTYPE hDrawIndexedInstanced(ID3D11DeviceContext *c, UINT count, UINT instances, UINT start, INT base,
                                                 UINT startInstance) {
      if (counted(c) && instances == 1)
        recordDraw(c, count, start, base, 0, 0, true);
      original<DrawIndexedInstancedFn>(c, 20)(c, count, instances, start, base, startInstance);
    }
    void STDMETHODCALLTYPE hDrawInstanced(ID3D11DeviceContext *c, UINT count, UINT instances, UINT start,
                                          UINT startInstance) {
      if (counted(c) && instances == 1)
        recordDraw(c, 0, 0, 0, count, start, false);
      original<DrawInstancedFn>(c, 21)(c, count, instances, start, startInstance);
    }

    // d3d11 rewrites the draw slots of the immediate context's (heap) vtable
    // as state changes, switching between its own draw variants; after each
    // state call the draw hooks go back in, forwarding to the variant d3d11
    // just installed.
    uint64_t g_rehooks = 0;
    void rehookDraws(void *c) {
      void **vtable = *reinterpret_cast<void ***>(c);
      auto it = g_originals.find(vtable);
      if (it == g_originals.end())
        return;
      static const struct { unsigned slot; void *hook; } draws[] = {
          {12, reinterpret_cast<void *>(&hDrawIndexed)}, {13, reinterpret_cast<void *>(&hDraw)},
          {20, reinterpret_cast<void *>(&hDrawIndexedInstanced)}, {21, reinterpret_cast<void *>(&hDrawInstanced)}};
      for (const auto &d : draws) {
        void *current = vtable[d.slot];
        if (current == d.hook)
          continue;
        if (g_rehooks++ < 8)
          log("d3d11 replaced draw slot %u: %p -> %p", d.slot, it->second[d.slot], current);
        it->second[d.slot] = current;
        DWORD old = 0;
        VirtualProtect(&vtable[d.slot], sizeof(void *), PAGE_EXECUTE_READWRITE, &old);
        vtable[d.slot] = d.hook;
        VirtualProtect(&vtable[d.slot], sizeof(void *), old, &old);
      }
    }
  } // namespace

  State &state() { return g_state; }
  Counters &counters() { return g_counters; }
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
    HRESULT hr = original<CreateBufferFn>(device, 3)(device, &staging, nullptr, &copy);
    device->Release();
    if (FAILED(hr) || !copy)
      return false;
    context->CopyResource(copy, buffer); // not hooked
    D3D11_MAPPED_SUBRESOURCE mapped{};
    bool ok = SUCCEEDED(original<MapFn>(context, 14)(context, copy, 0, D3D11_MAP_READ, 0, &mapped));
    if (ok) {
      out.assign(static_cast<const uint8_t *>(mapped.pData), static_cast<const uint8_t *>(mapped.pData) + desc.ByteWidth);
      original<UnmapFn>(context, 15)(context, copy, 0);
      std::lock_guard l(g_lock);
      auto &b = g_buffers[buffer];
      b.desc = desc;
      b.data = out;
      b.valid = true;
    }
    copy->Release();
    return ok;
  }

  namespace {
    // The module a code address belongs to (diagnostics).
    std::string moduleOf(void *address) {
      HMODULE module = nullptr;
      char name[MAX_PATH] = "?";
      if (GetModuleHandleExA(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                             static_cast<LPCSTR>(address), &module))
        GetModuleFileNameA(module, name, MAX_PATH);
      return name;
    }
  } // namespace

  // Patches the context's current vtable (once per vtable). The runtime builds
  // a context's vtables on the heap and may switch the object between them, so
  // a capture window patches whichever one the context has when it opens.
  void patchContext(void *c) {
    void **vtable = *reinterpret_cast<void ***>(c);
    if (g_originals.count(vtable))
      return;
    log("context interface %p vtable %p: DrawIndexed %p in %s", c, vtable, vtable[12], moduleOf(vtable[12]).c_str());
    // ID3D11DeviceContext: 7 VSSetConstantBuffers, 8 PSSetShaderResources, 9 PSSetShader,
    // 10 PSSetSamplers, 11 VSSetShader, 12 DrawIndexed, 13 Draw, 14 Map, 15 Unmap,
    // 16 PSSetConstantBuffers, 17 IASetInputLayout, 18 IASetVertexBuffers,
    // 19 IASetIndexBuffer, 20 DrawIndexedInstanced, 21 DrawInstanced,
    // 24 IASetPrimitiveTopology, 44 RSSetViewports, 48 UpdateSubresource.
    patch(c, 7, reinterpret_cast<void *>(&hVSSetConstantBuffers));
    patch(c, 8, reinterpret_cast<void *>(&hPSSetShaderResources));
    patch(c, 9, reinterpret_cast<void *>(&hPSSetShader));
    patch(c, 10, reinterpret_cast<void *>(&hPSSetSamplers));
    patch(c, 11, reinterpret_cast<void *>(&hVSSetShader));
    patch(c, 12, reinterpret_cast<void *>(&hDrawIndexed));
    patch(c, 13, reinterpret_cast<void *>(&hDraw));
    patch(c, 14, reinterpret_cast<void *>(&hMap));
    patch(c, 15, reinterpret_cast<void *>(&hUnmap));
    patch(c, 16, reinterpret_cast<void *>(&hPSSetConstantBuffers));
    patch(c, 17, reinterpret_cast<void *>(&hIASetInputLayout));
    patch(c, 18, reinterpret_cast<void *>(&hIASetVertexBuffers));
    patch(c, 19, reinterpret_cast<void *>(&hIASetIndexBuffer));
    patch(c, 20, reinterpret_cast<void *>(&hDrawIndexedInstanced));
    patch(c, 21, reinterpret_cast<void *>(&hDrawInstanced));
    patch(c, 24, reinterpret_cast<void *>(&hIASetPrimitiveTopology));
    patch(c, 44, reinterpret_cast<void *>(&hRSSetViewports));
    patch(c, 48, reinterpret_cast<void *>(&hUpdateSubresource));
  }

  void repatchContexts() {
    for (void *c : g_contexts)
      if (c)
        patchContext(c);
  }

  bool install(ID3D11Device *device) {
    if (!device)
      return false;
    ID3D11DeviceContext *context = nullptr;
    device->GetImmediateContext(&context);
    if (!context)
      return false;
    g_state.context = context;
    // Every interface version of the device and the immediate context: the
    // runtime may implement them with distinct vtables, and Unity calls
    // through a newer one than GetImmediateContext's.
    void *devices[6] = {device};
    const IID deviceIids[] = {__uuidof(ID3D11Device1), __uuidof(ID3D11Device2), __uuidof(ID3D11Device3),
                              __uuidof(ID3D11Device4), __uuidof(ID3D11Device5)};
    for (int i = 0; i < 5; ++i)
      if (SUCCEEDED(device->QueryInterface(deviceIids[i], &devices[i + 1])))
        static_cast<IUnknown *>(devices[i + 1])->Release(); // the pointers only identify the interfaces
    g_contexts[0] = context;
    const IID contextIids[] = {__uuidof(ID3D11DeviceContext1), __uuidof(ID3D11DeviceContext2),
                               __uuidof(ID3D11DeviceContext3), __uuidof(ID3D11DeviceContext4)};
    for (int i = 0; i < 4; ++i)
      if (SUCCEEDED(context->QueryInterface(contextIids[i], &g_contexts[i + 1])))
        static_cast<IUnknown *>(g_contexts[i + 1])->Release();
    context->Release(); // Unity owns it
    for (void *d : devices) {
      if (!d)
        continue;
      log("device interface %p vtable %p", d, *reinterpret_cast<void **>(d));
      // ID3D11Device: 3 CreateBuffer, 11 CreateInputLayout, 12 CreateVertexShader, 15 CreatePixelShader.
      patch(d, 3, reinterpret_cast<void *>(&hCreateBuffer));
      patch(d, 11, reinterpret_cast<void *>(&hCreateInputLayout));
      patch(d, 12, reinterpret_cast<void *>(&hCreateVertexShader));
      patch(d, 15, reinterpret_cast<void *>(&hCreatePixelShader));
    }
    for (void *c : g_contexts)
      if (c)
        patchContext(c);
    log("installed on device %p, immediate context %p: %zu distinct vtables", device, context, g_originals.size());
    return true;
  }

  uint64_t stateCalls() { return g_stateCalls; }
  uint64_t rehooks() { return g_rehooks; }

  void log(const char *format, ...) {
    if (!g_log) {
      // Beside this DLL (the plugin folder), not in the game's working directory.
      HMODULE self = nullptr;
      wchar_t path[MAX_PATH] = L"UnityRTXHostPass.log";
      if (GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                             reinterpret_cast<LPCWSTR>(&log), &self)) {
        const DWORD n = GetModuleFileNameW(self, path, MAX_PATH);
        if (n && n < MAX_PATH - 4) {
          wchar_t *dot = wcsrchr(path, L'.');
          if (dot)
            wcscpy(dot, L".log");
        }
      }
      g_log = _wfopen(path, L"w");
    }
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
