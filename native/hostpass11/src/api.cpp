// UnityRTXHostPass's exports, for UnityRTX (P/Invoke):
//   UnityRTXHostPass_Install(ID3D11Device *) hooks Unity's device (once);
//   UnityRTXHostPass_RenderEvent() is the UnityRenderingEvent callback a
//     CommandBuffer.IssuePluginEvent runs on the render thread: event ids
//     kBegin | tag open (or continue) the capture with that tag, kEnd closes it;
//   UnityRTXHostPass_Report(buffer, bytes) writes the last capture's summary;
//   UnityRTXHostPass_BindRemix(HMODULE) finds remix.dll's host-pass entry points;
//   UnityRTXHostPass_SetFrameTexture / SetProjection / SetTexture (main thread)
//     name the texture the frame replaces, the capture camera's projection and
//     the other textures' pixels;
//   UnityRTXHostPass_SetGrabTexture (main thread) names the stand-in bound as
//     _GrabTexture, which the frame replaces as well;
//   UnityRTXHostPass_SetScreenSource (main thread) names the texture a
//     screen effect's draws (tag kScreenTag) sample as the composed frame;
//   UnityRTXHostPass_SetStains (main thread) / SetStainView (the presenting
//     thread) give the blood stains drawn over the frame and its camera;
//   UnityRTXHostPass_Submit() (the presenting thread, before each present)
//     submits the stains and the last capture as host passes.
#include "hostpass.h"
#include <algorithm>
#include <cstring>

namespace {
  constexpr int kBegin = 0x55520000, kEnd = 0x5552ffff;
  bool g_installed = false;

  void __stdcall renderEvent(int id) {
    if (id == kEnd)
      urtx::endCapture();
    else if ((id & 0xffff0000) == kBegin)
      urtx::beginCapture(uint32_t(id & 0xffff));
  }
} // namespace

extern "C" {
__declspec(dllexport) int UnityRTXHostPass_Install(void *device) {
  if (g_installed)
    return 1;
  g_installed = urtx::install(static_cast<ID3D11Device *>(device));
  return g_installed ? 1 : 0;
}

__declspec(dllexport) void *UnityRTXHostPass_RenderEvent() { return reinterpret_cast<void *>(&renderEvent); }

__declspec(dllexport) int UnityRTXHostPass_BindRemix(void *remix) {
  return urtx::bindRemix(static_cast<HMODULE>(remix)) ? 1 : 0;
}
__declspec(dllexport) void UnityRTXHostPass_SetFrameTexture(void *resource) { urtx::setFrameTexture(resource); }
__declspec(dllexport) void UnityRTXHostPass_SetGrabTexture(void *resource) { urtx::setGrabTexture(resource); }
__declspec(dllexport) void UnityRTXHostPass_SetScreenSource(void *resource, int width, int height, int frameWidth,
                                                             int frameHeight) {
  urtx::setScreenSource(resource, uint32_t(std::max(width, 0)), uint32_t(std::max(height, 0)),
                        uint32_t(std::max(frameWidth, 0)), uint32_t(std::max(frameHeight, 0)));
}
__declspec(dllexport) void UnityRTXHostPass_SetProjection(float p22, float p23, int reversedZ) {
  urtx::setProjection(p22, p23, reversedZ != 0);
}
__declspec(dllexport) void UnityRTXHostPass_SetTexture(void *resource, int width, int height, int depth, int vkFormat,
                                                       const void *rgba8) {
  if (resource && rgba8 && width > 0 && height > 0 && depth > 0)
    urtx::setTexture(resource, uint32_t(width), uint32_t(height), uint32_t(depth), uint32_t(vkFormat), rgba8);
}
__declspec(dllexport) int UnityRTXHostPass_MissingTextures(void **resources, int *sizes, int max) {
  return resources && sizes && max > 0 ? urtx::missingTextures(resources, sizes, max) : 0;
}
__declspec(dllexport) void UnityRTXHostPass_SetStains(const float *vertices, int quads, void *atlas) {
  urtx::setStains(vertices, vertices && quads > 0 ? size_t(quads) : 0, atlas);
}
__declspec(dllexport) void UnityRTXHostPass_SetStainView(const float *rows) { urtx::setStainView(rows); }
__declspec(dllexport) int UnityRTXHostPass_Submit() { return urtx::submit(); }
__declspec(dllexport) void UnityRTXHostPass_ClearCapture() { urtx::clearCapture(); }

__declspec(dllexport) int UnityRTXHostPass_Report(char *buffer, int bytes) {
  const std::string text = urtx::report() + urtx::submitReport() + "\n";
  if (!buffer || bytes <= 0)
    return int(text.size()) + 1;
  const size_t n = std::min(size_t(bytes - 1), text.size());
  std::memcpy(buffer, text.data(), n);
  buffer[n] = 0;
  return int(n);
}
}
