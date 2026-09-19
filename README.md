# UnityRTX
A BepInEx plugin that brings NVIDIA RTX Remix path tracing to most modern Unity games.

| Supported Features |   |
|--------------------|---|
| Skinned meshes     |✅|
| Remix Replacements |✅|
| Basic textures     |✅|
| Point and spot lights (that are not baked) |✅|
| Directional lights |❌|
| Particle systems |❌|
| GPU-Instanced / Statically Batched Geometry |✅|
| Hardware Skinning |✅|

## Requirements

- BepInEx 5.x
- A Unity 2019+ game that uses MonoBleedingEdge
<img width="600" alt="image" src="https://github.com/user-attachments/assets/421d12cb-e19d-40bb-89ec-69da1370e653" />


## Installation
1. Download the latest [release](https://github.com/sambow23/UnityRTX/releases/latest)
2. Extract to the root of your Unity game

OR

1. Install BepInEx in your Unity game
2. Place all dlls inside the `.trex` folder of the remix archive in the game's root folder (next to the .exe)
3. Copy `UnityRemix.dll` to `BepInEx/plugins/`
4. Launch the game

## Configuration

Edit `BepInEx/config/com.Unity.remix.cfg`:

## Building

Requirements: a .NET SDK capable of targeting .NET Standard 2.1, BepInEx 5.x installed to a game, and the game's Unity Mono managed assemblies.

### Linux

On Arch Linux, install the build tools:

```bash
sudo pacman -Syu --needed dotnet-sdk netstandard-targeting-pack
```

From the repository root:

```bash
# Copy references from the default ULTRAKILL installation and build
./src/build.sh

# Build and deploy UnityRemix.dll plus the existing OpenRemix Windows build
./src/build.sh --deploy

# Use an OpenRemix checkout in another location
./src/build.sh --deploy --openremix-path "/path/to/openremix"

# Update just UnityRemix.dll while keeping the installed renderer
./src/build.sh --deploy --plugin-only

# Use another Unity Mono game installation
./src/build.sh --unity-path "/path/to/Unity game" --deploy
```

The default game directory is `$HOME/.local/share/Steam/steamapps/common/ULTRAKILL`
(`/home/cr/.local/share/Steam/steamapps/common/ULTRAKILL` for user `cr`).
The script works from any working directory, copies the required assemblies into
`src/lib/`, generates `src/BuildInfo.cs`, and builds
`src/bin/Release/netstandard2.1/UnityRemix.dll`. PowerShell is not required.
Deployment only happens with `--deploy`. By default it also copies the Windows
build from the sibling `../openremix` checkout: `remix.dll` becomes `d3d9.dll` in
the game root, alongside `SDL3.dll`, `libwinpthread-1.dll`, `libgcc_s_seh-1.dll`
and `libstdc++-6.dll`. All required runtime files must exist before deployment.
The script copies an existing OpenRemix build; it does not rebuild the renderer.
Changed destination files are backed up under `BepInEx/UnityRTX-backups/` with
their relative game paths preserved. Identical files are left in place.
The plugin still uses Windows APIs and requires the Windows game environment
and a compatible renderer at runtime.

`--unity-path` also selects the game's Unity assemblies for compilation.
Unity 2019 uses a fallback to find inactive renderers in loaded scenes and
the existing UI geometry fallback when `CanvasRenderer.GetMesh` is unavailable.
Newer Unity versions use those APIs when present.

The plugin supports openremix API `0.1003.0`, Remix Plus API `0.1000.0`, and the
API `0.6.1` runtime bundled with UnityRTX v0.2. Its selected API is logged in
`BepInEx/LogOutput.log`. Other `0.6.x` interface layouts are not supported.

### Using openremix under Proton

Build the Windows x64 runtime from the sibling openremix repository using
`python3 scripts/linux/build-windows-remix.py` there. Its platform guide lists
the Clang/MinGW, CMake, Meson, Ninja and Wine prerequisites.

Then run `./src/build.sh --deploy` from this repository. It copies openremix's
`_work/build/windows-cross/src/native/remix.dll` and the required DLLs from
`_work/deps/windows/prefix/bin/`, along with the rebuilt UnityRTX plugin.
Restart the Windows game through Proton as usual. UnityRTX loads the runtime explicitly and adapts
openremix's API table, event pumping and render-thread shutdown.

openremix is a separate SDL3/Vulkan renderer with different rendering features.
Press **F12** with either the game or openremix window focused to show or hide
its settings panel. Select **Backend > Path trace** to enable the ray-query
backend for the current session. Alt+X in the game also opens this panel.

It starts with its raster backend. To request its ray-query backend at startup, edit
`BepInEx/config/com.Unity.remix.cfg` after the first launch and restart:

```ini
[Native]
Backend = pathtrace
```

Use `Backend = raster` to switch back. The log records both the selected API
and the backend selection result; unsupported path tracing leaves raster active.
The original RTX Remix ImGui overlay is not used with openremix.

### API compatibility checks

The API compatibility checks run with .NET 10:

```bash
dotnet run --project tests/RemixApiCompatibility
# Optionally verify the ABI against an existing v0.2 release plugin
dotnet run --project tests/RemixApiCompatibility -- "/path/to/unityrtx-v0.2/BepInEx/plugins/UnityRemix.dll"
```

### Windows / PowerShell

```powershell
cd src

# Setup references
.\setup-references.ps1 -UnityPath "F:\Program Files (x86)\Steam\steamapps\common\ULTRAKILL"

# Build
.\build.ps1 -Deploy -UnityPath "F:\Program Files (x86)\Steam\steamapps\common\ULTRAKILL"
```
