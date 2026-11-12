namespace UnityRemix
{
    // The plugin's NativeLogForwarder needs BepInEx; the checks don't load a runtime.
    internal static class NativeLogForwarder
    {
        public static void Install() { }
    }
}
