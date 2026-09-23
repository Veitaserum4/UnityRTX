using System;
using System.Collections.Generic;
using System.Reflection;
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
    public class RemixFrameCapture
    {
        private readonly ManualLogSource logger;
        private readonly RemixCameraHandler cameraHandler;
        private readonly RemixMeshConverter meshConverter;
        private readonly RemixMaterialManager materialManager;
        
        private readonly ConfigEntry<bool> configUseDistanceCulling;
        private readonly ConfigEntry<float> configMaxRenderDistance;
        private readonly ConfigEntry<bool> configUseVisibilityCulling;
        private readonly ConfigEntry<int> configRendererCacheDuration;
        private readonly ConfigEntry<int> configDebugLogInterval;
        private readonly ConfigEntry<bool> configCaptureStaticMeshes;
        private readonly ConfigEntry<bool> configCaptureSkinnedMeshes;
        private readonly ConfigEntry<bool> configCaptureParticles;
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
        
        // Cached baked meshes for skinned renderers
        private Dictionary<int, Mesh> bakedMeshes = new Dictionary<int, Mesh>();
        private Dictionary<int, Matrix4x4> lastSkinnedTransforms = new Dictionary<int, Matrix4x4>();
        
        // Reusable mesh and materials for dynamic line/sprite capture
        private Mesh sharedTempLineMesh;
        private Material fallbackSpriteMaterial;
        private const int MAX_LINE_SLOTS = 32;
        private const int MAX_SPRITE_SLOTS = 32;
        
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
        public bool IsLayerDisabled(int layerIndex)
        {
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
        private readonly Dictionary<int, MeshFilter> cachedMeshFilters = new Dictionary<int, MeshFilter>();
        private readonly Dictionary<int, int> cachedMaterialSignatures = new Dictionary<int, int>();
        private readonly Dictionary<int, Material[]> cachedRendererMaterials = new Dictionary<int, Material[]>();
        private LineRenderer[] cachedLineRenderers = null;
        private int lastLineRendererRefreshFrame = -1;
        private SpriteRenderer[] cachedSpriteRenderers = null;
        private int lastSpriteRendererRefreshFrame = -1;
        private MaterialPropertyBlock sharedStaticMpb = null;

        // --- Diagnostic getters for debug HUD ---
        public int FailedMeshCount { get { lock (meshQueueLock) return failedMeshKeys.Count; } }
        public int PendingMeshQueueCount { get { lock (meshQueueLock) return meshesToCreate.Count + priorityMeshesToCreate.Count; } }
        public int PersistentStaticCount { get { lock (persistentStaticLock) return persistentStaticInstances.Count; } }
        public int CachedStaticRendererCount => cachedRenderers.Count;
        public int CachedSkinnedRendererCount => cachedSkinnedRenderers.Count;

        private bool ShouldPreferSceneScan(MeshRenderer renderer, Mesh mesh)
        {
            if (renderer == null || mesh == null)
                return false;

            return renderer.isPartOfStaticBatch;
        }

        private static int CountDistinctMaterials(Material[] materials)
        {
            if (materials == null || materials.Length == 0)
                return 0;

            var materialIds = new HashSet<int>();
            for (int i = 0; i < materials.Length; i++)
            {
                if (materials[i] != null)
                    materialIds.Add(materials[i].GetInstanceID());
            }

            return materialIds.Count;
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
        }
        private readonly object persistentStaticLock = new object();
        private Dictionary<int, PersistentStaticInstance> persistentStaticInstances = new Dictionary<int, PersistentStaticInstance>();
        private int skinnedRoundRobinIndex = 0; // rotates through cachedSkinnedRenderers each frame (BakeMesh fallback)
        
        // Persistent cache: last-baked data for every skinned mesh, so all are drawn every frame
        private Dictionary<int, SkinnedMeshData> persistentSkinnedData = new Dictionary<int, SkinnedMeshData>();
        
        // Per-mesh cached static data (UVs, triangles don't change with skinning)
        private struct CachedMeshTopology
        {
            public Vector2[] uvs;
            public int[] triangles;
            public int vertexCount;
            public int positionOffset;  // byte offset of Position in interleaved vertex buffer
            public int normalOffset;    // byte offset of Normal in interleaved vertex buffer
            public int stride;          // total bytes per vertex in GPU buffer
            public bool valid;
            public bool layoutValid;    // vertex buffer layout cached but triangles/UVs pending (mesh not readable)
        }
        private Dictionary<int, CachedMeshTopology> cachedTopology = new Dictionary<int, CachedMeshTopology>(); // keyed by sharedMesh instance ID
        
        // Cached GPU skinning data per sharedMesh (bind-pose vertices + bone weights)
        private Dictionary<int, CachedSkinningData> cachedSkinning = new Dictionary<int, CachedSkinningData>(); // keyed by sharedMesh instance ID
        
        // Pending GPU readback requests
        private struct PendingReadback
        {
            public int skinnedId;
            public int materialId;
            public int sharedMeshId;
            public Matrix4x4 localToWorld;
            public AsyncGPUReadbackRequest request;
        }
        private List<PendingReadback> pendingReadbacks = new List<PendingReadback>();
        
        // Track which renderers have had vertexBufferTarget configured
        private HashSet<int> configuredBufferTargets = new HashSet<int>();
        private HashSet<string> loggedHashDebugMeshes = new HashSet<string>();
        
        // Track logged skinned mesh materials to avoid spam
        private HashSet<string> loggedSkinnedMaterials = new HashSet<string>();
        
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
        }
        
        public struct MeshInstanceData
        {
            public ulong meshKey;
            public int meshId;
            public Matrix4x4 localToWorld;
            public int rendererInstanceId;
            public StaticGeometryKey dedupeKey;
        }
        
        public struct SkinnedMeshData
        {
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
            // When true and existing Remix handle exists, reuse it instead of recreating BLAS every frame
            public bool isStaticData;
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
            public int[] triangles;
            public float[] blendWeights;    // bonesPerVertex * vertexCount
            public uint[] blendIndices;     // bonesPerVertex * vertexCount
            public int bonesPerVertex;
            public int boneCount;
            public Matrix4x4[] bindPoses;
            public bool meshCreated;        // true after Remix mesh has been created on render thread
        }
        
        public class FrameState
        {
            public CameraData camera;
            public List<MeshInstanceData> instances = new List<MeshInstanceData>();
            public List<SkinnedMeshData> skinned = new List<SkinnedMeshData>();
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
            ConfigEntry<bool> captureParticles = null)
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
            this.configHardwareSkinning = hardwareSkinning;
            this.configPersistDisabledRenderers = persistDisabledRenderers;
            this.configStaticMeshFrameSkip = staticMeshFrameSkip;
        }
        
        /// <summary>
        /// Invalidate caches on scene change
        /// </summary>
        public void InvalidateCaches()
        {
            rendererCacheFrame = -1;
            cachedRenderers.Clear();
            cachedSkinnedRenderers.Clear();
            cachedRendererIds.Clear();
            cachedSkinnedRendererIds.Clear();
            trackedParticleSystems.Clear();
            trackedParticleSystemIds.Clear();
            lastSkinnedTransforms.Clear();
            cachedStaticInstances.Clear();
            lock (meshQueueLock)
            {
                meshesToCreate.Clear();
                priorityMeshesToCreate.Clear();
                meshesInQueue.Clear();
                failedMeshKeys.Clear();
            }
            loggedSkinnedMaterials.Clear();
            skinnedRoundRobinIndex = 0;
            persistentSkinnedData.Clear();
            pendingReadbacks.Clear();
            configuredBufferTargets.Clear();
            loggedHashDebugMeshes.Clear();
            cachedTopology.Clear();
            cachedSkinning.Clear();
            cachedMeshFilters.Clear();
            cachedMaterialSignatures.Clear();
            cachedRendererMaterials.Clear();
            cachedLineRenderers = null;
            lastLineRendererRefreshFrame = -1;
            cachedSpriteRenderers = null;
            lastSpriteRendererRefreshFrame = -1;
            lock (persistentStaticLock)
            {
                persistentStaticInstances.Clear();
            }
            _cachedBloodVerts = null;
            _cachedBloodNorms = null;
            _cachedBloodUvs = null;
            _cachedBloodColors = null;
            _cachedBloodTris = null;
            _lastCapturedBloodCount = -1;
            _lastCapturedMeshVerts = -1;
            _bloodMeshVersion++;
            _currentBloodMeshHash = 0x7B100D0000000000UL | (ulong)_bloodMeshVersion;
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
            cachedMaterialSignatures.Clear();
            cachedRendererMaterials.Clear();
            trackedParticleSystems.Clear();
            trackedParticleSystemIds.Clear();
            skinnedRoundRobinIndex = 0;
            // Don't clear configuredBufferTargets — the property persists on the component
            
            var allStatic = UnityEngine.Object.FindObjectsOfType<MeshRenderer>(true);
            for (int i = 0; i < allStatic.Length; i++)
            {
                var r = allStatic[i];
                if (r != null && cachedRendererIds.Add(r.GetInstanceID()))
                {
                    cachedRenderers.Add(r);
                }
            }

            var allSkinned = UnityEngine.Object.FindObjectsOfType<SkinnedMeshRenderer>(true);
            for (int i = 0; i < allSkinned.Length; i++)
            {
                var sr = allSkinned[i];
                if (sr != null && cachedSkinnedRendererIds.Add(sr.GetInstanceID()))
                {
                    cachedSkinnedRenderers.Add(sr);
                    if (!sr.updateWhenOffscreen)
                        sr.updateWhenOffscreen = true;
                    var anim = sr.GetComponentInParent<Animator>();
                    if (anim != null && anim.cullingMode != AnimatorCullingMode.AlwaysAnimate)
                        anim.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                }
            }

            var allParticles = UnityEngine.Object.FindObjectsOfType<ParticleSystemRenderer>(false);
            for (int i = 0; i < allParticles.Length; i++)
            {
                var pr = allParticles[i];
                if (pr != null && trackedParticleSystemIds.Add(pr.GetInstanceID()))
                {
                    var ps = pr.GetComponent<ParticleSystem>();
                    if (ps != null)
                    {
                        var main = ps.main;
                        if (main.cullingMode != ParticleSystemCullingMode.AlwaysSimulate)
                            main.cullingMode = ParticleSystemCullingMode.AlwaysSimulate;

                        trackedParticleSystems.Add(new TrackedParticleSystem
                        {
                            renderer = pr,
                            system = ps,
                            id = pr.GetInstanceID()
                        });
                    }
                }
            }

            // Ensure all scene Animators continue ticking when cameras have cullingMask = 0
            var allAnimators = UnityEngine.Object.FindObjectsOfType<Animator>();
            for (int i = 0; i < allAnimators.Length; i++)
            {
                var a = allAnimators[i];
                if (a != null && a.cullingMode != AnimatorCullingMode.AlwaysAnimate)
                    a.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            }
            
            rendererCacheFrame = frameCount;
            
            logger.LogInfo($"Renderer cache refreshed: {cachedRenderers.Count} static, {cachedSkinnedRenderers.Count} skinned, {trackedParticleSystems.Count} particles");
            
            if (configDebugLogInterval.Value > 0)
            {
                int gridCount = 0, combinedCount = 0, noMeshCount = 0, disabledCount = 0;
                foreach (var r in cachedRenderers)
                {
                    if (r == null) continue;
                    if (!r.enabled || !r.gameObject.activeInHierarchy) { disabledCount++; continue; }
                    var mf = r.GetComponent<MeshFilter>();
                    if (mf == null || mf.sharedMesh == null) { noMeshCount++; continue; }
                    string mn = mf.sharedMesh.name;
                    if (mn != null && mn.Contains("Endless")) gridCount++;
                    if (mn != null && mn.StartsWith("Combined Mesh")) combinedCount++;
                }
                logger.LogInfo($"  breakdown: grid={gridCount} combined={combinedCount} disabled={disabledCount} noMesh={noMeshCount}");
                
                if (cachedSkinnedRenderers.Count > 0)
                {
                    var sb = new System.Text.StringBuilder();
                    sb.AppendLine($"[SkinnedDump] All {cachedSkinnedRenderers.Count} SkinnedMeshRenderers:");
                    for (int i = 0; i < cachedSkinnedRenderers.Count; i++)
                    {
                        var sr = cachedSkinnedRenderers[i];
                        if (sr == null) { sb.AppendLine($"  [{i}] NULL"); continue; }
                        string meshName = sr.sharedMesh != null ? sr.sharedMesh.name : "NULL_MESH";
                        int boneCount = sr.bones != null ? sr.bones.Length : 0;
                        sb.AppendLine($"  [{i}] '{sr.gameObject.name}' mesh='{meshName}' layer={sr.gameObject.layer}({LayerMask.LayerToName(sr.gameObject.layer)}) enabled={sr.enabled} active={sr.gameObject.activeInHierarchy} bones={boneCount} id={sr.GetInstanceID()}");
                    }
                    logger.LogInfo(sb.ToString());
                }
            }
            
            RefreshRendererSnapshots();
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
        /// This avoids any delay when switching weapons or activating arms.
        /// </summary>
        public void EnsureCameraRenderersTracked(Camera cam)
        {
            if (cam == null) return;
            
            var childRenderers = cam.GetComponentsInChildren<MeshRenderer>(false);
            for (int i = 0; i < childRenderers.Length; i++)
            {
                var r = childRenderers[i];
                if (r != null && cachedRendererIds.Add(r.GetInstanceID()))
                {
                    cachedRenderers.Add(r);
                }
            }
            
            var childSkinned = cam.GetComponentsInChildren<SkinnedMeshRenderer>(false);
            for (int i = 0; i < childSkinned.Length; i++)
            {
                var sr = childSkinned[i];
                if (sr != null && cachedSkinnedRendererIds.Add(sr.GetInstanceID()))
                {
                    cachedSkinnedRenderers.Add(sr);
                    if (!sr.updateWhenOffscreen)
                        sr.updateWhenOffscreen = true;
                    var anim = sr.GetComponentInParent<Animator>();
                    if (anim != null && anim.cullingMode != AnimatorCullingMode.AlwaysAnimate)
                        anim.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                }
            }
        }

        private static readonly List<Component> _spawnedDynamicQueue = new List<Component>();
        private static readonly object _spawnedQueueLock = new object();

        /// <summary>
        /// Called by DynamicSpawnPatch whenever a projectile, explosion, rocket, magnet, or enemy
        /// is instantiated / initialized. Zero polling overhead.
        /// </summary>
        public static void QueueDynamicObjectForTracking(Component c)
        {
            if (c == null) return;
            lock (_spawnedQueueLock)
            {
                _spawnedDynamicQueue.Add(c);
            }
        }

        /// <summary>
        /// Drains newly spawned dynamic objects and adds their renderers to the active caches.
        /// Takes ~0.0001 ms per frame when no new objects spawn.
        /// </summary>
        public void ProcessQueuedDynamicObjects()
        {
            List<Component> queueCopy = null;
            lock (_spawnedQueueLock)
            {
                if (_spawnedDynamicQueue.Count > 0)
                {
                    queueCopy = new List<Component>(_spawnedDynamicQueue);
                    _spawnedDynamicQueue.Clear();
                }
            }

            if (queueCopy == null) return;

            for (int i = 0; i < queueCopy.Count; i++)
            {
                var c = queueCopy[i];
                if (c == null) continue;

                try
                {
                    var mrs = c.GetComponentsInChildren<MeshRenderer>(true);
                    for (int j = 0; j < mrs.Length; j++)
                    {
                        var r = mrs[j];
                        if (r != null && cachedRendererIds.Add(r.GetInstanceID()))
                        {
                            cachedRenderers.Add(r);
                        }
                    }

                    var smrs = c.GetComponentsInChildren<SkinnedMeshRenderer>(true);
                    for (int j = 0; j < smrs.Length; j++)
                    {
                        var sr = smrs[j];
                        if (sr != null && cachedSkinnedRendererIds.Add(sr.GetInstanceID()))
                        {
                            cachedSkinnedRenderers.Add(sr);
                            if (!sr.updateWhenOffscreen)
                                sr.updateWhenOffscreen = true;
                            var anim = sr.GetComponentInParent<Animator>();
                            if (anim != null && anim.cullingMode != AnimatorCullingMode.AlwaysAnimate)
                                anim.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                        }
                    }
                }
                catch { }
            }
        }

        /// <summary>
        /// Capture static meshes from scene
        /// </summary>
        public void CaptureStaticMeshes(FrameState state, int frameCount)
        {
            // Rebuild cache if stale
            if (frameCount - rendererCacheFrame > configRendererCacheDuration.Value || rendererCacheFrame < 0)
            {
                RefreshRendererCache(frameCount);
            }
            
            // Get camera
            Camera mainCam = cameraHandler.GetPreferredCamera();
            Vector3 camPos = mainCam != null ? mainCam.transform.position : Vector3.zero;

            if (mainCam != null)
            {
                EnsureCameraRenderersTracked(mainCam);
            }
            ProcessQueuedDynamicObjects();
            
            // Capture camera
            if (mainCam != null)
            {
                var t = mainCam.transform;
                state.camera = new CameraData
                {
                    position = t.position,
                    forward = t.forward,
                    up = t.up,
                    right = t.right,
                    fov = mainCam.fieldOfView,
                    aspect = mainCam.aspect,
                    nearPlane = mainCam.nearClipPlane,
                    farPlane = mainCam.farClipPlane,
                    valid = true
                };
            }
            
            // Debug toggle check
            if (!configCaptureStaticMeshes.Value)
                return;

            // Static mesh frame skipping: reuse cached static instances if enabled and available
            int frameSkip = configStaticMeshFrameSkip != null ? configStaticMeshFrameSkip.Value : 1;
            if (frameSkip > 1 && (frameCount % frameSkip != 0) && cachedStaticInstances.Count > 0)
            {
                state.instances.AddRange(cachedStaticInstances);
                if (mainCam != null)
                {
                    CaptureCameraViewModelMeshes(state, mainCam);
                }
                return;
            }

            cachedStaticInstances.Clear();
            staticCaptureCount++;
            int totalDrawn = 0;
            
            // Capture static meshes
            foreach (var renderer in cachedRenderers)
            {
                if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy)
                    continue;

                if (renderer.isPartOfStaticBatch)
                    continue;

                var lossy = renderer.transform.lossyScale;
                if (lossy.sqrMagnitude < 0.0001f)
                    continue;
                
                if (IsLayerDisabled(renderer.gameObject.layer))
                    continue;

                if (IsRendererDisabled(renderer.GetInstanceID()))
                    continue;
                
                // Optional visibility culling (can cause issues in some games)
                if (configUseVisibilityCulling.Value && !renderer.isVisible)
                    continue;
                
                // Optional distance culling
                if (configUseDistanceCulling.Value)
                {
                    float maxDist = configMaxRenderDistance.Value;
                    float sqrDistance = (renderer.bounds.center - camPos).sqrMagnitude;
                    if (sqrDistance > maxDist * maxDist)
                        continue;
                }
                
                int rendererInstanceId = renderer.GetInstanceID();
                if (!cachedMeshFilters.TryGetValue(rendererInstanceId, out var meshFilter) || meshFilter == null)
                {
                    meshFilter = renderer.GetComponent<MeshFilter>();
                    if (meshFilter != null) cachedMeshFilters[rendererInstanceId] = meshFilter;
                }
                if (meshFilter == null || meshFilter.sharedMesh == null)
                    continue;
                
                var mesh = meshFilter.sharedMesh;
                int meshId = mesh.GetInstanceID();
                var dedupeKey = StaticGeometryDedupe.BuildKey(renderer, mesh);

                if (ShouldPreferSceneScan(renderer, mesh))
                {
                    lock (persistentStaticLock)
                    {
                        persistentStaticInstances.Remove(rendererInstanceId);
                    }
                    continue;
                }

                Material[] materials = null;
                if (!cachedMaterialSignatures.TryGetValue(rendererInstanceId, out int baseMatSig))
                {
                    materials = renderer.sharedMaterials;
                    baseMatSig = StaticGeometryDedupe.ComputeMaterialSignature(materials);
                    cachedMaterialSignatures[rendererInstanceId] = baseMatSig;
                    if (materials != null) cachedRendererMaterials[rendererInstanceId] = materials;
                }
                else
                {
                    cachedRendererMaterials.TryGetValue(rendererInstanceId, out materials);
                }

                Texture mpbMainTex = null;
                Color? mpbColor = null;
                Color? mpbEmissive = null;
                int mpbHash = 0;

                if (sharedStaticMpb == null)
                    sharedStaticMpb = new MaterialPropertyBlock();

                if (renderer.HasPropertyBlock())
                {
                    sharedStaticMpb.Clear();
                    renderer.GetPropertyBlock(sharedStaticMpb);
                    Texture tex = sharedStaticMpb.GetTexture("_MainTex");
                    if (tex == null) tex = sharedStaticMpb.GetTexture("_BaseMap");
                    if (tex == null) tex = sharedStaticMpb.GetTexture("_Diffuse");
                    if (tex == null) tex = sharedStaticMpb.GetTexture("_Texture");
                    if (tex != null)
                    {
                        mpbMainTex = tex;
                        mpbHash = HashCombine(mpbHash, tex.GetInstanceID());
                    }
                    Color col = sharedStaticMpb.GetColor("_Color");
                    if (col.a <= 0f && col.r <= 0f && col.g <= 0f && col.b <= 0f)
                        col = sharedStaticMpb.GetColor("_BaseColor");
                    if (col.a > 0f || col.r > 0f || col.g > 0f || col.b > 0f)
                    {
                        mpbColor = col;
                        Color32 c32 = col;
                        int colInt = (c32.a << 24) | (c32.r << 16) | (c32.g << 8) | c32.b;
                        mpbHash = HashCombine(mpbHash, colInt);
                    }
                    Color emis = sharedStaticMpb.GetColor("_EmissiveColor");
                    if (emis.a > 0f || emis.r > 0f || emis.g > 0f || emis.b > 0f)
                    {
                        mpbEmissive = emis;
                        Color32 e32 = emis;
                        int emisInt = (e32.a << 24) | (e32.r << 16) | (e32.g << 8) | e32.b;
                        mpbHash = HashCombine(mpbHash, emisInt);
                    }
                    else if (mpbColor.HasValue && mainCam != null && renderer.transform.IsChildOf(mainCam.transform))
                    {
                        mpbEmissive = mpbColor.Value;
                        Color32 e32 = mpbColor.Value;
                        int emisInt = (e32.a << 24) | (e32.r << 16) | (e32.g << 8) | e32.b;
                        mpbHash = HashCombine(mpbHash, emisInt);
                    }
                }

                int matSig = baseMatSig;
                if (mpbHash != 0)
                {
                    matSig = HashCombine(matSig, mpbHash);
                }
                ulong meshKey = RemixMeshConverter.GetMeshKey(meshId, matSig);
                
                // Queue mesh for creation if not cached, not already queued, and not previously failed
                bool needsQueue;
                lock (meshQueueLock)
                {
                    needsQueue = !meshConverter.IsMeshCached(meshKey) && !meshesInQueue.Contains(meshKey) && !failedMeshKeys.Contains(meshKey);
                }
                
                if (needsQueue)
                {
                    Vector3[] vertices = null;
                    Vector3[] normals = null;
                    Vector2[] uvs = null;
                    Color32[] colors = null;
                    var submeshIndices = new List<uint[]>();
                    var submeshMaterials = new List<Material>();

                    if (mesh.isReadable)
                    {
                        try
                        {
                            vertices = mesh.vertices;
                            normals = mesh.normals;
                            uvs = mesh.uv;
                            colors = mesh.colors32;
                            if (colors != null && colors.Length == 0) colors = null;

                            int subMeshCount = mesh.subMeshCount;
                            for (int s = 0; s < subMeshCount; s++)
                            {
                                if (mesh.GetTopology(s) != MeshTopology.Triangles)
                                    continue;
                                var subTris = mesh.GetTriangles(s);
                                if (subTris == null || subTris.Length == 0 || subTris.Length % 3 != 0)
                                    continue;
                                bool valid = true;
                                uint[] sIdx = new uint[subTris.Length];
                                for (int j = 0; j < subTris.Length; j++)
                                {
                                    if (subTris[j] < 0 || subTris[j] >= vertices.Length)
                                    {
                                        valid = false;
                                        break;
                                    }
                                    sIdx[j] = (uint)subTris[j];
                                }
                                if (valid)
                                {
                                    submeshIndices.Add(sIdx);
                                    Material mat = (materials != null && s < materials.Length) ? materials[s] : null;
                                    submeshMaterials.Add(mat);
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            if (configDebugLogInterval.Value > 0)
                                logger.LogWarning($"Failed reading readable mesh '{mesh.name}': {ex.Message}");
                        }
                    }

                    if (vertices == null || vertices.Length == 0 || submeshIndices.Count == 0)
                    {
                        try
                        {
                            if (NativeMeshReader.ReadMeshFromGPU(mesh, out vertices, out normals, out uvs, out int[][] subTris))
                            {
                                submeshIndices.Clear();
                                submeshMaterials.Clear();
                                for (int s = 0; s < subTris.Length; s++)
                                {
                                    var tris = subTris[s];
                                    if (tris == null || tris.Length == 0 || tris.Length % 3 != 0)
                                        continue;
                                    bool valid = true;
                                    uint[] sIdx = new uint[tris.Length];
                                    for (int j = 0; j < tris.Length; j++)
                                    {
                                        if (tris[j] < 0 || tris[j] >= vertices.Length)
                                        {
                                            valid = false;
                                            break;
                                        }
                                        sIdx[j] = (uint)tris[j];
                                    }
                                    if (valid)
                                    {
                                        submeshIndices.Add(sIdx);
                                        Material mat = (materials != null && s < materials.Length) ? materials[s] : null;
                                        submeshMaterials.Add(mat);
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            if (configDebugLogInterval.Value > 0)
                                logger.LogWarning($"GPU readback failed for mesh '{mesh.name}': {ex.Message}");
                        }
                    }

                    if (vertices == null || vertices.Length == 0 || submeshIndices.Count == 0)
                    {
                        lock (meshQueueLock) { failedMeshKeys.Add(meshKey); }
                        continue;
                    }

                    int totalIndices = 0;
                    foreach (var sIdx in submeshIndices) totalIndices += sIdx.Length;
                    ulong meshHash = RemixMeshConverter.GenerateMeshHash(mesh.name, vertices.Length, totalIndices, matSig);

                    // Capture material textures (pixel data gathered here, Remix API deferred to render thread)
                    bool isViewModel = mainCam != null && renderer.transform.IsChildOf(mainCam.transform);
                    float? emIntensity = (isViewModel && mpbEmissive.HasValue) ? 2.5f : (float?)null;

                    List<int> submeshMaterialIds = null;
                    if (materials != null)
                    {
                        submeshMaterialIds = new List<int>(materials.Length);
                        for (int m = 0; m < materials.Length; m++)
                        {
                            if (materials[m] != null)
                            {
                                int matId = materials[m].GetInstanceID();
                                if (mpbHash != 0)
                                {
                                    matId = HashCombine(matId, mpbHash);
                                }
                                submeshMaterialIds.Add(matId);
                                materialManager.CaptureMaterialTextures(materials[m], matId, mpbEmissive, emIntensity, mpbMainTex as Texture2D, mpbColor);
                            }
                            else
                            {
                                submeshMaterialIds.Add(0);
                            }
                        }
                    }

                    // Queue pre-extracted mesh data
                    var preparedData = new PreparedMeshData
                    {
                        MeshKey = meshKey,
                        MeshId = meshId,
                        MeshName = mesh.name,
                        MeshHash = meshHash,
                        Vertices = vertices,
                        Normals = normals,
                        UVs = uvs,
                        Colors = colors,
                        SubmeshIndices = submeshIndices,
                        SubmeshMaterials = submeshMaterials,
                        SubmeshMaterialIds = submeshMaterialIds
                    };

                    lock (meshQueueLock)
                    {
                        if (isViewModel)
                        {
                            priorityMeshesToCreate.Enqueue(preparedData);
                        }
                        else
                        {
                            meshesToCreate.Enqueue(preparedData);
                        }
                        meshesInQueue.Add(meshKey);
                    }
                    
                    if (!isViewModel)
                        continue;
                }
                
                // Add instance
                var transform = renderer.transform.localToWorldMatrix;
                var instanceData = new MeshInstanceData
                {
                    meshKey = meshKey,
                    meshId = meshId,
                    localToWorld = transform,
                    rendererInstanceId = rendererInstanceId,
                    dedupeKey = dedupeKey
                };
                state.instances.Add(instanceData);
                totalDrawn++;
                
                // Remember this renderer's transform so we can keep drawing it if it gets disabled.
                // Do NOT persist viewmodels/weapons (they should disappear when unequipped).
                bool isCurrentViewModel = mainCam != null && renderer.transform.IsChildOf(mainCam.transform);
                if (!isCurrentViewModel)
                {
                    cachedStaticInstances.Add(instanceData);
                    lock (persistentStaticLock)
                    {
                        persistentStaticInstances[rendererInstanceId] = new PersistentStaticInstance
                        {
                            renderer = renderer,
                            meshKey = meshKey,
                            meshId = meshId,
                            localToWorld = transform,
                            dedupeKey = dedupeKey
                        };
                    }
                }
            }
            
            // Draw persistent instances for disabled (but not destroyed) renderers.
            // This keeps objects visible that the game deactivates (e.g. CyberGrind cubes after wave settles).
            // Gated by config — disabled by default since games with scene variants (e.g. Stanley Parable)
            // use inactive GameObjects for alternate rooms that should NOT be rendered.
            int persistentDrawn = 0;
            if (configPersistDisabledRenderers.Value)
            {
                var keysToRemove = (List<int>)null;
                lock (persistentStaticLock)
                {
                    foreach (var kv in persistentStaticInstances)
                    {
                        var entry = kv.Value;
                        // Unity null check: renderer was destroyed
                        if (entry.renderer == null)
                        {
                            if (keysToRemove == null) keysToRemove = new List<int>();
                            keysToRemove.Add(kv.Key);
                            continue;
                        }
                        
                        // Skip if the renderer is active — it was already drawn above
                        if (entry.renderer.enabled && entry.renderer.gameObject.activeInHierarchy)
                            continue;

                        var lossy = entry.renderer.transform.lossyScale;
                        if (lossy.sqrMagnitude < 0.0001f)
                        {
                            if (keysToRemove == null) keysToRemove = new List<int>();
                            keysToRemove.Add(kv.Key);
                            continue;
                        }

                        var meshFilter = entry.renderer.GetComponent<MeshFilter>();
                        var mesh = meshFilter != null ? meshFilter.sharedMesh : null;
                        if (ShouldPreferSceneScan(entry.renderer, mesh))
                        {
                            if (keysToRemove == null) keysToRemove = new List<int>();
                            keysToRemove.Add(kv.Key);
                            continue;
                        }
                        
                        // Skip if mesh isn't cached in Remix yet
                        if (!meshConverter.IsMeshCached(entry.meshKey))
                            continue;
                        
                        // Skip if layer is disabled by user
                        if (IsLayerDisabled(entry.renderer.gameObject.layer))
                            continue;
                        
                        // Distance culling using stored transform position
                        if (configUseDistanceCulling.Value)
                        {
                            float maxDist = configMaxRenderDistance.Value;
                            Vector3 objPos = new Vector3(entry.localToWorld.m03, entry.localToWorld.m13, entry.localToWorld.m23);
                            float sqrDistance = (objPos - camPos).sqrMagnitude;
                            if (sqrDistance > maxDist * maxDist)
                                continue;
                        }
                        
                        // Draw with last-known transform
                        var pInstanceData = new MeshInstanceData
                        {
                            meshKey = entry.meshKey,
                            meshId = entry.meshId,
                            localToWorld = entry.localToWorld,
                            rendererInstanceId = entry.renderer.GetInstanceID(),
                            dedupeKey = entry.dedupeKey
                        };
                        state.instances.Add(pInstanceData);
                        cachedStaticInstances.Add(pInstanceData);
                        persistentDrawn++;
                    }
                    if (keysToRemove != null)
                    {
                        foreach (var key in keysToRemove)
                            persistentStaticInstances.Remove(key);
                    }
                }
            }
            
            // Periodic tracking
            if (configDebugLogInterval.Value > 0 && staticCaptureCount % 300 == 1)
            {
                int persistentCount;
                lock (persistentStaticLock) { persistentCount = persistentStaticInstances.Count; }
                logger.LogInfo($"[StaticCapture] frame={frameCount} drawn={totalDrawn} persistent={persistentDrawn} total={persistentCount} queued={meshesToCreate.Count} failedMeshes={failedMeshKeys.Count}");
            }
        }

        /// <summary>
        /// Captures camera-attached viewmodels/weapons on skipped frames so weapons never jitter or lag behind camera motion.
        /// </summary>
        private void CaptureCameraViewModelMeshes(FrameState state, Camera mainCam)
        {
            if (mainCam == null) return;
            var viewmodelRenderers = mainCam.GetComponentsInChildren<MeshRenderer>(false);
            for (int i = 0; i < viewmodelRenderers.Length; i++)
            {
                var renderer = viewmodelRenderers[i];
                if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy)
                    continue;
                if (IsLayerDisabled(renderer.gameObject.layer) || IsRendererDisabled(renderer.GetInstanceID()))
                    continue;
                var meshFilter = renderer.GetComponent<MeshFilter>();
                if (meshFilter == null || meshFilter.sharedMesh == null)
                    continue;
                var mesh = meshFilter.sharedMesh;
                int meshId = mesh.GetInstanceID();
                int rendererInstanceId = renderer.GetInstanceID();
                var dedupeKey = StaticGeometryDedupe.BuildKey(renderer, mesh);
                var materials = renderer.sharedMaterials;
                int matSig = StaticGeometryDedupe.ComputeMaterialSignature(materials);
                ulong meshKey = RemixMeshConverter.GetMeshKey(meshId, matSig);
                state.instances.Add(new MeshInstanceData
                {
                    meshKey = meshKey,
                    meshId = meshId,
                    localToWorld = renderer.transform.localToWorldMatrix,
                    rendererInstanceId = rendererInstanceId,
                    dedupeKey = dedupeKey
                });
            }
        }
        
        /// <summary>
        /// Process queued mesh creation (batched with frame time budget)
        /// </summary>
        public void ProcessMeshCreationBatch()
        {
            // Upload any textures queued by the main thread before creating meshes/materials
            materialManager.ProcessPendingTextureUploads();
            
            // First process high-priority viewmodel/weapon meshes immediately so they appear without delay
            while (true)
            {
                PreparedMeshData prioData = null;
                lock (meshQueueLock)
                {
                    if (priorityMeshesToCreate.Count > 0)
                        prioData = priorityMeshesToCreate.Dequeue();
                }
                if (prioData == null) break;
                
                ulong meshKey = prioData.MeshKey != 0 ? prioData.MeshKey : (ulong)(uint)prioData.MeshId;
                lock (meshQueueLock) { meshesInQueue.Remove(meshKey); }
                if (meshConverter.IsMeshCached(meshKey)) continue;
                
                try
                {
                    IntPtr handle = meshConverter.CreateRemixMeshFromPrepared(prioData);
                    if (handle == IntPtr.Zero)
                    {
                        if (configDebugLogInterval.Value > 0)
                            logger.LogWarning($"[MeshFail] Failed to create priority viewmodel mesh '{prioData.MeshName}'");
                        lock (meshQueueLock) { failedMeshKeys.Add(meshKey); }
                    }
                }
                catch (Exception ex)
                {
                    logger.LogWarning($"Exception creating priority viewmodel mesh: {ex.Message}");
                }
            }

            // Adaptive batch size based on queue length
            int batchSize;
            lock (meshQueueLock)
            {
                batchSize = meshesToCreate.Count > 100 ? 3 : Math.Min(5, meshesToCreate.Count);
            }
            
            // Frame time budget: max 2ms for mesh creation
            var startTime = System.Diagnostics.Stopwatch.StartNew();
            const float maxMilliseconds = 2.0f;
            
            for (int i = 0; i < batchSize; i++)
            {
                PreparedMeshData meshData;
                lock (meshQueueLock)
                {
                    if (meshesToCreate.Count == 0) break;
                    meshData = meshesToCreate.Dequeue();
                }
                if (meshData == null) continue;
                
                ulong meshKey = meshData.MeshKey != 0 ? meshData.MeshKey : (ulong)(uint)meshData.MeshId;
                
                // Remove from queue tracking
                lock (meshQueueLock)
                {
                    meshesInQueue.Remove(meshKey);
                }
                
                if (meshConverter.IsMeshCached(meshKey))
                    continue;
                
                try
                {
                    // Create mesh with its pre-extracted data!
                    IntPtr handle = meshConverter.CreateRemixMeshFromPrepared(meshData);
                    
                    if (handle == IntPtr.Zero)
                    {
                        if (configDebugLogInterval.Value > 0)
                            logger.LogWarning($"[MeshFail] Failed to create mesh '{meshData.MeshName}' vertCount={meshData.Vertices?.Length} subMeshCount={meshData.SubmeshIndices?.Count}");
                        lock (meshQueueLock) { failedMeshKeys.Add(meshKey); }
                    }
                    
                    // Check time budget - break if exceeded
                    if (startTime.Elapsed.TotalMilliseconds > maxMilliseconds)
                        break;
                }
                catch (Exception ex)
                {
                    logger.LogWarning($"Exception creating mesh: {ex.Message}");
                }
            }
        }
        
        /// <summary>
        /// Capture skinned meshes — Remix GPU skinning primary path with BakeMesh fallback.
        /// GPU skinning: mesh created once (bind-pose + bone weights), bone transforms sent per-frame.
        /// BakeMesh fallback: when bone extraction fails (>256 bones, missing weights, etc.).
        /// </summary>
        public void CaptureSkinnedMeshes(FrameState state, int frameCount)
        {
            // Rebuild cache if stale
            if (frameCount - rendererCacheFrame > configRendererCacheDuration.Value || rendererCacheFrame < 0)
            {
                RefreshRendererCache(frameCount);
            }
            
            if (!configCaptureSkinnedMeshes.Value)
                return;
            
            skinnedCaptureCount++;
            bool doLog = configDebugLogInterval.Value > 0 && (skinnedCaptureCount % configDebugLogInterval.Value == 1);
            
            int total = cachedSkinnedRenderers.Count;
            if (total == 0) return;
            
            int gpuSkinned = 0;
            int baked = 0;
            int skipNull = 0, skipLayer = 0, skipVis = 0, skipDist = 0, skipNoMesh = 0;
            var validSkinnedIds = new HashSet<int>();
            
            Camera mainCam = cameraHandler.GetPreferredCamera();
            Vector3 camPos = mainCam != null ? mainCam.transform.position : Vector3.zero;

            if (mainCam != null)
            {
                EnsureCameraRenderersTracked(mainCam);
            }
            ProcessQueuedDynamicObjects();
            
            // BakeMesh fallback budget
            var bakeSw = System.Diagnostics.Stopwatch.StartNew();
            const float bakeMaxMs = 5.0f;
            if (skinnedRoundRobinIndex >= total) skinnedRoundRobinIndex = 0;
            var bakeFallbackQueue = new List<(int idx, SkinnedMeshRenderer smr, int skinnedId, Matrix4x4 matrix, int matId)>();
            
            for (int i = 0; i < total; i++)
            {
                var skinned = cachedSkinnedRenderers[i];
                if (skinned == null || !skinned.enabled || !skinned.gameObject.activeInHierarchy)
                {
                    skipNull++;
                    continue;
                }

                if (!skinned.updateWhenOffscreen)
                    skinned.updateWhenOffscreen = true;

                var anim = skinned.GetComponentInParent<Animator>();
                if (anim != null && anim.cullingMode != AnimatorCullingMode.AlwaysAnimate)
                    anim.cullingMode = AnimatorCullingMode.AlwaysAnimate;

                var scale = skinned.transform.lossyScale;
                if (scale.sqrMagnitude < 0.0001f)
                {
                    skipNull++;
                    continue;
                }
                
                if (IsLayerDisabled(skinned.gameObject.layer))
                {
                    skipLayer++;
                    continue;
                }

                int skinnedId = HashUtils.GetHierarchyHashInt(skinned.transform);

                if (IsRendererDisabled(skinnedId))
                {
                    skipLayer++;
                    continue;
                }
                
                if (configUseVisibilityCulling.Value && !skinned.isVisible)
                {
                    skipVis++;
                    continue;
                }
                
                if (configUseDistanceCulling.Value)
                {
                    float maxDist = configMaxRenderDistance.Value;
                    float sqrDistance = (skinned.bounds.center - camPos).sqrMagnitude;
                    if (sqrDistance > maxDist * maxDist)
                    {
                        skipDist++;
                        continue;
                    }
                }
                
                if (skinned.sharedMesh == null)
                {
                    if (configDebugLogInterval.Value > 0 && persistentSkinnedData.ContainsKey(skinnedId))
                        logger.LogWarning($"[SkinSkip] '{skinned.gameObject.name}' id={skinnedId}: sharedMesh became NULL");
                    skipNoMesh++;
                    continue;
                }
                
                validSkinnedIds.Add(skinnedId);
                
                // Compute unscaled transform (sign-only scale preserves winding)
                Vector3 ls = skinned.transform.lossyScale;
                Matrix4x4 unscaledMatrix = Matrix4x4.TRS(
                    skinned.transform.position,
                    skinned.transform.rotation,
                    new Vector3(Mathf.Sign(ls.x), Mathf.Sign(ls.y), Mathf.Sign(ls.z))
                );
                
                int matId = ResolveMaterial(skinned, doLog);
                int sharedMeshId = skinned.sharedMesh.GetInstanceID();
                
                // Try GPU skinning path: extract bone weights once, compute bone transforms each frame
                var skinData = (CachedSkinningData)null;
                if (configHardwareSkinning.Value)
                {
                    if (!cachedSkinning.ContainsKey(sharedMeshId))
                        CacheSkinningData(skinned, sharedMeshId);
                    skinData = cachedSkinning.TryGetValue(sharedMeshId, out var sd) ? sd : null;
                }
                if (skinData != null && skinned.bones != null && skinned.bones.Length > 0)
                {
                    // Compute bone transforms for Remix GPU skinning
                    var bones = skinned.bones;
                    int boneCount = Mathf.Min(bones.Length, skinData.boneCount);
                    var boneMatrices = new Matrix4x4[boneCount];
                    
                    // Bone transforms relative to instance transform:
                    //   boneTransform[i] = instanceInverse * bones[i].l2w * bindPoses[i]
                    // Remix applies: v_final = instanceTransform * Σ(w * boneTransform) * v_bind
                    //   = unscaled * unscaled^-1 * Σ(w * bones.l2w * bindPoses) * v_bind = v_world
                    Matrix4x4 invInstance = unscaledMatrix.inverse;
                    for (int b = 0; b < boneCount; b++)
                    {
                        boneMatrices[b] = (bones[b] != null)
                            ? invInstance * bones[b].localToWorldMatrix * skinData.bindPoses[b]
                            : Matrix4x4.identity;
                    }
                    
                        ulong combinedMeshHash = RemixMeshConverter.GenerateMeshHash(skinned.sharedMesh);
                        ulong baseMeshHash = combinedMeshHash; // save for logging
                        if (skinned.bones != null)
                        {
                            foreach (var b in skinned.bones)
                            {
                                if (b != null)
                                    combinedMeshHash ^= HashUtils.HashStringFNV(b.name);
                                combinedMeshHash *= 1099511628211UL;
                            }
                        }
                        
                        // Debug: log hash components once per unique mesh name
                        string meshDebugKey = skinned.sharedMesh.name + "_gpu";
                        if (!loggedHashDebugMeshes.Contains(meshDebugKey))
                        {
                            loggedHashDebugMeshes.Add(meshDebugKey);
                            string meshName = skinned.sharedMesh.name;
                            string cleanedName = meshName.Replace(" (Instance)", "").Replace(" Instance", "").Replace("(Clone)", "").Trim();
                            cleanedName = System.Text.RegularExpressions.Regex.Replace(cleanedName, @"[\s_-]*[0-9]+$", "");
                            int vertCount = skinned.sharedMesh.vertexCount;
                            int triCount = (skinned.sharedMesh != null && skinned.sharedMesh.isReadable) ? skinned.sharedMesh.triangles.Length : 0;
                            string boneNames = skinned.bones != null ? string.Join(",", System.Linq.Enumerable.Select(skinned.bones, b => b != null ? b.name : "null")) : "none";
                            logger.LogInfo($"[HashDebug-GPU] '{skinned.name}' meshName='{meshName}' cleanedName='{cleanedName}' verts={vertCount} tris={triCount} baseMeshHash=0x{baseMeshHash:X16} combinedMeshHash=0x{combinedMeshHash:X16} matId={matId} bones=[{boneNames}]");
                        }
                        
                        persistentSkinnedData[skinnedId] = new SkinnedMeshData
                        {
                            meshId = skinnedId,
                            remixMeshHash = combinedMeshHash,
                        materialId = matId,
                        vertices = skinData.bindVertices,
                        normals = skinData.bindNormals,
                        uvs = skinData.uvs,
                        colors = skinData.colors,
                        triangles = skinData.triangles,
                        localToWorld = unscaledMatrix,
                        boneTransforms = boneMatrices,
                        skinningData = skinData
                    };
                    gpuSkinned++;
                    continue;
                }
                
                // Update transform on existing persistent data
                if (persistentSkinnedData.TryGetValue(skinnedId, out var existing))
                {
                    existing.localToWorld = unscaledMatrix;
                    persistentSkinnedData[skinnedId] = existing;
                }
                
                // Queue for BakeMesh fallback
                bakeFallbackQueue.Add((i, skinned, skinnedId, unscaledMatrix, matId));
            }
            
            // Process BakeMesh fallback queue with round-robin + time budget
            if (bakeFallbackQueue.Count > 0)
            {
                int fbStart = 0;
                for (int f = 0; f < bakeFallbackQueue.Count; f++)
                {
                    if (bakeFallbackQueue[f].idx >= skinnedRoundRobinIndex) { fbStart = f; break; }
                }
                
                for (int f = 0; f < bakeFallbackQueue.Count; f++)
                {
                    int fi = (fbStart + f) % bakeFallbackQueue.Count;
                    var (idx, skinned, skinnedId, matrix, matId) = bakeFallbackQueue[fi];
                    
                    if (BakeSingleMesh(skinned, skinnedId, matId, matrix, doLog))
                        baked++;
                    
                    if (bakeSw.Elapsed.TotalMilliseconds > bakeMaxMs)
                    {
                        skinnedRoundRobinIndex = bakeFallbackQueue[(fi + 1) % bakeFallbackQueue.Count].idx;
                        break;
                    }
                }
            }
            
            // Prune any persistent entries that are no longer valid (e.g. unequipped weapons, deactivated objects)
            if (persistentSkinnedData.Count > validSkinnedIds.Count)
            {
                var staleIds = (List<int>)null;
                foreach (var id in persistentSkinnedData.Keys)
                {
                    if (!validSkinnedIds.Contains(id))
                    {
                        if (staleIds == null) staleIds = new List<int>();
                        staleIds.Add(id);
                    }
                }
                if (staleIds != null)
                {
                    for (int s = 0; s < staleIds.Count; s++)
                    {
                        persistentSkinnedData.Remove(staleIds[s]);
                    }
                }
            }
            
            // Step 5: Submit only currently valid entries to frame state
            foreach (var entry in persistentSkinnedData.Values)
            {
                if (validSkinnedIds.Contains(entry.meshId))
                {
                    state.skinned.Add(entry);
                }
            }
            

            if (doLog && total > 0)
            {
                logger.LogInfo($"CaptureSkinnedMeshes: gpuSkinned={gpuSkinned}, baked={baked}, " +
                    $"skip(null={skipNull},layer={skipLayer},vis={skipVis},dist={skipDist},mesh={skipNoMesh}), " +
                    $"cached={persistentSkinnedData.Count}, total={total}");
            }
        }

        private static readonly MethodInfo _doMeshGenerationMethod = typeof(Graphic).GetMethod("DoMeshGeneration", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly PropertyInfo _workerMeshProperty = typeof(Graphic).GetProperty("workerMesh", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        private static readonly MethodInfo _canvasRendererGetMesh = typeof(CanvasRenderer).GetMethod("GetMesh", Type.EmptyTypes);
        private static Type _tmpTextType;
        private static PropertyInfo _tmpMeshProperty;
        private static MethodInfo _tmpForceMeshUpdateMethod;
        private static bool _tmpReflectionChecked;

        private static void EnsureTmpReflection()
        {
            if (_tmpReflectionChecked) return;
            _tmpReflectionChecked = true;
            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    var t = asm.GetType("TMPro.TMP_Text");
                    if (t != null)
                    {
                        _tmpTextType = t;
                        _tmpMeshProperty = t.GetProperty("mesh", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        _tmpForceMeshUpdateMethod = t.GetMethod("ForceMeshUpdate", new Type[] { typeof(bool), typeof(bool) });
                        if (_tmpForceMeshUpdateMethod == null)
                            _tmpForceMeshUpdateMethod = t.GetMethod("ForceMeshUpdate", Type.EmptyTypes);
                        break;
                    }
                }
            }
            catch { }
        }

        private static bool IsExcludedHudElement(Graphic g)
        {
            Transform curr = g.transform;
            while (curr != null)
            {
                string name = curr.name;
                if (name.IndexOf("hud", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("overlay", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("crosshair", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }

                curr = curr.parent;
            }
            return false;
        }

        private static bool IsEquippedWeaponElement(Graphic g)
        {
            // Must be rendered by a non-overlay Canvas that moves with the viewmodel/camera hierarchy
            var canvas = g.canvas;
            if (canvas == null || canvas.renderMode == RenderMode.ScreenSpaceOverlay)
                return false;

            return true;
        }

        /// <summary>
        /// Captures world-space UI screens attached to viewmodels or camera hierarchy (e.g. digital ammo counters, holographic weapon displays).
        /// </summary>
        private void CaptureWeaponCanvasScreens(FrameState state, int frameCount)
        {
            Camera mainCam = cameraHandler?.GetPreferredCamera();
            if (mainCam == null)
                return;

            var graphics = mainCam.GetComponentsInChildren<Graphic>(false);
            if (graphics == null || graphics.Length == 0)
                return;

            EnsureTmpReflection();

            for (int i = 0; i < graphics.Length; i++)
            {
                var g = graphics[i];
                if (g == null || !g.enabled || !g.gameObject.activeInHierarchy)
                    continue;

                // Reject anything attached to the 2D HUD or overlay
                if (IsExcludedHudElement(g))
                    continue;

                // Must be mounted on an equipped weapon
                if (!IsEquippedWeaponElement(g))
                    continue;

                var canvas = g.canvas;
                if (canvas == null || canvas.renderMode == RenderMode.ScreenSpaceOverlay)
                    continue;

                Color col = g.color;
                if (col.a <= 0.001f)
                    continue;

                Mesh mesh = null;
                try
                {
                    if (_tmpTextType != null && _tmpTextType.IsInstanceOfType(g))
                    {
                        if (_tmpForceMeshUpdateMethod != null)
                        {
                            var parms = _tmpForceMeshUpdateMethod.GetParameters();
                            if (parms.Length == 2)
                                _tmpForceMeshUpdateMethod.Invoke(g, new object[] { false, false });
                            else
                                _tmpForceMeshUpdateMethod.Invoke(g, null);
                        }
                        if (_tmpMeshProperty != null)
                            mesh = (Mesh)_tmpMeshProperty.GetValue(g, null);
                    }

                    if (mesh == null || mesh.vertexCount == 0)
                    {
                        if (_doMeshGenerationMethod != null)
                            _doMeshGenerationMethod.Invoke(g, null);
                        if (_workerMeshProperty != null)
                            mesh = (Mesh)_workerMeshProperty.GetValue(null);
                    }
                }
                catch { }

                if (mesh == null || mesh.vertexCount == 0)
                {
                    var cr = g.canvasRenderer;
                    if (cr != null && _canvasRendererGetMesh != null)
                    {
                        try { mesh = (Mesh)_canvasRendererGetMesh.Invoke(cr, null); } catch { }
                    }
                }

                Vector3[] verts = null;
                Vector3[] normals = null;
                Vector2[] uvs = null;
                Color32[] colors = null;
                int[] tris = null;

                if (mesh != null && mesh.vertexCount > 0 && mesh.isReadable)
                {
                    try
                    {
                        verts = mesh.vertices;
                        normals = mesh.normals;
                        uvs = mesh.uv;
                        colors = mesh.colors32;
                        tris = mesh.triangles;
                    }
                    catch { }
                }

                if (verts == null || verts.Length == 0 || tris == null || tris.Length == 0)
                {
                    Rect r = g.rectTransform.rect;
                    if (r.width <= 0.001f || r.height <= 0.001f)
                        continue;

                    verts = new Vector3[]
                    {
                        new Vector3(r.xMin, r.yMin, 0f),
                        new Vector3(r.xMin, r.yMax, 0f),
                        new Vector3(r.xMax, r.yMax, 0f),
                        new Vector3(r.xMax, r.yMin, 0f)
                    };
                    normals = new Vector3[]
                    {
                        Vector3.back, Vector3.back, Vector3.back, Vector3.back
                    };
                    uvs = new Vector2[]
                    {
                        new Vector2(0f, 0f),
                        new Vector2(0f, 1f),
                        new Vector2(1f, 1f),
                        new Vector2(1f, 0f)
                    };
                    colors = new Color32[] { col, col, col, col };
                    tris = new int[]
                    {
                        0, 1, 2,
                        0, 2, 3
                    };
                }

                if (normals == null || normals.Length != verts.Length)
                {
                    normals = new Vector3[verts.Length];
                    for (int n = 0; n < normals.Length; n++)
                        normals[n] = Vector3.back;
                }

                if (uvs == null || uvs.Length != verts.Length)
                {
                    uvs = new Vector2[verts.Length];
                }

                // Encode g.color into vertex colors so each graphic's tint is preserved.
                // The material ID is static (mat+tex hash only), so per-graphic color goes into vertex buffer.
                if (colors == null || colors.Length != verts.Length)
                {
                    Color32 c = col; // col == g.color captured above
                    colors = new Color32[verts.Length];
                    for (int cIdx = 0; cIdx < colors.Length; cIdx++)
                        colors[cIdx] = c;
                }
                else
                {
                    // Multiply existing mesh vertex colors by g.color to apply tint
                    byte tr = (byte)Mathf.RoundToInt(col.r * 255f);
                    byte tg = (byte)Mathf.RoundToInt(col.g * 255f);
                    byte tb = (byte)Mathf.RoundToInt(col.b * 255f);
                    byte ta = (byte)Mathf.RoundToInt(col.a * 255f);
                    for (int cIdx = 0; cIdx < colors.Length; cIdx++)
                    {
                        colors[cIdx] = new Color32(
                            (byte)((colors[cIdx].r * tr) / 255),
                            (byte)((colors[cIdx].g * tg) / 255),
                            (byte)((colors[cIdx].b * tb) / 255),
                            (byte)((colors[cIdx].a * ta) / 255)
                        );
                    }
                }

                // IMPORTANT: Use g.materialForRendering for the texture lookup but g.defaultMaterial
                // for the material ID hash. g.materialForRendering can return an instanced material
                // clone (different GetInstanceID every frame) which would flood Remix's material table.
                Material sharedMat = g.defaultMaterial;
                if (sharedMat == null) continue;

                // For the actual texture we want from this graphic, use its mainTexture directly
                Texture2D mainTex = g.mainTexture as Texture2D;

                // Try to read the actual canvas renderer color in case the graphic color doesn't reflect
                // the true tint (e.g. Revolver battery MeshRenderer sets MPB _Color, not g.color)
                Color crColor = col;
                var cr2 = g.canvasRenderer;
                if (cr2 != null)
                    crColor = cr2.GetColor();
                // If canvas renderer color is still white, keep using g.color (col)
                if (crColor.r > 0.98f && crColor.g > 0.98f && crColor.b > 0.98f && crColor.a > 0.98f)
                    crColor = col;

                // Re-apply vertex colors using the canvas renderer color (more accurate tint source)
                if (crColor != col && crColor != Color.white)
                {
                    byte tr2 = (byte)Mathf.RoundToInt(crColor.r * 255f);
                    byte tg2 = (byte)Mathf.RoundToInt(crColor.g * 255f);
                    byte tb2 = (byte)Mathf.RoundToInt(crColor.b * 255f);
                    byte ta2 = (byte)Mathf.RoundToInt(crColor.a * 255f);
                    for (int cIdx = 0; cIdx < colors.Length; cIdx++)
                    {
                        colors[cIdx] = new Color32(
                            (byte)((colors[cIdx].r * tr2) / 255),
                            (byte)((colors[cIdx].g * tg2) / 255),
                            (byte)((colors[cIdx].b * tb2) / 255),
                            (byte)((colors[cIdx].a * ta2) / 255)
                        );
                    }
                }

                if (configDebugLogInterval.Value > 0 && frameCount % 300 == 1)
                    logger.LogInfo($"[UIGraphic] '{g.gameObject.name}' g.color=({col.r:F2},{col.g:F2},{col.b:F2},{col.a:F2}) crColor=({crColor.r:F2},{crColor.g:F2},{crColor.b:F2}) mat='{sharedMat.name}' tex='{mainTex?.name}'");

                int texHash = mainTex != null ? mainTex.GetInstanceID() : 0;
                int uiMatId = HashCombine(sharedMat.GetInstanceID(), texHash);

                materialManager.CaptureMaterialTextures(
                    sharedMat,
                    uiMatId,
                    mpbEmissiveColor: Color.white,
                    mpbEmissiveIntensity: 1.0f,
                    mpbMainTex: mainTex,
                    mpbColor: Color.white,
                    forcedAlphaMode: RemixMaterialManager.AlphaMode.Blend
                );

                int graphicId = g.GetInstanceID();
                ulong remixMeshHash = (ulong)(uint)graphicId | 0x8000000000000000UL;

                state.skinned.Add(new SkinnedMeshData
                {
                    meshId = graphicId,
                    remixMeshHash = remixMeshHash,
                    materialId = uiMatId,
                    vertices = verts,
                    normals = normals,
                    uvs = uvs,
                    colors = colors,
                    triangles = tris,
                    localToWorld = g.rectTransform.localToWorldMatrix,
                    boneTransforms = null,
                    skinningData = null
                });
            }
        }

        /// <summary>
        /// Captures dynamic line and trail renderers (e.g. Revolver bullet beams, Railcannon beam).
        /// Bakes camera-facing geometry in world space and applies emissive materials.
        /// </summary>
        private void CaptureLineRenderers(FrameState state, int frameCount)
        {
            Camera mainCam = cameraHandler?.GetPreferredCamera();
            Vector3 camPos = mainCam != null ? mainCam.transform.position : Vector3.zero;
            float maxDist = configUseDistanceCulling.Value ? configMaxRenderDistance.Value : float.MaxValue;
            float sqrMaxDist = maxDist * maxDist;

            if (cachedLineRenderers == null || frameCount - lastLineRendererRefreshFrame > 30)
            {
                cachedLineRenderers = UnityEngine.Object.FindObjectsOfType<LineRenderer>();
                lastLineRendererRefreshFrame = frameCount;
            }
            var lines = cachedLineRenderers;
            if (lines == null || lines.Length == 0) return;

            if (sharedTempLineMesh == null)
            {
                sharedTempLineMesh = new Mesh();
                sharedTempLineMesh.name = "Remix_SharedLineMesh";
            }

            int slot = 0;
            for (int i = 0; i < lines.Length && slot < MAX_LINE_SLOTS; i++)
            {
                var lr = lines[i];
                if (lr == null || !lr.enabled || !lr.gameObject.activeInHierarchy)
                    continue;

                if (lr.positionCount < 2 || lr.widthMultiplier <= 0.0001f)
                    continue;

                int layer = lr.gameObject.layer;
                if (layer == 5 || IsLayerDisabled(layer))
                    continue;

                if (configUseDistanceCulling.Value)
                {
                    if (lr.bounds.SqrDistance(camPos) > sqrMaxDist)
                        continue;
                }

                sharedTempLineMesh.Clear();
                try
                {
                    if (mainCam != null)
                        lr.BakeMesh(sharedTempLineMesh, mainCam, false);
                    else
                        lr.BakeMesh(sharedTempLineMesh, false);
                }
                catch
                {
                    continue;
                }

                int vertCount = sharedTempLineMesh.vertexCount;
                if (vertCount < 3)
                    continue;

                var tris = sharedTempLineMesh.triangles;
                if (tris == null || tris.Length == 0 || tris.Length % 3 != 0)
                    continue;

                var verts = sharedTempLineMesh.vertices;
                var uvs = sharedTempLineMesh.uv;
                if (uvs == null || uvs.Length != vertCount)
                    uvs = new Vector2[vertCount];

                var norms = sharedTempLineMesh.normals;
                if (norms == null || norms.Length != vertCount)
                {
                    Vector3 norm = mainCam != null ? -mainCam.transform.forward : Vector3.up;
                    norms = new Vector3[vertCount];
                    for (int n = 0; n < vertCount; n++) norms[n] = norm;
                }

                Material mat = lr.sharedMaterial;
                if (mat == null)
                {
                    if (fallbackSpriteMaterial == null)
                    {
                        var pShader = Shader.Find("Sprites/Default");
                        if (pShader != null) fallbackSpriteMaterial = new Material(pShader);
                    }
                    mat = fallbackSpriteMaterial;
                }
                if (mat == null)
                    continue;

                Color lrCol = Color.white;
                try
                {
                    var cg = lr.colorGradient;
                    if (cg != null && cg.colorKeys != null && cg.colorKeys.Length > 0)
                    {
                        lrCol = cg.colorKeys[0].color;
                    }
                    else if (lr.startColor.a > 0.001f)
                    {
                        lrCol = lr.startColor;
                    }
                }
                catch
                {
                    if (lr.startColor.a > 0.001f) lrCol = lr.startColor;
                }
                if (lrCol.r <= 0.001f && lrCol.g <= 0.001f && lrCol.b <= 0.001f)
                    lrCol = Color.white;

                var colors = sharedTempLineMesh.colors32;
                if (colors == null || colors.Length != vertCount)
                {
                    colors = new Color32[vertCount];
                    Color32 c32 = lrCol;
                    for (int c = 0; c < vertCount; c++) colors[c] = c32;
                }

                int dynamicMatId = mat.GetInstanceID();

                materialManager.CaptureMaterialTextures(
                    mat,
                    dynamicMatId,
                    mpbEmissiveColor: Color.white,
                    mpbEmissiveIntensity: 3.0f,
                    mpbColor: Color.white,
                    forcedAlphaMode: RemixMaterialManager.AlphaMode.Blend
                );

                ulong meshHash = (ulong)(uint)lr.GetInstanceID() | 0x4000000000000000UL;
                slot++;

                if (logger != null && frameCount % 60 == 0)
                {
                    logger.LogInfo($"[LineCapture] '{lr.gameObject.name}' id={lr.GetInstanceID()} verts={vertCount} w={lr.widthMultiplier:F2} col=({lrCol.r:F2},{lrCol.g:F2},{lrCol.b:F2})");
                }

                state.skinned.Add(new SkinnedMeshData
                {
                    meshId = lr.GetInstanceID(),
                    remixMeshHash = meshHash,
                    materialId = dynamicMatId,
                    vertices = verts,
                    normals = norms,
                    uvs = uvs,
                    colors = colors,
                    triangles = tris,
                    localToWorld = Matrix4x4.identity,
                    boneTransforms = null,
                    skinningData = null
                });
            }
        }

        /// <summary>
        /// Captures dynamic sprite renderers (e.g. Revolver muzzle flash bursts).
        /// Generates two-sided camera-facing quads and applies high emissive intensity.
        /// </summary>
        private void CaptureSpriteRenderers(FrameState state, int frameCount)
        {
            Camera mainCam = cameraHandler?.GetPreferredCamera();
            Vector3 camPos = mainCam != null ? mainCam.transform.position : Vector3.zero;
            float maxDist = configUseDistanceCulling.Value ? configMaxRenderDistance.Value : float.MaxValue;
            float sqrMaxDist = maxDist * maxDist;

            if (cachedSpriteRenderers == null || frameCount - lastSpriteRendererRefreshFrame > 30)
            {
                cachedSpriteRenderers = UnityEngine.Object.FindObjectsOfType<SpriteRenderer>();
                lastSpriteRendererRefreshFrame = frameCount;
            }
            var sprites = cachedSpriteRenderers;
            if (sprites == null || sprites.Length == 0) return;

            int slot = 0;
            for (int i = 0; i < sprites.Length && slot < MAX_SPRITE_SLOTS; i++)
            {
                var sr = sprites[i];
                if (sr == null || !sr.enabled || !sr.gameObject.activeInHierarchy)
                    continue;

                // Ignore invisible sprites (e.g. twirlSprite when not spinning, or fading effects)
                if (sr.color.a <= 0.005f)
                    continue;

                Sprite s = sr.sprite;
                if (s == null)
                    continue;

                int layer = sr.gameObject.layer;
                if (layer == 5 || IsLayerDisabled(layer))
                    continue;

                if (configUseDistanceCulling.Value)
                {
                    if (sr.bounds.SqrDistance(camPos) > sqrMaxDist)
                        continue;
                }

                Vector3[] verts;
                int[] tris;
                Vector2[] uvs;

                Vector2[] sVerts = null;
                ushort[] sTris = null;
                Vector2[] sUvs = null;

                try
                {
                    sVerts = s.vertices;
                    sTris = s.triangles;
                    sUvs = s.uv;
                }
                catch { }

                if (sVerts != null && sVerts.Length >= 3 && sTris != null && sTris.Length >= 3)
                {
                    int vertCount = sVerts.Length;
                    verts = new Vector3[vertCount];
                    bool flipX = sr.flipX;
                    bool flipY = sr.flipY;
                    for (int v = 0; v < vertCount; v++)
                    {
                        float vx = flipX ? -sVerts[v].x : sVerts[v].x;
                        float vy = flipY ? -sVerts[v].y : sVerts[v].y;
                        verts[v] = new Vector3(vx, vy, 0f);
                    }

                    int triCount = sTris.Length;
                    tris = new int[triCount * 2];
                    for (int t = 0; t < triCount; t++)
                        tris[t] = sTris[t];
                    // Backfaces for two-sided visibility
                    for (int t = 0; t < triCount; t += 3)
                    {
                        tris[triCount + t] = sTris[t + 2];
                        tris[triCount + t + 1] = sTris[t + 1];
                        tris[triCount + t + 2] = sTris[t];
                    }

                    uvs = sUvs != null && sUvs.Length == vertCount ? sUvs : new Vector2[vertCount];
                }
                else
                {
                    // Fallback to bounding quad
                    Bounds sb = s.bounds;
                    Vector3 min = sb.min;
                    Vector3 max = sb.max;
                    verts = new Vector3[]
                    {
                        new Vector3(min.x, min.y, 0f),
                        new Vector3(max.x, min.y, 0f),
                        new Vector3(max.x, max.y, 0f),
                        new Vector3(min.x, max.y, 0f)
                    };
                    uvs = new Vector2[]
                    {
                        new Vector2(0f, 0f),
                        new Vector2(1f, 0f),
                        new Vector2(1f, 1f),
                        new Vector2(0f, 1f)
                    };
                    tris = new int[]
                    {
                        0, 1, 2, 0, 2, 3,
                        2, 1, 0, 3, 2, 0
                    };
                }

                int totalVerts = verts.Length;
                Vector3 norm = Vector3.back;
                var norms = new Vector3[totalVerts];
                for (int n = 0; n < totalVerts; n++) norms[n] = norm;

                Color32 c32 = sr.color;
                var colors = new Color32[totalVerts];
                for (int c = 0; c < totalVerts; c++) colors[c] = c32;

                Material mat = sr.sharedMaterial;
                if (mat == null)
                {
                    if (fallbackSpriteMaterial == null)
                    {
                        var sprShader = Shader.Find("Sprites/Default");
                        if (sprShader != null)
                            fallbackSpriteMaterial = new Material(sprShader);
                    }
                    mat = fallbackSpriteMaterial;
                }

                Texture2D tex = s.texture;
                Color srColor = sr.color;

                bool isMuzzle = sr.gameObject.name.IndexOf("muzzle", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    (sr.transform.parent != null && sr.transform.parent.name.IndexOf("muzzle", StringComparison.OrdinalIgnoreCase) >= 0);
                float emIntensity = isMuzzle ? 5.0f : 1.0f;

                if (configDebugLogInterval.Value > 0 && (isMuzzle || sr.gameObject.name.IndexOf("beep", StringComparison.OrdinalIgnoreCase) >= 0 || (sr.transform.parent != null && sr.transform.parent.name.IndexOf("beep", StringComparison.OrdinalIgnoreCase) >= 0)))
                {
                    logger.LogInfo($"[SpriteDiag] '{sr.gameObject.name}' parent='{sr.transform.parent?.name}' color=({srColor.r:F3},{srColor.g:F3},{srColor.b:F3},{srColor.a:F3}) emInt={emIntensity} mat='{mat?.name}' tex='{tex?.name}'");
                }

                // Scale clamping for muzzle flashes: prevent gigantic muzzle flashes (e.g. sawblade launcher 24m-48m)
                Matrix4x4 l2w = sr.transform.localToWorldMatrix;
                if (isMuzzle)
                {
                    Vector3 lossy = sr.transform.lossyScale;
                    float maxDim = Mathf.Max(Mathf.Abs(lossy.x), Mathf.Max(Mathf.Abs(lossy.y), Mathf.Abs(lossy.z)));
                    if (maxDim > 2.5f)
                    {
                        float rescale = 2.0f / maxDim;
                        Vector4 c0 = l2w.GetColumn(0) * rescale;
                        Vector4 c1 = l2w.GetColumn(1) * rescale;
                        Vector4 c2 = l2w.GetColumn(2) * rescale;
                        l2w.SetColumn(0, c0);
                        l2w.SetColumn(1, c1);
                        l2w.SetColumn(2, c2);
                    }
                }

                int texHash = tex != null ? tex.GetInstanceID() : 0;
                int srMatId = HashCombine(mat != null ? mat.GetInstanceID() : 0, texHash);

                materialManager.CaptureMaterialTextures(
                    mat,
                    srMatId,
                    mpbEmissiveColor: Color.white,
                    mpbEmissiveIntensity: emIntensity,
                    mpbMainTex: tex,
                    mpbColor: Color.white
                );

                ulong meshHash = (ulong)(uint)sr.GetInstanceID() | 0x2000000000000000UL;
                slot++;

                state.skinned.Add(new SkinnedMeshData
                {
                    meshId = sr.GetInstanceID(),
                    remixMeshHash = meshHash,
                    materialId = srMatId,
                    vertices = verts,
                    normals = norms,
                    uvs = uvs,
                    colors = colors,
                    triangles = tris,
                    localToWorld = l2w,
                    boneTransforms = null,
                    skinningData = null
                });
            }
        }

        private static Type _bsmType;
        private static UnityEngine.Object _cachedBsmInstance;
        private static FieldInfo _bsmCurrentBloodCountField;
        private static FieldInfo _bsmTotalStainMeshField;
        private static FieldInfo _bsmStainMatField;
        private static FieldInfo _bsmUsedComputeField;
        private static FieldInfo _bsmMeshDirtyField;
        private static MethodInfo _bsmRebuildMeshMethod;
        private static bool _bsmReflectionInitialized;
        private static bool _disabledComputeShadersSet;
        private static bool _loggedBloodShaderProps;
        private int _lastLoggedBloodCount = -1;

        // Blood decal mesh caching to prevent re-extracting and BLAS rebuilding every frame
        private Vector3[] _cachedBloodVerts;
        private Vector3[] _cachedBloodNorms;
        private Vector2[] _cachedBloodUvs;
        private Color32[] _cachedBloodColors;
        private int[] _cachedBloodTris;
        private int _lastCapturedBloodCount = -1;
        private int _lastCapturedMeshVerts = -1;
        private static uint _bloodMeshVersion = 1;
        private static ulong _currentBloodMeshHash = 0x7B100D0000000001UL;

        /// <summary>
        /// Captures dynamic blood splatter decal geometry generated by BloodsplatterManager.
        /// ULTRAKILL draws blood stains on walls and floors into an offscreen CommandBuffer,
        /// which RTX Remix cannot see directly. When compute shaders are disabled, ULTRAKILL's
        /// CPU job (GenerateBloodMeshJob) automatically combines all blood decals into totalStainMesh.
        /// We capture totalStainMesh and submit it directly to RTX Remix in world space!
        /// </summary>
        private void CaptureBloodStains(FrameState state, int frameCount)
        {
            if (!_bsmReflectionInitialized)
            {
                _bsmReflectionInitialized = true;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (_bsmType == null)
                    {
                        var t = asm.GetType("BloodsplatterManager");
                        if (t != null)
                        {
                            _bsmType = t;
                            _bsmCurrentBloodCountField = t.GetField("currentBloodCount", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                            _bsmTotalStainMeshField = t.GetField("totalStainMesh", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                            _bsmStainMatField = t.GetField("stainMat", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                            _bsmUsedComputeField = t.GetField("usedComputeShadersAtStart", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                            _bsmMeshDirtyField = t.GetField("meshDirty", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                            _bsmRebuildMeshMethod = t.GetMethod("RebuildMesh", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                            logger.LogInfo("[BloodCapture] Found BloodsplatterManager type and fields!");
                        }
                    }
                    if (!_disabledComputeShadersSet)
                    {
                        var gsType = asm.GetType("SettingsMenu.Components.Pages.GraphicsSettings");
                        if (gsType != null)
                        {
                            var f = gsType.GetField("disabledComputeShaders", BindingFlags.Public | BindingFlags.Static);
                            if (f != null)
                            {
                                f.SetValue(null, true);
                                _disabledComputeShadersSet = true;
                                logger.LogInfo("[BloodCapture] Set GraphicsSettings.disabledComputeShaders = true");
                            }
                        }
                    }
                }
            }

            if (_bsmType == null) return;

            if (_cachedBsmInstance == null)
            {
                _cachedBsmInstance = UnityEngine.Object.FindObjectOfType(_bsmType);
            }
            var bsm = _cachedBsmInstance;
            if (bsm == null) return;

            // Ensure compute shaders are disabled so ULTRAKILL builds totalStainMesh via GenerateBloodMeshJob
            if (_bsmUsedComputeField != null)
            {
                bool usedCompute = (bool)_bsmUsedComputeField.GetValue(bsm);
                if (usedCompute)
                {
                    _bsmUsedComputeField.SetValue(bsm, false);
                    _bsmMeshDirtyField?.SetValue(bsm, true);
                    _bsmRebuildMeshMethod?.Invoke(bsm, null);
                    logger.LogInfo("[BloodCapture] Switched BloodsplatterManager to CPU mesh path (usedComputeShadersAtStart = false)");
                }
            }

            int bloodCount = _bsmCurrentBloodCountField != null ? (int)_bsmCurrentBloodCountField.GetValue(bsm) : 0;
            Mesh stainMesh = _bsmTotalStainMeshField != null ? _bsmTotalStainMeshField.GetValue(bsm) as Mesh : null;
            Material stainMat = _bsmStainMatField != null ? _bsmStainMatField.GetValue(bsm) as Material : null;

            // Log diagnostics every 300 frames if enabled
            bool shouldLog = (frameCount % 300 == 0) && (configDebugLogInterval.Value > 0);
            if (shouldLog)
            {
                _lastLoggedBloodCount = bloodCount;
                logger.LogInfo($"[BloodDiag] frame={frameCount} count={bloodCount} meshVerts={(stainMesh != null ? stainMesh.vertexCount : 0)} meshTris={(stainMesh != null ? stainMesh.triangles.Length : 0)} mat='{stainMat?.name}' shader='{stainMat?.shader?.name}' mainTex='{stainMat?.mainTexture?.name}'");
            }

            // Dump shader properties once to understand the blood material layout
            if (stainMat != null && !_loggedBloodShaderProps)
            {
                _loggedBloodShaderProps = true;
                var shader = stainMat.shader;
                if (shader != null)
                {
                    var sb = new System.Text.StringBuilder();
                    sb.Append($"[BloodMatProps] shader='{shader.name}' renderQueue={stainMat.renderQueue}: ");
                    int pCount = shader.GetPropertyCount();
                    for (int p = 0; p < pCount; p++)
                    {
                        var pName = shader.GetPropertyName(p);
                        var pType = shader.GetPropertyType(p);
                        sb.Append($"{pName}({pType}) ");
                    }
                    logger.LogInfo(sb.ToString());
                }
            }

            if (bloodCount <= 0 || stainMesh == null || stainMesh.vertexCount == 0)
            {
                // If blood exists but mesh is empty, trigger rebuild
                if (bloodCount > 0 && (stainMesh == null || stainMesh.vertexCount == 0))
                {
                    _bsmMeshDirtyField?.SetValue(bsm, true);
                    _bsmRebuildMeshMethod?.Invoke(bsm, null);
                }
                return;
            }

            bool isDirty = (bloodCount != _lastCapturedBloodCount) ||
                           (stainMesh.vertexCount != _lastCapturedMeshVerts) ||
                           _cachedBloodVerts == null;

            if (isDirty)
            {
                _bloodMeshVersion++;
                _currentBloodMeshHash = 0x7B100D0000000000UL | (ulong)_bloodMeshVersion;

                // Extract mesh data from totalStainMesh
                Vector3[] rawVerts = stainMesh.vertices;
                Vector3[] rawNorms = stainMesh.normals;
                int[] rawTris = stainMesh.triangles;
                if (rawVerts == null || rawVerts.Length == 0 || rawTris == null || rawTris.Length == 0)
                    return;

                int quadCount = rawVerts.Length / 4;
                Vector3[] verts = new Vector3[rawVerts.Length];
                Vector3[] norms = new Vector3[rawVerts.Length];
                int[] tris = new int[quadCount * 6];
                bool hasRawNorms = rawNorms != null && rawNorms.Length == rawVerts.Length;

                for (int s = 0; s < quadCount; s++)
                {
                    int vBase = s * 4;
                    Vector3 v0 = rawVerts[vBase + 0];
                    Vector3 v1 = rawVerts[vBase + 1];
                    Vector3 v2 = rawVerts[vBase + 2];
                    Vector3 v3 = rawVerts[vBase + 3];

                    // Read surface normal directly from stainMesh.normals (stored as VertexAttribute.Normal by GenerateBloodMeshJob)
                    Vector3 outNorm = Vector3.zero;
                    if (hasRawNorms)
                    {
                        outNorm = rawNorms[vBase + 0];
                    }
                    if (outNorm.sqrMagnitude < 0.001f)
                    {
                        outNorm = Vector3.Cross(v2 - v0, v1 - v0).normalized;
                    }
                    if (outNorm.sqrMagnitude < 0.001f)
                    {
                        outNorm = Vector3.up;
                    }

                    // Push vertices outward along normal (12mm base + 0.5mm step per layer up to 20mm).
                    // This clears floor/wall brush seams and collider discrepancies across wide 2.5m quads,
                    // while eliminating Z-fighting between overlapping blood decals.
                    float layerOffset = 0.012f + ((s % 16) * 0.0005f);
                    Vector3 offset = outNorm * layerOffset;
                    verts[vBase + 0] = v0 + offset;
                    verts[vBase + 1] = v1 + offset;
                    verts[vBase + 2] = v2 + offset;
                    verts[vBase + 3] = v3 + offset;

                    norms[vBase + 0] = outNorm;
                    norms[vBase + 1] = outNorm;
                    norms[vBase + 2] = outNorm;
                    norms[vBase + 3] = outNorm;

                    // Invert winding order so triangles face outward into the room: (0, 2, 1) and (0, 3, 2)
                    int tBase = s * 6;
                    tris[tBase + 0] = vBase + 0;
                    tris[tBase + 1] = vBase + 2;
                    tris[tBase + 2] = vBase + 1;
                    tris[tBase + 3] = vBase + 0;
                    tris[tBase + 4] = vBase + 3;
                    tris[tBase + 5] = vBase + 2;
                }

                // ULTRAKILL's _SplatterAtlas contains 5 horizontal sub-splatters (160x32 pixels, 5 cells of 32x32).
                // GenerateBloodMeshJob creates 4 vertices per quad with base UVs (0,0), (1,0), (1,1), (0,1).
                // In ULTRAKILL's vertex shader, each quad's UV.x is scaled by 0.2 and offset by (index % 5) * 0.2.
                // We compute the sub-atlas UVs directly here so RTX Remix samples the correct splatter shape!
                Vector2[] rawUvs = stainMesh.uv;
                Vector2[] uvs = new Vector2[verts.Length];
                for (int s = 0; s < quadCount; s++)
                {
                    int vBase = s * 4;
                    float uOffset = (s % 5) * 0.2f;
                    if (rawUvs != null && rawUvs.Length == verts.Length)
                    {
                        uvs[vBase + 0] = new Vector2(uOffset + rawUvs[vBase + 0].x * 0.2f, rawUvs[vBase + 0].y);
                        uvs[vBase + 1] = new Vector2(uOffset + rawUvs[vBase + 1].x * 0.2f, rawUvs[vBase + 1].y);
                        uvs[vBase + 2] = new Vector2(uOffset + rawUvs[vBase + 2].x * 0.2f, rawUvs[vBase + 2].y);
                        uvs[vBase + 3] = new Vector2(uOffset + rawUvs[vBase + 3].x * 0.2f, rawUvs[vBase + 3].y);
                    }
                    else
                    {
                        uvs[vBase + 0] = new Vector2(uOffset, 0f);
                        uvs[vBase + 1] = new Vector2(uOffset + 0.2f, 0f);
                        uvs[vBase + 2] = new Vector2(uOffset + 0.2f, 1f);
                        uvs[vBase + 3] = new Vector2(uOffset, 1f);
                    }
                }

                // White vertex colors (texture is already tinted blood red)
                Color32 bloodColor = new Color32(255, 255, 255, 255);
                Color32[] colors = new Color32[verts.Length];
                for (int i = 0; i < verts.Length; i++) colors[i] = bloodColor;

                _cachedBloodVerts = verts;
                _cachedBloodNorms = norms;
                _cachedBloodTris = tris;
                _cachedBloodUvs = uvs;
                _cachedBloodColors = colors;
                _lastCapturedBloodCount = bloodCount;
                _lastCapturedMeshVerts = stainMesh.vertexCount;
            }

            if (_cachedBloodVerts == null || _cachedBloodVerts.Length == 0)
                return;

            int bloodMatId = stainMat != null ? stainMat.GetInstanceID() : 0x7B100D01;
            if (stainMat != null)
            {
                Texture2D bloodTex = null;
                if (stainMat.HasProperty("_SplatterAtlas"))
                    bloodTex = stainMat.GetTexture("_SplatterAtlas") as Texture2D;
                else if (stainMat.HasProperty("_MainTex"))
                    bloodTex = stainMat.GetTexture("_MainTex") as Texture2D;

                materialManager.CaptureMaterialTextures(
                    stainMat,
                    bloodMatId,
                    mpbMainTex: bloodTex,
                    mpbColor: new Color(0.70f, 0.02f, 0.02f, 0.75f),
                    forcedAlphaMode: RemixMaterialManager.AlphaMode.Blend,
                    forcedAlphaCutoff: 0.05f
                );
            }

            state.skinned.Add(new SkinnedMeshData
            {
                meshId = 0x7B100D01,
                remixMeshHash = _currentBloodMeshHash,
                materialId = bloodMatId,
                vertices = _cachedBloodVerts,
                normals = _cachedBloodNorms,
                uvs = _cachedBloodUvs,
                colors = _cachedBloodColors,
                triangles = _cachedBloodTris,
                localToWorld = Matrix4x4.identity,
                boneTransforms = null,
                skinningData = null,
                isStaticData = !isDirty
            });
        }

        /// <summary>
        /// Captures dynamic non-skinned effects: weapon canvas screens, line/trail beams, sprites, and blood decals.
        /// Decoupled from CaptureSkinnedMeshes so they never disappear based on weapon model type or enemy skinned mesh counts.
        /// </summary>
        public void CaptureDynamicEffects(FrameState state, int frameCount)
        {
            // Capture world-space weapon UI screens (e.g. Nailgun ammo counter/heat, Shotgun slider, Rocket Launcher timer)
            try
            {
                using (RemixTracy.Zone("CaptureWeaponCanvasScreens"))
                {
                    CaptureWeaponCanvasScreens(state, frameCount);
                }
            }
            catch (Exception ex) { if (configDebugLogInterval.Value > 0 && frameCount % 300 == 0) logger.LogWarning($"[DynamicEffects] CaptureWeaponCanvasScreens error: {ex.Message}"); }

            // Capture dynamic line and trail renderers (e.g. Revolver bullet trail, Railcannon beam)
            try
            {
                using (RemixTracy.Zone("CaptureLineRenderers"))
                {
                    CaptureLineRenderers(state, frameCount);
                }
            }
            catch (Exception ex) { if (configDebugLogInterval.Value > 0 && frameCount % 300 == 0) logger.LogWarning($"[DynamicEffects] CaptureLineRenderers error: {ex.Message}"); }

            // Capture dynamic sprite renderers (e.g. Revolver muzzle flash)
            try
            {
                using (RemixTracy.Zone("CaptureSpriteRenderers"))
                {
                    CaptureSpriteRenderers(state, frameCount);
                }
            }
            catch (Exception ex) { if (configDebugLogInterval.Value > 0 && frameCount % 300 == 0) logger.LogWarning($"[DynamicEffects] CaptureSpriteRenderers error: {ex.Message}"); }

            // Capture blood splatter decals (BloodsplatterManager on floors/walls)
            try
            {
                using (RemixTracy.Zone("CaptureBloodStains"))
                {
                    CaptureBloodStains(state, frameCount);
                }
            }
            catch (Exception ex) { if (configDebugLogInterval.Value > 0 && frameCount % 300 == 0) logger.LogWarning($"[DynamicEffects] CaptureBloodStains error: {ex.Message}"); }

            // Capture active particle systems (blood splatters, rain, smoke, sparks, etc.)
            try
            {
                using (RemixTracy.Zone("CaptureParticleSystems"))
                {
                    CaptureParticleSystems(state, frameCount);
                }
            }
            catch (Exception ex) { if (configDebugLogInterval.Value > 0 && frameCount % 300 == 0) logger.LogWarning($"[DynamicEffects] CaptureParticleSystems error: {ex.Message}"); }

            // Capture active trail renderers (bullets, gibs, blood trails, projectiles)
            try
            {
                using (RemixTracy.Zone("CaptureTrailRenderers"))
                {
                    CaptureTrailRenderers(state, frameCount);
                }
            }
            catch (Exception ex) { if (configDebugLogInterval.Value > 0 && frameCount % 300 == 0) logger.LogWarning($"[DynamicEffects] CaptureTrailRenderers error: {ex.Message}"); }
        }
        
        /// <summary>
        /// Capture active particle systems (both pooled and dynamic) using zero-allocation billboard extraction.
        /// </summary>
        public void CaptureParticleSystems(FrameState state, int frameCount)
        {
            if (configCaptureParticles != null && !configCaptureParticles.Value)
                return;

            Camera mainCam = cameraHandler?.GetPreferredCamera() ?? Camera.main;
            if (mainCam == null || !mainCam.gameObject.activeInHierarchy || !mainCam.enabled || Time.frameCount < 10)
                return;

            Vector3 camRight = mainCam.transform.right;
            Vector3 camUp = mainCam.transform.up;
            Vector3 camForward = mainCam.transform.forward;

            // Check active particle renderers every frame to capture transient particles immediately (bullet impacts, sparks, newly spawned systems)
            var activeRenderers = UnityEngine.Object.FindObjectsOfType<ParticleSystemRenderer>(false);
            for (int a = 0; a < activeRenderers.Length; a++)
            {
                var ar = activeRenderers[a];
                if (ar != null && trackedParticleSystemIds.Add(ar.GetInstanceID()))
                {
                    var aps = ar.GetComponent<ParticleSystem>();
                    if (aps != null)
                    {
                        var main = aps.main;
                        if (main.cullingMode != ParticleSystemCullingMode.AlwaysSimulate)
                            main.cullingMode = ParticleSystemCullingMode.AlwaysSimulate;

                        trackedParticleSystems.Add(new TrackedParticleSystem
                        {
                            renderer = ar,
                            system = aps,
                            id = ar.GetInstanceID()
                        });
                    }
                }
            }

            // Periodically prune dead or deactivated particle systems to keep tracking lean
            if (frameCount % 180 == 0)
            {
                for (int d = trackedParticleSystems.Count - 1; d >= 0; d--)
                {
                    var t = trackedParticleSystems[d];
                    if (t.renderer == null || t.system == null || !t.renderer.gameObject.activeInHierarchy)
                    {
                        trackedParticleSystemIds.Remove(t.id);
                        trackedParticleSystems.RemoveAt(d);
                    }
                }
            }

            for (int i = 0; i < trackedParticleSystems.Count; i++)
            {
                var tracked = trackedParticleSystems[i];
                var pr = tracked.renderer;
                var ps = tracked.system;

                if (pr == null || ps == null) continue;
                if (!pr.enabled || !pr.gameObject.activeInHierarchy) continue;

                var main = ps.main;
                if (main.cullingMode != ParticleSystemCullingMode.AlwaysSimulate)
                {
                    main.cullingMode = ParticleSystemCullingMode.AlwaysSimulate;
                }

                int numAlive = ps.particleCount;
                if (numAlive <= 0) continue;

                // Expand buffer if needed
                if (_particleBuffer.Length < numAlive)
                {
                    _particleBuffer = new ParticleSystem.Particle[Mathf.NextPowerOfTwo(numAlive)];
                }

                int actualAlive = ps.GetParticles(_particleBuffer);
                if (actualAlive <= 0) continue;

                Material mat = pr.sharedMaterial;
                if (mat == null)
                {
                    var mats = pr.sharedMaterials;
                    if (mats != null && mats.Length > 0)
                        mat = mats[0];
                }
                if (mat == null) continue;

                // Sample color from active particles or system start color
                Color pCol = Color.white;
                bool foundAliveColor = false;
                for (int pIdx = 0; pIdx < Math.Min(actualAlive, 16); pIdx++)
                {
                    Color32 c = _particleBuffer[pIdx].GetCurrentColor(ps);
                    if (c.a > 10 && (c.r > 10 || c.g > 10 || c.b > 10))
                    {
                        pCol = (Color)c;
                        foundAliveColor = true;
                        break;
                    }
                }
                if (!foundAliveColor)
                {
                    var startColor = main.startColor;
                    if (startColor.mode == ParticleSystemGradientMode.Color)
                        pCol = startColor.color;
                    else if (startColor.mode == ParticleSystemGradientMode.TwoColors)
                        pCol = (startColor.colorMin + startColor.colorMax) * 0.5f;
                    else if (startColor.gradient != null)
                        pCol = startColor.gradient.Evaluate(0.5f);
                }

                // Check material tint
                Color matTint = Color.white;
                if (mat.HasProperty("_TintColor"))
                    matTint = mat.GetColor("_TintColor");
                else if (mat.HasProperty("_Color"))
                    matTint = mat.GetColor("_Color");
                else if (mat.HasProperty("_BaseColor"))
                    matTint = mat.GetColor("_BaseColor");

                Color finalParticleColor = new Color(
                    pCol.r * matTint.r,
                    pCol.g * matTint.g,
                    pCol.b * matTint.b,
                    Mathf.Clamp01(Math.Max(pCol.a, matTint.a))
                );

                if (finalParticleColor.r < 0.01f && finalParticleColor.g < 0.01f && finalParticleColor.b < 0.01f)
                {
                    if (pCol.r > 0.05f || pCol.g > 0.05f || pCol.b > 0.05f)
                        finalParticleColor = pCol;
                    else if (matTint.r > 0.05f || matTint.g > 0.05f || matTint.b > 0.05f)
                        finalParticleColor = matTint;
                }

                // Quantize color (0..15 per channel) to group similar tints and prevent material explosion
                int r4 = Mathf.Clamp((int)(finalParticleColor.r * 15f + 0.5f), 0, 15);
                int g4 = Mathf.Clamp((int)(finalParticleColor.g * 15f + 0.5f), 0, 15);
                int b4 = Mathf.Clamp((int)(finalParticleColor.b * 15f + 0.5f), 0, 15);
                int colorKey = (r4 << 8) | (g4 << 4) | b4;
                Color quantizedColor = new Color(r4 / 15f, g4 / 15f, b4 / 15f, 1f);

                int tintedMatId = unchecked(mat.GetInstanceID() * 397 ^ colorKey);
                float emIntensity = (r4 != g4 || g4 != b4) ? 0.8f : 0.2f;

                materialManager.CaptureMaterialTextures(
                    mat, 
                    tintedMatId, 
                    mpbEmissiveColor: quantizedColor, 
                    mpbEmissiveIntensity: emIntensity, 
                    mpbColor: quantizedColor
                );

                if (configDebugLogInterval.Value > 0)
                {
                    if (loggedParticleSystems.Add(pr.name))
                    {
                        logger.LogInfo($"[ParticleSystem] First capture '{pr.name}' (renderMode={pr.renderMode}, alive={actualAlive}, mat='{mat.name}', color=({quantizedColor.r:F2},{quantizedColor.g:F2},{quantizedColor.b:F2}))");
                    }
                }

                if (pr.renderMode == ParticleSystemRenderMode.None)
                    continue;

                if (pr.renderMode != ParticleSystemRenderMode.Billboard || pr.alignment != ParticleSystemRenderSpace.View)
                {
                    if (!BakeMeshParticleSystem(pr, mainCam, tintedMatId, state))
                    {
                        int particlesToDraw = Math.Min(actualAlive, 4096);
                        GenerateBillboardParticleSystem(pr, ps, particlesToDraw, tintedMatId, mainCam, camRight, camUp, camForward, state);
                    }
                }
                else
                {
                    int particlesToDraw = Math.Min(actualAlive, 4096);
                    GenerateBillboardParticleSystem(pr, ps, particlesToDraw, tintedMatId, mainCam, camRight, camUp, camForward, state);
                }

                // Capture trails if enabled on this particle system
                var trails = ps.trails;
                if (trails.enabled)
                {
                    BakeParticleTrails(pr, ps, mainCam, quantizedColor, emIntensity, state);
                }
            }
        }

        private void GenerateBillboardParticleSystem(
            ParticleSystemRenderer pr,
            ParticleSystem ps,
            int numParticlesAlive,
            int matId,
            Camera cam,
            Vector3 camRight,
            Vector3 camUp,
            Vector3 camForward,
            FrameState state)
        {
            int vertCount = numParticlesAlive * 4;
            int triCount = numParticlesAlive * 6;

            Vector3[] verts = new Vector3[vertCount];
            Vector3[] normals = new Vector3[vertCount];
            Vector2[] uvs = new Vector2[vertCount];
            Color32[] colors = new Color32[vertCount];
            int[] tris = new int[triCount];

            bool isWorldSpace = ps.main.simulationSpace == ParticleSystemSimulationSpace.World;
            bool isCustomSpace = ps.main.simulationSpace == ParticleSystemSimulationSpace.Custom && ps.main.customSimulationSpace != null;
            Matrix4x4 sysTransform = isWorldSpace ? Matrix4x4.identity 
                : (isCustomSpace ? ps.main.customSimulationSpace.localToWorldMatrix : ps.transform.localToWorldMatrix);

            var texModule = ps.textureSheetAnimation;
            bool useTexSheet = texModule.enabled;
            int tilesX = useTexSheet ? Math.Max(1, texModule.numTilesX) : 1;
            int tilesY = useTexSheet ? Math.Max(1, texModule.numTilesY) : 1;
            float uvWidth = 1f / tilesX;
            float uvHeight = 1f / tilesY;

            var renderMode = pr.renderMode;
            var alignment = pr.alignment;
            bool isHorizontal = renderMode == ParticleSystemRenderMode.HorizontalBillboard;
            bool isVertical = renderMode == ParticleSystemRenderMode.VerticalBillboard;
            bool isStretch = renderMode == ParticleSystemRenderMode.Stretch;

            Vector3 defaultNormal = -camForward;
            if (isHorizontal || alignment == ParticleSystemRenderSpace.World) defaultNormal = Vector3.up;
            else if (alignment == ParticleSystemRenderSpace.Local) defaultNormal = pr.transform.forward;

            for (int i = 0; i < numParticlesAlive; i++)
            {
                var p = _particleBuffer[i];
                Vector3 pos = isWorldSpace ? p.position : sysTransform.MultiplyPoint3x4(p.position);
                Vector3 size = ps.main.startSize3D ? p.GetCurrentSize3D(ps) : (Vector3.one * p.GetCurrentSize(ps));
                Color32 col = p.GetCurrentColor(ps);

                float rot = p.rotation * Mathf.Deg2Rad;
                float cosR = Mathf.Cos(rot);
                float sinR = Mathf.Sin(rot);

                Vector3 hR, hU;
                Vector3 rAxis, uAxis;
                if (isHorizontal)
                {
                    rAxis = Vector3.right * cosR + Vector3.forward * sinR;
                    uAxis = -Vector3.right * sinR + Vector3.forward * cosR;
                    hR = rAxis * (size.x * 0.5f);
                    hU = uAxis * (size.y * 0.5f);
                }
                else if (isVertical)
                {
                    Vector3 facing = Vector3.ProjectOnPlane(camForward, Vector3.up).normalized;
                    if (facing.sqrMagnitude < 0.001f) facing = Vector3.forward;
                    Vector3 side = Vector3.Cross(Vector3.up, facing).normalized;
                    rAxis = side * cosR + Vector3.up * sinR;
                    uAxis = -side * sinR + Vector3.up * cosR;
                    hR = rAxis * (size.x * 0.5f);
                    hU = uAxis * (size.y * 0.5f);
                }
                else if (isStretch)
                {
                    Vector3 worldVel = isWorldSpace ? p.velocity : sysTransform.MultiplyVector(p.velocity);
                    float speed = worldVel.magnitude;
                    if (speed > 0.001f)
                    {
                        Vector3 velDir = worldVel / speed;
                        Vector3 cross = Vector3.Cross(velDir, camForward).normalized;
                        if (cross.sqrMagnitude < 0.001f) cross = camRight;
                        float stretchLen = Mathf.Clamp(size.y * Mathf.Abs(pr.lengthScale) + speed * pr.velocityScale, size.y, 20f);
                        hR = cross * (size.x * 0.5f);
                        hU = velDir * (stretchLen * 0.5f);
                        rAxis = cross;
                        uAxis = velDir;
                    }
                    else
                    {
                        rAxis = camRight * cosR + camUp * sinR;
                        uAxis = -camRight * sinR + camUp * cosR;
                        hR = rAxis * (size.x * 0.5f);
                        hU = uAxis * (size.y * 0.5f);
                    }
                }
                else if (alignment == ParticleSystemRenderSpace.Local)
                {
                    // Aligned with the ParticleSystem transform (e.g. jump pads, surface rings)
                    rAxis = pr.transform.right * cosR + pr.transform.up * sinR;
                    uAxis = -pr.transform.right * sinR + pr.transform.up * cosR;
                    hR = rAxis * (size.x * 0.5f);
                    hU = uAxis * (size.y * 0.5f);
                }
                else if (alignment == ParticleSystemRenderSpace.World)
                {
                    // Aligned with world axes (horizontal plane)
                    rAxis = Vector3.right * cosR + Vector3.forward * sinR;
                    uAxis = -Vector3.right * sinR + Vector3.forward * cosR;
                    hR = rAxis * (size.x * 0.5f);
                    hU = uAxis * (size.y * 0.5f);
                }
                else if (alignment == ParticleSystemRenderSpace.Facing)
                {
                    // Faces camera position directly
                    Vector3 toCam = (cam.transform.position - pos).normalized;
                    if (toCam.sqrMagnitude < 0.001f) toCam = -camForward;
                    Vector3 right = Vector3.Cross(camUp, toCam).normalized;
                    if (right.sqrMagnitude < 0.001f) right = camRight;
                    Vector3 up = Vector3.Cross(toCam, right).normalized;
                    rAxis = right * cosR + up * sinR;
                    uAxis = -right * sinR + up * cosR;
                    hR = rAxis * (size.x * 0.5f);
                    hU = uAxis * (size.y * 0.5f);
                }
                else if (alignment == ParticleSystemRenderSpace.Velocity)
                {
                    Vector3 worldVel = isWorldSpace ? p.velocity : sysTransform.MultiplyVector(p.velocity);
                    Vector3 velDir = worldVel.normalized;
                    if (velDir.sqrMagnitude < 0.001f) velDir = pr.transform.forward;
                    Vector3 side = Vector3.Cross(velDir, camForward).normalized;
                    if (side.sqrMagnitude < 0.001f) side = Vector3.Cross(velDir, Vector3.up).normalized;
                    if (side.sqrMagnitude < 0.001f) side = camRight;
                    rAxis = side * cosR + velDir * sinR;
                    uAxis = -side * sinR + velDir * cosR;
                    hR = rAxis * (size.x * 0.5f);
                    hU = uAxis * (size.y * 0.5f);
                }
                else
                {
                    // Standard View Billboard (faces camera plane)
                    rAxis = camRight * cosR + camUp * sinR;
                    uAxis = -camRight * sinR + camUp * cosR;
                    hR = rAxis * (size.x * 0.5f);
                    hU = uAxis * (size.y * 0.5f);
                }

                Vector3 norm = Vector3.Cross(uAxis, rAxis).normalized;
                if (norm.sqrMagnitude < 0.001f) norm = defaultNormal;

                int vi = i * 4;
                verts[vi + 0] = pos - hR - hU;
                verts[vi + 1] = pos + hR - hU;
                verts[vi + 2] = pos + hR + hU;
                verts[vi + 3] = pos - hR + hU;

                normals[vi + 0] = norm;
                normals[vi + 1] = norm;
                normals[vi + 2] = norm;
                normals[vi + 3] = norm;

                float uvX0 = 0f, uvX1 = 1f, uvY0 = 0f, uvY1 = 1f;
                if (useTexSheet)
                {
                    float progress = p.startLifetime > 0.0001f ? (1f - (p.remainingLifetime / p.startLifetime)) : 0f;
                    int totalFrames = tilesX * tilesY;
                    int frameIdx = Mathf.Clamp(Mathf.FloorToInt(progress * totalFrames), 0, totalFrames - 1);
                    int tx = frameIdx % tilesX;
                    int ty = tilesY - 1 - (frameIdx / tilesX);
                    uvX0 = tx * uvWidth;
                    uvX1 = (tx + 1) * uvWidth;
                    uvY0 = ty * uvHeight;
                    uvY1 = (ty + 1) * uvHeight;
                }

                uvs[vi + 0] = new Vector2(uvX0, uvY0);
                uvs[vi + 1] = new Vector2(uvX1, uvY0);
                uvs[vi + 2] = new Vector2(uvX1, uvY1);
                uvs[vi + 3] = new Vector2(uvX0, uvY1);

                colors[vi + 0] = col;
                colors[vi + 1] = col;
                colors[vi + 2] = col;
                colors[vi + 3] = col;

                int ti = i * 6;
                tris[ti + 0] = vi + 0;
                tris[ti + 1] = vi + 2;
                tris[ti + 2] = vi + 1;
                tris[ti + 3] = vi + 0;
                tris[ti + 4] = vi + 3;
                tris[ti + 5] = vi + 2;
            }

            ulong remixMeshHash = (ulong)(uint)pr.GetInstanceID() | 0x4000000000000000UL;

            state.skinned.Add(new SkinnedMeshData
            {
                meshId = pr.GetInstanceID(),
                remixMeshHash = remixMeshHash,
                materialId = matId,
                vertices = verts,
                normals = normals,
                uvs = uvs,
                colors = colors,
                triangles = tris,
                localToWorld = Matrix4x4.identity,
                boneTransforms = null,
                skinningData = null
            });
        }

        private bool BakeMeshParticleSystem(ParticleSystemRenderer pr, Camera cam, int matId, FrameState state)
        {
            if (_reusableParticleMesh == null)
            {
                _reusableParticleMesh = new Mesh();
                _reusableParticleMesh.name = "ReusableParticleBakeMesh";
            }

            try
            {
                _reusableParticleMesh.Clear();
#pragma warning disable CS0618
                if (cam != null)
                {
                    pr.BakeMesh(_reusableParticleMesh, cam, true);
                }
                else
                {
                    pr.BakeMesh(_reusableParticleMesh, true);
                }
#pragma warning restore CS0618

                var verts = _reusableParticleMesh.vertices;
                if (verts != null && verts.Length > 0 && _reusableParticleMesh.triangles.Length > 0)
                {
                    ulong remixMeshHash = (ulong)(uint)pr.GetInstanceID() | 0x4000000000000000UL;

                    state.skinned.Add(new SkinnedMeshData
                    {
                        meshId = pr.GetInstanceID(),
                        remixMeshHash = remixMeshHash,
                        materialId = matId,
                        vertices = verts,
                        normals = _reusableParticleMesh.normals,
                        uvs = _reusableParticleMesh.uv,
                        colors = _reusableParticleMesh.colors32,
                        triangles = _reusableParticleMesh.triangles,
                        localToWorld = Matrix4x4.identity,
                        boneTransforms = null,
                        skinningData = null
                    });
                    return true;
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning($"BakeMeshParticleSystem failed on '{pr.name}': {ex.Message}");
            }
            return false;
        }

        private void BakeParticleTrails(
            ParticleSystemRenderer pr, 
            ParticleSystem ps, 
            Camera cam, 
            Color trailColor, 
            float emIntensity, 
            FrameState state)
        {
            if (_reusableParticleTrailMesh == null)
            {
                _reusableParticleTrailMesh = new Mesh { name = "ParticleTrailBakeMesh" };
            }

            try
            {
                _reusableParticleTrailMesh.Clear();
                // useTransform=false keeps vertices in the ParticleSystem's local space so we
                // can apply the full TRS via localToWorld below. This correctly handles both
                // world-space and local-space simulated particles (e.g. blood gibs attached to
                // a moving transform). With useTransform=true Unity only applies R+S without
                // the translation, which misplaces local-space particles.
#pragma warning disable CS0618
                pr.BakeTrailsMesh(_reusableParticleTrailMesh, cam, false);
#pragma warning restore CS0618

                var verts = _reusableParticleTrailMesh.vertices;
                var tris = _reusableParticleTrailMesh.triangles;
                if (verts != null && verts.Length >= 3 && tris != null && tris.Length >= 3)
                {
                    // Only use a dedicated trail material. Falling back to the particle's own
                    // billboard material (pr.sharedMaterial) causes opaque sprite quads to be
                    // rendered over the ribbon geometry, appearing as flat "squares".
                    Material trailMat = pr.trailMaterial;
                    if (trailMat == null)
                    {
                        var mats = pr.sharedMaterials;
                        if (mats != null && mats.Length > 1 && mats[1] != null)
                            trailMat = mats[1];
                    }
                    // No dedicated trail material → skip rather than using the wrong one.
                    if (trailMat == null) return;

                    int r4 = Mathf.Clamp((int)(trailColor.r * 15f + 0.5f), 0, 15);
                    int g4 = Mathf.Clamp((int)(trailColor.g * 15f + 0.5f), 0, 15);
                    int b4 = Mathf.Clamp((int)(trailColor.b * 15f + 0.5f), 0, 15);
                    int colorKey = (r4 << 8) | (g4 << 4) | b4;
                    int trailMatId = unchecked(trailMat.GetInstanceID() * 397 ^ colorKey);

                    materialManager.CaptureMaterialTextures(
                        trailMat, 
                        trailMatId, 
                        mpbEmissiveColor: trailColor, 
                        mpbEmissiveIntensity: emIntensity, 
                        mpbColor: trailColor
                    );

                    ulong trailHash = (ulong)(uint)pr.GetInstanceID() | 0x4800000000000000UL;

                    // Use the full local-to-world matrix so position is correctly applied.
                    // For world-space simulated particles the transform is usually identity/origin
                    // and this is still correct. For local-space (e.g. attached-gib) particles
                    // this is essential so the trails follow the gib in world space.
                    Matrix4x4 trailLocalToWorld = pr.transform.localToWorldMatrix;

                    state.skinned.Add(new SkinnedMeshData
                    {
                        meshId = pr.GetInstanceID() ^ 0x0F0F0F,
                        remixMeshHash = trailHash,
                        materialId = trailMatId,
                        vertices = verts,
                        normals = _reusableParticleTrailMesh.normals,
                        uvs = _reusableParticleTrailMesh.uv,
                        colors = _reusableParticleTrailMesh.colors32,
                        triangles = tris,
                        localToWorld = trailLocalToWorld,
                        boneTransforms = null,
                        skinningData = null
                    });
                }
            }
            catch (Exception ex)
            {
                if (configDebugLogInterval.Value > 0)
                    logger.LogWarning($"BakeParticleTrails failed on '{pr.name}': {ex.Message}");
            }
        }

        /// <summary>
        /// Capture active TrailRenderer ribbons (bullets, gibs, blood trails, projectiles) using BakeMesh.
        /// </summary>
        public void CaptureTrailRenderers(FrameState state, int frameCount)
        {
            if (configCaptureParticles != null && !configCaptureParticles.Value)
                return;

            Camera mainCam = cameraHandler?.GetPreferredCamera() ?? Camera.main;
            if (mainCam == null || !mainCam.gameObject.activeInHierarchy || !mainCam.enabled || Time.frameCount < 10)
                return;

            var activeTrails = UnityEngine.Object.FindObjectsOfType<TrailRenderer>(false);
            if (activeTrails == null || activeTrails.Length == 0)
                return;

            if (_reusableTrailRendererMesh == null)
            {
                _reusableTrailRendererMesh = new Mesh { name = "TrailRendererBakeMesh" };
            }

            for (int i = 0; i < activeTrails.Length; i++)
            {
                var tr = activeTrails[i];
                if (tr == null || !tr.enabled || !tr.gameObject.activeInHierarchy)
                    continue;

                if (tr.positionCount < 2)
                    continue;

                Material mat = tr.sharedMaterial;
                if (mat == null)
                {
                    var mats = tr.sharedMaterials;
                    if (mats != null && mats.Length > 0)
                        mat = mats[0];
                }
                if (mat == null)
                    continue;

                try
                {
                    _reusableTrailRendererMesh.Clear();
                    tr.BakeMesh(_reusableTrailRendererMesh, mainCam, true);

                    var verts = _reusableTrailRendererMesh.vertices;
                    var tris = _reusableTrailRendererMesh.triangles;
                    if (verts == null || verts.Length < 3 || tris == null || tris.Length < 3)
                        continue;

                    // Resolve trail color
                    Color trCol = (tr.startColor + tr.endColor) * 0.5f;
                    if (trCol.a <= 0.01f)
                        trCol = tr.startColor;
                    if (trCol.a <= 0.01f && tr.colorGradient != null)
                        trCol = tr.colorGradient.Evaluate(0.5f);

                    Color matTint = Color.white;
                    if (mat.HasProperty("_TintColor"))
                        matTint = mat.GetColor("_TintColor");
                    else if (mat.HasProperty("_Color"))
                        matTint = mat.GetColor("_Color");
                    else if (mat.HasProperty("_BaseColor"))
                        matTint = mat.GetColor("_BaseColor");

                    Color finalTrailCol = new Color(
                        trCol.r * matTint.r,
                        trCol.g * matTint.g,
                        trCol.b * matTint.b,
                        Mathf.Clamp01(Math.Max(trCol.a, matTint.a))
                    );

                    if (finalTrailCol.r < 0.01f && finalTrailCol.g < 0.01f && finalTrailCol.b < 0.01f)
                    {
                        if (trCol.r > 0.05f || trCol.g > 0.05f || trCol.b > 0.05f)
                            finalTrailCol = trCol;
                        else if (matTint.r > 0.05f || matTint.g > 0.05f || matTint.b > 0.05f)
                            finalTrailCol = matTint;
                    }

                    int r4 = Mathf.Clamp((int)(finalTrailCol.r * 15f + 0.5f), 0, 15);
                    int g4 = Mathf.Clamp((int)(finalTrailCol.g * 15f + 0.5f), 0, 15);
                    int b4 = Mathf.Clamp((int)(finalTrailCol.b * 15f + 0.5f), 0, 15);
                    int colorKey = (r4 << 8) | (g4 << 4) | b4;
                    Color quantizedColor = new Color(r4 / 15f, g4 / 15f, b4 / 15f, 1f);

                    int trailMatId = unchecked(mat.GetInstanceID() * 397 ^ colorKey);
                    float emIntensity = (r4 != g4 || g4 != b4) ? 0.8f : 0.2f;

                    materialManager.CaptureMaterialTextures(
                        mat,
                        trailMatId,
                        mpbEmissiveColor: quantizedColor,
                        mpbEmissiveIntensity: emIntensity,
                        mpbColor: quantizedColor
                    );

                    ulong remixMeshHash = (ulong)(uint)tr.GetInstanceID() | 0x4C00000000000000UL;

                    state.skinned.Add(new SkinnedMeshData
                    {
                        meshId = tr.GetInstanceID(),
                        remixMeshHash = remixMeshHash,
                        materialId = trailMatId,
                        vertices = verts,
                        normals = _reusableTrailRendererMesh.normals,
                        uvs = _reusableTrailRendererMesh.uv,
                        colors = _reusableTrailRendererMesh.colors32,
                        triangles = tris,
                        localToWorld = Matrix4x4.identity,
                        boneTransforms = null,
                        skinningData = null
                    });
                }
                catch (Exception ex)
                {
                    if (configDebugLogInterval.Value > 0)
                        logger.LogWarning($"CaptureTrailRenderers failed on '{tr.name}': {ex.Message}");
                }
            }
        }
        
        /// <summary>
        /// Cache UVs, triangles, and vertex buffer layout for a sharedMesh. Called once per unique mesh.
        /// </summary>
        private void CacheMeshTopology(Mesh sharedMesh, int meshId, bool doLog)
        {
            try
            {
                if (!sharedMesh.isReadable)
                {
                    // Mesh not readable — can't get triangles directly.
                    // Cache vertex buffer layout now; topology will be completed from first BakeMesh.
                    int posOff2 = -1, nrmOff2 = -1, stride2 = 0;
                    if (NativeMeshReader.TryGetVertexLayout(sharedMesh, out var l2))
                    {
                        posOff2 = l2.PositionOffset;
                        nrmOff2 = l2.NormalOffset;
                        stride2 = l2.Stride;
                    }
                    bool layoutOk = posOff2 >= 0 && stride2 > 0;
                    cachedTopology[meshId] = new CachedMeshTopology
                    {
                        valid = false,
                        layoutValid = layoutOk,
                        vertexCount = sharedMesh.vertexCount,
                        positionOffset = posOff2,
                        normalOffset = nrmOff2,
                        stride = stride2
                    };
                    logger.LogInfo($"[MeshTopology] '{sharedMesh.name}' not readable — cached layout (layoutValid={layoutOk} stride={stride2} posOff={posOff2} nrmOff={nrmOff2} verts={sharedMesh.vertexCount}), awaiting BakeMesh for triangles/UVs");
                    return;
                }

                var uvCoords = sharedMesh.uv;
                
                // Combine all submesh triangles
                var allTris = new List<int>();
                for (int i = 0; i < sharedMesh.subMeshCount; i++)
                {
                    if (sharedMesh.GetTopology(i) != MeshTopology.Triangles)
                        continue;
                    var subTris = sharedMesh.GetTriangles(i);
                    if (subTris != null && subTris.Length > 0)
                        allTris.AddRange(subTris);
                }
                
                if (allTris.Count == 0 || allTris.Count % 3 != 0)
                {
                    logger.LogInfo($"[MeshTopology] '{sharedMesh.name}' INVALID: subMeshCount={sharedMesh.subMeshCount} triCount={allTris.Count} isReadable={sharedMesh.isReadable}");
                    cachedTopology[meshId] = new CachedMeshTopology { valid = false };
                    return;
                }
                
                // Get vertex buffer layout for GPU readback
                int posOffset = -1;
                int nrmOffset = -1;
                int stride = 0;
                
                if (NativeMeshReader.TryGetVertexLayout(sharedMesh, out var l1))
                {
                    posOffset = l1.PositionOffset;
                    nrmOffset = l1.NormalOffset;
                    stride = l1.Stride;
                }
                
                bool topoValid = posOffset >= 0 && stride > 0;
                cachedTopology[meshId] = new CachedMeshTopology
                {
                    uvs = uvCoords ?? new Vector2[sharedMesh.vertexCount],
                    triangles = allTris.ToArray(),
                    vertexCount = sharedMesh.vertexCount,
                    positionOffset = posOffset,
                    normalOffset = nrmOffset,
                    stride = stride,
                    valid = topoValid
                };
                
                // Always log topology — only fires once per unique mesh
                logger.LogInfo($"[MeshTopology] '{sharedMesh.name}' verts={sharedMesh.vertexCount} tris={allTris.Count/3} stride={stride} posOff={posOffset} nrmOff={nrmOffset} valid={topoValid}");
            }
            catch (Exception ex)
            {
                cachedTopology[meshId] = new CachedMeshTopology { valid = false };
                logger.LogWarning($"[MeshTopology] Failed for '{sharedMesh.name}': {ex.Message}");
            }
        }
        
        private const int MAX_REMIX_BONES = 256;
        private const int BONES_PER_VERTEX = 4; // D3D9 standard, Remix expectation
        
        private static readonly bool _hasBoneWeight1 = Type.GetType("UnityEngine.BoneWeight1, UnityEngine.CoreModule") != null;

        private static class ModernBoneWeightHelper
        {
            [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
            public static bool TryExtractBoneWeights(Mesh mesh, int vertexCount, float[] blendWeights, uint[] blendIndices)
            {
                var bonesPerVertex = mesh.GetBonesPerVertex();
                var allBoneWeights = mesh.GetAllBoneWeights();

                if (bonesPerVertex.Length == 0 || allBoneWeights.Length == 0)
                    return false;

                int weightIdx = 0;
                for (int v = 0; v < vertexCount; v++)
                {
                    int count = bonesPerVertex[v];
                    int baseIdx = v * BONES_PER_VERTEX;
                    for (int w = 0; w < count && w < BONES_PER_VERTEX; w++)
                    {
                        var bw = allBoneWeights[weightIdx + w];
                        blendWeights[baseIdx + w] = bw.weight;
                        blendIndices[baseIdx + w] = (uint)bw.boneIndex;
                    }
                    weightIdx += count;
                }
                return true;
            }
        }

        /// <summary>
        /// Extract bind-pose geometry and bone weights from a SkinnedMeshRenderer's sharedMesh.
        /// Stores result in cachedSkinning. Called once per unique sharedMesh. Returns null on failure.
        /// </summary>
        private void CacheSkinningData(SkinnedMeshRenderer skinned, int sharedMeshId)
        {
            var mesh = skinned.sharedMesh;
            try
            {
                // Bone data validation
                var bindPoses = mesh.bindposes;
                var bones = skinned.bones;
                if (bindPoses == null || bindPoses.Length == 0 || bones == null || bones.Length == 0)
                {
                    logger.LogInfo($"[Skinning] '{mesh.name}' has no bones/bindposes — BakeMesh fallback");
                    cachedSkinning[sharedMeshId] = null;
                    return;
                }
                
                int boneCount = Mathf.Min(bindPoses.Length, bones.Length);
                if (boneCount > MAX_REMIX_BONES)
                {
                    logger.LogInfo($"[Skinning] '{mesh.name}' has {boneCount} bones (>{MAX_REMIX_BONES}) — BakeMesh fallback");
                    cachedSkinning[sharedMeshId] = null;
                    return;
                }
                
                // Extract bone weights — try modern API first (Unity 2019.3+), then legacy
                int vertexCount = mesh.vertexCount;
                float[] blendWeights = new float[BONES_PER_VERTEX * vertexCount];
                uint[] blendIndices = new uint[BONES_PER_VERTEX * vertexCount];
                
                bool extractedWeights = false;
                if (_hasBoneWeight1)
                {
                    try
                    {
                        extractedWeights = ModernBoneWeightHelper.TryExtractBoneWeights(mesh, vertexCount, blendWeights, blendIndices);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning($"[Skinning] Modern bone weights failed for '{mesh.name}': {ex.Message}");
                    }
                }

                if (!extractedWeights)
                {
                    // Legacy fallback: BoneWeight per vertex (always exactly 4)
                    try
                    {
                        var legacyWeights = mesh.boneWeights;
                        if (legacyWeights == null || legacyWeights.Length == 0)
                        {
                            logger.LogInfo($"[Skinning] '{mesh.name}' bone weights not accessible — BakeMesh fallback");
                            cachedSkinning[sharedMeshId] = null;
                            return;
                        }
                        
                        for (int v = 0; v < vertexCount; v++)
                        {
                            var bw = legacyWeights[v];
                            int baseIdx = v * BONES_PER_VERTEX;
                            blendWeights[baseIdx + 0] = bw.weight0;
                            blendWeights[baseIdx + 1] = bw.weight1;
                            blendWeights[baseIdx + 2] = bw.weight2;
                            blendWeights[baseIdx + 3] = bw.weight3;
                            blendIndices[baseIdx + 0] = (uint)bw.boneIndex0;
                            blendIndices[baseIdx + 1] = (uint)bw.boneIndex1;
                            blendIndices[baseIdx + 2] = (uint)bw.boneIndex2;
                            blendIndices[baseIdx + 3] = (uint)bw.boneIndex3;
                        }
                    }
                    catch (Exception ex2)
                    {
                        logger.LogInfo($"[Skinning] '{mesh.name}' legacy bone weights failed: {ex2.Message} — BakeMesh fallback");
                        cachedSkinning[sharedMeshId] = null;
                        return;
                    }
                }
                
                // Extract bind-pose geometry
                Vector3[] bindVerts, bindNorms;
                Vector2[] uvs;
                Color32[] colors = null;
                int[] triangles;
                
                if (mesh.isReadable)
                {
                    bindVerts = mesh.vertices;
                    bindNorms = mesh.normals;
                    uvs = mesh.uv;
                    colors = mesh.colors32;
                    if (colors != null && colors.Length == 0) colors = null;
                    
                    var allTris = new List<int>();
                    for (int s = 0; s < mesh.subMeshCount; s++)
                    {
                        if (mesh.GetTopology(s) != MeshTopology.Triangles)
                            continue;
                        var sub = mesh.GetTriangles(s);
                        if (sub != null && sub.Length > 0)
                            allTris.AddRange(sub);
                    }
                    triangles = allTris.ToArray();
                }
                else
                {
                    // Non-readable: bake once, then recover bind-pose vertices
                    // by inverting the per-vertex skinning transform.
                    var tempMesh = new Mesh();
                    skinned.BakeMesh(tempMesh);
                    uvs = tempMesh.uv;
                    colors = tempMesh.colors32;
                    if (colors != null && colors.Length == 0) colors = null;
                    
                    var allTris = new List<int>();
                    for (int s = 0; s < tempMesh.subMeshCount; s++)
                    {
                        if (tempMesh.GetTopology(s) != MeshTopology.Triangles)
                            continue;
                        var sub = tempMesh.GetTriangles(s);
                        if (sub != null && sub.Length > 0)
                            allTris.AddRange(sub);
                    }
                    triangles = allTris.ToArray();
                    
                    // Recover bind-pose vertices by inverting the per-vertex skinning transform.
                    // BakeMesh(false) returns vertices in SMR local space WITHOUT scale:
                    //   v_baked = noScale_w2l * Σ(w_i * bones[i].l2w * bindPoses[i]) * v_bindpose
                    // Per vertex, the no-scale local skinning matrix is:
                    //   localSkinMatrix = noScale_w2l * Σ(w_i * bones[i].l2w * bindPoses[i])
                    // So: v_bindpose = localSkinMatrix^{-1} * v_baked
                    
                    // BakeMesh(false) strips the SMR's scale, so use no-scale W2L to match.
                    Matrix4x4 noScaleW2L = Matrix4x4.TRS(
                        skinned.transform.position, skinned.transform.rotation, Vector3.one).inverse;
                    var localBoneMatrices = new Matrix4x4[boneCount];
                    for (int b = 0; b < boneCount; b++)
                    {
                        localBoneMatrices[b] = (bones[b] != null)
                            ? noScaleW2L * bones[b].localToWorldMatrix * bindPoses[b]
                            : Matrix4x4.identity;
                    }
                    
                    var bakedVerts = tempMesh.vertices;
                    var bakedNorms = tempMesh.normals ?? new Vector3[0];
                    UnityEngine.Object.Destroy(tempMesh);
                    
                    bindVerts = new Vector3[bakedVerts.Length];
                    bindNorms = new Vector3[bakedVerts.Length];
                    int degenerateCount = 0;
                    
                    for (int v = 0; v < bakedVerts.Length; v++)
                    {
                        // Build per-vertex skinning matrix (local space)
                        int baseIdx = v * BONES_PER_VERTEX;
                        Matrix4x4 skinMatrix = new Matrix4x4();
                        for (int w = 0; w < BONES_PER_VERTEX; w++)
                        {
                            float weight = blendWeights[baseIdx + w];
                            if (weight <= 0f) continue;
                            int boneIdx = (int)blendIndices[baseIdx + w];
                            if (boneIdx >= boneCount) continue;
                            Matrix4x4 bm = localBoneMatrices[boneIdx];
                            for (int r = 0; r < 4; r++)
                                for (int c = 0; c < 4; c++)
                                    skinMatrix[r, c] += weight * bm[r, c];
                        }
                        
                        // Invert and recover bind-pose vertex
                        Matrix4x4 inv = skinMatrix.inverse;
                        float det = skinMatrix.determinant;
                        if (Mathf.Abs(det) < 1e-6f)
                        {
                            // Singular matrix — keep baked vertex as-is (fallback)
                            bindVerts[v] = bakedVerts[v];
                            bindNorms[v] = (v < bakedNorms.Length) ? bakedNorms[v] : Vector3.up;
                            degenerateCount++;
                            continue;
                        }
                        
                        Vector4 bp = inv * new Vector4(bakedVerts[v].x, bakedVerts[v].y, bakedVerts[v].z, 1f);
                        bindVerts[v] = new Vector3(bp.x, bp.y, bp.z);
                        
                        if (v < bakedNorms.Length)
                        {
                            Vector4 bn = inv * new Vector4(bakedNorms[v].x, bakedNorms[v].y, bakedNorms[v].z, 0f);
                            Vector3 n = new Vector3(bn.x, bn.y, bn.z);
                            bindNorms[v] = (n.sqrMagnitude > 1e-8f) ? n.normalized : Vector3.up;
                        }
                        else
                        {
                            bindNorms[v] = Vector3.up;
                        }
                    }
                    
                    if (degenerateCount > 0)
                        logger.LogInfo($"[Skinning] '{mesh.name}' {degenerateCount}/{bakedVerts.Length} verts had degenerate skin matrices");
                }
                
                if (bindVerts == null || bindVerts.Length == 0 || triangles.Length == 0)
                {
                    logger.LogInfo($"[Skinning] '{mesh.name}' empty geometry — BakeMesh fallback");
                    cachedSkinning[sharedMeshId] = null;
                    return;
                }
                
                if (bindNorms == null || bindNorms.Length != bindVerts.Length)
                {
                    bindNorms = new Vector3[bindVerts.Length];
                    for (int n = 0; n < bindNorms.Length; n++)
                        bindNorms[n] = Vector3.up;
                }
                if (uvs == null || uvs.Length != bindVerts.Length)
                    uvs = new Vector2[bindVerts.Length];
                
                cachedSkinning[sharedMeshId] = new CachedSkinningData
                {
                    bindVertices = bindVerts,
                    bindNormals = bindNorms,
                    uvs = uvs,
                    colors = colors,
                    triangles = triangles,
                    blendWeights = blendWeights,
                    blendIndices = blendIndices,
                    bonesPerVertex = BONES_PER_VERTEX,
                    boneCount = boneCount,
                    bindPoses = bindPoses,
                    meshCreated = false
                };
                
                logger.LogInfo($"[Skinning] '{mesh.name}' cached: {vertexCount} verts, {triangles.Length / 3} tris, {boneCount} bones, readable={mesh.isReadable}");
            }
            catch (Exception ex)
            {
                logger.LogWarning($"[Skinning] '{mesh.name}' extraction failed: {ex.Message} — BakeMesh fallback");
                cachedSkinning[sharedMeshId] = null;
            }
        }
        
        /// <summary>
        /// Try to issue an async GPU readback for a skinned mesh renderer's vertex buffer.
        /// Returns true if request was issued, false if GPU readback not possible for this renderer.
        /// </summary>
        private bool TryIssueGPUReadback(SkinnedMeshRenderer skinned, int skinnedId, int sharedMeshId, int matId, Matrix4x4 localToWorld, bool doLog)
        {
            try
            {
                // Ensure vertex buffer is readable — must be set before the GPU skins this renderer,
                // so we configure it and skip readback this frame (buffer won't exist yet).
                // Unity 2019 lacks vertexBufferTarget and forceMatrixRecalculationPerRender —
                // MissingMethodException will be caught and BakeMesh fallback used instead.
                if (!configuredBufferTargets.Contains(skinnedId))
                {
                    skinned.vertexBufferTarget |= GraphicsBuffer.Target.Raw;
                    skinned.forceMatrixRecalculationPerRender = true;
                    configuredBufferTargets.Add(skinnedId);
                    logger.LogInfo($"  GPU readback: configured vertexBufferTarget for '{skinned.name}' (id={skinnedId}), deferring to next frame");
                    return false;
                }
                
                var buffer = skinned.GetVertexBuffer();
                if (buffer == null || !buffer.IsValid())
                {
                    logger.LogInfo($"  GPU readback: GetVertexBuffer() returned {(buffer == null ? "null" : "invalid")} for '{skinned.name}' (id={skinnedId})");
                    buffer?.Dispose();
                    return false;
                }
                
                var request = AsyncGPUReadback.Request(buffer);
                buffer.Dispose();
                
                pendingReadbacks.Add(new PendingReadback
                {
                    skinnedId = skinnedId,
                    materialId = matId,
                    sharedMeshId = sharedMeshId,
                    localToWorld = localToWorld,
                    request = request
                });
                
                return true;
            }
            catch (MissingMethodException)
            {
                // Unity 2019: vertexBufferTarget / GetVertexBuffer / forceMatrixRecalculationPerRender
                // don't exist. Silently fall through to BakeMesh — log once per renderer.
                if (!configuredBufferTargets.Contains(skinnedId))
                {
                    logger.LogInfo($"  GPU readback: not available for '{skinned.name}' (Unity 2019) — using BakeMesh");
                    configuredBufferTargets.Add(skinnedId); // prevent repeated log
                }
                return false;
            }
            catch (Exception ex)
            {
                logger.LogWarning($"  GPU readback: exception for '{skinned.name}' (id={skinnedId}): {ex.Message}");
                return false;
            }
        }
        
        /// <summary>
        /// BakeMesh fallback: CPU re-skin a single skinned mesh.
        /// </summary>
        private bool BakeSingleMesh(SkinnedMeshRenderer skinned, int skinnedId, int matId, Matrix4x4 localToWorld, bool doLog)
        {
            try
            {
                if (!bakedMeshes.TryGetValue(skinnedId, out Mesh bakedMesh) || bakedMesh == null)
                {
                    bakedMesh = new Mesh();
                    bakedMesh.name = $"Baked_{skinned.name}";
                    bakedMeshes[skinnedId] = bakedMesh;
                }
                
                skinned.BakeMesh(bakedMesh);
                
                var verts = bakedMesh.vertices;
                var norms = bakedMesh.normals;
                
                // Reuse cached UVs and triangles — they never change between frames
                int sharedMeshId2 = skinned.sharedMesh.GetInstanceID();
                Vector2[] uvCoords;
                int[] tris;
                if (cachedTopology.TryGetValue(sharedMeshId2, out var topo) && topo.valid && topo.uvs != null && topo.triangles != null)
                {
                    uvCoords = topo.uvs;
                    tris = topo.triangles;
                }
                else
                {
                    uvCoords = bakedMesh.uv;
                    var allTris = new List<int>();
                    for (int i = 0; i < bakedMesh.subMeshCount; i++)
                    {
                        if (bakedMesh.GetTopology(i) != MeshTopology.Triangles)
                            continue;
                        var subTris = bakedMesh.GetTriangles(i);
                        if (subTris != null && subTris.Length > 0)
                            allTris.AddRange(subTris);
                    }
                    tris = allTris.ToArray();
                }
                
                if (verts == null || verts.Length == 0 || tris.Length == 0 || tris.Length % 3 != 0)
                    return false;
                
                // Vertex colors don't change with animation — read from sharedMesh or baked mesh
                Color32[] colors = null;
                try
                {
                    var sharedMesh = skinned.sharedMesh;
                    if (sharedMesh != null && sharedMesh.isReadable)
                    {
                        colors = sharedMesh.colors32;
                        if (colors != null && colors.Length == 0) colors = null;
                    }
                    if (colors == null)
                    {
                        colors = bakedMesh.colors32;
                        if (colors != null && colors.Length == 0) colors = null;
                    }
                }
                catch { }
                
                ulong combinedMeshHash = RemixMeshConverter.GenerateMeshHash(skinned.sharedMesh);
                ulong baseMeshHash = combinedMeshHash;
                if (skinned.bones != null)
                {
                    foreach (var b in skinned.bones)
                    {
                        if (b != null)
                            combinedMeshHash ^= HashUtils.HashStringFNV(b.name);
                        combinedMeshHash *= 1099511628211UL;
                    }
                }
                
                // Debug: log hash components once per unique mesh name
                string meshDebugKey = skinned.sharedMesh.name + "_bake";
                if (!loggedHashDebugMeshes.Contains(meshDebugKey))
                {
                    loggedHashDebugMeshes.Add(meshDebugKey);
                    string meshName = skinned.sharedMesh.name;
                    string cleanedName = meshName.Replace(" (Instance)", "").Replace(" Instance", "").Replace("(Clone)", "").Trim();
                    cleanedName = System.Text.RegularExpressions.Regex.Replace(cleanedName, @"[\s_-]*[0-9]+$", "");
                    int vertCount = skinned.sharedMesh.vertexCount;
                    int triCount = (skinned.sharedMesh != null && skinned.sharedMesh.isReadable) ? skinned.sharedMesh.triangles.Length : 0;
                    string boneNames = skinned.bones != null ? string.Join(",", System.Linq.Enumerable.Select(skinned.bones, b => b != null ? b.name : "null")) : "none";
                    logger.LogInfo($"[HashDebug-BakeMesh] '{skinned.name}' meshName='{meshName}' cleanedName='{cleanedName}' verts={vertCount} tris={triCount} baseMeshHash=0x{baseMeshHash:X16} combinedMeshHash=0x{combinedMeshHash:X16} matId={matId} bones=[{boneNames}]");
                }
                
                persistentSkinnedData[skinnedId] = new SkinnedMeshData
                {
                    meshId = skinnedId,
                    remixMeshHash = combinedMeshHash,
                    materialId = matId,
                    vertices = verts,
                    normals = norms,
                    uvs = uvCoords,
                    colors = colors,
                    triangles = tris,
                    localToWorld = localToWorld
                };

                // Complete topology from baked mesh if triangles were unavailable (non-readable mesh)
                if (cachedTopology.TryGetValue(sharedMeshId2, out var pendingTopo) && !pendingTopo.valid)
                {
                    pendingTopo.uvs = uvCoords;
                    pendingTopo.triangles = tris;
                    pendingTopo.valid = true;
                    cachedTopology[sharedMeshId2] = pendingTopo;
                    logger.LogInfo($"[MeshTopology] '{skinned.sharedMesh.name}' topology completed from BakeMesh: verts={pendingTopo.vertexCount} tris={tris.Length / 3} stride={pendingTopo.stride}");
                }

                return true;
            }
            catch (Exception ex)
            {
                if (doLog)
                    logger.LogError($"BakeMesh failed for '{skinned.name}': {ex.Message}");
                return false;
            }
        }
        
        /// <summary>
        /// Combine two ints into a unique hash for per-renderer material IDs
        /// </summary>
        private static int HashCombine(int a, int b)
        {
            unchecked { return a * 397 ^ b; }
        }
        
        /// <summary>
        /// Resolve the best material for a skinned mesh renderer. Returns material instance ID.
        /// </summary>
        private int ResolveMaterial(SkinnedMeshRenderer skinned, bool doLog)
        {
            int matId = 0;
            Material bestMaterial = null;
            
            var materials = skinned.sharedMaterials;
            if (materials == null || materials.Length == 0)
                return 0;
            
            string[] textureProps = { "_MainTex", "_BaseMap", "_BaseColorMap", "_AlbedoTex" };
            
            foreach (var mat in materials)
            {
                if (mat == null) continue;
                bool hasTexture = false;
                try
                {
                    if (mat.HasProperty("_MainTex"))
                        hasTexture = mat.mainTexture != null;
                    if (!hasTexture)
                    {
                        foreach (var prop in textureProps)
                        {
                            if (mat.HasProperty(prop) && mat.GetTexture(prop) != null)
                            { hasTexture = true; break; }
                        }
                    }
                }
                catch { }
                if (hasTexture) { bestMaterial = mat; break; }
            }
            
            if (bestMaterial == null)
            {
                foreach (var mat in materials)
                {
                    if (mat != null) { bestMaterial = mat; break; }
                }
            }
            
            if (bestMaterial != null)
            {
                matId = bestMaterial.GetInstanceID();
                
                // Check for MaterialPropertyBlock overrides (used by games for dynamic per-renderer colors/emission)
                Color? mpbEmissiveColor = null;
                float? mpbEmissiveIntensity = null;
                try
                {
                    var mpb = new MaterialPropertyBlock();
                    skinned.GetPropertyBlock(mpb);
                    if (!mpb.isEmpty)
                    {
                        if (bestMaterial.HasProperty("_EmissiveColor"))
                        {
                            var c = mpb.GetColor("_EmissiveColor");
                            if (c.r != 0 || c.g != 0 || c.b != 0)
                            {
                                mpbEmissiveColor = c;
                                // Generate unique material ID for this renderer's override
                                matId = HashCombine(bestMaterial.GetInstanceID(), HashUtils.GetHierarchyHashInt(skinned.transform));
                            }
                        }
                        if (bestMaterial.HasProperty("_EmissiveIntensity"))
                        {
                            float v = mpb.GetFloat("_EmissiveIntensity");
                            if (v > 0) mpbEmissiveIntensity = v;
                        }
                    }
                }
                catch { }
                
                materialManager.CaptureMaterialTextures(bestMaterial, matId, mpbEmissiveColor, mpbEmissiveIntensity);
                
                if (doLog)
                {
                    string logKey = $"{skinned.name}:{bestMaterial.name}:{matId}";
                    if (!loggedSkinnedMaterials.Contains(logKey))
                    {
                        loggedSkinnedMaterials.Add(logKey);
                        if (mpbEmissiveColor.HasValue)
                        {
                            var c = mpbEmissiveColor.Value;
                            logger.LogInfo($"  Skinned mesh '{skinned.name}' using material '{bestMaterial.name}' (ID: {matId}) [MPB _EmissiveColor=({c.r:F3},{c.g:F3},{c.b:F3})");
                        }
                        else
                        {
                            logger.LogInfo($"  Skinned mesh '{skinned.name}' using material '{bestMaterial.name}' (ID: {matId})");
                        }
                    }
                }
            }
            
            return matId;
        }
        
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
            pendingReadbacks.Clear();
            configuredBufferTargets.Clear();
            cachedTopology.Clear();
        }
    }
}
