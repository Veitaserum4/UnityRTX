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

        public override void OnSceneLoaded()
        {
            nextRefresh = 0f;
            nextSkyRefresh = 0f;
        }

        // Areas with a 3D skybox (the Sanctum's) carry a SkyboxCamera: each LateUpdate it takes
        // the main camera's rotation and, under its parent, the main camera's position / 100
        // (_skyboxScale), drawing a miniature of the distant land the main camera draws over.
        private Type skyboxCameraType;
        private bool skyLookedUp;
        private Behaviour[] skyComponents = Array.Empty<Behaviour>();
        private float nextSkyRefresh;

        public override Camera FindSkyCamera(Camera worldCamera)
        {
            if (!skyLookedUp)
            {
                skyLookedUp = true;
                skyboxCameraType = AccessTools.TypeByName("SkyboxCamera");
                if (skyboxCameraType == null)
                    Logger.LogInfo("[ATLYSS] SkyboxCamera not found: no 3D skybox");
            }
            if (skyboxCameraType == null)
                return null;
            // Looked up again on each scene load (areas load additively) and once a second.
            if (Time.unscaledTime >= nextSkyRefresh)
            {
                nextSkyRefresh = Time.unscaledTime + 1f;
                var found = UnityEngine.Object.FindObjectsOfType(skyboxCameraType);
                skyComponents = new Behaviour[found.Length];
                for (int i = 0; i < found.Length; i++)
                    skyComponents[i] = found[i] as Behaviour;
            }
            foreach (var c in skyComponents)
            {
                if (c == null || !c.isActiveAndEnabled)
                    continue;
                var cam = c.GetComponent<Camera>();
                if (cam != null && cam.enabled && cam != worldCamera)
                    return cam;
            }
            return null;
        }

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
