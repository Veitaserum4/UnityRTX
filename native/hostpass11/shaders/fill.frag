#version 450
// One step of filling the "behind" image's holes (alpha 0: something stood in
// front of the walls there) from pixels `offset` away on either side, so the
// walls' shader, sampling around its own pixel (chromatic aberration, noise
// jitter), finds what is behind the walls rather than what is in front. The
// last step fills what is left from the frame.
layout(binding = 0) uniform sampler2D source;
layout(binding = 1) uniform sampler2D frame;
layout(push_constant) uniform Push {
  int dx, dy, last;
} push;
layout(location = 0) out vec4 color;
void main() {
  const ivec2 pixel = ivec2(gl_FragCoord.xy);
  const ivec2 limit = textureSize(source, 0) - 1;
  vec4 c = texelFetch(source, pixel, 0);
  if (c.a == 0) {
    const ivec2 offset = ivec2(push.dx, push.dy);
    const vec4 before = texelFetch(source, clamp(pixel - offset, ivec2(0), limit), 0);
    const vec4 after = texelFetch(source, clamp(pixel + offset, ivec2(0), limit), 0);
    c = before.a != 0 ? before : after;
  }
  if (push.last != 0 && c.a == 0)
    c = vec4(texelFetch(frame, pixel, 0).rgb, 1);
  color = c;
}
