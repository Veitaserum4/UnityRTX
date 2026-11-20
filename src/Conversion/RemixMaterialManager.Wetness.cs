using System.Collections.Generic;
using UnityEngine;

namespace UnityRemix
{
    // Surfaces wet with rain: while the game says it rains where the player is
    // (GameProfile.RainIntensity), lit opaque surfaces darken and turn glossy, as wet stone, wood
    // and earth do. Applied to what each material sends OpenRemix (CreateRemixMaterialSimple), not
    // to the captured data: a material is replaced in place under its hash, its meshes untouched.
    // The wetness eases in and out in steps, each step re-creating the materials a few at a time.
    public partial class RemixMaterialManager
    {
        public BepInEx.Configuration.ConfigEntry<bool> ConfigRainWetness { get; private set; }
        public BepInEx.Configuration.ConfigEntry<float> ConfigWetnessStrength { get; private set; }

        private const float WetSeconds = 15f, DrySeconds = 45f;
        private const int WetnessSteps = 8;
        private const int WetnessRefreshPerFrame = 48;

        // The wetness the materials are being made with (a step), and how wet it is now.
        private float appliedWetness, wetness;
        private readonly Queue<int> wetnessRefresh = new Queue<int>();
        private readonly HashSet<int> wetnessQueued = new HashSet<int>();

        private void BindWetness(BepInEx.Configuration.ConfigFile config)
        {
            ConfigRainWetness = config.Bind("Materials", "RainWetness", true,
                "While it rains where the player is (games whose profile reports it), lit surfaces darken and turn glossy, " +
                "easing in over about 15 s and drying over about 45 s.");
            ConfigWetnessStrength = config.Bind("Materials", "WetnessStrength", 1f,
                new BepInEx.Configuration.ConfigDescription("How wet rain makes surfaces (0 to 1).",
                    new BepInEx.Configuration.AcceptableValueRange<float>(0f, 1f)));
        }

        // Main thread, each frame.
        public void UpdateWetness(float deltaTime)
        {
            float target = ConfigRainWetness != null && ConfigRainWetness.Value
                ? Mathf.Clamp01(Games.GameProfiles.RainIntensity()) * (ConfigWetnessStrength != null ? Mathf.Clamp01(ConfigWetnessStrength.Value) : 1f)
                : 0f;
            float rate = deltaTime / (target > wetness ? WetSeconds : DrySeconds);
            wetness = Mathf.MoveTowards(wetness, target, rate);
            float step = Mathf.Round(wetness * WetnessSteps) / WetnessSteps;
            if (step != appliedWetness)
            {
                appliedWetness = step;
                foreach (var kvp in materialTextureData)
                    if (kvp.Value.remixMaterialHandle != System.IntPtr.Zero && Wettable(kvp.Value) && wetnessQueued.Add(kvp.Key))
                        wetnessRefresh.Enqueue(kvp.Key);
            }
            for (int n = 0; n < WetnessRefreshPerFrame && wetnessRefresh.Count > 0; n++)
            {
                int id = wetnessRefresh.Dequeue();
                wetnessQueued.Remove(id);
                if (!materialTextureData.TryGetValue(id, out var matData) || matData.remixMaterialHandle == System.IntPtr.Zero)
                    continue;
                matData.remixMaterialHandle = CreateRemixMaterialSimple(ref matData, id, replace: true);
                materialTextureData[id] = matData;
            }
        }

        // Lit, solid surfaces: not glass, sheets, particles, glows, lights or portals.
        private static bool Wettable(in MaterialTextureData m) =>
            m.alphaMode != AlphaMode.Blend && m.translucentKind == TranslucentKind.None &&
            !m.suppressLightGeneration && !m.lightsScene && m.rayPortalIndex < 0 &&
            !(m.emissiveIntensity > 0f && (m.emissiveHandle != System.IntPtr.Zero ||
                                           Mathf.Max(m.emissiveColor.r, Mathf.Max(m.emissiveColor.g, m.emissiveColor.b)) > 0.01f));

        // The wetness a material is made with now (0: dry or not wettable).
        private float WetnessOf(in MaterialTextureData m) => appliedWetness > 0f && Wettable(m) ? appliedWetness : 0f;

        // Wet: darker (porous surfaces hold water; metals barely change) and glossier, roughness
        // falling to a quarter (not below 0.06).
        private static float WetAlbedoScale(float wet, float metallic) => 1f - wet * 0.45f * (1f - Mathf.Clamp01(metallic));
        private static float WetRoughness(float roughness, float wet) => Mathf.Lerp(roughness, Mathf.Max(0.06f, roughness * 0.25f), wet);
        private static float WetRoughnessScale(float wet) => Mathf.Lerp(1f, 0.25f, wet);
    }
}
