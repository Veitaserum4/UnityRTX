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
        private Type checkPointType;
        private bool checkPointTypeSearched;
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

        // Weapon colours (GunColorGetter): the weapon's materials are swapped for "colored" copies
        // (keyword CUSTOM_COLORS) and _CustomColor1/2/3 set through the renderer's property
        // block. ULTRAKILL/Master's CUSTOM_COLORS pixel shader (disassembled): the albedo is
        // _IDTex.a (a greyscale copy of the weapon; _MainTex is not read), lerped toward
        // itself x _CustomColorN.rgb by 0.8 x _IDTex.r/g/b in turn; the metal amount is
        // _IDTex.rgb x _CustomColorN.a, combined 1 - (1-a)(1-b)(1-c), and adds _CubeTex's
        // reflection tinted by the albedo: a metallic surface.
        private static readonly int IDTex = Shader.PropertyToID("_IDTex");
        private static readonly int[] CustomColors =
        {
            Shader.PropertyToID("_CustomColor1"), Shader.PropertyToID("_CustomColor2"), Shader.PropertyToID("_CustomColor3")
        };

        public override bool GetColorMask(Material material, MaterialPropertyBlock block, out RemixMaterialManager.ColorMask colorMask)
        {
            colorMask = default;
            if (!material.IsKeywordEnabled("CUSTOM_COLORS") || !material.HasProperty(IDTex) || !(material.GetTexture(IDTex) is Texture2D mask))
                return false;
            var colors = new Color[3];
            for (int i = 0; i < 3; i++)
            {
                Color c = block != null && !block.isEmpty ? block.GetColor(CustomColors[i]) : Color.clear;
                if (c == Color.clear)
                    c = material.HasProperty(CustomColors[i]) ? material.GetColor(CustomColors[i]) : Color.white;
                colors[i] = c;
            }
            colorMask = new RemixMaterialManager.ColorMask
            {
                mask = mask,
                color1 = colors[0], color2 = colors[1], color3 = colors[2],
                weight = 0.8f,
                baseInMaskAlpha = true,
                metalFromColorAlpha = true,
                metalRoughness = 0.2f
            };
            return true;
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

        // Checkpoint graphics and the doors' "NoPass" skull locks are toggled by deactivation.
        public override bool FollowsActiveState(MeshFilter filter, Renderer renderer)
        {
            if (!checkPointTypeSearched)
            {
                checkPointTypeSearched = true;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try { checkPointType = asm.GetType("CheckPoint"); } catch { }
                    if (checkPointType != null)
                        break;
                }
            }
            if (checkPointType != null && renderer.GetComponentInParent(checkPointType) != null)
                return true;

            var parent = filter.transform.parent;
            string parentName = parent != null ? parent.name : null;
            var material = renderer.sharedMaterial;
            return ContainsAny(filter.gameObject.name, followNames) || ContainsAny(parentName, followNames) ||
                   (material != null && ContainsAny(material.name, noPassNames));
        }

        private static readonly string[] followNames = { "CheckPoint", "NoPass" };
        private static readonly string[] noPassNames = { "NoPass" };

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
