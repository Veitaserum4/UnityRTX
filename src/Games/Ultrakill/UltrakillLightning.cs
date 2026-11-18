using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace UnityRemix.Games.Ultrakill
{
    /// <summary>
    /// The storm's decorative lightning (LightningStrikeDecorative_Animated, 5-2 and 5-3): its
    /// bolt is a quad whose AnimatedTexture copies the strike's frames on the GPU
    /// (Graphics.CopyTexture into a texture set through the property block), blank between
    /// strikes. Path-traced, the quad shows its material's still bolt all the time; it is drawn
    /// only while the game shows a strike (its buildup and strike).
    /// </summary>
    internal sealed class UltrakillLightning
    {
        private const int RefreshFrames = 120;

        private struct Strike
        {
            public Component component;
            public Renderer bolt;
        }

        private readonly List<Strike> strikes = new List<Strike>();
        private Type strikeType;
        private FieldInfo flashing, lightningAnim;
        private bool unavailable;
        private int lastRefresh = -RefreshFrames;

        public void Reset()
        {
            strikes.Clear();
            lastRefresh = -RefreshFrames;
        }

        public void Update(ManualLogSource logger, int frameCount)
        {
            if (unavailable)
                return;
            if (strikeType == null)
            {
                strikeType = AccessTools.TypeByName("LightningStrikeDecorative_Animated");
                flashing = strikeType != null ? AccessTools.Field(strikeType, "flashing") : null;
                lightningAnim = strikeType != null ? AccessTools.Field(strikeType, "lightningAnim") : null;
                if (flashing == null || lightningAnim == null)
                {
                    unavailable = true;
                    logger.LogInfo("[UltrakillLightning] LightningStrikeDecorative_Animated.flashing/lightningAnim not found: lightning left as drawn");
                    return;
                }
            }
            // Strikes start under inactive groups (a sea's lightning comes later): found again
            // every few seconds, not every frame.
            if (frameCount - lastRefresh >= RefreshFrames)
            {
                lastRefresh = frameCount;
                Refresh(logger);
            }
            for (int i = 0; i < strikes.Count; i++)
            {
                var strike = strikes[i];
                if (strike.component == null || strike.bolt == null)
                    continue;
                bool shown = (bool)flashing.GetValue(strike.component);
                if (strike.bolt.forceRenderingOff == shown)
                    strike.bolt.forceRenderingOff = !shown;
            }
        }

        private void Refresh(ManualLogSource logger)
        {
            int before = strikes.Count;
            strikes.Clear();
            foreach (var o in Resources.FindObjectsOfTypeAll(strikeType))
            {
                // Scene instances only (FindObjectsOfTypeAll also returns prefabs).
                var c = o as Component;
                if (c == null || !c.gameObject.scene.IsValid())
                    continue;
                var anim = lightningAnim.GetValue(c) as Component;
                var bolt = anim != null ? anim.GetComponent<Renderer>() : null;
                if (bolt != null)
                    strikes.Add(new Strike { component = c, bolt = bolt });
            }
            if (strikes.Count != before && VerboseLog.On)
                logger.LogInfo($"[UltrakillLightning] {strikes.Count} lightning strikes");
        }
    }
}
