using System;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;

namespace UnityRemix
{
    /// <summary>
    /// Detects Unity engine fog (UnityEngine.RenderSettings) and synchronizes it with
    /// OpenRemix's path-traced volumetrics via remixapi_SetFogState.
    /// Supports automatic level/trigger detection, density/distance multipliers, and manual override.
    /// </summary>
    public class RemixFogManager
    {
        private readonly ManualLogSource logger;

        // Configuration
        public ConfigEntry<bool> ConfigEnableFog { get; }
        public ConfigEntry<float> ConfigFogDensityMultiplier { get; }
        public ConfigEntry<float> ConfigFogDistanceMultiplier { get; }
        public ConfigEntry<bool> ConfigFogManualOverride { get; }
        public ConfigEntry<int> ConfigFogManualMode { get; }
        public ConfigEntry<float> ConfigFogManualDensity { get; }
        public ConfigEntry<float> ConfigFogManualStart { get; }
        public ConfigEntry<float> ConfigFogManualEnd { get; }
        public ConfigEntry<float> ConfigFogManualColorR { get; }
        public ConfigEntry<float> ConfigFogManualColorG { get; }
        public ConfigEntry<float> ConfigFogManualColorB { get; }

        // State caching
        private bool forceDirty = true;
        private bool lastSentActive = false;
        private uint lastSentMode = 0;
        private Color lastSentColor = Color.black;
        private float lastSentScale = 0f;
        private float lastSentEnd = 0f;
        private float lastSentDensity = 0f;

        // Diagnostics
        public bool GameFogEnabled => RenderSettings.fog;
        public FogMode GameFogMode => RenderSettings.fogMode;
        public Color GameFogColor => RenderSettings.fogColor;
        public float GameFogDensity => RenderSettings.fogDensity;
        public float GameFogStart => RenderSettings.fogStartDistance;
        public float GameFogEnd => RenderSettings.fogEndDistance;

        public bool IsActiveInRemix => lastSentActive;
        public uint CurrentRemixMode => lastSentMode;
        public string StatusText { get; private set; } = "Initializing...";

        public RemixFogManager(ManualLogSource logger, ConfigFile config)
        {
            this.logger = logger;

            ConfigEnableFog = config.Bind("Fog", "EnableFog", true,
                "Synchronize Unity fog with OpenRemix path-traced volumetric fog.");

            ConfigFogDensityMultiplier = config.Bind("Fog", "DensityMultiplier", 1.0f,
                new ConfigDescription("Multiplier for exponential/exp2 fog density.",
                    new AcceptableValueRange<float>(0.01f, 20.0f)));

            ConfigFogDistanceMultiplier = config.Bind("Fog", "DistanceMultiplier", 1.0f,
                new ConfigDescription("Multiplier for linear fog distances (scales start and end distance).",
                    new AcceptableValueRange<float>(0.1f, 10.0f)));

            ConfigFogManualOverride = config.Bind("Fog", "ManualOverride", false,
                "Override game fog with manual volumetric fog settings.");

            ConfigFogManualMode = config.Bind("Fog", "ManualMode", 3,
                new ConfigDescription("Manual fog mode: 0 = Off, 1 = Exp, 2 = Exp2, 3 = Linear.",
                    new AcceptableValueRange<int>(0, 3)));

            ConfigFogManualDensity = config.Bind("Fog", "ManualDensity", 0.01f,
                new ConfigDescription("Manual exponential fog density.",
                    new AcceptableValueRange<float>(0.0001f, 1.0f)));

            ConfigFogManualStart = config.Bind("Fog", "ManualStartDistance", 5.0f,
                new ConfigDescription("Manual linear fog start distance in meters.",
                    new AcceptableValueRange<float>(0.0f, 500.0f)));

            ConfigFogManualEnd = config.Bind("Fog", "ManualEndDistance", 80.0f,
                new ConfigDescription("Manual linear fog end distance in meters.",
                    new AcceptableValueRange<float>(1.0f, 2000.0f)));

            ConfigFogManualColorR = config.Bind("Fog", "ManualColorR", 0.8f,
                new ConfigDescription("Manual fog color Red component [0..1].", new AcceptableValueRange<float>(0f, 1f)));
            ConfigFogManualColorG = config.Bind("Fog", "ManualColorG", 0.85f,
                new ConfigDescription("Manual fog color Green component [0..1].", new AcceptableValueRange<float>(0f, 1f)));
            ConfigFogManualColorB = config.Bind("Fog", "ManualColorB", 0.9f,
                new ConfigDescription("Manual fog color Blue component [0..1].", new AcceptableValueRange<float>(0f, 1f)));

            logger.LogInfo("RemixFogManager initialized.");
        }

        public void MarkDirty()
        {
            forceDirty = true;
        }

        /// <summary>
        /// Check Unity RenderSettings fog and dispatch updates to OpenRemix when values change.
        /// Should be called once per frame from Unity's main thread (Update or LateUpdate).
        /// </summary>
        public void Update()
        {
            if (RemixAPI.SetFogStateFunc == null)
            {
                StatusText = "RemixAPI SetFogState unavailable (non-OpenRemix runtime)";
                return;
            }

            uint mode = 0;
            Color color = Color.black;
            float scale = 0f;
            float end = 0f;
            float density = 0f;
            bool active = false;

            try
            {
                if (ConfigFogManualOverride.Value)
                {
                    mode = (uint)Mathf.Clamp(ConfigFogManualMode.Value, 0, 3);
                    float mr = Mathf.Clamp01(ConfigFogManualColorR.Value);
                    float mg = Mathf.Clamp01(ConfigFogManualColorG.Value);
                    float mb = Mathf.Clamp01(ConfigFogManualColorB.Value);
                    color = new Color(mr, mg, mb);
                    scale = Mathf.Max(ConfigFogManualStart.Value, 0f);
                    end = Mathf.Max(ConfigFogManualEnd.Value, scale + 0.1f);
                    density = Mathf.Clamp(ConfigFogManualDensity.Value, 0f, 1f);
                    active = mode != 0;
                }
                else if (ConfigEnableFog.Value && RenderSettings.fog)
                {
                    color = RenderSettings.fogColor;
                    float cr = float.IsNaN(color.r) || float.IsInfinity(color.r) ? 0f : Mathf.Clamp01(color.r);
                    float cg = float.IsNaN(color.g) || float.IsInfinity(color.g) ? 0f : Mathf.Clamp01(color.g);
                    float cb = float.IsNaN(color.b) || float.IsInfinity(color.b) ? 0f : Mathf.Clamp01(color.b);
                    color = new Color(cr, cg, cb);

                    float distMult = Mathf.Max(ConfigFogDistanceMultiplier.Value, 0.01f);
                    float densMult = Mathf.Max(ConfigFogDensityMultiplier.Value, 0.001f);

                    float rawStart = RenderSettings.fogStartDistance;
                    float rawEnd = RenderSettings.fogEndDistance;
                    float rawDens = RenderSettings.fogDensity;

                    if (float.IsNaN(rawStart) || float.IsInfinity(rawStart)) rawStart = 0f;
                    if (float.IsNaN(rawEnd) || float.IsInfinity(rawEnd)) rawEnd = 100f;
                    if (float.IsNaN(rawDens) || float.IsInfinity(rawDens)) rawDens = 0.01f;

                    switch (RenderSettings.fogMode)
                    {
                        case FogMode.Linear:
                            mode = 3; // D3DFOG_LINEAR
                            scale = Mathf.Max(rawStart * distMult, 0f);
                            end = Mathf.Max(rawEnd * distMult, scale + 0.1f);
                            active = end > 0.5f;
                            break;

                        case FogMode.Exponential:
                            mode = 1; // D3DFOG_EXP
                            density = Mathf.Clamp(rawDens * densMult, 0f, 1f);
                            active = density > 0.00001f;
                            break;

                        case FogMode.ExponentialSquared:
                            mode = 2; // D3DFOG_EXP2
                            density = Mathf.Clamp(rawDens * densMult, 0f, 1f);
                            active = density > 0.00001f;
                            break;

                        default:
                            mode = 1;
                            density = Mathf.Clamp(rawDens * densMult, 0f, 1f);
                            active = density > 0.00001f;
                            break;
                    }
                }
                else
                {
                    // Fog disabled
                    mode = 0;
                    active = false;
                }

                // Check if anything changed
                if (!forceDirty &&
                    active == lastSentActive &&
                    mode == lastSentMode &&
                    Mathf.Abs(color.r - lastSentColor.r) < 0.002f &&
                    Mathf.Abs(color.g - lastSentColor.g) < 0.002f &&
                    Mathf.Abs(color.b - lastSentColor.b) < 0.002f &&
                    Mathf.Abs(scale - lastSentScale) < 0.05f &&
                    Mathf.Abs(end - lastSentEnd) < 0.05f &&
                    Mathf.Abs(density - lastSentDensity) < 0.00005f)
                {
                    return;
                }

                // Send to OpenRemix (remixapi_SetFogState is thread-safe internally and uses its own g_gameFogLock)
                var info = new RemixAPI.remixapi_FogInfo
                {
                    sType = RemixAPI.remixapi_StructType.REMIXAPI_STRUCT_TYPE_FOG_INFO,
                    pNext = IntPtr.Zero,
                    mode = mode,
                    color = new RemixAPI.remixapi_Float3D
                    {
                        x = color.r,
                        y = color.g,
                        z = color.b
                    },
                    scale = scale,
                    end = end,
                    density = density
                };

                var result = RemixAPI.SetFogStateFunc(ref info);

                if (result == RemixAPI.remixapi_ErrorCode.REMIXAPI_ERROR_CODE_SUCCESS)
                {
                    lastSentActive = active;
                    lastSentMode = mode;
                    lastSentColor = color;
                    lastSentScale = scale;
                    lastSentEnd = end;
                    lastSentDensity = density;
                    forceDirty = false;

                    if (active)
                    {
                        string modeStr = mode == 1 ? "Exp" : (mode == 2 ? "Exp2" : "Linear");
                        StatusText = $"Active ({modeStr}, RGB: {color.r:F2},{color.g:F2},{color.b:F2})";
                        logger.LogInfo($"[Volumetrics] Fog updated in OpenRemix -> mode: {modeStr}, color: ({color.r:F2}, {color.g:F2}, {color.b:F2}), density: {density:F5}, start: {scale:F1}m, end: {end:F1}m");
                    }
                    else
                    {
                        StatusText = "Inactive (No fog)";
                        if (lastSentActive != active)
                        {
                            logger.LogInfo("[Volumetrics] Fog deactivated in OpenRemix (mode: None).");
                        }
                    }
                }
                else
                {
                    StatusText = $"Error setting fog: {result}";
                    logger.LogWarning($"[Volumetrics] remixapi_SetFogState failed with error code: {result}");
                }
            }
            catch (Exception ex)
            {
                StatusText = $"Exception: {ex.Message}";
                logger.LogError($"[Volumetrics] Exception in RemixFogManager.Update: {ex}");
            }
        }

        public void Cleanup()
        {
            if (RemixAPI.SetFogStateFunc != null && lastSentActive)
            {
                try
                {
                    var clearInfo = new RemixAPI.remixapi_FogInfo
                    {
                        sType = RemixAPI.remixapi_StructType.REMIXAPI_STRUCT_TYPE_FOG_INFO,
                        pNext = IntPtr.Zero,
                        mode = 0
                    };
                    RemixAPI.SetFogStateFunc(ref clearInfo);
                    lastSentActive = false;
                    lastSentMode = 0;
                    logger.LogInfo("[Volumetrics] Cleared fog state on cleanup.");
                }
                catch (Exception ex)
                {
                    logger.LogWarning($"[Volumetrics] Failed to clear fog on cleanup: {ex.Message}");
                }
            }
        }
    }
}
