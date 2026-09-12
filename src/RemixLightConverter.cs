using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;

namespace UnityRemix
{
    /// <summary>
    /// Thread-safe snapshot of Unity light properties captured on the main thread.
    /// </summary>
    public struct UnityLightData
    {
        public int instanceId;
        public ulong hash;
        public LightType type;
        public Color color;
        public float intensity;
        public float range;
        public Vector3 position;
        public Vector3 forward;
        public float spotAngle;
        public string name;
    }

    /// <summary>
    /// Converts Unity lights to Remix lights
    /// </summary>
    public class RemixLightConverter
    {
        private readonly ManualLogSource logger;
        private readonly ConfigEntry<bool> configEnableLights;
        private readonly ConfigEntry<float> configLightIntensityMultiplier;
        private readonly ConfigEntry<int> configDebugLogInterval;
        private readonly object apiLock;
        
        // Cached delegates
        private RemixAPI.PFN_remixapi_CreateLight createLightFunc;
        private RemixAPI.PFN_remixapi_DestroyLight destroyLightFunc;
        private RemixAPI.PFN_remixapi_DrawLightInstance drawLightInstanceFunc;
        
        // Cache for Unity lights - maps Light instance ID to Remix handle
        private readonly object cacheLock = new object();
        private readonly Dictionary<int, IntPtr> lightCache = new Dictionary<int, IntPtr>();

        // Thread-safe snapshot of light data transferred from main thread to render thread
        private readonly object lightDataLock = new object();
        private UnityLightData[] currentLightData = Array.Empty<UnityLightData>();

        public int CachedLightCount
        {
            get
            {
                lock (lightDataLock)
                {
                    return currentLightData != null ? currentLightData.Length : 0;
                }
            }
        }
        
        public RemixLightConverter(
            ManualLogSource logger,
            ConfigEntry<bool> enableLights,
            ConfigEntry<float> intensityMultiplier,
            ConfigEntry<int> debugLogInterval,
            RemixAPI.remixapi_Interface remixInterface,
            object apiLock)
        {
            this.logger = logger;
            this.configEnableLights = enableLights;
            this.configLightIntensityMultiplier = intensityMultiplier;
            this.configDebugLogInterval = debugLogInterval;
            this.apiLock = apiLock;
            
            // Cache delegates
            if (remixInterface.CreateLight != IntPtr.Zero)
            {
                createLightFunc = Marshal.GetDelegateForFunctionPointer<RemixAPI.PFN_remixapi_CreateLight>(
                    remixInterface.CreateLight);
            }
            
            if (remixInterface.DestroyLight != IntPtr.Zero)
            {
                destroyLightFunc = Marshal.GetDelegateForFunctionPointer<RemixAPI.PFN_remixapi_DestroyLight>(
                    remixInterface.DestroyLight);
            }
            
            if (remixInterface.DrawLightInstance != IntPtr.Zero)
            {
                drawLightInstanceFunc = Marshal.GetDelegateForFunctionPointer<RemixAPI.PFN_remixapi_DrawLightInstance>(
                    remixInterface.DrawLightInstance);
            }
        }
        
        /// <summary>
        /// Refresh cached light list from scene (called on Unity main thread)
        /// </summary>
        public void RefreshLightCache()
        {
            if (!configEnableLights.Value)
                return;
                
            Light[] allLights = UnityEngine.Object.FindObjectsOfType<Light>();
            var lightList = new List<UnityLightData>(allLights.Length);

            for (int i = 0; i < allLights.Length; i++)
            {
                Light l = allLights[i];
                if (l == null || !l.enabled || !l.gameObject.activeInHierarchy || l.intensity <= 0.001f || l.range <= 0.001f)
                    continue;

                Transform t = l.transform;
                string path = HashUtils.GetHierarchyPath(t);
                ulong hash = HashUtils.HashStringFNV(path);

                // For dynamically instantiated objects (like explosions, muzzle flashes, projectiles),
                // include instance ID in hash to avoid hash collisions between multiple clones.
                if (path.Contains("(Clone)"))
                {
                    hash ^= ((ulong)(uint)l.GetInstanceID() * 1099511628211UL);
                }

                lightList.Add(new UnityLightData
                {
                    instanceId = l.GetInstanceID(),
                    hash = hash,
                    type = l.type,
                    color = l.color,
                    intensity = l.intensity,
                    range = l.range,
                    position = t.position,
                    forward = t.forward,
                    spotAngle = l.spotAngle,
                    name = l.name
                });
            }

            lock (lightDataLock)
            {
                currentLightData = lightList.ToArray();
            }
        }
        
        /// <summary>
        /// Clear light cache (call on scene change)
        /// </summary>
        public void ClearCache()
        {
            lock (cacheLock)
            {
                lock (apiLock)
                {
                    foreach (var lightHandle in lightCache.Values)
                    {
                        if (lightHandle != IntPtr.Zero && destroyLightFunc != null)
                        {
                            try { destroyLightFunc(lightHandle); } catch { }
                        }
                    }
                }
                lightCache.Clear();
            }

            lock (lightDataLock)
            {
                currentLightData = Array.Empty<UnityLightData>();
            }
        }
        
        /// <summary>
        /// Process and draw all Unity lights (runs on render thread)
        /// </summary>
        public void ProcessLights(int frameCount)
        {
            if (!configEnableLights.Value)
            {
                lock (cacheLock)
                {
                    if (lightCache.Count > 0)
                    {
                        lock (apiLock)
                        {
                            foreach (var handle in lightCache.Values)
                            {
                                if (handle != IntPtr.Zero && destroyLightFunc != null)
                                {
                                    try { destroyLightFunc(handle); } catch { }
                                }
                            }
                        }
                        lightCache.Clear();
                    }
                }
                return;
            }

            if (drawLightInstanceFunc == null || createLightFunc == null)
                return;

            UnityLightData[] lightsSnapshot;
            lock (lightDataLock)
            {
                lightsSnapshot = currentLightData;
            }

            if (lightsSnapshot == null)
                return;

            lock (cacheLock)
            {
                HashSet<int> activeLightIds = new HashSet<int>();

                for (int i = 0; i < lightsSnapshot.Length; i++)
                {
                    ref UnityLightData lightData = ref lightsSnapshot[i];
                    activeLightIds.Add(lightData.instanceId);

                    IntPtr lightHandle = CreateRemixLightFromData(ref lightData, frameCount);

                    if (lightHandle != IntPtr.Zero)
                    {
                        lightCache[lightData.instanceId] = lightHandle;
                        lock (apiLock)
                        {
                            drawLightInstanceFunc(lightHandle);
                        }
                    }
                }

                // Destroy persistent Remix lights that are no longer active in Unity
                List<int> toRemove = null;
                foreach (var kvp in lightCache)
                {
                    if (!activeLightIds.Contains(kvp.Key))
                    {
                        if (toRemove == null)
                            toRemove = new List<int>();
                        toRemove.Add(kvp.Key);

                        if (kvp.Value != IntPtr.Zero && destroyLightFunc != null)
                        {
                            lock (apiLock)
                            {
                                try { destroyLightFunc(kvp.Value); } catch { }
                            }
                        }
                    }
                }

                if (toRemove != null)
                {
                    for (int i = 0; i < toRemove.Count; i++)
                    {
                        lightCache.Remove(toRemove[i]);
                    }
                }
            }
        }
        
        /// <summary>
        /// Create Remix light from Unity light snapshot
        /// </summary>
        private IntPtr CreateRemixLightFromData(ref UnityLightData light, int frameCount)
        {
            if (createLightFunc == null)
                return IntPtr.Zero;
            
            try
            {
                // Convert Unity color and intensity to Remix radiance
                Color lightColor = light.color * light.intensity * configLightIntensityMultiplier.Value;
                var radiance = new RemixAPI.remixapi_Float3D(lightColor.r, lightColor.g, lightColor.b);
                
                // Create base light info
                var lightInfo = new RemixAPI.remixapi_LightInfo
                {
                    sType = RemixAPI.remixapi_StructType.REMIXAPI_STRUCT_TYPE_LIGHT_INFO,
                    pNext = IntPtr.Zero,
                    hash = light.hash,
                    radiance = radiance,
                    isDynamic = 1,
                    ignoreViewModel = 0
                };
                
                IntPtr lightHandle = IntPtr.Zero;
                
                switch (light.type)
                {
                    case LightType.Point:
                        lightHandle = CreatePointLight(ref light, lightInfo);
                        break;
                    
                    case LightType.Spot:
                        lightHandle = CreateSpotLight(ref light, lightInfo);
                        break;
                    
                    case LightType.Directional:
                        if (configDebugLogInterval.Value > 0 && frameCount % configDebugLogInterval.Value == 0)
                        {
                            logger.LogInfo($"Directional light '{light.name}' not yet supported");
                        }
                        break;
                    
                    default:
                        if (configDebugLogInterval.Value > 0 && frameCount % configDebugLogInterval.Value == 0)
                        {
                            logger.LogInfo($"Light type {light.type} for '{light.name}' not supported");
                        }
                        break;
                }
                
                return lightHandle;
            }
            catch (Exception ex)
            {
                logger.LogError($"Failed to create Remix light from '{light.name}': {ex.Message}");
                return IntPtr.Zero;
            }
        }
        
        private IntPtr CreatePointLight(ref UnityLightData light, RemixAPI.remixapi_LightInfo baseInfo)
        {
            var position = light.position;
            
            var sphereExt = new RemixAPI.remixapi_LightInfoSphereEXT
            {
                sType = RemixAPI.remixapi_StructType.REMIXAPI_STRUCT_TYPE_LIGHT_INFO_SPHERE_EXT,
                pNext = IntPtr.Zero,
                // Convert Unity Y-up to Remix Z-up: (x, y, z) -> (x, z, y)
                position = new RemixAPI.remixapi_Float3D(position.x, position.z, position.y),
                radius = light.range * 0.1f,
                shaping_hasvalue = 0,
                shaping_value = new RemixAPI.remixapi_LightInfoLightShaping(),
                volumetricRadianceScale = 1.0f
            };
            
            GCHandle sphereHandle = GCHandle.Alloc(sphereExt, GCHandleType.Pinned);
            
            try
            {
                baseInfo.pNext = sphereHandle.AddrOfPinnedObject();
                
                IntPtr handle;
                RemixAPI.remixapi_ErrorCode result;
                lock (apiLock)
                {
                    result = createLightFunc(ref baseInfo, out handle);
                }
                
                if (result != RemixAPI.remixapi_ErrorCode.REMIXAPI_ERROR_CODE_SUCCESS)
                {
                    logger.LogWarning($"Failed to create point light '{light.name}': {result}");
                    return IntPtr.Zero;
                }
                
                return handle;
            }
            finally
            {
                sphereHandle.Free();
            }
        }
        
        private IntPtr CreateSpotLight(ref UnityLightData light, RemixAPI.remixapi_LightInfo baseInfo)
        {
            var position = light.position;
            var direction = light.forward;
            
            var shaping = new RemixAPI.remixapi_LightInfoLightShaping
            {
                // Convert Unity Y-up to Remix Z-up: (x, y, z) -> (x, z, y)
                direction = new RemixAPI.remixapi_Float3D(direction.x, direction.z, direction.y),
                coneAngleDegrees = light.spotAngle,
                coneSoftness = 0.1f,
                focusExponent = 1.0f
            };
            
            var sphereExt = new RemixAPI.remixapi_LightInfoSphereEXT
            {
                sType = RemixAPI.remixapi_StructType.REMIXAPI_STRUCT_TYPE_LIGHT_INFO_SPHERE_EXT,
                pNext = IntPtr.Zero,
                // Convert Unity Y-up to Remix Z-up: (x, y, z) -> (x, z, y)
                position = new RemixAPI.remixapi_Float3D(position.x, position.z, position.y),
                radius = light.range * 0.1f,
                shaping_hasvalue = 1,
                shaping_value = shaping,
                volumetricRadianceScale = 1.0f
            };
            
            GCHandle sphereHandle = GCHandle.Alloc(sphereExt, GCHandleType.Pinned);
            
            try
            {
                baseInfo.pNext = sphereHandle.AddrOfPinnedObject();
                
                IntPtr handle;
                RemixAPI.remixapi_ErrorCode result;
                lock (apiLock)
                {
                    result = createLightFunc(ref baseInfo, out handle);
                }
                
                if (result != RemixAPI.remixapi_ErrorCode.REMIXAPI_ERROR_CODE_SUCCESS)
                {
                    logger.LogWarning($"Failed to create spot light '{light.name}': {result}");
                    return IntPtr.Zero;
                }
                
                return handle;
            }
            finally
            {
                sphereHandle.Free();
            }
        }
        
        /// <summary>
        /// Create a test light for debugging
        /// </summary>
        public IntPtr CreateTestLight()
        {
            if (createLightFunc == null)
                return IntPtr.Zero;
            
            var sphereLight = new RemixAPI.remixapi_LightInfoSphereEXT
            {
                sType = RemixAPI.remixapi_StructType.REMIXAPI_STRUCT_TYPE_LIGHT_INFO_SPHERE_EXT,
                pNext = IntPtr.Zero,
                position = new RemixAPI.remixapi_Float3D(0, -1, 0),
                radius = 0.1f,
                shaping_hasvalue = 0,
                shaping_value = new RemixAPI.remixapi_LightInfoLightShaping()
            };
            
            GCHandle sphereHandle = GCHandle.Alloc(sphereLight, GCHandleType.Pinned);
            
            try
            {
                var lightInfo = new RemixAPI.remixapi_LightInfo
                {
                    sType = RemixAPI.remixapi_StructType.REMIXAPI_STRUCT_TYPE_LIGHT_INFO,
                    pNext = sphereHandle.AddrOfPinnedObject(),
                    hash = 0x3,
                    radiance = new RemixAPI.remixapi_Float3D(100, 200, 100)
                };
                
                IntPtr handle;
                var result = createLightFunc(ref lightInfo, out handle);
                
                if (result != RemixAPI.remixapi_ErrorCode.REMIXAPI_ERROR_CODE_SUCCESS)
                {
                    logger.LogError($"Failed to create test light: {result}");
                    return IntPtr.Zero;
                }
                
                logger.LogInfo($"Test light created! Handle: {handle}");
                return handle;
            }
            finally
            {
                sphereHandle.Free();
            }
        }
    }
}
