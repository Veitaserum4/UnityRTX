using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx.Configuration;
using BepInEx.Logging;

namespace UnityRemix.Games
{
    /// <summary>
    /// A game's own defaults, embedded from Games/&lt;folder&gt;/Defaults (GameProfile.DefaultsFolder):
    /// UnityRTX.cfg, the BepInEx entries that differ from UnityRTX's defaults for that game, and
    /// OpenRemix.conf, an OpenRemix profile for its first run. Saved settings always win: these
    /// fill in only what the user has not saved.
    /// </summary>
    public static class GameDefaults
    {
        public const string ConfigFile = "UnityRTX.cfg", OpenRemixFile = "OpenRemix.conf";

        private static string Read(string folder, string file)
        {
            if (string.IsNullOrEmpty(folder))
                return null;
            var assembly = typeof(GameDefaults).Assembly;
            string suffix = ("/" + folder + "/Defaults/" + file).ToLowerInvariant();
            foreach (var name in assembly.GetManifestResourceNames())
            {
                if (!name.Replace('\\', '/').ToLowerInvariant().EndsWith(suffix))
                    continue;
                using (var stream = assembly.GetManifestResourceStream(name))
                using (var reader = new StreamReader(stream))
                    return reader.ReadToEnd();
            }
            return null;
        }

        /// <summary>
        /// Main thread, before the first Config.Bind: the game's UnityRTX.cfg entries become the
        /// starting values of the keys the user's config file lacks. BepInEx holds a file's
        /// entries that are not bound yet as orphans, and Bind starts from an orphan's value, so
        /// a game default added there reads exactly like a saved one.
        /// </summary>
        public static void ApplyConfigDefaults(BepInEx.Configuration.ConfigFile config, ManualLogSource log)
        {
            var profile = GameProfiles.ForExecutable();
            string text = Read(profile?.DefaultsFolder, ConfigFile);
            if (text == null)
                return;
            var orphans = typeof(BepInEx.Configuration.ConfigFile)
                .GetProperty("OrphanedEntries", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                ?.GetValue(config, null) as Dictionary<ConfigDefinition, string>;
            if (orphans == null)
            {
                log.LogWarning("[Games] This BepInEx has no ConfigFile.OrphanedEntries: game defaults not applied");
                return;
            }
            int applied = 0, kept = 0;
            string section = null;
            foreach (var raw in text.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#')
                    continue;
                if (line[0] == '[' && line[line.Length - 1] == ']')
                {
                    section = line.Substring(1, line.Length - 2);
                    continue;
                }
                int equal = line.IndexOf('=');
                if (section == null || equal <= 0)
                    continue;
                var definition = new ConfigDefinition(section, line.Substring(0, equal).Trim());
                if (orphans.ContainsKey(definition))
                {
                    kept++;
                    continue;
                }
                orphans[definition] = line.Substring(equal + 1).Trim();
                applied++;
            }
            log.LogInfo($"[Games] {profile.Name} defaults: {applied} setting(s) applied, {kept} already saved");
        }

        /// <summary>
        /// The game's OpenRemix profile as settings, for a first run (no saved profile); null
        /// when the game has none.
        /// </summary>
        public static List<(string key, string value)> OpenRemixSettings()
        {
            var profile = GameProfiles.Active ?? GameProfiles.ForExecutable();
            string text = Read(profile?.DefaultsFolder, OpenRemixFile);
            if (text == null)
                return null;
            var settings = new List<(string, string)>();
            foreach (var raw in text.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#')
                    continue;
                int equal = line.IndexOf(" = ", StringComparison.Ordinal);
                if (equal <= 0)
                    continue;
                string key = line.Substring(0, equal);
                if (key == "profile.version")
                    continue;
                settings.Add((key, line.Substring(equal + 3)));
            }
            return settings;
        }
    }

    /// <summary>A game with defaults only (no patches): its name, executables and defaults folder.</summary>
    internal sealed class DefaultsOnlyProfile : GameProfile
    {
        private readonly string name, folder;
        private readonly string[] executables;

        public DefaultsOnlyProfile(string name, string folder, params string[] executables)
        {
            this.name = name;
            this.folder = folder;
            this.executables = executables;
        }

        public override string Name => name;
        public override string[] ExecutableNames => executables;
        public override string DefaultsFolder => folder;
    }
}
