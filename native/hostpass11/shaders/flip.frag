#version 450
// The frame upside down, for draws reading it as a GrabPass texture: Unity's
// ComputeGrabScreenPos flips y on D3D whatever the target (unlike
// ComputeScreenPos, which follows _ProjectionParams.x), so the grab texture it
// expects is upside down.
layout(binding = 0) uniform sampler2D frame;
layout(location = 0) out vec4 color;
void main() {
  const ivec2 size = textureSize(frame, 0);
  const ivec2 pixel = ivec2(gl_FragCoord.xy);
  color = texelFetch(frame, ivec2(pixel.x, size.y - 1 - pixel.y), 0);
}
