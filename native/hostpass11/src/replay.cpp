// Replay: the last capture's draws as OpenRemix host passes, over the
// path-traced frame, on the thread that presents it (UnityRTX's Remix render
// thread), before each present.
//
// Each frame: the frame is copied to a host target (what the draws sample in
// place of the texture the plugin names the frame), the frame's depth is
// filled from the runtime's view depth in the capture camera's projection,
// and the draws follow, depth-tested. Unity renders the capture into a render
// texture, upside down on D3D (its projection flips y, _ProjectionParams.x is
// -1): the draws are replayed with the viewport flipped back, and their
// _ProjectionParams.x set to 1 so their screen positions address the frame
// copy, row 0 at the top, rather than a Unity render texture.
#include "hostpass.h"
#include <remix/remix_native_ext.h>
#include <algorithm>
#include <cstdio>
#include <cstring>
#include <unordered_set>
#include <windows.h>

#include "fill.frag.inc"
#include "glass.frag.inc"
#include "flip.frag.inc"
#include "frame_depth.frag.inc"
#include "fullscreen.vert.inc"
#include "stain.frag.inc"
#include "stain.vert.inc"
#include "wall_mask.frag.inc"
#include "wall_mask_glass.frag.inc"

namespace urtx {
  namespace {
    PFN_remixapi_RegisterHostShaderEXT g_register = nullptr;
    PFN_remixapi_SubmitHostPassesEXT g_submit = nullptr;
    PFN_remixapi_UpdateHostTargetEXT g_updateTarget = nullptr;
    PFN_remixapi_UpdateHostVolumeEXT g_updateVolume = nullptr; // optional: volumes stay missing without it
    PFN_remixapi_GetHostPassStatisticsEXT g_statistics = nullptr;

    constexpr remixapi_HostTargetHandleEXT kFrameCopy = 0x5552000000000001ull;
    // The composed frame (walls and stains drawn) for screen effects to sample.
    constexpr remixapi_HostTargetHandleEXT kScreenCopy = 0x5552000000000004ull;
    // What is behind the walls: the frame where they show, holes (where
    // something stands in front of them) filled from beside (ping-pong).
    constexpr remixapi_HostTargetHandleEXT kBehind[2] = {0x5552000000000002ull, 0x5552000000000003ull};
    // The frame upside down (flip.frag), for draws sampling the _GrabTexture stand-in.
    constexpr remixapi_HostTargetHandleEXT kGrabCopy = 0x5552000000000005ull;
    // The walls' pixels (alpha 1) no other surface stands in front of: where
    // glass.frag puts the runtime's sheets back over them.
    constexpr remixapi_HostTargetHandleEXT kCover = 0x5552000000000006ull;
#ifndef REMIXAPI_HOST_TARGET_VIEW_TRANSMITTANCE_EXT
#define REMIXAPI_HOST_TARGET_VIEW_TRANSMITTANCE_EXT ((remixapi_HostTargetHandleEXT)0xffffffff00000003ull)
#define REMIXAPI_HOST_TARGET_VIEW_OPAQUE_DEPTH_EXT ((remixapi_HostTargetHandleEXT)0xffffffff00000004ull)
#endif
    // Whether the runtime has its sheets' transmittance and the depth behind
    // them (REMIXAPI_HOST_TARGET_VIEW_TRANSMITTANCE_EXT, _VIEW_OPAQUE_DEPTH_EXT).
    // One without them refuses the list naming them: the walls are then
    // replayed as before, hidden behind sheets.
    bool g_viewLayers = true;
    int g_layersView = 0; // setLayersView
    constexpr uint32_t kVkR32G32Sfloat = 103;
    constexpr uint32_t kCompareLessOrEqual = 3, kCompareGreaterOrEqual = 6, kCompareAlways = 7; // VkCompareOp

    // From the plugin (main thread), read on the render thread.
    std::mutex g_settingsLock;
    void *g_frameTexture = nullptr; // the resource the draws sample as the frame
    void *g_grabTexture = nullptr;  // another (the _GrabTexture stand-in)
    void *g_screenSource = nullptr; // the resource screen effects sample as the frame
    uint32_t g_screenSize[2] = {};  // the frame's scale for them (0: as it is)
    uint32_t g_screenFrame[2] = {}; // the frame they cover (the capture's target)
    float g_projection[2] = {};     // the capture camera's GPU projection: P22, P23
    bool g_reversedZ = true;
    struct PendingTexture {
      uint32_t width = 0, height = 0, depth = 1, vkFormat = 0;
      std::vector<uint8_t> pixels;
    };
    std::unordered_map<void *, PendingTexture> g_pending; // to upload
    std::unordered_set<void *> g_uploaded;                // render thread's view: uploaded to the runtime

    // Registered shaders, by the capture's shader identity.
    std::unordered_map<const Shader *, remixapi_HostShaderHandleEXT> g_handles;
    std::unordered_set<const Shader *> g_failed;
    remixapi_HostShaderHandleEXT g_fullscreenVs = 0, g_frameDepthFs = 0, g_maskFs = 0, g_fillFs = 0, g_flipFs = 0,
                                 g_maskGlassFs = 0, g_glassFs = 0;
    bool g_depthFailed = false;

    std::string g_lastSubmit = "nothing submitted yet";

    // Blood stains (setStains, main thread; setStainView, the presenting
    // thread): quads of (x, y, z, u, v) vertices, the atlas they sample (a
    // texture uploaded with setTexture) and the path-traced camera's
    // view-projection rows for the frame being presented.
    constexpr uint32_t kVkR32G32B32Sfloat = 106;
    std::shared_ptr<const std::vector<float>> g_stainVertices;
    void *g_stainAtlas = nullptr;
    float g_stainView[16] = {};
    bool g_stainViewSet = false;
    remixapi_HostShaderHandleEXT g_stainVs = 0, g_stainFs = 0;
    bool g_stainFailed = false;

    bool uploaded(void *resource) {
      std::lock_guard l(g_settingsLock);
      return g_uploaded.count(resource) != 0;
    }

    remixapi_HostShaderHandleEXT handleOf(const Shader &shader) {
      auto it = g_handles.find(&shader);
      if (it != g_handles.end())
        return it->second;
      if (g_failed.count(&shader) || !shader.compiled)
        return 0;
      std::vector<remixapi_HostShaderBindingEXT> bindings;
      for (const auto &b : shader.bindings)
        bindings.push_back({b.binding, b.type});
      remixapi_HostShaderInfoEXT info{};
      info.sType = REMIXAPI_STRUCT_TYPE_HOST_SHADER_INFO_EXT;
      info.stage = shader.vertex ? REMIXAPI_HOST_SHADER_VERTEX_EXT : REMIXAPI_HOST_SHADER_FRAGMENT_EXT;
      info.code = shader.spirv.data();
      info.codeBytes = shader.spirv.size() * 4;
      info.bindings = bindings.data();
      info.bindingCount = uint32_t(bindings.size());
      info.pushConstantBytes = shader.pushBytes;
      remixapi_HostShaderHandleEXT handle = 0;
      const auto error = g_register(&info, &handle);
      if (error != REMIXAPI_ERROR_CODE_SUCCESS || !handle) {
        log("registering %s shader %016llx failed: %d", shader.vertex ? "vertex" : "pixel",
            (unsigned long long)shader.hash, int(error));
        g_failed.insert(&shader);
        return 0;
      }
      g_handles[&shader] = handle;
      return handle;
    }

    bool depthShaders() {
      if (g_depthFailed)
        return false;
      if (g_fullscreenVs && g_frameDepthFs && g_maskFs && g_fillFs && g_flipFs && g_maskGlassFs && g_glassFs)
        return true;
      remixapi_HostShaderInfoEXT info{};
      info.sType = REMIXAPI_STRUCT_TYPE_HOST_SHADER_INFO_EXT;
      info.pushConstantBytes = 12;
      info.stage = REMIXAPI_HOST_SHADER_VERTEX_EXT;
      info.code = fullscreen_vert;
      info.codeBytes = sizeof(fullscreen_vert);
      if (g_register(&info, &g_fullscreenVs) != REMIXAPI_ERROR_CODE_SUCCESS) {
        g_depthFailed = true;
        return false;
      }
      const remixapi_HostShaderBindingEXT sampler = {0, REMIXAPI_HOST_BINDING_SAMPLER_EXT};
      info.stage = REMIXAPI_HOST_SHADER_FRAGMENT_EXT;
      info.code = frame_depth_frag;
      info.codeBytes = sizeof(frame_depth_frag);
      info.bindings = &sampler;
      info.bindingCount = 1;
      if (g_register(&info, &g_frameDepthFs) != REMIXAPI_ERROR_CODE_SUCCESS) {
        g_depthFailed = true;
        return false;
      }
      const remixapi_HostShaderBindingEXT two[2] = {{0, REMIXAPI_HOST_BINDING_SAMPLER_EXT},
                                                   {1, REMIXAPI_HOST_BINDING_SAMPLER_EXT}};
      info.bindings = two;
      info.bindingCount = 2;
      info.code = wall_mask_frag;
      info.codeBytes = sizeof(wall_mask_frag);
      info.pushConstantBytes = 0;
      if (g_register(&info, &g_maskFs) != REMIXAPI_ERROR_CODE_SUCCESS) {
        g_depthFailed = true;
        return false;
      }
      info.code = fill_frag;
      info.codeBytes = sizeof(fill_frag);
      info.pushConstantBytes = 12;
      if (g_register(&info, &g_fillFs) != REMIXAPI_ERROR_CODE_SUCCESS) {
        g_depthFailed = true;
        return false;
      }
      info.bindings = &sampler;
      info.bindingCount = 1;
      info.code = flip_frag;
      info.codeBytes = sizeof(flip_frag);
      info.pushConstantBytes = 0;
      if (g_register(&info, &g_flipFs) != REMIXAPI_ERROR_CODE_SUCCESS) {
        g_depthFailed = true;
        return false;
      }
      const remixapi_HostShaderBindingEXT seven[7] = {
          {0, REMIXAPI_HOST_BINDING_SAMPLER_EXT}, {1, REMIXAPI_HOST_BINDING_SAMPLER_EXT},
          {2, REMIXAPI_HOST_BINDING_SAMPLER_EXT}, {3, REMIXAPI_HOST_BINDING_SAMPLER_EXT},
          {4, REMIXAPI_HOST_BINDING_SAMPLER_EXT}, {5, REMIXAPI_HOST_BINDING_SAMPLER_EXT},
          {6, REMIXAPI_HOST_BINDING_SAMPLER_EXT}};
      info.bindings = seven;
      info.bindingCount = 3;
      info.code = wall_mask_glass_frag;
      info.codeBytes = sizeof(wall_mask_glass_frag);
      if (g_register(&info, &g_maskGlassFs) != REMIXAPI_ERROR_CODE_SUCCESS) {
        g_depthFailed = true;
        return false;
      }
      info.bindingCount = 7;
      info.code = glass_frag;
      info.codeBytes = sizeof(glass_frag);
      info.pushConstantBytes = 12;
      if (g_register(&info, &g_glassFs) != REMIXAPI_ERROR_CODE_SUCCESS) {
        g_depthFailed = true;
        return false;
      }
      return true;
    }

    uint32_t vkFormat(uint32_t dxgi) {
      switch (dxgi) {
      case DXGI_FORMAT_R32G32B32A32_FLOAT: return 109;
      case DXGI_FORMAT_R32G32B32_FLOAT: return 106;
      case DXGI_FORMAT_R32G32_FLOAT: return 103;
      case DXGI_FORMAT_R32_FLOAT: return 100;
      case DXGI_FORMAT_R16G16B16A16_FLOAT: return 97;
      case DXGI_FORMAT_R16G16_FLOAT: return 83;
      case DXGI_FORMAT_R16G16B16A16_UNORM: return 91;
      case DXGI_FORMAT_R16G16B16A16_SNORM: return 92;
      case DXGI_FORMAT_R8G8B8A8_UNORM: return 37;
      case DXGI_FORMAT_R8G8B8A8_SNORM: return 38;
      case DXGI_FORMAT_R8G8B8A8_UINT: return 41;
      case DXGI_FORMAT_B8G8R8A8_UNORM: return 44;
      default: return 0;
      }
    }
    uint32_t address(D3D11_TEXTURE_ADDRESS_MODE mode) {
      switch (mode) {
      case D3D11_TEXTURE_ADDRESS_CLAMP:
      case D3D11_TEXTURE_ADDRESS_BORDER: return 1;
      case D3D11_TEXTURE_ADDRESS_MIRROR:
      case D3D11_TEXTURE_ADDRESS_MIRROR_ONCE: return 2;
      default: return 0;
      }
    }

    // A pass and the data it points at (kept alive until submitted).
    struct Pass {
      remixapi_HostPassEXT info{};
      std::vector<remixapi_HostPassBufferEXT> buffers;
      std::vector<std::vector<uint8_t>> bufferData;
      std::vector<remixapi_HostPassSamplerEXT> samplers;
      std::vector<remixapi_HostPassVertexBindingEXT> vertexBindings;
      std::vector<remixapi_HostPassAttributeEXT> attributes;
      std::vector<uint8_t> push;
      std::vector<uint32_t> indices;
      std::vector<std::vector<uint8_t>> vertexData;
      void point() {
        for (size_t i = 0; i < buffers.size(); ++i)
          buffers[i].data = bufferData[i].data();
        for (size_t i = 0; i < vertexBindings.size(); ++i)
          vertexBindings[i].data = vertexData[i].data();
        info.buffers = buffers.data();
        info.bufferCount = uint32_t(buffers.size());
        info.samplers = samplers.data();
        info.samplerCount = uint32_t(samplers.size());
        info.vertexBindings = vertexBindings.data();
        info.vertexBindingCount = uint32_t(vertexBindings.size());
        info.attributes = attributes.data();
        info.attributeCount = uint32_t(attributes.size());
        info.indices = indices.data();
        info.indexCount = uint32_t(indices.size());
        info.pushConstants = push.empty() ? nullptr : push.data();
        info.pushConstantBytes = uint32_t(push.size());
      }
    };

    // A captured draw as a pass on the frame, sampling `frameTarget` for the
    // frame texture; or (mask) its geometry only, with wall_mask.frag, into
    // kBehind[0]. False when it can't be (why in `why`).
    // D3D11_BLEND to VkBlendFactor.
    uint32_t vkBlend(D3D11_BLEND b) {
      switch (b) {
      case D3D11_BLEND_ZERO: return 0;
      case D3D11_BLEND_ONE: return 1;
      case D3D11_BLEND_SRC_COLOR: return 2;
      case D3D11_BLEND_INV_SRC_COLOR: return 3;
      case D3D11_BLEND_DEST_COLOR: return 4;
      case D3D11_BLEND_INV_DEST_COLOR: return 5;
      case D3D11_BLEND_SRC_ALPHA: return 6;
      case D3D11_BLEND_INV_SRC_ALPHA: return 7;
      case D3D11_BLEND_DEST_ALPHA: return 8;
      case D3D11_BLEND_INV_DEST_ALPHA: return 9;
      case D3D11_BLEND_SRC_ALPHA_SAT: return 14;
      default: return 1;
      }
    }
    // A draw blended over what is behind it (a GrabPass distortion, a
    // transparent effect): it samples the frame as it is, not what is behind
    // the walls (kBehind), and leaves no mask.
    bool blended(const Draw &d) { return d.blend.BlendEnable != FALSE; }

    // A draw's geometry with one of the replay's own fragment shaders: into
    // `target`, sampling `samplers` (the walls' masks).
    struct Mask {
      remixapi_HostShaderHandleEXT fragment = 0;
      remixapi_HostTargetHandleEXT target = 0;
      std::vector<remixapi_HostPassSamplerEXT> samplers;
    };

    bool drawPass(const Draw &d, void *frameTexture, void *grabTexture, remixapi_HostTargetHandleEXT frameTarget,
                  uint32_t depthTest, const Mask *mask, Pass &pass, std::string &why) {
      const auto vs = handleOf(*d.vs), ps = mask ? mask->fragment : handleOf(*d.ps);
      if (!vs || !ps) {
        why = "shaders not compiled or registered";
        return false;
      }
      auto &info = pass.info;
      info.kind = REMIXAPI_HOST_PASS_DRAW_EXT;
      info.target = mask ? mask->target : 0; // else the frame
      if (mask) {
        info.targetWidth = uint32_t(d.viewport.Width);
        info.targetHeight = uint32_t(d.viewport.Height);
      }
      info.depth = mask ? 0 : depthTest;
      info.vertexShader = vs;
      info.fragmentShader = ps;
      if (mask)
        pass.samplers = mask->samplers;
      for (const auto *shader : {d.vs.get(), d.ps.get()}) {
        if (mask && !shader->vertex)
          continue;
        const auto &constants = shader->vertex ? d.vsConstants : d.psConstants;
        for (const auto &r : shader->resources) {
          if (r.type == 0) {
            auto cb = std::find_if(constants.begin(), constants.end(),
                                   [&](const ConstantBuffer &c) { return c.slot == r.bindPoint; });
            if (cb == constants.end()) {
              why = "a constant buffer was not bound";
              return false;
            }
            std::vector<uint8_t> data = cb->data;
            // UnityPerCamera (144 bytes): _ProjectionParams at 80. Its x is -1
            // in a capture into a render texture; 1 addresses the frame.
            if (shader->vertex && data.size() == 144) {
              float &flip = *reinterpret_cast<float *>(data.data() + 80);
              if (flip == -1.f)
                flip = 1.f;
            }
            pass.buffers.push_back({r.binding, nullptr, uint32_t(data.size())});
            pass.bufferData.push_back(std::move(data));
          } else if (r.type == 2) {
            if (shader->vertex) {
              why = "the vertex shader samples a texture";
              return false;
            }
            auto t = std::find_if(d.textures.begin(), d.textures.end(),
                                  [&](const Texture &t) { return t.slot == r.bindPoint; });
            if (t == d.textures.end()) {
              why = "a texture was not bound";
              return false;
            }
            remixapi_HostTargetHandleEXT target = 0;
            if (t->resource == frameTexture)
              target = frameTarget;
            else if (grabTexture && t->resource == grabTexture)
              target = kGrabCopy;
            else if (uploaded(t->resource))
              target = reinterpret_cast<remixapi_HostTargetHandleEXT>(t->resource);
            else {
              why = "a texture has not been uploaded yet";
              return false;
            }
            pass.samplers.push_back({r.binding, target, 0, 0, 0});
          } else if (r.type == 3) {
            auto s = std::find_if(d.samplers.begin(), d.samplers.end(),
                                  [&](const Sampler &s) { return s.slot == r.bindPoint; });
            remixapi_HostPassSamplerEXT entry{r.binding, 0, 1, 0, 0};
            if (s != d.samplers.end()) {
              const auto filter = s->desc.Filter;
              entry.linear = (filter & 0x4) ? 1 : 0; // D3D11 filter's min/mag bit: linear magnification
              entry.addressU = address(s->desc.AddressU);
              entry.addressV = address(s->desc.AddressV);
            }
            pass.samplers.push_back(entry);
          }
        }
      }
      // Vertex streams and the attributes the vertex shader reads, by semantic.
      for (const auto &stream : d.streams) {
        pass.vertexBindings.push_back({nullptr, uint32_t(stream.data.size()), stream.stride});
        pass.vertexData.push_back(stream.data);
      }
      for (const auto &in : d.vs->inputs) {
        auto e = std::find_if(d.layout.begin(), d.layout.end(), [&](const InputElement &e) {
          return e.semanticIndex == in.semanticIndex && _stricmp(e.semantic.c_str(), in.semantic.c_str()) == 0;
        });
        if (e == d.layout.end()) {
          why = "the vertex shader reads " + in.semantic + " the layout lacks";
          return false;
        }
        auto stream = std::find_if(d.streams.begin(), d.streams.end(),
                                   [&](const VertexStream &s) { return s.slot == e->slot; });
        const uint32_t format = vkFormat(e->format);
        if (stream == d.streams.end() || !format) {
          why = "an attribute's stream or format is unsupported";
          return false;
        }
        pass.attributes.push_back({in.reg, uint32_t(stream - d.streams.begin()), format, e->offset});
      }
      pass.indices = d.indices;
      if (d.topology != D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST) {
        why = "not a triangle list";
        return false;
      }
      info.topology = 0;
      info.srcColorBlend = info.srcAlphaBlend = 1; // ONE (no blending)
      info.colorBlendOp = info.alphaBlendOp = 0;
      info.colorWriteMask = 0xf;
      if (!mask) {
        // As the draw blended in Unity (the masks write everything).
        const auto &b = d.blend;
        info.colorWriteMask = b.RenderTargetWriteMask & 0xf;
        if (b.BlendEnable) {
          info.blendEnable = 1;
          info.srcColorBlend = vkBlend(b.SrcBlend);
          info.dstColorBlend = vkBlend(b.DestBlend);
          info.colorBlendOp = uint32_t(b.BlendOp) - 1; // D3D11_BLEND_OP 1..5, VkBlendOp 0..4
          info.srcAlphaBlend = vkBlend(b.SrcBlendAlpha);
          info.dstAlphaBlend = vkBlend(b.DestBlendAlpha);
          info.alphaBlendOp = uint32_t(b.BlendOpAlpha) - 1;
        }
      }
      // The capture's viewport, flipped (a negative height): Unity's render
      // texture is upside down on D3D.
      info.viewport[0] = int32_t(d.viewport.TopLeftX);
      info.viewport[1] = int32_t(d.viewport.TopLeftY + d.viewport.Height);
      info.viewport[2] = int32_t(d.viewport.Width);
      info.viewport[3] = -int32_t(d.viewport.Height);
      // dxbc's push constants: the rasterizer's sample count.
      const uint32_t pushBytes = std::max(d.vs->pushBytes, d.ps->pushBytes);
      if (pushBytes) {
        pass.push.assign(pushBytes, 0);
        const uint32_t samples = 1;
        std::memcpy(pass.push.data(), &samples, std::min<size_t>(4, pushBytes));
      }
      return true;
    }
    bool stainShaders() {
      if (g_stainFailed)
        return false;
      if (g_stainVs && g_stainFs)
        return true;
      remixapi_HostShaderInfoEXT info{};
      info.sType = REMIXAPI_STRUCT_TYPE_HOST_SHADER_INFO_EXT;
      info.pushConstantBytes = 64;
      info.stage = REMIXAPI_HOST_SHADER_VERTEX_EXT;
      info.code = stain_vert;
      info.codeBytes = sizeof(stain_vert);
      const remixapi_HostShaderBindingEXT two[2] = {{0, REMIXAPI_HOST_BINDING_SAMPLER_EXT},
                                                   {1, REMIXAPI_HOST_BINDING_SAMPLER_EXT}};
      if (g_register(&info, &g_stainVs) != REMIXAPI_ERROR_CODE_SUCCESS) {
        g_stainFailed = true;
        log("registering the stain vertex shader failed");
        return false;
      }
      info.stage = REMIXAPI_HOST_SHADER_FRAGMENT_EXT;
      info.code = stain_frag;
      info.codeBytes = sizeof(stain_frag);
      info.bindings = two;
      info.bindingCount = 2;
      if (g_register(&info, &g_stainFs) != REMIXAPI_ERROR_CODE_SUCCESS) {
        g_stainFailed = true;
        log("registering the stain fragment shader failed");
        return false;
      }
      return true;
    }

    // The stains as one draw on the frame (a multiply blend), or none.
    bool stainPass(const std::vector<float> &vertices, void *atlas, const float view[16], Pass &pass) {
      const size_t quads = vertices.size() / 20;
      if (!quads || !atlas || !stainShaders())
        return false;
      auto &info = pass.info;
      info.kind = REMIXAPI_HOST_PASS_DRAW_EXT;
      info.target = 0; // the frame
      info.vertexShader = g_stainVs;
      info.fragmentShader = g_stainFs;
      pass.samplers.push_back({0, reinterpret_cast<remixapi_HostTargetHandleEXT>(atlas), 0, 1, 1});
      pass.samplers.push_back({1, REMIXAPI_HOST_TARGET_VIEW_DEPTH_EXT, 0, 1, 1});
      pass.vertexData.emplace_back(reinterpret_cast<const uint8_t *>(vertices.data()),
                                   reinterpret_cast<const uint8_t *>(vertices.data() + quads * 20));
      pass.vertexBindings.push_back({nullptr, uint32_t(quads * 20 * sizeof(float)), 5 * sizeof(float)});
      pass.attributes.push_back({0, 0, kVkR32G32B32Sfloat, 0});
      pass.attributes.push_back({1, 0, kVkR32G32Sfloat, 3 * sizeof(float)});
      pass.indices.resize(quads * 6);
      for (uint32_t q = 0; q < quads; ++q) {
        const uint32_t v = q * 4;
        const uint32_t quad[6] = {v, v + 1, v + 2, v, v + 2, v + 3};
        std::memcpy(&pass.indices[size_t(q) * 6], quad, sizeof(quad));
      }
      info.topology = 0;
      // frame * (1, 0, 0): VK_BLEND_FACTOR_DST_COLOR, ZERO; alpha kept.
      info.blendEnable = 1;
      info.srcColorBlend = 4;
      info.dstColorBlend = 0;
      info.colorBlendOp = 0;
      info.srcAlphaBlend = 0;
      info.dstAlphaBlend = 1;
      info.alphaBlendOp = 0;
      info.colorWriteMask = 0x7;
      pass.push.assign(reinterpret_cast<const uint8_t *>(view), reinterpret_cast<const uint8_t *>(view) + 64);
      return true;
    }

    Pass fullScreen(remixapi_HostShaderHandleEXT fragment, remixapi_HostTargetHandleEXT target, uint32_t width,
                    uint32_t height, const void *push, size_t pushBytes) {
      Pass pass;
      auto &info = pass.info;
      info.kind = REMIXAPI_HOST_PASS_DRAW_EXT;
      info.target = target;
      info.targetWidth = width;
      info.targetHeight = height;
      info.vertexShader = g_fullscreenVs;
      info.fragmentShader = fragment;
      info.srcColorBlend = info.srcAlphaBlend = 1;
      info.colorWriteMask = 0xf;
      const float triangle[6] = {-1, -1, 3, -1, -1, 3};
      pass.vertexData.emplace_back(reinterpret_cast<const uint8_t *>(triangle),
                                   reinterpret_cast<const uint8_t *>(triangle) + sizeof(triangle));
      pass.vertexBindings.push_back({nullptr, sizeof(triangle), 8});
      pass.attributes.push_back({0, 0, kVkR32G32Sfloat, 0});
      pass.indices = {0, 1, 2};
      pass.push.assign(static_cast<const uint8_t *>(push), static_cast<const uint8_t *>(push) + pushBytes);
      return pass;
    }
  } // namespace

  bool bindRemix(HMODULE remix) {
    if (!remix)
      return false;
    g_register = reinterpret_cast<PFN_remixapi_RegisterHostShaderEXT>(
        GetProcAddress(remix, "remixapi_RegisterHostShaderEXT"));
    g_submit = reinterpret_cast<PFN_remixapi_SubmitHostPassesEXT>(GetProcAddress(remix, "remixapi_SubmitHostPassesEXT"));
    g_updateTarget =
        reinterpret_cast<PFN_remixapi_UpdateHostTargetEXT>(GetProcAddress(remix, "remixapi_UpdateHostTargetEXT"));
    g_updateVolume =
        reinterpret_cast<PFN_remixapi_UpdateHostVolumeEXT>(GetProcAddress(remix, "remixapi_UpdateHostVolumeEXT"));
    g_statistics = reinterpret_cast<PFN_remixapi_GetHostPassStatisticsEXT>(
        GetProcAddress(remix, "remixapi_GetHostPassStatisticsEXT"));
    log("remix host passes: register %p submit %p update %p statistics %p", g_register, g_submit, g_updateTarget,
        g_statistics);
    return g_register && g_submit && g_updateTarget;
  }

  void setFrameTexture(void *resource) {
    std::lock_guard l(g_settingsLock);
    g_frameTexture = resource;
  }
  void setGrabTexture(void *resource) {
    std::lock_guard l(g_settingsLock);
    g_grabTexture = resource;
  }
  void setScreenSource(void *resource, uint32_t width, uint32_t height, uint32_t frameWidth, uint32_t frameHeight) {
    std::lock_guard l(g_settingsLock);
    g_screenSource = resource;
    g_screenSize[0] = width;
    g_screenSize[1] = height;
    g_screenFrame[0] = frameWidth;
    g_screenFrame[1] = frameHeight;
  }
  void setProjection(float p22, float p23, bool reversedZ) {
    std::lock_guard l(g_settingsLock);
    g_projection[0] = p22;
    g_projection[1] = p23;
    g_reversedZ = reversedZ;
  }
  void setTexture(void *resource, uint32_t width, uint32_t height, uint32_t depth, uint32_t vkFormat,
                  const void *pixels) {
    PendingTexture t;
    t.width = width;
    t.height = height;
    t.depth = depth;
    t.vkFormat = vkFormat;
    t.pixels.assign(static_cast<const uint8_t *>(pixels),
                    static_cast<const uint8_t *>(pixels) + size_t(width) * height * depth * 4);
    std::lock_guard l(g_settingsLock);
    // Content replaced (a palette's LUT recomputed): sampled as new once uploaded.
    g_uploaded.erase(resource);
    g_pending[resource] = std::move(t);
  }

  void setStains(const float *vertices, size_t quads, void *atlas) {
    std::lock_guard l(g_settingsLock);
    g_stainVertices = quads ? std::make_shared<const std::vector<float>>(vertices, vertices + quads * 20) : nullptr;
    g_stainAtlas = atlas;
  }
  void setLayersView(int view) {
    std::lock_guard l(g_settingsLock);
    g_layersView = view;
  }
  void setStainView(const float rows[16]) {
    std::lock_guard l(g_settingsLock);
    if (rows)
      std::memcpy(g_stainView, rows, sizeof(g_stainView));
    g_stainViewSet = rows != nullptr;
  }

  int missingTextures(void **resources, int *sizes, int max) {
    const auto draws = lastCapture();
    std::lock_guard l(g_settingsLock);
    int count = 0;
    for (const auto &d : draws)
      for (const auto &t : d.textures) {
        if (!t.resource || t.resource == g_frameTexture || t.resource == g_grabTexture || t.resource == g_screenSource ||
            g_uploaded.count(t.resource) ||
            g_pending.count(t.resource))
          continue;
        if (std::find(resources, resources + count, t.resource) != resources + count)
          continue;
        if (count >= max)
          return count;
        resources[count] = t.resource;
        sizes[3 * count] = int(t.width);
        sizes[3 * count + 1] = int(t.height);
        sizes[3 * count + 2] = int(t.depth);
        ++count;
      }
    return count;
  }

  int submit() {
    if (!g_submit)
      return 0;
    void *frameTexture = nullptr, *grabTexture = nullptr, *screenSource = nullptr;
    uint32_t screenSize[2], screenFrame[2];
    float projection[2];
    bool reversedZ;
    int layersView;
    std::unordered_map<void *, PendingTexture> pending;
    std::shared_ptr<const std::vector<float>> stainVertices;
    void *stainAtlas = nullptr;
    float stainView[16];
    {
      std::lock_guard l(g_settingsLock);
      if (g_stainViewSet && g_stainVertices && g_stainAtlas && g_uploaded.count(g_stainAtlas)) {
        stainVertices = g_stainVertices;
        stainAtlas = g_stainAtlas;
        std::memcpy(stainView, g_stainView, sizeof(stainView));
      }
      frameTexture = g_frameTexture;
      grabTexture = g_grabTexture;
      screenSource = g_screenSource;
      screenSize[0] = g_screenSize[0];
      screenSize[1] = g_screenSize[1];
      screenFrame[0] = g_screenFrame[0];
      screenFrame[1] = g_screenFrame[1];
      projection[0] = g_projection[0];
      projection[1] = g_projection[1];
      reversedZ = g_reversedZ;
      layersView = g_layersView;
      pending.swap(g_pending);
    }
    for (auto &[resource, t] : pending) {
      const auto handle = reinterpret_cast<remixapi_HostTargetHandleEXT>(resource);
      const auto error =
          t.depth > 1 ? (g_updateVolume ? g_updateVolume(handle, t.width, t.height, t.depth, t.vkFormat,
                                                         t.pixels.data(), t.pixels.size())
                                        : REMIXAPI_ERROR_CODE_GET_PROC_ADDRESS_FAILURE)
                      : g_updateTarget(handle, t.width, t.height, t.vkFormat, t.pixels.data(), t.pixels.size());
      if (error == REMIXAPI_ERROR_CODE_SUCCESS) {
        std::lock_guard l(g_settingsLock);
        g_uploaded.insert(resource);
      } else
        log("uploading texture %p (%ux%u) failed: %d", resource, t.width, t.height, int(error));
    }

    // Screen effects apart: they follow everything else, over the composed frame.
    auto draws = lastCapture();
    std::vector<Draw> screen;
    for (auto it = draws.begin(); it != draws.end();)
      if (it->tag == kScreenTag) {
        screen.push_back(std::move(*it));
        it = draws.erase(it);
      } else
        ++it;
    std::vector<Pass> passes;
    passes.reserve(draws.size() + 2);
    std::string skipped;
    if (!draws.empty()) {
      const auto &first = draws.front();
      const uint32_t width = uint32_t(first.viewport.Width), height = uint32_t(first.viewport.Height);
      // The frame, for the draws to sample.
      Pass copy;
      copy.info.kind = REMIXAPI_HOST_PASS_COPY_EXT;
      copy.info.source = 0;
      copy.info.target = kFrameCopy;
      copy.info.targetWidth = width;
      copy.info.targetHeight = height;
      passes.push_back(std::move(copy));
      // The frame's depth, in the capture camera's projection.
      uint32_t depthTest = 0;
      remixapi_HostTargetHandleEXT frameTarget = kFrameCopy;
      const bool walls = std::any_of(draws.begin(), draws.end(), [](const Draw &d) { return !blended(d); });
      const bool anyBlended = std::any_of(draws.begin(), draws.end(), [](const Draw &d) { return blended(d); });
      const bool grabs = grabTexture && std::any_of(draws.begin(), draws.end(), [&](const Draw &d) {
        return std::any_of(d.textures.begin(), d.textures.end(),
                           [&](const Texture &t) { return t.resource == grabTexture; });
      });
      auto grabCopy = [&] {
        if (grabs && depthShaders()) {
          Pass flip = fullScreen(g_flipFs, kGrabCopy, width, height, nullptr, 0);
          flip.samplers.push_back({0, kFrameCopy, 0, 1, 1});
          passes.push_back(std::move(flip));
        }
      };
      grabCopy();
      // Walls behind the runtime's sheets (glass, water) are replayed there
      // too, the sheets put back over them (glass.frag): the walls test
      // against the depth of the first surface that is not a sheet.
      const bool layers = g_viewLayers && walls;
      const auto wallDepth = layers ? REMIXAPI_HOST_TARGET_VIEW_OPAQUE_DEPTH_EXT : REMIXAPI_HOST_TARGET_VIEW_DEPTH_EXT;
      auto frameDepth = [&](remixapi_HostTargetHandleEXT source) {
        const float push[3] = {-projection[0], projection[1], reversedZ ? 0.f : 1.f};
        Pass fill = fullScreen(g_frameDepthFs, REMIXAPI_HOST_TARGET_FRAME_DEPTH_EXT, 0, 0, push, sizeof(push));
        fill.info.depth = (kCompareAlways + 1) | REMIXAPI_HOST_DEPTH_WRITE_EXT;
        fill.info.colorWriteMask = 0;
        fill.samplers.push_back({0, source, 0, 1, 1});
        passes.push_back(std::move(fill));
      };
      if (projection[0] != 0.f && depthShaders()) {
        frameDepth(walls ? wallDepth : REMIXAPI_HOST_TARGET_VIEW_DEPTH_EXT);
        depthTest = (reversedZ ? kCompareGreaterOrEqual : kCompareLessOrEqual) + 1;
      }
      auto clearTarget = [&](remixapi_HostTargetHandleEXT target) {
        Pass clear;
        clear.info.kind = REMIXAPI_HOST_PASS_CLEAR_EXT;
        clear.info.target = target;
        clear.info.targetWidth = width;
        clear.info.targetHeight = height;
        passes.push_back(std::move(clear));
      };
      auto masks = [&](const Mask &mask) {
        for (const auto &d : draws) {
          Pass pass;
          std::string why;
          if (!blended(d) && drawPass(d, frameTexture, grabTexture, 0, 0, &mask, pass, why))
            passes.push_back(std::move(pass));
        }
      };
      remixapi_HostTargetHandleEXT behind = 0;
      if (walls && depthTest) {
        // What is behind the walls, for their shader to sample in place of
        // the frame: it samples around each pixel (chromatic aberration,
        // noise jitter), and the frame has what stands in front of the walls
        // there. The walls' pixels, then holes filled sideways (out to 511
        // pixels) and a little vertically (31), the rest from the frame.
        clearTarget(kBehind[0]);
        Mask mask{layers ? g_maskGlassFs : g_maskFs, kBehind[0], {{0, kFrameCopy, 0, 1, 1}, {1, wallDepth, 0, 1, 1}}};
        if (layers)
          mask.samplers.push_back({2, REMIXAPI_HOST_TARGET_VIEW_TRANSMITTANCE_EXT, 0, 1, 1});
        masks(mask);
        if (layers) {
          // The walls' pixels with nothing but sheets in front of them.
          clearTarget(kCover);
          masks(Mask{g_maskFs, kCover, {{0, kFrameCopy, 0, 1, 1}, {1, wallDepth, 0, 1, 1}}});
        }
        std::vector<std::pair<int, int>> steps;
        for (int step = 1; step <= 256; step *= 2)
          steps.push_back({step, 0});
        for (int step = 1; step <= 16; step *= 2)
          steps.push_back({0, step});
        int current = 0;
        for (size_t i = 0; i < steps.size(); ++i) {
          const int push[3] = {steps[i].first, steps[i].second, i + 1 == steps.size() ? 1 : 0};
          Pass step = fullScreen(g_fillFs, kBehind[1 - current], width, height, push, sizeof(push));
          step.samplers.push_back({0, kBehind[current], 0, 1, 1});
          step.samplers.push_back({1, kFrameCopy, 0, 1, 1});
          passes.push_back(std::move(step));
          current = 1 - current;
        }
        frameTarget = behind = kBehind[current];
      }
      // The opaque draws (the walls), then the blended ones, as Unity orders
      // its queues.
      size_t replayed = 0, opaqueReplayed = 0;
      auto replay = [&](const Draw &d, remixapi_HostTargetHandleEXT sampled) {
        Pass pass;
        std::string why;
        if (drawPass(d, frameTexture, grabTexture, sampled, depthTest, nullptr, pass, why)) {
          passes.push_back(std::move(pass));
          ++replayed;
          return true;
        }
        if (skipped.empty())
          skipped = "draw tag " + std::to_string(d.tag) + ": " + why;
        return false;
      };
      for (const auto &d : draws)
        if (!blended(d) && replay(d, frameTarget))
          ++opaqueReplayed;
      if (layers && behind && opaqueReplayed) {
        const int push[3] = {layersView, 0, 0};
        Pass glass = fullScreen(g_glassFs, 0, 0, 0, push, sizeof(push));
        glass.samplers.push_back({0, 0, 0, 1, 1}); // the frame as it is now
        glass.samplers.push_back({1, kFrameCopy, 0, 1, 1});
        glass.samplers.push_back({2, behind, 0, 1, 1});
        glass.samplers.push_back({3, REMIXAPI_HOST_TARGET_VIEW_TRANSMITTANCE_EXT, 1, 1, 1});
        glass.samplers.push_back({4, kCover, 0, 1, 1});
        glass.samplers.push_back({5, REMIXAPI_HOST_TARGET_VIEW_DEPTH_EXT, 0, 1, 1});
        glass.samplers.push_back({6, REMIXAPI_HOST_TARGET_VIEW_OPAQUE_DEPTH_EXT, 0, 1, 1});
        passes.push_back(std::move(glass));
      }
      if (anyBlended && opaqueReplayed) {
        // Blended draws sample the frame with the walls, and test against
        // what the camera sees first (sheets included).
        Pass again;
        again.info.kind = REMIXAPI_HOST_PASS_COPY_EXT;
        again.info.source = 0;
        again.info.target = kFrameCopy;
        again.info.targetWidth = width;
        again.info.targetHeight = height;
        passes.push_back(std::move(again));
        grabCopy();
        if (depthTest && layers)
          frameDepth(REMIXAPI_HOST_TARGET_VIEW_DEPTH_EXT);
      }
      for (const auto &d : draws)
        if (blended(d))
          replay(d, kFrameCopy);
      if (!replayed)
        passes.clear(); // no draw made it: nothing to replay
    }
    // Blood stains first: the replayed draws then sample a frame with them.
    size_t stains = 0;
    {
      Pass pass;
      if (stainVertices && stainPass(*stainVertices, stainAtlas, stainView, pass)) {
        stains = stainVertices->size() / 20;
        passes.insert(passes.begin(), std::move(pass));
      }
    }
    // Screen effects (the game's full-screen post-process): each over a copy of
    // the frame as composed so far, its whole viewport, no depth.
    size_t screens = 0;
    for (const auto &d : screen) {
      Pass pass;
      std::string why;
      if (!screenSource || !drawPass(d, screenSource, nullptr, kScreenCopy, 0, nullptr, pass, why)) {
        if (skipped.empty())
          skipped = "screen effect: " + (screenSource ? why : std::string("no screen source"));
        continue;
      }
      // All of the frame (flipped as the captures are), whatever viewport Unity's blit had.
      if (screenFrame[0] && screenFrame[1]) {
        pass.info.viewport[0] = 0;
        pass.info.viewport[1] = int32_t(screenFrame[1]);
        pass.info.viewport[2] = int32_t(screenFrame[0]);
        pass.info.viewport[3] = -int32_t(screenFrame[1]);
      }
      Pass copy;
      copy.info.kind = REMIXAPI_HOST_PASS_COPY_EXT;
      copy.info.source = 0;
      copy.info.target = kScreenCopy;
      // At the game's render resolution (its downsampling), nearest as it renders.
      copy.info.targetWidth = screenSize[0] ? screenSize[0] : screenFrame[0] ? screenFrame[0] : uint32_t(d.viewport.Width);
      copy.info.targetHeight =
          screenSize[1] ? screenSize[1] : screenFrame[1] ? screenFrame[1] : uint32_t(d.viewport.Height);
      copy.info.linear = 0;
      passes.push_back(std::move(copy));
      passes.push_back(std::move(pass));
      ++screens;
    }
    std::vector<remixapi_HostPassEXT> list;
    std::vector<uint32_t> flags;
    for (auto &p : passes) {
      p.point();
      list.push_back(p.info);
      flags.push_back(REMIXAPI_HOST_PASS_ALWAYS_EXT);
    }
    remixapi_HostPassListFlagsEXT flagList{};
    flagList.sType = REMIXAPI_STRUCT_TYPE_HOST_PASS_LIST_FLAGS_EXT;
    flagList.flags = flags.data();
    flagList.flagCount = uint32_t(flags.size());
    remixapi_HostPassListEXT submission{};
    submission.sType = REMIXAPI_STRUCT_TYPE_HOST_PASS_LIST_EXT;
    submission.pNext = &flagList;
    submission.passes = list.data();
    submission.passCount = uint32_t(list.size());
    auto error = g_submit(&submission);
    if (error == REMIXAPI_ERROR_CODE_INVALID_ARGUMENTS && g_viewLayers &&
        std::any_of(passes.begin(), passes.end(), [](const Pass &p) {
          return std::any_of(p.samplers.begin(), p.samplers.end(), [](const remixapi_HostPassSamplerEXT &s) {
            return s.target == REMIXAPI_HOST_TARGET_VIEW_TRANSMITTANCE_EXT ||
                   s.target == REMIXAPI_HOST_TARGET_VIEW_OPAQUE_DEPTH_EXT;
          });
        })) {
      g_viewLayers = false;
      log("this remix.dll has no sheet transmittance (REMIXAPI_HOST_TARGET_VIEW_TRANSMITTANCE_EXT): walls are hidden "
          "behind glass and water");
    }

    char line[512];
    std::snprintf(line, sizeof(line), "submitted %zu passes (%zu captured draws, %zu screen effects, %zu stains, error %d)%s%s",
                  list.size(), draws.size(), screens, stains, int(error), skipped.empty() ? "" : "; first skipped: ",
                  skipped.c_str());
    std::string text = line;
    if (g_statistics) {
      remixapi_HostPassStatisticsEXT s{};
      s.structSize = sizeof(s);
      if (g_statistics(&s) == REMIXAPI_ERROR_CODE_SUCCESS) {
        std::snprintf(line, sizeof(line),
                      " | runtime last frame: %u passes, %u draws, %u copies, %u skipped (copy %u shaders %u pipeline "
                      "%u descriptors %u buffer %u sampler %u target %u view depth %u), %u shaders, %u targets, %u "
                      "pipelines, %u always, %u dropped",
                      s.passes, s.draws, s.copies, s.skipped, s.skippedCopySource, s.skippedShaders, s.skippedPipeline,
                      s.skippedDescriptors, s.skippedBuffer, s.skippedSampler, s.skippedTarget, s.skippedViewDepth,
                      s.shaders, s.targets, s.pipelines, s.always, s.dropped);
        text += line;
      }
    }
    {
      std::lock_guard l(g_settingsLock);
      g_lastSubmit = std::move(text);
    }
    return int(list.size());
  }

  std::string submitReport() {
    std::lock_guard l(g_settingsLock);
    return g_lastSubmit;
  }

} // namespace urtx
