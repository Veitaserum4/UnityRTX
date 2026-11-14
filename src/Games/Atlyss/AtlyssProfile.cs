using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace UnityRemix.Games.Atlyss
{
    internal sealed class AtlyssProfile : GameProfile
    {
        public override string Name => "ATLYSS";
        public override string[] ExecutableNames => new[] { "ATLYSS" };
        public override string DefaultsFolder => "Atlyss";

        // Each area's sky camera carries a CameraFogDensity: RenderSettings.fogDensity holds the
        // sky's density until that camera has drawn, then its fogDensity, which the main camera
        // draws the world with. Read at an arbitrary time, RenderSettings alternates between the
        // two; the world's is the component's.
        private Type fogDensityType;
        private FieldInfo fogDensityField;
        private bool lookedUp;
        private Behaviour[] fogComponents = Array.Empty<Behaviour>();
        private float nextRefresh;

        public override void OnSceneLoaded() => nextRefresh = 0f;

        public override float WorldFogDensity(float density)
        {
            if (!lookedUp)
            {
                lookedUp = true;
                fogDensityType = AccessTools.TypeByName("CameraFogDensity");
                fogDensityField = fogDensityType != null ? AccessTools.Field(fogDensityType, "fogDensity") : null;
                if (fogDensityField == null)
                    Logger.LogInfo("[ATLYSS] CameraFogDensity.fogDensity not found: fog density read from RenderSettings");
            }
            if (fogDensityField == null)
                return density;
            // Areas load additively; the components are looked up again on each scene load and
            // once a second.
            if (Time.unscaledTime >= nextRefresh)
            {
                nextRefresh = Time.unscaledTime + 1f;
                var found = UnityEngine.Object.FindObjectsOfType(fogDensityType);
                fogComponents = new Behaviour[found.Length];
                for (int i = 0; i < found.Length; i++)
                    fogComponents[i] = found[i] as Behaviour;
            }
            foreach (var c in fogComponents)
            {
                if (c == null || !c.isActiveAndEnabled)
                    continue;
                var cam = c.GetComponent<Camera>();
                if (cam != null && !cam.enabled)
                    continue;
                return (float)fogDensityField.GetValue(c);
            }
            return density;
        }
    }
}
