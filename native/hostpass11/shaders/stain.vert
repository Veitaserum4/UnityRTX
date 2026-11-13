#version 450
// A blood stain's quad (world position, atlas coordinates), in the path-traced
// camera's view: the push constants are the rows of its view-projection (D3D's
// clip space, y up; w the distance along the camera's axis).
layout(location = 0) in vec3 position;
layout(location = 1) in vec2 atlasIn;
layout(push_constant) uniform Push {
  vec4 rows[4];
} push;
layout(location = 0) out vec2 atlas;
layout(location = 1) out vec3 clip; // x, y, w
void main() {
  const vec4 p = vec4(position, 1);
  const vec4 c = vec4(dot(push.rows[0], p), dot(push.rows[1], p), dot(push.rows[2], p), dot(push.rows[3], p));
  gl_Position = c;
  atlas = atlasIn;
  clip = c.xyw;
}
