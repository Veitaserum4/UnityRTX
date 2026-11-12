using System.IO;
using UnityEngine;

namespace UnityRemix
{
    /// <summary>
    /// Where UnityRTX's files are in a game. Everything of UnityRTX's lives in its plugin folder
    /// (BepInEx/plugins/UnityRTX), so the game folder keeps only BepInEx's loader:
    ///
    ///   BepInEx/plugins/UnityRTX/
    ///     UnityRemix.dll             this plugin
    ///     UnityRTXHostPass.dll       shader pass-through's D3D11 capture (native/hostpass11)
    ///     openremix/                 OpenRemix's runtime, as its scripts/windows/package-runtime.py
    ///                                bundles it: remix.dll, SDL3, USD, oneTBB, dlss/, ...
    ///
    /// OpenRemix keeps its user data (openremix/ profiles and caches, rtx-remix/ captures and
    /// mods) in the game folder, its working directory. A remix.dll in the game folder itself
    /// (the layout before this one) is still found when the plugin folder has none.
    /// </summary>
    public static class InstallLayout
    {
        /// <summary>The folder this plugin was loaded from.</summary>
        public static string PluginDirectory => Path.GetDirectoryName(typeof(InstallLayout).Assembly.Location);

        /// <summary>The game's folder (beside its executable).</summary>
        public static string GameDirectory => Path.GetDirectoryName(Application.dataPath);

        /// <summary>OpenRemix's runtime folder.</summary>
        public static string RuntimeDirectory => Path.Combine(PluginDirectory, "openremix");

        /// <summary>The remix.dll to load: the runtime folder's, else the game folder's; null if neither.</summary>
        public static string RemixDll
        {
            get
            {
                string runtime = Path.Combine(RuntimeDirectory, "remix.dll");
                if (File.Exists(runtime))
                    return runtime;
                string legacy = Path.Combine(GameDirectory, "remix.dll");
                return File.Exists(legacy) ? legacy : null;
            }
        }

        /// <summary>Whether RemixDll is the game folder's (the old layout).</summary>
        public static bool IsLegacyRuntime => RemixDll != null && !File.Exists(Path.Combine(RuntimeDirectory, "remix.dll"));

        /// <summary>A native library of UnityRTX's own: the plugin folder's, else the game folder's.</summary>
        public static string NativeLibrary(string fileName)
        {
            string own = Path.Combine(PluginDirectory, fileName);
            if (File.Exists(own))
                return own;
            string legacy = Path.Combine(GameDirectory, fileName);
            return File.Exists(legacy) ? legacy : null;
        }
    }
}
