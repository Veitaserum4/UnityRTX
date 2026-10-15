using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;

namespace UnityRemix
{
    /// <summary>
    /// Manages water bodies, glass materials, and underwater camera detection for OpenRemix path tracing.
    /// In OpenRemix:
    /// - Translucent bodies (water): Snell's window refraction, TIR, and Beer-Lambert volumetric absorption.
    /// - Translucent sheets (glass): Specular transmission sheet with Fresnel reflection.
    /// - Camera medium: Sets remixapi_CameraMediumEXT when the camera is submerged.
    /// </summary>
    public class RemixWaterManager
    {
        private readonly ManualLogSource logger;

        // Configuration
        public ConfigEntry<bool> ConfigEnableTranslucents { get; }
        public ConfigEntry<bool> ConfigEnableWater { get; }
        public ConfigEntry<bool> ConfigEnableGlass { get; }
        public ConfigEntry<float> ConfigWaterIOR { get; }
        public ConfigEntry<float> ConfigWaterClarity { get; }
        public ConfigEntry<float> ConfigBloodIOR { get; }
        public ConfigEntry<float> ConfigBloodClarity { get; }
        public ConfigEntry<float> ConfigGlassIOR { get; }
        public ConfigEntry<bool> ConfigEnableUnderwaterMedium { get; }

        // Live stats
        public int WaterMaterialCount { get; internal set; }
        public int GlassMaterialCount { get; internal set; }
        public int ActiveWaterSurfaceCount => waterSurfaces.Count;

        private volatile bool isCameraUnderwater = false;
        public bool IsCameraUnderwater => isCameraUnderwater;

        private readonly object mediumLock = new object();
        private Color currentUnderwaterColor = new Color(0.15f, 0.55f, 0.75f, 1.0f);
        private float currentUnderwaterDistance = 15.0f;

        public Color CurrentUnderwaterColor
        {
            get { lock (mediumLock) return currentUnderwaterColor; }
            private set { lock (mediumLock) currentUnderwaterColor = value; }
        }

        public float CurrentUnderwaterDistance
        {
            get { lock (mediumLock) return currentUnderwaterDistance; }
            private set { lock (mediumLock) currentUnderwaterDistance = value; }
        }

        // Registered water surface tracking
        private struct WaterSurfaceInfo
        {
            public int rendererId;
            public Bounds worldBounds;
            public float surfaceY;
            public Color waterColor;
            public float clarityDistance;
            public float lastSeenTime;
        }

        private readonly Dictionary<int, WaterSurfaceInfo> waterSurfaces = new Dictionary<int, WaterSurfaceInfo>();
        private readonly object surfacesLock = new object();

        // Cached underwater overlay search (e.g. ULTRAKILL 'UnderwaterOverlay')
        private float lastOverlayCheckTime = 0f;
        private bool overlayUnderwaterDetected = false;

        private static readonly System.Reflection.MethodInfo physicsCheckSphereMethod = FindPhysicsCheckSphere();
        private static System.Reflection.MethodInfo FindPhysicsCheckSphere()
        {
            try
            {
                var physType = Type.GetType("UnityEngine.Physics, UnityEngine.PhysicsModule") 
                            ?? Type.GetType("UnityEngine.Physics, UnityEngine");
                if (physType != null)
                {
                    return physType.GetMethod("CheckSphere", new[] { typeof(Vector3), typeof(float), typeof(int) });
                }
            }
            catch { }
            return null;
        }

        public RemixWaterManager(ManualLogSource logger, ConfigFile config)
        {
            this.logger = logger;

            ConfigEnableTranslucents = config.Bind(
                "Translucents", "EnableTranslucents", true,
                "Enable OpenRemix path-traced translucent material rendering for water and glass."
            );

            ConfigEnableWater = config.Bind(
                "Translucents", "EnableWaterDetection", true,
                "Automatically detect water surfaces and render them as refractive translucent bodies with volumetric absorption."
            );

            ConfigEnableGlass = config.Bind(
                "Translucents", "EnableGlassDetection", true,
                "Automatically detect glass/windows and render them as thin specular transmission sheets."
            );

            ConfigWaterIOR = config.Bind(
                "Translucents", "WaterRefractiveIndex", 1.333f,
                "Index of refraction for water (standard fresh/sea water is ~1.333)."
            );

            ConfigWaterClarity = config.Bind(
                "Translucents", "WaterClarityDistance", 15.0f,
                "Transmittance measurement distance in meters for water. Light is absorbed exponentially with depth (Beer-Lambert law)."
            );

            ConfigBloodIOR = config.Bind(
                "Translucents", "BloodRefractiveIndex", 1.34f,
                "Index of refraction for blood and visceral fluids (standard blood is ~1.34 - 1.35)."
            );

            ConfigBloodClarity = config.Bind(
                "Translucents", "BloodClarityDistance", 2.0f,
                "Transmittance measurement distance in meters for blood and visceral fluids. Lower values make blood deep and dark crimson faster."
            );

            ConfigGlassIOR = config.Bind(
                "Translucents", "GlassRefractiveIndex", 1.52f,
                "Index of refraction for glass (crown glass is ~1.50 - 1.52)."
            );

            ConfigEnableUnderwaterMedium = config.Bind(
                "Translucents", "EnableUnderwaterMedium", true,
                "Send camera medium parameters to OpenRemix when the camera goes underwater, enabling Snell's window and underwater volumetric fog."
            );

            logger.LogInfo("[Translucents] RemixWaterManager initialized.");
        }

        /// <summary>
        /// Registers or updates an active water surface renderer so its bounds can be checked for underwater camera submergence.
        /// </summary>
        public void RegisterWaterSurface(Renderer renderer, Color waterColor, float clarity)
        {
            if (renderer == null) return;
            if (!renderer.enabled || !renderer.gameObject.activeInHierarchy) return;
            var sm = renderer.sharedMaterial;
            if (sm != null && SceneMeshScanner.IsMaterialOrShaderInvisible(sm)) return;

            try
            {
                int id = renderer.GetInstanceID();
                Bounds b = renderer.bounds;
                float topY = b.max.y;
                float now = Environment.TickCount * 0.001f;

                lock (surfacesLock)
                {
                    waterSurfaces[id] = new WaterSurfaceInfo
                    {
                        rendererId = id,
                        worldBounds = b,
                        surfaceY = topY,
                        waterColor = waterColor,
                        clarityDistance = clarity > 0f ? clarity : ConfigWaterClarity.Value,
                        lastSeenTime = now
                    };
                }
            }
            catch { }
        }

        /// <summary>
        /// Evaluates camera position and scene state on the Unity MAIN THREAD to determine whether the camera is submerged in water.
        /// Thread-safe for background render thread to query properties at any time.
        /// </summary>
        public void UpdateMainThread(Camera mainCam, Vector3 cameraPosition)
        {
            if (!ConfigEnableTranslucents.Value || !ConfigEnableUnderwaterMedium.Value)
            {
                if (isCameraUnderwater)
                {
                    isCameraUnderwater = false;
                    logger.LogInfo("[Translucents] Camera exited water body into air.");
                }
                return;
            }

            bool underwater = false;
            Color detectedColor = new Color(0.15f, 0.55f, 0.75f, 1.0f);
            float detectedDistance = ConfigWaterClarity.Value;

            float now = Environment.TickCount * 0.001f;

            // 1. Check against registered water surface bounding boxes
            lock (surfacesLock)
            {
                // Prune stale surfaces not seen for > 10 seconds
                if (waterSurfaces.Count > 0)
                {
                    List<int> toRemove = null;
                    foreach (var kvp in waterSurfaces)
                    {
                        if (now - kvp.Value.lastSeenTime > 10f)
                        {
                            if (toRemove == null) toRemove = new List<int>();
                            toRemove.Add(kvp.Key);
                            continue;
                        }

                        var surf = kvp.Value;
                        // Camera is within the X/Z bounds of the water surface and below the top plane
                        if (cameraPosition.x >= surf.worldBounds.min.x && cameraPosition.x <= surf.worldBounds.max.x &&
                            cameraPosition.z >= surf.worldBounds.min.z && cameraPosition.z <= surf.worldBounds.max.z &&
                            cameraPosition.y <= (surf.surfaceY + 0.1f) && cameraPosition.y >= (surf.worldBounds.min.y - 2.0f))
                        {
                            underwater = true;
                            detectedColor = surf.waterColor;
                            detectedDistance = surf.clarityDistance;
                            break;
                        }
                    }

                    if (toRemove != null)
                    {
                        for (int i = 0; i < toRemove.Count; i++)
                            waterSurfaces.Remove(toRemove[i]);
                    }
                }
            }

            // 2. Physics layer check (Layer 4 = Water) via reflection on main thread
            if (!underwater && physicsCheckSphereMethod != null)
            {
                try
                {
                    int waterLayerMask = 1 << 4;
                    object res = physicsCheckSphereMethod.Invoke(null, new object[] { cameraPosition, 0.15f, waterLayerMask });
                    if (res is bool b && b)
                    {
                        underwater = true;
                    }
                }
                catch { }
            }

            // 3. Game-specific underwater overlay / UI check (e.g. ULTRAKILL 'UnderwaterOverlay') on main thread
            if (!underwater && (now - lastOverlayCheckTime > 0.2f))
            {
                lastOverlayCheckTime = now;
                try
                {
                    // Look for active GameObject named 'UnderwaterOverlay' or similar
                    var obj = GameObject.Find("UnderwaterOverlay");
                    if (obj != null && obj.activeInHierarchy)
                    {
                        var img = obj.GetComponent<UnityEngine.UI.Graphic>();
                        if (img != null && img.enabled && img.color.a > 0.05f)
                        {
                            overlayUnderwaterDetected = true;
                            if (img.color.r > 0f || img.color.g > 0f || img.color.b > 0f)
                            {
                                detectedColor = new Color(img.color.r, img.color.g, img.color.b, 1.0f);
                                if (img.color.r > 0.35f && img.color.r > (img.color.g + img.color.b) * 0.7f)
                                {
                                    detectedDistance = ConfigBloodClarity.Value;
                                }
                            }
                        }
                        else
                        {
                            overlayUnderwaterDetected = false;
                        }
                    }
                    else
                    {
                        overlayUnderwaterDetected = false;
                    }
                }
                catch
                {
                    overlayUnderwaterDetected = false;
                }
            }

            if (overlayUnderwaterDetected)
            {
                underwater = true;
            }

            if (underwater != isCameraUnderwater)
            {
                isCameraUnderwater = underwater;
                if (underwater)
                {
                    CurrentUnderwaterColor = detectedColor;
                    CurrentUnderwaterDistance = detectedDistance;
                    logger.LogInfo($"[Translucents] Camera entered water/fluid body! Color: ({detectedColor.r:F2}, {detectedColor.g:F2}, {detectedColor.b:F2}), depth: {detectedDistance:F1}m");
                }
                else
                {
                    logger.LogInfo("[Translucents] Camera exited water/fluid body into air.");
                }
            }
            else if (underwater)
            {
                CurrentUnderwaterColor = detectedColor;
                CurrentUnderwaterDistance = detectedDistance;
            }
        }

        public void Clear()
        {
            lock (surfacesLock)
            {
                waterSurfaces.Clear();
            }
            isCameraUnderwater = false;
        }
    }
}
