using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Rendering;
using Unity.Collections.LowLevel.Unsafe;

namespace UnityRemix
{
    /// <summary>
    /// Captures frame state from Unity main thread for safe transfer to render thread
    /// </summary>
    public partial class RemixFrameCapture
    {
        private readonly ManualLogSource logger;
        private readonly RemixCameraHandler cameraHandler;
        private readonly RemixMeshConverter meshConverter;
        private readonly RemixMaterialManager materialManager;
        private RemixSkyboxManager skyboxManager;
        
        public void SetSkyboxManager(RemixSkyboxManager sm) => skyboxManager = sm;
        
        private readonly ConfigEntry<bool> configUseDistanceCulling;
        private readonly ConfigEntry<float> configMaxRenderDistance;
        private readonly ConfigEntry<bool> configUseVisibilityCulling;
        private readonly ConfigEntry<int> configRendererCacheDuration;
        private readonly ConfigEntry<int> configDebugLogInterval;
        private readonly ConfigEntry<bool> configCaptureStaticMeshes;
        private readonly ConfigEntry<bool> configCaptureSkinnedMeshes;
        private readonly ConfigEntry<bool> configCaptureParticles;
        private readonly ConfigEntry<bool> configEnableParticleDistanceCulling;
        private readonly ConfigEntry<float> configParticleMaxDistance;
        private readonly ConfigEntry<bool> configHardwareSkinning;
        private readonly ConfigEntry<bool> configPersistDisabledRenderers;
        private readonly ConfigEntry<int> configStaticMeshFrameSkip;
        
        // Renderer caching
        private List<MeshRenderer> cachedRenderers = new List<MeshRenderer>();
        private List<SkinnedMeshRenderer> cachedSkinnedRenderers = new List<SkinnedMeshRenderer>();
        private readonly HashSet<int> cachedRendererIds = new HashSet<int>();
        private readonly HashSet<int> cachedSkinnedRendererIds = new HashSet<int>();
        private readonly List<MeshInstanceData> cachedStaticInstances = new List<MeshInstanceData>();

        // Particle system caching
        private struct TrackedParticleSystem
        {
            public ParticleSystemRenderer renderer;
            public ParticleSystem system;
            public int id;
        }
        private readonly List<TrackedParticleSystem> trackedParticleSystems = new List<TrackedParticleSystem>();
        private readonly HashSet<int> trackedParticleSystemIds = new HashSet<int>();
        private readonly HashSet<string> loggedParticleSystems = new HashSet<string>();
        private static ParticleSystem.Particle[] _particleBuffer = new ParticleSystem.Particle[2048];
        private static Mesh _reusableParticleMesh = null;
        private static Mesh _reusableParticleTrailMesh = null;
        private static Mesh _reusableTrailRendererMesh = null;
        private int rendererCacheFrame = -1;

        // Particle batching
        private class ParticleBatchBucket
        {
            public int MaterialId;
            public int SubBucketIndex;
            public int ParticleCount;
            public readonly List<Vector3> Vertices = new List<Vector3>(2048);
            public readonly List<Vector3> Normals = new List<Vector3>(2048);
            public readonly List<Vector2> UVs = new List<Vector2>(2048);
            public readonly List<Color32> Colors = new List<Color32>(2048);
            public readonly List<int> Triangles = new List<int>(3072);

            public void Reset(int matId, int subBucket)
            {
                MaterialId = matId;
                SubBucketIndex = subBucket;
                ParticleCount = 0;
                Vertices.Clear();
                Normals.Clear();
                UVs.Clear();
                Colors.Clear();
                Triangles.Clear();
            }
        }
        private readonly Dictionary<int, ParticleBatchBucket> _particleBatchesByMat = new Dictionary<int, ParticleBatchBucket>();
        private readonly List<ParticleBatchBucket> _activeParticleBatches = new List<ParticleBatchBucket>();
        private readonly List<ParticleBatchBucket> _batchBucketPool = new List<ParticleBatchBucket>();
        private readonly HashSet<int> _capturedParticleMatIds = new HashSet<int>();
        private readonly Dictionary<int, ParticleLook> _particleLookCache = new Dictionary<int, ParticleLook>();
        // Per (particle renderer, material): name- and shader-derived traits, read once instead of
        // allocating the names every frame.
        private struct ParticleTraits { public bool emissive, jumpPad; }
        private readonly Dictionary<long, ParticleTraits> _particleTraitsCache = new Dictionary<long, ParticleTraits>();

        public class ParticleStatsSnapshot
        {
            public int TotalTrackedSystems;
            public int ActiveSystems;
            public int InactiveSystems;
            public int DistanceCulledSystems;
            public int EmittingSystems;
            public int RenderedParticles;
            public int CulledParticles;
            public int BatchedDrawCalls;
            public int BakedMeshes;
            public float FurthestSystemDistance;
            public string FurthestSystemName;
            public int TotalTrackedTrails;
            public int ActiveTrails;
            public int DistanceCulledTrails;
            public ParticleEmitterInfo[] Emitters = Array.Empty<ParticleEmitterInfo>();
        }

        public struct ParticleEmitterInfo
        {
            public string Name;
            public float Distance;
            public int ParticleCount;
            public string Status;
        }

        private ParticleStatsSnapshot _latestParticleStats = new ParticleStatsSnapshot();
        public ParticleStatsSnapshot LatestParticleStats => _latestParticleStats;

        // TrailRenderer caching
        private struct TrackedTrailRenderer
        {
            public TrailRenderer renderer;
            public int id;
        }
        private readonly List<TrackedTrailRenderer> trackedTrailRenderers = new List<TrackedTrailRenderer>();
        private readonly HashSet<int> trackedTrailRendererIds = new HashSet<int>();

        // Scrolling UV mesh tracking
        private static readonly int PropScrollOffset = Shader.PropertyToID("_ScrollOffset");
        private static readonly int PropMainTex = Shader.PropertyToID("_MainTex");
        private static readonly int PropBaseMap = Shader.PropertyToID("_BaseMap");
        private static readonly int PropDiffuse = Shader.PropertyToID("_Diffuse");
        private static readonly int PropTexture = Shader.PropertyToID("_Texture");
        private static readonly int PropColor = Shader.PropertyToID("_Color");
        private static readonly int PropBaseColor = Shader.PropertyToID("_BaseColor");
        private static readonly int PropEmissiveColor = Shader.PropertyToID("_EmissiveColor");
        private class CachedScrollingMesh
        {
            public Vector3[] vertices;
            public Vector3[] normals;
            public Vector2[] baseUVs;
            public Color32[] colors;
            public List<int[]> submeshTriangles;
        }
        private readonly Dictionary<int, CachedScrollingMesh> cachedScrollingMeshes = new Dictionary<int, CachedScrollingMesh>();
        private readonly HashSet<int> scrollingRendererIds = new HashSet<int>();
        private readonly List<MeshRenderer> cachedScrollingRenderers = new List<MeshRenderer>();
        
        // Cached baked meshes for skinned renderers
        private Dictionary<int, Mesh> bakedMeshes = new Dictionary<int, Mesh>();
        private Dictionary<int, Matrix4x4> lastSkinnedTransforms = new Dictionary<int, Matrix4x4>();
        
        // Thread-safe renderer snapshots for UI
        private LayerSnapshot[] _layerSnapshots = Array.Empty<LayerSnapshot>();
        
        // User-disabled layers and individual renderers. Checked during capture.
        private readonly HashSet<int> _disabledLayers = new HashSet<int>();
        private readonly HashSet<int> _disabledRendererIds = new HashSet<int>();
        private readonly object _disabledLock = new object();
        
        /// <summary>
        /// Immutable snapshot of a Unity layer with its renderers.
        /// </summary>
        public struct LayerSnapshot
        {
            public int LayerIndex;
            public string LayerName;
            public int StaticCount;
            public int SkinnedCount;
            public bool UserDisabled;
        }
        
        /// <summary>
        /// Immutable snapshot of a single renderer.
        /// </summary>
        public struct RendererSnapshot
        {
            public int InstanceId;
            public string Name;
            public string Type; // "Static" or "Skinned"
            public int Layer;
        }
        
        // Full renderer list kept alongside layers for drill-down
        private RendererSnapshot[] _rendererSnapshots = Array.Empty<RendererSnapshot>();
        
        /// <summary>
        /// Current layer snapshots. Safe to read from any thread.
        /// </summary>
        public LayerSnapshot[] LayerSnapshots => Volatile.Read(ref _layerSnapshots);
        
        /// <summary>
        /// Current renderer snapshots. Safe to read from any thread.
        /// </summary>
        public RendererSnapshot[] RendererSnapshots => Volatile.Read(ref _rendererSnapshots);
        
        /// <summary>
        /// Set whether a layer is disabled by the user.
        /// </summary>
        public void SetLayerDisabled(int layerIndex, bool disabled)
        {
            lock (_disabledLock)
            {
                if (disabled)
                    _disabledLayers.Add(layerIndex);
                else
                    _disabledLayers.Remove(layerIndex);
            }
        }
        
        /// <summary>
        /// Check if a layer is user-disabled.
        /// </summary>
        // Layers some game camera draws (SetGameLayerMask); others hold trigger volumes and
        // helper geometry the game never shows. -1 until known.
        private volatile int gameLayerMask = -1;

        // This frame's 3D skybox (GameProfile.GetSkyboxView; main thread): the layers only its
        // camera draws, and where their geometry stands in the world.
        private int skyLayers;
        private Matrix4x4 skyToWorld = Matrix4x4.identity;
        private int loggedSkyLayers = -1;

        private bool IsSkyLayer(int layer) => (uint)layer < 32 && (skyLayers & (1 << layer)) != 0;

        // A sky layer's transform placed in the world (else as it is).
        private Matrix4x4 SkyPlaced(int layer, Matrix4x4 localToWorld) =>
            IsSkyLayer(layer) ? skyToWorld * localToWorld : localToWorld;

        // Its layers are those the world camera does not draw: both draw some (the player's
        // light, effects), which stay the world's.
        // The world camera's far plane reaches as far as the sky camera's does, placed.
        private void CaptureSkybox(Camera worldCamera, ref CameraData camera)
        {
            bool found = Games.GameProfiles.GetSkyboxView(worldCamera, out var view);
            skyLayers = found ? GameCameraMasks.Get(view.camera) & ~GameCameraMasks.Get(worldCamera) : 0;
            skyToWorld = found ? view.ToWorld : Matrix4x4.identity;
            if (skyLayers != 0)
                camera.farPlane = Mathf.Max(camera.farPlane, view.camera.farClipPlane * view.scale);
            SceneMeshScanner.SkyLayers = skyLayers;
            SceneMeshScanner.SkyToWorld = skyToWorld;
            if (skyLayers == loggedSkyLayers)
                return;
            loggedSkyLayers = skyLayers;
            var names = new List<string>();
            for (int layer = 0; layer < 32; layer++)
                if ((skyLayers & (1 << layer)) != 0)
                    names.Add($"{LayerMask.LayerToName(layer)}({layer})");
            logger.LogInfo(found
                ? $"[Sky] 3D skybox camera '{view.camera.name}' (mask 0x{GameCameraMasks.Get(view.camera):X8}, world 0x{GameCameraMasks.Get(worldCamera):X8}), " +
                  $"origin {view.origin}, scale {view.scale}: sky layers {(names.Count > 0 ? string.Join(", ", names) : "none")}"
                : "[Sky] no 3D skybox");
            // Once per change: what the sky holds.
            if (skyLayers == 0)
                return;
            int listed = 0;
            foreach (var r in UnityEngine.Object.FindObjectsOfType<Renderer>())
            {
                if ((skyLayers & (1 << r.gameObject.layer)) == 0 || listed++ >= 40)
                    continue;
                var m = r.sharedMaterial;
                logger.LogInfo($"[Sky]   '{r.name}' ({r.GetType().Name}, mesh '{r.GetComponent<MeshFilter>()?.sharedMesh?.name}'), material '{m?.name}' " +
                               $"shader '{m?.shader?.name}' queue {m?.renderQueue}, keywords [{(m != null ? string.Join(" ", m.shaderKeywords) : "")}], " +
                               $"scale {r.transform.lossyScale}, bounds {r.bounds.center} +- {r.bounds.extents}");
            }
        }

        public void SetGameLayerMask(int mask)
        {
            if (mask == 0) mask = -1;
            // Less the layers the game's profile says it never shows in the world (a minimap's
            // icons, drawn by its map camera only).
            for (int layer = 0; layer < 32; layer++)
                if ((mask & (1 << layer)) != 0 && Games.GameProfiles.IsNeverRenderedLayer(layer))
                    mask &= ~(1 << layer);
            if (mask == gameLayerMask) return;
            gameLayerMask = mask;
            var hidden = new List<string>();
            for (int layer = 0; layer < 32; layer++)
            {
                string name = LayerMask.LayerToName(layer);
                if ((mask & (1 << layer)) == 0 && !string.IsNullOrEmpty(name))
                    hidden.Add($"{name}({layer})");
            }
            logger.LogInfo($"[Layers] Game cameras draw 0x{mask:X8}; not captured: {(hidden.Count > 0 ? string.Join(", ", hidden) : "none")}");
        }

        public bool IsLayerDisabled(int layerIndex)
        {
            if ((uint)layerIndex < 32 && (gameLayerMask & (1 << layerIndex)) == 0)
                return true;
            lock (_disabledLock)
            {
                return _disabledLayers.Contains(layerIndex);
            }
        }

        public void SetRendererDisabled(int instanceId, bool disabled)
        {
            lock (_disabledLock)
            {
                if (disabled)
                    _disabledRendererIds.Add(instanceId);
                else
                    _disabledRendererIds.Remove(instanceId);
            }
        }

        public bool IsRendererDisabled(int instanceId)
        {
            lock (_disabledLock)
            {
                return _disabledRendererIds.Contains(instanceId);
            }
        }

        /// <summary>
        /// Serializes disabled layers as a comma-separated string for config persistence.
        /// </summary>
        public string GetDisabledLayersString()
        {
            lock (_disabledLock)
            {
                if (_disabledLayers.Count == 0) return "";
                var sb = new System.Text.StringBuilder();
                bool first = true;
                foreach (int layer in _disabledLayers)
                {
                    if (!first) sb.Append(',');
                    sb.Append(layer);
                    first = false;
                }
                return sb.ToString();
            }
        }

        /// <summary>
        /// Restores disabled layers from a comma-separated string.
        /// </summary>
        public void LoadDisabledLayersString(string csv)
        {
            lock (_disabledLock)
            {
                _disabledLayers.Clear();
                if (string.IsNullOrEmpty(csv)) return;
                foreach (var token in csv.Split(new[] { ',' }))
                {
                    if (int.TryParse(token.Trim(), out int layer))
                        _disabledLayers.Add(layer);
                }
            }
        }
        
        // Mesh creation queue with pre-extracted geometry data
        private Queue<PreparedMeshData> meshesToCreate = new Queue<PreparedMeshData>();
        private Queue<PreparedMeshData> priorityMeshesToCreate = new Queue<PreparedMeshData>(); // High-priority queue for viewmodel/weapon meshes
        private HashSet<ulong> meshesInQueue = new HashSet<ulong>(); // Track which mesh keys are already queued
        private HashSet<ulong> failedMeshKeys = new HashSet<ulong>();
        private readonly object meshQueueLock = new object(); // Synchronize main thread enqueue + render thread dequeue
        private MaterialPropertyBlock sharedStaticMpb = null;

        // --- Diagnostic getters for debug HUD ---
        public int FailedMeshCount { get { lock (meshQueueLock) return failedMeshKeys.Count; } }
        public int PendingMeshQueueCount { get { lock (meshQueueLock) return meshesToCreate.Count + priorityMeshesToCreate.Count; } }
        public int PersistentStaticCount { get { lock (persistentStaticLock) return persistentStaticInstances.Count; } }
        public int CachedStaticRendererCount => cachedRenderers.Count;
        public int CachedSkinnedRendererCount => cachedSkinnedRenderers.Count;

        // A game's view portal as a Remix ray portal pair (drawn by the render thread).
        private RayPortals rayPortals;
        internal RayPortals RayPortals => rayPortals ?? (rayPortals = new RayPortals(logger));
        private readonly List<Games.PortalPair> framePortals = new List<Games.PortalPair>();

        public void QueuePriorityMesh(PreparedMeshData data)
        {
            if (data == null) return;
            ulong meshKey = data.MeshKey != 0 ? data.MeshKey : (ulong)(uint)data.MeshId;
            lock (meshQueueLock)
            {
                if (!meshConverter.IsMeshCached(meshKey) && !meshesInQueue.Contains(meshKey))
                {
                    priorityMeshesToCreate.Enqueue(data);
                    meshesInQueue.Add(meshKey);
                }
            }
        }

        // Layers only the game's view-model camera draws (ULTRAKILL's HUD Camera; 0 without
        // one), from the masks the game set (GameCameraMasks). What is on them the game draws
        // from that camera over the scene, wherever it sits in the hierarchy: a held item, a
        // weapon's gauge, ejected shells. Main thread, set each frame in CaptureStaticMeshes.
        private int viewModelLayers;
        private Func<Renderer, bool> isPlayerBodyRenderer;
        private int loggedViewModelLayers = -1;
        private readonly HashSet<int> loggedLayerViewModels = new HashSet<int>();

        // Drawn as a view model: under the game camera, or on a layer only the view-model
        // camera draws.
        private bool IsViewModel(Transform tr, int layer, Transform mainCamTransform, string kind)
        {
            if (mainCamTransform != null && tr.IsChildOf(mainCamTransform))
                return true;
            if ((viewModelLayers & (1 << layer)) == 0)
                return false;
            if (loggedLayerViewModels.Add(tr.GetInstanceID()) && loggedLayerViewModels.Count <= 100)
                logger.LogInfo($"[ViewModel] {kind} '{GetHierarchyPath(tr)}' on layer {layer} ({LayerMask.LayerToName(layer)}): a view model by its layer, not under the camera");
            return true;
        }

        private static string GetHierarchyPath(Transform t)
        {
            var path = t.name;
            for (var p = t.parent; p != null; p = p.parent)
                path = p.name + "/" + path;
            return path;
        }

        private void UpdateViewModelLayers(Camera mainCam, Camera viewModelCam)
        {
            viewModelLayers = viewModelCam != null && viewModelCam != mainCam
                ? GameCameraMasks.Get(viewModelCam) & ~GameCameraMasks.Get(mainCam)
                : 0;
            SceneMeshScanner.ViewModelLayers = viewModelLayers;
            SceneMeshScanner.ViewModelRoot = mainCam != null ? mainCam.transform : null;
            SceneMeshScanner.IsPlayerBody = isPlayerBodyRenderer ??= IsPlayerBodyRenderer;
            if (viewModelLayers == loggedViewModelLayers)
                return;
            loggedViewModelLayers = viewModelLayers;
            var names = new List<string>();
            for (int l = 0; l < 32; l++)
                if ((viewModelLayers & (1 << l)) != 0)
                    names.Add($"{l} {LayerMask.LayerToName(l)}");
            logger.LogInfo($"[ViewModel] layers only '{(viewModelCam != null ? viewModelCam.name : "none")}' draws: {(names.Count > 0 ? string.Join(", ", names) : "none")}");
        }

        // GameProfile.IsPlayerBody, asked once per renderer.
        private bool IsPlayerBodyRenderer(Renderer renderer)
        {
            int id = renderer.GetInstanceID();
            if (!playerBodyRenderers.TryGetValue(id, out bool body))
                playerBodyRenderers[id] = body = Games.GameProfiles.IsPlayerBody(renderer);
            return body;
        }

        // A renderer the game rewrote for its own batching is the scene scan's too: it draws the
        // original geometry (GameProfile.GetOriginalGeometry). Asked once a renderer.
        private readonly Dictionary<int, bool> restoredRenderers = new Dictionary<int, bool>();
        // Filters left to the scan that it had not read, for which a rescan was asked.
        private readonly HashSet<int> rescanAsked = new HashSet<int>();

        // Geometry left to the scan (a static batch's slice, a rewritten renderer), which may not
        // have it: a room a checkpoint cloned after the scan's rescans ended. Asked once a filter,
        // only while active (the scan skips inactive objects and reads them once active).
        private void AskScan(MeshFilter filter, GameObject go)
        {
            var scanner = SceneMeshScanner.Current;
            if (scanner == null || filter == null || go == null || !go.activeInHierarchy || scanner.HasScanned(filter) ||
                !rescanAsked.Add(filter.GetInstanceID()))
                return;
            scanner.RequestRescan();
            if (VerboseLog.On)
                logger.LogInfo($"[StaticCapture] '{go.name}' is the scan's but not scanned: rescan asked");
        }

        private bool ShouldPreferSceneScan(MeshRenderer renderer, Mesh mesh)
        {
            if (renderer == null || mesh == null)
                return false;
            if (renderer.isPartOfStaticBatch)
                return true;
            int id = renderer.GetInstanceID();
            if (!restoredRenderers.TryGetValue(id, out bool restored))
                restoredRenderers[id] = restored = Games.GameProfiles.GetOriginalGeometry(renderer, out _, out _);
            return restored;
        }

        public StaticGeometryStats GetStaticGeometryStats(SceneMeshScanner scanner)
        {
            var stats = new StaticGeometryStats();
            var claimedRendererIds = new HashSet<int>();
            var visibleKeys = new HashSet<StaticGeometryKey>();
            var rendererKeys = new HashSet<StaticGeometryKey>();

            for (int i = 0; i < cachedRenderers.Count; i++)
            {
                var renderer = cachedRenderers[i];
                if (renderer == null)
                    continue;

                var meshFilter = renderer.GetComponent<MeshFilter>();
                var mesh = meshFilter != null ? meshFilter.sharedMesh : null;
                if (mesh == null)
                    continue;

                var key = StaticGeometryDedupe.BuildKey(renderer, mesh);
                if (!key.IsValid)
                    continue;

                stats.RawStaticRenderers++;
                if (rendererKeys.Add(key))
                    stats.DedupedStaticRenderers++;
                else
                    stats.SuppressedStaticRenderers++;

                if (ShouldPreferSceneScan(renderer, mesh))
                    continue;

                if (!renderer.enabled || !renderer.gameObject.activeInHierarchy)
                    continue;
                if (IsLayerDisabled(renderer.gameObject.layer))
                    continue;
                if (IsRendererDisabled(renderer.GetInstanceID()))
                    continue;
                int matSig = StaticGeometryDedupe.ComputeMaterialSignature(renderer.sharedMaterials);
                ulong meshKey = RemixMeshConverter.GetMeshKey(mesh.GetInstanceID(), matSig);
                if (!meshConverter.IsMeshCached(meshKey))
                    continue;

                stats.RawStaticMeshes++;
                if (StaticGeometryDedupe.TryClaimVisibleInstance(renderer.GetInstanceID(), key, claimedRendererIds, visibleKeys))
                    stats.DedupedStaticMeshes++;
                else
                    stats.SuppressedStaticMeshes++;
            }

            if (configPersistDisabledRenderers.Value)
            {
                PersistentStaticInstance[] persistentCopy = null;
                lock (persistentStaticLock)
                {
                    if (persistentStaticInstances.Count > 0)
                    {
                        persistentCopy = new PersistentStaticInstance[persistentStaticInstances.Count];
                        persistentStaticInstances.Values.CopyTo(persistentCopy, 0);
                    }
                }

                if (persistentCopy != null)
                {
                    for (int pIdx = 0; pIdx < persistentCopy.Length; pIdx++)
                    {
                        var entry = persistentCopy[pIdx];
                        if (entry.renderer == null)
                            continue;
                        if (entry.renderer.enabled && entry.renderer.gameObject.activeInHierarchy)
                            continue;
                        var meshFilter = entry.renderer.GetComponent<MeshFilter>();
                        var mesh = meshFilter != null ? meshFilter.sharedMesh : null;
                        if (ShouldPreferSceneScan(entry.renderer, mesh))
                            continue;
                        if (IsLayerDisabled(entry.renderer.gameObject.layer))
                            continue;
                        if (!meshConverter.IsMeshCached(entry.meshKey))
                            continue;

                        stats.RawStaticMeshes++;
                        if (StaticGeometryDedupe.TryClaimVisibleInstance(entry.renderer.GetInstanceID(), entry.dedupeKey, claimedRendererIds, visibleKeys))
                            stats.DedupedStaticMeshes++;
                        else
                            stats.SuppressedStaticMeshes++;
                    }
                }
            }

            if (scanner != null)
            {
                var scannerEntries = scanner.GetDedupeEntries();
                for (int i = 0; i < scannerEntries.Length; i++)
                {
                    stats.RawSceneScanInstances++;
                    if (StaticGeometryDedupe.TryClaimVisibleInstance(scannerEntries[i].RendererInstanceId, scannerEntries[i].DedupeKey, claimedRendererIds, visibleKeys))
                        stats.DedupedSceneScanInstances++;
                    else
                        stats.SuppressedSceneScanInstances++;
                }
            }

            stats.DedupedVisibleStaticTotal = stats.DedupedStaticMeshes + stats.DedupedSceneScanInstances;
            return stats;
        }

        /// <summary>
        /// Debug entry for per-mesh 3D box overlay. Collected on main thread only when HUD is visible.
        /// </summary>
        public struct DebugMeshEntry
        {
            public string Name;
            public string MeshName;
            public int MeshId;
            public int LayerIndex;
            public int RendererInstanceId;
            public string LayerName;
            public string Origin;       // "Runtime", "Skinned", "Scanner", "Persistent"
            public string MaterialName;
            public ulong TextureHash;
            public bool Failed;
            public bool NoTexture;
            public Vector3 BoundsCenter;
            public Vector3 BoundsExtents;
        }

        /// <summary>
        /// Collect per-mesh debug entries from cached renderers. Only call when debug HUD is visible.
        /// </summary>
        public List<DebugMeshEntry> CollectDebugMeshEntries()
        {
            var entries = new List<DebugMeshEntry>(cachedRenderers.Count + cachedSkinnedRenderers.Count);

            // Static renderers (runtime capture path)
            for (int i = 0; i < cachedRenderers.Count; i++)
            {
                var r = cachedRenderers[i];
                if (r == null) continue;
                if (!r.enabled || !r.gameObject.activeInHierarchy) continue;

                var mf = r.GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null) continue;

                int meshId = mf.sharedMesh.GetInstanceID();
                int matSig = StaticGeometryDedupe.ComputeMaterialSignature(r.sharedMaterials);
                ulong meshKey = RemixMeshConverter.GetMeshKey(meshId, matSig);
                bool failed = failedMeshKeys.Contains(meshKey);
                bool cached = meshConverter.IsMeshCached(meshKey);
                if (!failed && !cached) continue; // still queued, not interesting yet

                var entry = new DebugMeshEntry
                {
                    Name = r.gameObject.name,
                    MeshName = mf.sharedMesh.name,
                    MeshId = meshId,
                    LayerIndex = r.gameObject.layer,
                    RendererInstanceId = r.GetInstanceID(),
                    LayerName = LayerMask.LayerToName(r.gameObject.layer),
                    Origin = "Runtime",
                    Failed = failed,
                    BoundsCenter = r.bounds.center,
                    BoundsExtents = r.bounds.extents,
                };

                // Resolve material/texture info
                ResolveMaterialInfo(ref entry, r.sharedMaterial);
                entries.Add(entry);
            }

            // Skinned renderers
            for (int i = 0; i < cachedSkinnedRenderers.Count; i++)
            {
                var sr = cachedSkinnedRenderers[i];
                if (sr == null) continue;
                if (!sr.enabled || !sr.gameObject.activeInHierarchy) continue;
                if (sr.sharedMesh == null) continue;

                var entry = new DebugMeshEntry
                {
                    Name = sr.gameObject.name,
                    MeshName = sr.sharedMesh.name,
                    MeshId = sr.sharedMesh.GetInstanceID(),
                    LayerIndex = sr.gameObject.layer,
                    RendererInstanceId = sr.GetInstanceID(),
                    LayerName = LayerMask.LayerToName(sr.gameObject.layer),
                    Origin = "Skinned",
                    BoundsCenter = sr.bounds.center,
                    BoundsExtents = sr.bounds.extents,
                };

                ResolveMaterialInfo(ref entry, sr.sharedMaterial);
                entries.Add(entry);
            }

            // Persistent disabled renderers
            PersistentStaticInstance[] persistentCopy = null;
            lock (persistentStaticLock)
            {
                if (persistentStaticInstances.Count > 0)
                {
                    persistentCopy = new PersistentStaticInstance[persistentStaticInstances.Count];
                    persistentStaticInstances.Values.CopyTo(persistentCopy, 0);
                }
            }

            if (persistentCopy != null)
            {
                for (int pIdx = 0; pIdx < persistentCopy.Length; pIdx++)
                {
                    var p = persistentCopy[pIdx];
                    if (p.renderer == null) continue;
                    if (p.renderer.enabled && p.renderer.gameObject.activeInHierarchy) continue;

                    var entry = new DebugMeshEntry
                    {
                        Name = p.renderer.gameObject.name,
                        MeshName = "",
                        MeshId = p.meshId,
                        LayerIndex = p.renderer.gameObject.layer,
                        RendererInstanceId = p.renderer.GetInstanceID(),
                        LayerName = LayerMask.LayerToName(p.renderer.gameObject.layer),
                        Origin = "Persistent",
                        BoundsCenter = p.renderer.bounds.center,
                        BoundsExtents = p.renderer.bounds.extents,
                    };

                    ResolveMaterialInfo(ref entry, p.renderer.sharedMaterial);
                    entries.Add(entry);
                }
            }

            return entries;
        }

        private void ResolveMaterialInfo(ref DebugMeshEntry entry, Material mat)
        {
            if (mat == null)
            {
                entry.MaterialName = "(null)";
                entry.NoTexture = true;
                return;
            }

            entry.MaterialName = mat.name;
            int matId = mat.GetInstanceID();
            if (materialManager != null && materialManager.TryGetMaterialData(matId, out var matData))
            {
                entry.TextureHash = matData.albedoTextureHash;
                entry.NoTexture = matData.albedoHandle == IntPtr.Zero;
            }
            else
            {
                entry.NoTexture = true;
            }
        }
        
        private int skinnedCaptureCount = 0;
        private int staticCaptureCount = 0;
        
        // Persistent static instance cache: remembers transforms of disabled MeshRenderers
        // so objects that get deactivated (e.g. CyberGrind cubes after wave settles) keep drawing
        private struct PersistentStaticInstance
        {
            public MeshRenderer renderer; // weak ref via Unity object — becomes null when destroyed
            public ulong meshKey;
            public int meshId;
            public Matrix4x4 localToWorld;
            public StaticGeometryKey dedupeKey;
            public int drawnFrame; // frame the main capture loop last drew it (active that frame)
        }
        private readonly object persistentStaticLock = new object();
        private Dictionary<int, PersistentStaticInstance> persistentStaticInstances = new Dictionary<int, PersistentStaticInstance>();
        // Per renderer: whether the game toggles it by deactivation (GameProfiles.FollowsActiveState),
        // so it is never persisted. Asked once per renderer.
        private readonly Dictionary<int, bool> followsActiveState = new Dictionary<int, bool>();
        private int skinnedRoundRobinIndex = 0; // rotates through cachedSkinnedRenderers each frame (BakeMesh fallback)
        
        // Persistent cache: last-baked data for every skinned mesh, so all are drawn every frame
        private Dictionary<int, SkinnedMeshData> persistentSkinnedData = new Dictionary<int, SkinnedMeshData>();
        
        
        // Cached GPU skinning data per sharedMesh + blendshape signature (bind-pose vertices + bone weights)
        private Dictionary<long, CachedSkinningData> cachedSkinning = new Dictionary<long, CachedSkinningData>(); // keyed by (sharedMeshId << 32) | blendShapeHash
        private readonly Dictionary<int, CachedSkinningData> skinningBases = new Dictionary<int, CachedSkinningData>(); // per sharedMesh, no blendshapes; null: bake only
        private readonly List<long> reusableStaleSkinningKeys = new List<long>();
        private const int OffscreenBakeInterval = 4;
        private readonly Plane[] skinnedFrustumPlanes = new Plane[6];
        private int offscreenBakeSkips;
        


        // Frame state structures
        public struct CameraData
        {
            public Vector3 position;
            public Vector3 forward;
            public Vector3 up;
            public Vector3 right;
            public float fov;
            public float aspect;
            public float nearPlane;
            public float farPlane;
            public bool valid;
            // The view-model camera (REMIXAPI_CAMERA_TYPE_VIEW_MODEL): the game's own when its
            // GameProfile names one, else the world camera's.
            public Vector3 viewModelPosition, viewModelForward, viewModelUp, viewModelRight;
            public float viewModelFov, viewModelNearPlane, viewModelFarPlane;
            // The game's view portal this frame (GameProfile.GetRayPortalView; RayPortals).
            public bool portalActive;
            public Games.RayPortalView portal;
            // The game's portals this frame, nearest first (GameProfile.GetPortals), or null.
            public Games.PortalPair[] portals;
        }
        
        public struct MeshInstanceData
        {
            public ulong meshKey;
            public int meshId;
            public Matrix4x4 localToWorld;
            public int rendererInstanceId;
            public StaticGeometryKey dedupeKey;
            public uint categoryFlags;
        }
        
        // What produced a per-frame mesh, for the render thread's upload statistics.
        // Game: a GameProfile's own meshes (Games/).
        public enum DynamicSource : byte { Skinned, Scrolling, WorldUI, Billboard, MeshInstanced, MeshBaked, ParticleTrail, Trail, Sprite, Game, Rewritten, Count }

        public struct SkinnedMeshData
        {
            public DynamicSource source;
            public int meshId;
            public ulong remixMeshHash;
            public int materialId;  // Unity material instance ID
            public Vector3[] vertices;
            public Vector3[] normals;
            public Vector2[] uvs;
            public Color32[] colors;
            public int[] triangles;
            public Matrix4x4 localToWorld;
            // GPU skinning: bone transforms per frame (null = software skinned / BakeMesh fallback)
            public Matrix4x4[] boneTransforms;
            // GPU skinning: bind-pose data + weights cached per sharedMesh (null = BakeMesh fallback)
            public CachedSkinningData skinningData;
            public uint categoryFlags;
            // Multiplicative paint (with a decal category): drawn blending DST_COLOR * ZERO.
            public bool multiplyPaint;
            // Only camera rays meet it (RemixMeshConverter.IsCameraOnly: a scrolling sky dome).
            public bool cameraOnly;
            public float glowFade;  // an additive glow's live strength over its captured one (0: unset, 1)
            public Vector4 uvST;
            public int bakeFrame;
            // Optional: the vertices this entry's triangles use (triangles index into it).
            // Lets one submesh of a multi-material baked mesh upload only its own vertices.
            public int[] vertexMap;
            // Nonzero: the arrays are reused buffers (ParticleBuffers) and only this many
            // vertices / indices belong to the entry.
            public int usedVertexCount;
            public int usedIndexCount;
        }

        /// <summary>
        /// One-time cached data per sharedMesh for GPU skinning: bind-pose geometry + bone weights.
        /// </summary>
        public class CachedSkinningData
        {
            public Vector3[] bindVertices;
            public Vector3[] bindNormals;
            public Vector2[] uvs;
            public Color32[] colors;
            public int[] triangles;             // merged all-submesh triangles (legacy / BakeMesh fallback)
            public int[][] submeshTriangles;    // per-submesh triangle arrays for multi-material rendering
            public float[] blendWeights;    // bonesPerVertex * vertexCount
            public uint[] blendIndices;     // bonesPerVertex * vertexCount
            public int bonesPerVertex;
            public int boneCount;
            public Matrix4x4[] bindPoses;
            public bool meshCreated;        // true after Remix mesh has been created on render thread
            public int lastUsedFrame;       // PruneSkinningCache
            internal SkinnedMeshGpuReader.SparseShape[] sparseBlendShapes; // non-readable meshes: blendshape deltas read from the GPU
        }
        
        public class FrameState
        {
            public CameraData camera;
            public List<MeshInstanceData> instances = new List<MeshInstanceData>();
            public List<SkinnedMeshData> skinned = new List<SkinnedMeshData>();
            // Light shafts drawn as glowing air (Conversion/LightShafts), Remix space.
            public List<RemixAPI.remixapi_VolumeShaftEXT> shafts = new List<RemixAPI.remixapi_VolumeShaftEXT>();
            public int frameCount;
        }
        
        public RemixFrameCapture(
            ManualLogSource logger,
            RemixCameraHandler cameraHandler,
            RemixMeshConverter meshConverter,
            RemixMaterialManager materialManager,
            ConfigEntry<bool> useDistanceCulling,
            ConfigEntry<float> maxRenderDistance,
            ConfigEntry<bool> useVisibilityCulling,
            ConfigEntry<int> rendererCacheDuration,
            ConfigEntry<int> debugLogInterval,
            ConfigEntry<bool> captureStaticMeshes,
            ConfigEntry<bool> captureSkinnedMeshes,
            ConfigEntry<bool> hardwareSkinning,
            ConfigEntry<bool> persistDisabledRenderers,
            ConfigEntry<int> staticMeshFrameSkip = null,
            ConfigEntry<bool> captureParticles = null,
            ConfigEntry<bool> enableParticleDistanceCulling = null,
            ConfigEntry<float> particleMaxDistance = null)
        {
            this.logger = logger;
            this.cameraHandler = cameraHandler;
            this.meshConverter = meshConverter;
            this.materialManager = materialManager;
            this.configUseDistanceCulling = useDistanceCulling;
            this.configMaxRenderDistance = maxRenderDistance;
            this.configUseVisibilityCulling = useVisibilityCulling;
            this.configRendererCacheDuration = rendererCacheDuration;
            this.configDebugLogInterval = debugLogInterval;
            this.configCaptureStaticMeshes = captureStaticMeshes;
            this.configCaptureSkinnedMeshes = captureSkinnedMeshes;
            this.configCaptureParticles = captureParticles;
            this.configEnableParticleDistanceCulling = enableParticleDistanceCulling;
            this.configParticleMaxDistance = particleMaxDistance;
            this.configHardwareSkinning = hardwareSkinning;
            this.configPersistDisabledRenderers = persistDisabledRenderers;
            this.configStaticMeshFrameSkip = staticMeshFrameSkip;
        }
        
        private class CachedStaticRendererInfo
        {
            public int rendererId;
            public MeshFilter filter;
            public Transform transform;
            public GameObject gameObject;
            public Material primarySharedMaterial;
            public Material[] sharedMaterials;
            public int materialSignature;
            public bool isPartOfStaticBatch;
            // Its first material is a runtime copy (renderer.material: "name (Instance)"), whose
            // colour a script may animate (RemixFrameCapture.ReadLiveColours).
            public bool runtimeMaterial;
        }

        private class CachedSkinnedRendererInfo
        {
            public int rendererId;
            public int hierarchyHash;
            public Transform transform;
            public GameObject gameObject;
            public Animator animator;
            // GetComponentInParent skips inactive objects: a renderer cached while inactive
            // (enemies waiting for their wave) looks its animator up once it is active.
            public bool animatorResolved;
            public Material primarySharedMaterial;
            public Material[] sharedMaterials;
            public Transform[] bones;
            public ulong[] boneNameHashes;
            public Matrix4x4[] boneMatricesA;
            public Matrix4x4[] boneMatricesB;
            public SkeletonPose skeleton;
        }

        private readonly Dictionary<int, CachedStaticRendererInfo> cachedStaticInfo = new Dictionary<int, CachedStaticRendererInfo>(1024);
        private readonly Dictionary<int, CachedSkinnedRendererInfo> cachedSkinnedInfo = new Dictionary<int, CachedSkinnedRendererInfo>(128);
        private readonly HashSet<int> reusableValidSkinnedIds = new HashSet<int>();
        private readonly HashSet<int> overlaySkinnedIds = new HashSet<int>();
        private readonly HashSet<long> loggedSkinPasses = new HashSet<long>(); // CaptureSkinnedMeshes: a later skin of the same mesh and skeleton
        private Dictionary<long, (int lowestQueue, int count)> skinPassesLast = new Dictionary<long, (int, int)>(),
                                                               skinPassesNext = new Dictionary<long, (int, int)>();
        private readonly HashSet<int> viewModelSkinnedIds = new HashSet<int>(); // CaptureSkinnedMeshes: under the camera
        // The player's body (GameProfile.IsPlayerBody, asked once per renderer): reflection-only.
        private readonly HashSet<int> playerBodySkinnedIds = new HashSet<int>();
        private readonly Dictionary<int, bool> playerBodyRenderers = new Dictionary<int, bool>();
        // Skinned id (CachedSkinnedRendererInfo.hierarchyHash) -> the renderer holding it; kept
        // across renderer cache refreshes so twins never swap ids, cleared on scene load.
        private readonly Dictionary<int, SkinnedMeshRenderer> skinnedIdOwners = new Dictionary<int, SkinnedMeshRenderer>();
        private readonly List<(int idx, SkinnedMeshRenderer smr, int skinnedId, Matrix4x4 matrix, int matId)> reusableBakeFallbackQueue = new List<(int idx, SkinnedMeshRenderer smr, int skinnedId, Matrix4x4 matrix, int matId)>();
        private readonly List<int> reusableStaleSkinnedKeys = new List<int>();
        private Camera lastTrackedCamera;
        private int lastCameraTrackFrame = -100;

        private static bool IsRuntimeMaterial(Material m) =>
            m != null && m.name.EndsWith(" (Instance)", StringComparison.Ordinal);

        private CachedStaticRendererInfo GetOrCreateStaticInfo(MeshRenderer r)
        {
            int id = r.GetInstanceID();
            if (!cachedStaticInfo.TryGetValue(id, out var info))
            {
                var mats = r.sharedMaterials;
                info = new CachedStaticRendererInfo
                {
                    rendererId = id,
                    filter = r.GetComponent<MeshFilter>(),
                    transform = r.transform,
                    gameObject = r.gameObject,
                    primarySharedMaterial = (mats != null && mats.Length > 0) ? mats[0] : null,
                    sharedMaterials = mats,
                    materialSignature = StaticGeometryDedupe.ComputeMaterialSignature(mats),
                    isPartOfStaticBatch = r.isPartOfStaticBatch,
                    runtimeMaterial = IsRuntimeMaterial((mats != null && mats.Length > 0) ? mats[0] : null)
                };
                cachedStaticInfo[id] = info;
                return info;
            }

            if (info.filter == null)
                info.filter = r.GetComponent<MeshFilter>();

            var currentPrimary = r.sharedMaterial;
            if (!ReferenceEquals(currentPrimary, info.primarySharedMaterial))
            {
                var mats = r.sharedMaterials;
                info.primarySharedMaterial = currentPrimary;
                info.sharedMaterials = mats;
                info.materialSignature = StaticGeometryDedupe.ComputeMaterialSignature(mats);
                info.runtimeMaterial = IsRuntimeMaterial(currentPrimary);
            }

            return info;
        }

        // The bones' world matrices for one frame, shared by every renderer of the same
        // skeleton (a character's body, arms, face, clothes...).
        private sealed class SkeletonPose
        {
            public Transform[] bones;
            public Matrix4x4[] world;
            public bool[] present;
            public int frame = -1;
        }
        private readonly Dictionary<ulong, List<SkeletonPose>> skeletonPoses = new Dictionary<ulong, List<SkeletonPose>>();
        private readonly Dictionary<int, ulong> skinnedMeshHashes = new Dictionary<int, ulong>();

        private SkeletonPose GetSkeletonPose(CachedSkinnedRendererInfo info, int frameCount)
        {
            var pose = info.skeleton;
            if (pose == null)
            {
                var bones = info.bones;
                ulong key = 14695981039346656037UL;
                for (int b = 0; b < bones.Length; b++)
                {
                    key ^= (ulong)(uint)(bones[b] != null ? bones[b].GetInstanceID() : 0);
                    key *= 1099511628211UL;
                }
                if (!skeletonPoses.TryGetValue(key, out var candidates))
                {
                    candidates = new List<SkeletonPose>(1);
                    skeletonPoses[key] = candidates;
                }
                foreach (var candidate in candidates)
                {
                    if (SameBones(candidate.bones, bones)) { pose = candidate; break; }
                }
                if (pose == null)
                {
                    pose = new SkeletonPose
                    {
                        bones = bones,
                        world = new Matrix4x4[bones.Length],
                        present = new bool[bones.Length]
                    };
                    candidates.Add(pose);
                }
                info.skeleton = pose;
            }
            if (pose.frame != frameCount)
            {
                pose.frame = frameCount;
                var bones = pose.bones;
                for (int b = 0; b < bones.Length; b++)
                {
                    var bone = bones[b];
                    bool present = bone != null;
                    pose.present[b] = present;
                    if (present)
                        pose.world[b] = bone.localToWorldMatrix;
                }
            }
            return pose;
        }

        private static bool SameBones(Transform[] a, Transform[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
            {
                if (!ReferenceEquals(a[i], b[i])) return false;
            }
            return true;
        }

        // r = a * b where a's bottom row is (0, 0, 0, 1), as for transforms and their inverses;
        // the same sums as Matrix4x4's operator*, without copying the operands.
        private static void MulAffine(ref Matrix4x4 a, ref Matrix4x4 b, out Matrix4x4 r)
        {
            r.m00 = a.m00 * b.m00 + a.m01 * b.m10 + a.m02 * b.m20 + a.m03 * b.m30;
            r.m01 = a.m00 * b.m01 + a.m01 * b.m11 + a.m02 * b.m21 + a.m03 * b.m31;
            r.m02 = a.m00 * b.m02 + a.m01 * b.m12 + a.m02 * b.m22 + a.m03 * b.m32;
            r.m03 = a.m00 * b.m03 + a.m01 * b.m13 + a.m02 * b.m23 + a.m03 * b.m33;
            r.m10 = a.m10 * b.m00 + a.m11 * b.m10 + a.m12 * b.m20 + a.m13 * b.m30;
            r.m11 = a.m10 * b.m01 + a.m11 * b.m11 + a.m12 * b.m21 + a.m13 * b.m31;
            r.m12 = a.m10 * b.m02 + a.m11 * b.m12 + a.m12 * b.m22 + a.m13 * b.m32;
            r.m13 = a.m10 * b.m03 + a.m11 * b.m13 + a.m12 * b.m23 + a.m13 * b.m33;
            r.m20 = a.m20 * b.m00 + a.m21 * b.m10 + a.m22 * b.m20 + a.m23 * b.m30;
            r.m21 = a.m20 * b.m01 + a.m21 * b.m11 + a.m22 * b.m21 + a.m23 * b.m31;
            r.m22 = a.m20 * b.m02 + a.m21 * b.m12 + a.m22 * b.m22 + a.m23 * b.m32;
            r.m23 = a.m20 * b.m03 + a.m21 * b.m13 + a.m22 * b.m23 + a.m23 * b.m33;
            r.m30 = b.m30;
            r.m31 = b.m31;
            r.m32 = b.m32;
            r.m33 = b.m33;
        }

        private CachedSkinnedRendererInfo GetOrCreateSkinnedInfo(SkinnedMeshRenderer sr)
        {
            int id = sr.GetInstanceID();
            if (!cachedSkinnedInfo.TryGetValue(id, out var info))
            {
                var tr = sr.transform;
                var mats = sr.sharedMaterials;
                var bones = sr.bones;
                ulong[] boneHashes = null;
                if (bones != null && bones.Length > 0)
                {
                    boneHashes = new ulong[bones.Length];
                    for (int b = 0; b < bones.Length; b++)
                    {
                        boneHashes[b] = bones[b] != null ? HashUtils.HashStringFNV(bones[b].name) : 0UL;
                    }
                }

                // Clones under one parent share a hierarchy path: a second live renderer with
                // the path gets an id of its own, or both share persistent data and one Remix
                // instance (one enemy's pose drawn for both).
                int hierarchyHash = HashUtils.GetHierarchyHashInt(tr);
                if (skinnedIdOwners.TryGetValue(hierarchyHash, out var owner) && owner != null && owner != sr)
                    hierarchyHash = unchecked((int)((uint)hierarchyHash ^ ((uint)id * 0x85EBCA6Bu)));
                skinnedIdOwners[hierarchyHash] = sr;

                info = new CachedSkinnedRendererInfo
                {
                    rendererId = id,
                    hierarchyHash = hierarchyHash,
                    transform = tr,
                    gameObject = sr.gameObject,
                    animator = sr.GetComponentInParent<Animator>(),
                    animatorResolved = sr.gameObject.activeInHierarchy,
                    primarySharedMaterial = (mats != null && mats.Length > 0) ? mats[0] : null,
                    sharedMaterials = mats,
                    bones = bones,
                    boneNameHashes = boneHashes
                };
                cachedSkinnedInfo[id] = info;
                return info;
            }

            var currentPrimary = sr.sharedMaterial;
            if (!ReferenceEquals(currentPrimary, info.primarySharedMaterial))
            {
                var mats = sr.sharedMaterials;
                info.primarySharedMaterial = currentPrimary;
                info.sharedMaterials = mats;
            }

            return info;
        }

        // A settings change from the panel (render thread): the main thread's next capture
        // clears the caches. Cleared from the panel, the capture enumerating them threw.
        private volatile bool invalidateRequested;
        public void RequestInvalidateCaches() => invalidateRequested = true;

        /// <summary>
        /// Invalidate caches on scene change
        /// </summary>
        public void InvalidateCaches()
        {
            invalidateRequested = false;
            rendererCacheFrame = -1;
            lastCameraTrackFrame = -100;
            lastTrackedCamera = null;
            cachedRenderers.Clear();
            cachedSkinnedRenderers.Clear();
            cachedRendererIds.Clear();
            cachedSkinnedRendererIds.Clear();
            cachedStaticInfo.Clear();
            cachedSkinnedInfo.Clear();
            skeletonPoses.Clear();
            trackedParticleSystems.Clear();
            trackedParticleSystemIds.Clear();
            meshParticleInstancing.Clear();
            lastSkinnedTransforms.Clear();
            cachedStaticInstances.Clear();
            lock (meshQueueLock)
            {
                meshesToCreate.Clear();
                priorityMeshesToCreate.Clear();
                meshesInQueue.Clear();
                failedMeshKeys.Clear();
            }
            skinnedRoundRobinIndex = 0;
            persistentSkinnedData.Clear();
            bakedTopology.Clear();
            cachedSkinning.Clear();
            skinningBases.Clear();
            cachedScrollingMeshes.Clear();
            scrollingRendererIds.Clear();
            // Asked again: a game may know its player's body only after it has spawned.
            playerBodyRenderers.Clear();
            cachedScrollingRenderers.Clear();
            RemixScrollingTextureDetector.ClearCache();
            skinnedIdOwners.Clear();
            lock (persistentStaticLock)
            {
                persistentStaticInstances.Clear();
            }
            followsActiveState.Clear();
            trackedParticleSystems.Clear();
            trackedParticleSystemIds.Clear();
            meshParticleInstancing.Clear();
            trackedTrailRenderers.Clear();
            trackedTrailRendererIds.Clear();
            _capturedParticleMatIds.Clear();
            _particleLookCache.Clear();
            _particleTraitsCache.Clear();
            logger.LogInfo("Renderer caches invalidated");
        }
        
        /// <summary>
        /// Refresh renderer cache (includes inactive renderers so newly activated weapons/objects are tracked immediately)
        /// </summary>
        private void RefreshRendererCache(int frameCount)
        {
            cachedRenderers.Clear();
            cachedSkinnedRenderers.Clear();
            cachedRendererIds.Clear();
            cachedSkinnedRendererIds.Clear();
            cachedStaticInfo.Clear();
            cachedSkinnedInfo.Clear();
            skeletonPoses.Clear();
            lastCameraTrackFrame = -100;
            trackedParticleSystems.Clear();
            trackedParticleSystemIds.Clear();
            meshParticleInstancing.Clear();
            trackedTrailRenderers.Clear();
            trackedTrailRendererIds.Clear();
            _capturedParticleMatIds.Clear();
            _particleLookCache.Clear();
            _particleTraitsCache.Clear();
            cachedScrollingMeshes.Clear();
            scrollingRendererIds.Clear();
            // Asked again: a game may know its player's body only after it has spawned.
            playerBodyRenderers.Clear();
            cachedScrollingRenderers.Clear();
            // The scroll detection cache is kept (cleared on scene load): re-detecting every
            // renderer here made each refresh a spike. Property-block scrolling is still seen
            // live by CaptureStaticMeshes.
            skinnedRoundRobinIndex = 0;
            
            // Lower-detail LOD levels are not captured: the detailed level is (LodFilter).
            var lowerLodRenderers = LodFilter.CollectLowerLevelRenderers(out _);
            var allStatic = UnityCompat.FindSceneComponentsIncludingInactive<MeshRenderer>();
            for (int i = 0; i < allStatic.Length; i++)
            {
                var r = allStatic[i];
                if (r != null && !lowerLodRenderers.Contains(r.GetInstanceID()))
                    TrackMeshRenderer(r);
            }

            var allSkinned = UnityCompat.FindSceneComponentsIncludingInactive<SkinnedMeshRenderer>();
            for (int i = 0; i < allSkinned.Length; i++)
            {
                var sr = allSkinned[i];
                if (sr != null && !lowerLodRenderers.Contains(sr.GetInstanceID()))
                    TrackSkinnedRenderer(sr);
            }

            var allParticles = UnityCompat.FindSceneComponentsIncludingInactive<ParticleSystemRenderer>();
            for (int i = 0; i < allParticles.Length; i++)
                TrackParticleRenderer(allParticles[i]);

            var allTrails = UnityCompat.FindSceneComponentsIncludingInactive<TrailRenderer>();
            for (int i = 0; i < allTrails.Length; i++)
                TrackTrailRenderer(allTrails[i]);

            // Ensure all scene Animators continue ticking when cameras have cullingMask = 0
            var allAnimators = UnityEngine.Object.FindObjectsOfType<Animator>();
            for (int i = 0; i < allAnimators.Length; i++)
            {
                var a = allAnimators[i];
                if (a != null && a.cullingMode != AnimatorCullingMode.AlwaysAnimate)
                    a.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            }
            
            rendererCacheFrame = frameCount;
            
            if (VerboseLog.On) logger.LogInfo($"Renderer cache refreshed: {cachedRenderers.Count} static ({cachedScrollingRenderers.Count} scrolling UV), {cachedSkinnedRenderers.Count} skinned, {trackedParticleSystems.Count} particles, {lowerLodRenderers.Count} lower LOD levels skipped");
            
            if (configDebugLogInterval.Value > 0)
            {
                int gridCount = 0, combinedCount = 0, noMeshCount = 0, disabledCount = 0;
                foreach (var r in cachedRenderers)
                {
                    if (r == null) continue;
                    if (!r.enabled || !r.gameObject.activeInHierarchy) { disabledCount++; continue; }
                    var info = GetOrCreateStaticInfo(r);
                    var mf = info.filter;
                    if (mf == null || mf.sharedMesh == null) { noMeshCount++; continue; }
                    string mn = mf.sharedMesh.name;
                    if (mn != null && mn.Contains("Endless")) gridCount++;
                    if (mn != null && mn.StartsWith("Combined Mesh")) combinedCount++;
                }
                logger.LogInfo($"  breakdown: grid={gridCount} combined={combinedCount} disabled={disabledCount} noMesh={noMeshCount}");
                

            }
            
            RefreshRendererSnapshots();
        }

        // Diagnostics (LookAtDiagnostics): the nearest tracked renderer this capture would draw
        // whose bounds the ray enters in front of the camera, colliders or not. View models
        // (under the camera) and bounds around the camera are skipped. Main thread.
        public Renderer PickDrawnRenderer(Ray ray, Transform camera, out float distance)
        {
            Renderer best = null;
            float nearest = float.MaxValue;
            void Consider(Renderer r)
            {
                if (r == null || !r.enabled || SceneMeshScanner.IsRenderingOff(r) || !r.gameObject.activeInHierarchy ||
                    IsLayerDisabled(r.gameObject.layer) || r.transform.IsChildOf(camera))
                    return;
                if (r.bounds.IntersectRay(ray, out float d) && d > 0.01f && d < nearest)
                {
                    nearest = d;
                    best = r;
                }
            }
            foreach (var r in cachedRenderers) Consider(r);
            foreach (var r in cachedSkinnedRenderers) Consider(r);
            distance = nearest;
            return best;
        }

        private void TrackMeshRenderer(MeshRenderer r)
        {
            if (r == null || SceneMeshScanner.IsRendererInvisible(r) || !cachedRendererIds.Add(r.GetInstanceID()))
                return;
            if (RemixScrollingTextureDetector.IsScrollingRenderer(r))
            {
                scrollingRendererIds.Add(r.GetInstanceID());
                cachedScrollingRenderers.Add(r);
            }
            cachedRenderers.Add(r);
            GetOrCreateStaticInfo(r);
        }

        private void TrackSkinnedRenderer(SkinnedMeshRenderer sr)
        {
            if (sr == null || SceneMeshScanner.IsRendererInvisible(sr) || !cachedSkinnedRendererIds.Add(sr.GetInstanceID()))
                return;
            cachedSkinnedRenderers.Add(sr);
            if (!sr.updateWhenOffscreen)
                sr.updateWhenOffscreen = true;
            var info = GetOrCreateSkinnedInfo(sr);
            if (info.animator != null && info.animator.cullingMode != AnimatorCullingMode.AlwaysAnimate)
                info.animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
        }

        private void TrackParticleRenderer(ParticleSystemRenderer pr)
        {
            if (pr == null || !trackedParticleSystemIds.Add(pr.GetInstanceID()))
                return;
            var ps = pr.GetComponent<ParticleSystem>();
            if (ps == null)
                return;
            UnityCompat.KeepSimulating(ps);
            trackedParticleSystems.Add(new TrackedParticleSystem
            {
                renderer = pr,
                system = ps,
                id = pr.GetInstanceID()
            });
        }

        private void TrackTrailRenderer(TrailRenderer tr)
        {
            if (tr == null || !trackedTrailRendererIds.Add(tr.GetInstanceID()))
                return;
            trackedTrailRenderers.Add(new TrackedTrailRenderer
            {
                renderer = tr,
                id = tr.GetInstanceID()
            });
        }

        // Objects spawned since the last frame (a GameProfile's spawn hooks): tracked
        // right away instead of at the next renderer cache refresh.
        private static readonly List<Component> spawnedObjects = new List<Component>();

        public static void TrackSpawnedObject(Component root)
        {
            if (root == null) return;
            lock (spawnedObjects)
                spawnedObjects.Add(root);
        }

        private void TrackSpawnedObjects()
        {
            Component[] spawned;
            lock (spawnedObjects)
            {
                if (spawnedObjects.Count == 0) return;
                spawned = spawnedObjects.ToArray();
                spawnedObjects.Clear();
            }

            foreach (var root in spawned)
            {
                if (root == null) continue;
                foreach (var r in root.GetComponentsInChildren<MeshRenderer>(true)) TrackMeshRenderer(r);
                foreach (var sr in root.GetComponentsInChildren<SkinnedMeshRenderer>(true)) TrackSkinnedRenderer(sr);
                foreach (var pr in root.GetComponentsInChildren<ParticleSystemRenderer>(true)) TrackParticleRenderer(pr);
                foreach (var tr in root.GetComponentsInChildren<TrailRenderer>(true)) TrackTrailRenderer(tr);
                foreach (var animator in root.GetComponentsInChildren<Animator>(true))
                    if (animator.cullingMode != AnimatorCullingMode.AlwaysAnimate)
                        animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            }
        }
        
        /// <summary>
        /// Build thread-safe renderer snapshots from the current cache. Must be called from main thread.
        /// </summary>
        private void RefreshRendererSnapshots()
        {
            // Build per-renderer snapshots
            var renderers = new List<RendererSnapshot>();
            // Track counts per layer: [layer] -> (static, skinned)
            var layerCounts = new Dictionary<int, (int s, int k)>();
            
            for (int i = 0; i < cachedRenderers.Count; i++)
            {
                var r = cachedRenderers[i];
                if (r == null) continue;
                int layer = r.gameObject.layer;
                renderers.Add(new RendererSnapshot
                {
                    InstanceId = r.GetInstanceID(),
                    Name = r.gameObject.name ?? "(null)",
                    Type = "Static",
                    Layer = layer
                });
                if (!layerCounts.ContainsKey(layer)) layerCounts[layer] = (0, 0);
                var c = layerCounts[layer];
                layerCounts[layer] = (c.s + 1, c.k);
            }
            
            for (int i = 0; i < cachedSkinnedRenderers.Count; i++)
            {
                var r = cachedSkinnedRenderers[i];
                if (r == null) continue;
                int layer = r.gameObject.layer;
                renderers.Add(new RendererSnapshot
                {
                    InstanceId = r.GetInstanceID(),
                    Name = r.gameObject.name ?? "(null)",
                    Type = "Skinned",
                    Layer = layer
                });
                if (!layerCounts.ContainsKey(layer)) layerCounts[layer] = (0, 0);
                var c = layerCounts[layer];
                layerCounts[layer] = (c.s, c.k + 1);
            }
            
            // Build layer snapshots
            var layers = new List<LayerSnapshot>();
            lock (_disabledLock)
            {
                foreach (var kv in layerCounts)
                {
                    string name;
                    try { name = LayerMask.LayerToName(kv.Key); }
                    catch { name = ""; }
                    if (string.IsNullOrEmpty(name)) name = $"Layer {kv.Key}";
                    
                    layers.Add(new LayerSnapshot
                    {
                        LayerIndex = kv.Key,
                        LayerName = name,
                        StaticCount = kv.Value.s,
                        SkinnedCount = kv.Value.k,
                        UserDisabled = _disabledLayers.Contains(kv.Key)
                    });
                }
            }
            
            layers.Sort((a, b) => a.LayerIndex.CompareTo(b.LayerIndex));
            
            Volatile.Write(ref _layerSnapshots, layers.ToArray());
            Volatile.Write(ref _rendererSnapshots, renderers.ToArray());
        }
        
        /// <summary>
        /// Ensures all active renderers parented under the camera (viewmodels, weapons, arms) are tracked immediately.
        /// Throttled to once every 10 frames (or immediately on camera change) to avoid redundant GetComponentsInChildren allocations.
        /// </summary>
        public void EnsureCameraRenderersTracked(Camera cam, int frameCount = -1)
        {
            if (cam == null) return;
            if (frameCount >= 0 && ReferenceEquals(cam, lastTrackedCamera) && (frameCount - lastCameraTrackFrame) < 10)
                return;

            lastTrackedCamera = cam;
            if (frameCount >= 0)
                lastCameraTrackFrame = frameCount;
            
            // Invisible ones as TrackMeshRenderer leaves them out: a camera's child can be an
            // effect the profile hides (a screen distortion its pass-through replays).
            var childRenderers = cam.GetComponentsInChildren<MeshRenderer>(false);
            for (int i = 0; i < childRenderers.Length; i++)
            {
                var r = childRenderers[i];
                if (r != null && !SceneMeshScanner.IsRendererInvisible(r) && cachedRendererIds.Add(r.GetInstanceID()))
                {
                    cachedRenderers.Add(r);
                    GetOrCreateStaticInfo(r);
                }
            }
            
            var childSkinned = cam.GetComponentsInChildren<SkinnedMeshRenderer>(false);
            for (int i = 0; i < childSkinned.Length; i++)
            {
                var sr = childSkinned[i];
                if (sr != null && !SceneMeshScanner.IsRendererInvisible(sr) && cachedSkinnedRendererIds.Add(sr.GetInstanceID()))
                {
                    cachedSkinnedRenderers.Add(sr);
                    if (!sr.updateWhenOffscreen)
                        sr.updateWhenOffscreen = true;
                    var info = GetOrCreateSkinnedInfo(sr);
                    if (info.animator != null && info.animator.cullingMode != AnimatorCullingMode.AlwaysAnimate)
                        info.animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                }
            }
        }

        /// <summary>
        /// Combine two ints into a unique hash for per-renderer material IDs
        /// </summary>
        private static int HashCombine(int a, int b)
        {
            unchecked { return a * 397 ^ b; }
        }

        // The game's recolour mask for a renderer's material (GameProfile.GetColorMask), with
        // its colours folded into the material id (a recolour is another Remix material).
        private MaterialPropertyBlock colorMaskBlock;

        /// <summary>
        /// Cleanup resources
        /// </summary>
        public void Cleanup()
        {
            foreach (var mesh in bakedMeshes.Values)
            {
                if (mesh != null)
                {
                    UnityEngine.Object.Destroy(mesh);
                }
            }
            bakedMeshes.Clear();
            bakedTopology.Clear();
        }
    }
}
