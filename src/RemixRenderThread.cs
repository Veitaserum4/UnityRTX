using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using BepInEx.Configuration;
using BepInEx.Logging;

namespace UnityRemix
{
    /// <summary>
    /// Manages the background render thread for Remix
    /// </summary>
    public class RemixRenderThread
    {
        private readonly ManualLogSource logger;
        private readonly RemixWindowManager windowManager;
        private readonly RemixCameraHandler cameraHandler;
        private readonly RemixMeshConverter meshConverter;
        private readonly RemixLightConverter lightConverter;
        private readonly RemixFrameCapture frameCapture;
        
        private readonly ConfigEntry<int> configTargetFPS;
        private readonly ConfigEntry<int> configDebugLogInterval;
        private readonly ConfigEntry<bool> configEnableLights;
        private readonly ConfigEntry<bool> configUseGameGeometry;
        
        // Cached delegates
        private RemixAPI.PFN_remixapi_Present presentFunc;
        private RemixAPI.PFN_remixapi_Shutdown shutdownFunc;
        
        // Thread state
        private Thread renderThread;
        private volatile bool renderThreadRunning = false;
        private volatile bool deviceReady = false;
        
        /// <summary>
        /// True once the render thread has created the window and called Remix Startup successfully.
        /// The main thread must not call any Remix API until this is true.
        /// </summary>
        public bool DeviceReady => deviceReady;
        
        // Test objects
        private IntPtr testMeshHandle = IntPtr.Zero;
        private IntPtr testLightHandle = IntPtr.Zero;
        
        // Frame state
        private volatile RemixFrameCapture.FrameState currentFrameState = new RemixFrameCapture.FrameState();
        private readonly object captureLock = new object();
        private readonly AutoResetEvent frameReadyEvent = new AutoResetEvent(false);
        
        // Scene mesh scanner (optional)
        private SceneMeshScanner sceneMeshScanner;
        private RemixSkyboxManager skyboxManager;
        private RemixTerrainManager terrainManager;
        private readonly HashSet<ulong> updatedMeshes = new HashSet<ulong>();
        // Dynamic mesh uploads (RenderSkinnedMeshes), summed between periodic log lines.
        private int uploadLogFrames;
        // Per RemixFrameCapture.DynamicSource: uploads, vertices and milliseconds since the last log.
        private readonly long[] uploadsBySource = new long[(int)RemixFrameCapture.DynamicSource.Count];
        private readonly long[] uploadVertsBySource = new long[(int)RemixFrameCapture.DynamicSource.Count];
        private readonly double[] uploadMsBySource = new double[(int)RemixFrameCapture.DynamicSource.Count];
        private int largestUploadVerts;
        private ulong largestUploadHash;
        private RemixFrameCapture.DynamicSource largestUploadSource;
        private readonly HashSet<int> claimedRendererIds = new HashSet<int>();
        private readonly HashSet<StaticGeometryKey> claimedStaticKeys = new HashSet<StaticGeometryKey>();
        
        // Retained Scene Instances (OpenRemix native extension)
        private struct RetainedInstanceRecord
        {
            public ulong NativeHandle;
            public IntPtr MeshHandle;
            public RemixAPI.remixapi_Transform Transform;
            public bool Visible;
            public uint LastSeenFrame;
        }

        private readonly Dictionary<ulong, RetainedInstanceRecord> retainedSceneInstances = new Dictionary<ulong, RetainedInstanceRecord>(4096);
        private readonly HashSet<ulong> visibleRetainedKeysThisFrame = new HashSet<ulong>(4096);
        private readonly List<RemixAPI.remixapi_InstanceInfo> pendingAddInfos = new List<RemixAPI.remixapi_InstanceInfo>(512);
        private readonly List<ulong> pendingAddKeys = new List<ulong>(512);
        private readonly List<RemixAPI.remixapi_SceneInstanceChangeEXT> pendingChanges = new List<RemixAPI.remixapi_SceneInstanceChangeEXT>(512);
        private readonly List<ulong> reusableHiddenKeys = new List<ulong>(256);
        private readonly List<ulong> reusableRemoveKeys = new List<ulong>(256);
        private RemixAPI.remixapi_InstanceInfo[] addInfosBuffer = new RemixAPI.remixapi_InstanceInfo[512];
        private ulong[] addHandlesBuffer = new ulong[512];
        private ulong[] removeHandlesBuffer = new ulong[256];
        private RemixAPI.remixapi_SceneInstanceChangeEXT[] changesBuffer = new RemixAPI.remixapi_SceneInstanceChangeEXT[512];
        private int needClearRetained = 0;

        public void RequestClearRetainedSceneInstances()
        {
            Interlocked.Exchange(ref needClearRetained, 1);
        }

        private static bool TransformEquals(in RemixAPI.remixapi_Transform a, in RemixAPI.remixapi_Transform b)
        {
            unsafe
            {
                fixed (RemixAPI.remixapi_Transform* pa = &a)
                fixed (RemixAPI.remixapi_Transform* pb = &b)
                {
                    float* fa = (float*)pa;
                    float* fb = (float*)pb;
                    for (int i = 0; i < 12; i++)
                    {
                        if (fa[i] != fb[i]) return false;
                    }
                    return true;
                }
            }
        }

        private void EnsureAddBuffersCapacity(int count)
        {
            if (addInfosBuffer.Length < count)
            {
                int newCap = Math.Max(count, addInfosBuffer.Length * 2);
                addInfosBuffer = new RemixAPI.remixapi_InstanceInfo[newCap];
                addHandlesBuffer = new ulong[newCap];
            }
        }

        private void EnsureChangesBufferCapacity(int count)
        {
            if (changesBuffer.Length < count)
            {
                int newCap = Math.Max(count, changesBuffer.Length * 2);
                changesBuffer = new RemixAPI.remixapi_SceneInstanceChangeEXT[newCap];
            }
        }

        public void ClearRetainedSceneInstances()
        {
            if (!RemixAPI.HasRetainedSceneInstancesEXT || retainedSceneInstances.Count == 0)
            {
                retainedSceneInstances.Clear();
                return;
            }

            try
            {
                ulong[] handles = new ulong[retainedSceneInstances.Count];
                int idx = 0;
                foreach (var kvp in retainedSceneInstances)
                {
                    handles[idx++] = kvp.Value.NativeHandle;
                }

                unsafe
                {
                    fixed (ulong* pHandles = handles)
                    {
                        RemixAPI.RemoveSceneInstancesEXTFunc((IntPtr)pHandles, (uint)handles.Length);
                    }
                }
                logger.LogInfo($"[RenderThread] Cleared {handles.Length} retained scene instances.");
            }
            catch (Exception ex)
            {
                logger.LogWarning($"Failed to clear retained scene instances: {ex.Message}");
            }
            finally
            {
                retainedSceneInstances.Clear();
            }
        }

        public void SetSkyboxManager(RemixSkyboxManager manager)
        {
            skyboxManager = manager;
        }

        public void SetTerrainManager(RemixTerrainManager manager)
        {
            terrainManager = manager;
        }
        
        public RemixRenderThread(
            ManualLogSource logger,
            RemixWindowManager windowManager,
            RemixCameraHandler cameraHandler,
            RemixMeshConverter meshConverter,
            RemixLightConverter lightConverter,
            RemixFrameCapture frameCapture,
            ConfigEntry<int> targetFPS,
            ConfigEntry<int> debugLogInterval,
            ConfigEntry<bool> enableLights,
            ConfigEntry<bool> useGameGeometry,
            RemixAPI.remixapi_Interface remixInterface)
        {
            this.logger = logger;
            this.windowManager = windowManager;
            this.cameraHandler = cameraHandler;
            this.meshConverter = meshConverter;
            this.lightConverter = lightConverter;
            this.frameCapture = frameCapture;
            this.configTargetFPS = targetFPS;
            this.configDebugLogInterval = debugLogInterval;
            this.configEnableLights = enableLights;
            this.configUseGameGeometry = useGameGeometry;
            
            // Cache delegate
            if (remixInterface.Present != IntPtr.Zero)
            {
                presentFunc = Marshal.GetDelegateForFunctionPointer<RemixAPI.PFN_remixapi_Present>(
                    remixInterface.Present);
            }
            if (remixInterface.Shutdown != IntPtr.Zero)
                shutdownFunc = Marshal.GetDelegateForFunctionPointer<RemixAPI.PFN_remixapi_Shutdown>(remixInterface.Shutdown);
        }
        
        /// <summary>
        /// Set the scene mesh scanner for drawing scanned level geometry.
        /// </summary>
        public void SetSceneMeshScanner(SceneMeshScanner scanner)
        {
            sceneMeshScanner = scanner;
        }
        
        /// <summary>
        /// Update frame state (called from main thread)
        /// </summary>
        public void UpdateFrameState(RemixFrameCapture.FrameState newState)
        {
            lock (captureLock)
            {
                currentFrameState = newState;
                RemixFrameCapture.PublishedFrame = newState.frameCount;
            }
            frameReadyEvent.Set();
        }
        
        /// <summary>
        /// Start the render thread
        /// </summary>
        public void Start()
        {
            if (renderThread != null && renderThread.IsAlive)
            {
                logger.LogInfo("Render thread already running");
                return;
            }
            
            renderThreadRunning = true;
            renderThread = new Thread(RenderThreadLoop);
            renderThread.IsBackground = true;
            renderThread.Start();
            logger.LogInfo("Render thread started");
        }
        
        /// <summary>
        /// Stop the render thread
        /// </summary>
        public bool Stop()
        {
            renderThreadRunning = false;
            frameReadyEvent.Set();
            return renderThread == null || !renderThread.IsAlive || renderThread.Join(5000);
        }

        private void RenderThreadLoop()
        {
            try
            {
                RunRenderLoop();
            }
            catch (Exception ex)
            {
                logger.LogError($"Render thread failed: {ex}");
            }
            finally
            {
                deviceReady = false;
                ClearRetainedSceneInstances();
                // SDL/Vulkan shutdown must run on the Startup thread, before
                // its borrowed HWND is destroyed or the native DLL unloaded.
                var result = shutdownFunc?.Invoke();
                logger.LogInfo($"openremix render-thread shutdown: {result}");
                windowManager.DestroyRemixWindow();
            }
        }
        
        /// <summary>
        /// Main render loop
        /// </summary>
        private void RunRenderLoop()
        {
            logger.LogInfo("Render thread loop starting...");
            
            // Initialize Remix API on this render thread
            if (!windowManager.InitializeRemixAPI())
            {
                logger.LogError("Failed to initialize Remix API on render thread");
                return;
            }
            
            // Signal that the device is ready for API calls from other threads
            deviceReady = true;
            logger.LogInfo("Remix device ready — main thread may now call API");
            
            // Create test objects
            logger.LogInfo("Creating test triangle and light...");
            testMeshHandle = meshConverter.CreateTestTriangle();
            testLightHandle = lightConverter.CreateTestLight();
            
            int frameNum = 0;
            
            while (renderThreadRunning)
            {
                try
                {
                    // Frame rate limiting / wait timeout
                    int waitTimeout = configTargetFPS.Value > 0 ? (1000 / configTargetFPS.Value) : 50;
                    
                    RemixWatchdog.BeatRender("RenderLoop.WaitForFrame");
                    bool frameReady = frameReadyEvent.WaitOne(waitTimeout);
                    if (!renderThreadRunning) break;
                    
                    RemixWatchdog.BeatRender("RenderLoop.PumpMessages");
                    // Process messages / SDL events
                    windowManager.PumpWindowsMessages();
                    
                    // Render frame when new capture data arrives, or continuously in test mode
                    if (frameReady || !configUseGameGeometry.Value)
                    {
                        RemixWatchdog.BeatRender("RenderLoop.RenderFrame");
                        RenderFrame(frameNum);
                        frameNum++;
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError($"Render thread error: {ex}");
                    Thread.Sleep(500);
                }
            }
            
            logger.LogInfo("Render thread loop ended");
        }
        
        private readonly System.Diagnostics.Stopwatch renderTotalSw = new System.Diagnostics.Stopwatch();
        private readonly System.Diagnostics.Stopwatch renderPhaseSw = new System.Diagnostics.Stopwatch();
        private static readonly uint FramePerfStructSize = (uint)Marshal.SizeOf<RemixAPI.remixapi_FramePerformanceEXT>();
        private static readonly uint FrameBreakdownStructSize = (uint)Marshal.SizeOf<RemixAPI.remixapi_FrameBreakdownEXT>();
        private static readonly uint ScenePreparationStructSize = (uint)Marshal.SizeOf<RemixAPI.remixapi_ScenePreparationEXT>();
        private static readonly uint TracingStatsStructSize = (uint)Marshal.SizeOf<RemixAPI.remixapi_TracingStatisticsEXT>();
        private static readonly uint TextureStatsStructSize = (uint)Marshal.SizeOf<RemixAPI.remixapi_TextureStatisticsEXT>();

        /// <summary>
        /// Render a single frame
        /// </summary>
        private void RenderFrame(int frameNum)
        {
            try
            {
                renderTotalSw.Restart();
                double meshBatchMs = 0;
                double renderGeomMs = 0;
                double processLightsMs = 0;
                double presentMs = 0;

                bool hasGeometry = false;
                if (configUseGameGeometry.Value)
                {
                    // Process queued mesh creation on render thread (prevents main thread deadlocks)
                    RemixWatchdog.BeatRender("RenderFrame.ProcessMeshCreationBatch");
                    renderPhaseSw.Restart();
                    frameCapture?.ProcessMeshCreationBatch();
                    meshBatchMs = renderPhaseSw.Elapsed.TotalMilliseconds;
                    
                    // Render game geometry
                    RemixWatchdog.BeatRender("RenderFrame.RenderGameGeometry");
                    renderPhaseSw.Restart();
                    hasGeometry = RenderGameGeometry();
                    renderGeomMs = renderPhaseSw.Elapsed.TotalMilliseconds;
                    
                    // Process Unity lights + skybox light
                    RemixWatchdog.BeatRender("RenderFrame.ProcessLights");
                    renderPhaseSw.Restart();
                    lightConverter.ProcessLights(frameNum);

                    // Draw skybox light (DomeLight mode)
                    RemixWatchdog.BeatRender("RenderFrame.DrawSkyLight");
                    skyboxManager?.DrawSkyLight(frameNum);
                    processLightsMs = renderPhaseSw.Elapsed.TotalMilliseconds;
                    
                    // Draw test light if lights disabled
                    if (!configEnableLights.Value && testLightHandle != IntPtr.Zero)
                    {
                        // DrawLightInstance would be called here
                    }
                }
                else
                {
                    // Test mode - just render triangle and light
                    cameraHandler.SetupTestCamera(
                        windowManager.WindowWidth,
                        windowManager.WindowHeight,
                        frameNum
                    );
                    
                    // Draw test objects
                    if (testMeshHandle != IntPtr.Zero)
                    {
                        meshConverter.DrawMeshInstance(
                            testMeshHandle,
                            UnityEngine.Matrix4x4.identity,
                            1
                        );
                        hasGeometry = true;
                    }
                }
                
                // Present (only when geometry has been submitted to prevent DXVK divide-by-zero crashes on empty scenes)
                if (presentFunc != null && hasGeometry)
                {
                    RemixWatchdog.BeatRender("RenderFrame.Present.Before");
                    var presentInfo = new RemixAPI.remixapi_PresentInfo
                    {
                        sType = RemixAPI.remixapi_StructType.REMIXAPI_STRUCT_TYPE_PRESENT_INFO,
                        pNext = IntPtr.Zero,
                        hwndOverride = IntPtr.Zero
                    };
                    
                    renderPhaseSw.Restart();
                    var result = presentFunc(ref presentInfo);
                    presentMs = renderPhaseSw.Elapsed.TotalMilliseconds;
                    RemixWatchdog.BeatRender("RenderFrame.Present.After");
                    if (result != RemixAPI.remixapi_ErrorCode.REMIXAPI_ERROR_CODE_SUCCESS)
                    {
                        if (configDebugLogInterval.Value > 0 && frameNum % configDebugLogInterval.Value == 0)
                        {
                            logger.LogWarning($"Present failed: {result}");
                        }
                    }

                    // Poll OpenRemix native telemetry periodically after Present
                    if ((frameNum % 10 == 0))
                    {
                        PollNativeTelemetry();
                    }
                }

                double totalMs = renderTotalSw.Elapsed.TotalMilliseconds;
                RemixProfiler.RecordRenderThread(totalMs, meshBatchMs, renderGeomMs, processLightsMs, presentMs);
            }
            catch (Exception ex)
            {
                logger.LogError($"Error in RenderFrame: {ex}");
            }
        }

        private static void PollNativeTelemetry()
        {
            RemixWindowManager.PollNativeUIStateFromRenderThread();

            bool any = false;
            var fp = new RemixAPI.remixapi_FramePerformanceEXT { structSize = FramePerfStructSize };
            var fb = new RemixAPI.remixapi_FrameBreakdownEXT { structSize = FrameBreakdownStructSize };
            var ts = new RemixAPI.remixapi_TracingStatisticsEXT { structSize = TracingStatsStructSize };
            var tx = new RemixAPI.remixapi_TextureStatisticsEXT { structSize = TextureStatsStructSize };

            if (RemixAPI.GetFramePerformanceEXTFunc != null)
            {
                if (RemixAPI.GetFramePerformanceEXTFunc(ref fp) == RemixAPI.remixapi_ErrorCode.REMIXAPI_ERROR_CODE_SUCCESS)
                    any = true;
            }
            if (RemixAPI.GetFrameBreakdownEXTFunc != null)
            {
                RemixAPI.GetFrameBreakdownEXTFunc(ref fb);
            }
            if (RemixAPI.GetTracingStatisticsEXTFunc != null)
            {
                RemixAPI.GetTracingStatisticsEXTFunc(ref ts);
            }
            if (RemixAPI.GetTextureStatisticsEXTFunc != null)
            {
                RemixAPI.GetTextureStatisticsEXTFunc(ref tx);
            }

            if (any)
            {
                RemixProfiler.RecordNativeTelemetry(in fp, in fb, in ts, in tx);
            }

            if (any && RemixAPI.GetScenePreparationEXTFunc != null)
            {
                var sp = new RemixAPI.remixapi_ScenePreparationEXT { structSize = ScenePreparationStructSize };
                if (RemixAPI.GetScenePreparationEXTFunc(ref sp) == RemixAPI.remixapi_ErrorCode.REMIXAPI_ERROR_CODE_SUCCESS)
                    RemixProfiler.RecordScenePreparation(in sp);
            }
        }
        
        /// <summary>
        /// Render game geometry from captured frame state
        /// </summary>
        private bool RenderGameGeometry()
        {
            // Get frame state atomically
            RemixFrameCapture.FrameState state;
            lock (captureLock)
            {
                state = currentFrameState;
                // Its reused particle buffers stay untouched until this thread takes a newer frame.
                RemixFrameCapture.RenderingFrame = state.frameCount;
            }
            
            // Setup camera - CRITICAL for rendering!
            if (state.camera.valid)
            {
                var cam = state.camera;
                // Convert camera from Unity Y-up to Z-up coordinate system
                cameraHandler.SetupRemixCamera(
                    cam.position, cam.forward, cam.up, cam.right,
                    cam.fov, cam.aspect, cam.nearPlane, cam.farPlane
                );
            }
            else
            {
                // Fallback to test camera if no valid camera captured
                cameraHandler.SetupTestCamera(
                    windowManager.WindowWidth,
                    windowManager.WindowHeight,
                    state.frameCount
                );
            }
            
            if (Interlocked.Exchange(ref needClearRetained, 0) != 0)
            {
                ClearRetainedSceneInstances();
            }

            long retainedStart = System.Diagnostics.Stopwatch.GetTimestamp();
            double sceneApiMs = 0;
            uint objectPickingValue = 1;
            claimedRendererIds.Clear();
            claimedStaticKeys.Clear();
            visibleRetainedKeysThisFrame.Clear();
            pendingAddInfos.Clear();
            pendingAddKeys.Clear();
            pendingChanges.Clear();

            // 1. Scanned static scene meshes (level geometry)
            if (sceneMeshScanner != null && (sceneMeshScanner.HasData || sceneMeshScanner.IsStreaming))
            {
                var scannedInstances = sceneMeshScanner.GetInstances(out int scannedCount);
                if (scannedInstances != null && scannedCount > 0)
                {
                    for (int i = 0; i < scannedCount; i++)
                    {
                        var instance = scannedInstances[i];
                        if (instance.MeshHandle == IntPtr.Zero)
                            continue;

                        if (frameCapture != null && frameCapture.IsLayerDisabled(instance.Layer))
                            continue;

                        if (!StaticGeometryDedupe.TryClaimVisibleInstance(instance.RendererInstanceId, instance.DedupeKey, claimedRendererIds, claimedStaticKeys))
                            continue;

                        ulong instanceKey = ((ulong)(uint)instance.RendererInstanceId << 32) | (ulong)(uint)(instance.MeshHandle.ToInt64() & 0xFFFFFFFF);
                        visibleRetainedKeysThisFrame.Add(instanceKey);

                        if (retainedSceneInstances.TryGetValue(instanceKey, out var record))
                        {
                            uint fields = 0;
                            if (!TransformEquals(in record.Transform, in instance.Transform))
                            {
                                fields |= RemixAPI.REMIXAPI_SCENE_INSTANCE_TRANSFORM_EXT;
                                record.Transform = instance.Transform;
                            }
                            if (!record.Visible)
                            {
                                fields |= RemixAPI.REMIXAPI_SCENE_INSTANCE_VISIBLE_EXT;
                                record.Visible = true;
                            }
                            if (fields != 0)
                            {
                                pendingChanges.Add(new RemixAPI.remixapi_SceneInstanceChangeEXT
                                {
                                    instance = record.NativeHandle,
                                    fields = fields,
                                    visible = 1u,
                                    transform = instance.Transform
                                });
                            }
                            record.LastSeenFrame = (uint)state.frameCount;
                            retainedSceneInstances[instanceKey] = record;
                        }
                        else
                        {
                            pendingAddInfos.Add(new RemixAPI.remixapi_InstanceInfo
                            {
                                sType = RemixAPI.remixapi_StructType.REMIXAPI_STRUCT_TYPE_INSTANCE_INFO,
                                pNext = IntPtr.Zero,
                                categoryFlags = 0,
                                mesh = instance.MeshHandle,
                                transform = instance.Transform,
                                doubleSided = 1
                            });
                            pendingAddKeys.Add(instanceKey);
                        }
                    }
                }
            }

            // 2. Terrain chunks
            if (terrainManager != null && (terrainManager.HasData || terrainManager.IsStreaming))
            {
                var terrainInstances = terrainManager.GetInstances();
                if (terrainInstances != null && terrainInstances.Length > 0)
                {
                    for (int i = 0; i < terrainInstances.Length; i++)
                    {
                        var instance = terrainInstances[i];
                        if (instance.MeshHandle == IntPtr.Zero)
                            continue;

                        if (frameCapture != null && frameCapture.IsLayerDisabled(instance.Layer))
                            continue;

                        ulong instanceKey = ((ulong)(uint)instance.RendererInstanceId << 32) | (ulong)(uint)(instance.MeshHandle.ToInt64() & 0xFFFFFFFF);
                        visibleRetainedKeysThisFrame.Add(instanceKey);

                        if (retainedSceneInstances.TryGetValue(instanceKey, out var record))
                        {
                            uint fields = 0;
                            if (!TransformEquals(in record.Transform, in instance.Transform))
                            {
                                fields |= RemixAPI.REMIXAPI_SCENE_INSTANCE_TRANSFORM_EXT;
                                record.Transform = instance.Transform;
                            }
                            if (!record.Visible)
                            {
                                fields |= RemixAPI.REMIXAPI_SCENE_INSTANCE_VISIBLE_EXT;
                                record.Visible = true;
                            }
                            if (fields != 0)
                            {
                                pendingChanges.Add(new RemixAPI.remixapi_SceneInstanceChangeEXT
                                {
                                    instance = record.NativeHandle,
                                    fields = fields,
                                    visible = 1u,
                                    transform = instance.Transform
                                });
                            }
                            record.LastSeenFrame = (uint)state.frameCount;
                            retainedSceneInstances[instanceKey] = record;
                        }
                        else
                        {
                            pendingAddInfos.Add(new RemixAPI.remixapi_InstanceInfo
                            {
                                sType = RemixAPI.remixapi_StructType.REMIXAPI_STRUCT_TYPE_INSTANCE_INFO,
                                pNext = IntPtr.Zero,
                                categoryFlags = 0,
                                mesh = instance.MeshHandle,
                                transform = instance.Transform,
                                doubleSided = 1
                            });
                            pendingAddKeys.Add(instanceKey);
                        }
                    }
                }
            }

            // 3. Static and dynamic instances from state.instances
            foreach (var instance in state.instances)
            {
                ulong meshKey = instance.meshKey != 0 ? instance.meshKey : (ulong)(uint)instance.meshId;
                if (meshConverter.TryGetMeshHandle(meshKey, out IntPtr meshHandle))
                {
                    if (instance.categoryFlags != 0)
                    {
                        // Dynamic / categorized instance (view models, particles, UI, decals)
                        ulong persistentId = (ulong)(uint)instance.rendererInstanceId;
                        if (persistentId == 0) persistentId = (ulong)objectPickingValue;

                        meshConverter.DrawMeshInstance(meshHandle, instance.localToWorld, objectPickingValue, instance.categoryFlags, persistentId, isDynamic: true);
                        objectPickingValue++;
                    }
                    else
                    {
                        // Static instance from runtime capture (if not already claimed by scanner)
                        if (!StaticGeometryDedupe.TryClaimVisibleInstance(instance.rendererInstanceId, instance.dedupeKey, claimedRendererIds, claimedStaticKeys))
                            continue;

                        var m = instance.localToWorld;
                        var transform = RemixAPI.remixapi_Transform.FromMatrix(
                            m.m00, m.m02, m.m01, m.m03,
                            m.m20, m.m22, m.m21, m.m23,
                            m.m10, m.m12, m.m11, m.m13
                        );

                        ulong instanceKey = ((ulong)(uint)instance.rendererInstanceId << 32) | (ulong)(uint)(meshHandle.ToInt64() & 0xFFFFFFFF);
                        visibleRetainedKeysThisFrame.Add(instanceKey);

                        if (retainedSceneInstances.TryGetValue(instanceKey, out var record))
                        {
                            uint fields = 0;
                            if (!TransformEquals(in record.Transform, in transform))
                            {
                                fields |= RemixAPI.REMIXAPI_SCENE_INSTANCE_TRANSFORM_EXT;
                                record.Transform = transform;
                            }
                            if (!record.Visible)
                            {
                                fields |= RemixAPI.REMIXAPI_SCENE_INSTANCE_VISIBLE_EXT;
                                record.Visible = true;
                            }
                            if (fields != 0)
                            {
                                pendingChanges.Add(new RemixAPI.remixapi_SceneInstanceChangeEXT
                                {
                                    instance = record.NativeHandle,
                                    fields = fields,
                                    visible = 1u,
                                    transform = transform
                                });
                            }
                            record.LastSeenFrame = (uint)state.frameCount;
                            retainedSceneInstances[instanceKey] = record;
                        }
                        else
                        {
                            pendingAddInfos.Add(new RemixAPI.remixapi_InstanceInfo
                            {
                                sType = RemixAPI.remixapi_StructType.REMIXAPI_STRUCT_TYPE_INSTANCE_INFO,
                                pNext = IntPtr.Zero,
                                categoryFlags = 0,
                                mesh = meshHandle,
                                transform = transform,
                                doubleSided = 1
                            });
                            pendingAddKeys.Add(instanceKey);
                        }
                    }
                }
            }

            // 4. Update visibility for retained instances not seen this frame, and prune stale unseen instances
            if (retainedSceneInstances.Count > visibleRetainedKeysThisFrame.Count)
            {
                reusableHiddenKeys.Clear();
                reusableRemoveKeys.Clear();
                uint currentFrame = (uint)state.frameCount;

                foreach (var kvp in retainedSceneInstances)
                {
                    if (!visibleRetainedKeysThisFrame.Contains(kvp.Key))
                    {
                        if (kvp.Value.Visible)
                        {
                            reusableHiddenKeys.Add(kvp.Key);
                        }
                        else if (currentFrame > kvp.Value.LastSeenFrame && (currentFrame - kvp.Value.LastSeenFrame) > 300u)
                        {
                            reusableRemoveKeys.Add(kvp.Key);
                        }
                    }
                }

                for (int i = 0; i < reusableHiddenKeys.Count; i++)
                {
                    ulong k = reusableHiddenKeys[i];
                    var record = retainedSceneInstances[k];
                    pendingChanges.Add(new RemixAPI.remixapi_SceneInstanceChangeEXT
                    {
                        instance = record.NativeHandle,
                        fields = RemixAPI.REMIXAPI_SCENE_INSTANCE_VISIBLE_EXT,
                        visible = 0u,
                        transform = record.Transform
                    });
                    record.Visible = false;
                    retainedSceneInstances[k] = record;
                }

                if (reusableRemoveKeys.Count > 0)
                {
                    if (removeHandlesBuffer.Length < reusableRemoveKeys.Count)
                    {
                        removeHandlesBuffer = new ulong[Math.Max(reusableRemoveKeys.Count, removeHandlesBuffer.Length * 2)];
                    }
                    for (int i = 0; i < reusableRemoveKeys.Count; i++)
                    {
                        ulong k = reusableRemoveKeys[i];
                        removeHandlesBuffer[i] = retainedSceneInstances[k].NativeHandle;
                        retainedSceneInstances.Remove(k);
                    }
                    long apiStart = System.Diagnostics.Stopwatch.GetTimestamp();
                    unsafe
                    {
                        fixed (ulong* pRemove = removeHandlesBuffer)
                        {
                            RemixAPI.RemoveSceneInstancesEXTFunc((IntPtr)pRemove, (uint)reusableRemoveKeys.Count);
                        }
                    }
                    sceneApiMs += RemixProfiler.ElapsedMs(apiStart);
                }
            }

            // 5. Submit pending additions
            if (pendingAddInfos.Count > 0)
            {
                EnsureAddBuffersCapacity(pendingAddInfos.Count);
                for (int i = 0; i < pendingAddInfos.Count; i++)
                    addInfosBuffer[i] = pendingAddInfos[i];

                long apiStart = System.Diagnostics.Stopwatch.GetTimestamp();
                unsafe
                {
                    fixed (RemixAPI.remixapi_InstanceInfo* pInfos = addInfosBuffer)
                    fixed (ulong* pHandles = addHandlesBuffer)
                    {
                        var res = RemixAPI.AddSceneInstancesEXTFunc((IntPtr)pInfos, (uint)pendingAddInfos.Count, (IntPtr)pHandles);
                        if (res == RemixAPI.remixapi_ErrorCode.REMIXAPI_ERROR_CODE_SUCCESS)
                        {
                            for (int i = 0; i < pendingAddInfos.Count; i++)
                            {
                                retainedSceneInstances[pendingAddKeys[i]] = new RetainedInstanceRecord
                                {
                                    NativeHandle = addHandlesBuffer[i],
                                    MeshHandle = addInfosBuffer[i].mesh,
                                    Transform = addInfosBuffer[i].transform,
                                    Visible = true,
                                    LastSeenFrame = (uint)state.frameCount
                                };
                            }
                        }
                        else
                        {
                            logger.LogWarning($"[RenderThread] AddSceneInstancesEXT failed: {res}");
                        }
                    }
                }
                sceneApiMs += RemixProfiler.ElapsedMs(apiStart);
            }

            // 6. Submit pending changes
            if (pendingChanges.Count > 0)
            {
                EnsureChangesBufferCapacity(pendingChanges.Count);
                for (int i = 0; i < pendingChanges.Count; i++)
                    changesBuffer[i] = pendingChanges[i];

                long apiStart = System.Diagnostics.Stopwatch.GetTimestamp();
                unsafe
                {
                    fixed (RemixAPI.remixapi_SceneInstanceChangeEXT* pChanges = changesBuffer)
                    {
                        RemixAPI.ChangeSceneInstancesEXTFunc((IntPtr)pChanges, (uint)pendingChanges.Count);
                    }
                }
                sceneApiMs += RemixProfiler.ElapsedMs(apiStart);
            }
            RemixProfiler.AddDetail(RemixProfiler.Detail.DrawRetained, RemixProfiler.ElapsedMs(retainedStart));
            RemixProfiler.AddDetail(RemixProfiler.Detail.DrawSceneApi, sceneApiMs);

            // 7. Draw all retained scene instances in ONE call
            long sceneStart = System.Diagnostics.Stopwatch.GetTimestamp();
            RemixAPI.DrawSceneInstancesEXTFunc(IntPtr.Zero);
            RemixProfiler.AddDetail(RemixProfiler.Detail.DrawScene, RemixProfiler.ElapsedMs(sceneStart));

            // 8. Draw dynamic / skinned meshes
            objectPickingValue = RenderSkinnedMeshes(state, objectPickingValue);

            if (retainedSceneInstances.Count == 0 && objectPickingValue == 1 && testMeshHandle != IntPtr.Zero)
            {
                var dummyMatrix = UnityEngine.Matrix4x4.TRS(
                    new UnityEngine.Vector3(0, -999999f, 0),
                    UnityEngine.Quaternion.identity,
                    UnityEngine.Vector3.one * 0.0001f
                );
                meshConverter.DrawMeshInstance(testMeshHandle, dummyMatrix, objectPickingValue, isDynamic: true);
                objectPickingValue++;
            }

            return retainedSceneInstances.Count > 0 || objectPickingValue > 1;
        }
        
        /// <summary>
        /// Render skinned meshes from frame state
        /// </summary>
        private uint RenderSkinnedMeshes(RemixFrameCapture.FrameState state, uint startObjectPickingValue)
        {
            if (state.skinned == null || state.skinned.Count == 0)
                return startObjectPickingValue;
            
            updatedMeshes.Clear();
            uint objectPickingValue = startObjectPickingValue;
            double gpuSkinnedMs = 0, dynUploadMs = 0, dynDrawMs = 0;
            int gpuSkinnedDraws = 0, dynUploads = 0, dynDraws = 0;

            foreach (var skinned in state.skinned)
            {
                long phaseStart = System.Diagnostics.Stopwatch.GetTimestamp();
                try
                {
                    if (skinned.skinningData != null && skinned.boneTransforms != null)
                    {
                        // GPU skinning path: create mesh once with bone weights, draw with bone transforms each frame
                        IntPtr meshHandle;
                        if (!skinned.skinningData.meshCreated ||
                            !meshConverter.TryGetSkinnedMeshHandle(skinned.remixMeshHash, out meshHandle) ||
                            meshHandle == IntPtr.Zero)
                        {
                            meshHandle = meshConverter.CreateSkinnedMeshWithBones(
                                skinned.remixMeshHash,
                                skinned.vertices,
                                skinned.normals,
                                skinned.uvs,
                                skinned.triangles,
                                skinned.skinningData.blendWeights,
                                skinned.skinningData.blendIndices,
                                skinned.skinningData.bonesPerVertex,
                                skinned.materialId,
                                skinned.colors
                            );
                            
                            if (meshHandle == IntPtr.Zero)
                                continue;
                            
                            meshConverter.UpdateSkinnedMeshHandle(skinned.remixMeshHash, meshHandle, state.frameCount);
                            skinned.skinningData.meshCreated = true;
                        }
                        
                        updatedMeshes.Add(skinned.remixMeshHash);
                        ulong persistentId = (ulong)(uint)skinned.meshId;
                        if (persistentId == 0) persistentId = (ulong)objectPickingValue;

                        meshConverter.DrawSkinnedInstance(meshHandle, skinned.localToWorld, skinned.boneTransforms, objectPickingValue, persistentId, skinned.categoryFlags);
                        objectPickingValue++;
                        gpuSkinnedMs += RemixProfiler.ElapsedMs(phaseStart);
                        gpuSkinnedDraws++;
                    }
                    else
                    {
                        // BakeMesh / dynamic mesh path: only upload vertex data when bakeFrame has advanced
                        if (!meshConverter.TryGetUpToDateSkinnedMesh(skinned.remixMeshHash, skinned.bakeFrame, out IntPtr meshHandle))
                        {
                            meshHandle = meshConverter.CreateRemixMeshFromData(
                                skinned.remixMeshHash,
                                skinned.vertices,
                                skinned.normals,
                                skinned.uvs,
                                skinned.triangles,
                                state.frameCount,
                                skinned.materialId,
                                skinned.colors,
                                skinned.uvST,
                                skinned.vertexMap,
                                skinned.usedVertexCount,
                                skinned.usedIndexCount
                            );

                            if (meshHandle == IntPtr.Zero)
                                continue;

                            int uploaded = skinned.vertexMap != null ? skinned.vertexMap.Length
                                : skinned.usedVertexCount > 0 ? skinned.usedVertexCount : skinned.vertices.Length;
                            int source = (int)skinned.source;
                            uploadsBySource[source]++;
                            uploadVertsBySource[source] += uploaded;
                            uploadMsBySource[source] += RemixProfiler.ElapsedMs(phaseStart);
                            if (uploaded > largestUploadVerts)
                            {
                                largestUploadVerts = uploaded;
                                largestUploadHash = skinned.remixMeshHash;
                                largestUploadSource = skinned.source;
                            }

                            meshConverter.UpdateSkinnedMeshHandle(skinned.remixMeshHash, meshHandle, state.frameCount, skinned.bakeFrame);
                            dynUploads++;
                        }
                        updatedMeshes.Add(skinned.remixMeshHash);

                        ulong persistentId = (ulong)(uint)skinned.meshId;
                        if (persistentId == 0) persistentId = (ulong)objectPickingValue;

                        dynUploadMs += RemixProfiler.ElapsedMs(phaseStart);
                        long drawStart = System.Diagnostics.Stopwatch.GetTimestamp();
                        meshConverter.DrawMeshInstance(meshHandle, skinned.localToWorld, objectPickingValue, skinned.categoryFlags, persistentId, isDynamic: true);
                        objectPickingValue++;
                        dynDrawMs += RemixProfiler.ElapsedMs(drawStart);
                        dynDraws++;
                    }
                }
                catch { }
            }
            RemixProfiler.AddDetail(RemixProfiler.Detail.DrawGpuSkinned, gpuSkinnedMs);
            RemixProfiler.AddDetail(RemixProfiler.Detail.DrawDynUpload, dynUploadMs);
            RemixProfiler.AddDetail(RemixProfiler.Detail.DrawDynDraw, dynDrawMs);
            RemixProfiler.AddDrawCounts(gpuSkinnedDraws, dynUploads, dynDraws);

            if (++uploadLogFrames >= 300)
            {
                var line = new System.Text.StringBuilder($"[DynamicUploads] per frame over {uploadLogFrames}:");
                for (int src = 0; src < uploadsBySource.Length; src++)
                {
                    if (uploadsBySource[src] == 0) continue;
                    line.Append($" {(RemixFrameCapture.DynamicSource)src} {uploadsBySource[src] / (double)uploadLogFrames:F1} meshes, {uploadVertsBySource[src] / uploadLogFrames} verts, {uploadMsBySource[src] / uploadLogFrames:F2}ms;");
                }
                line.Append($" largest {largestUploadSource} 0x{largestUploadHash:X16} {largestUploadVerts} verts");
                logger.LogInfo(line.ToString());
                uploadLogFrames = 0;
                Array.Clear(uploadsBySource, 0, uploadsBySource.Length);
                Array.Clear(uploadVertsBySource, 0, uploadVertsBySource.Length);
                Array.Clear(uploadMsBySource, 0, uploadMsBySource.Length);
                largestUploadVerts = 0;
                largestUploadHash = 0;
            }

            // Cleanup stale meshes periodically
            if (state.frameCount % 60 == 0)
            {
                meshConverter.CleanupStaleSkinnedMeshes(updatedMeshes, state.frameCount);
            }

            return objectPickingValue;
        }
    }
}
