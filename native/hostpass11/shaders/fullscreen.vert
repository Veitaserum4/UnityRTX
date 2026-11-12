#version 450
// A full-screen triangle (three clip-space vertices); `uv` addresses the
// runtime's images, row 0 at the top as the frame's.
layout(location = 0) in vec2 position;
layout(location = 0) out vec2 uv;
void main() {
  gl_Position = vec4(position, 0, 1);
  uv = vec2(position.x * .5 + .5, .5 - position.y * .5);
}
