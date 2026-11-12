// UnityRTXHostPass's exports, for UnityRTX (P/Invoke):
//   UnityRTXHostPass_Install(ID3D11Device *) hooks Unity's device (once);
//   UnityRTXHostPass_RenderEvent() is the UnityRenderingEvent callback a
//     CommandBuffer.IssuePluginEvent runs on the render thread: event ids
//     kBegin | tag open (or continue) the capture with that tag, kEnd closes it;
//   UnityRTXHostPass_Report(buffer, bytes) writes the last capture's summary.
#include "hostpass.h"
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

__declspec(dllexport) int UnityRTXHostPass_Report(char *buffer, int bytes) {
  const std::string text = urtx::report();
  if (!buffer || bytes <= 0)
    return int(text.size()) + 1;
  const size_t n = std::min(size_t(bytes - 1), text.size());
  std::memcpy(buffer, text.data(), n);
  buffer[n] = 0;
  return int(n);
}
}
