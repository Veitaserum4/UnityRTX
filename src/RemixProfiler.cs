using System;
using System.Diagnostics;
using BepInEx.Logging;

namespace UnityRemix
{
    /// <summary>
    /// Lightweight diagnostic profiler for tracking execution time across
    /// Unity main thread, Remix render thread, background overlay processing,
    /// and OpenRemix native telemetry (FramePerformanceEXT, FrameBreakdownEXT, TracingStatisticsEXT, TextureStatisticsEXT).
    /// Emits to Tracy Profiler (plots and zones), exposes a live snapshot for the F3 Debug HUD,
    /// and logs summary diagnostics to BepInEx log.
    /// </summary>
    public static class RemixProfiler
    {
        public struct ProfilerSnapshot
        {
            // Main thread timings (ms)
            public double MainTotalMs;
            public double EngineMs;
            public double LightsMs;
            public double StaticMeshesMs;
            public double ScannerMs;
            public double SkinnedMeshesMs;
            public double DynamicEffectsMs;
            public double UpdateOverlayMs;
            public double HudMs;
            public double EndOfFrameMs;

            // Render thread timings (ms)
            public double RenderTotalMs;
            public double MeshBatchMs;
            public double RenderGeomMs;
            public double ProcessLightsMs;
            public double PresentMs;

            // Overlay thread timings (ms)
            public double OverlayProcessMs;
            public double DrawScreenOverlayMs;

            // OpenRemix native telemetry
            public bool HasNativeTelemetry;
            public RemixAPI.remixapi_FramePerformanceEXT FramePerf;
            public RemixAPI.remixapi_FrameBreakdownEXT FrameBreakdown;
            public RemixAPI.remixapi_TracingStatisticsEXT TracingStats;
            public RemixAPI.remixapi_TextureStatisticsEXT TextureStats;
        }

        public static ManualLogSource Logger { get; set; }
        public static bool Enabled { get; set; } = true;
        public static int LogIntervalFrames { get; set; } = 180; // Every ~3 seconds at 60 FPS

        // Main thread timings (ms)
        private static double _curMainTotal;
        private static double _curEngine;
        private static double _curLights;
        private static double _curStaticMeshes;
        private static double _curScanner;
        private static double _curSkinnedMeshes;
        private static double _curDynamicEffects;
        private static double _curUpdateOverlay;
        private static double _curHUD;
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

        // Native telemetry
        private static volatile bool _hasNativeTelemetry;
        private static RemixAPI.remixapi_FramePerformanceEXT _curFramePerf;
        private static RemixAPI.remixapi_FrameBreakdownEXT _curFrameBreakdown;
        private static RemixAPI.remixapi_TracingStatisticsEXT _curTracingStats;
        private static RemixAPI.remixapi_TextureStatisticsEXT _curTextureStats;
        private static volatile bool _hasScenePreparation;
        private static RemixAPI.remixapi_ScenePreparationEXT _curScenePreparation;
        private static ulong _prevLogTexturesCreated;
        private static ulong _prevLogMaterialsCreated;
        private static ulong _prevLogGpuTexturesCreated;
        private static ulong _prevLogGpuMaterialsCreated;

        // Rolling stats
        private static int _statSampleCount;
        private static int _renderSampleCount;
        private static double _sumMainTotal, _maxMainTotal;
        private static double _sumEngine, _maxEngine;
        private static double _sumLights, _maxLights;
        private static double _sumStatic, _maxStatic;
        private static double _sumScanner, _maxScanner;
        private static double _sumSkinned, _maxSkinned;
        private static double _sumDynamic, _maxDynamic;
        private static double _sumOverlay, _maxOverlay;
        private static double _sumHUD, _maxHUD;
        private static double _sumEndOfFrame, _maxEndOfFrame;

        private static double _sumRenderTotal, _maxRenderTotal;
        private static double _sumMeshBatch, _maxMeshBatch;
        private static double _sumRenderGeom, _maxRenderGeom;
        private static double _sumProcessLights, _maxProcessLights;
        private static double _sumPresent, _maxPresent;
        private static double _sumOverlayProcess, _maxOverlayProcess;

        public static ProfilerSnapshot GetSnapshot()
        {
            return new ProfilerSnapshot
            {
                MainTotalMs = _curMainTotal,
                EngineMs = _curEngine,
                LightsMs = _curLights,
                StaticMeshesMs = _curStaticMeshes,
                ScannerMs = _curScanner,
                SkinnedMeshesMs = _curSkinnedMeshes,
                DynamicEffectsMs = _curDynamicEffects,
                UpdateOverlayMs = _curUpdateOverlay,
                HudMs = _curHUD,
                EndOfFrameMs = _curEndOfFrame,
                RenderTotalMs = _curRenderTotal,
                MeshBatchMs = _curMeshBatch,
                RenderGeomMs = _curRenderGeometry,
                ProcessLightsMs = _curProcessLights,
                PresentMs = _curPresent,
                OverlayProcessMs = _curOverlayProcess,
                DrawScreenOverlayMs = _curUpdateLayeredWindow,
                HasNativeTelemetry = _hasNativeTelemetry,
                FramePerf = _curFramePerf,
                FrameBreakdown = _curFrameBreakdown,
                TracingStats = _curTracingStats,
                TextureStats = _curTextureStats
            };
        }

        public static void RecordMainThread(
            double totalMs,
            double engineMs,
            double lightsMs,
            double staticMs,
            double scannerMs,
            double skinnedMs,
            double dynamicMs,
            double overlayMs,
            double hudMs = 0)
        {
            // Smooth current display values (EMA alpha = 0.2) so HUD numbers are readable
            const double alpha = 0.2;
            _curMainTotal = _curMainTotal == 0 ? totalMs : (_curMainTotal * (1.0 - alpha) + totalMs * alpha);
            _curEngine = _curEngine == 0 ? engineMs : (_curEngine * (1.0 - alpha) + engineMs * alpha);
            _curLights = _curLights * (1.0 - alpha) + lightsMs * alpha;
            _curStaticMeshes = _curStaticMeshes * (1.0 - alpha) + staticMs * alpha;
            _curScanner = _curScanner * (1.0 - alpha) + scannerMs * alpha;
            _curSkinnedMeshes = _curSkinnedMeshes * (1.0 - alpha) + skinnedMs * alpha;
            _curDynamicEffects = _curDynamicEffects * (1.0 - alpha) + dynamicMs * alpha;
            _curUpdateOverlay = _curUpdateOverlay * (1.0 - alpha) + overlayMs * alpha;
            _curHUD = _curHUD * (1.0 - alpha) + hudMs * alpha;

            RemixTracy.Plot("Main_Total_ms", totalMs);
            RemixTracy.Plot("Main_Engine_ms", engineMs);
            RemixTracy.Plot("Main_Lights_ms", lightsMs);
            RemixTracy.Plot("Main_Static_ms", staticMs);
            RemixTracy.Plot("Main_Scanner_ms", scannerMs);
            RemixTracy.Plot("Main_Skinned_ms", skinnedMs);
            RemixTracy.Plot("Main_Dynamic_ms", dynamicMs);
            RemixTracy.Plot("Main_Overlay_ms", overlayMs);
            RemixTracy.Plot("Main_HUD_ms", hudMs);

            _sumMainTotal += totalMs; if (totalMs > _maxMainTotal) _maxMainTotal = totalMs;
            _sumEngine += engineMs; if (engineMs > _maxEngine) _maxEngine = engineMs;
            _sumLights += lightsMs; if (lightsMs > _maxLights) _maxLights = lightsMs;
            _sumStatic += staticMs; if (staticMs > _maxStatic) _maxStatic = staticMs;
            _sumScanner += scannerMs; if (scannerMs > _maxScanner) _maxScanner = scannerMs;
            _sumSkinned += skinnedMs; if (skinnedMs > _maxSkinned) _maxSkinned = skinnedMs;
            _sumDynamic += dynamicMs; if (dynamicMs > _maxDynamic) _maxDynamic = dynamicMs;
            _sumOverlay += overlayMs; if (overlayMs > _maxOverlay) _maxOverlay = overlayMs;
            _sumHUD += hudMs; if (hudMs > _maxHUD) _maxHUD = hudMs;

            _statSampleCount++;
            if (_statSampleCount >= LogIntervalFrames)
            {
                FlushLog();
            }
        }

        public static void RecordEndOfFrame(double eofMs)
        {
            const double alpha = 0.2;
            _curEndOfFrame = _curEndOfFrame * (1.0 - alpha) + eofMs * alpha;
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
            const double alpha = 0.2;
            _curRenderTotal = _curRenderTotal == 0 ? totalMs : (_curRenderTotal * (1.0 - alpha) + totalMs * alpha);
            _curMeshBatch = _curMeshBatch * (1.0 - alpha) + meshBatchMs * alpha;
            _curRenderGeometry = _curRenderGeometry * (1.0 - alpha) + renderGeomMs * alpha;
            _curProcessLights = _curProcessLights * (1.0 - alpha) + processLightsMs * alpha;
            _curPresent = _curPresent * (1.0 - alpha) + presentMs * alpha;

            RemixTracy.Plot("Render_Total_ms", totalMs);
            RemixTracy.Plot("Render_Present_ms", presentMs);

            _renderSampleCount++;
            _sumRenderTotal += totalMs; if (totalMs > _maxRenderTotal) _maxRenderTotal = totalMs;
            _sumMeshBatch += meshBatchMs; if (meshBatchMs > _maxMeshBatch) _maxMeshBatch = meshBatchMs;
            _sumRenderGeom += renderGeomMs; if (renderGeomMs > _maxRenderGeom) _maxRenderGeom = renderGeomMs;
            _sumProcessLights += processLightsMs; if (processLightsMs > _maxProcessLights) _maxProcessLights = processLightsMs;
            _sumPresent += presentMs; if (presentMs > _maxPresent) _maxPresent = presentMs;
        }

        public static void RecordOverlayThread(double totalProcessMs, double updateLayeredWindowMs)
        {
            const double alpha = 0.2;
            _curOverlayProcess = _curOverlayProcess == 0 ? totalProcessMs : (_curOverlayProcess * (1.0 - alpha) + totalProcessMs * alpha);
            _curUpdateLayeredWindow = _curUpdateLayeredWindow * (1.0 - alpha) + updateLayeredWindowMs * alpha;

            RemixTracy.Plot("Overlay_Process_ms", totalProcessMs);
            RemixTracy.Plot("Overlay_UpdateLayeredWindow_ms", updateLayeredWindowMs);

            _sumOverlayProcess += totalProcessMs; if (totalProcessMs > _maxOverlayProcess) _maxOverlayProcess = totalProcessMs;
        }

        public static void RecordNativeTelemetry(
            in RemixAPI.remixapi_FramePerformanceEXT framePerf,
            in RemixAPI.remixapi_FrameBreakdownEXT frameBreakdown,
            in RemixAPI.remixapi_TracingStatisticsEXT tracingStats,
            in RemixAPI.remixapi_TextureStatisticsEXT textureStats)
        {
            _curFramePerf = framePerf;
            _curFrameBreakdown = frameBreakdown;
            _curTracingStats = tracingStats;
            _curTextureStats = textureStats;
            _hasNativeTelemetry = true;
        }

        // Sub-phases of Draw (render thread) and Dynamic (main thread), summed per log interval.
        public enum Detail
        {
            DrawRetained, DrawSceneApi, DrawScene, DrawGpuSkinned, DrawDynUpload, DrawDynDraw,
            ParticleDiscover, ParticleRead, ParticleBuild, ParticleEmit, Trails, DynamicOther,
            Count
        }
        private static readonly double[] _detailSum = new double[(int)Detail.Count];
        private static long _gpuSkinnedDraws, _dynUploads, _dynDraws, _particles;
        private static int _prevGcCount;
        private static readonly double TicksToMs = 1000.0 / Stopwatch.Frequency;

        public static double ElapsedMs(long startTimestamp)
        {
            return (Stopwatch.GetTimestamp() - startTimestamp) * TicksToMs;
        }

        public static void AddDetail(Detail detail, double ms)
        {
            _detailSum[(int)detail] += ms;
        }

        public static void AddDrawCounts(int gpuSkinnedDraws, int dynUploads, int dynDraws)
        {
            _gpuSkinnedDraws += gpuSkinnedDraws;
            _dynUploads += dynUploads;
            _dynDraws += dynDraws;
        }

        public static void AddParticles(int particles)
        {
            _particles += particles;
        }

        public static void RecordScenePreparation(in RemixAPI.remixapi_ScenePreparationEXT scenePreparation)
        {
            _curScenePreparation = scenePreparation;
            _hasScenePreparation = true;
        }

        private static string D(Detail detail, int samples)
        {
            return (_detailSum[(int)detail] / samples).ToString("F2");
        }

        private static void FlushLog()
        {
            if (_statSampleCount == 0 || Logger == null || !Enabled) return;

            int n = _statSampleCount;
            int rn = Math.Max(1, _renderSampleCount);
            double avgMain = _sumMainTotal / n;
            double avgEngine = _sumEngine / n;
            double avgFullFrame = avgMain + avgEngine;
            double unityFps = avgFullFrame > 0.001 ? (1000.0 / avgFullFrame) : 0;
            double avgRender = _sumRenderTotal / rn;
            double remixFps = avgRender > 0.001 ? (1000.0 / avgRender) : 0;
            int gcCount = GC.CollectionCount(0);

            string nativeMsg = "";
            if (_hasNativeTelemetry)
            {
                var fp = _curFramePerf;
                var fb = _curFrameBreakdown;
                var ts = _curTracingStats;
                var tx = _curTextureStats;

                ulong deltaTex = tx.texturesCreated >= _prevLogTexturesCreated ? (tx.texturesCreated - _prevLogTexturesCreated) : tx.texturesCreated;
                ulong deltaMat = tx.materialsCreated >= _prevLogMaterialsCreated ? (tx.materialsCreated - _prevLogMaterialsCreated) : tx.materialsCreated;
                ulong deltaGpuTex = tx.gpuTexturesCreated >= _prevLogGpuTexturesCreated ? (tx.gpuTexturesCreated - _prevLogGpuTexturesCreated) : tx.gpuTexturesCreated;
                ulong deltaGpuMat = tx.gpuMaterialsCreated >= _prevLogGpuMaterialsCreated ? (tx.gpuMaterialsCreated - _prevLogGpuMaterialsCreated) : tx.gpuMaterialsCreated;
                _prevLogTexturesCreated = tx.texturesCreated;
                _prevLogMaterialsCreated = tx.materialsCreated;
                _prevLogGpuTexturesCreated = tx.gpuTexturesCreated;
                _prevLogGpuMaterialsCreated = tx.gpuMaterialsCreated;

                nativeMsg =
                    $"\n  OpenRemix CPU: Present={fp.presentMilliseconds:F2}ms (Scene={fp.sceneMilliseconds:F2}ms, Record={fp.recordMilliseconds:F2}ms [Geom={fp.geometryMilliseconds:F2}ms, Upload={fp.uploadMilliseconds:F2}ms ({fp.uploadBytes / 1024}KB), Accel={fp.accelerationMilliseconds:F2}ms, UI={fb.uiMilliseconds:F2}ms], Fence={fp.fenceMilliseconds:F2}ms, Submit={fp.submitMilliseconds:F2}ms)" +
                    $"\n  OpenRemix Cache & GPU: GeomHits={fp.geometryCacheHits}/{fp.geometryCacheHits + fp.geometryConversions}, BufHits={fp.bufferCacheHits}/{fp.bufferCacheHits + fp.bufferAllocations}, TraceInst={fb.traceInstances}, TraceSurf={fb.traceSurfaces}, GPU(Build={ts.gpuBuildMilliseconds:F2}ms, Trace={ts.gpuTraceMilliseconds:F2}ms), BLAS(B={ts.blasBuilds}/R={ts.blasRefits}), DynTLAS(B={ts.dynamicTlasBuilds}/R={ts.dynamicTlasRefits}), Turnover(+{deltaTex} tex, +{deltaMat} mat, GPU +{deltaGpuTex} tex/+{deltaGpuMat} mat, Create={tx.gpuCreateMilliseconds:F1}ms)";

                if (_hasScenePreparation)
                {
                    var sp = _curScenePreparation;
                    nativeMsg +=
                        $"\n  OpenRemix Scene: Total={sp.totalMilliseconds:F2}ms [Frame={sp.frameMilliseconds:F2}ms (Sync={sp.staticSyncMilliseconds:F2}ms), Prune={sp.pruneMilliseconds:F2}ms, Stage={sp.stageMilliseconds:F2}ms, Gather={sp.gatherMilliseconds:F2}ms, Split={sp.splitMilliseconds:F2}ms, Tree={sp.treeMilliseconds:F2}ms, Visibility={sp.visibilityMilliseconds:F2}ms] ({sp.lights} lights, {sp.sphereLights} spheres) | " +
                        $"Static(active={sp.staticActive}, units={sp.staticUnits}, changes={sp.staticChanges}, rebuilt={sp.staticRebuiltUnits}, removed={sp.staticRemovedUnits}, resyncs={sp.staticResyncs}, tables={sp.staticTableMilliseconds:F2}ms, surf reused/rebuilt={sp.staticReusedSurfaces}/{sp.staticRebuiltSurfaces}) | FrameInst={sp.frameInstances}";
                }
            }

            Logger.LogInfo(
                $"[BridgeProfiler] Unity: {unityFps:F1} FPS (Total: {avgFullFrame:F1}ms [Engine: {avgEngine:F1}ms, Bridge: {avgMain:F2}ms (max {_maxMainTotal:F1}ms), EOF: {_sumEndOfFrame / n:F2}ms]) | " +
                $"RenderThread: {remixFps:F1} FPS (Total: {avgRender:F2}ms [Batch: {_sumMeshBatch / rn:F2}ms, Draw: {_sumRenderGeom / rn:F2}ms, Lights: {_sumProcessLights / rn:F2}ms, Present: {_sumPresent / rn:F2}ms])\n" +
                $"  Bridge Breakdown: Static: {_sumStatic / n:F2}ms (max {_maxStatic:F1}ms), " +
                $"Scanner: {_sumScanner / n:F2}ms (max {_maxScanner:F1}ms), " +
                $"Skinned: {_sumSkinned / n:F2}ms (max {_maxSkinned:F1}ms), " +
                $"Lights: {_sumLights / n:F2}ms (max {_maxLights:F1}ms), " +
                $"Dynamic: {_sumDynamic / n:F2}ms (max {_maxDynamic:F1}ms), " +
                $"Presenter: {_sumOverlay / n:F2}ms, HUD: {_sumHUD / n:F2}ms | AsyncOverlay: {_sumOverlayProcess / n:F2}ms (max {_maxOverlayProcess:F1}ms)" +
                $"\n  Draw Breakdown: Retained={D(Detail.DrawRetained, rn)}ms (API {D(Detail.DrawSceneApi, rn)}ms), Scene={D(Detail.DrawScene, rn)}ms, " +
                $"GpuSkinned={D(Detail.DrawGpuSkinned, rn)}ms ({_gpuSkinnedDraws / rn}), DynUpload={D(Detail.DrawDynUpload, rn)}ms ({_dynUploads / (double)rn:F1}), DynDraw={D(Detail.DrawDynDraw, rn)}ms ({_dynDraws / rn})" +
                $"\n  Dynamic Breakdown: Particles[Discover={D(Detail.ParticleDiscover, n)}ms, Read={D(Detail.ParticleRead, n)}ms, Build={D(Detail.ParticleBuild, n)}ms, Emit={D(Detail.ParticleEmit, n)}ms] ({_particles / n} particles), " +
                $"Trails={D(Detail.Trails, n)}ms, Other={D(Detail.DynamicOther, n)}ms | GCs={gcCount - _prevGcCount} over {n} frames, heap {GC.GetTotalMemory(false) / (1024 * 1024)}MB" +
                nativeMsg
            );

            _prevGcCount = gcCount;
            Array.Clear(_detailSum, 0, _detailSum.Length);
            _gpuSkinnedDraws = _dynUploads = _dynDraws = _particles = 0;
            _statSampleCount = 0;
            _renderSampleCount = 0;
            _sumMainTotal = _maxMainTotal = 0;
            _sumEngine = _maxEngine = 0;
            _sumLights = _maxLights = 0;
            _sumStatic = _maxStatic = 0;
            _sumScanner = _maxScanner = 0;
            _sumSkinned = _maxSkinned = 0;
            _sumDynamic = _maxDynamic = 0;
            _sumOverlay = _maxOverlay = 0;
            _sumHUD = _maxHUD = 0;
            _sumEndOfFrame = _maxEndOfFrame = 0;
            _sumRenderTotal = _maxRenderTotal = 0;
            _sumMeshBatch = _maxMeshBatch = 0;
            _sumRenderGeom = _maxRenderGeom = 0;
            _sumProcessLights = _maxProcessLights = 0;
            _sumPresent = _maxPresent = 0;
            _sumOverlayProcess = _maxOverlayProcess = 0;
        }
    }
}
