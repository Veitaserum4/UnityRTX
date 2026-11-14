# When OpenRemix needs a change

UnityRTX only talks to OpenRemix through its C API: `public/include/remix/remix_c.h` and
the native extensions in `remix_native_ext.h`. The C# bindings are in
`src/Remix/RemixAPI.cs`. Most problems can and should be solved on the UnityRTX side. A
runtime change is justified only when the renderer truly can't express something. Example:
a sphere light's finite range (`remixapi_LightInfoRangeEXT`), which the game's rasterizer
has and the path tracer didn't.

## Rules

- **One feature per OpenRemix branch, based on OpenRemix's upstream `main`, PR'd there
  separately.** Never on top of other unmerged work.
  - UnityRTX must keep working without the change wherever it can: check an export with
    `GetProcAddress`, and degrade gracefully.
  - Or state clearly in the UnityRTX PR which OpenRemix PR it needs.
- **Extensions are additive, through `pNext` structs.** A new struct gets:
  - an sType in the `0x7fff1xxx` range that no other extension on upstream (or in an open
    PR) uses. Look before choosing, and renumber on rebase if someone took it.
  - a header comment in `remix_native_ext.h` stating its meaning, its units, what's
    invalid, and what happens without it;
  - validation that rejects invalid values (`invalid` / `REMIXAPI_ERROR_CODE_INVALID_ARGUMENTS`).
- Mirror the struct exactly in `RemixAPI.cs`: `[StructLayout(LayoutKind.Sequential)]`,
  `int sType; IntPtr pNext;` then the fields. Use the same constant value. Pin it with
  `GCHandle` while the call runs, and free it in `finally`.
- Diagnostics-only additions to OpenRemix (extra exports or logging used to investigate)
  are not PR material. Keep them on a local branch.
- Don't add compatibility paths for stock RTX Remix. UnityRTX targets OpenRemix only.

## Building OpenRemix's runtime for a game

See OpenRemix's own docs. Then install it into the game with:

```powershell
.\scripts\build.ps1 -GamePath "C:\...\<Game>" -Deploy -OpenRemix "C:\...\OpenRemix" -OpenRemixBuild <build dir>
```

This puts the runtime in `BepInEx/plugins/UnityRTX/openremix/`.
