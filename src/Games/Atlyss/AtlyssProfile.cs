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

        // Unlit/panUV (ShaderForge; the Sanctum's sky dome and its sea): its texture _mainTex
        // sampled at (uv + (_Value01, _Value02) x _Time.y) x _mainTex_ST.xy + _mainTex_ST.zw.
        private static readonly int PanSpeedX = Shader.PropertyToID("_Value01");
        private static readonly int PanSpeedY = Shader.PropertyToID("_Value02");

        // Unlit/panUV overlays _colorTint on its texture (its fragment program): the Sanctum's
        // sky is a turquoise tint with the clouds' texture faint in it.
        private static readonly int PanTint = Shader.PropertyToID("_colorTint");

        // panUV's FORWARD pass does not blend (no Blend state): its texels draw opaque, those of
        // alpha under one half discarded, though the shader sits in the transparent queue.
        // Imported as a blended sheet, the fog took the sky dome for open sky and left it clear.
        public override void OverrideMaterial(Material material, ref RemixMaterialManager.AlphaMode alphaMode, ref float alphaCutoff, ref Color albedoColor)
        {
            if (material.shader == null || material.shader.name != "Unlit/panUV")
                return;
            alphaMode = RemixMaterialManager.AlphaMode.Cutout;
            alphaCutoff = 0.5f;
        }

        // The areas' sky domes (_material_sky_*, panUV on a sphere in the 3D skybox).
        public override bool IsSkyDome(Material material) =>
            material.shader != null && material.shader.name == "Unlit/panUV" &&
            material.name.StartsWith("_material_sky", StringComparison.Ordinal);

        public override bool GetOverlayTint(Material material, out Color tint)
        {
            tint = default;
            if (material.shader == null || material.shader.name != "Unlit/panUV" || !material.HasProperty(PanTint))
                return false;
            tint = material.GetColor(PanTint);
            return true;
        }

        public override bool GetShaderScroll(Material material, out Vector4 uvST)
        {
            uvST = default;
            if (material.shader == null || material.shader.name != "Unlit/panUV")
                return false;
            var scale = material.GetTextureScale("_mainTex");
            var offset = material.GetTextureOffset("_mainTex");
            float t = Time.time;
            float u = material.GetFloat(PanSpeedX) * t * scale.x + offset.x;
            float v = material.GetFloat(PanSpeedY) * t * scale.y + offset.y;
            // Kept within one repeat (the texture wraps): float precision as time grows.
            uvST = new Vector4(scale.x, scale.y, u - Mathf.Floor(u), v - Mathf.Floor(v));
            return true;
        }

        // ATLYSS's light shafts: small cones (a ProBuilder cylinder of 16 vertices, tapered) under
        // windows and canopies, drawn additively with the Swoosh01 streak texture (material
        // 'Swoosh01 6', a warm tint). The same texture also streaks attack effects (the swing
        // torus, Standard), so the shaft is the additive particle shader on a plain mesh.
        public override bool IsLightShaft(Renderer renderer)
        {
            if (!(renderer is MeshRenderer))
                return false;
            var material = renderer.sharedMaterial;
            if (material == null || material.shader == null || material.shader.name != "Legacy Shaders/Particles/Additive")
                return false;
            var texture = material.HasProperty("_MainTex") ? material.GetTexture("_MainTex") : null;
            return texture != null && texture.name == "Swoosh01";
        }

        // Faint additive haze that is no light source: dust kicked up by moving (footsteps,
        // landing, dashing, sweeping) and water ripples (in puddles, and the rain's under its
        // _weatherEffect_*). Its torches and braziers share the dust's material
        // (_particleMaterial_waterCloud): told apart by the system's name or an ancestor's.
        private static readonly string[] UnlitParticleNames =
            { "footstep", "_effect_land", "_effect_dash", "broomsweep", "ripple", "weathereffect" };

        public override bool CastsParticleLight(ParticleSystemRenderer renderer)
        {
            for (var t = renderer.transform; t != null; t = t.parent)
            {
                string name = t.name;
                foreach (var unlit in UnlitParticleNames)
                    if (name.IndexOf(unlit, StringComparison.OrdinalIgnoreCase) >= 0)
                        return false;
            }
            return true;
        }

        // The minimap's icons (SpriteRenderers on Minimap, drawn by _camera_overworldMinimap into
        // the map): never in the world.
        private int minimapLayer = -2;

        public override bool IsNeverRenderedLayer(int layer)
        {
            if (minimapLayer == -2)
                minimapLayer = LayerMask.NameToLayer("Minimap");
            return layer == minimapLayer;
        }

        public override void OnSceneLoaded()
        {
            nextRefresh = 0f;
            nextSkyRefresh = 0f;
        }

        // Areas with a 3D skybox (the Sanctum's) carry a SkyboxCamera: each LateUpdate it takes
        // the main camera's rotation and, under its parent, the main camera's position / 100
        // (_skyboxScale), drawing a miniature of the distant land the main camera draws over.
        private Type skyboxCameraType;
        private FieldInfo skyboxScaleField;
        private bool skyLookedUp;
        private Behaviour[] skyComponents = Array.Empty<Behaviour>();
        private float nextSkyRefresh;

        // The sky camera stands at parent + parent scale x mainCamera.position / _skyboxScale, so a
        // sky point p is seen from the main camera at (p - parent) x _skyboxScale / parent scale.
        public override bool GetSkyboxView(Camera worldCamera, out SkyboxView view)
        {
            view = default;
            var sky = FindSkyComponent(worldCamera, out var cam);
            if (sky == null)
                return false;
            float scale = skyboxScaleField != null ? (float)skyboxScaleField.GetValue(sky) : 100f;
            var parent = sky.transform.parent;
            float parentScale = parent != null ? parent.lossyScale.x : 1f;
            if (!(scale > 0f) || !(parentScale > 0f))
                return false;
            view.camera = cam;
            view.origin = parent != null ? parent.position : Vector3.zero;
            view.scale = scale / parentScale;
            return true;
        }

        private Behaviour FindSkyComponent(Camera worldCamera, out Camera camera)
        {
            camera = null;
            if (!skyLookedUp)
            {
                skyLookedUp = true;
                skyboxCameraType = AccessTools.TypeByName("SkyboxCamera");
                skyboxScaleField = skyboxCameraType != null ? AccessTools.Field(skyboxCameraType, "_skyboxScale") : null;
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
                {
                    camera = cam;
                    return c;
                }
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
