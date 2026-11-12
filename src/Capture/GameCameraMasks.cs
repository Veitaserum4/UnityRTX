using System.Collections.Generic;
using UnityEngine;

namespace UnityRemix
{
    /// <summary>
    /// Each camera's culling mask as the game set it. Whatever changes a game camera's mask
    /// (Single Window suppression, UI routing) calls Remember first; the layers the game
    /// draws (DrawnLayers) come from these, not from the masks UnityRTX left behind.
    /// Main thread only.
    /// </summary>
    public static class GameCameraMasks
    {
        private static readonly Dictionary<Camera, int> original = new Dictionary<Camera, int>();
        private static readonly List<Camera> destroyed = new List<Camera>();

        public static void Remember(Camera cam)
        {
            if (cam != null && !original.ContainsKey(cam))
                original[cam] = cam.cullingMask;
        }

        public static int Get(Camera cam) => original.TryGetValue(cam, out int mask) ? mask : cam.cullingMask;

        // Union over the enabled game cameras (UnityRTX's own cameras excluded).
        public static int DrawnLayers()
        {
            destroyed.Clear();
            foreach (var cam in original.Keys)
                if (cam == null) destroyed.Add(cam);
            foreach (var cam in destroyed)
                original.Remove(cam);

            int mask = 0;
            foreach (var cam in Camera.allCameras)
            {
                if (cam == null || IsOwnCamera(cam)) continue;
                mask |= Get(cam);
            }
            return mask;
        }

        private static bool IsOwnCamera(Camera cam)
        {
            string name = cam.name;
            return name.StartsWith("UnityRemix_") || name == "RemixSkyboxCaptureCamera";
        }
    }
}
