using System;
using System.Diagnostics;
using BepInEx.Logging;

namespace UnityRemix
{
    /// <summary>
    /// Lightweight diagnostic profiler for tracking execution time across
    /// Unity main thread, Remix render thread, and background overlay processing.
    /// Emits to Tracy Profiler (plots and zones) and logs summary diagnostics to BepInEx log.
    /// </summary>
    public static class RemixProfiler
    {
        public static ManualLogSource Logger { get; set; }
        public static bool Enabled { get; set; } = true;
        public static int LogIntervalFrames { get; set; } = 180; // Every ~2 seconds at 90 FPS

        // Main thread timings (ms)
        private static double _curMainTotal;
        private static double _curLights;
        private static double _curStaticMeshes;
        private static double _curSkinnedMeshes;
        private static double _curDynamicEffects;
        private static double _curUpdateOverlay;

        // Render thread timings (ms)
        private static double _curRenderTotal;
        private static double _curMeshBatch;
        private static double _curRenderGeometry;
        private static double _curProcessLights;
        private static double _curPresent;

        // Overlay thread timings (ms)
        private static double _curOverlayProcess;
        private static double _curUpdateLayeredWindow;

        // Rolling stats
        private static int _statSampleCount;
        private static double _sumMainTotal, _maxMainTotal;
        private static double _sumLights, _maxLights;
        private static double _sumStatic, _maxStatic;
        private static double _sumSkinned, _maxSkinned;
        private static double _sumDynamic, _maxDynamic;
        private static double _sumOverlay, _maxOverlay;

        private static double _sumRenderTotal, _maxRenderTotal;
        private static double _sumPresent, _maxPresent;
        private static double _sumOverlayProcess, _maxOverlayProcess;

        public static void RecordMainThread(
            double totalMs,
            double lightsMs,
            double staticMs,
            double skinnedMs,
            double dynamicMs,
            double overlayMs)
        {
            _curMainTotal = totalMs;
            _curLights = lightsMs;
            _curStaticMeshes = staticMs;
            _curSkinnedMeshes = skinnedMs;
            _curDynamicEffects = dynamicMs;
            _curUpdateOverlay = overlayMs;

            RemixTracy.Plot("Main_Total_ms", totalMs);
            RemixTracy.Plot("Main_Lights_ms", lightsMs);
            RemixTracy.Plot("Main_Static_ms", staticMs);
            RemixTracy.Plot("Main_Skinned_ms", skinnedMs);
            RemixTracy.Plot("Main_Dynamic_ms", dynamicMs);
            RemixTracy.Plot("Main_Overlay_ms", overlayMs);

            _sumMainTotal += totalMs; if (totalMs > _maxMainTotal) _maxMainTotal = totalMs;
            _sumLights += lightsMs; if (lightsMs > _maxLights) _maxLights = lightsMs;
            _sumStatic += staticMs; if (staticMs > _maxStatic) _maxStatic = staticMs;
            _sumSkinned += skinnedMs; if (skinnedMs > _maxSkinned) _maxSkinned = skinnedMs;
            _sumDynamic += dynamicMs; if (dynamicMs > _maxDynamic) _maxDynamic = dynamicMs;
            _sumOverlay += overlayMs; if (overlayMs > _maxOverlay) _maxOverlay = overlayMs;

            _statSampleCount++;
            if (_statSampleCount >= LogIntervalFrames)
            {
                FlushLog();
            }
        }

        public static void RecordRenderThread(
            double totalMs,
            double meshBatchMs,
            double renderGeomMs,
            double processLightsMs,
            double presentMs)
        {
            _curRenderTotal = totalMs;
            _curMeshBatch = meshBatchMs;
            _curRenderGeometry = renderGeomMs;
            _curProcessLights = processLightsMs;
            _curPresent = presentMs;

            RemixTracy.Plot("Render_Total_ms", totalMs);
            RemixTracy.Plot("Render_Present_ms", presentMs);

            _sumRenderTotal += totalMs; if (totalMs > _maxRenderTotal) _maxRenderTotal = totalMs;
            _sumPresent += presentMs; if (presentMs > _maxPresent) _maxPresent = presentMs;
        }

        public static void RecordOverlayThread(double totalProcessMs, double updateLayeredWindowMs)
        {
            _curOverlayProcess = totalProcessMs;
            _curUpdateLayeredWindow = updateLayeredWindowMs;

            RemixTracy.Plot("Overlay_Process_ms", totalProcessMs);
            RemixTracy.Plot("Overlay_UpdateLayeredWindow_ms", updateLayeredWindowMs);

            _sumOverlayProcess += totalProcessMs; if (totalProcessMs > _maxOverlayProcess) _maxOverlayProcess = totalProcessMs;
        }

        private static void FlushLog()
        {
            if (_statSampleCount == 0 || Logger == null || !Enabled) return;

            int n = _statSampleCount;
            Logger.LogInfo(
                $"[BridgeProfiler] Main avg: {_sumMainTotal/n:F2}ms (max {_maxMainTotal:F1}ms) | " +
                $"Lights: {_sumLights/n:F2}ms (max {_maxLights:F1}ms), " +
                $"Static: {_sumStatic/n:F2}ms (max {_maxStatic:F1}ms), " +
                $"Skinned: {_sumSkinned/n:F2}ms (max {_maxSkinned:F1}ms), " +
                $"Dynamic: {_sumDynamic/n:F2}ms (max {_maxDynamic:F1}ms), " +
                $"UIOverlay: {_sumOverlay/n:F2}ms (max {_maxOverlay:F1}ms) | " +
                $"Render avg: {_sumRenderTotal/n:F2}ms (max {_maxRenderTotal:F1}ms, Present: {_sumPresent/n:F2}ms) | " +
                $"AsyncOverlay: {_sumOverlayProcess/n:F2}ms (max {_maxOverlayProcess:F1}ms)"
            );

            _statSampleCount = 0;
            _sumMainTotal = _maxMainTotal = 0;
            _sumLights = _maxLights = 0;
            _sumStatic = _maxStatic = 0;
            _sumSkinned = _maxSkinned = 0;
            _sumDynamic = _maxDynamic = 0;
            _sumOverlay = _maxOverlay = 0;
            _sumRenderTotal = _maxRenderTotal = 0;
            _sumPresent = _maxPresent = 0;
            _sumOverlayProcess = _maxOverlayProcess = 0;
        }
    }
}
