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
        private readonly Dictionary<int, Color> _materialTintCache = new Dictionary<int, Color>();
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
            public int drawnFrame; // frame the main capture loop last drew it (active that frame)
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
        public enum DynamicSource : byte { Skinned, Scrolling, WorldUI, Billboard, MeshInstanced, MeshBaked, ParticleTrail, Trail, Count }

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
        }

        private class CachedSkinnedRendererInfo
        {
            public int rendererId;
            public int hierarchyHash;
            public Transform transform;
            public GameObject gameObject;
            public Animator animator;
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
        private readonly List<(int idx, SkinnedMeshRenderer smr, int skinnedId, Matrix4x4 matrix, int matId)> reusableBakeFallbackQueue = new List<(int idx, SkinnedMeshRenderer smr, int skinnedId, Matrix4x4 matrix, int matId)>();
        private readonly List<int> reusableStaleSkinnedKeys = new List<int>();
        private Camera lastTrackedCamera;
        private int lastCameraTrackFrame = -100;

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
                    isPartOfStaticBatch = r.isPartOfStaticBatch
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

                info = new CachedSkinnedRendererInfo
                {
                    rendererId = id,
                    hierarchyHash = HashUtils.GetHierarchyHashInt(tr),
                    transform = tr,
                    gameObject = sr.gameObject,
                    animator = sr.GetComponentInParent<Animator>(),
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

        /// <summary>
        /// Invalidate caches on scene change
        /// </summary>
        public void InvalidateCaches()
        {
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
            cachedTopology.Clear();
            bakedTopology.Clear();
            cachedSkinning.Clear();
            skinningBases.Clear();
            cachedScrollingMeshes.Clear();
            scrollingRendererIds.Clear();
            cachedScrollingRenderers.Clear();
            RemixScrollingTextureDetector.ClearCache();
            lock (persistentStaticLock)
            {
                persistentStaticInstances.Clear();
            }
            trackedParticleSystems.Clear();
            trackedParticleSystemIds.Clear();
            meshParticleInstancing.Clear();
            trackedTrailRenderers.Clear();
            trackedTrailRendererIds.Clear();
            _capturedParticleMatIds.Clear();
            _materialTintCache.Clear();
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
            _materialTintCache.Clear();
            _particleTraitsCache.Clear();
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
                    GetOrCreateStaticInfo(r);
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
                    var info = GetOrCreateSkinnedInfo(sr);
                    if (info.animator != null && info.animator.cullingMode != AnimatorCullingMode.AlwaysAnimate)
                        info.animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                }
            }

            var allParticles = UnityCompat.FindSceneComponentsIncludingInactive<ParticleSystemRenderer>();
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

            var allTrails = UnityCompat.FindSceneComponentsIncludingInactive<TrailRenderer>();
            for (int i = 0; i < allTrails.Length; i++)
            {
                var tr = allTrails[i];
                if (tr != null && trackedTrailRendererIds.Add(tr.GetInstanceID()))
                {
                    trackedTrailRenderers.Add(new TrackedTrailRenderer
                    {
                        renderer = tr,
                        id = tr.GetInstanceID()
                    });
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
            
            var childRenderers = cam.GetComponentsInChildren<MeshRenderer>(false);
            for (int i = 0; i < childRenderers.Length; i++)
            {
                var r = childRenderers[i];
                if (r != null && cachedRendererIds.Add(r.GetInstanceID()))
                {
                    cachedRenderers.Add(r);
                    GetOrCreateStaticInfo(r);
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
                    var info = GetOrCreateSkinnedInfo(sr);
                    if (info.animator != null && info.animator.cullingMode != AnimatorCullingMode.AlwaysAnimate)
                        info.animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
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
                EnsureCameraRenderersTracked(mainCam, frameCount);
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
            Transform mainCamTransform = mainCam != null ? mainCam.transform : null;
            float maxDistSqr = configMaxRenderDistance.Value * configMaxRenderDistance.Value;
            bool useDistCulling = configUseDistanceCulling.Value;
            bool useVisCulling = configUseVisibilityCulling.Value;

            foreach (var renderer in cachedRenderers)
            {
                if (renderer == null || !renderer.enabled)
                    continue;

                var info = GetOrCreateStaticInfo(renderer);
                if (info.isPartOfStaticBatch && !scrollingRendererIds.Contains(info.rendererId))
                    continue;

                var go = info.gameObject;
                if (go == null || !go.activeInHierarchy)
                    continue;

                int layer = go.layer;
                if (IsLayerDisabled(layer))
                    continue;

                int rendererInstanceId = info.rendererId;
                if (IsRendererDisabled(rendererInstanceId))
                    continue;

                var tr = info.transform;
                var lossy = tr.lossyScale;
                if (lossy.sqrMagnitude < 0.0001f)
                    continue;
                
                // Optional visibility culling (can cause issues in some games)
                if (useVisCulling && !renderer.isVisible)
                    continue;

                var meshFilter = info.filter;
                if (meshFilter == null)
                    continue;
                var mesh = meshFilter.sharedMesh;
                if (mesh == null)
                    continue;

                Bounds bounds = renderer.bounds;
                
                // Optional distance culling
                if (useDistCulling)
                {
                    float sqrDistance = (bounds.center - camPos).sqrMagnitude;
                    if (sqrDistance > maxDistSqr)
                        continue;
                }
                
                int meshId = mesh.GetInstanceID();
                var materials = info.sharedMaterials;
                int baseMatSig = info.materialSignature;
                var dedupeKey = StaticGeometryDedupe.BuildKey(meshId, layer, baseMatSig, in bounds);

                // Animated / scrolling UV meshes (water, lava, conveyor belts, scrolling textures)
                // must be rendered dynamically via state.skinned with live UV offsets every frame.
                if (scrollingRendererIds.Contains(rendererInstanceId) || RemixScrollingTextureDetector.IsScrollingRenderer(renderer))
                {
                    if (scrollingRendererIds.Add(rendererInstanceId))
                    {
                        cachedScrollingRenderers.Add(renderer);
                        logger.LogInfo($"[ScrollingTexture] Registered scrolling renderer '{go.name}' (id={rendererInstanceId}, mesh='{mesh.name}') - total scrolling: {scrollingRendererIds.Count}");
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

                    Texture tex = sharedStaticMpb.GetTexture(PropMainTex);
                    if (tex == null) tex = sharedStaticMpb.GetTexture(PropBaseMap);
                    if (tex == null) tex = sharedStaticMpb.GetTexture(PropDiffuse);
                    if (tex == null) tex = sharedStaticMpb.GetTexture(PropTexture);
                    if (tex != null)
                    {
                        mpbMainTex = tex;
                        mpbHash = HashCombine(mpbHash, tex.GetInstanceID());
                    }
                    Color col = sharedStaticMpb.GetColor(PropColor);
                    if (col.a <= 0f && col.r <= 0f && col.g <= 0f && col.b <= 0f)
                        col = sharedStaticMpb.GetColor(PropBaseColor);
                    if (col.a > 0f || col.r > 0f || col.g > 0f || col.b > 0f)
                    {
                        mpbColor = col;
                        Color32 c32 = col;
                        int colInt = (c32.a << 24) | (c32.r << 16) | (c32.g << 8) | c32.b;
                        mpbHash = HashCombine(mpbHash, colInt);
                    }
                    Color emis = sharedStaticMpb.GetColor(PropEmissiveColor);
                    if (emis.a > 0f || emis.r > 0f || emis.g > 0f || emis.b > 0f)
                    {
                        mpbEmissive = emis;
                        Color32 e32 = emis;
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
                bool isViewModel = mainCamTransform != null && tr.IsChildOf(mainCamTransform);
                uint categoryFlags = 0;
                if (isViewModel)
                {
                    categoryFlags |= (uint)RemixAPI.remixapi_InstanceCategoryBit.REMIXAPI_INSTANCE_CATEGORY_BIT_VIEW_MODEL;
                }
                
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
                                int matId = StaticGeometryDedupe.GetCanonicalMaterialId(materials[m]);
                                if (mpbHash != 0)
                                {
                                    matId = HashCombine(matId, mpbHash);
                                }
                                try
                                {
                                    var mMat = materials[m];
                                    if (mMat.HasProperty("_Hue") || mMat.HasProperty("_HueShift") || mMat.HasProperty("_HueAdjust") ||
                                        mMat.HasProperty("_Brightness") || mMat.HasProperty("_Contrast") || mMat.HasProperty("_Saturation") ||
                                        mMat.HasProperty("_HSBC"))
                                    {
                                        float h = mMat.HasProperty("_Hue") ? mMat.GetFloat("_Hue") :
                                                  (mMat.HasProperty("_HueShift") ? mMat.GetFloat("_HueShift") :
                                                  (mMat.HasProperty("_HueAdjust") ? mMat.GetFloat("_HueAdjust") : 0f));
                                        float b = mMat.HasProperty("_Brightness") ? mMat.GetFloat("_Brightness") : 0f;
                                        float c = mMat.HasProperty("_Contrast") ? mMat.GetFloat("_Contrast") : 1f;
                                        float s = mMat.HasProperty("_Saturation") ? mMat.GetFloat("_Saturation") : 1f;
                                        if (mMat.HasProperty("_HSBC"))
                                        {
                                            Vector4 v = mMat.GetVector("_HSBC");
                                            h = v.x; s = v.y; b = v.z; c = v.w;
                                        }

                                        if (Mathf.Abs(h) > 0.001f || Mathf.Abs(b) > 0.001f || Mathf.Abs(c - 1f) > 0.001f || Mathf.Abs(s - 1f) > 0.001f)
                                        {
                                            int hInt = Mathf.RoundToInt(h * 1000f);
                                            int bInt = Mathf.RoundToInt(b * 1000f);
                                            int cInt = Mathf.RoundToInt(c * 1000f);
                                            int sInt = Mathf.RoundToInt(s * 1000f);
                                            int hsbcHash = HashCombine(HashCombine(hInt, bInt), HashCombine(cInt, sInt));
                                            matId = HashCombine(matId, hsbcHash);
                                        }
                                    }
                                }
                                catch { }
                                submeshMaterialIds.Add(matId);
                                materialManager.CaptureMaterialTextures(materials[m], matId, mpbEmissive, null, mpbMainTex as Texture2D, mpbColor, renderer);
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
                var transform = tr.localToWorldMatrix;
                var instanceData = new MeshInstanceData
                {
                    meshKey = meshKey,
                    meshId = meshId,
                    localToWorld = transform,
                    rendererInstanceId = rendererInstanceId,
                    dedupeKey = dedupeKey,
                    categoryFlags = categoryFlags
                };
                state.instances.Add(instanceData);
                totalDrawn++;
                
                // Remember this renderer's transform so we can keep drawing it if it gets disabled.
                // Do NOT persist viewmodels/weapons (they should disappear when unequipped).
                if (!isViewModel)
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
                            dedupeKey = dedupeKey,
                            drawnFrame = frameCount
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
                        // Drawn by the loop above this frame: alive and active, nothing to check.
                        if (entry.drawnFrame == frameCount)
                            continue;

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
                    dedupeKey = dedupeKey,
                    categoryFlags = (uint)RemixAPI.remixapi_InstanceCategoryBit.REMIXAPI_INSTANCE_CATEGORY_BIT_VIEW_MODEL
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
                    materialManager.CaptureMaterialTextures(mat, matId, renderer: renderer);
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
                    source = DynamicSource.Scrolling,
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
                    uvST = uvST,
                    bakeFrame = frameCount
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
            reusableValidSkinnedIds.Clear();
            var validSkinnedIds = reusableValidSkinnedIds;
            
            Camera mainCam = cameraHandler.GetPreferredCamera();
            Vector3 camPos = mainCam != null ? mainCam.transform.position : Vector3.zero;

            if (mainCam != null)
            {
                EnsureCameraRenderersTracked(mainCam, frameCount);
            }
            
            // BakeMesh fallback budget
            var bakeSw = System.Diagnostics.Stopwatch.StartNew();
            const float bakeMaxMs = 100.0f;
            if (skinnedRoundRobinIndex >= total) skinnedRoundRobinIndex = 0;
            reusableBakeFallbackQueue.Clear();
            var bakeFallbackQueue = reusableBakeFallbackQueue;

            bool useDistCulling = configUseDistanceCulling.Value;
            float maxDistSqr = configMaxRenderDistance.Value * configMaxRenderDistance.Value;
            bool useHwSkinning = configHardwareSkinning.Value;
            bool useEvenBuffer = (frameCount & 1) == 0;
            // Baked renderers outside the camera's view (seen only in reflections and shadows)
            // are re-baked every OffscreenBakeInterval frames, staggered; they still follow their
            // transform every frame.
            offscreenBakeSkips = 0;
            bool haveFrustum = mainCam != null;
            if (haveFrustum)
                GeometryUtility.CalculateFrustumPlanes(mainCam, skinnedFrustumPlanes);
            
            for (int i = 0; i < total; i++)
            {
                var skinned = cachedSkinnedRenderers[i];
                if (skinned == null)
                {
                    skipNull++;
                    continue;
                }

                var info = GetOrCreateSkinnedInfo(skinned);
                int skinnedId = info.hierarchyHash;
                var go = info.gameObject;

                if (!skinned.enabled || go == null || !go.activeInHierarchy)
                {
                    persistentSkinnedData.Remove(skinnedId);
                    skipNull++;
                    continue;
                }

                var tr = info.transform;
                var ls = tr.lossyScale;
                if (ls.sqrMagnitude < 0.0001f)
                {
                    persistentSkinnedData.Remove(skinnedId);
                    skipNull++;
                    continue;
                }

                if (!skinned.updateWhenOffscreen)
                    skinned.updateWhenOffscreen = true;

                var anim = info.animator;
                if (anim != null && anim.cullingMode != AnimatorCullingMode.AlwaysAnimate)
                    anim.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                
                if (IsLayerDisabled(go.layer) || IsRendererDisabled(skinnedId))
                {
                    skipLayer++;
                    continue;
                }
                
                if (useDistCulling)
                {
                    float sqrDistance = (skinned.bounds.center - camPos).sqrMagnitude;
                    if (sqrDistance > maxDistSqr)
                    {
                        skipDist++;
                        continue;
                    }
                }
                
                var sharedMesh = skinned.sharedMesh;
                if (sharedMesh == null)
                {
                    if (configDebugLogInterval.Value > 0 && persistentSkinnedData.ContainsKey(skinnedId))
                        logger.LogWarning($"[SkinSkip] '{go.name}' id={skinnedId}: sharedMesh became NULL");
                    skipNoMesh++;
                    continue;
                }
                
                validSkinnedIds.Add(skinnedId);
                // Also register per-submesh sub-keys so they survive the stale-pruning pass
                // Formula: sub0 = skinnedId, sub>0 = skinnedId ^ (sub * KNUTH_PRIME)
                var smMaterials = info.sharedMaterials;
                if (smMaterials != null)
                {
                    for (int sub = 1; sub < smMaterials.Length; sub++)
                        validSkinnedIds.Add((int)((uint)skinnedId ^ ((uint)sub * 0x9E3779B9u)));
                }
                
                // Compute unscaled transform (sign-only scale preserves winding)
                Matrix4x4 unscaledMatrix = Matrix4x4.TRS(
                    tr.position,
                    tr.rotation,
                    new Vector3(Mathf.Sign(ls.x), Mathf.Sign(ls.y), Mathf.Sign(ls.z))
                );
                
                Material primaryMat = info.primarySharedMaterial;
                int matId = ResolveMaterial(skinned, primaryMat, doLog);
                int sharedMeshId = sharedMesh.GetInstanceID();

                // Compute BlendShape hash from active weights to support custom character creator proportions (belly, torso, etc.)
                uint bsHash = 0;
                int bsCount = sharedMesh.blendShapeCount;
                if (bsCount > 0)
                {
                    bsHash = 2166136261u;
                    for (int bsi = 0; bsi < bsCount; bsi++)
                    {
                        int wInt = Mathf.RoundToInt(skinned.GetBlendShapeWeight(bsi) * 10f);
                        bsHash = (bsHash ^ (uint)wInt) * 16777619u;
                    }
                }
                long skinCacheKey = ((long)sharedMeshId << 32) | (uint)bsHash;
                
                // Try GPU skinning path: extract bone weights once, compute bone transforms each frame
                var skinData = (CachedSkinningData)null;
                if (useHwSkinning)
                {
                    if (!cachedSkinning.TryGetValue(skinCacheKey, out skinData))
                    {
                        CacheSkinningData(skinned, skinCacheKey);
                        cachedSkinning.TryGetValue(skinCacheKey, out skinData);
                    }
                    if (skinData != null)
                        skinData.lastUsedFrame = frameCount;
                }

                var bones = info.bones;
                if (skinData != null && bones != null && bones.Length > 0)
                {
                    // Compute bone transforms for Remix GPU skinning using double-buffered array
                    int boneCount = skinData.boneCount;
                    Matrix4x4[] boneMatrices;
                    if (useEvenBuffer)
                    {
                        if (info.boneMatricesA == null || info.boneMatricesA.Length != boneCount)
                            info.boneMatricesA = new Matrix4x4[boneCount];
                        boneMatrices = info.boneMatricesA;
                    }
                    else
                    {
                        if (info.boneMatricesB == null || info.boneMatricesB.Length != boneCount)
                            info.boneMatricesB = new Matrix4x4[boneCount];
                        boneMatrices = info.boneMatricesB;
                    }
                    
                    Matrix4x4 invInstance = unscaledMatrix.inverse;
                    var skeleton = GetSkeletonPose(info, frameCount);
                    var world = skeleton.world;
                    var present = skeleton.present;
                    var bindPoses = skinData.bindPoses;
                    for (int b = 0; b < boneCount; b++)
                    {
                        if (b < present.Length && present[b])
                        {
                            MulAffine(ref invInstance, ref world[b], out Matrix4x4 boneToInstance);
                            MulAffine(ref boneToInstance, ref bindPoses[b], out boneMatrices[b]);
                        }
                        else
                        {
                            boneMatrices[b] = Matrix4x4.identity;
                        }
                    }
                    
                    if (!skinnedMeshHashes.TryGetValue(sharedMeshId, out ulong combinedMeshHash))
                    {
                        combinedMeshHash = RemixMeshConverter.GenerateMeshHash(sharedMesh);
                        skinnedMeshHashes[sharedMeshId] = combinedMeshHash;
                    }
                    var boneHashes = info.boneNameHashes;
                    if (boneHashes != null)
                    {
                        for (int b = 0; b < boneHashes.Length; b++)
                        {
                            if (boneHashes[b] != 0UL)
                                combinedMeshHash ^= boneHashes[b];
                            combinedMeshHash *= 1099511628211UL;
                        }
                    }
                    if (bsHash != 0)
                    {
                        combinedMeshHash ^= (ulong)bsHash;
                        combinedMeshHash *= 1099511628211UL;
                    }
                    
                    // Emit one SkinnedMeshData per submesh so each submesh gets its correct material
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
                            subMatId = (sub == 0) ? matId : ResolveMaterial(skinned, subMat, doLog);
                            if (subMatId == 0) subMatId = matId;
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
                int subCountForUpdate = (smMaterials != null) ? smMaterials.Length : 1;
                for (int sub = 0; sub < subCountForUpdate; sub++)
                {
                    int subKey = (sub == 0) ? skinnedId : (int)((uint)skinnedId ^ ((uint)sub * 0x9E3779B9u));
                    if (persistentSkinnedData.TryGetValue(subKey, out var existing))
                    {
                        existing.localToWorld = unscaledMatrix;
                        persistentSkinnedData[subKey] = existing;
                    }
                }
                
                if (haveFrustum && ((frameCount + skinnedId) % OffscreenBakeInterval) != 0 &&
                    persistentSkinnedData.ContainsKey(skinnedId) &&
                    !GeometryUtility.TestPlanesAABB(skinnedFrustumPlanes, skinned.bounds))
                {
                    offscreenBakeSkips++;
                    continue;
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
                        var smMatsForBake = GetOrCreateSkinnedInfo(skinned).sharedMaterials;
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
                reusableStaleSkinnedKeys.Clear();
                foreach (var id in persistentSkinnedData.Keys)
                {
                    if (!validSkinnedIds.Contains(id))
                    {
                        reusableStaleSkinnedKeys.Add(id);
                    }
                }
                for (int s = 0; s < reusableStaleSkinnedKeys.Count; s++)
                {
                    persistentSkinnedData.Remove(reusableStaleSkinnedKeys[s]);
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
            
            PruneSkinningCache(frameCount);

            // Capture world-space weapon UI screens (e.g. Nailgun ammo counter/heat, Shotgun slider, Rocket Launcher timer)
            CaptureWeaponCanvasScreens(state, frameCount);
            
            if (frameCount % 60 == 0) // Force log every 60 frames
            {
                logger.LogInfo($"CaptureSkinnedMeshes: gpuSkinned={gpuSkinned}, baked={baked}, offscreenSkipped={offscreenBakeSkips}, " +
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
                    mpbColor: col,
                    suppressLightGeneration: true
                );

                int graphicId = g.GetInstanceID();
                ulong remixMeshHash = (ulong)(uint)graphicId | 0x8000000000000000UL;

                state.skinned.Add(new SkinnedMeshData
                {
                    source = DynamicSource.WorldUI,
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
                    categoryFlags = (uint)RemixAPI.remixapi_InstanceCategoryBit.REMIXAPI_INSTANCE_CATEGORY_BIT_WORLD_UI,
                    bakeFrame = frameCount
                });
            }
        }
        
        /// <summary>
        /// Capture active particle systems (both pooled and dynamic) using zero-allocation billboard extraction.
        /// </summary>
        public void CaptureParticleSystems(FrameState state, int frameCount)
        {
            if (configCaptureParticles != null && !configCaptureParticles.Value)
            {
                if (frameCount % 30 == 0 || _latestParticleStats == null || _latestParticleStats.TotalTrackedSystems == 0)
                {
                    int totalPs = trackedParticleSystems.Count;
                    int activePs = 0;
                    for (int s = 0; s < trackedParticleSystems.Count; s++)
                    {
                        var trk = trackedParticleSystems[s];
                        if (trk.renderer != null && trk.renderer.enabled && trk.renderer.gameObject.activeInHierarchy)
                            activePs++;
                    }
                    _latestParticleStats = new ParticleStatsSnapshot
                    {
                        TotalTrackedSystems = totalPs,
                        ActiveSystems = activePs,
                        InactiveSystems = totalPs - activePs,
                        TotalTrackedTrails = trackedTrailRenderers.Count
                    };
                }
                return;
            }

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
            long discoverStart = System.Diagnostics.Stopwatch.GetTimestamp();

            // Recycle batch buckets from previous frame
            for (int b = 0; b < _activeParticleBatches.Count; b++)
            {
                _batchBucketPool.Add(_activeParticleBatches[b]);
            }
            _activeParticleBatches.Clear();
            _particleBatchesByMat.Clear();

            // Check active particle renderers periodically to capture newly spawned systems without sweeping full scene every frame
            if (frameCount % 4 == 0)
            {
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
            }

            // Always check camera hierarchy every frame for instantaneous first-person / viewmodel particle discovery
            if (mainCam != null)
            {
                var camRenderers = mainCam.GetComponentsInChildren<ParticleSystemRenderer>(false);
                for (int a = 0; a < camRenderers.Length; a++)
                {
                    var ar = camRenderers[a];
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
            }

            // Periodically prune destroyed particle systems
            if (frameCount % 180 == 0)
            {
                for (int d = trackedParticleSystems.Count - 1; d >= 0; d--)
                {
                    var t = trackedParticleSystems[d];
                    if (t.renderer == null || t.system == null)
                    {
                        trackedParticleSystemIds.Remove(t.id);
                        trackedParticleSystems.RemoveAt(d);
                    }
                }
            }

            int totalTracked = trackedParticleSystems.Count;
            int activeCount = 0;
            int inactiveCount = 0;
            int distanceCulled = 0;
            int emittingCount = 0;
            int renderedParticles = 0;
            int culledParticles = 0;
            int bakedMeshes = 0;
            float furthestDist = 0f;
            string furthestName = null;

            bool collectEmitters = (frameCount % 30 == 0);
            RemixProfiler.AddDetail(RemixProfiler.Detail.ParticleDiscover, RemixProfiler.ElapsedMs(discoverStart));
            long loopStart = System.Diagnostics.Stopwatch.GetTimestamp();
            double readMs = 0;
            particleAppendMs = 0;
            particleCurveCount = 0;
            List<ParticleEmitterInfo> emitterDetails = collectEmitters ? new List<ParticleEmitterInfo>(Math.Min(totalTracked, 256)) : null;

            for (int i = 0; i < trackedParticleSystems.Count; i++)
            {
                var tracked = trackedParticleSystems[i];
                var pr = tracked.renderer;
                var ps = tracked.system;

                if (pr == null || ps == null) continue;
                if (!pr.enabled || !pr.gameObject.activeInHierarchy)
                {
                    inactiveCount++;
                    if (collectEmitters && emitterDetails.Count < 200)
                    {
                        emitterDetails.Add(new ParticleEmitterInfo
                        {
                            Name = pr.name,
                            Distance = 0f,
                            ParticleCount = 0,
                            Status = "Inactive"
                        });
                    }
                    continue;
                }

                activeCount++;
                Bounds prBounds = pr.bounds;
                float distSqr = prBounds.extents.sqrMagnitude > 0.01f
                    ? prBounds.SqrDistance(camPos)
                    : (pr.transform.position - camPos).sqrMagnitude;
                float dist = Mathf.Sqrt(distSqr);
                if (dist > furthestDist)
                {
                    furthestDist = dist;
                    furthestName = pr.name;
                }

                int numAlive = ps.particleCount;

                // Distance culling: skip particle systems whose bounds/transform are beyond max render distance
                if (configEnableParticleDistanceCulling != null && configEnableParticleDistanceCulling.Value && distSqr > maxParticleDistSqr)
                {
                    distanceCulled++;
                    culledParticles += numAlive;
                    if (collectEmitters && emitterDetails.Count < 200)
                    {
                        emitterDetails.Add(new ParticleEmitterInfo
                        {
                            Name = pr.name,
                            Distance = dist,
                            ParticleCount = numAlive,
                            Status = "Culled"
                        });
                    }
                    continue;
                }

                if (numAlive <= 0)
                {
                    if (collectEmitters && emitterDetails.Count < 200)
                    {
                        emitterDetails.Add(new ParticleEmitterInfo
                        {
                            Name = pr.name,
                            Distance = dist,
                            ParticleCount = 0,
                            Status = "Idle (0)"
                        });
                    }
                    continue;
                }

                // Expand buffer if needed
                if (_particleBuffer.Length < numAlive)
                {
                    _particleBuffer = new ParticleSystem.Particle[Mathf.NextPowerOfTwo(numAlive)];
                }

                long readStart = System.Diagnostics.Stopwatch.GetTimestamp();
                int actualAlive = ps.GetParticles(_particleBuffer);
                readMs += RemixProfiler.ElapsedMs(readStart);
                if (actualAlive <= 0)
                {
                    if (collectEmitters && emitterDetails.Count < 200)
                    {
                        emitterDetails.Add(new ParticleEmitterInfo
                        {
                            Name = pr.name,
                            Distance = dist,
                            ParticleCount = 0,
                            Status = "Idle (0)"
                        });
                    }
                    continue;
                }

                emittingCount++;

                Material mat = pr.sharedMaterial;
                if (mat == null)
                {
                    var mats = pr.sharedMaterials;
                    if (mats != null && mats.Length > 0)
                        mat = mats[0];
                }
                if (mat == null) continue;

                // Sample color from active particles (fast struct read) or fallback
                Color pCol = Color.white;
                bool foundAliveColor = false;
                for (int pIdx = 0; pIdx < Math.Min(actualAlive, 8); pIdx++)
                {
                    Color32 c = _particleBuffer[pIdx].startColor;
                    if (c.a > 0 && (c.r > 2 || c.g > 2 || c.b > 2))
                    {
                        pCol = (Color)c;
                        foundAliveColor = true;
                        break;
                    }
                }
                if (!foundAliveColor && actualAlive > 0)
                {
                    Color32 c = _particleBuffer[0].GetCurrentColor(ps);
                    if (c.a > 0 && (c.r > 2 || c.g > 2 || c.b > 2))
                    {
                        pCol = (Color)c;
                        foundAliveColor = true;
                    }
                }
                if (!foundAliveColor)
                {
                    var main = ps.main;
                    var startColor = main.startColor;
                    if (startColor.mode == ParticleSystemGradientMode.Color)
                        pCol = startColor.color;
                    else if (startColor.mode == ParticleSystemGradientMode.TwoColors)
                        pCol = (startColor.colorMin + startColor.colorMax) * 0.5f;
                    else if (startColor.gradient != null)
                        pCol = startColor.gradient.Evaluate(0.5f);
                }

                // Check material tint using cache
                int matInstId = mat.GetInstanceID();
                if (!_materialTintCache.TryGetValue(matInstId, out Color matTint))
                {
                    matTint = Color.white;
                    if (mat.HasProperty("_TintColor"))
                        matTint = mat.GetColor("_TintColor");
                    else if (mat.HasProperty("_Color"))
                        matTint = mat.GetColor("_Color");
                    else if (mat.HasProperty("_BaseColor"))
                        matTint = mat.GetColor("_BaseColor");
                    _materialTintCache[matInstId] = matTint;
                }

                float matAlpha = Mathf.Clamp01(matTint.a);

                Color finalParticleColor = new Color(
                    pCol.r * matTint.r,
                    pCol.g * matTint.g,
                    pCol.b * matTint.b,
                    Mathf.Clamp01(pCol.a * matAlpha)
                );

                if (finalParticleColor.r < 0.01f && finalParticleColor.g < 0.01f && finalParticleColor.b < 0.01f)
                {
                    if (pCol.r > 0.05f || pCol.g > 0.05f || pCol.b > 0.05f)
                        finalParticleColor = new Color(pCol.r, pCol.g, pCol.b, finalParticleColor.a);
                    else if (matTint.r > 0.05f || matTint.g > 0.05f || matTint.b > 0.05f)
                        finalParticleColor = new Color(matTint.r, matTint.g, matTint.b, finalParticleColor.a);
                }

                long traitsKey = ((long)tracked.id << 32) | (uint)matInstId;
                if (!_particleTraitsCache.TryGetValue(traitsKey, out var traits))
                {
                    traits.emissive = IsEmissiveParticleSystem(pr, mat);
                    traits.jumpPad = pr.name.IndexOf("jump", StringComparison.OrdinalIgnoreCase) >= 0
                        || (pr.transform.parent != null && pr.transform.parent.name.IndexOf("jump", StringComparison.OrdinalIgnoreCase) >= 0);
                    _particleTraitsCache[traitsKey] = traits;
                }
                bool isEmissiveParticle = traits.emissive;
                float emIntensity = isEmissiveParticle ? 0.8f : 0f;

                // Quantize color (0..15 per channel) to group similar tints and prevent material explosion
                int r4 = Mathf.Clamp((int)(finalParticleColor.r * 15f + 0.5f), 0, 15);
                int g4 = Mathf.Clamp((int)(finalParticleColor.g * 15f + 0.5f), 0, 15);
                int b4 = Mathf.Clamp((int)(finalParticleColor.b * 15f + 0.5f), 0, 15);
                int colorKey = (isEmissiveParticle ? (1 << 12) : 0) | (r4 << 8) | (g4 << 4) | b4;
                Color quantizedColor = new Color(r4 / 15f, g4 / 15f, b4 / 15f, 1f);

                int tintedMatId = unchecked(mat.GetInstanceID() * 397 ^ colorKey);

                if (_capturedParticleMatIds.Add(tintedMatId))
                {
                    materialManager.CaptureMaterialTextures(
                        mat, 
                        tintedMatId, 
                        mpbEmissiveColor: isEmissiveParticle ? (Color?)quantizedColor : null, 
                        mpbEmissiveIntensity: emIntensity, 
                        mpbColor: quantizedColor,
                        suppressLightGeneration: true,
                        renderer: pr
                    );
                }

                if (configDebugLogInterval.Value > 0)
                {
                    if (loggedParticleSystems.Add(pr.name))
                    {
                        logger.LogInfo($"[ParticleSystem] First capture '{pr.name}' (renderMode={pr.renderMode}, alive={actualAlive}, mat='{mat.name}', color=({quantizedColor.r:F2},{quantizedColor.g:F2},{quantizedColor.b:F2}), matAlpha={matAlpha:F2}, em={emIntensity:F1})");
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
                bool isJumpPad = traits.jumpPad;
                bool isJumpPadRing = isJumpPad && (isShapeAligned || isFlatShape || ps.main.startSizeMultiplier >= 1.0f);

                bool isSurfaceAligned = isShapeAligned || isJumpPadRing;

                if (!isSurfaceAligned && pr.renderMode == ParticleSystemRenderMode.Mesh)
                {
                    if (!TryInstanceMeshParticles(tracked.id, pr, ps, mainCam, tintedMatId, matAlpha, actualAlive, state) &&
                        !BakeMeshParticleSystem(pr, ps, mainCam, tintedMatId, matAlpha, state))
                    {
                        int particlesToDraw = Math.Min(actualAlive, 4096);
                        renderedParticles += particlesToDraw;
                        var bucket = GetOrCreateBatchBucket(tintedMatId, particlesToDraw);
                        long appendStart = System.Diagnostics.Stopwatch.GetTimestamp();
                        AppendBillboardParticles(bucket, pr, ps, particlesToDraw, mainCam, camRight, camUp, camForward, matAlpha, isSurfaceAligned);
                        particleAppendMs += RemixProfiler.ElapsedMs(appendStart);
                        if (collectEmitters && emitterDetails.Count < 200)
                        {
                            emitterDetails.Add(new ParticleEmitterInfo
                            {
                                Name = pr.name,
                                Distance = dist,
                                ParticleCount = particlesToDraw,
                                Status = "Rendered (Batch)"
                            });
                        }
                    }
                    else
                    {
                        bakedMeshes++;
                        renderedParticles += actualAlive;
                        if (collectEmitters && emitterDetails.Count < 200)
                        {
                            emitterDetails.Add(new ParticleEmitterInfo
                            {
                                Name = pr.name,
                                Distance = dist,
                                ParticleCount = actualAlive,
                                Status = "Rendered (Bake)"
                            });
                        }
                    }
                }
                else
                {
                    int particlesToDraw = Math.Min(actualAlive, 4096);
                    renderedParticles += particlesToDraw;
                    var bucket = GetOrCreateBatchBucket(tintedMatId, particlesToDraw);
                    long appendStart = System.Diagnostics.Stopwatch.GetTimestamp();
                    AppendBillboardParticles(bucket, pr, ps, particlesToDraw, mainCam, camRight, camUp, camForward, matAlpha, isSurfaceAligned);
                    particleAppendMs += RemixProfiler.ElapsedMs(appendStart);
                    if (collectEmitters && emitterDetails.Count < 200)
                    {
                        emitterDetails.Add(new ParticleEmitterInfo
                        {
                            Name = pr.name,
                            Distance = dist,
                            ParticleCount = particlesToDraw,
                            Status = "Rendered (Batch)"
                        });
                    }
                }

                // Capture trails if enabled on this particle system
                var trails = ps.trails;
                if (trails.enabled)
                {
                    BakeParticleTrails(pr, ps, mainCam, quantizedColor, emIntensity, state);
                }
            }

            RemixProfiler.AddDetail(RemixProfiler.Detail.ParticleRead, readMs);
            RemixProfiler.AddDetail(RemixProfiler.Detail.ParticleBuild, RemixProfiler.ElapsedMs(loopStart) - readMs);
            RemixProfiler.AddDetail(RemixProfiler.Detail.ParticleAppend, particleAppendMs);
            RemixProfiler.AddParticles(renderedParticles, emittingCount, particleCurveCount);
            long emitStart = System.Diagnostics.Stopwatch.GetTimestamp();

            // Emit batched billboard meshes to state.skinned
            for (int b = 0; b < _activeParticleBatches.Count; b++)
            {
                var bucket = _activeParticleBatches[b];
                if (bucket.ParticleCount <= 0 || bucket.Vertices.Count == 0) continue;

                ulong remixMeshHash = 0x4000000000000000UL | ((ulong)(uint)bucket.SubBucketIndex << 32) | (ulong)(uint)bucket.MaterialId;

                int vertexCount = bucket.Vertices.Count, indexCount = bucket.Triangles.Count;
                var buffers = TakeParticleBuffers(remixMeshHash, frameCount, vertexCount, indexCount);
                if (buffers != null)
                {
                    bucket.Vertices.CopyTo(buffers.vertices);
                    bucket.Normals.CopyTo(buffers.normals);
                    bucket.UVs.CopyTo(buffers.uvs);
                    bucket.Colors.CopyTo(buffers.colors);
                    bucket.Triangles.CopyTo(buffers.triangles);
                }

                state.skinned.Add(new SkinnedMeshData
                {
                    source = DynamicSource.Billboard,
                    meshId = bucket.MaterialId,
                    remixMeshHash = remixMeshHash,
                    materialId = bucket.MaterialId,
                    vertices = buffers != null ? buffers.vertices : bucket.Vertices.ToArray(),
                    normals = buffers != null ? buffers.normals : bucket.Normals.ToArray(),
                    uvs = buffers != null ? buffers.uvs : bucket.UVs.ToArray(),
                    colors = buffers != null ? buffers.colors : bucket.Colors.ToArray(),
                    triangles = buffers != null ? buffers.triangles : bucket.Triangles.ToArray(),
                    usedVertexCount = buffers != null ? vertexCount : 0,
                    usedIndexCount = buffers != null ? indexCount : 0,
                    localToWorld = Matrix4x4.identity,
                    boneTransforms = null,
                    skinningData = null,
                    categoryFlags = (uint)RemixAPI.remixapi_InstanceCategoryBit.REMIXAPI_INSTANCE_CATEGORY_BIT_PARTICLE,
                    bakeFrame = frameCount
                });
            }

            RemixProfiler.AddDetail(RemixProfiler.Detail.ParticleEmit, RemixProfiler.ElapsedMs(emitStart));
            if (frameCount % 600 == 0)
                PruneParticleBuffers(frameCount);

            if (emitterDetails != null)
            {
                emitterDetails.Sort((a, b) => b.Distance.CompareTo(a.Distance));
            }

            var prevStats = _latestParticleStats;
            _latestParticleStats = new ParticleStatsSnapshot
            {
                TotalTrackedSystems = totalTracked,
                ActiveSystems = activeCount,
                InactiveSystems = inactiveCount,
                DistanceCulledSystems = distanceCulled,
                EmittingSystems = emittingCount,
                RenderedParticles = renderedParticles,
                CulledParticles = culledParticles,
                BatchedDrawCalls = _activeParticleBatches.Count,
                BakedMeshes = bakedMeshes,
                FurthestSystemDistance = furthestDist,
                FurthestSystemName = furthestName,
                TotalTrackedTrails = prevStats != null ? prevStats.TotalTrackedTrails : 0,
                ActiveTrails = prevStats != null ? prevStats.ActiveTrails : 0,
                DistanceCulledTrails = prevStats != null ? prevStats.DistanceCulledTrails : 0,
                Emitters = emitterDetails != null ? emitterDetails.ToArray() : (prevStats != null ? prevStats.Emitters : Array.Empty<ParticleEmitterInfo>())
            };
        }

        private ParticleBatchBucket GetOrCreateBatchBucket(int matId, int neededParticles)
        {
            if (_particleBatchesByMat.TryGetValue(matId, out var existing))
            {
                if (existing.Vertices.Count + neededParticles * 4 <= 65535)
                    return existing;
            }

            int subIndex = existing != null ? existing.SubBucketIndex + 1 : 0;

            ParticleBatchBucket bucket;
            if (_batchBucketPool.Count > 0)
            {
                bucket = _batchBucketPool[_batchBucketPool.Count - 1];
                _batchBucketPool.RemoveAt(_batchBucketPool.Count - 1);
                bucket.Reset(matId, subIndex);
            }
            else
            {
                bucket = new ParticleBatchBucket();
                bucket.Reset(matId, subIndex);
            }

            _particleBatchesByMat[matId] = bucket;
            _activeParticleBatches.Add(bucket);
            return bucket;
        }

        private static bool IsEmissiveParticleSystem(ParticleSystemRenderer pr, Material mat)
        {
            if (mat != null)
            {
                if (mat.HasProperty("EMISSIVE") && mat.GetFloat("EMISSIVE") > 0.5f)
                    return true;
                if (mat.IsKeywordEnabled("_EMISSION") && mat.HasProperty("_EmissionColor"))
                {
                    Color ec = mat.GetColor("_EmissionColor");
                    if (ec.r > 0.05f || ec.g > 0.05f || ec.b > 0.05f)
                        return true;
                }
                if (mat.HasProperty("_SrcBlend") && mat.HasProperty("_DstBlend"))
                {
                    int dst = mat.GetInt("_DstBlend");
                    if (dst == (int)UnityEngine.Rendering.BlendMode.One)
                        return true;
                }
                string shName = mat.shader != null ? mat.shader.name : "";
                if (shName.IndexOf("Additive", StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }

            string name = (pr != null ? pr.name : "") + " " + (mat != null ? mat.name : "");
            if (name.IndexOf("smoke", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("steam", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("fog", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("mist", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("dust", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("cloud", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("blood", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("gore", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("gib", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("water", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("splash", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("drip", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("rubble", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("debris", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("dirt", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return false;
            }

            if (name.IndexOf("spark", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("fire", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("flame", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("glow", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("laser", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("beam", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("charge", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("flash", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("explo", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("electr", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("lightn", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("plasma", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("orb", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("energy", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("ember", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("muzzle", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("portal", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("holy", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("hell", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("radian", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("additive", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            return false;
        }

        private double particleAppendMs;
        private int particleCurveCount;

        // Particle geometry goes to the render thread in reused buffers, not new arrays every
        // frame (megabytes per frame, and a garbage collection every few frames). Each mesh
        // has three sets; a set is refilled only when neither the frame the render thread is
        // drawing nor the last published one uses it.
        internal static volatile int RenderingFrame = -1;
        internal static volatile int PublishedFrame = -1;

        private sealed class ParticleBuffers
        {
            public Vector3[] vertices = Array.Empty<Vector3>();
            public Vector3[] normals = Array.Empty<Vector3>();
            public Vector2[] uvs = Array.Empty<Vector2>();
            public Color32[] colors = Array.Empty<Color32>();
            public int[] triangles = Array.Empty<int>();
            public int frame = int.MinValue;

            public void Ensure(int vertexCount, int indexCount)
            {
                if (vertices.Length < vertexCount)
                {
                    int capacity = Mathf.NextPowerOfTwo(vertexCount);
                    vertices = new Vector3[capacity];
                    normals = new Vector3[capacity];
                    uvs = new Vector2[capacity];
                    colors = new Color32[capacity];
                }
                if (triangles.Length < indexCount)
                    triangles = new int[Mathf.NextPowerOfTwo(indexCount)];
            }
        }

        private sealed class ParticleBufferRing
        {
            public readonly ParticleBuffers[] sets = { new ParticleBuffers(), new ParticleBuffers(), new ParticleBuffers() };
            public int lastUsedFrame;
        }

        private readonly Dictionary<ulong, ParticleBufferRing> particleBufferRings = new Dictionary<ulong, ParticleBufferRing>();
        private readonly List<ulong> staleParticleBufferKeys = new List<ulong>();

        private ParticleBuffers TakeParticleBuffers(ulong meshHash, int frameCount, int vertexCount, int indexCount)
        {
            if (!particleBufferRings.TryGetValue(meshHash, out var ring))
            {
                ring = new ParticleBufferRing();
                particleBufferRings[meshHash] = ring;
            }
            ring.lastUsedFrame = frameCount;
            int rendering = RenderingFrame, published = PublishedFrame;
            ParticleBuffers buffers = null;
            foreach (var set in ring.sets)
            {
                if (set.frame != frameCount && set.frame != rendering && set.frame != published)
                {
                    buffers = set;
                    break;
                }
            }
            if (buffers == null)
                return null; // cannot happen with three sets; the caller allocates instead
            buffers.frame = frameCount;
            buffers.Ensure(vertexCount, indexCount);
            return buffers;
        }

        private void PruneParticleBuffers(int frameCount)
        {
            staleParticleBufferKeys.Clear();
            foreach (var kvp in particleBufferRings)
            {
                if (frameCount - kvp.Value.lastUsedFrame > 600)
                    staleParticleBufferKeys.Add(kvp.Key);
            }
            foreach (var key in staleParticleBufferKeys)
                particleBufferRings.Remove(key);
        }

        private readonly List<Vector3> bakeVertices = new List<Vector3>();
        private readonly List<Vector3> bakeNormals = new List<Vector3>();
        private readonly List<Vector2> bakeUvs = new List<Vector2>();
        private readonly List<Color32> bakeColors = new List<Color32>();
        private readonly List<int> bakeTriangles = new List<int>();

        private void AppendBillboardParticles(
            ParticleBatchBucket bucket,
            ParticleSystemRenderer pr,
            ParticleSystem ps,
            int numParticlesAlive,
            Camera cam,
            Vector3 camRight,
            Vector3 camUp,
            Vector3 camForward,
            float matAlpha = 1f,
            bool isSurfaceAligned = false)
        {
            var mainModule = ps.main;
            bool isWorldSpace = mainModule.simulationSpace == ParticleSystemSimulationSpace.World;
            bool isCustomSpace = mainModule.simulationSpace == ParticleSystemSimulationSpace.Custom && mainModule.customSimulationSpace != null;
            Matrix4x4 sysTransform = isWorldSpace ? Matrix4x4.identity 
                : (isCustomSpace ? mainModule.customSimulationSpace.localToWorldMatrix : ps.transform.localToWorldMatrix);

            bool isStartSize3D = mainModule.startSize3D;
            bool hasSizeOverLifetime = ps.sizeOverLifetime.enabled;
            bool hasColorOverLifetime = ps.colorOverLifetime.enabled;
            if (hasSizeOverLifetime || hasColorOverLifetime)
                particleCurveCount += numParticlesAlive;

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
            bool isStandardView = !isSurfaceAligned && !isHorizontal && !isVertical && !isStretch && alignment == ParticleSystemRenderSpace.View;

            Vector3 upDir = pr.transform.up;
            if (upDir.sqrMagnitude < 0.001f) upDir = Vector3.up;

            Vector3 defaultNormal = -camForward;
            if (isSurfaceAligned) defaultNormal = upDir;
            else if (isHorizontal || alignment == ParticleSystemRenderSpace.World) defaultNormal = Vector3.up;
            else if (alignment == ParticleSystemRenderSpace.Local) defaultNormal = pr.transform.forward;

            int baseVert = bucket.Vertices.Count;

            for (int i = 0; i < numParticlesAlive; i++)
            {
                var p = _particleBuffer[i];
                Vector3 pos = isWorldSpace ? p.position : sysTransform.MultiplyPoint3x4(p.position);
                
                Vector3 size;
                if (hasSizeOverLifetime)
                    size = isStartSize3D ? p.GetCurrentSize3D(ps) : (Vector3.one * p.GetCurrentSize(ps));
                else
                    size = isStartSize3D ? p.startSize3D : (Vector3.one * p.startSize);

                Color32 col = hasColorOverLifetime ? p.GetCurrentColor(ps) : p.startColor;
                if (matAlpha < 0.999f)
                {
                    col.a = (byte)Mathf.Clamp(Mathf.RoundToInt(col.a * matAlpha), 0, 255);
                }

                float rot = p.rotation;
                float cosR = 1f;
                float sinR = 0f;
                if (rot != 0f)
                {
                    float rotRad = rot * Mathf.Deg2Rad;
                    cosR = Mathf.Cos(rotRad);
                    sinR = Mathf.Sin(rotRad);
                }

                Vector3 hR, hU;
                Vector3 rAxis = camRight, uAxis = camUp;
                if (isStandardView)
                {
                    if (rot == 0f)
                    {
                        hR = camRight * (size.x * 0.5f);
                        hU = camUp * (size.y * 0.5f);
                    }
                    else
                    {
                        rAxis = camRight * cosR + camUp * sinR;
                        uAxis = -camRight * sinR + camUp * cosR;
                        hR = rAxis * (size.x * 0.5f);
                        hU = uAxis * (size.y * 0.5f);
                    }
                }
                else if (isSurfaceAligned)
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
                    Vector3 rawVel = p.totalVelocity.sqrMagnitude > 0.0001f ? p.totalVelocity : p.velocity;
                    Vector3 worldVel = isWorldSpace ? rawVel : sysTransform.MultiplyVector(rawVel);
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
                    Vector3 rawVel = p.totalVelocity.sqrMagnitude > 0.0001f ? p.totalVelocity : p.velocity;
                    Vector3 worldVel = isWorldSpace ? rawVel : sysTransform.MultiplyVector(rawVel);
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
                    rAxis = camRight * cosR + camUp * sinR;
                    uAxis = -camRight * sinR + camUp * cosR;
                    hR = rAxis * (size.x * 0.5f);
                    hU = uAxis * (size.y * 0.5f);
                }

                Vector3 norm = defaultNormal;
                if (!isStandardView)
                {
                    norm = isSurfaceAligned ? upDir : Vector3.Cross(uAxis, rAxis).normalized;
                    if (norm.sqrMagnitude < 0.001f) norm = defaultNormal;
                }

                int vi = baseVert + i * 4;
                bucket.Vertices.Add(pos - hR - hU);
                bucket.Vertices.Add(pos + hR - hU);
                bucket.Vertices.Add(pos + hR + hU);
                bucket.Vertices.Add(pos - hR + hU);

                bucket.Normals.Add(norm);
                bucket.Normals.Add(norm);
                bucket.Normals.Add(norm);
                bucket.Normals.Add(norm);

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

                bucket.UVs.Add(new Vector2(uvX0, uvY0));
                bucket.UVs.Add(new Vector2(uvX1, uvY0));
                bucket.UVs.Add(new Vector2(uvX1, uvY1));
                bucket.UVs.Add(new Vector2(uvX0, uvY1));

                bucket.Colors.Add(col);
                bucket.Colors.Add(col);
                bucket.Colors.Add(col);
                bucket.Colors.Add(col);

                bucket.Triangles.Add(vi + 0);
                bucket.Triangles.Add(vi + 2);
                bucket.Triangles.Add(vi + 1);
                bucket.Triangles.Add(vi + 0);
                bucket.Triangles.Add(vi + 3);
                bucket.Triangles.Add(vi + 2);
            }

            bucket.ParticleCount += numParticlesAlive;
        }

        // Mesh-mode particle systems drawn as instances of their mesh, uploaded once, instead of
        // BakeMesh'd and re-uploaded every frame (a swarm of a hundred small meshes is tens of
        // thousands of vertices a frame). Unity documents no exact per-particle transform, so
        // each system's is picked from a few models by matching BakeMesh, and checked again
        // now and then; a system none matches keeps baking.
        private sealed class MeshParticleInstancing
        {
            public Mesh mesh;
            public bool rejected;
            public int model = -1;
            public int checkFrame;
            public Vector3[] vertices, normals;
            public Vector2[] uvs;
            public Color32[] meshColors; // null: white
            public int[] triangles;
            // (material, quantized particle color) -> mesh with that color baked into its vertices
            public readonly Dictionary<long, ulong> variantHashes = new Dictionary<long, ulong>();
            public readonly Dictionary<ulong, Color32[]> variantColors = new Dictionary<ulong, Color32[]>();
        }

        private struct MeshParticleFrame
        {
            public Quaternion systemRotation, cameraRotation;
            public Vector3 lossyScale, pivot;
        }

        private readonly Dictionary<int, MeshParticleInstancing> meshParticleInstancing = new Dictionary<int, MeshParticleInstancing>();
        private readonly List<long> meshParticleVariantKeys = new List<long>();
        private readonly List<Vector3> meshParticleSizes = new List<Vector3>();
        private const int MaxInstancedMeshParticles = 1024;
        private const int MeshParticleCheckInterval = 300;
        private const int MeshParticleCheckParticles = 16;
        private const int MeshParticleVariantLimit = 64;
        // Rotation frames x Euler orders x scales.
        private const int MeshParticleBases = 5, MeshParticleOrders = 3, MeshParticleScales = 2;
        private const int MeshParticleModels = MeshParticleBases * MeshParticleOrders * MeshParticleScales;

        private static Matrix4x4 MeshParticleMatrix(int model, ref ParticleSystem.Particle particle, Vector3 size, in MeshParticleFrame frame)
        {
            int scaleMode = model % MeshParticleScales;
            int order = (model / MeshParticleScales) % MeshParticleOrders;
            int frameMode = model / (MeshParticleScales * MeshParticleOrders);

            Vector3 e = particle.rotation3D;
            Quaternion rx = Quaternion.AngleAxis(e.x, Vector3.right);
            Quaternion ry = Quaternion.AngleAxis(e.y, Vector3.up);
            Quaternion rz = Quaternion.AngleAxis(e.z, Vector3.forward);
            Quaternion rotation = order == 0 ? ry * rx * rz      // Quaternion.Euler: Z, then X, then Y
                                : order == 1 ? rz * ry * rx      // X, then Y, then Z
                                : rx * ry * rz;                  // Z, then Y, then X
            switch (frameMode)
            {
                case 1: rotation = frame.systemRotation * rotation; break;
                case 2: rotation = Quaternion.Inverse(frame.systemRotation) * rotation; break;
                case 3: rotation = frame.cameraRotation * rotation; break;
                case 4: rotation = frame.cameraRotation * Quaternion.AngleAxis(180f, Vector3.up) * rotation; break;
            }
            if (scaleMode == 1)
                size = Vector3.Scale(size, frame.lossyScale);
            return Matrix4x4.TRS(particle.position, rotation, size) * Matrix4x4.Translate(frame.pivot);
        }

        private bool PrepareMeshParticleInstancing(MeshParticleInstancing inst, ParticleSystemRenderer pr, Mesh mesh, out string reason)
        {
            reason = null;
            inst.mesh = mesh;
            inst.model = -1;
            inst.variantHashes.Clear();
            inst.variantColors.Clear();
            if (mesh.isReadable)
            {
                inst.vertices = mesh.vertices;
                var normals = mesh.normals;
                inst.normals = normals != null && normals.Length == inst.vertices.Length ? normals : null;
                var uvs = mesh.uv;
                inst.uvs = uvs != null && uvs.Length == inst.vertices.Length ? uvs : null;
                var colors = mesh.colors32;
                inst.meshColors = colors != null && colors.Length == inst.vertices.Length ? colors : null;
                inst.triangles = mesh.triangles;
            }
            else
            {
                if (!NativeMeshReader.ReadMeshFromGPU(mesh, out var positions, out var normals, out var uvs, out var subMeshIndices))
                {
                    reason = "GPU read of its non-readable mesh failed";
                    return false;
                }
                inst.vertices = positions;
                inst.normals = normals != null && normals.Length == positions.Length ? normals : null;
                inst.uvs = uvs != null && uvs.Length == positions.Length ? uvs : null;
                inst.meshColors = null;
                var all = new List<int>();
                foreach (var sub in subMeshIndices)
                    if (sub != null) all.AddRange(sub);
                inst.triangles = all.ToArray();
            }
            if (inst.vertices == null || inst.vertices.Length == 0 || inst.triangles == null || inst.triangles.Length < 3)
            {
                reason = "empty mesh";
                return false;
            }
            return true;
        }

        // Compares the models with BakeMesh on the first particles: the current one first, then
        // all of them when it no longer matches.
        private bool CheckMeshParticleModel(MeshParticleInstancing inst, ParticleSystemRenderer pr, ParticleSystem ps, Camera cam,
                                            int alive, in MeshParticleFrame frame, out string reason)
        {
            reason = null;
            if (_reusableParticleMesh == null)
                _reusableParticleMesh = new Mesh { name = "ReusableParticleBakeMesh" };
            _reusableParticleMesh.Clear();
#pragma warning disable CS0618
            if (cam != null) pr.BakeMesh(_reusableParticleMesh, cam, false);
            else pr.BakeMesh(_reusableParticleMesh, false);
#pragma warning restore CS0618
            _reusableParticleMesh.GetVertices(bakeVertices);
            int meshVertices = inst.vertices.Length;
            if (bakeVertices.Count != alive * meshVertices)
            {
                reason = $"BakeMesh made {bakeVertices.Count} vertices, not {alive} x {meshVertices}";
                return false;
            }
            int checkCount = Math.Min(alive, MeshParticleCheckParticles);
            meshParticleSizes.Clear();
            for (int i = 0; i < checkCount; i++)
                meshParticleSizes.Add(_particleBuffer[i].GetCurrentSize3D(ps));
            float tolerance = Mathf.Max(1e-3f * _reusableParticleMesh.bounds.size.magnitude, 1e-4f);
            float toleranceSqr = tolerance * tolerance;

            int best = -1;
            float bestError = float.PositiveInfinity;
            for (int pass = 0; pass < 2 && best < 0; pass++)
            {
                for (int model = 0; model < MeshParticleModels; model++)
                {
                    if (pass == 0 ? model != inst.model : model == inst.model) continue;
                    float worst = 0f;
                    for (int i = 0; i < checkCount && worst <= toleranceSqr; i++)
                    {
                        var m = MeshParticleMatrix(model, ref _particleBuffer[i], meshParticleSizes[i], frame);
                        int baseVertex = i * meshVertices;
                        for (int v = 0; v < meshVertices; v++)
                        {
                            float d = (m.MultiplyPoint3x4(inst.vertices[v]) - bakeVertices[baseVertex + v]).sqrMagnitude;
                            if (d > worst) { worst = d; if (worst > toleranceSqr) break; }
                        }
                    }
                    if (worst < bestError) bestError = worst;
                    if (worst <= toleranceSqr) { best = model; break; }
                }
            }
            if (best < 0)
            {
                reason = $"no transform model matches BakeMesh (closest {Mathf.Sqrt(bestError):G3} > {tolerance:G3})";
                return false;
            }
            if (best != inst.model && inst.model >= 0)
                logger.LogInfo($"[MeshParticles] '{pr.name}' transform model {inst.model} -> {best}");
            inst.model = best;
            return true;
        }

        private bool TryInstanceMeshParticles(int systemId, ParticleSystemRenderer pr, ParticleSystem ps, Camera cam,
                                              int matId, float matAlpha, int alive, FrameState state)
        {
            if (alive <= 0 || alive > MaxInstancedMeshParticles)
                return false;
            if (!meshParticleInstancing.TryGetValue(systemId, out var inst))
            {
                inst = new MeshParticleInstancing();
                meshParticleInstancing[systemId] = inst;
            }
            if (inst.rejected)
                return false;

            var main = ps.main;
            var space = main.simulationSpace;
            Mesh mesh = pr.mesh;
            string reason = null;
            if (space == ParticleSystemSimulationSpace.Custom) reason = "custom simulation space";
            else if (mesh == null || pr.meshCount != 1) reason = "not exactly one mesh";
            else if (pr.flip != Vector3.zero) reason = "flipped particles";
            else if (!ReferenceEquals(inst.mesh, mesh) && !PrepareMeshParticleInstancing(inst, pr, mesh, out reason)) { }
            if (reason != null)
            {
                inst.rejected = true;
                logger.LogInfo($"[MeshParticles] '{pr.name}' keeps BakeMesh: {reason}");
                return false;
            }

            var tr = pr.transform;
            Quaternion systemRotation = tr.rotation;
            Quaternion cameraRotation = cam != null ? cam.transform.rotation : Quaternion.identity;
            if (space == ParticleSystemSimulationSpace.Local)
                cameraRotation = Quaternion.Inverse(systemRotation) * cameraRotation;
            var frame = new MeshParticleFrame
            {
                systemRotation = systemRotation,
                cameraRotation = cameraRotation,
                lossyScale = tr.lossyScale,
                pivot = pr.pivot
            };

            int frameCount = state.frameCount;
            if (inst.model < 0 || frameCount >= inst.checkFrame)
            {
                bool first = inst.model < 0;
                if (!CheckMeshParticleModel(inst, pr, ps, cam, alive, frame, out reason))
                {
                    if (first || !reason.StartsWith("BakeMesh made"))
                    {
                        inst.rejected = true;
                        logger.LogInfo($"[MeshParticles] '{pr.name}' keeps BakeMesh: {reason}");
                    }
                    return false;
                }
                inst.checkFrame = frameCount + MeshParticleCheckInterval;
                if (first)
                    logger.LogInfo($"[MeshParticles] '{pr.name}' instanced: {alive} particles of '{inst.mesh.name}' ({inst.vertices.Length} verts), transform model {inst.model}");
            }

            // Particle colors, quantized like the billboard batches: one mesh per color in use.
            meshParticleVariantKeys.Clear();
            for (int i = 0; i < alive; i++)
            {
                Color32 c = _particleBuffer[i].GetCurrentColor(ps);
                int a = matAlpha < 0.999f ? Mathf.RoundToInt(c.a * matAlpha) : c.a;
                int key = ((c.r * 15 + 127) / 255 << 12) | ((c.g * 15 + 127) / 255 << 8) | ((c.b * 15 + 127) / 255 << 4) | ((a * 15 + 127) / 255);
                long variantKey = ((long)matId << 32) | (uint)key;
                if (!inst.variantHashes.ContainsKey(variantKey))
                {
                    if (inst.variantHashes.Count >= MeshParticleVariantLimit)
                    {
                        inst.rejected = true;
                        logger.LogInfo($"[MeshParticles] '{pr.name}' keeps BakeMesh: more than {MeshParticleVariantLimit} particle colors");
                        return false;
                    }
                    ulong hash = 14695981039346656037UL;
                    hash = (hash ^ (uint)inst.mesh.GetInstanceID()) * 1099511628211UL;
                    hash = (hash ^ (uint)systemId) * 1099511628211UL;
                    hash = (hash ^ (ulong)variantKey) * 1099511628211UL;
                    hash = (hash & 0x1FFFFFFFFFFFFFFFUL) | 0x6000000000000000UL;
                    var tint = new Color32((byte)((key >> 12 & 15) * 17), (byte)((key >> 8 & 15) * 17), (byte)((key >> 4 & 15) * 17), (byte)((key & 15) * 17));
                    var colors = new Color32[inst.vertices.Length];
                    for (int v = 0; v < colors.Length; v++)
                    {
                        Color32 m = inst.meshColors != null ? inst.meshColors[v] : new Color32(255, 255, 255, 255);
                        colors[v] = new Color32((byte)(m.r * tint.r / 255), (byte)(m.g * tint.g / 255), (byte)(m.b * tint.b / 255), (byte)(m.a * tint.a / 255));
                    }
                    inst.variantHashes[variantKey] = hash;
                    inst.variantColors[hash] = colors;
                }
                meshParticleVariantKeys.Add(variantKey);
            }

            Matrix4x4 simToWorld = space == ParticleSystemSimulationSpace.World ? Matrix4x4.identity : tr.localToWorldMatrix;
            for (int i = 0; i < alive; i++)
            {
                ref var particle = ref _particleBuffer[i];
                var m = MeshParticleMatrix(inst.model, ref particle, particle.GetCurrentSize3D(ps), frame);
                ulong hash = inst.variantHashes[meshParticleVariantKeys[i]];
                state.skinned.Add(new SkinnedMeshData
                {
                    source = DynamicSource.MeshInstanced,
                    meshId = HashCombine(systemId, (int)particle.randomSeed),
                    remixMeshHash = hash,
                    materialId = matId,
                    vertices = inst.vertices,
                    normals = inst.normals,
                    uvs = inst.uvs,
                    colors = inst.variantColors[hash],
                    triangles = inst.triangles,
                    localToWorld = simToWorld * m,
                    boneTransforms = null,
                    skinningData = null,
                    categoryFlags = (uint)RemixAPI.remixapi_InstanceCategoryBit.REMIXAPI_INSTANCE_CATEGORY_BIT_PARTICLE,
                    bakeFrame = 1 // the geometry never changes: uploaded once
                });
            }
            return true;
        }

        private bool BakeMeshParticleSystem(ParticleSystemRenderer pr, ParticleSystem ps, Camera cam, int matId, float matAlpha, FrameState state)
        {
            if (_reusableParticleMesh == null)
            {
                _reusableParticleMesh = new Mesh();
                _reusableParticleMesh.name = "ReusableParticleBakeMesh";
            }

            try
            {
                _reusableParticleMesh.Clear();
                // Pass useTransform=false so local-space particles stay in the emitter's local space
                // and receive the full TRS via localToWorld (useTransform=true only applies R+S without translation).
#pragma warning disable CS0618
                if (cam != null)
                {
                    pr.BakeMesh(_reusableParticleMesh, cam, false);
                }
                else
                {
                    pr.BakeMesh(_reusableParticleMesh, false);
                }
#pragma warning restore CS0618

                var mesh = _reusableParticleMesh;
                int vertexCount = mesh.vertexCount;
                if (vertexCount > 0 && mesh.subMeshCount > 0)
                {
                    ulong remixMeshHash = (ulong)(uint)pr.GetInstanceID() | 0x4000000000000000UL;
                    bool isWorldSpace = ps != null && ps.main.simulationSpace == ParticleSystemSimulationSpace.World;
                    Matrix4x4 particleLocalToWorld = isWorldSpace ? Matrix4x4.identity : pr.transform.localToWorldMatrix;

                    mesh.GetVertices(bakeVertices);
                    mesh.GetNormals(bakeNormals);
                    mesh.GetUVs(0, bakeUvs);
                    mesh.GetColors(bakeColors);
                    if (mesh.subMeshCount == 1)
                        mesh.GetTriangles(bakeTriangles, 0);
                    else
                    {
                        bakeTriangles.Clear();
                        bakeTriangles.AddRange(mesh.triangles);
                    }
                    int indexCount = bakeTriangles.Count;
                    if (indexCount == 0)
                        return false;
                    bool hasNormals = bakeNormals.Count == vertexCount;
                    bool hasUvs = bakeUvs.Count == vertexCount;
                    bool hasColors = bakeColors.Count == vertexCount;

                    var buffers = TakeParticleBuffers(remixMeshHash, state.frameCount, vertexCount, indexCount);
                    Vector3[] vertices, normals = null;
                    Vector2[] uvs = null;
                    Color32[] colors = null;
                    int[] triangles;
                    if (buffers != null)
                    {
                        bakeVertices.CopyTo(buffers.vertices);
                        bakeTriangles.CopyTo(buffers.triangles);
                        vertices = buffers.vertices;
                        triangles = buffers.triangles;
                        if (hasNormals) { bakeNormals.CopyTo(buffers.normals); normals = buffers.normals; }
                        if (hasUvs) { bakeUvs.CopyTo(buffers.uvs); uvs = buffers.uvs; }
                        if (hasColors) { bakeColors.CopyTo(buffers.colors); colors = buffers.colors; }
                    }
                    else
                    {
                        vertices = bakeVertices.ToArray();
                        triangles = bakeTriangles.ToArray();
                        if (hasNormals) normals = bakeNormals.ToArray();
                        if (hasUvs) uvs = bakeUvs.ToArray();
                        if (hasColors) colors = bakeColors.ToArray();
                    }

                    if (matAlpha < 0.999f && colors != null)
                    {
                        for (int ci = 0; ci < vertexCount; ci++)
                        {
                            colors[ci].a = (byte)Mathf.Clamp(Mathf.RoundToInt(colors[ci].a * matAlpha), 0, 255);
                        }
                    }

                    state.skinned.Add(new SkinnedMeshData
                    {
                        source = DynamicSource.MeshBaked,
                        meshId = pr.GetInstanceID(),
                        remixMeshHash = remixMeshHash,
                        materialId = matId,
                        vertices = vertices,
                        normals = normals,
                        uvs = uvs,
                        colors = colors,
                        triangles = triangles,
                        usedVertexCount = buffers != null ? vertexCount : 0,
                        usedIndexCount = buffers != null ? indexCount : 0,
                        localToWorld = particleLocalToWorld,
                        boneTransforms = null,
                        skinningData = null,
                        categoryFlags = (uint)RemixAPI.remixapi_InstanceCategoryBit.REMIXAPI_INSTANCE_CATEGORY_BIT_PARTICLE,
                        bakeFrame = state.frameCount
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
                        mpbColor: trailColor,
                        suppressLightGeneration: true,
                        renderer: pr
                    );

                    ulong trailHash = (ulong)(uint)pr.GetInstanceID() | 0x4800000000000000UL;

                    // Use the full local-to-world matrix so position is correctly applied.
                    // For world-space simulated particles the transform is usually identity/origin
                    // and this is still correct. For local-space (e.g. attached-gib) particles
                    // this is essential so the trails follow the gib in world space.
                    Matrix4x4 trailLocalToWorld = pr.transform.localToWorldMatrix;

                    state.skinned.Add(new SkinnedMeshData
                    {
                        source = DynamicSource.ParticleTrail,
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
                        categoryFlags = (uint)RemixAPI.remixapi_InstanceCategoryBit.REMIXAPI_INSTANCE_CATEGORY_BIT_PARTICLE,
                        bakeFrame = state.frameCount
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

            // Throttle discovery of newly spawned TrailRenderers to avoid full-scene sweeps every frame
            if (frameCount % 4 == 0)
            {
                var activeTrails = UnityCompat.FindActiveSceneComponents<TrailRenderer>();
                for (int a = 0; a < activeTrails.Length; a++)
                {
                    var ar = activeTrails[a];
                    if (ar != null && trackedTrailRendererIds.Add(ar.GetInstanceID()))
                    {
                        trackedTrailRenderers.Add(new TrackedTrailRenderer
                        {
                            renderer = ar,
                            id = ar.GetInstanceID()
                        });
                    }
                }
            }

            // Always check camera hierarchy every frame for instantaneous first-person / viewmodel trails
            if (mainCam != null)
            {
                var camTrails = mainCam.GetComponentsInChildren<TrailRenderer>(false);
                for (int a = 0; a < camTrails.Length; a++)
                {
                    var ar = camTrails[a];
                    if (ar != null && trackedTrailRendererIds.Add(ar.GetInstanceID()))
                    {
                        trackedTrailRenderers.Add(new TrackedTrailRenderer
                        {
                            renderer = ar,
                            id = ar.GetInstanceID()
                        });
                    }
                }
            }

            // Periodically prune destroyed trail renderers
            if (frameCount % 180 == 0)
            {
                for (int d = trackedTrailRenderers.Count - 1; d >= 0; d--)
                {
                    var t = trackedTrailRenderers[d];
                    if (t.renderer == null)
                    {
                        trackedTrailRendererIds.Remove(t.id);
                        trackedTrailRenderers.RemoveAt(d);
                    }
                }
            }

            if (trackedTrailRenderers.Count == 0)
                return;

            if (_reusableTrailRendererMesh == null)
            {
                _reusableTrailRendererMesh = new Mesh { name = "TrailRendererBakeMesh" };
            }

            Vector3 camPos = mainCam.transform.position;
            float maxParticleDist = (configParticleMaxDistance != null && configParticleMaxDistance.Value > 0f)
                ? configParticleMaxDistance.Value
                : 60f;
            float maxParticleDistSqr = maxParticleDist * maxParticleDist;

            int trackedTrails = trackedTrailRenderers.Count;
            int renderingTrails = 0;
            int culledTrails = 0;

            for (int i = 0; i < trackedTrailRenderers.Count; i++)
            {
                var t = trackedTrailRenderers[i];
                var tr = t.renderer;
                if (tr == null || !tr.enabled || !tr.gameObject.activeInHierarchy)
                    continue;

                if (tr.positionCount < 2)
                    continue;

                if (configEnableParticleDistanceCulling != null && configEnableParticleDistanceCulling.Value && (tr.transform.position - camPos).sqrMagnitude > maxParticleDistSqr)
                {
                    culledTrails++;
                    continue;
                }

                renderingTrails++;

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

                    // Check material tint using cache
                    int matInstId = mat.GetInstanceID();
                    if (!_materialTintCache.TryGetValue(matInstId, out Color matTint))
                    {
                        matTint = Color.white;
                        if (mat.HasProperty("_TintColor"))
                            matTint = mat.GetColor("_TintColor");
                        else if (mat.HasProperty("_Color"))
                            matTint = mat.GetColor("_Color");
                        else if (mat.HasProperty("_BaseColor"))
                            matTint = mat.GetColor("_BaseColor");
                        _materialTintCache[matInstId] = matTint;
                    }

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

                    if (_capturedParticleMatIds.Add(trailMatId))
                    {
                        materialManager.CaptureMaterialTextures(
                            mat,
                            trailMatId,
                            mpbEmissiveColor: quantizedColor,
                            mpbEmissiveIntensity: emIntensity,
                            mpbColor: quantizedColor,
                            suppressLightGeneration: true,
                            renderer: tr
                        );
                    }

                    ulong remixMeshHash = (ulong)(uint)tr.GetInstanceID() | 0x4C00000000000000UL;

                    state.skinned.Add(new SkinnedMeshData
                    {
                        source = DynamicSource.Trail,
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
                        categoryFlags = (uint)RemixAPI.remixapi_InstanceCategoryBit.REMIXAPI_INSTANCE_CATEGORY_BIT_PARTICLE,
                        bakeFrame = frameCount
                    });
                }
                catch (Exception ex)
                {
                    if (configDebugLogInterval.Value > 0)
                        logger.LogWarning($"CaptureTrailRenderers failed on '{tr.name}': {ex.Message}");
                }
            }

            var snap = _latestParticleStats;
            if (snap != null)
            {
                snap.TotalTrackedTrails = trackedTrails;
                snap.ActiveTrails = renderingTrails;
                snap.DistanceCulledTrails = culledTrails;
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
        /// Extract bind-pose geometry and bone weights from a SkinnedMeshRenderer's sharedMesh,
        /// without blendshapes. Once per sharedMesh (skinningBases). Returns null when the mesh
        /// can only be baked (BakeMesh fallback).
        /// </summary>
        private CachedSkinningData BuildSkinningBase(SkinnedMeshRenderer skinned)
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
                    return null;
                }
                
                int boneCount = bindPoses.Length;
                if (boneCount > MAX_REMIX_BONES)
                {
                    logger.LogInfo($"[Skinning] '{mesh.name}' has {boneCount} bones (>{MAX_REMIX_BONES}) — BakeMesh fallback");
                    return null;
                }

                // Read/Write disabled (the import default): the CPU copy is gone, read the GPU's.
                if (!mesh.isReadable)
                    return BuildSkinningBaseFromGpu(skinned, mesh, bindPoses, boneCount);
                
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
                            return null;
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
                        return null;
                    }
                }
                
                // Extract bind-pose geometry
                Vector3[] bindVerts, bindNorms;
                Vector2[] uvs;
                Color32[] colors = null;
                int[] triangles;
                int[][] perSubTrisLocal = null;
                
                // Blendshapes are applied to these bind-pose vertices per weight set (CacheSkinningData).
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
                    perSubTrisLocal = perSubTris;
                }
                else
                {
                    logger.LogInfo($"[Skinning] '{mesh.name}' is non-readable — BakeMesh fallback");
                    return null;
                }
                
                if (bindVerts == null || bindVerts.Length == 0 || triangles.Length == 0)
                {
                    logger.LogInfo($"[Skinning] '{mesh.name}' empty geometry — BakeMesh fallback");
                    return null;
                }
                
                if (bindNorms == null || bindNorms.Length != bindVerts.Length)
                {
                    bindNorms = new Vector3[bindVerts.Length];
                    for (int n = 0; n < bindNorms.Length; n++)
                        bindNorms[n] = Vector3.up;
                }
                if (uvs == null || uvs.Length != bindVerts.Length)
                    uvs = new Vector2[bindVerts.Length];
                
                var result = new CachedSkinningData
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
                
                logger.LogInfo($"[Skinning] '{mesh.name}' cached: {vertexCount} verts, {triangles.Length / 3} tris, {boneCount} bones, {mesh.blendShapeCount} blendshapes, readable={mesh.isReadable}");
                return result;
            }
            catch (Exception ex)
            {
                logger.LogWarning($"[Skinning] '{mesh.name}' extraction failed: {ex.Message} — BakeMesh fallback");
                return null;
            }
        }
        
        /// <summary>
        /// GPU skinning data of a non-readable mesh, read back from its GPU buffers once
        /// (SkinnedMeshGpuReader), then checked against Unity's own BakeMesh of the renderer's
        /// current pose: a layout this does not understand bakes instead of rendering wrong.
        /// </summary>
        private CachedSkinningData BuildSkinningBaseFromGpu(SkinnedMeshRenderer skinned, Mesh mesh, Matrix4x4[] bindPoses, int boneCount)
        {
            SkinnedMeshGpuReader.Data gpu;
            string reason;
            try
            {
                if (!SkinnedMeshGpuReader.TryRead(mesh, BONES_PER_VERTEX, out gpu, out reason))
                {
                    logger.LogInfo($"[Skinning] '{mesh.name}' is non-readable, GPU read failed: {reason} — BakeMesh fallback");
                    return null;
                }
            }
            catch (Exception ex)
            {
                logger.LogInfo($"[Skinning] '{mesh.name}' is non-readable, GPU read unavailable: {ex.Message} — BakeMesh fallback");
                return null;
            }

            var allTris = new List<int>();
            foreach (var sub in gpu.submeshTriangles)
                allTris.AddRange(sub);
            if (gpu.positions.Length == 0 || allTris.Count == 0)
            {
                logger.LogInfo($"[Skinning] '{mesh.name}' empty geometry on the GPU — BakeMesh fallback");
                return null;
            }
            var normals = gpu.normals ?? NativeMeshReader.ComputeFaceNormals(gpu.positions, gpu.submeshTriangles);
            var result = new CachedSkinningData
            {
                bindVertices = gpu.positions,
                bindNormals = normals,
                uvs = gpu.uvs ?? new Vector2[gpu.positions.Length],
                colors = gpu.colors,
                triangles = allTris.ToArray(),
                submeshTriangles = gpu.submeshTriangles,
                blendWeights = gpu.weights,
                blendIndices = gpu.indices,
                bonesPerVertex = BONES_PER_VERTEX,
                boneCount = boneCount,
                bindPoses = bindPoses,
                meshCreated = false,
                sparseBlendShapes = gpu.blendShapes
            };
            if (!ValidateGpuSkinning(skinned, mesh, result, out float error, out float tolerance))
            {
                logger.LogInfo($"[Skinning] '{mesh.name}' GPU skinning data differs from BakeMesh (max error {error:G3} > {tolerance:G3}) — BakeMesh fallback");
                return null;
            }
            logger.LogInfo($"[Skinning] '{mesh.name}' cached from the GPU (non-readable): {gpu.positions.Length} verts, {allTris.Count / 3} tris, {boneCount} bones, {mesh.blendShapeCount} blendshapes; matches BakeMesh within {error:G3}");
            return result;
        }

        private Mesh skinningValidationMesh;

        // Skins the data on the CPU as the renderer will on the GPU (bones relative to the
        // unscaled renderer transform, blendshapes at the current weights) and compares with
        // BakeMesh of the same pose. False also when the data cannot be compared.
        private bool ValidateGpuSkinning(SkinnedMeshRenderer skinned, Mesh mesh, CachedSkinningData data, out float error, out float tolerance)
        {
            error = float.PositiveInfinity;
            tolerance = 0f;
            if (skinningValidationMesh == null)
                skinningValidationMesh = new Mesh { name = "SkinningValidationBake" };
            skinned.BakeMesh(skinningValidationMesh);
            var baked = skinningValidationMesh.vertices;
            int count = data.bindVertices.Length;
            if (baked == null || baked.Length != count)
                return false;

            var vertices = (Vector3[])data.bindVertices.Clone();
            var normals = (Vector3[])data.bindNormals.Clone();
            if (mesh.blendShapeCount > 0)
                ApplyBlendShapes(mesh, skinned, vertices, normals, data.sparseBlendShapes);

            var tr = skinned.transform;
            var ls = tr.lossyScale;
            var inverse = Matrix4x4.TRS(tr.position, tr.rotation,
                new Vector3(Mathf.Sign(ls.x), Mathf.Sign(ls.y), Mathf.Sign(ls.z))).inverse;
            var bones = skinned.bones;
            var matrices = new Matrix4x4[data.boneCount];
            for (int b = 0; b < matrices.Length; b++)
            {
                matrices[b] = bones != null && b < bones.Length && bones[b] != null
                    ? inverse * bones[b].localToWorldMatrix * data.bindPoses[b]
                    : Matrix4x4.identity;
            }

            float worst = 0f;
            for (int v = 0; v < count; v++)
            {
                Vector3 p = Vector3.zero;
                int baseIndex = v * data.bonesPerVertex;
                for (int k = 0; k < data.bonesPerVertex; k++)
                {
                    float w = data.blendWeights[baseIndex + k];
                    if (w == 0f) continue;
                    uint bone = data.blendIndices[baseIndex + k];
                    if (bone >= (uint)matrices.Length)
                        return false;
                    p += matrices[bone].MultiplyPoint3x4(vertices[v]) * w;
                }
                worst = Mathf.Max(worst, (p - baked[v]).sqrMagnitude);
            }
            error = Mathf.Sqrt(worst);
            tolerance = Mathf.Max(1e-3f * skinningValidationMesh.bounds.size.magnitude, 1e-4f);
            return error <= tolerance;
        }

        /// <summary>
        /// GPU skinning data for one sharedMesh and blendshape weight set (cachedSkinning). The
        /// mesh's bones, weights and bind pose are read once (skinningBases); a weight set gets the
        /// bind pose with its blendshapes applied, so characters with blendshapes are skinned on the
        /// GPU and only re-blended when their weights change.
        /// </summary>
        private void CacheSkinningData(SkinnedMeshRenderer skinned, long skinCacheKey)
        {
            var mesh = skinned.sharedMesh;
            int meshId = mesh.GetInstanceID();
            if (!skinningBases.TryGetValue(meshId, out var baseData))
            {
                baseData = BuildSkinningBase(skinned);
                skinningBases[meshId] = baseData;
            }
            if (baseData == null || mesh.blendShapeCount == 0)
            {
                cachedSkinning[skinCacheKey] = baseData;
                return;
            }
            try
            {
                var vertices = (Vector3[])baseData.bindVertices.Clone();
                var normals = (Vector3[])baseData.bindNormals.Clone();
                ApplyBlendShapes(mesh, skinned, vertices, normals, baseData.sparseBlendShapes);
                cachedSkinning[skinCacheKey] = new CachedSkinningData
                {
                    bindVertices = vertices,
                    bindNormals = normals,
                    uvs = baseData.uvs,
                    colors = baseData.colors,
                    triangles = baseData.triangles,
                    submeshTriangles = baseData.submeshTriangles,
                    blendWeights = baseData.blendWeights,
                    blendIndices = baseData.blendIndices,
                    bonesPerVertex = baseData.bonesPerVertex,
                    boneCount = baseData.boneCount,
                    bindPoses = baseData.bindPoses,
                    meshCreated = false,
                    sparseBlendShapes = baseData.sparseBlendShapes
                };
            }
            catch (Exception ex)
            {
                logger.LogWarning($"[Skinning] '{mesh.name}' blendshapes failed: {ex.Message} — BakeMesh fallback");
                cachedSkinning[skinCacheKey] = null;
            }
        }

        private Vector3[] blendDeltaVertices, blendDeltaNormals;

        // Blendshapes as Unity applies them before skinning: a weight up to the first frame scales
        // that frame, between frames it interpolates, past the last it extrapolates the last two.
        private void ApplyBlendShapes(Mesh mesh, SkinnedMeshRenderer skinned, Vector3[] vertices, Vector3[] normals,
                                      SkinnedMeshGpuReader.SparseShape[] sparse = null)
        {
            int count = vertices.Length;
            if (sparse != null)
            {
                // Read from the GPU (non-readable mesh): single-frame shapes, only the vertices they move.
                bool moved = false;
                for (int shape = 0; shape < sparse.Length; shape++)
                {
                    float weight = skinned.GetBlendShapeWeight(shape);
                    var deltas = sparse[shape];
                    if (weight == 0f || deltas == null || deltas.frameWeight == 0f) continue;
                    float factor = weight / deltas.frameWeight;
                    for (int i = 0; i < deltas.indices.Length; i++)
                    {
                        int v = deltas.indices[i];
                        vertices[v] += deltas.positions[i] * factor;
                        normals[v] += deltas.normals[i] * factor;
                    }
                    moved = true;
                }
                if (moved)
                {
                    for (int v = 0; v < count; v++)
                    {
                        var n = normals[v];
                        float lengthSqr = n.sqrMagnitude;
                        normals[v] = lengthSqr > 1e-12f ? n / Mathf.Sqrt(lengthSqr) : Vector3.up;
                    }
                }
                return;
            }
            if (blendDeltaVertices == null || blendDeltaVertices.Length != count)
            {
                blendDeltaVertices = new Vector3[count];
                blendDeltaNormals = new Vector3[count];
            }
            bool touched = false;
            void Add(int shape, int frame, float factor)
            {
                if (factor == 0f) return;
                mesh.GetBlendShapeFrameVertices(shape, frame, blendDeltaVertices, blendDeltaNormals, null);
                for (int v = 0; v < count; v++)
                {
                    vertices[v] += blendDeltaVertices[v] * factor;
                    normals[v] += blendDeltaNormals[v] * factor;
                }
                touched = true;
            }
            int shapes = mesh.blendShapeCount;
            for (int shape = 0; shape < shapes; shape++)
            {
                float weight = skinned.GetBlendShapeWeight(shape);
                if (weight == 0f) continue;
                int frames = mesh.GetBlendShapeFrameCount(shape);
                if (frames <= 0) continue;
                float first = mesh.GetBlendShapeFrameWeight(shape, 0);
                if (frames == 1 || weight <= first)
                {
                    if (first != 0f) Add(shape, 0, weight / first);
                    continue;
                }
                int upper = 1;
                while (upper < frames - 1 && mesh.GetBlendShapeFrameWeight(shape, upper) < weight)
                    upper++;
                float low = mesh.GetBlendShapeFrameWeight(shape, upper - 1);
                float high = mesh.GetBlendShapeFrameWeight(shape, upper);
                float t = high != low ? (weight - low) / (high - low) : 1f;
                Add(shape, upper - 1, 1f - t);
                Add(shape, upper, t);
            }
            if (touched)
            {
                for (int v = 0; v < count; v++)
                {
                    var n = normals[v];
                    float lengthSqr = n.sqrMagnitude;
                    normals[v] = lengthSqr > 1e-12f ? n / Mathf.Sqrt(lengthSqr) : Vector3.up;
                }
            }
        }

        // Weight sets no renderer has used for a while (an animated face's past expressions).
        private void PruneSkinningCache(int frameCount)
        {
            if (frameCount % 300 != 0 || cachedSkinning.Count == 0)
                return;
            reusableStaleSkinningKeys.Clear();
            foreach (var kv in cachedSkinning)
            {
                if (kv.Value == null || frameCount - kv.Value.lastUsedFrame > 600)
                    reusableStaleSkinningKeys.Add(kv.Key);
            }
            for (int i = 0; i < reusableStaleSkinningKeys.Count; i++)
                cachedSkinning.Remove(reusableStaleSkinningKeys[i]);
        }

        /// <summary>
        /// BakeMesh fallback: CPU re-skin a single skinned mesh.
        /// </summary>
        // BakeMesh fallback: what does not change between bakes of one renderer — its
        // mesh hash (bone names included), UVs, vertex colors and per-submesh triangles,
        // each compacted to the vertices it uses so a multi-material mesh does not
        // upload every vertex once per submesh. Rebuilt when the shared mesh changes.
        private sealed class BakedSubmesh
        {
            public int[] triangles;
            public int[] vertexMap; // null: the triangles index the whole mesh
        }
        private sealed class BakedRendererTopology
        {
            public int sharedMeshId;
            public int vertexCount;
            public ulong meshHash;
            public Vector2[] uvs;
            public Color32[] colors;
            public int[] mergedTriangles;
            public BakedSubmesh[] submeshes; // null entries: no valid triangles
            // Pose of the last bake (bones relative to the renderer, its scale, blendshape
            // weights): an unchanged pose bakes the same vertices, so the last upload stands.
            public ulong poseSignature;
            public bool poseValid;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct FloatBits
        {
            [FieldOffset(0)] public float value;
            [FieldOffset(0)] public uint bits;
        }

        private static void MixPose(ref ulong hash, float value)
        {
            var f = new FloatBits { value = value };
            hash = (hash ^ f.bits) * 1099511628211UL;
        }

        private static ulong ComputePoseSignature(SkinnedMeshRenderer skinned, Transform root, Transform[] bones, Mesh sharedMesh)
        {
            ulong hash = 14695981039346656037UL;
            var scale = root.lossyScale;
            MixPose(ref hash, scale.x);
            MixPose(ref hash, scale.y);
            MixPose(ref hash, scale.z);
            if (bones != null)
            {
                var rootInverse = root.worldToLocalMatrix;
                for (int b = 0; b < bones.Length; b++)
                {
                    var bone = bones[b];
                    if (bone == null)
                    {
                        hash = (hash ^ 0x9E3779B9UL) * 1099511628211UL;
                        continue;
                    }
                    var m = rootInverse * bone.localToWorldMatrix;
                    MixPose(ref hash, m.m00); MixPose(ref hash, m.m01); MixPose(ref hash, m.m02); MixPose(ref hash, m.m03);
                    MixPose(ref hash, m.m10); MixPose(ref hash, m.m11); MixPose(ref hash, m.m12); MixPose(ref hash, m.m13);
                    MixPose(ref hash, m.m20); MixPose(ref hash, m.m21); MixPose(ref hash, m.m22); MixPose(ref hash, m.m23);
                }
            }
            int blendShapes = sharedMesh.blendShapeCount;
            for (int i = 0; i < blendShapes; i++)
                MixPose(ref hash, skinned.GetBlendShapeWeight(i));
            return hash;
        }

        // The last bake still holds: move its entries and refresh their materials.
        // A changed material uploads again (it is part of the mesh); false: bake.
        private bool ReuseBake(SkinnedMeshRenderer skinned, int skinnedId, int matId, Matrix4x4 localToWorld,
                               BakedRendererTopology topo, Material[] materials, bool doLog)
        {
            for (int sub = 0; sub < topo.submeshes.Length; sub++)
            {
                if (topo.submeshes[sub] == null) continue;
                int subKey = (sub == 0) ? skinnedId : (int)((uint)skinnedId ^ ((uint)sub * 0x9E3779B9u));
                if (!persistentSkinnedData.ContainsKey(subKey))
                    return false;
            }
            for (int sub = 0; sub < topo.submeshes.Length; sub++)
            {
                if (topo.submeshes[sub] == null) continue;
                int subKey = (sub == 0) ? skinnedId : (int)((uint)skinnedId ^ ((uint)sub * 0x9E3779B9u));
                int subMatId = matId;
                if (materials != null && sub < materials.Length && materials[sub] != null)
                {
                    subMatId = ResolveMaterial(skinned, materials[sub], doLog);
                    if (subMatId == 0) subMatId = matId;
                }
                var entry = persistentSkinnedData[subKey];
                entry.localToWorld = localToWorld;
                if (entry.materialId != subMatId)
                {
                    entry.materialId = subMatId;
                    entry.bakeFrame = Time.frameCount;
                }
                persistentSkinnedData[subKey] = entry;
            }
            return true;
        }
        private readonly Dictionary<int, BakedRendererTopology> bakedTopology = new Dictionary<int, BakedRendererTopology>();

        private static BakedSubmesh CompactSubmesh(int[] triangles, int vertexCount)
        {
            var remap = new int[vertexCount];
            for (int i = 0; i < remap.Length; i++) remap[i] = -1;
            int used = 0;
            for (int i = 0; i < triangles.Length; i++)
            {
                int v = triangles[i];
                if (v < 0 || v >= vertexCount)
                    return new BakedSubmesh { triangles = triangles }; // the converter rejects it
                if (remap[v] < 0)
                    remap[v] = used++;
            }
            if (used == vertexCount)
                return new BakedSubmesh { triangles = triangles };

            var map = new int[used];
            for (int v = 0; v < vertexCount; v++)
                if (remap[v] >= 0)
                    map[remap[v]] = v;
            var local = new int[triangles.Length];
            for (int i = 0; i < triangles.Length; i++)
                local[i] = remap[triangles[i]];
            return new BakedSubmesh { triangles = local, vertexMap = map };
        }

        private BakedRendererTopology BuildBakedTopology(SkinnedMeshRenderer skinned, Mesh sharedMesh, Mesh bakedMesh, int sharedMeshId, int vertexCount)
        {
            // Reuse cached UVs and triangles — they never change between frames
            Vector2[] uvCoords;
            int[] tris;
            if (cachedTopology.TryGetValue(sharedMeshId, out var topo) && topo.valid && topo.uvs != null && topo.triangles != null)
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

            if (tris.Length == 0 || tris.Length % 3 != 0)
                return null;

            // Vertex colors don't change with animation — read from sharedMesh or baked mesh
            Color32[] colors = null;
            try
            {
                if (sharedMesh.isReadable)
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

            ulong combinedMeshHash = RemixMeshConverter.GenerateMeshHash(sharedMesh);
            var bones = skinned.bones;
            if (bones != null)
            {
                foreach (var b in bones)
                {
                    if (b != null)
                        combinedMeshHash ^= HashUtils.HashStringFNV(b.name);
                    combinedMeshHash *= 1099511628211UL;
                }
            }

            // Per-submesh triangle arrays from bakedMesh
            int subMeshCount = bakedMesh.subMeshCount;
            var perSubTris = new int[subMeshCount][];
            bool anySubValid = false;
            for (int i = 0; i < subMeshCount; i++)
            {
                if (bakedMesh.GetTopology(i) != MeshTopology.Triangles) continue;
                var st = bakedMesh.GetTriangles(i);
                if (st != null && st.Length > 0)
                {
                    perSubTris[i] = st;
                    anySubValid = true;
                }
            }
            // Fallback: if no valid per-sub triangles, use merged
            if (!anySubValid)
                perSubTris = new int[][] { tris };

            var submeshes = new BakedSubmesh[perSubTris.Length];
            for (int i = 0; i < perSubTris.Length; i++)
            {
                var st = perSubTris[i];
                if (st != null && st.Length > 0 && st.Length % 3 == 0)
                    submeshes[i] = perSubTris.Length > 1 ? CompactSubmesh(st, vertexCount) : new BakedSubmesh { triangles = st };
            }

            return new BakedRendererTopology
            {
                sharedMeshId = sharedMeshId,
                vertexCount = vertexCount,
                meshHash = combinedMeshHash,
                uvs = uvCoords,
                colors = colors,
                mergedTriangles = tris,
                submeshes = submeshes
            };
        }

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

                var sharedMesh = skinned.sharedMesh;
                if (sharedMesh == null)
                    return false;
                int sharedMeshId = sharedMesh.GetInstanceID();
                var skinnedInfo = GetOrCreateSkinnedInfo(skinned);
                ulong pose = ComputePoseSignature(skinned, skinnedInfo.transform, skinnedInfo.bones, sharedMesh);
                if (bakedTopology.TryGetValue(skinnedId, out var lastTopo) && lastTopo.sharedMeshId == sharedMeshId &&
                    lastTopo.poseValid && lastTopo.poseSignature == pose &&
                    ReuseBake(skinned, skinnedId, matId, localToWorld, lastTopo, skinnedInfo.sharedMaterials, doLog))
                {
                    return true;
                }

                skinned.BakeMesh(bakedMesh);

                var verts = bakedMesh.vertices;
                var norms = bakedMesh.normals;
                if (verts == null || verts.Length == 0)
                    return false;

                if (!bakedTopology.TryGetValue(skinnedId, out var topo) || topo.sharedMeshId != sharedMeshId || topo.vertexCount != verts.Length)
                {
                    topo = BuildBakedTopology(skinned, sharedMesh, bakedMesh, sharedMeshId, verts.Length);
                    if (topo == null)
                    {
                        bakedTopology.Remove(skinnedId);
                        return false;
                    }
                    bakedTopology[skinnedId] = topo;
                    if (verts.Length > 20000)
                        logger.LogInfo($"[Skinning] Large BakeMesh fallback '{skinned.name}': {verts.Length} verts, {topo.mergedTriangles.Length / 3} tris, {topo.submeshes.Length} submeshes");

                    // Complete topology from baked mesh if triangles were unavailable (non-readable mesh)
                    if (cachedTopology.TryGetValue(sharedMeshId, out var pendingTopo) && !pendingTopo.valid)
                    {
                        pendingTopo.uvs = topo.uvs;
                        pendingTopo.triangles = topo.mergedTriangles;
                        pendingTopo.valid = true;
                        cachedTopology[sharedMeshId] = pendingTopo;
                        logger.LogInfo($"[MeshTopology] '{sharedMesh.name}' topology completed from BakeMesh: verts={pendingTopo.vertexCount} tris={topo.mergedTriangles.Length / 3} stride={pendingTopo.stride}");
                    }
                }

                topo.poseSignature = pose;
                topo.poseValid = true;

                var smMaterialsBake = skinnedInfo.sharedMaterials;
                ulong combinedMeshHash = topo.meshHash;
                for (int sub = 0; sub < topo.submeshes.Length; sub++)
                {
                    var submesh = topo.submeshes[sub];
                    if (submesh == null) continue;

                    int subMatId = matId;
                    if (smMaterialsBake != null && sub < smMaterialsBake.Length && smMaterialsBake[sub] != null)
                    {
                        var subMat = smMaterialsBake[sub];
                        subMatId = ResolveMaterial(skinned, subMat, doLog);
                        if (subMatId == 0) subMatId = matId;
                    }

                    int subKey = (sub == 0) ? skinnedId : (int)((uint)skinnedId ^ ((uint)sub * 0x9E3779B9u));
                    ulong subMeshHash = (sub == 0) ? combinedMeshHash : combinedMeshHash ^ ((ulong)(uint)sub * 2654435761UL);

                    persistentSkinnedData[subKey] = new SkinnedMeshData
                    {
                        meshId = subKey,
                        remixMeshHash = subMeshHash,
                        materialId = subMatId,
                        vertices = verts,
                        normals = norms,
                        uvs = topo.uvs,
                        colors = topo.colors,
                        triangles = submesh.triangles,
                        vertexMap = submesh.vertexMap,
                        localToWorld = localToWorld,
                        bakeFrame = Time.frameCount
                    };
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
        // ResolveMaterial runs per skinned renderer per frame: property IDs, one reused property
        // block, and each material's property set (fixed by its shader) looked up once.
        private static readonly int[] SkinnedMpbColorProps =
        {
            PropColor, PropBaseColor, Shader.PropertyToID("_TintColor"), Shader.PropertyToID("_Tint"),
            Shader.PropertyToID("_Color1"), Shader.PropertyToID("_Color2"), Shader.PropertyToID("_ColorTint"),
            Shader.PropertyToID("_BodyColor"), Shader.PropertyToID("_SkinColor"), Shader.PropertyToID("_FurColor"),
            Shader.PropertyToID("_HairColor"), Shader.PropertyToID("_ClothColor"), Shader.PropertyToID("_ClothingColor"),
            Shader.PropertyToID("_EyeColor")
        };
        private static readonly int PropEmissiveIntensity = Shader.PropertyToID("_EmissiveIntensity");
        private static readonly int PropColorTint = Shader.PropertyToID("_ColorTint");
        private static readonly int PropHue = Shader.PropertyToID("_Hue");
        private static readonly int PropHueShift = Shader.PropertyToID("_HueShift");
        private static readonly int PropHueAdjust = Shader.PropertyToID("_HueAdjust");
        private static readonly int PropBrightness = Shader.PropertyToID("_Brightness");
        private static readonly int PropContrast = Shader.PropertyToID("_Contrast");
        private static readonly int PropSaturation = Shader.PropertyToID("_Saturation");
        private static readonly int PropHSBC = Shader.PropertyToID("_HSBC");
        private MaterialPropertyBlock skinnedMpb;

        private struct SkinnedMaterialFacts
        {
            public Shader shader;
            public bool emissiveIntensity, colorTint, hue, hueShift, hueAdjust, brightness, contrast, saturation, hsbc;
        }
        private readonly Dictionary<int, SkinnedMaterialFacts> skinnedMaterialFacts = new Dictionary<int, SkinnedMaterialFacts>();

        private SkinnedMaterialFacts GetSkinnedMaterialFacts(Material mat)
        {
            int id = mat.GetInstanceID();
            var shader = mat.shader;
            if (skinnedMaterialFacts.TryGetValue(id, out var facts) && ReferenceEquals(facts.shader, shader))
                return facts;
            facts = new SkinnedMaterialFacts
            {
                shader = shader,
                emissiveIntensity = mat.HasProperty(PropEmissiveIntensity),
                colorTint = mat.HasProperty(PropColorTint),
                hue = mat.HasProperty(PropHue),
                hueShift = mat.HasProperty(PropHueShift),
                hueAdjust = mat.HasProperty(PropHueAdjust),
                brightness = mat.HasProperty(PropBrightness),
                contrast = mat.HasProperty(PropContrast),
                saturation = mat.HasProperty(PropSaturation),
                hsbc = mat.HasProperty(PropHSBC)
            };
            skinnedMaterialFacts[id] = facts;
            return facts;
        }

        private int ResolveMaterial(SkinnedMeshRenderer skinned, Material targetMat = null, bool doLog = false)
        {
            int matId = 0;
            Material bestMaterial = targetMat;
            
            if (bestMaterial == null)
            {
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
            }
            
            if (bestMaterial != null)
            {
                matId = StaticGeometryDedupe.GetCanonicalMaterialId(bestMaterial);
                
                // Check for MaterialPropertyBlock overrides (used by games for dynamic per-renderer colors/emission)
                Color? mpbColor = null;
                Color? mpbEmissiveColor = null;
                float? mpbEmissiveIntensity = null;
                Texture2D mpbMainTex = null;
                var facts = GetSkinnedMaterialFacts(bestMaterial);
                try
                {
                    if (skinnedMpb == null)
                        skinnedMpb = new MaterialPropertyBlock();
                    var mpb = skinnedMpb;
                    if (skinned.HasPropertyBlock())
                        skinned.GetPropertyBlock(mpb);
                    else
                        mpb.Clear();
                    if (!mpb.isEmpty)
                    {
                        // Check for main texture override
                        Texture tex = mpb.GetTexture(PropMainTex);
                        if (tex == null) tex = mpb.GetTexture(PropBaseMap);
                        if (tex == null) tex = mpb.GetTexture(PropDiffuse);
                        if (tex == null) tex = mpb.GetTexture(PropTexture);
                        if (tex != null && tex is Texture2D t2d)
                        {
                            mpbMainTex = t2d;
                            matId = HashCombine(matId, tex.GetInstanceID());
                        }

                        // Capture albedo/diffuse color overrides (ATLYSS character skin/hair/eye colors)
                        foreach (var cp in SkinnedMpbColorProps)
                        {
                            var c = mpb.GetColor(cp);
                            // GetColor returns Color.clear (0,0,0,0) if the property is missing from the MPB.
                            // Ignore missing properties, and ignore pure white (no tint).
                            if (c.a > 0.0f && (c.r < 0.99f || c.g < 0.99f || c.b < 0.99f || c.a < 0.99f))
                            {
                                mpbColor = c;
                                // Generate unique material ID for this color override
                                Color32 c32 = c;
                                int colInt = (c32.r << 24) | (c32.g << 16) | (c32.b << 8) | c32.a;
                                matId = HashCombine(matId, colInt);
                                break;
                            }
                        }

                        var emC = mpb.GetColor(PropEmissiveColor);
                        if (emC.a > 0.0f && (emC.r > 0.01f || emC.g > 0.01f || emC.b > 0.01f))
                        {
                            mpbEmissiveColor = emC;
                            Color32 e32 = emC;
                            int emisInt = (e32.r << 24) | (e32.g << 16) | (e32.b << 8) | e32.a;
                            matId = HashCombine(matId, emisInt);
                        }
                        if (facts.emissiveIntensity)
                        {
                            float v = mpb.GetFloat(PropEmissiveIntensity);
                            if (v > 0) mpbEmissiveIntensity = v;
                        }
                    }
                }
                catch { }

                // Check for per-material HSV or ColorTint customizations
                if (!mpbColor.HasValue)
                {
                    try
                    {
                        if (facts.colorTint)
                        {
                            Color ct = bestMaterial.GetColor(PropColorTint);
                            if (ct.a > 0f && (ct.r < 0.98f || ct.g < 0.98f || ct.b < 0.98f))
                            {
                                Color32 c32 = ct;
                                int colInt = (c32.r << 24) | (c32.g << 16) | (c32.b << 8) | c32.a;
                                matId = HashCombine(matId, colInt);
                            }
                        }

                        if (facts.hue || facts.hueShift || facts.hueAdjust ||
                            facts.brightness || facts.contrast || facts.saturation ||
                            facts.hsbc)
                        {
                            float h = facts.hue ? bestMaterial.GetFloat(PropHue) :
                                      (facts.hueShift ? bestMaterial.GetFloat(PropHueShift) :
                                      (facts.hueAdjust ? bestMaterial.GetFloat(PropHueAdjust) : 0f));
                            float b = facts.brightness ? bestMaterial.GetFloat(PropBrightness) : 0f;
                            float c = facts.contrast ? bestMaterial.GetFloat(PropContrast) : 1f;
                            float s = facts.saturation ? bestMaterial.GetFloat(PropSaturation) : 1f;
                            if (facts.hsbc)
                            {
                                Vector4 v = bestMaterial.GetVector(PropHSBC);
                                h = v.x; s = v.y; b = v.z; c = v.w;
                            }

                            if (Mathf.Abs(h) > 0.001f || Mathf.Abs(b) > 0.001f || Mathf.Abs(c - 1f) > 0.001f || Mathf.Abs(s - 1f) > 0.001f)
                            {
                                int hInt = Mathf.RoundToInt(h * 1000f);
                                int bInt = Mathf.RoundToInt(b * 1000f);
                                int cInt = Mathf.RoundToInt(c * 1000f);
                                int sInt = Mathf.RoundToInt(s * 1000f);
                                int hsbcHash = HashCombine(HashCombine(hInt, bInt), HashCombine(cInt, sInt));
                                matId = HashCombine(matId, hsbcHash);
                            }
                        }
                    }
                    catch { }
                }

                materialManager.CaptureMaterialTextures(bestMaterial, matId, mpbEmissiveColor, mpbEmissiveIntensity, mpbMainTex, mpbColor);
                

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
            bakedTopology.Clear();
        }
    }
}
