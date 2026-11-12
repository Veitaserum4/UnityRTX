#version 450
// The frame's depth buffer (REMIXAPI_HOST_TARGET_FRAME_DEPTH_EXT) from the
// runtime's view depth d, as the Unity camera's GPU projection P writes it
// (view space looks down -z, so z_clip = -P22 d + P23 and w_clip = d):
// device z = (scale * d + offset) / d, with scale = -P22 and offset = P23;
// `far` where nothing was hit (0 with Unity's reversed depth, else 1). The
// replayed draws then test against what is in front of them.
//
// The view depth is at the tracer's render resolution and the frame is
// upscaled and antialiased: each pixel takes the nearest depth of the 3x3
// render pixels around it, so a replayed draw stops short of an object's
// outline instead of cutting it in render-pixel steps.
layout(binding = 0) uniform sampler2D viewDepth;
layout(push_constant) uniform Push {
  float scale, offset, far;
} push;
layout(location = 0) in vec2 uv;
void main() {
  const ivec2 size = textureSize(viewDepth, 0);
  const ivec2 center = ivec2(uv * vec2(size));
  float d = 1e30;
  for (int y = -1; y <= 1; ++y)
    for (int x = -1; x <= 1; ++x) {
      const float s = texelFetch(viewDepth, clamp(center + ivec2(x, y), ivec2(0), size - 1), 0).r;
      if (s > 0)
        d = min(d, s);
    }
  gl_FragDepth = d < 1e9 ? clamp((push.scale * d + push.offset) / d, 0, 1) : push.far;
}
