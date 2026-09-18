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
        
        // Frame state & event synchronization
        private volatile RemixFrameCapture.FrameState currentFrameState = new RemixFrameCapture.FrameState();
        private readonly object captureLock = new object();
        private readonly AutoResetEvent frameReadyEvent = new AutoResetEvent(false);
        private readonly HashSet<int> claimedRendererIds = new HashSet<int>(4096);
        private readonly HashSet<StaticGeometryKey> claimedStaticKeys = new HashSet<StaticGeometryKey>(4096);
        
        // Scene mesh scanner (optional)
        private SceneMeshScanner sceneMeshScanner;
        
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
        public void Stop()
        {
            renderThreadRunning = false;
            frameReadyEvent.Set();
            if (renderThread != null && renderThread.IsAlive)
            {
                renderThread.Join(1000);
            }
        }
        
        /// <summary>
        /// Main render loop
        /// </summary>
        private void RenderThreadLoop()
        {
            logger.LogInfo("Render thread loop starting...");
            RemixTracy.SetThreadName("RemixRenderThread");
            
            // Create window on this thread
            if (!windowManager.CreateRemixWindow())
            {
                logger.LogError("Failed to create Remix window on render thread");
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
                    // Wait for main thread to capture a new frame (up to 16ms fallback)
                    frameReadyEvent.WaitOne(16);

                    // Process messages
                    windowManager.PumpWindowsMessages();
                    
                    // Render frame
                    RenderFrame(frameNum);
                    frameNum++;
                    
                    // Frame rate limiting
                    if (configTargetFPS.Value > 0)
                    {
                        uint waitMs = (uint)(1000 / configTargetFPS.Value);
                        if (waitMs > 0 && windowManager.WaitForMessages(waitMs))
                        {
                            windowManager.PumpWindowsMessages();
                        }
                    }
                    else
                    {
                        if (windowManager.WaitForMessages(0))
                        {
                            windowManager.PumpWindowsMessages();
                        }
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError($"Render thread error: {ex}");
                    Thread.Sleep(1000);
                }
            }
            
            logger.LogInfo("Render thread loop ended");
        }
        
        /// <summary>
        /// Render a single frame
        /// </summary>
        private void RenderFrame(int frameNum)
        {
            using (RemixTracy.Zone("RemixRenderThread_Frame"))
            {
                var totalSw = System.Diagnostics.Stopwatch.StartNew();
                double meshBatchMs = 0, renderGeomMs = 0, processLightsMs = 0, presentMs = 0;

                try
                {
                    bool hasGeometry = false;
                    if (configUseGameGeometry.Value)
                    {
                        // Process queued mesh creation on render thread (prevents main thread deadlocks)
                        using (RemixTracy.Zone("ProcessMeshCreationBatch"))
                        {
                            var sw = System.Diagnostics.Stopwatch.StartNew();
                            frameCapture?.ProcessMeshCreationBatch();
                            meshBatchMs = sw.Elapsed.TotalMilliseconds;
                        }
                        
                        // Render game geometry
                        using (RemixTracy.Zone("RenderGameGeometry"))
                        {
                            var sw = System.Diagnostics.Stopwatch.StartNew();
                            hasGeometry = RenderGameGeometry();
                            renderGeomMs = sw.Elapsed.TotalMilliseconds;
                        }
                        
                        // Process Unity lights
                        using (RemixTracy.Zone("ProcessLights"))
                        {
                            var sw = System.Diagnostics.Stopwatch.StartNew();
                            lightConverter.ProcessLights(frameNum);
                            processLightsMs = sw.Elapsed.TotalMilliseconds;
                        }
                        
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
                        using (RemixTracy.Zone("RemixPresent"))
                        {
                            var sw = System.Diagnostics.Stopwatch.StartNew();
                            var presentInfo = new RemixAPI.remixapi_PresentInfo
                            {
                                sType = RemixAPI.remixapi_StructType.REMIXAPI_STRUCT_TYPE_PRESENT_INFO,
                                pNext = IntPtr.Zero,
                                hwndOverride = IntPtr.Zero
                            };
                            
                            var result = presentFunc(ref presentInfo);
                            presentMs = sw.Elapsed.TotalMilliseconds;

                            if (result != RemixAPI.remixapi_ErrorCode.REMIXAPI_ERROR_CODE_SUCCESS)
                            {
                                if (configDebugLogInterval.Value > 0 && frameNum % configDebugLogInterval.Value == 0)
                                {
                                    logger.LogWarning($"Present failed: {result}");
                                }
                            }
                        }
                    }

                    RemixProfiler.RecordRenderThread(
                        totalSw.Elapsed.TotalMilliseconds,
                        meshBatchMs,
                        renderGeomMs,
                        processLightsMs,
                        presentMs
                    );
                }
                catch (Exception ex)
                {
                    logger.LogError($"Error in RenderFrame: {ex}");
                }
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
            
            // Draw static mesh instances
            uint objectPickingValue = 1;
            claimedRendererIds.Clear();
            claimedStaticKeys.Clear();
            
            foreach (var instance in state.instances)
            {
                ulong meshKey = instance.meshKey != 0 ? instance.meshKey : (ulong)(uint)instance.meshId;
                if (meshConverter.TryGetMeshHandle(meshKey, out IntPtr meshHandle))
                {
                    if (!StaticGeometryDedupe.TryClaimVisibleInstance(instance.rendererInstanceId, instance.dedupeKey, claimedRendererIds, claimedStaticKeys))
                        continue;

                    meshConverter.DrawMeshInstance(meshHandle, instance.localToWorld, objectPickingValue);
                    objectPickingValue++;
                }
            }
            
            // Draw scanned scene mesh instances
            // Always call GetInstancesSnapshot during streaming to drain the queue
            if (sceneMeshScanner != null && (sceneMeshScanner.HasData || sceneMeshScanner.IsStreaming))
            {
                var snapshot = sceneMeshScanner.GetInstancesSnapshot();
                if (snapshot.Items != null && snapshot.Count > 0)
                {
                    var drawFunc = meshConverter.GetDrawInstanceFunc();
                    if (drawFunc != null)
                    {
                        for (int i = 0; i < snapshot.Count; i++)
                        {
                            var instance = snapshot.Items[i];
                            if (instance.MeshHandle == IntPtr.Zero)
                                continue;
                            
                            if (frameCapture != null && frameCapture.IsLayerDisabled(instance.Layer))
                                continue;

                            if (!StaticGeometryDedupe.TryClaimVisibleInstance(instance.RendererInstanceId, instance.DedupeKey, claimedRendererIds, claimedStaticKeys))
                                continue;
                            
                            var instanceInfo = new RemixAPI.remixapi_InstanceInfo
                            {
                                sType = RemixAPI.remixapi_StructType.REMIXAPI_STRUCT_TYPE_INSTANCE_INFO,
                                pNext = IntPtr.Zero,
                                categoryFlags = 0,
                                mesh = instance.MeshHandle,
                                transform = instance.Transform,
                                doubleSided = 1
                            };
                            
                            drawFunc(ref instanceInfo);
                            objectPickingValue++;
                        }
                    }
                }
            }
            
            // Draw skinned meshes
            objectPickingValue = RenderSkinnedMeshes(state, objectPickingValue);

            // If no geometry was drawn this frame, submit a tiny dummy triangle far below the world.
            // DXVK-Remix has a divide-by-zero crash in DxvkBuffer::DxvkBuffer (256 / (surfaceCount * 128))
            // if materials exist in the material cache but 0 mesh surfaces/instances are submitted during present.
            if (objectPickingValue == 1 && testMeshHandle != IntPtr.Zero)
            {
                var dummyMatrix = UnityEngine.Matrix4x4.TRS(
                    new UnityEngine.Vector3(0, -999999f, 0),
                    UnityEngine.Quaternion.identity,
                    UnityEngine.Vector3.one * 0.0001f
                );
                meshConverter.DrawMeshInstance(testMeshHandle, dummyMatrix, objectPickingValue);
                objectPickingValue++;
            }

            return objectPickingValue > 1;
        }
        
        /// <summary>
        /// Render skinned meshes from frame state
        /// </summary>
        private uint RenderSkinnedMeshes(RemixFrameCapture.FrameState state, uint startObjectPickingValue)
        {
            if (state.skinned == null || state.skinned.Count == 0)
                return startObjectPickingValue;
            
            HashSet<ulong> updatedMeshes = new HashSet<ulong>();
            uint objectPickingValue = startObjectPickingValue;
            
            foreach (var skinned in state.skinned)
            {
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
                            
                            meshConverter.UpdateSkinnedMeshHandle(skinned.remixMeshHash, meshHandle);
                            skinned.skinningData.meshCreated = true;
                        }
                        
                        updatedMeshes.Add(skinned.remixMeshHash);
                        meshConverter.DrawSkinnedInstance(meshHandle, skinned.localToWorld, skinned.boneTransforms, objectPickingValue);
                        objectPickingValue++;
                    }
                    else
                    {
                        ulong trackingKey = skinned.meshId != 0 
                            ? (0x7F00000000000000UL | (ulong)(uint)skinned.meshId) 
                            : skinned.remixMeshHash;

                        IntPtr meshHandle;
                        if (skinned.isStaticData &&
                            meshConverter.TryGetSkinnedMeshHandle(trackingKey, out meshHandle) &&
                            meshHandle != IntPtr.Zero)
                        {
                            // Static / cached mesh data unchanged: reuse existing Remix mesh handle!
                            // Prevents generating new geom hashes every frame and preserves temporal history/motion vectors.
                            updatedMeshes.Add(trackingKey);
                            meshConverter.DrawMeshInstance(meshHandle, skinned.localToWorld, objectPickingValue);
                            objectPickingValue++;
                        }
                        else
                        {
                            // BakeMesh fallback: recreate mesh with new vertex data
                            meshHandle = meshConverter.CreateRemixMeshFromData(
                                skinned.remixMeshHash,
                                skinned.vertices,
                                skinned.normals,
                                skinned.uvs,
                                skinned.triangles,
                                state.frameCount,
                                skinned.materialId,
                                skinned.colors,
                                poolKey: trackingKey
                            );

                            if (meshHandle == IntPtr.Zero)
                                continue;

                            meshConverter.UpdateSkinnedMeshHandle(trackingKey, meshHandle);
                            updatedMeshes.Add(trackingKey);
                            meshConverter.DrawMeshInstance(meshHandle, skinned.localToWorld, objectPickingValue);
                            objectPickingValue++;
                        }
                    }
                }
                catch { }
            }
            
            // Cleanup stale meshes periodically
            if (state.frameCount % 60 == 0)
            {
                meshConverter.CleanupStaleSkinnedMeshes(updatedMeshes);
            }

            return objectPickingValue;
        }
    }
}
