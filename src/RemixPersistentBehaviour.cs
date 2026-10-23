using BepInEx.Logging;
using UnityEngine;

namespace UnityRemix
{
    /// <summary>
    /// Persistent behaviour that survives scene changes and handles LateUpdate for skinned mesh capture
    /// </summary>
    public class RemixPersistentBehaviour : MonoBehaviour
    {
        private UnityRemixPlugin plugin;
        private static ManualLogSource LogSource => UnityRemixPlugin.LogSource;
        
        public void Initialize(UnityRemixPlugin sourcePlugin)
        {
            plugin = sourcePlugin;
            LogSource.LogInfo("RemixPersistentBehaviour initialized");
            StartCoroutine(EndOfFrameLoop());
        }

        private System.Collections.IEnumerator EndOfFrameLoop()
        {
            var wait = new WaitForEndOfFrame();
            while (true)
            {
                yield return wait;
                if ((object)plugin != null)
                {
                    plugin.OnEndOfFrame();
                }
            }
        }
        
        void Update()
        {
            if ((object)plugin != null)
            {
                plugin.OnUpdateFromPersistent();
            }
        }

        void LateUpdate()
        {
            // Frame capture runs in Application.onBeforeRender (registered by the plugin, whose
            // C# object outlives its GameObject), with an end-of-frame fallback (OnEndOfFrame).
            // Cast to object to avoid Unity's lifetime check (overloaded == operator)
            if ((object)plugin == null && Time.frameCount % 300 == 0)
                LogSource.LogWarning("RemixPersistentBehaviour: Plugin reference is null!");
        }
        
        void OnApplicationQuit()
        {
            UnityRemixPlugin.SetQuitting();
        }
    }
}
