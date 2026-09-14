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
        private readonly ConfigEntry<bool> configHardwareSkinning;
        private readonly ConfigEntry<bool> configPersistDisabledRenderers;
        
        // Renderer caching
        private List<MeshRenderer> cachedRenderers = new List<MeshRenderer>();
        private List<SkinnedMeshRenderer> cachedSkinnedRenderers = new List<SkinnedMeshRenderer>();
        private readonly HashSet<int> cachedRendererIds = new HashSet<int>();
        private readonly HashSet<int> cachedSkinnedRendererIds = new HashSet<int>();
        private int rendererCacheFrame = -1;
        
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
        public int PersistentStaticCount => persistentStaticInstances.Count;
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
                foreach (var kv in persistentStaticInstances)
                {
                    var entry = kv.Value;
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
            foreach (var kv in persistentStaticInstances)
            {
                var p = kv.Value;
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
            ConfigEntry<bool> persistDisabledRenderers)
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
            this.configHardwareSkinning = hardwareSkinning;
            this.configPersistDisabledRenderers = persistDisabledRenderers;
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
            lastSkinnedTransforms.Clear();
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
            persistentStaticInstances.Clear();
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
                }
            }
            
            rendererCacheFrame = frameCount;
            
            logger.LogInfo($"Renderer cache refreshed: {cachedRenderers.Count} static, {cachedSkinnedRenderers.Count} skinned");
            
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
            }
            
            // Debug toggle check
            if (!configCaptureStaticMeshes.Value)
                return;
            
            staticCaptureCount++;
            int totalDrawn = 0;
            
            // Capture static meshes
            foreach (var renderer in cachedRenderers)
            {
                if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy)
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

                if (ShouldPreferSceneScan(renderer, mesh))
                {
                    persistentStaticInstances.Remove(rendererInstanceId);
                    continue;
                }

                var materials = renderer.sharedMaterials;

                Texture mpbMainTex = null;
                Color? mpbColor = null;
                Color? mpbEmissive = null;
                int mpbHash = 0;

                if (sharedStaticMpb == null)
                    sharedStaticMpb = new MaterialPropertyBlock();

                if (renderer.HasPropertyBlock())
                {
                    renderer.GetPropertyBlock(sharedStaticMpb);
                    Texture tex = sharedStaticMpb.GetTexture("_MainTex");
                    if (tex != null)
                    {
                        mpbMainTex = tex;
                        mpbHash = HashCombine(mpbHash, tex.GetInstanceID());
                    }
                    Color col = sharedStaticMpb.GetColor("_Color");
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
                state.instances.Add(new MeshInstanceData
                {
                    meshKey = meshKey,
                    meshId = meshId,
                    localToWorld = transform,
                    rendererInstanceId = rendererInstanceId,
                    dedupeKey = dedupeKey
                });
                totalDrawn++;
                
                // Remember this renderer's transform so we can keep drawing it if it gets disabled.
                // Do NOT persist viewmodels/weapons (they should disappear when unequipped).
                bool isCurrentViewModel = mainCam != null && renderer.transform.IsChildOf(mainCam.transform);
                if (!isCurrentViewModel)
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
            
            // Draw persistent instances for disabled (but not destroyed) renderers.
            // This keeps objects visible that the game deactivates (e.g. CyberGrind cubes after wave settles).
            // Gated by config — disabled by default since games with scene variants (e.g. Stanley Parable)
            // use inactive GameObjects for alternate rooms that should NOT be rendered.
            int persistentDrawn = 0;
            if (configPersistDisabledRenderers.Value)
            {
            var keysToRemove = (List<int>)null;
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
                state.instances.Add(new MeshInstanceData
                {
                    meshKey = entry.meshKey,
                    meshId = entry.meshId,
                    localToWorld = entry.localToWorld,
                    rendererInstanceId = entry.renderer.GetInstanceID(),
                    dedupeKey = entry.dedupeKey
                });
                persistentDrawn++;
            }
            if (keysToRemove != null)
            {
                foreach (var key in keysToRemove)
                    persistentStaticInstances.Remove(key);
            }
            }
            
            // Periodic tracking
            if (configDebugLogInterval.Value > 0 && staticCaptureCount % 300 == 1)
                logger.LogInfo($"[StaticCapture] frame={frameCount} drawn={totalDrawn} persistent={persistentDrawn} total={persistentStaticInstances.Count} queued={meshesToCreate.Count} failedMeshes={failedMeshKeys.Count}");
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
                        mesh = cr.GetMesh();
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
                    skinningData = null
                });
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
                    if (sharedMesh.HasVertexAttribute(VertexAttribute.Position))
                    {
                        int s = MeshCompat.GetVertexAttributeStream(sharedMesh, VertexAttribute.Position);
                        if (s == 0)
                        {
                            posOff2 = MeshCompat.GetVertexAttributeOffset(sharedMesh, VertexAttribute.Position);
                            stride2 = MeshCompat.GetVertexBufferStride(sharedMesh, 0);
                        }
                    }
                    if (sharedMesh.HasVertexAttribute(VertexAttribute.Normal))
                    {
                        int s = MeshCompat.GetVertexAttributeStream(sharedMesh, VertexAttribute.Normal);
                        if (s == 0)
                            nrmOff2 = MeshCompat.GetVertexAttributeOffset(sharedMesh, VertexAttribute.Normal);
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
                
                if (sharedMesh.HasVertexAttribute(VertexAttribute.Position))
                {
                    int stream = MeshCompat.GetVertexAttributeStream(sharedMesh, VertexAttribute.Position);
                    if (stream == 0) // GPU skinned buffer is always stream 0
                    {
                        posOffset = MeshCompat.GetVertexAttributeOffset(sharedMesh, VertexAttribute.Position);
                        stride = MeshCompat.GetVertexBufferStride(sharedMesh, 0);
                    }
                }
                
                if (sharedMesh.HasVertexAttribute(VertexAttribute.Normal))
                {
                    int stream = MeshCompat.GetVertexAttributeStream(sharedMesh, VertexAttribute.Normal);
                    if (stream == 0)
                        nrmOffset = MeshCompat.GetVertexAttributeOffset(sharedMesh, VertexAttribute.Normal);
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
                    return;
                }
                
                int boneCount = Mathf.Min(bindPoses.Length, bones.Length);
                if (boneCount > MAX_REMIX_BONES)
                {
                    logger.LogInfo($"[Skinning] '{mesh.name}' has {boneCount} bones (>{MAX_REMIX_BONES}) — BakeMesh fallback");
                    return;
                }
                
                // Extract bone weights — try modern API first (Unity 2019.3+), then legacy
                int vertexCount = mesh.vertexCount;
                float[] blendWeights = new float[BONES_PER_VERTEX * vertexCount];
                uint[] blendIndices = new uint[BONES_PER_VERTEX * vertexCount];
                
                try
                {
                    var bonesPerVertex = mesh.GetBonesPerVertex();
                    var allBoneWeights = mesh.GetAllBoneWeights();
                    
                    if (bonesPerVertex.Length == 0 || allBoneWeights.Length == 0)
                        throw new InvalidOperationException("Empty bone weight data");
                    
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
                }
                catch
                {
                    // Legacy fallback: BoneWeight per vertex (always exactly 4)
                    try
                    {
                        var legacyWeights = mesh.boneWeights;
                        if (legacyWeights == null || legacyWeights.Length == 0)
                        {
                            logger.LogInfo($"[Skinning] '{mesh.name}' bone weights not accessible — BakeMesh fallback");
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
                bool hasTexture = mat.mainTexture != null;
                if (!hasTexture)
                {
                    foreach (var prop in textureProps)
                    {
                        if (mat.HasProperty(prop) && mat.GetTexture(prop) != null)
                        { hasTexture = true; break; }
                    }
                }
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
