#include "dxbc_options.h"

namespace dxvk {

  // Standalone (UnityRTX): the defaults only. DXVK's device constructor reads
  // the device's features; the host's passes run on OpenRemix, which compiles
  // the SPIR-V with core Vulkan 1.2 and its own features.
  DxbcOptions::DxbcOptions() {

  }

}
