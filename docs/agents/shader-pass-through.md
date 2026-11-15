# Shader pass-through (D3D11 host passes)

Some of what a game draws can't be path-traced:
- screen effects (post-processing, a death screen, palette and dithering);
- screen-space distortion (heat haze, GrabPass refraction);
- surfaces whose shader shows a view of the scene (TV screens that sample a render texture).

Shader pass-through makes Unity draw those things with the game's **own D3D11 shaders**,
captures the draws, and replays them as OpenRemix **host passes** over the path-traced frame.
SourcePT does the same for Source's D3D9 shaders.

Use it when the shader *is* the look. Before choosing it, ask whether a path-traced
translation (a material, a light, a ray portal) would be truer. Pass-through draws are
composited on top of the image: they don't cast light, don't show in reflections, and are
lit by nothing.

## How it works

```
main thread (ShaderPassThrough.Capture, once a frame)
  profile -> renderers (GetPassThroughRenderers) + screen effect (PassThroughScreenEffect)
  hidden helper camera = copy of the game camera, cullingMask 0, target = screen-sized RT
  command buffer (CameraEvent.AfterForwardOpaque):
      SetGlobalTexture(_GrabTexture, stand-in)
      per renderer/material: IssuePluginEvent(Begin | tag) ; DrawRenderer(r, mat, sub, PassThroughShaderPass(mat))
      screen effect:         IssuePluginEvent(Begin | 0xfffe) ; Blit(screenSource, target, copy of the game's material)
      IssuePluginEvent(End)
  helper.Render()
  PrepareReplay: projection, frame/grab/screen texture identities, upload textures the draws sample

render thread (native/hostpass11, UnityRTXHostPass.dll)
  hooks.cpp    vtable hooks on Unity's ID3D11Device / immediate context: shaders (DXBC kept at
               creation), input layouts, buffers, and the state the draws use (cbuffers, SRVs,
               samplers, VB/IB, topology, viewport, blend)
  capture.cpp  between Begin and End: each draw recorded with copies of its state and data
  shaders.cpp  DXBC -> SPIR-V (native/deps/dxbc, DXVK's compiler), resources mapped to bindings

presenting thread (Remix render thread, ShaderPassThrough.Submit before each present)
  replay.cpp   host passes over the path-traced frame:
               stains -> copy frame (kFrameCopy) -> frame depth from Remix's view depth
               (behind its sheets, with opaque draws) -> [opaque draws: "behind the walls"
               fill] -> opaque draws, depth-tested -> [the sheets put back over them:
               glass.frag] -> blended draws, with their own blend, over a new copy ->
               screen effects, each over a copy of the composed frame
```

Things to know about the pipeline:
- UnityRTX suppresses the game's world cameras, so the helper camera's draws are the only
  draws of those renderers Unity makes.
- The replay depth-tests each draw against the path-traced depth, converted to the helper
  camera's projection. It never writes depth.
- Path-traced sheets (glass, water, blended surfaces) in front of an opaque draw:
  - The opaque draws test against the depth of the first surface that isn't a sheet
    (`REMIXAPI_HOST_TARGET_VIEW_OPAQUE_DEPTH_EXT`), so they're drawn behind sheets too.
  - `glass.frag` then puts the sheets back over them, from what the sheets let through
    (`REMIXAPI_HOST_TARGET_VIEW_TRANSMITTANCE_EXT`): before + t × (look − behind).
  - A remix.dll without those targets refuses the list once. The replay then falls back
    to hiding opaque draws behind sheets, and logs it.
- Textures the frame **stands in for**: whatever `PassThroughFrameTexture(material)` returns
  (a render texture the game's shader samples as a view of the scene), plus the
  `_GrabTexture` stand-in. The replay samples the path-traced frame wherever a draw samples
  those.
  - Opaque draws sample the frame with themselves cut out and the hole filled from beside it
    (Limbo's walls sample around their own pixels).
  - Blended draws sample the frame as it is.
  - The `_GrabTexture` stand-in samples the frame **upside down** (`kGrabCopy`, made by
    `flip.frag`). Unity's `ComputeGrabScreenPos` flips y on D3D whatever the render target,
    unlike `ComputeScreenPos`. Sampled right way up, a GrabPass distortion shows a mirrored
    copy of the screen.
- **Other textures** are read back from Unity once (`SendTexture`, through a blit, so they
  needn't be readable) and uploaded.
  - They're sent again when `Texture.updateCount` changes.
  - A compute shader's writes don't change `updateCount`. Call
    `ShaderPassThrough.TextureChanged(texture)` for those; ULTRAKILL's palette LUT is the
    example (`UltrakillProfile.WatchPalette`).
- **Screen effects** (draws tagged `0xfffe`) are full-screen. Each samples a copy of the
  composed frame at `PassThroughScreenResolution` (nearest filtering, matching a game's
  downsampling), in place of `_MainTex`.

## Requirements and limits

- **D3D11 only.** No graphics jobs: draws on deferred contexts aren't captured.
  - When a profile sets `UsesShaderPassThrough => true`, UnityRTX turns graphics jobs off
    in `<game>_Data/boot.config` (backed up as `boot.config.unityrtx-backup`). It takes
    effect from the next launch.
- Shaders must be created after the hooks install, which happens at plugin load. Shaders
  Unity created earlier are reported as "created before the hooks" and skipped.
- Only triangle lists are replayed. Instanced draws are captured only with one instance.
  Vertex buffers are read from slots 0-3.
- Vertex shaders may not sample textures. Pixel shaders may sample 2D textures and 3D
  textures (volumes).
- Blending: render target 0's D3D11 blend state (factors, ops, write mask), with a single
  render target.
- Unity renders into a render texture upside down on D3D. The replay flips the viewport and
  sets `_ProjectionParams.x` to 1 in the 144-byte `UnityPerCamera` buffer. Shaders that work
  out the screen position some other way may come out flipped.
- Pass-through is composited after path tracing (and after upscaling and tonemapping for
  screen effects). It doesn't appear in reflections.

## Adding a pass-through effect

### 1. Understand the shader first

Get the shader's passes, properties and disassembly before writing code. See
[investigating-a-game.md](investigating-a-game.md#shaders). You need to know:
- **The passes.** Is pass 0 a GrabPass? Which pass draws, and with what blend, ZWrite and
  ColorMask?
- **Which textures it samples, and which of them shows the scene:**
  - a `GrabPass` texture (`_GrabTexture`, or a named grab texture);
  - a render texture of a camera's view (Limbo's `_LimboSky`);
  - `_MainTex` in a full-screen `Blit`.
- **What it reads from the scene that Remix doesn't have.** `_CameraDepthTexture`, for
  example, isn't available.

### 2. Pick the kind

| Kind | Example | Profile hooks |
| --- | --- | --- |
| A world renderer whose shader shows a texture standing for the scene | Limbo's TV walls (`LIMBO_WALLS`, `_LimboSky`) | `GetPassThroughRenderers`, `PassThroughFrameTexture`, `UsesShaderPassThrough` |
| A world renderer with a GrabPass (distortion, refraction) | Greed's heat haze (`ULTRAKILL/HeatWave` particles) | `GetPassThroughRenderers`, `PassThroughShaderPass` (the pass after the GrabPass), `IsHiddenMaterial` (so it isn't also path-traced) |
| A full-screen effect the game blits | ULTRAKILL's PostProcessV2 (death screen, Greed's WICKED distortion, palette/dither/downscale) | `PassThroughScreenEffect`, `PassThroughScreenResolution` |

### 3. Implement it in the profile

- `GetPassThroughRenderers(camera, list)` adds the renderers each frame.
  - Find them by a cached scan, refreshed on a timer and reset in `OnSceneLoaded`; see
    `UltrakillProfile.GetPassThroughRenderers`.
  - ShaderPassThrough culls them against the camera's frustum itself.
- **Hide them from the path tracer** (`IsHiddenMaterial`) when their texture only drives the
  shader. Otherwise they're drawn twice: once as a textured surface, once replayed. The heat
  haze was white 30-unit sprites before it was hidden.
- `PassThroughShaderPass(material)` returns the pass index to draw.
  - Never draw a GrabPass: the replay binds the stand-in as `_GrabTexture` and samples the
    path-traced frame in its place.
- `PassThroughScreenEffect()` returns the game's screen material while the effect shows,
  and null otherwise.
  - The capture blits a copy of it each frame, with the game's keywords and values copied
    over.
  - Returning null when nothing shows keeps the replay idle.
  - If the game sets the effect's values from its own code, read them where the game sets
    them. ULTRAKILL's PostProcessV2_Handler is the example.
- Set `UsesShaderPassThrough => true`.

### 4. Verify

- `LogOutput.log` gets a `[ShaderPassThrough]` report every 600 frames (always the first
  five, then whenever the draws change). For each captured draw it lists:
  - its shaders' compile status;
  - bindings;
  - constant buffers;
  - textures;
  - vertex inputs;
  - blend state.
- The submit part of the report says how many passes were submitted and the first reason a
  draw was skipped. For example: "a texture has not been uploaded yet", "not a triangle
  list", or "the vertex shader reads COLOR the layout lacks".
- `[ShaderPassThrough] resource 0x... is <material>.<property>` lines show which material
  property each sampled texture was matched to. `no texture found for resource` means the
  draw can't be replayed: usually an engine-internal texture the profile has to provide or
  stand in for.
- `BepInEx/plugins/UnityRTX/UnityRTXHostPass.log`: hook installation, vtables patched,
  shaders created before the hooks.
- Compare against the unmodded game, side by side. Check:
  - orientation (flipped?);
  - depth (hidden behind walls?);
  - blend (too strong or weak?);
  - animation (does `_Time` move?).

## Changing the native side

- Build with `.\scripts\build.ps1 -GamePath ... -Deploy -Native`. The DLL is
  `native/_build/hostpass11/UnityRTXHostPass.dll`.
- **Generic** capture or replay features belong in `native/hostpass11`: a newly tracked
  state, a texture type, a topology. For example, OMSetBlendState is context slot 35.
  - Context vtable slots are listed in `patchContext`.
  - After every hooked state call, call `rehookDraws(c)`: d3d11 rewrites the draw slots of
    the immediate context's vtable as state changes.
  - New state goes in `State` (`hostpass.h`), copied into `Draw` by `recordDraw`
    (`capture.cpp`), and used by `drawPass` (`replay.cpp`).
- New exports go in `api.cpp`, with the matching `[DllImport]` in `ShaderPassThrough.cs`.
- Avoid **game-specific** native code.
  - Exception: something the game's own shader can't be replayed for, such as ULTRAKILL's
    blood stain overlay (`stain.vert/frag`), which composites the game's stain quads.
  - Keep such code small, behind its own export, and driven by the profile.
- The replay's own GLSL shaders live in `native/hostpass11/shaders/`. They're compiled to C
  arrays at build time (listed in `meson.build`).
- Host passes are OpenRemix API (`remixapi_RegisterHostShaderEXT`, `SubmitHostPassesEXT`,
  `UpdateHostTargetEXT`, `UpdateHostVolumeEXT`). If they lack something, see
  [openremix-changes.md](openremix-changes.md).
