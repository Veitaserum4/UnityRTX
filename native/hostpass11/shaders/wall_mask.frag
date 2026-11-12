#version 450
// The frame where the replayed walls show (what is behind them, the image
// their shader samples), alpha 1; discarded where something is in front of
// a wall (the runtime's view depth nearer than the wall's own) and near such
// places: the view depth is at the tracer's render resolution and the frame
// is upscaled and antialiased, so pixels along an object's outline mix it
// with what is behind; checking +-2 render pixels keeps them out (they would
// be spread into the holes as streaks of the object). Drawn with the walls'
// own vertex shader into the "behind" image.
layout(binding = 0) uniform sampler2D frame;     // the frame copy
layout(binding = 1) uniform sampler2D viewDepth; // the runtime's, at its render resolution
layout(location = 0) out vec4 color;
void main() {
  const ivec2 pixel = ivec2(gl_FragCoord.xy);
  const vec2 uv = gl_FragCoord.xy / vec2(textureSize(frame, 0));
  const ivec2 depthSize = textureSize(viewDepth, 0);
  const ivec2 center = ivec2(uv * vec2(depthSize));
  const float wall = 1.0 / gl_FragCoord.w; // clip w: the distance along the camera's axis
  for (int y = -2; y <= 2; ++y)
    for (int x = -2; x <= 2; ++x) {
      const float scene = texelFetch(viewDepth, clamp(center + ivec2(x, y), ivec2(0), depthSize - 1), 0).r;
      if (scene < wall * 0.98)
        discard;
    }
  color = vec4(texelFetch(frame, pixel, 0).rgb, 1);
}
