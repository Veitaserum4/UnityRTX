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
        }

        private static void OnSpawned(Component __instance) => RemixFrameCapture.TrackSpawnedObject(__instance);
    }
}
