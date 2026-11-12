#version 450
// The frame where the replayed walls show (what is behind them, the image
// their shader samples), alpha 1; discarded where something is in front of
// a wall (the runtime's view depth nearer than the wall's own). Drawn with
// the walls' own vertex shader into the "behind" image.
layout(binding = 0) uniform sampler2D frame;     // the frame copy
layout(binding = 1) uniform sampler2D viewDepth; // the runtime's, at its render resolution
layout(location = 0) out vec4 color;
void main() {
  const ivec2 pixel = ivec2(gl_FragCoord.xy);
  const vec2 size = vec2(textureSize(frame, 0));
  const float scene = texture(viewDepth, gl_FragCoord.xy / size).r;
  const float wall = 1.0 / gl_FragCoord.w; // clip w: the distance along the camera's axis
  if (scene < wall * 0.98)
    discard;
  color = vec4(texelFetch(frame, pixel, 0).rgb, 1);
}
