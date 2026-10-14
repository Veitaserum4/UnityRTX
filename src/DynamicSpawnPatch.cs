using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace UnityRemix
{
    /// <summary>
    /// Hooks instantiation / initialization lifecycle of dynamic gameplay objects
    /// (projectiles, explosions, spawned enemies, characters, combat effects) to register
    /// their renderers immediately on the exact frame they spawn, with zero polling overhead.
    ///
    /// Supports:
    /// 1. Common generic naming patterns across Unity games (Projectile, Bullet, Missile, Rocket, Enemy, Monster, etc.)
    /// 2. Known game-specific types (ULTRAKILL types: EnemyIdentifier, VirtueInsignia, PhysicalShockwave, Shockwave, Magnet, etc.)
    /// 3. Configurable custom extra types via BepInEx config.
    /// </summary>
    public static class DynamicSpawnPatch
    {
        private static bool _applied;
        private static Harmony _harmony;
        private static readonly HashSet<Type> _patchedTypes = new HashSet<Type>();
        private static readonly HashSet<string> _scannedAssemblyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Known exact type names (from ULTRAKILL and common game frameworks)
        private static readonly string[] WellKnownExactTypes = new string[]
        {
            // ULTRAKILL dynamic objects
            "EnemyIdentifier",
            "Projectile",
            "Explosion",
            "Grenade",
            "Magnet",
            "Nail",
            "Harpoon",
            "Cannonball",
            "VirtueInsignia",
            "PhysicalShockwave",
            "Shockwave",
            // Common game / mod types
            "Bullet",
            "Rocket",
            "Missile",
            "Landmine",
            "Flashbang"
        };

        // Heuristic substrings to match dynamic object classes across any Unity game (case-insensitive)
        private static readonly string[] GenericKeywords = new string[]
        {
            "Projectile",
            "Bullet",
            "Missile",
            "Rocket",
            "Grenade",
            "Explosion",
            "Shockwave",
            "Enemy",
            "Monster",
            "Boss",
            "Minion",
            "Creep",
            "Spawnable",
            "Poolable"
        };

        // Method names to hook
        private static readonly string[] LifecycleMethods = new string[] { "Awake", "Start", "OnEnable" };

        private static string _extraTypesConfig = "";

        public static void Apply(Harmony harmony, ManualLogSource logger, string extraTypes = null)
        {
            _harmony = harmony;
            if (extraTypes != null) _extraTypesConfig = extraTypes;

            int patchedCount = ScanAndPatch(logger);
            _applied = true;

            logger?.LogInfo($"[DynamicSpawnPatch] Applied lifecycle hooks to {patchedCount} dynamic object methods across {_patchedTypes.Count} types.");
        }

        public static void CheckNewAssemblies(ManualLogSource logger)
        {
            if (!_applied || _harmony == null) return;
            int newPatched = ScanAndPatch(logger);
            if (newPatched > 0)
            {
                logger?.LogInfo($"[DynamicSpawnPatch] Hooked {newPatched} additional dynamic object methods from newly loaded assemblies.");
            }
        }

        private static int ScanAndPatch(ManualLogSource logger)
        {
            if (_harmony == null) return 0;

            var postfix = new HarmonyMethod(typeof(DynamicSpawnPatch), nameof(OnDynamicSpawned));
            int patchedMethodCount = 0;

            // Build set of extra types from user config
            HashSet<string> extraTypeNames = null;
            if (!string.IsNullOrEmpty(_extraTypesConfig))
            {
                extraTypeNames = new HashSet<string>(
                    _extraTypesConfig.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries),
                    StringComparer.OrdinalIgnoreCase
                );
            }

            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            foreach (var asm in assemblies)
            {
                if (asm == null) continue;
                string asmName = asm.GetName().Name;

                // Skip system and runtime utility assemblies to keep scanning ultra-fast
                if (IsSystemOrEngineAssembly(asmName))
                    continue;

                if (_scannedAssemblyNames.Contains(asmName))
                    continue;
                _scannedAssemblyNames.Add(asmName);

                Type[] types = null;
                try
                {
                    types = asm.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    types = ex.Types;
                }
                catch
                {
                    continue;
                }

                if (types == null) continue;

                for (int i = 0; i < types.Length; i++)
                {
                    var t = types[i];
                    if (t == null || t.IsAbstract || t.IsInterface)
                        continue;

                    if (_patchedTypes.Contains(t))
                        continue;

                    // Must be a Component / MonoBehaviour
                    if (!typeof(Component).IsAssignableFrom(t))
                        continue;

                    if (IsDynamicCandidate(t, extraTypeNames))
                    {
                        int hookedOnType = PatchTypeMethods(t, postfix);
                        if (hookedOnType > 0)
                        {
                            _patchedTypes.Add(t);
                            patchedMethodCount += hookedOnType;
                        }
                    }
                }
            }

            return patchedMethodCount;
        }

        private static bool IsSystemOrEngineAssembly(string asmName)
        {
            if (string.IsNullOrEmpty(asmName)) return true;
            if (asmName.StartsWith("System", StringComparison.OrdinalIgnoreCase)) return true;
            if (asmName.StartsWith("mscorlib", StringComparison.OrdinalIgnoreCase)) return true;
            if (asmName.StartsWith("Mono.", StringComparison.OrdinalIgnoreCase)) return true;
            if (asmName.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase)) return true;
            if (asmName.StartsWith("BepInEx", StringComparison.OrdinalIgnoreCase)) return true;
            if (asmName.StartsWith("0Harmony", StringComparison.OrdinalIgnoreCase)) return true;
            if (asmName.StartsWith("Harmony", StringComparison.OrdinalIgnoreCase)) return true;
            if (asmName.StartsWith("UnityEngine", StringComparison.OrdinalIgnoreCase)) return true;
            if (asmName.StartsWith("UnityEditor", StringComparison.OrdinalIgnoreCase)) return true;
            if (asmName.StartsWith("UnityRemix", StringComparison.OrdinalIgnoreCase)) return true;
            if (asmName.StartsWith("Newtonsoft", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static bool IsDynamicCandidate(Type t, HashSet<string> extraTypeNames)
        {
            string name = t.Name;

            // 1. Check user extra types
            if (extraTypeNames != null && (extraTypeNames.Contains(name) || extraTypeNames.Contains(t.FullName)))
                return true;

            // 2. Check well-known exact types
            for (int i = 0; i < WellKnownExactTypes.Length; i++)
            {
                if (string.Equals(name, WellKnownExactTypes[i], StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            // 3. Check generic keywords (case-insensitive substring match)
            for (int i = 0; i < GenericKeywords.Length; i++)
            {
                if (name.IndexOf(GenericKeywords[i], StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }

            return false;
        }

        private static int PatchTypeMethods(Type t, HarmonyMethod postfix)
        {
            int count = 0;
            for (int i = 0; i < LifecycleMethods.Length; i++)
            {
                string mName = LifecycleMethods[i];
                try
                {
                    var m = t.GetMethod(mName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                    if (m != null && !m.IsAbstract && !m.ContainsGenericParameters)
                    {
                        _harmony.Patch(m, postfix: postfix);
                        count++;
                    }
                }
                catch { }
            }
            return count;
        }

        private static void OnDynamicSpawned(Component __instance)
        {
            try
            {
                if (__instance != null)
                {
                    RemixFrameCapture.QueueDynamicObjectForTracking(__instance);
                }
            }
            catch { }
        }
    }
}
