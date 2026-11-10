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

        // Limbo's TV-screen walls (LIMBO_WALLS) show _LimboSky in screen space (times their own
        // texture, with glitches): the LimboSkybox camera's view of a miniature scene elsewhere
        // in the level. That camera ("fakeCam") turns like the player's, at fakeCamStart +
        // (player - playerStart) / 16 (y not below the start's with lockMinimumHeight): the
        // walls are a view portal into the miniature at scale 1/16. The exit region: around
        // fakeCamStart, within its camera's far plane and nearer it than the player's start.
        public override bool OpensViewPortal(Material material) => material.IsKeywordEnabled("LIMBO_WALLS");

        // ULTRAKILL.Portal.Portal (Fraud): entry and exit transforms (opening in their local XY
        // plane, +Z out), a PlaneShape (full width and height); travel exit (-1, 1, -1) entry^-1,
        // unscaled. Listed every 60 frames, read every frame.
        private Type portalType;
        private bool portalTypeSearched;
        private System.Reflection.FieldInfo portalEntry, portalExit, portalShape, shapeWidth, shapeHeight;
        private readonly List<Behaviour> portalComponents = new List<Behaviour>();
        private int portalListFrame = int.MinValue / 2;

        public override void GetPortals(Camera worldCamera, List<PortalPair> portals)
        {
            if (!portalTypeSearched)
            {
                portalTypeSearched = true;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try { portalType = asm.GetType("ULTRAKILL.Portal.Portal"); } catch { }
                    if (portalType != null) break;
                }
                if (portalType != null)
                {
                    portalEntry = portalType.GetField("entry");
                    portalExit = portalType.GetField("exit");
                    portalShape = portalType.GetField("shape");
                    var planeShape = portalType.Assembly.GetType("ULTRAKILL.Portal.Geometry.PlaneShape");
                    shapeWidth = planeShape?.GetField("width");
                    shapeHeight = planeShape?.GetField("height");
                }
                Logger.LogInfo($"[Portal] ULTRAKILL portals {(portalType != null && portalEntry != null && portalExit != null && shapeWidth != null ? "found" : "not found")}");
            }
            if (portalType == null || portalEntry == null || portalExit == null || portalShape == null || shapeWidth == null || shapeHeight == null || worldCamera == null)
                return;
            int frame = Time.frameCount;
            if (frame - portalListFrame >= 60)
            {
                portalListFrame = frame;
                portalComponents.Clear();
                foreach (var found in UnityEngine.Object.FindObjectsOfType(portalType))
                    if (found is Behaviour behaviour)
                        portalComponents.Add(behaviour);
            }
            Vector3 eye = worldCamera.transform.position;
            foreach (var portal in portalComponents)
            {
                if (portal == null || !portal.isActiveAndEnabled)
                    continue;
                var entry = portalEntry.GetValue(portal) as Transform;
                var exit = portalExit.GetValue(portal) as Transform;
                var shape = portalShape.GetValue(portal);
                if (entry == null || exit == null || shape == null || shape.GetType() != shapeWidth.DeclaringType)
                    continue;
                float width = (float)shapeWidth.GetValue(shape), height = (float)shapeHeight.GetValue(shape);
                if (width <= 0f || height <= 0f)
                    continue;
                // Both ends in one place (a view turned about the opening): left out, it would
                // take a pair and lie on itself.
                if ((entry.position - exit.position).sqrMagnitude < 0.0001f)
                    continue;
                portals.Add(new PortalPair
                {
                    id = portal.GetInstanceID(),
                    entry = Matrix4x4.TRS(entry.position, entry.rotation, Vector3.one),
                    exit = Matrix4x4.TRS(exit.position, exit.rotation, Vector3.one),
                    width = width,
                    height = height
                });
            }
            // Remix has room for a few: those with an end in front of the camera first, nearest first.
            Vector3 look = worldCamera.transform.forward;
            portals.Sort((a, b) => Priority(a, eye, look).CompareTo(Priority(b, eye, look)));
        }

        private static float Priority(PortalPair p, Vector3 eye, Vector3 look)
        {
            float best = float.MaxValue;
            foreach (var end in new[] { p.entry, p.exit })
            {
                Vector3 offset = (Vector3)end.GetColumn(3) - eye;
                float distance = offset.magnitude;
                float radius = 0.5f * Mathf.Sqrt(p.width * p.width + p.height * p.height);
                // Behind the camera (its opening wholly so): after every end in front of it.
                bool behind = Vector3.Dot(offset, look) < -radius;
                best = Mathf.Min(best, distance + (behind ? 1e6f : 0f));
            }
            return best;
        }

        private Type limboType;
        private bool limboTypeSearched;
        private Behaviour limbo;
        private int limboSearchFrame = int.MinValue / 2;
        private System.Reflection.FieldInfo limboFakeCamStart, limboPlayerStartPos, limboLockMinimumHeight;
        private int loggedLimbo;

        public override bool GetRayPortalView(Camera worldCamera, out RayPortalView view)
        {
            view = default;
            if (!limboTypeSearched)
            {
                limboTypeSearched = true;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try { limboType = asm.GetType("LimboSkybox"); } catch { }
                    if (limboType != null) break;
                }
                if (limboType != null)
                {
                    const System.Reflection.BindingFlags all = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
                    limboFakeCamStart = limboType.GetField("fakeCamStart", all);
                    limboPlayerStartPos = limboType.GetField("playerStartPos", all);
                    limboLockMinimumHeight = limboType.GetField("lockMinimumHeight", all);
                }
            }
            if (limboType == null || limboFakeCamStart == null || limboPlayerStartPos == null || worldCamera == null)
                return false;
            int frame = Time.frameCount;
            if (limbo == null && frame - limboSearchFrame >= 120)
            {
                limboSearchFrame = frame;
                limbo = UnityEngine.Object.FindObjectOfType(limboType) as Behaviour;
            }
            if (limbo == null || !limbo.isActiveAndEnabled)
                return false;
            var fakeCamStart = limboFakeCamStart.GetValue(limbo) as Transform;
            if (fakeCamStart == null)
                return false;
            var playerStart = (Vector3)limboPlayerStartPos.GetValue(limbo);
            bool lockHeight = limboLockMinimumHeight == null || (bool)limboLockMinimumHeight.GetValue(limbo);
            Vector3 offset = (worldCamera.transform.position - playerStart) / 16f;
            if (lockHeight)
                offset.y = Mathf.Max(offset.y, 0f);
            Vector3 centre = fakeCamStart.position;
            var fakeCam = limbo.GetComponent<Camera>();
            float far = fakeCam != null ? fakeCam.farClipPlane : 1000f;
            view.exitCamera = centre + offset;
            view.scale = 1f / 16f;
            view.exitRegionCenter = centre;
            view.exitRegionRadius = Mathf.Min(far, Vector3.Distance(centre, playerStart) * 0.5f);
            if (loggedLimbo != limbo.GetInstanceID())
            {
                loggedLimbo = limbo.GetInstanceID();
                Logger.LogInfo($"[Limbo] view portal '{limbo.name}': fake start {centre}, player start {playerStart}, far {far:F0}, " +
                               $"exit region radius {view.exitRegionRadius:F1}, lock height {lockHeight}");
            }
            return true;
        }

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
