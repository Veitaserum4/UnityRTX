using System;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace UnityRemix.Games.Ultrakill
{
    /// <summary>
    /// Hooks the spawn lifecycle (Awake / Start / OnEnable) of ULTRAKILL's short-lived
    /// objects (projectiles, explosions, rockets, magnets, nails, enemies) so their
    /// renderers are tracked on the frame they appear, not at the next cache refresh.
    /// </summary>
    internal static class UltrakillSpawnPatch
    {
        private static readonly string[] typeNames =
        {
            "Projectile", "Explosion", "Grenade", "Magnet", "Nail", "Harpoon", "Cannonball",
            "EnemyIdentifier", "VirtueInsignia", "PhysicalShockwave", "Shockwave"
        };
        private static readonly string[] methodNames = { "Awake", "Start", "OnEnable" };

        public static void Apply(Harmony harmony, ManualLogSource logger)
        {
            var postfix = new HarmonyMethod(typeof(UltrakillSpawnPatch), nameof(OnSpawned));
            int patched = 0;
            foreach (var typeName in typeNames)
            {
                Type type = AccessTools.TypeByName(typeName);
                if (type == null || !typeof(Component).IsAssignableFrom(type)) continue;

                foreach (var methodName in methodNames)
                {
                    try
                    {
                        var method = type.GetMethod(methodName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                        if (method == null) continue;
                        harmony.Patch(method, postfix: postfix);
                        patched++;
                    }
                    catch (Exception ex)
                    {
                        logger?.LogWarning($"[UltrakillSpawnPatch] {typeName}.{methodName}: {ex.Message}");
                    }
                }
            }
            logger?.LogInfo($"[UltrakillSpawnPatch] Hooked {patched} spawn methods");

            // Explosion.Start shows a copy of the game's blank white material for its first 0.1 s.
            // Copies of one material share a converted material (StaticGeometryDedupe, by name),
            // and other objects copy that one too: named apart, the flash is captured as its
            // explosion's own and can be a light (UltrakillProfile.SceneLightScale).
            var start = AccessTools.TypeByName("Explosion")?.GetMethod("Start",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            if (start == null)
            {
                logger?.LogWarning("[UltrakillSpawnPatch] Explosion.Start not found: explosion flashes stay unlit");
                return;
            }
            try
            {
                harmony.Patch(start, postfix: new HarmonyMethod(typeof(UltrakillSpawnPatch), nameof(NameExplosionFlash)));
            }
            catch (Exception ex)
            {
                logger?.LogWarning($"[UltrakillSpawnPatch] Explosion.Start: {ex.Message}");
            }
        }

        private const string ExplosionFlashName = "Explosion Flash";

        private static void OnSpawned(Component __instance) => RemixFrameCapture.TrackSpawnedObject(__instance);

        private static void NameExplosionFlash(Component __instance)
        {
            var renderer = __instance.GetComponent<MeshRenderer>();
            var flash = renderer != null ? renderer.sharedMaterial : null;
            if (flash != null)
                flash.name = ExplosionFlashName;
        }
    }
}
