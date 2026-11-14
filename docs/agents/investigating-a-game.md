# Investigating a game

Most wrong-looking things are a mistranslation: UnityRTX guessed how the game draws
something and guessed wrong. Find out how the game actually draws it before changing code.
Its C# shows behaviour, its shaders show the look, and a runtime log shows what's actually
in the scene.

## Compare side by side

Run OpenRemix in single-window mode next to the unmodded game, at the same spot (or with
the same screenshot). Describe the difference precisely before guessing: wrong colour,
alpha, position, missing, extra, flickering, wrong only in one level.
- **Extra** things that aren't in the original are usually something the game draws only
  through a shader effect (distortion sprites, invisible helper geometry, trigger volumes).
  Hide them: `IsHiddenMaterial`, `IsNeverRenderedLayer`.
- **Missing** things are usually drawn by something the capture doesn't read (a custom
  renderer, a `CommandBuffer`, `Graphics.DrawMesh`, a LineRenderer) or skipped by a
  heuristic.
- Toggling an option in the Remix menu (F12 → Plugin) or setting `GameProfile = None`
  tells you whether the profile is involved.

## Runtime logs

`BepInEx/LogOutput.log`. Turn on `[Debug] VerboseLogging` in `BepInEx/config/com.Unity.remix.cfg`
for per-object descriptions, reproduce, then read the log. Useful lines:
- `[ParticleSystem] '<name>': <mode>, ... mat '<m>' shader '<s>' queue <q> (blend ...), look <kind> tint (...) x<scale>, sample (...), size <n> ... | Remix <how it was submitted>`:
  one per particle system. This is how Greed's "pale haze" was found to be
  `ULTRAKILL/HeatWave` heat distortion sprites.
- `[ParticleTrail]`, `[MeshParticles]`, `[ParticleSnapshot]`: trails, mesh particles, and
  procedural particle materials.
- `[ShaderPassThrough]`: see [shader-pass-through.md](shader-pass-through.md#4-verify).
- Scene scan timing and counts, light conversion, material resolution: grep for the
  subsystem's tag.

When the code can't tell you what's in a scene (which objects sit where, serialized
references, which material a renderer has), add a temporary one-off log line or a
`VerboseLog.On` description. That's faster than extracting assets. Remove one-off lines
before the PR, or move them behind `VerboseLog.On`.

## The game's code

Decompile `<game>_Data/Managed/Assembly-CSharp.dll`:

```powershell
dotnet tool install -g ilspycmd --version 9.1.0.7988   # newer versions may not install on every SDK
ilspycmd -t <TypeName> "<game>\<game>_Data\Managed\Assembly-CSharp.dll"   # one class
ilspycmd -p -o decomp "<game>\<game>_Data\Managed\Assembly-CSharp.dll"     # whole project
```

Look for:
- what spawns or toggles objects (for `IsDynamicObject` / `FollowsActiveState` / spawn
  patches);
- what sets shader properties every frame (`Material.SetFloat`, `Shader.SetGlobal*`);
- custom renderers (`Graphics.DrawMesh*`, `CommandBuffer`), and post-process handlers.

Never commit decompiled code. Reference type and member names only, resolved by name at
runtime.

## Shaders

Unity games ship shaders compiled. To see what a custom shader does:
- **Its passes and properties**: load the shader object's typetree with
  [UnityPy](https://github.com/K0lb3/UnityPy).
  - Shaders are in `<game>_Data/sharedassets*.assets`, `resources.assets`, or Addressables
    bundles under `StreamingAssets/aa/**`.
  - Read `m_ParsedForm` → `m_Name`, `m_PropInfo.m_Props` (properties), and
    `m_SubShaders[].m_Passes[]`:
    - `m_Type`: 0 normal, 2 GrabPass;
    - `m_TextureName`: the grab texture;
    - `m_State`: blend `rtBlend0`, `zWrite`, `colMask`, tags.
- **Its code**: the DX11 programs are under
  `m_Passes[].progVertex/progFragment.m_PlayerSubPrograms[3]` (index 3 is DX11). Each one
  has `m_BlobIndex` and `m_KeywordIndices` (into `m_KeywordNames`).
  - The blob is the shader's `compressedBlob`: LZ4 segments of platform 0, split by
    `offsets` / `compressedLengths` / `decompressedLengths`.
  - Segment 0 starts with a count, followed by `(offset, length, segment)` entries.
  - The program's DXBC starts at the `DXBC` magic, with its size at magic + 24.
  - Disassemble it with `d3dcompiler_47.dll`'s `D3DDisassemble` (through ctypes).
  - `tools/investigation/` has helper scripts.
- UnityPy's own shader export can fail on these programs (unknown GpuProgramType). The
  typetree route works regardless.
- Reading the disassembly: `cb0` is usually the material's constants; `cb1` is
  `UnityPerCamera` (`_Time` at [0], `_ProjectionParams` at [5]); `cb2` is `UnityPerDraw`
  (`unity_ObjectToWorld`); `cb3` is `UnityPerFrame` (`unity_MatrixVP` at [17..20]).
  - Unity strips the resource names (RDEF), so match textures to properties by the order
    of `m_PropInfo` and the sampling code, or at runtime (pass-through logs the match).

Typical findings and where they go:
- **A formula UnityRTX should apply** (tint × 2, an alpha remap, a palette) goes in the
  material conversion: generic if it's a standard Unity shader, through a profile hook if
  it's the game's own.
- **An effect only the shader can draw** (screen-space, GrabPass, depth-based) becomes
  [shader pass-through](shader-pass-through.md), plus hiding it from the path tracer.

Never commit extracted shaders, textures or other game assets.
