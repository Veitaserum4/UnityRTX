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

        // Scrolling UV mesh tracking
        private static readonly int PropScrollOffset = Shader.PropertyToID("_ScrollOffset");
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
        private MaterialPropertyBlock sharedStaticMpb = null;

        // --- Diagnostic getters for debug HUD ---
        public int FailedMeshCount { get { lock (meshQueueLock) return failedMeshKeys.Count; } }
        public int PendingMeshQueueCount { get { lock (meshQueueLock) return meshesToCreate.Count + priorityMeshesToCreate.Count; } }
        public int PersistentStaticCount { get { lock (persistentStaticLock) return persistentStaticInstances.Count; } }
        public int CachedStaticRendererCount => cachedRenderers.Count;
        public int CachedSkinnedRendererCount => cachedSkinnedRenderers.Count;

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
            public uint categoryFlags;
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
            public uint categoryFlags;
            public Vector4 uvST;
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
            loggedHashDebugMeshes.Clear();
            cachedTopology.Clear();
            cachedSkinning.Clear();
            cachedScrollingMeshes.Clear();
            scrollingRendererIds.Clear();
            cachedScrollingRenderers.Clear();
            RemixScrollingTextureDetector.ClearCache();
            lock (persistentStaticLock)
            {
                persistentStaticInstances.Clear();
            }
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
            trackedParticleSystems.Clear();
            trackedParticleSystemIds.Clear();
            cachedScrollingMeshes.Clear();
            scrollingRendererIds.Clear();
            cachedScrollingRenderers.Clear();
            RemixScrollingTextureDetector.ClearCache();
            skinnedRoundRobinIndex = 0;
            
            var allStatic = UnityCompat.FindSceneComponentsIncludingInactive<MeshRenderer>();
            for (int i = 0; i < allStatic.Length; i++)
            {
                var r = allStatic[i];
                if (r != null && cachedRendererIds.Add(r.GetInstanceID()))
                {
                    if (RemixScrollingTextureDetector.IsScrollingRenderer(r))
                    {
                        scrollingRendererIds.Add(r.GetInstanceID());
                        cachedScrollingRenderers.Add(r);
                    }
                    cachedRenderers.Add(r);
                }
            }

            var allSkinned = UnityCompat.FindSceneComponentsIncludingInactive<SkinnedMeshRenderer>();
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

            var allParticles = UnityCompat.FindActiveSceneComponents<ParticleSystemRenderer>();
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
            
            logger.LogInfo($"Renderer cache refreshed: {cachedRenderers.Count} static ({cachedScrollingRenderers.Count} scrolling UV), {cachedSkinnedRenderers.Count} skinned, {trackedParticleSystems.Count} particles");
            
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

                // Update skybox state on main thread
                skyboxManager?.UpdateSkybox(mainCam, frameCount);
            }
            
            // Debug toggle check
            if (!configCaptureStaticMeshes.Value)
            {
                if (mainCam != null)
                {
                    skyboxManager?.EmitSkyboxInstance(state, mainCam);
                }
                return;
            }

            // Static mesh frame skipping: reuse cached static instances if enabled and available
            int frameSkip = configStaticMeshFrameSkip != null ? configStaticMeshFrameSkip.Value : 1;
            if (frameSkip > 1 && (frameCount % frameSkip != 0) && cachedStaticInstances.Count > 0)
            {
                state.instances.AddRange(cachedStaticInstances);
                if (mainCam != null)
                {
                    CaptureCameraViewModelMeshes(state, mainCam);
                    CaptureAllScrollingMeshes(state, frameCount, camPos);
                    skyboxManager?.EmitSkyboxInstance(state, mainCam);
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
                
                var meshFilter = renderer.GetComponent<MeshFilter>();
                if (meshFilter == null || meshFilter.sharedMesh == null)
                    continue;
                
                var mesh = meshFilter.sharedMesh;
                int meshId = mesh.GetInstanceID();
                int rendererInstanceId = renderer.GetInstanceID();
                var dedupeKey = StaticGeometryDedupe.BuildKey(renderer, mesh);

                // Animated / scrolling UV meshes (water, lava, conveyor belts, scrolling textures)
                // must be rendered dynamically via state.skinned with live UV offsets every frame.
                if (scrollingRendererIds.Contains(rendererInstanceId) || RemixScrollingTextureDetector.IsScrollingRenderer(renderer))
                {
                    if (scrollingRendererIds.Add(rendererInstanceId))
                    {
                        cachedScrollingRenderers.Add(renderer);
                        logger.LogInfo($"[ScrollingTexture] Registered scrolling renderer '{renderer.gameObject.name}' (id={rendererInstanceId}, mesh='{mesh.name}') - total scrolling: {scrollingRendererIds.Count}");
                    }
                    lock (persistentStaticLock)
                    {
                        persistentStaticInstances.Remove(rendererInstanceId);
                    }
                    CaptureScrollingMesh(renderer, mesh, state, frameCount);
                    continue;
                }

                if (ShouldPreferSceneScan(renderer, mesh))
                {
                    lock (persistentStaticLock)
                    {
                        persistentStaticInstances.Remove(rendererInstanceId);
                    }
                    continue;
                }

                var materials = renderer.sharedMaterials;

                Texture mpbMainTex = null;
                Color? mpbColor = null;
                Color? mpbEmissive = null;
                int mpbHash = 0;

                if (sharedStaticMpb == null)
                    sharedStaticMpb = new MaterialPropertyBlock();
                else
                    sharedStaticMpb.Clear();

                if (renderer.HasPropertyBlock())
                {
                    renderer.GetPropertyBlock(sharedStaticMpb);

                    Vector4 sOff = sharedStaticMpb.GetVector(PropScrollOffset);
                    if (sOff.x != 0f || sOff.y != 0f)
                    {
                        if (scrollingRendererIds.Add(rendererInstanceId))
                        {
                            cachedScrollingRenderers.Add(renderer);
                        }
                        lock (persistentStaticLock)
                        {
                            persistentStaticInstances.Remove(rendererInstanceId);
                        }
                        CaptureScrollingMesh(renderer, mesh, state, frameCount);
                        continue;
                    }

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
                }

                int matSig = StaticGeometryDedupe.ComputeMaterialSignature(materials);
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
                                materialManager.CaptureMaterialTextures(materials[m], matId, mpbEmissive, null, mpbMainTex as Texture2D, mpbColor);
                            }
                            else
                            {
                                submeshMaterialIds.Add(0);
                            }
                        }
                    }
                    
                    bool isViewModel = mainCam != null && renderer.transform.IsChildOf(mainCam.transform);

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

            // Emit skybox instance into frame state
            if (mainCam != null)
            {
                skyboxManager?.EmitSkyboxInstance(state, mainCam);
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
        /// Retrieves or extracts cached base geometry for a scrolling UV mesh.
        /// Base geometry (positions, normals, base UVs, indices) is read only once.
        /// </summary>
        private CachedScrollingMesh GetOrCreateScrollingMesh(Mesh mesh)
        {
            if (mesh == null) return null;
            int meshId = mesh.GetInstanceID();
            if (cachedScrollingMeshes.TryGetValue(meshId, out var cached))
                return cached;

            Vector3[] vertices = null;
            Vector3[] normals = null;
            Vector2[] uvs = null;
            Color32[] colors = null;
            var submeshTriangles = new List<int[]>();

            if (mesh.isReadable)
            {
                try
                {
                    vertices = mesh.vertices;
                    normals = mesh.normals;
                    uvs = mesh.uv;
                    colors = mesh.colors32;
                    if (colors != null && colors.Length == 0) colors = null;

                    for (int s = 0; s < mesh.subMeshCount; s++)
                    {
                        if (mesh.GetTopology(s) != MeshTopology.Triangles)
                            continue;
                        var tris = mesh.GetTriangles(s);
                        if (tris != null && tris.Length > 0 && tris.Length % 3 == 0)
                        {
                            submeshTriangles.Add(tris);
                        }
                    }
                }
                catch { }
            }

            if (vertices == null || vertices.Length == 0 || submeshTriangles.Count == 0)
            {
                try
                {
                    if (NativeMeshReader.ReadMeshFromGPU(mesh, out vertices, out normals, out uvs, out int[][] subTris))
                    {
                        submeshTriangles.Clear();
                        for (int s = 0; s < subTris.Length; s++)
                        {
                            var tris = subTris[s];
                            if (tris != null && tris.Length > 0 && tris.Length % 3 == 0)
                            {
                                submeshTriangles.Add(tris);
                            }
                        }
                    }
                }
                catch { }
            }

            if (vertices == null || vertices.Length == 0 || submeshTriangles.Count == 0)
                return null;

            if (uvs == null || uvs.Length != vertices.Length)
                uvs = new Vector2[vertices.Length];

            if (normals == null || normals.Length != vertices.Length)
                normals = RemixMeshConverter.ComputeFaceNormals(vertices, submeshTriangles[0]);

            cached = new CachedScrollingMesh
            {
                vertices = vertices,
                normals = normals,
                baseUVs = uvs,
                colors = colors,
                submeshTriangles = submeshTriangles
            };

            cachedScrollingMeshes[meshId] = cached;
            return cached;
        }

        /// <summary>
        /// Captures a renderer with scrolling UVs and submits it to state.skinned with live animated UV coordinates.
        /// </summary>
        private void CaptureScrollingMesh(MeshRenderer renderer, Mesh mesh, FrameState state, int frameCount)
        {
            var cachedMesh = GetOrCreateScrollingMesh(mesh);
            if (cachedMesh == null)
                return;

            if (!RemixScrollingTextureDetector.TryGetAnimatedUVTransform(renderer, sharedStaticMpb, out Vector4 uvST))
            {
                uvST = new Vector4(1f, 1f, 0f, 0f);
            }

            var materials = renderer.sharedMaterials;
            int rendererId = renderer.GetInstanceID();
            Matrix4x4 localToWorld = renderer.transform.localToWorldMatrix;

            for (int s = 0; s < cachedMesh.submeshTriangles.Count; s++)
            {
                var tris = cachedMesh.submeshTriangles[s];
                if (tris == null || tris.Length == 0) continue;

                Material mat = (materials != null && s < materials.Length) ? materials[s] : null;
                int matId = mat != null ? mat.GetInstanceID() : 0;
                if (mat != null)
                {
                    materialManager.CaptureMaterialTextures(mat, matId);
                }

                uint catFlags = 0;
                if (mat != null)
                {
                    string mName = mat.name;
                    if (!string.IsNullOrEmpty(mName) && mName.IndexOf("water", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        catFlags |= (uint)RemixAPI.remixapi_InstanceCategoryBit.REMIXAPI_INSTANCE_CATEGORY_BIT_ANIMATED_WATER;
                    }
                }

                ulong remixMeshHash = 0x5343524CUL << 32 | ((uint)rendererId ^ ((uint)s * 0x9E3779B9U));

                state.skinned.Add(new SkinnedMeshData
                {
                    meshId = rendererId ^ (s * 10007),
                    remixMeshHash = remixMeshHash,
                    materialId = matId,
                    vertices = cachedMesh.vertices,
                    normals = cachedMesh.normals,
                    uvs = cachedMesh.baseUVs,
                    colors = cachedMesh.colors,
                    triangles = tris,
                    localToWorld = localToWorld,
                    boneTransforms = null,
                    skinningData = null,
                    categoryFlags = catFlags,
                    uvST = uvST
                });
            }
        }

        /// <summary>
        /// On frames where static geometry is skipped (StaticMeshFrameSkip > 1), captures all active
        /// scrolling UV meshes so animated water, lava, and conveyors continue moving smoothly.
        /// </summary>
        private void CaptureAllScrollingMeshes(FrameState state, int frameCount, Vector3 camPos)
        {
            for (int i = 0; i < cachedScrollingRenderers.Count; i++)
            {
                var r = cachedScrollingRenderers[i];
                if (r == null || !r.enabled || !r.gameObject.activeInHierarchy) continue;
                if (r.transform.lossyScale.sqrMagnitude < 0.0001f) continue;
                if (IsLayerDisabled(r.gameObject.layer) || IsRendererDisabled(r.GetInstanceID())) continue;
                if (configUseVisibilityCulling.Value && !r.isVisible) continue;
                if (configUseDistanceCulling.Value)
                {
                    float maxDist = configMaxRenderDistance.Value;
                    if ((r.bounds.center - camPos).sqrMagnitude > maxDist * maxDist) continue;
                }
                var mf = r.GetComponent<MeshFilter>();
                if (mf != null && mf.sharedMesh != null)
                {
                    CaptureScrollingMesh(r, mf.sharedMesh, state, frameCount);
                }
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
                    if (skinned != null)
                    {
                        int staleId = HashUtils.GetHierarchyHashInt(skinned.transform);
                        persistentSkinnedData.Remove(staleId);
                    }
                    skipNull++;
                    continue;
                }

                var scale = skinned.transform.lossyScale;
                if (scale.sqrMagnitude < 0.0001f)
                {
                    int staleId = HashUtils.GetHierarchyHashInt(skinned.transform);
                    persistentSkinnedData.Remove(staleId);
                    skipNull++;
                    continue;
                }

                if (!skinned.updateWhenOffscreen)
                    skinned.updateWhenOffscreen = true;

                var anim = skinned.GetComponentInParent<Animator>();
                if (anim != null && anim.cullingMode != AnimatorCullingMode.AlwaysAnimate)
                    anim.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                
                if (IsLayerDisabled(skinned.gameObject.layer) || IsRendererDisabled(HashUtils.GetHierarchyHashInt(skinned.transform)))
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
                    if (configDebugLogInterval.Value > 0 && persistentSkinnedData.ContainsKey(HashUtils.GetHierarchyHashInt(skinned.transform)))
                        logger.LogWarning($"[SkinSkip] '{skinned.gameObject.name}' id={HashUtils.GetHierarchyHashInt(skinned.transform)}: sharedMesh became NULL");
                    skipNoMesh++;
                    continue;
                }
                
                int skinnedId = HashUtils.GetHierarchyHashInt(skinned.transform);
                validSkinnedIds.Add(skinnedId);
                // Also register per-submesh sub-keys so they survive the stale-pruning pass
                // Formula: sub0 = skinnedId, sub>0 = skinnedId ^ (sub * KNUTH_PRIME)
                var smMatsEarly = skinned.sharedMaterials;
                if (smMatsEarly != null)
                {
                    for (int sub = 1; sub < smMatsEarly.Length; sub++)
                        validSkinnedIds.Add((int)((uint)skinnedId ^ ((uint)sub * 0x9E3779B9u)));
                }
                
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
                        
                        // Emit one SkinnedMeshData per submesh so each submesh gets its correct material
                        // (fixes ATLYSS face/body/hair UV + color mismatch)
                        var smMaterials = skinned.sharedMaterials;
                        int subCount = (skinData.submeshTriangles != null) ? skinData.submeshTriangles.Length : 1;
                        for (int sub = 0; sub < subCount; sub++)
                        {
                            int[] subTris = (skinData.submeshTriangles != null && sub < skinData.submeshTriangles.Length)
                                ? skinData.submeshTriangles[sub]
                                : skinData.triangles;
                            if (subTris == null || subTris.Length == 0) continue;
                            
                            // Resolve per-submesh material
                            int subMatId = matId; // default to renderer-best material
                            if (smMaterials != null && sub < smMaterials.Length && smMaterials[sub] != null)
                            {
                                var subMat = smMaterials[sub];
                                subMatId = subMat.GetInstanceID();
                                // Re-capture material for this specific submesh with MPB overrides
                                // (MPB overrides apply to all submeshes of the renderer identically)
                                materialManager.CaptureMaterialTextures(subMat, subMatId);
                            }
                            
                            // Unique key per submesh: xor with Knuth multiplied submesh index
                            int subSkinnedId = (sub == 0) ? skinnedId : (int)((uint)skinnedId ^ ((uint)sub * 0x9E3779B9u));
                            ulong subMeshHash = (sub == 0) ? combinedMeshHash : combinedMeshHash ^ ((ulong)(uint)sub * 2654435761UL);
                            
                            persistentSkinnedData[subSkinnedId] = new SkinnedMeshData
                            {
                                meshId = subSkinnedId,
                                remixMeshHash = subMeshHash,
                                materialId = subMatId,
                                vertices = skinData.bindVertices,
                                normals = skinData.bindNormals,
                                uvs = skinData.uvs,
                                colors = skinData.colors,
                                triangles = subTris,
                                localToWorld = unscaledMatrix,
                                boneTransforms = boneMatrices,
                                skinningData = skinData
                            };
                            validSkinnedIds.Add(subSkinnedId);
                        }
                    gpuSkinned++;
                    continue;
                }
                
                // Update transform on existing persistent data (for all submesh entries of this renderer)
                var smMatsForUpdate = skinned.sharedMaterials;
                int subCountForUpdate = (smMatsForUpdate != null) ? smMatsForUpdate.Length : 1;
                for (int sub = 0; sub < subCountForUpdate; sub++)
                {
                    int subKey = (sub == 0) ? skinnedId : (int)((uint)skinnedId ^ ((uint)sub * 0x9E3779B9u));
                    if (persistentSkinnedData.TryGetValue(subKey, out var existing))
                    {
                        existing.localToWorld = unscaledMatrix;
                        persistentSkinnedData[subKey] = existing;
                    }
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
                    {
                        baked++;
                        // Register all per-submesh keys as valid so they aren't pruned
                        var smMatsForBake = skinned.sharedMaterials;
                        int subCntBake = (smMatsForBake != null) ? smMatsForBake.Length : 1;
                        for (int sub = 0; sub < subCntBake; sub++)
                        {
                            int subKey = (sub == 0) ? skinnedId : (int)((uint)skinnedId ^ ((uint)sub * 0x9E3779B9u));
                            validSkinnedIds.Add(subKey);
                        }
                    }
                    
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
            
            // Capture world-space weapon UI screens (e.g. Nailgun ammo counter/heat, Shotgun slider, Rocket Launcher timer)
            CaptureWeaponCanvasScreens(state, frameCount);
            
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
                    if (cr != null)
                        mesh = UnityCompat.GetCanvasMesh(cr);
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

                if (colors == null || colors.Length != verts.Length)
                {
                    colors = new Color32[verts.Length];
                    Color32 c = col;
                    for (int cIdx = 0; cIdx < colors.Length; cIdx++)
                        colors[cIdx] = c;
                }

                Material mat = g.materialForRendering;
                if (mat == null)
                    mat = g.defaultMaterial;
                if (mat == null)
                    continue;

                Texture2D mainTex = g.mainTexture as Texture2D;

                Color32 c32 = col;
                int qR = (c32.r >> 3);
                int qG = (c32.g >> 3);
                int qB = (c32.b >> 3);
                int qA = (c32.a >> 3);
                int colHash = (qA << 15) | (qR << 10) | (qG << 5) | qB;
                int texHash = mainTex != null ? mainTex.GetInstanceID() : 0;
                int uiMatId = HashCombine(mat.GetInstanceID(), HashCombine(texHash, colHash));

                materialManager.CaptureMaterialTextures(
                    mat,
                    uiMatId,
                    mpbEmissiveColor: col,
                    mpbEmissiveIntensity: 1.0f,
                    mpbMainTex: mainTex,
                    mpbColor: col
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
                    skinningData = null,
                    categoryFlags = (uint)RemixAPI.remixapi_InstanceCategoryBit.REMIXAPI_INSTANCE_CATEGORY_BIT_WORLD_UI
                });
            }
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
            Vector3 camPos = mainCam.transform.position;
            float maxParticleDist = (configParticleMaxDistance != null && configParticleMaxDistance.Value > 0f)
                ? configParticleMaxDistance.Value
                : 60f;
            float maxParticleDistSqr = maxParticleDist * maxParticleDist;

            // Check active particle renderers every frame to capture transient particles immediately (bullet impacts, sparks, newly spawned systems)
            var activeRenderers = UnityCompat.FindActiveSceneComponents<ParticleSystemRenderer>();
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

                // Distance culling: skip particle systems whose emitter transform is beyond max render distance
                if (configEnableParticleDistanceCulling != null && configEnableParticleDistanceCulling.Value && (pr.transform.position - camPos).sqrMagnitude > maxParticleDistSqr)
                    continue;

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

                bool isFlatShape = ps.shape.enabled && (
                    ps.shape.shapeType == ParticleSystemShapeType.Circle || 
                    ps.shape.shapeType == ParticleSystemShapeType.Donut ||
                    ps.shape.shapeType == ParticleSystemShapeType.Rectangle
                );
                bool isShapeAligned = isFlatShape && ps.shape.alignToDirection;
                bool isJumpPad = pr.name.IndexOf("jump", StringComparison.OrdinalIgnoreCase) >= 0 
                    || (pr.transform.parent != null && pr.transform.parent.name.IndexOf("jump", StringComparison.OrdinalIgnoreCase) >= 0);
                bool isJumpPadRing = isJumpPad && (isShapeAligned || isFlatShape || ps.main.startSizeMultiplier >= 1.0f);

                bool isSurfaceAligned = isShapeAligned || isJumpPadRing;

                if (!isSurfaceAligned && (pr.renderMode != ParticleSystemRenderMode.Billboard || pr.alignment != ParticleSystemRenderSpace.View))
                {
                    if (!BakeMeshParticleSystem(pr, mainCam, tintedMatId, state))
                    {
                        int particlesToDraw = Math.Min(actualAlive, 4096);
                        GenerateBillboardParticleSystem(pr, ps, particlesToDraw, tintedMatId, mainCam, camRight, camUp, camForward, state, isSurfaceAligned);
                    }
                }
                else
                {
                    int particlesToDraw = Math.Min(actualAlive, 4096);
                    GenerateBillboardParticleSystem(pr, ps, particlesToDraw, tintedMatId, mainCam, camRight, camUp, camForward, state, isSurfaceAligned);
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
            FrameState state,
            bool isSurfaceAligned = false)
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

            Vector3 upDir = pr.transform.up;
            if (upDir.sqrMagnitude < 0.001f) upDir = Vector3.up;

            Vector3 defaultNormal = -camForward;
            if (isSurfaceAligned) defaultNormal = upDir;
            else if (isHorizontal || alignment == ParticleSystemRenderSpace.World) defaultNormal = Vector3.up;
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
                if (isSurfaceAligned)
                {
                    Vector3 rightDir = pr.transform.right;
                    Vector3 fwdDir = pr.transform.forward;
                    if (rightDir.sqrMagnitude < 0.001f) rightDir = Vector3.right;
                    if (fwdDir.sqrMagnitude < 0.001f) fwdDir = Vector3.forward;

                    rAxis = rightDir * cosR + fwdDir * sinR;
                    uAxis = -rightDir * sinR + fwdDir * cosR;
                    hR = rAxis * (size.x * 0.5f);
                    hU = uAxis * (size.y * 0.5f);
                    pos += upDir * 0.02f;
                }
                else if (isHorizontal)
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

                Vector3 norm = isSurfaceAligned ? upDir : Vector3.Cross(uAxis, rAxis).normalized;
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
                skinningData = null,
                categoryFlags = (uint)RemixAPI.remixapi_InstanceCategoryBit.REMIXAPI_INSTANCE_CATEGORY_BIT_PARTICLE
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
                        skinningData = null,
                        categoryFlags = (uint)RemixAPI.remixapi_InstanceCategoryBit.REMIXAPI_INSTANCE_CATEGORY_BIT_PARTICLE
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
                        skinningData = null,
                        categoryFlags = (uint)RemixAPI.remixapi_InstanceCategoryBit.REMIXAPI_INSTANCE_CATEGORY_BIT_PARTICLE
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

            var activeTrails = UnityCompat.FindActiveSceneComponents<TrailRenderer>();
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
                        skinningData = null,
                        categoryFlags = (uint)RemixAPI.remixapi_InstanceCategoryBit.REMIXAPI_INSTANCE_CATEGORY_BIT_PARTICLE
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
                int[][] perSubTrisLocal = null;
                
                if (mesh.isReadable)
                {
                    bindVerts = mesh.vertices;
                    bindNorms = mesh.normals;
                    uvs = mesh.uv;
                    colors = mesh.colors32;
                    if (colors != null && colors.Length == 0) colors = null;
                    
                    var allTris = new List<int>();
                    var perSubTris = new int[mesh.subMeshCount][];
                    for (int s = 0; s < mesh.subMeshCount; s++)
                    {
                        if (mesh.GetTopology(s) != MeshTopology.Triangles)
                        {
                            perSubTris[s] = new int[0];
                            continue;
                        }
                        var sub = mesh.GetTriangles(s);
                        perSubTris[s] = (sub != null && sub.Length > 0) ? sub : new int[0];
                        if (sub != null && sub.Length > 0)
                            allTris.AddRange(sub);
                    }
                    triangles = allTris.ToArray();
                    // store per-submesh for multi-material rendering
                    perSubTrisLocal = perSubTris;
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
                    var perSubTris = new int[tempMesh.subMeshCount][];
                    for (int s = 0; s < tempMesh.subMeshCount; s++)
                    {
                        if (tempMesh.GetTopology(s) != MeshTopology.Triangles)
                        {
                            perSubTris[s] = new int[0];
                            continue;
                        }
                        var sub = tempMesh.GetTriangles(s);
                        perSubTris[s] = (sub != null && sub.Length > 0) ? sub : new int[0];
                        if (sub != null && sub.Length > 0)
                            allTris.AddRange(sub);
                    }
                    triangles = allTris.ToArray();
                    perSubTrisLocal = perSubTris;
                    
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
                    submeshTriangles = perSubTrisLocal,
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
                
                // Build per-submesh triangle arrays from bakedMesh
                int subMeshCount = bakedMesh.subMeshCount;
                var perSubTrisBake = new int[subMeshCount][];
                for (int i = 0; i < subMeshCount; i++)
                {
                    if (bakedMesh.GetTopology(i) != MeshTopology.Triangles) { perSubTrisBake[i] = new int[0]; continue; }
                    var st = bakedMesh.GetTriangles(i);
                    perSubTrisBake[i] = (st != null && st.Length > 0) ? st : new int[0];
                }
                
                // Fallback: if no valid per-sub triangles, use merged
                bool anySubValid = false;
                foreach (var st in perSubTrisBake) if (st != null && st.Length > 0) { anySubValid = true; break; }
                if (!anySubValid)
                {
                    if (tris.Length == 0 || tris.Length % 3 != 0) return false;
                    perSubTrisBake = new int[][] { tris };
                }
                
                var smMaterialsBake = skinned.sharedMaterials;
                
                for (int sub = 0; sub < perSubTrisBake.Length; sub++)
                {
                    int[] subTris = perSubTrisBake[sub];
                    if (subTris == null || subTris.Length == 0 || subTris.Length % 3 != 0) continue;
                    
                    int subMatId = matId;
                    if (smMaterialsBake != null && sub < smMaterialsBake.Length && smMaterialsBake[sub] != null)
                    {
                        subMatId = smMaterialsBake[sub].GetInstanceID();
                        materialManager.CaptureMaterialTextures(smMaterialsBake[sub], subMatId);
                    }
                    
                    int subKey = (sub == 0) ? skinnedId : (int)((uint)skinnedId ^ ((uint)sub * 0x9E3779B9u));
                    ulong subMeshHash = (sub == 0) ? combinedMeshHash : combinedMeshHash ^ ((ulong)(uint)sub * 2654435761UL);
                    
                    persistentSkinnedData[subKey] = new SkinnedMeshData
                    {
                        meshId = subKey,
                        remixMeshHash = (sub == 0) ? combinedMeshHash : subMeshHash,
                        materialId = subMatId,
                        vertices = verts,
                        normals = norms,
                        uvs = uvCoords,
                        colors = colors,
                        triangles = subTris,
                        localToWorld = localToWorld
                    };
                }

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
            
            string[] textureProps = { "_MainTex", "_BaseMap", "_BaseTexture", "_Texture1", "_Texture", "_BaseColorMap", "_AlbedoTex", "_Albedo", "_Diffuse", "_TopTex" };
            
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
                Color? mpbColor = null;
                Color? mpbEmissiveColor = null;
                float? mpbEmissiveIntensity = null;
                try
                {
                    var mpb = new MaterialPropertyBlock();
                    skinned.GetPropertyBlock(mpb);
                    if (!mpb.isEmpty)
                    {
                        // Capture albedo/diffuse color overrides (ATLYSS character skin/hair/eye colors)
                        string[] colorProps = { "_Color", "_BaseColor", "_TintColor", "_Tint", "_Color1", "_Color2" };
                        foreach (var cp in colorProps)
                        {
                            if (bestMaterial.HasProperty(cp))
                            {
                                var c = mpb.GetColor(cp);
                                // Non-white means an intentional override
                                if (c.r < 0.99f || c.g < 0.99f || c.b < 0.99f || c.a < 0.99f)
                                {
                                    mpbColor = c;
                                    // Generate unique material ID for this renderer's color override
                                    Color32 c32 = c;
                                    int colInt = (c32.r << 24) | (c32.g << 16) | (c32.b << 8) | c32.a;
                                    matId = HashCombine(bestMaterial.GetInstanceID(), HashCombine(HashUtils.GetHierarchyHashInt(skinned.transform), colInt));
                                    break;
                                }
                            }
                        }

                        if (bestMaterial.HasProperty("_EmissiveColor"))
                        {
                            var c = mpb.GetColor("_EmissiveColor");
                            if (c.r != 0 || c.g != 0 || c.b != 0)
                            {
                                mpbEmissiveColor = c;
                                if (matId == bestMaterial.GetInstanceID())
                                {
                                    // Only update matId if albedo wasn't already overriding it
                                    matId = HashCombine(bestMaterial.GetInstanceID(), HashUtils.GetHierarchyHashInt(skinned.transform));
                                }
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
                
                materialManager.CaptureMaterialTextures(bestMaterial, matId, mpbEmissiveColor, mpbEmissiveIntensity, null, mpbColor);
                
                if (doLog)
                {
                    string logKey = $"{skinned.name}:{bestMaterial.name}:{matId}";
                    if (!loggedSkinnedMaterials.Contains(logKey))
                    {
                        loggedSkinnedMaterials.Add(logKey);
                        if (mpbColor.HasValue)
                        {
                            var c = mpbColor.Value;
                            logger.LogInfo($"  Skinned mesh '{skinned.name}' using material '{bestMaterial.name}' (ID: {matId}) [MPB _Color=({c.r:F3},{c.g:F3},{c.b:F3},{c.a:F3})]");
                        }
                        else if (mpbEmissiveColor.HasValue)
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
            cachedTopology.Clear();
        }
    }
}
