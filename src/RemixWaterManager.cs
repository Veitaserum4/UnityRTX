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
        public ConfigEntry<float> ConfigGlassIOR { get; }
        public ConfigEntry<bool> ConfigEnableUnderwaterMedium { get; }

        // Live stats
        public int WaterMaterialCount { get; internal set; }
        public int GlassMaterialCount { get; internal set; }
        public int ActiveWaterSurfaceCount => waterSurfaces.Count;
        public bool IsCameraUnderwater { get; private set; }
        public Color CurrentUnderwaterColor { get; private set; } = new Color(0.15f, 0.55f, 0.75f, 1.0f);
        public float CurrentUnderwaterDistance { get; private set; } = 15.0f;

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

            int id = renderer.GetInstanceID();
            Bounds b = renderer.bounds;
            float topY = b.max.y;

            lock (surfacesLock)
            {
                waterSurfaces[id] = new WaterSurfaceInfo
                {
                    rendererId = id,
                    worldBounds = b,
                    surfaceY = topY,
                    waterColor = waterColor,
                    clarityDistance = clarity > 0f ? clarity : ConfigWaterClarity.Value,
                    lastSeenTime = Time.time
                };
            }
        }

        /// <summary>
        /// Evaluates camera position every frame to determine whether the camera is submerged in water.
        /// </summary>
        public void UpdateCameraState(Vector3 cameraPosition)
        {
            if (!ConfigEnableTranslucents.Value || !ConfigEnableUnderwaterMedium.Value)
            {
                IsCameraUnderwater = false;
                return;
            }

            bool underwater = false;
            Color detectedColor = new Color(0.15f, 0.55f, 0.75f, 1.0f);
            float detectedDistance = ConfigWaterClarity.Value;

            float now = Time.time;

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

            // 2. Physics layer check (Layer 4 = Water) via reflection
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

            // 3. Game-specific underwater overlay / UI check (e.g. ULTRAKILL 'UnderwaterOverlay')
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

            if (underwater != IsCameraUnderwater)
            {
                IsCameraUnderwater = underwater;
                if (underwater)
                {
                    CurrentUnderwaterColor = detectedColor;
                    CurrentUnderwaterDistance = detectedDistance;
                    logger.LogInfo($"[Translucents] Camera entered water body! Color: ({detectedColor.r:F2}, {detectedColor.g:F2}, {detectedColor.b:F2}), depth: {detectedDistance:F1}m");
                }
                else
                {
                    logger.LogInfo("[Translucents] Camera exited water body into air.");
                }
            }
        }

        public void Clear()
        {
            lock (surfacesLock)
            {
                waterSurfaces.Clear();
            }
            IsCameraUnderwater = false;
        }
    }
}
