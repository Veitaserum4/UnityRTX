#version 450
// A blood stain, as ULTRAKILL composites them: where the splatter atlas is
// opaque, the frame keeps only its red channel (drawn with a multiply blend,
// so overlapping stains merge into one: (1, 0, 0) twice is (1, 0, 0)). The
// surface stays lit as the path tracer lit it.
//
// The stain shows only on the surface it lies on: discarded where the
// runtime's view depth is nearer (something in front of it, out to +-1 render
// pixel: the frame is upscaled and antialiased, an object's outline mixes it
// with what is behind) or farther (the quad hangs past an edge, over a drop
// or the sky).
layout(binding = 0) uniform sampler2D splatter;  // the atlas, as Unity samples it
layout(binding = 1) uniform sampler2D viewDepth; // the runtime's, at its render resolution
layout(location = 0) in vec2 atlas;
layout(location = 1) in vec3 clip;
layout(location = 0) out vec4 color;
void main() {
  const float stain = clip.z; // the distance along the camera's axis
  const vec2 uv = vec2(clip.x / clip.z * .5 + .5, .5 - clip.y / clip.z * .5);
  const ivec2 depthSize = textureSize(viewDepth, 0);
  // Allowing for the stain's own depth changing across a render pixel (a floor
  // seen at a glancing angle) and for rounding.
  const float outputPerTexel = 1.0 / max(fwidth(uv.x) * float(depthSize.x), 1e-3);
  const float tolerance = stain * 0.004 + 0.02 + fwidth(stain) * (1.5 * outputPerTexel + 1.0);
  if (texture(splatter, atlas).a < 0.999)
    discard;
  const ivec2 center = clamp(ivec2(uv * vec2(depthSize)), ivec2(0), depthSize - 1);
  if (texelFetch(viewDepth, center, 0).r > stain + tolerance)
    discard;
  for (int y = -1; y <= 1; ++y)
    for (int x = -1; x <= 1; ++x)
      if (texelFetch(viewDepth, clamp(center + ivec2(x, y), ivec2(0), depthSize - 1), 0).r < stain - tolerance)
        discard;
  color = vec4(1, 0, 0, 1);
}
