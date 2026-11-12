#version 450
// The frame's depth buffer (REMIXAPI_HOST_TARGET_FRAME_DEPTH_EXT) from the
// runtime's view depth d, as the Unity camera's GPU projection P writes it
// (view space looks down -z, so z_clip = -P22 d + P23 and w_clip = d):
// device z = (scale * d + offset) / d, with scale = -P22 and offset = P23;
// `far` where nothing was hit (0 with Unity's reversed depth, else 1). The
// replayed draws then test against what is in front of them.
layout(binding = 0) uniform sampler2D viewDepth;
layout(push_constant) uniform Push {
  float scale, offset, far;
} push;
layout(location = 0) in vec2 uv;
void main() {
  const float d = texture(viewDepth, uv).r;
  gl_FragDepth = d < 1e9 && d > 0 ? clamp((push.scale * d + push.offset) / d, 0, 1) : push.far;
}
