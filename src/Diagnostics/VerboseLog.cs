namespace UnityRemix
{
    /// <summary>
    /// Debug.VerboseLogging: repeated diagnostics (per-scan, per-capture and periodic state lines,
    /// per-object first-seen descriptions) go to the log only with it on. Warnings, errors and
    /// one-off events are logged regardless.
    /// </summary>
    internal static class VerboseLog
    {
        public static BepInEx.Configuration.ConfigEntry<bool> Enabled;

        public static bool On => Enabled != null && Enabled.Value;
    }
}
