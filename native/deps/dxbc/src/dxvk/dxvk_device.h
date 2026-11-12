#pragma once
// Standalone dxbc: no DXVK device (DxbcOptions keeps its default constructor
// only; the device one is declared, never defined). The basics DXVK's own
// header brought along for the compiler's headers.
#include <cstdint>
#include <vulkan/vulkan.h>

#include "../util/util_flags.h"
#include "../util/rc/util_rc.h"
#include "../util/rc/util_rc_ptr.h"

namespace dxvk {
  class DxvkDevice;
}
