# UnityRTX

A BepInEx plugin that path-traces Unity games with [OpenRemix](https://github.com/Veitaserum4/OpenRemix-J).
It reads each frame's scene from Unity (meshes, skinned meshes, particles, materials, lights, sky,
terrain, water, fog) and submits it to OpenRemix, which renders it in place of the game's own
cameras.

## Features

- Static, skinned, GPU-instanced and statically batched geometry; particle systems and trails
- Materials and textures, including scrolling UVs and layered stylized shaders
- Point, spot and directional lights; procedural and panoramic skies; fog
- Ray portals (a game's portals, and screens that show another view)
- Shader pass-through: a game's own D3D11 shaders replayed over the path-traced frame (screen
  effects such as a TV wall's distortion), the way SourcePT does for Source
- Game profiles for game-specific behaviour (`src/Games/<Game>/`, picked by executable name)

## Requirements

- Windows (or Proton), a Vulkan ray-tracing GPU
- A Unity 2019+ game on Mono (not IL2CPP), D3D11
- [BepInEx 5](https://github.com/BepInEx/BepInEx/releases)
- An OpenRemix Windows runtime

## Installation

Install BepInEx 5 in the game, then put UnityRTX in its plugin folder:

```
<game>/
  <game>.exe, winhttp.dll, doorstop_config.ini     BepInEx's loader
  BepInEx/plugins/UnityRTX/
    UnityRemix.dll                                 the plugin
    UnityRTXHostPass.dll                           shader pass-through's D3D11 capture
    openremix/                                     OpenRemix's runtime: remix.dll, SDL3.dll,
                                                   usd_*.dll, usd/, plugin/, dlss/, ...
```

`openremix/` is the folder OpenRemix's `scripts/windows/package-runtime.py` produces. OpenRemix
keeps its own data in the game folder: `openremix/` (profiles, caches) and `rtx-remix/` (captures,
mods). A `remix.dll` in the game folder itself (the previous layout) is still used when the plugin
folder has none.

Shader pass-through needs Unity's graphics jobs off; for games whose profile uses it, UnityRTX
turns them off in `<game>_Data/boot.config` (backed up as `boot.config.unityrtx-backup`) and it
takes effect from the next launch.

Press **F12** (or Alt+X) in game for OpenRemix's settings, including the plugin's own tab.
Plugin settings are also in `BepInEx/config/com.Unity.remix.cfg`.

## Repository layout

```
src/                     the plugin (C#, src/UnityRTX.csproj)
  Core/                  plugin entry point, render thread, install layout
  Remix/                 OpenRemix API bindings, ImGui, native log forwarding
  Capture/               reading the scene: frame capture, scene scan, mesh readers
  Conversion/            meshes, materials, textures, lights to OpenRemix
  Scene/                 sky, terrain, water, fog, camera, ray portals
  Bake/                  baked scene for OpenRemix's Forward+ renderer
  Presentation/          window, single-window presentation, UI capture
  UI/                    settings tab, debug HUD, menu cursor
  ShaderPassThrough/     replaying the game's own shaders (with native/hostpass11)
  Diagnostics/           profiler, Tracy, watchdog, diagnostic logs
  Util/                  hashing, Unity version compatibility
  Games/                 game profiles (GameProfile, and one folder per game)
native/                  native code (Meson, clang-cl)
  hostpass11/            UnityRTXHostPass.dll: D3D11 capture and host-pass replay
  deps/dxbc/             DXVK's DXBC to SPIR-V compiler, standalone
scripts/                 build and deploy (build.ps1 for Windows, build.sh for Linux/Proton)
tests/                   API compatibility checks
docs/agents/             contributor guides (game profiles, pass-through, investigation)
tools/investigation/     one-off scripts from investigating games' assets and code
```

Adding support for a game (game profiles, shader pass-through, investigating a game): see
[AGENTS.md](AGENTS.md) and [docs/agents/](docs/agents/).

## Building

### Windows

Requirements: the .NET SDK, Visual Studio's C++ tools with clang-cl, Meson and Ninja (for
`native/`), the Vulkan SDK's `glslangValidator`, Python 3 (to package OpenRemix's runtime).

```powershell
# Plugin only (copies the game's assemblies into src/lib for the build)
.\scripts\build.ps1 -GamePath "C:\...\ULTRAKILL"

# Build and install the plugin and UnityRTXHostPass.dll
.\scripts\build.ps1 -GamePath "C:\...\ULTRAKILL" -Deploy -Native

# Also package and install OpenRemix's runtime from a built checkout
.\scripts\build.ps1 -GamePath "C:\...\ULTRAKILL" -Deploy -Native `
    -OpenRemix "C:\...\OpenRemix" -OpenRemixBuild _work\build\windows-usd
```

### Linux (Proton)

```bash
sudo pacman -Syu --needed dotnet-sdk netstandard-targeting-pack   # Arch

./scripts/build.sh                                  # build only
./scripts/build.sh --deploy                         # plugin and OpenRemix's runtime
./scripts/build.sh --deploy --plugin-only           # plugin only
./scripts/build.sh --unity-path "/path/to/game" --openremix-path "/path/to/openremix" --deploy
```

Build OpenRemix's Windows runtime first with `scripts/linux/build-windows-remix.py` in its
checkout. Replaced files are backed up under `BepInEx/UnityRTX-backups/`. `native/` is built on
Windows; a `native/_build/hostpass11/UnityRTXHostPass.dll` that exists is deployed too.

### API compatibility checks

```bash
dotnet run --project tests/RemixApiCompatibility
```
