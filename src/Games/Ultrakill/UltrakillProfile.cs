using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace UnityRemix.Games.Ultrakill
{
    internal sealed class UltrakillProfile : GameProfile
    {
        public override string Name => "ULTRAKILL";
        public override string[] ExecutableNames => new[] { "ULTRAKILL" };

        // Components whose objects move, spawn or die: drawn by the per-frame capture.
        private static readonly string[] dynamicTypeNames =
        {
            "EnemyIdentifier", "SpawnEffect", "SeasonalHats", "NewMovement", "PlayerTracker", "Projectile",
            "Coin", "Nail", "Grenade", "ItemIdentifier", "Skull", "BloodAbsorber"
        };
        // Spawn effects and the seasonal hats swapped onto enemies, by object name / parent name.
        private static readonly string[] dynamicObjectNames = { "SpawnEffect", "SeasonalHats", "Pumpkin", "SantaHat", "EasterBunny" };
        private static readonly string[] dynamicParentNames = { "SpawnEffect", "SeasonalHats", "Halloween", "Christmas", "Easter" };

        private Type[] dynamicTypes;
        private readonly UltrakillBloodStains bloodStains = new UltrakillBloodStains();

        public override void Apply(Harmony harmony)
        {
            UltrakillPostProcessPatch.Apply(harmony, Logger);
            UltrakillGameState.Apply(harmony, Logger);
            UltrakillSpawnPatch.Apply(harmony, Logger);
        }

        // Weapons and arms are drawn by the main camera's "HUD Camera" child, at the weapon FOV.
        private Camera worldCamera, hudCamera;

        public override Camera FindViewModelCamera(Camera world)
        {
            if (world != worldCamera || (hudCamera == null && Time.frameCount % 60 == 0))
            {
                worldCamera = world;
                hudCamera = null;
                foreach (var cam in world.GetComponentsInChildren<Camera>(true))
                {
                    if (cam.name == "HUD Camera")
                    {
                        hudCamera = cam;
                        break;
                    }
                }
            }
            return hudCamera;
        }

        public override bool? OnRemixMenu(bool open) => UltrakillGameState.SetRemixMenuState(open, Logger);

        public override void CaptureFrame(RemixMaterialManager materials, RemixFrameCapture.FrameState state, int frameCount) =>
            bloodStains.Capture(Logger, materials, state);

        public override void OnSceneLoaded() => bloodStains.Reset();

        // Unity's built-in Default-Material appears only on helper shapes the game doesn't show:
        // crusher base boxes, skull altar placeholders, invisible walls.
        public override bool IsHiddenMaterial(Material material) =>
            material.name.StartsWith("Default-Material", StringComparison.Ordinal);

        public override void OverrideMaterial(Material material, ref RemixMaterialManager.AlphaMode alphaMode, ref float alphaCutoff, ref Color albedoColor)
        {
            if (!bloodStains.IsStainMaterial(material))
                return;
            alphaMode = RemixMaterialManager.AlphaMode.Blend;
            alphaCutoff = 0.05f;
            albedoColor = UltrakillBloodStains.Tint;
        }

        public override bool IsDynamicObject(MeshFilter filter, Renderer renderer)
        {
            if (dynamicTypes == null)
            {
                var found = new List<Type>();
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    foreach (var name in dynamicTypeNames)
                    {
                        Type t = null;
                        try { t = asm.GetType(name); } catch { }
                        if (t != null && !found.Contains(t))
                            found.Add(t);
                    }
                }
                dynamicTypes = found.ToArray();
            }

            for (int i = 0; i < dynamicTypes.Length; i++)
            {
                if (renderer.GetComponentInParent(dynamicTypes[i]) != null)
                    return true;
            }

            if (ContainsAny(filter.gameObject.name, dynamicObjectNames))
                return true;
            var parent = filter.transform.parent;
            return parent != null && ContainsAny(parent.name, dynamicParentNames);
        }

        private static bool ContainsAny(string name, string[] parts)
        {
            if (string.IsNullOrEmpty(name)) return false;
            foreach (var part in parts)
                if (name.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            return false;
        }
    }
}
