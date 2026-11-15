#version 450
// wall_mask.frag, where the runtime tells what its sheets let through
// (REMIXAPI_HOST_TARGET_VIEW_TRANSMITTANCE_EXT): the depth is that of the
// first surface that is not a sheet, and a wall seen through glass, water or
// a blended surface is a hole too (the frame there is the sheet over it, not
// what the wall shows), filled from beside like one behind an object.
layout(binding = 0) uniform sampler2D frame;         // the frame copy
layout(binding = 1) uniform sampler2D viewDepth;     // the runtime's opaque depth, at its render resolution
layout(binding = 2) uniform sampler2D transmittance; // the runtime's, at its render resolution
layout(location = 0) out vec4 color;
void main() {
  const ivec2 pixel = ivec2(gl_FragCoord.xy);
  const vec2 uv = gl_FragCoord.xy / vec2(textureSize(frame, 0));
  const ivec2 depthSize = textureSize(viewDepth, 0);
  const ivec2 center = ivec2(uv * vec2(depthSize));
  const float wall = 1.0 / gl_FragCoord.w;
  const float pixelsPerTexel = float(textureSize(frame, 0).x) / float(depthSize.x);
  const float tolerance = wall * 0.002 + fwidth(wall) * (2.5 * pixelsPerTexel + 1.0);
  for (int y = -2; y <= 2; ++y)
    for (int x = -2; x <= 2; ++x) {
      const ivec2 at = clamp(center + ivec2(x, y), ivec2(0), depthSize - 1);
      if (texelFetch(viewDepth, at, 0).r < wall - tolerance || any(lessThan(texelFetch(transmittance, at, 0).rgb, vec3(0.995))))
        discard;
    }
  color = vec4(texelFetch(frame, pixel, 0).rgb, 1);
}
