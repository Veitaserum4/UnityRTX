using System;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace UnityRemix
{
    /// <summary>
    /// Game-specific patch for ULTRAKILL's PostProcessV2_Handler.
    /// In SingleWindow mode, ULTRAKILL's custom retro post-processing system attempts
    /// to hijack HUD Camera into its internal downscaled buffer (mainTex).
    /// This patch prevents PostProcessV2_Handler from overriding HUD Camera when SingleWindow UI
    /// overlay is active, ensuring HUD and Menu canvases render directly into the transparent UI target.
    /// </summary>
    public static class UltrakillPostProcessPatch
    {
        private static bool applied = false;
        private static ManualLogSource logger;

        public static void Apply(Harmony harmony, ManualLogSource log)
        {
            if (applied) return;
            logger = log;

            try
            {
                var ppType = AccessTools.TypeByName("PostProcessV2_Handler");
                if (ppType == null)
                {
                    logger?.LogInfo("[UltrakillPostProcessPatch] PostProcessV2_Handler not found (not ULTRAKILL or running in different game). Skipping.");
                    return;
                }

                // Patch OnPreRenderCallback(Camera cam)
                var preRenderMethod = AccessTools.Method(ppType, "OnPreRenderCallback", new Type[] { typeof(Camera) });
                if (preRenderMethod != null)
                {
                    harmony.Patch(
                        preRenderMethod,
                        prefix: new HarmonyMethod(typeof(UltrakillPostProcessPatch), nameof(OnPreRenderCallbackPrefix))
                    );
                    logger?.LogInfo("[UltrakillPostProcessPatch] Patched PostProcessV2_Handler.OnPreRenderCallback");
                }
                else
                {
                    logger?.LogWarning("[UltrakillPostProcessPatch] Could not find PostProcessV2_Handler.OnPreRenderCallback");
                }

                // Patch SetupRTs()
                var setupRTsMethod = AccessTools.Method(ppType, "SetupRTs");
                if (setupRTsMethod != null)
                {
                    harmony.Patch(
                        setupRTsMethod,
                        postfix: new HarmonyMethod(typeof(UltrakillPostProcessPatch), nameof(SetupRTsPostfix))
                    );
                    logger?.LogInfo("[UltrakillPostProcessPatch] Patched PostProcessV2_Handler.SetupRTs");
                }
                else
                {
                    logger?.LogWarning("[UltrakillPostProcessPatch] Could not find PostProcessV2_Handler.SetupRTs");
                }

                applied = true;
                logger?.LogInfo("[UltrakillPostProcessPatch] Successfully applied ULTRAKILL post-processing protection patch.");
            }
            catch (Exception ex)
            {
                logger?.LogError($"[UltrakillPostProcessPatch] Failed to patch PostProcessV2_Handler: {ex}");
            }
        }

        private static bool OnPreRenderCallbackPrefix(object __instance, Camera cam)
        {
            if (!RemixFramebufferPresenter.IsSingleWindowUIActive)
                return true;

            if (cam == null) return true;

            // If this camera is a managed UI camera (e.g. HUD Camera), do not let PostProcessV2_Handler
            // override its target buffer to mainTex
            if (RemixUIOverlay.IsManagedUICamera(cam) || cam.name.Equals("HUD Camera", StringComparison.OrdinalIgnoreCase))
            {
                return false; // Skip PostProcessV2_Handler logic for this camera
            }

            return true;
        }

        private static void SetupRTsPostfix(object __instance)
        {
            if (!RemixFramebufferPresenter.IsSingleWindowUIActive)
                return;

            // SetupRTs resets hudCam's targetTexture to null and SetTargetBuffers to mainTex.
            // Restore managed UI cameras to uiRenderTexture immediately
            RemixUIOverlay.Instance?.RebindAllUICameras();
        }
    }
}
