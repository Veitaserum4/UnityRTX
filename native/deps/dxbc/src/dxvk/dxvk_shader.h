#pragma once
// Standalone dxbc (UnityRTX; from SourcePT's standalone dxso): the compiled
// shader as the compiler hands it over, in place of DXVK's DxvkShader. Its
// SPIR-V and the descriptor bindings it declared (set 0, as DXVK's D3D11
// layout numbers them). DxvkAccessOp and DxvkBindingInfo as DXVK 2.6.2's
// dxvk_pipelayout.h declares them (fields only).
#include <optional>
#include <type_traits>
#include <vector>
#include <vulkan/vulkan.h>

#include "../spirv/spirv_code_buffer.h"
#include "../util/rc/util_rc.h"
#include "../util/rc/util_rc_ptr.h"
#include "../util/util_small_vector.h"

namespace dxvk {

  constexpr uint32_t MaxNumXfbBuffers = 4;
  constexpr uint32_t MaxNumSpecConstants = 12; // DXVK's dxvk_limits.h
  constexpr uint32_t MaxNumRenderTargets = 8;

  struct DxvkAccessOp {
    enum OpType : uint16_t {
      None      = 0x0u,
      Or        = 0x1u,
      And       = 0x2u,
      Xor       = 0x3u,
      Add       = 0x4u,
      IMin      = 0x5u,
      IMax      = 0x6u,
      UMin      = 0x7u,
      UMax      = 0x8u,

      StoreF    = 0xdu,
      StoreUi   = 0xeu,
      StoreSi   = 0xfu,
    };

    DxvkAccessOp() = default;
    DxvkAccessOp(OpType t)
    : op(uint16_t(t)) { }

    DxvkAccessOp(OpType t, uint16_t constant)
    : op(uint16_t(t) | (constant << 4u)) { }

    uint16_t op = 0u;

    bool operator == (const DxvkAccessOp& t) const { return op == t.op; }
    bool operator != (const DxvkAccessOp& t) const { return op != t.op; }

    template<typename T, std::enable_if_t<std::is_integral_v<T>, bool> = true>
    explicit operator T() const { return op; }
  };

  struct DxvkBindingInfo {
    VkDescriptorType      descriptorType  = VK_DESCRIPTOR_TYPE_MAX_ENUM;
    uint32_t              resourceBinding = 0u;
    VkImageViewType       viewType        = VK_IMAGE_VIEW_TYPE_MAX_ENUM;
    VkShaderStageFlagBits stage           = VK_SHADER_STAGE_FLAG_BITS_MAX_ENUM;
    VkAccessFlags         access          = 0u;
    DxvkAccessOp          accessOp        = DxvkAccessOp::None;
    bool                  uboSet          = false;
    bool                  isMultisampled  = false;
  };

  struct DxvkShaderCreateInfo {
    VkShaderStageFlagBits stage = VK_SHADER_STAGE_FLAG_BITS_MAX_ENUM;
    uint32_t bindingCount = 0;
    const DxvkBindingInfo* bindings = nullptr;
    uint32_t inputMask = 0;
    uint32_t outputMask = 0;
    uint32_t flatShadingInputs = 0;
    VkShaderStageFlags pushConstStages = 0;
    uint32_t pushConstSize = 0;
    int32_t xfbRasterizedStream = 0;
    uint32_t patchVertexCount = 0;
    uint32_t xfbStrides[MaxNumXfbBuffers] = { };
    VkPrimitiveTopology inputTopology = VK_PRIMITIVE_TOPOLOGY_MAX_ENUM;
    VkPrimitiveTopology outputTopology = VK_PRIMITIVE_TOPOLOGY_MAX_ENUM;
  };

  class DxvkShader : public RcObject {
  public:
    DxvkShader(const DxvkShaderCreateInfo& info, SpirvCodeBuffer&& code)
      : m_info(info), m_bindings(info.bindings, info.bindings + info.bindingCount), m_code(std::move(code)) {
      m_info.bindings = m_bindings.data();
    }
    const DxvkShaderCreateInfo& info() const { return m_info; }
    const std::vector<DxvkBindingInfo>& bindings() const { return m_bindings; }
    const SpirvCodeBuffer& code() const { return m_code; }
  private:
    DxvkShaderCreateInfo m_info;
    std::vector<DxvkBindingInfo> m_bindings;
    SpirvCodeBuffer m_code;
  };

}
