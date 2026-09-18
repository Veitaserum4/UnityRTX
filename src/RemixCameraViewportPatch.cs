using System;
using HarmonyLib;
using UnityEngine;
using BepInEx.Logging;

namespace UnityRemix
{
    /// <summary>
    /// Harmony patch to preserve coordinate space fidelity for managed UI cameras.
    /// When RemixUIOverlay redirects a UI camera that originally rendered to a fixed-size offscreen RenderTexture
    /// (e.g. REPO's 1280x720 HUD camera) to the full-resolution uiRenderTexture, Unity's internal ScreenToViewportPoint
    /// divides by the new full-resolution dimensions instead of the original render texture dimensions.
    /// This causes in-game UI mouse calculations (like SemiFunc.UIMousePosToUIPos) to be compressed into a corner.
    /// This patch intercepts ScreenToViewportPoint and ViewportToScreenPoint on managed UI cameras to preserve
    /// the camera's original coordinate system.
    /// </summary>
    public static class RemixCameraViewportPatch
    {
        private static ManualLogSource logger;
        private static int screenToVpLogCount = 0;

        public static void Apply(Harmony harmony, ManualLogSource log)
        {
            logger = log;
            try
            {
                var screenToViewport = AccessTools.Method(typeof(Camera), nameof(Camera.ScreenToViewportPoint), new[] { typeof(Vector3) });
                if (screenToViewport != null)
                {
                    harmony.Patch(
                        screenToViewport,
                        prefix: new HarmonyMethod(typeof(RemixCameraViewportPatch), nameof(ScreenToViewportPointPrefix))
                    );
                }

                var viewportToScreen = AccessTools.Method(typeof(Camera), nameof(Camera.ViewportToScreenPoint), new[] { typeof(Vector3) });
                if (viewportToScreen != null)
                {
                    harmony.Patch(
                        viewportToScreen,
                        prefix: new HarmonyMethod(typeof(RemixCameraViewportPatch), nameof(ViewportToScreenPointPrefix))
                    );
                }

                logger?.LogInfo("[RemixCameraViewportPatch] Successfully applied Camera viewport patches.");
            }
            catch (Exception ex)
            {
                logger?.LogWarning($"[RemixCameraViewportPatch] Failed to patch Camera viewport methods: {ex.Message}");
            }
        }

        private static bool ScreenToViewportPointPrefix(Camera __instance, Vector3 position, ref Vector3 __result)
        {
            if (RemixUIOverlay.TryGetOriginalCameraDimensions(__instance, out int origW, out int origH))
            {
                if (__instance.targetTexture != null && (origW != __instance.targetTexture.width || origH != __instance.targetTexture.height))
                {
                    __result = new Vector3(position.x / origW, position.y / origH, position.z);
                    if (screenToVpLogCount++ < 10)
                    {
                        logger?.LogInfo($"[RemixCameraViewportPatch] ScreenToViewportPoint adjusted for '{__instance.name}': in=({position.x:F1},{position.y:F1}), origRes=({origW}x{origH}), rtRes=({__instance.targetTexture.width}x{__instance.targetTexture.height}) -> out=({__result.x:F3},{__result.y:F3})");
                    }
                    return false;
                }
            }
            return true;
        }

        private static bool ViewportToScreenPointPrefix(Camera __instance, Vector3 position, ref Vector3 __result)
        {
            if (RemixUIOverlay.TryGetOriginalCameraDimensions(__instance, out int origW, out int origH))
            {
                if (__instance.targetTexture != null && (origW != __instance.targetTexture.width || origH != __instance.targetTexture.height))
                {
                    __result = new Vector3(position.x * origW, position.y * origH, position.z);
                    return false;
                }
            }
            return true;
        }
    }
}
