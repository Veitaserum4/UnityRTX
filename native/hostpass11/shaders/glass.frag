#version 450
// The runtime's sheets (glass, water, blended surfaces) back over the
// replayed walls behind them. The path-traced frame is the sheets' light plus
// their transmittance t times what is behind them, there the wall as the path
// tracer drew it; the walls' shader then drew its own look over all of it. As
// the sheets would show that look: before + t (look - behind), in the
// display's linear values (the frame is gamma 2.2), where a wall shows behind
// a sheet (`cover`: a wall's pixel with no other surface in front of it).
layout(binding = 0) uniform sampler2D now;           // the frame with the walls drawn
layout(binding = 1) uniform sampler2D before;        // the frame before them
layout(binding = 2) uniform sampler2D behind;        // what the path tracer drew behind the walls (filled)
layout(binding = 3) uniform sampler2D transmittance; // the runtime's, at its render resolution
layout(binding = 4) uniform sampler2D cover;         // alpha 1 on the walls' pixels
layout(location = 0) in vec2 uv;
layout(location = 0) out vec4 color;
void main() {
  const ivec2 pixel = ivec2(gl_FragCoord.xy);
  if (texelFetch(cover, pixel, 0).a == 0)
    discard;
  const vec3 t = texture(transmittance, uv).rgb;
  if (all(greaterThanEqual(t, vec3(0.995))))
    discard;
  const vec3 look = pow(texelFetch(now, pixel, 0).rgb, vec3(2.2));
  const vec3 frame = pow(texelFetch(before, pixel, 0).rgb, vec3(2.2));
  const vec3 wall = pow(texelFetch(behind, pixel, 0).rgb, vec3(2.2));
  color = vec4(pow(max(frame + t * (look - wall), vec3(0)), vec3(1 / 2.2)), 1);
}
