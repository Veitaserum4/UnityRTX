#version 450
// The runtime's sheets (glass, water, blended surfaces) back over the
// replayed walls behind them. The path-traced frame is the sheets' light plus
// their transmittance t times what is behind them, there the wall as the path
// tracer drew it; the walls' shader then drew its own look over all of it. As
// the sheets would show that look: before + t (look - behind), in the
// display's linear values (the frame is gamma 2.2), where a wall shows behind
// a sheet (`cover`: a wall's pixel with no other surface in front of it).
//
// push.view (UnityRTX's [Debug] PassThroughLayersView), over the whole frame:
// 1 the transmittance; 2 the walls' pixels (red: cover, over the frame
// darkened); 3 the depths (red: an opaque surface, green: a sheet is the view
// depth there, blue: the view depth is the opaque one).
layout(binding = 0) uniform sampler2D now;           // the frame with the walls drawn
layout(binding = 1) uniform sampler2D before;        // the frame before them
layout(binding = 2) uniform sampler2D behind;        // what the path tracer drew behind the walls (filled)
layout(binding = 3) uniform sampler2D transmittance; // the runtime's, at its render resolution
layout(binding = 4) uniform sampler2D cover;         // alpha 1 on the walls' pixels
layout(binding = 5) uniform sampler2D viewDepth;     // the runtime's, at its render resolution
layout(binding = 6) uniform sampler2D opaqueDepth;   // the runtime's, at its render resolution
layout(push_constant) uniform Push {
  int view, unused0, unused1;
} push;
layout(location = 0) in vec2 uv;
layout(location = 0) out vec4 color;
void main() {
  const ivec2 pixel = ivec2(gl_FragCoord.xy);
  const float covered = texelFetch(cover, pixel, 0).a;
  const vec3 t = texture(transmittance, uv).rgb;
  if (push.view == 1) {
    color = vec4(t, 1);
    return;
  }
  if (push.view == 2) {
    color = vec4(mix(texelFetch(before, pixel, 0).rgb * .3, vec3(1, 0, 0), covered), 1);
    return;
  }
  if (push.view == 3) {
    const float view = texture(viewDepth, uv).r, opaque = texture(opaqueDepth, uv).r;
    color = vec4(opaque < 1e9 ? 1 : 0, view < opaque * .999 ? 1 : 0, abs(view - opaque) <= opaque * .001 ? 1 : 0, 1);
    return;
  }
  if (covered == 0 || all(greaterThanEqual(t, vec3(0.995))))
    discard;
  const vec3 look = pow(texelFetch(now, pixel, 0).rgb, vec3(2.2));
  const vec3 frame = pow(texelFetch(before, pixel, 0).rgb, vec3(2.2));
  const vec3 wall = pow(texelFetch(behind, pixel, 0).rgb, vec3(2.2));
  color = vec4(pow(max(frame + t * (look - wall), vec3(0)), vec3(1 / 2.2)), 1);
}
