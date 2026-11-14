# Game profiles

Everything UnityRTX does for one game only lives in that game's **profile**: a
`GameProfile` subclass in `src/Games/<Game>/`. `GameProfiles` picks the profile from the
running executable's name. The generic bridge calls the profile's hooks through
`GameProfiles`, so a game without a profile pays nothing and can't be affected.

ULTRAKILL (`src/Games/Ultrakill/`) is the complete worked example. Read it alongside this
guide.

## Adding a game

1. Create `src/Games/<Game>/<Game>Profile.cs`:

   ```csharp
   using System;
   using System.Collections.Generic;
   using HarmonyLib;
   using UnityEngine;

   namespace UnityRemix.Games.MyGame
   {
       internal sealed class MyGameProfile : GameProfile
       {
           public override string Name => "MyGame";
           // Process names without .exe, compared ignoring case. List every build's name
           // (demo, platform variants) that shares this code.
           public override string[] ExecutableNames => new[] { "MyGame" };
       }
   }
   ```

2. Register it: add `new MyGameProfile()` to `all` in `src/Games/GameProfiles.cs`. That is
   the only line outside your folder that a new game needs.
3. Run the game. `LogOutput.log` should say
   `[Games] Executable 'MyGame', GameProfile=Auto: MyGame patches`.
   - Users can force a profile, or turn profiles off, with `[Game] GameProfile = Auto | None | <Name>`.
   - `None` is how you check whether a problem comes from the profile.

Start with an empty profile. Most of a game should work through the generic bridge.
Override hooks only for what you've confirmed is the game's own behaviour.

## The hooks

`src/Games/GameProfile.cs` is the reference. Every hook has a comment saying what it means
and which thread calls it. In groups:

| Need | Hook(s) | ULTRAKILL uses it for |
| --- | --- | --- |
| Patch the game's code | `Apply(Harmony)` | GameStateManager pause/cursor, post-process handler, spawn tracking |
| Things that move, spawn or die, which the static scan would freeze | `IsDynamicObject`, `FollowsActiveState` | enemies, projectiles, checkpoints, light shafts toggled by flickering lamps |
| Things the game never shows, or that only pass through | `IsHiddenMaterial`, `IsHiddenRenderer`, `IsNeverRenderedLayer`, `IsHiddenLight` | Default-Material helper boxes, HeatWave distortion sprites, soul orbs' glow quads (passed through), trigger layers, the player's fill light |
| Material corrections | `OverrideMaterial`, `OverrideTextureTransform`, `GetColorMask`, `GetIridescence`, `ReadsBlendProperties` | level blood decals (cutout, atlas UVs), weapon colours, oiled enemies (a thin film from `_OiledAmount`, partial: filling up the height baked in TEXCOORD2), ULTRAKILL/Master's `_SrcBlend`/`_DstBlend` |
| Extra geometry per frame | `CaptureFrame` | blood stains |
| Per-scene state | `OnSceneLoaded` | reset caches and rescans |
| Cameras | `FindViewModelCamera`, `IsPlayerBody` | the weapon "HUD Camera" |
| Portals | `GetRayPortalView`, `OpensViewPortal`, `GetPortals` | Limbo's TV walls (a scaled view of another part of the level) |
| The game's own shaders over the frame | `GetPassThroughRenderers`, `UsesShaderPassThrough`, `PassThroughFrameTexture`, `PassThroughShaderPass`, `PassThroughScreenEffect`, `PassThroughScreenResolution`, `ViewChromaticAberration` | Limbo walls, Greed's heat haze, the death screen and graphics-menu effects. See [shader-pass-through.md](shader-pass-through.md) |
| Underwater look | `GetUnderwaterTint` | the HUD's water/blood overlay colour |
| Fog the game changes per camera | `WorldFogDensity` | (ATLYSS: its sky camera's `CameraFogDensity` swaps `RenderSettings.fogDensity` while rendering) |
| Remix menu interplay | `OnRemixMenu` | release and restore the cursor lock |
| Options | `DrawSettings` | the profile's section of the Plugin tab |

## Adding a hook

When shared code needs a decision only a game can make, add a hook. Don't put a game check
in shared code.

1. In `GameProfile.cs`, add a `public virtual` method:
   - Its default must keep the generic behaviour exactly as it was (`false`, `null`, `0`,
     no-op).
   - Write a comment saying what it means in game-neutral terms, which thread calls it, and
     how often.
2. In `GameProfiles.cs`, add a static forwarder that goes through `Run(nameof(Hook), ...)`.
   `Run` catches exceptions, logs each failing hook once, and skips it from then on.

   ```csharp
   public static bool IsHiddenLight(Light light)
   {
       bool hidden = false;
       Run(nameof(IsHiddenLight), p => hidden = p.IsHiddenLight(light));
       return hidden;
   }
   ```

3. Call `Games.GameProfiles.<Hook>(...)` from the shared code, and override it in your
   profile.

Keep hooks narrow and declarative. A hook answers a question ("is this light hidden?", "which
shader pass draws this material?"); it doesn't take over a subsystem. Hot-path hooks are
called per renderer or per material, so make them cheap: compare names or tags, and cache
anything that needs a search.

## Gating rules

- **Find game code by name at runtime**: `AccessTools.TypeByName("EnemyIdentifier")`,
  `AccessTools.Field(type, "processedLUT")`.
  - Don't reference the game's assembly types directly. The plugin is one DLL for every
    game.
  - Cache the `Type` / `FieldInfo` once, and handle `null` (a game update renamed it):
    disable that feature and log one line.
- **Harmony patches** go in their own static class (`<Game><Thing>Patch.cs`), applied from
  the profile's `Apply`. `UltrakillSpawnPatch.cs` shows the pattern:
  - resolve each method by name;
  - `try` each `harmony.Patch` separately;
  - count what was patched, and log the total once.
  - Postfixes must not throw. Wrap their body in `try/catch`.
- **Match on stable identifiers.** Prefer them in this order: shader name, then component
  type, then material or object name, then layer index.
  - Name substrings match too much. `"Fog"` matches `"FogOn"`, and a generic
    `"Additive"` test once made a game's whole fog layer glow.
  - Use exact names where you can, and comment on why each one is there.
- **Generic first.** If the cause is that UnityRTX mistranslates a standard Unity
  feature (Standard shader keywords, legacy particle shaders, `_SrcBlend`, `Renderer.enabled`
  toggles, LineRenderers), fix it in the shared code for every game.
  - A profile hook is for the game's own conventions. A custom shader is one.
  - So is a game that keeps stale `_DstBlend` values in shaders that don't read them
    (hence `ReadsBlendProperties`).

## Options and settings UI

- Bind a profile's config entries in `Core/UnityRemixPlugin.cs`, next to the existing
  `[Ultrakill]` ones, under a `[<Game>]` section. Store them in a static field on the
  profile. Give each one a description and a default that matches the unmodded game.
- Draw them in `DrawSettings()` with `RemixImGui` (look at `UltrakillProfile.DrawSettings`):
  - a coloured section header;
  - checkboxes or sliders;
  - a tooltip on every option saying what the user will see.
  - It runs on Remix's render thread: only read and write config values there.
- Effects that change how the game looks (its own post-processing) should default to the
  game's look and be switchable.

## Performance patterns

- Scene searches (`FindObjectsOfType<T>()`) run on a timer or on scene load, never every
  frame.
  - The ULTRAKILL profile rescans Limbo walls every 600 frames and heat waves every 30, and
    resets the timers in `OnSceneLoaded`.
- Cache per-material decisions by `material.GetInstanceID()`, and clear the caches in
  `OnSceneLoaded`.
- Objects the game spawns can be handed to the frame capture on the frame they appear:
  `RemixFrameCapture.TrackSpawnedObject(component)`, called from a Harmony postfix on the
  spawn method (Awake/Start/OnEnable). Without it they're picked up at the next cache
  refresh, which can be a visible delay.

## Logging

- `logger.LogInfo` once per fact worth knowing (a patch applied, a texture matched). Never
  per frame.
- Detailed per-object descriptions go behind `VerboseLog.On`. A user can then turn on
  `[Debug] VerboseLogging`, reproduce, and send the log.
  - The particle capture's `[ParticleSystem] '<name>': ... shader ... look ...` lines are
    how a "this effect looks wrong" report is diagnosed.
- Prefix log lines with a tag: `[Games]`, `[ShaderPassThrough]`, `[<Game>]`.

## Checklist before a PR

- [ ] Every game-specific line lives under `src/Games/<Game>/`, plus its line in `GameProfiles.all`.
- [ ] New hooks have neutral defaults and comments, and are forwarded through `GameProfiles.Run`.
- [ ] Game types and members are resolved by name, and a missing one disables the feature cleanly.
- [ ] No per-frame scene searches or allocations were added.
- [ ] Tested in game. The PR says which levels or scenes were compared against the unmodded game.
- [ ] Other games are unaffected. Run another Unity game, or set `GameProfile = None`, if
      shared code changed.
