# UnityRTX
A BepInEx plugin that brings path tracing to most modern Unity games, rendered by
[OpenRemix](https://github.com/Veitaserum4/OpenRemix-J).

| Supported Features |   |
|--------------------|---|
| Skinned meshes     |✅|
| Remix Replacements |✅|
| Basic textures     |✅|
| Scrolling and layered (stylized) materials |✅|
| Point and spot lights (that are not baked) |✅|
| Directional lights |✅|
| Sky, fog and water |✅|
| Particle systems, trails and line renderers |✅|
| GPU-Instanced / Statically Batched Geometry |✅|
| Hardware Skinning |✅|
| Ray portals (portals, mirrors, screens showing another view) |✅|
| Shader pass-through (a game's own D3D11 screen effects and distortion) |✅|
| Per-game profiles |✅|

## Requirements

- BepInEx 5.x
- A Unity 2019+ game that uses MonoBleedingEdge, on D3D11
- A Vulkan ray tracing GPU (Windows, or Linux through Proton)
<img width="600" alt="image" src="https://github.com/user-attachments/assets/421d12cb-e19d-40bb-89ec-69da1370e653" />


## Installation
1. Install BepInEx 5 in your Unity game
2. Put UnityRTX in `BepInEx/plugins/UnityRTX/`:
   - `UnityRemix.dll` (the plugin)
   - `UnityRTXHostPass.dll` (shader pass-through)
   - `openremix/` (OpenRemix's runtime: `remix.dll` and the files beside it)
3. Launch the game

Press **F12** (or Alt+X) in game for OpenRemix's settings, including the plugin's own tab.

Games whose profile uses shader pass-through need Unity's graphics jobs off. UnityRTX turns them off in
`<game>_Data/boot.config` (backed up as `boot.config.unityrtx-backup`); this takes effect from the
next launch.

## Configuration

Edit `BepInEx/config/com.Unity.remix.cfg`, or use the Plugin tab in the F12 menu.

- `[Game] GameProfile`: `Auto` (picked by executable name), `None`, or a profile's name
- `[Debug] VerboseLogging`: detailed per-object diagnostics in `BepInEx/LogOutput.log`

## Supported games

| Game | Profile |
|------|---|
| ULTRAKILL | ✅ `src/Games/Ultrakill/` |

Other Unity games run through the generic bridge without a profile. To add one, see
[Contributing](#contributing).

## Building

Requirements: a .NET SDK capable of targeting .NET Standard 2.1, BepInEx 5.x installed in a game
(its Unity Mono managed assemblies are copied into `src/lib/` to build against). For `native/`:
Visual Studio's C++ tools with clang-cl, Meson, Ninja and the Vulkan SDK's `glslangValidator`.

### Windows / PowerShell

```powershell
# Build the plugin
.\scripts\build.ps1 -GamePath "C:\Program Files (x86)\Steam\steamapps\common\ULTRAKILL"

# Build and install the plugin and UnityRTXHostPass.dll
.\scripts\build.ps1 -GamePath "C:\...\ULTRAKILL" -Deploy -Native

# Also package and install OpenRemix's runtime from a built checkout
.\scripts\build.ps1 -GamePath "C:\...\ULTRAKILL" -Deploy -Native `
    -OpenRemix "C:\...\OpenRemix" -OpenRemixBuild _work\build\windows-usd
```

### Linux

On Arch Linux, install the build tools:

```bash
sudo pacman -Syu --needed dotnet-sdk netstandard-targeting-pack
```

From the repository root:

```bash
./scripts/build.sh                                  # build only
./scripts/build.sh --deploy                         # plugin and OpenRemix's runtime
./scripts/build.sh --deploy --plugin-only           # plugin only
./scripts/build.sh --unity-path "/path/to/game" --openremix-path "/path/to/openremix" --deploy
```

Build OpenRemix's Windows runtime first with `python3 scripts/linux/build-windows-remix.py` in its
checkout. Replaced files are backed up under `BepInEx/UnityRTX-backups/`. `native/` is built on
Windows; a `native/_build/hostpass11/UnityRTXHostPass.dll` that exists is deployed too.

### API compatibility checks

```bash
dotnet run --project tests/RemixApiCompatibility
```

## Contributing

Game-specific fixes go in a game profile, never in the shared code. Read [AGENTS.md](AGENTS.md)
first (written for AI coding agents and people alike), then the guide for your task:

- [Game profiles](docs/agents/game-profiles.md): adding a game, its hooks, gating by executable
- [Shader pass-through](docs/agents/shader-pass-through.md): replaying a game's own D3D11 shaders
- [Investigating a game](docs/agents/investigating-a-game.md): logs, decompiling, shader bundles
- [OpenRemix changes](docs/agents/openremix-changes.md): when the renderer needs a new feature
