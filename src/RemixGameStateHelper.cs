using System;
using System.Runtime.InteropServices;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace UnityRemix
{
    /// <summary>
    /// Universal cursor state manager when the RTX Remix menu (ImGui) opens and closes.
    /// Handles unlocking the mouse cursor when Remix UI is opened and restoring the game's
    /// previous cursor lock and visibility state when closed.
    /// </summary>
    public static class RemixGameStateHelper
    {
        private static CursorLockMode previousLockMode = CursorLockMode.None;
        private static bool previousCursorVisible = true;
        private static bool isMenuOpen = false;

        [DllImport("user32.dll")]
        private static extern bool ClipCursor(IntPtr lpRect);

        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        public static void Apply(Harmony harmony, ManualLogSource logger)
        {
            // Universal implementation does not require game-specific method patching
        }

        public static void SetRemixMenuState(bool open, ManualLogSource logger = null)
        {
            if (open)
            {
                if (!isMenuOpen)
                {
                    previousLockMode = Cursor.lockState;
                    previousCursorVisible = Cursor.visible;
                    isMenuOpen = true;
                }

                ClipCursor(IntPtr.Zero);
                ReleaseCapture();
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;

                logger?.LogDebug("[RemixGameStateHelper] Remix menu opened: cursor unlocked and visible.");
            }
            else
            {
                if (isMenuOpen)
                {
                    Cursor.lockState = previousLockMode;
                    Cursor.visible = previousCursorVisible;
                    isMenuOpen = false;

                    if (previousLockMode != CursorLockMode.Locked)
                    {
                        ClipCursor(IntPtr.Zero);
                    }
                    ReleaseCapture();

                    logger?.LogDebug($"[RemixGameStateHelper] Remix menu closed: restored lockState={previousLockMode}, visible={previousCursorVisible}.");
                }
            }
        }
    }
}
