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
        private static double _curEngine;
        private static double _curLights;
        private static double _curStaticMeshes;
        private static double _curScanner;
        private static double _curSkinnedMeshes;
        private static double _curDynamicEffects;
        private static double _curUpdateOverlay;
        private static double _curEndOfFrame;

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
        private static double _sumEngine, _maxEngine;
        private static double _sumLights, _maxLights;
        private static double _sumStatic, _maxStatic;
        private static double _sumScanner, _maxScanner;
        private static double _sumSkinned, _maxSkinned;
        private static double _sumDynamic, _maxDynamic;
        private static double _sumOverlay, _maxOverlay;
        private static double _sumEndOfFrame, _maxEndOfFrame;

        private static double _sumRenderTotal, _maxRenderTotal;
        private static double _sumPresent, _maxPresent;
        private static double _sumOverlayProcess, _maxOverlayProcess;

        public static void RecordMainThread(
            double totalMs,
            double engineMs,
            double lightsMs,
            double staticMs,
            double scannerMs,
            double skinnedMs,
            double dynamicMs,
            double overlayMs)
        {
            _curMainTotal = totalMs;
            _curEngine = engineMs;
            _curLights = lightsMs;
            _curStaticMeshes = staticMs;
            _curScanner = scannerMs;
            _curSkinnedMeshes = skinnedMs;
            _curDynamicEffects = dynamicMs;
            _curUpdateOverlay = overlayMs;

            RemixTracy.Plot("Main_Total_ms", totalMs);
            RemixTracy.Plot("Main_Engine_ms", engineMs);
            RemixTracy.Plot("Main_Lights_ms", lightsMs);
            RemixTracy.Plot("Main_Static_ms", staticMs);
            RemixTracy.Plot("Main_Scanner_ms", scannerMs);
            RemixTracy.Plot("Main_Skinned_ms", skinnedMs);
            RemixTracy.Plot("Main_Dynamic_ms", dynamicMs);
            RemixTracy.Plot("Main_Overlay_ms", overlayMs);

            _sumMainTotal += totalMs; if (totalMs > _maxMainTotal) _maxMainTotal = totalMs;
            _sumEngine += engineMs; if (engineMs > _maxEngine) _maxEngine = engineMs;
            _sumLights += lightsMs; if (lightsMs > _maxLights) _maxLights = lightsMs;
            _sumStatic += staticMs; if (staticMs > _maxStatic) _maxStatic = staticMs;
            _sumScanner += scannerMs; if (scannerMs > _maxScanner) _maxScanner = scannerMs;
            _sumSkinned += skinnedMs; if (skinnedMs > _maxSkinned) _maxSkinned = skinnedMs;
            _sumDynamic += dynamicMs; if (dynamicMs > _maxDynamic) _maxDynamic = dynamicMs;
            _sumOverlay += overlayMs; if (overlayMs > _maxOverlay) _maxOverlay = overlayMs;

            _statSampleCount++;
            if (_statSampleCount >= LogIntervalFrames)
            {
                FlushLog();
            }
        }

        public static void RecordEndOfFrame(double eofMs)
        {
            _curEndOfFrame = eofMs;
            RemixTracy.Plot("Main_EndOfFrame_ms", eofMs);
            _sumEndOfFrame += eofMs;
            if (eofMs > _maxEndOfFrame) _maxEndOfFrame = eofMs;
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
            double avgMain = _sumMainTotal / n;
            double avgEngine = _sumEngine / n;
            double avgFullFrame = avgMain + avgEngine;
            double unityFps = avgFullFrame > 0.001 ? (1000.0 / avgFullFrame) : 0;
            double avgRender = _sumRenderTotal / Math.Max(1, n);
            double remixFps = avgRender > 0.001 ? (1000.0 / avgRender) : 0;

            Logger.LogInfo(
                $"[BridgeProfiler] Unity: {unityFps:F1} FPS (Total: {avgFullFrame:F1}ms [Engine: {avgEngine:F1}ms, Bridge: {avgMain:F1}ms, EOF: {_sumEndOfFrame/n:F2}ms]) | " +
                $"Remix: {remixFps:F1} FPS (Render: {avgRender:F1}ms, Present: {_sumPresent/n:F2}ms)\n" +
                $"  Bridge Breakdown: Static: {_sumStatic/n:F2}ms (max {_maxStatic:F1}ms), " +
                $"Scanner: {_sumScanner/n:F2}ms (max {_maxScanner:F1}ms), " +
                $"Skinned: {_sumSkinned/n:F2}ms (max {_maxSkinned:F1}ms), " +
                $"Lights: {_sumLights/n:F2}ms (max {_maxLights:F1}ms), " +
                $"Dynamic: {_sumDynamic/n:F2}ms (max {_maxDynamic:F1}ms), " +
                $"Presenter: {_sumOverlay/n:F2}ms | AsyncOverlay: {_sumOverlayProcess/n:F2}ms"
            );

            _statSampleCount = 0;
            _sumMainTotal = _maxMainTotal = 0;
            _sumEngine = _maxEngine = 0;
            _sumLights = _maxLights = 0;
            _sumStatic = _maxStatic = 0;
            _sumScanner = _maxScanner = 0;
            _sumSkinned = _maxSkinned = 0;
            _sumDynamic = _maxDynamic = 0;
            _sumOverlay = _maxOverlay = 0;
            _sumEndOfFrame = _maxEndOfFrame = 0;
            _sumRenderTotal = _maxRenderTotal = 0;
            _sumPresent = _maxPresent = 0;
            _sumOverlayProcess = _maxOverlayProcess = 0;
        }
    }
}
