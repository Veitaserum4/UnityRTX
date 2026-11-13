#pragma once
// UnityRTXHostPass: D3D11 capture of the draws UnityRTX marks, for OpenRemix
// host passes. Unity issues its D3D11 calls on its render thread; the plugin
// marks a capture window there with CommandBuffer.IssuePluginEvent (begin with
// a tag per draw, end), and the hooks record the draws inside it with the
// state they were drawn with. Shaders keep their DXBC (from creation) and
// compile to SPIR-V with the standalone dxbc on first capture.
#include <d3d11_4.h>
#include <cstdint>
#include <memory>
#include <mutex>
#include <string>
#include <unordered_map>
#include <vector>

namespace urtx {

  // A shader as created: its bytecode, resource names by register (RDEF) and,
  // for a vertex shader, its input signature; the SPIR-V once compiled.
  struct ShaderResource {
    std::string name;
    uint32_t type = 0;     // D3D_SHADER_INPUT_TYPE: 0 cbuffer, 2 texture, 3 sampler
    uint32_t bindPoint = 0;
    uint32_t bindCount = 1;
    uint32_t binding = 0;  // the SPIR-V binding the compiled shader uses for it
  };
  struct ShaderInput {
    std::string semantic;
    uint32_t semanticIndex = 0;
    uint32_t reg = 0;      // v#, the SPIR-V input location
  };
  struct ShaderBinding {
    uint32_t binding = 0;
    uint32_t type = 0;     // REMIXAPI_HOST_BINDING_*
  };
  struct Shader {
    bool vertex = false;
    uint64_t hash = 0;
    std::vector<uint8_t> dxbc;
    std::vector<ShaderResource> resources;
    std::vector<ShaderInput> inputs;
    // Compiled (compile()): SPIR-V, its bindings, push constant bytes.
    bool compiled = false, failed = false;
    std::vector<uint32_t> spirv;
    std::vector<ShaderBinding> bindings;
    uint32_t pushBytes = 0;
    std::string error;
  };
  using ShaderPtr = std::shared_ptr<Shader>;

  ShaderPtr registerShader(const void *bytecode, size_t bytes, bool vertex);
  bool compile(Shader &shader);

  // An input layout's elements, as created.
  struct InputElement {
    std::string semantic;
    uint32_t semanticIndex = 0, format = 0, slot = 0, offset = 0;
  };

  // A recorded draw: everything a host pass needs, copied.
  struct ConstantBuffer {
    uint32_t slot = 0;
    std::vector<uint8_t> data;
  };
  struct VertexStream {
    uint32_t slot = 0, stride = 0;
    std::vector<uint8_t> data; // from the bound offset
  };
  struct Texture {
    uint32_t slot = 0;
    void *resource = nullptr;  // the ID3D11Resource the view shows (identity only)
    uint32_t width = 0, height = 0, format = 0;
  };
  struct Sampler {
    uint32_t slot = 0;
    D3D11_SAMPLER_DESC desc{};
  };
  struct Draw {
    uint32_t tag = 0;
    ShaderPtr vs, ps;
    std::vector<ConstantBuffer> vsConstants, psConstants;
    std::vector<InputElement> layout;
    std::vector<VertexStream> streams;
    std::vector<uint32_t> indices; // expanded to 32 bits, rebased by baseVertex
    uint32_t vertexCount = 0;
    D3D11_PRIMITIVE_TOPOLOGY topology = D3D11_PRIMITIVE_TOPOLOGY_UNDEFINED;
    std::vector<Texture> textures;
    std::vector<Sampler> samplers;
    D3D11_VIEWPORT viewport{};
  };

  // Capture (capture.cpp), on the render thread.
  void beginCapture(uint32_t tag);
  void endCapture();
  bool capturing();
  void clearCapture(); // nothing to replay (none of the marked renderers in view)
  void recordDraw(ID3D11DeviceContext *context, uint32_t indexCount, uint32_t startIndex, int32_t baseVertex,
                  uint32_t vertexCount, uint32_t startVertex, bool indexed);
  // The last finished capture's draws (copied out under the lock).
  std::vector<Draw> lastCapture();
  std::string report();

  // Context state the hooks track (immediate context only).
  struct State {
    ID3D11DeviceContext *context = nullptr;
    void *vs = nullptr, *ps = nullptr;
    ID3D11Buffer *vsCb[D3D11_COMMONSHADER_CONSTANT_BUFFER_API_SLOT_COUNT] = {};
    ID3D11Buffer *psCb[D3D11_COMMONSHADER_CONSTANT_BUFFER_API_SLOT_COUNT] = {};
    ID3D11ShaderResourceView *psSrv[32] = {};
    ID3D11SamplerState *psSampler[D3D11_COMMONSHADER_SAMPLER_SLOT_COUNT] = {};
    void *layout = nullptr;
    ID3D11Buffer *vb[4] = {};
    UINT vbStride[4] = {}, vbOffset[4] = {};
    ID3D11Buffer *ib = nullptr;
    DXGI_FORMAT ibFormat = DXGI_FORMAT_UNKNOWN;
    UINT ibOffset = 0;
    D3D11_PRIMITIVE_TOPOLOGY topology = D3D11_PRIMITIVE_TOPOLOGY_UNDEFINED;
    D3D11_VIEWPORT viewport{};
  };
  State &state();
  std::mutex &lock();

  // Objects created through the hooked device (hooks.cpp).
  ShaderPtr shaderOf(void *shader);
  const std::vector<InputElement> *layoutOf(void *layout);
  // A buffer's CPU copy: kept for constant buffers (creation data, maps,
  // updates), read back on demand for vertex and index buffers.
  bool bufferData(ID3D11DeviceContext *context, ID3D11Buffer *buffer, std::vector<uint8_t> &out);

  // Diagnostics: plugin events and draws the hooks saw.
  struct Counters {
    uint64_t begins = 0, ends = 0, windowImmediate = 0, windowOther = 0, drawsImmediate = 0, drawsOther = 0;
  };
  Counters &counters();
  uint64_t stateCalls(); // every call through a hooked context slot
  uint64_t rehooks();    // draw slots d3d11 rewrote and the hooks took back

  // Replay (replay.cpp): the last capture as OpenRemix host passes.
  bool bindRemix(HMODULE remix);
  void setFrameTexture(void *resource);                     // the texture the frame replaces
  void setProjection(float p22, float p23, bool reversedZ); // the capture camera's GPU projection
  void setTexture(void *resource, uint32_t width, uint32_t height, uint32_t vkFormat, const void *rgba8);
  int missingTextures(void **resources, int *sizes, int max); // sampled, neither the frame nor uploaded
  // Blood stains drawn over the frame: quads of (x, y, z, u, v) vertices (main
  // thread, when they change) sampling `atlas` (uploaded with setTexture), in
  // the view-projection of the frame being presented (its thread, before
  // submit; null: none this frame).
  void setStains(const float *vertices, size_t quads, void *atlas);
  void setStainView(const float rows[16]);
  int submit(); // the presenting thread, before each present: passes submitted
  std::string submitReport();

  bool install(ID3D11Device *device);
  void repatchContexts(); // render thread: patch the context's current vtable if new
  void log(const char *format, ...);

} // namespace urtx
