# unity-api-check

Lists the Unity APIs the plugin references that an older Unity lacks, and the plugin
methods that use each. On Mono a method naming a missing member or type fails to compile
as a whole (TypeLoadException / MissingMethodException in its caller), so every use of a
newer API must sit in its own `NoInlining` method behind a `UnityCompat.Has*` check.

```
dotnet run -c Release -- <src/bin/Release/net472/UnityRemix.dll> "<Game>/<Game>_Data/Managed"
```

The oldest Unity versions in use: DUSK (2018.2, `Dusk_Data/Managed`) and Gloomwood
Demo (2019.2). Check every line of the second section (`Type::Method -> missing API`):
each method must be reachable only behind a guard, or only from a game profile whose game
is new enough.
