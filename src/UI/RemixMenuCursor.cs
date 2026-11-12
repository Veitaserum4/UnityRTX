using System;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityRemix.Games;

namespace UnityRemix
{
    /// <summary>
    /// Frees the cursor while the RTX Remix menu (ImGui) is open and restores it on close.
    /// The game's profile can lock the game's own controls and decide the cursor lock on close.
    /// </summary>
    public static class RemixMenuCursor
    {
        private static CursorLockMode previousLockMode = CursorLockMode.Locked;
        private static bool previousCursorVisible = false;

        [DllImport("user32.dll")]
        private static extern bool ClipCursor(IntPtr lpRect);

        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        public static void SetRemixMenuState(bool open)
        {
            if (open)
            {
                previousLockMode = Cursor.lockState;
                previousCursorVisible = Cursor.visible;

                ClipCursor(IntPtr.Zero);
                ReleaseCapture();
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
                RemixWindowManager.ResetUnityInputAxes();
                GameProfiles.OnRemixMenu(true);
                return;
            }

            bool? lockCursor = GameProfiles.OnRemixMenu(false);
            if (lockCursor.HasValue)
            {
                Cursor.lockState = lockCursor.Value ? CursorLockMode.Locked : CursorLockMode.None;
                Cursor.visible = !lockCursor.Value;
            }
            else
            {
                Cursor.lockState = previousLockMode;
                Cursor.visible = previousCursorVisible;
            }
            if (Cursor.lockState != CursorLockMode.Locked)
            {
                ClipCursor(IntPtr.Zero);
            }
            ReleaseCapture();
            RemixWindowManager.ResetUnityInputAxes();
        }
    }
}
