# AGENTS.md

Instructions for AI coding agents (and humans) working on UnityRTX. Read this file
first, then the guide under `docs/agents/` that covers your task.

UnityRTX is a BepInEx 5 plugin (C#, `src/`) that path-traces Unity games with
[OpenRemix](https://github.com/Veitaserum4/OpenRemix-J). Each frame it reads the scene from
Unity (meshes, materials, lights, particles, sky) and submits it to OpenRemix through the
Remix API (`src/Remix/RemixAPI.cs`). A native DLL (`native/hostpass11`) can also capture a
game's own D3D11 draws and replay them over the path-traced frame. That covers screen
effects, distortion, and anything else path tracing can't express.

## Guides

| Task | Read |
| --- | --- |
| Making a game work: profiles, gating, Harmony patches, settings | [docs/agents/game-profiles.md](docs/agents/game-profiles.md) |
| Replaying a game's own shaders (D3D11 host passes) | [docs/agents/shader-pass-through.md](docs/agents/shader-pass-through.md) |
| Finding out what a game does (decompiling, shaders, runtime logs) | [docs/agents/investigating-a-game.md](docs/agents/investigating-a-game.md) |
| Needing something OpenRemix can't do yet | [docs/agents/openremix-changes.md](docs/agents/openremix-changes.md) |

## Hard rules

1. **Game-specific code goes in that game's profile, never in the generic bridge.**
   - Put it in `src/Games/<Game>/` as a `GameProfile` subclass, picked by executable name.
   - Shared code (`Capture/`, `Conversion/`, `Scene/`, `ShaderPassThrough/`, ...) must not
     mention a game, a game's shader name, object name, type or layer.
   - When shared code needs a game-specific decision, add a virtual hook to `GameProfile`
     with a neutral default, forward it through `GameProfiles`, and override it in the
     profile. Details are in the game-profiles guide.
   - No per-game git branches. Everything lives on one branch, gated by profile.
2. **Fix the general case in the generic code; patch the specific case in the profile.**
   - Some mistakes are mistranslations of standard Unity. Examples: a blend mode, a legacy
     shader's 2x tint, an additive particle's coverage. Fix those generically
     (`Conversion/ParticleLook.cs` is an example), because every game benefits.
   - Use a profile only for what is truly the game's own: its custom shaders, objects,
     components and conventions.
3. **A profile must never be able to break the bridge.**
   - `GameProfiles.Run` catches and logs each hook's exceptions once.
   - Find game types by name (`AccessTools.TypeByName`), never by compile-time reference.
     The plugin must load in every Unity game, and in a game whose update renamed something.
   - A missing type or field disables that patch, with one log line. It never throws.
4. **OpenRemix only.**
   - There is no stock RTX Remix / dxvk-remix compatibility. Don't add fallbacks for it, or
     `if OpenRemix` branches.
   - Use OpenRemix's API extensions directly.
5. **Keep OpenRemix changes minimal and separate.**
   - If a feature truly needs runtime support, it is an OpenRemix PR of its own. See the
     openremix-changes guide.
   - Prefer solving it in UnityRTX.
6. **Main-thread cost matters more than GPU cost.**
   - Users accept being GPU-bound by the path tracer, not CPU-bound by the plugin.
   - Don't add per-frame scene-wide searches (`FindObjectsOfType`, `GetComponentsInChildren`
     over the scene). Cache the results and refresh them every N frames or on scene load.
   - Don't allocate per frame in hot paths. Reuse buffers, as the particle code does.
7. **Diagnostics go behind `VerboseLog.On`** (`Debug.VerboseLogging`, off by default).
   - One-time facts may log unconditionally, once (what a material resolved to, a patch
     that failed to apply).
   - Nothing logs every frame.
8. **Comments say why, not what changed.**
   - Write comments as present-tense facts about the code ("Unity's legacy particle shaders
     multiply by 2 x _TintColor").
   - Not history ("fixed the alpha bug"); that belongs in the commit message.
   - Keep the existing style: plain sentences, no banners, no TODO litter.

## Build and test

```powershell
# Plugin only (copies the game's managed assemblies to src/lib to compile against)
.\scripts\build.ps1 -GamePath "C:\...\<Game>"

# Plugin + native host-pass DLL, installed into the game
.\scripts\build.ps1 -GamePath "C:\...\<Game>" -Deploy -Native
```

- `-Native` needs Meson, Ninja, clang-cl and the Vulkan SDK's `glslangValidator`. It also
  needs the Visual Studio Installer folder on `PATH` (for `vswhere`).
- Linux/Proton: `./scripts/build.sh` (see README.md).
- API binding checks: `dotnet run --project tests/RemixApiCompatibility`.
- There is no automated in-game test. Verify by running the game:
  - Use OpenRemix's single window side by side with the unmodded game, or toggle the
    feature in the Remix menu (F12 → Plugin tab).
  - Read `BepInEx/LogOutput.log`, plus `BepInEx/plugins/UnityRTX/UnityRTXHostPass.log` for
    the native side.
  - Say in your PR what you verified in game and what you could not.

## Pull requests

- Base on `unity-bridge-openremix`. Keep one feature or fix per PR.
- A new game's PR touches only:
  - `src/Games/<Game>/`;
  - its one line in `GameProfiles.all`;
  - and any new `GameProfile` hooks (generic, documented, neutral default).
  - Generic fixes found while adding the game go in a separate PR, or at least a
    separate commit.
- Don't commit:
  - build output (`src/bin`, `src/obj`, `native/_build`);
  - the game's assemblies (`src/lib`);
  - decompiled game code;
  - extracted game assets or shader binaries.
  - Investigation scripts may go in `tools/investigation/` if they help others.
- Commit messages: a short subject (`Area: what it does`), then a body that says what was
  wrong and why the change fixes it.
