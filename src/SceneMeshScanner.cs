using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace UnityRemix
{
    /// <summary>
    /// Scans the Unity scene graph to find all static mesh geometry, including
    /// inactive GameObjects that the runtime capture misses. Rescans periodically
    /// after scene load to catch objects that arrive via async/Addressable loading.
    /// Streams mesh creation to the render thread to avoid frame hitches.
    /// </summary>
    public class SceneMeshScanner
    {
        private readonly ManualLogSource logger;
        private readonly RemixMeshConverter meshConverter;
        private readonly RemixMaterialManager materialManager;
        private readonly object apiLock;

        private struct SubMeshSurface
        {
            public int[] Indices;
            public int MaterialId;
        }

        private struct ScannedMeshData
        {
            public ulong MeshHash;
            public StaticGeometryKey DedupeKey;
            public int RendererInstanceId;
            public Vector3[] Vertices;
            public Vector3[] Normals;
            public Vector2[] UVs;
            public Color32[] Colors;
            public SubMeshSurface[] Surfaces;
            public Matrix4x4 LocalToWorld;
            public int Layer;
            public Renderer SourceRenderer;
        }

        public struct InstanceData
        {
            public IntPtr MeshHandle;
            public StaticGeometryKey DedupeKey;
            public int RendererInstanceId;
            public RemixAPI.remixapi_Transform Transform;
            public int Layer;
            public Vector3 BoundsCenter;
        }

        public struct DedupeEntry
        {
            public StaticGeometryKey DedupeKey;
            public int RendererInstanceId;
            public int Layer;
        }

        // Streaming: main thread pushes extracted mesh data, render thread drains batches
        private readonly Queue<ScannedMeshData> streamingQueue = new Queue<ScannedMeshData>();
        private readonly object streamLock = new object();
        private volatile bool streamingActive;

        // Completed instances drawn every frame
        private readonly List<InstanceData> currentInstances = new List<InstanceData>();
        private readonly List<Renderer> instanceRenderers = new List<Renderer>();
        private readonly object instanceLock = new object();

        // Visibility-filtered snapshot: built on main thread, read on render thread
        private InstanceData[] visibleInstances;

        // Mesh handle dedup: same geometry → same Remix handle
        private readonly Dictionary<ulong, IntPtr> meshHandles = new Dictionary<ulong, IntPtr>();

        // Track which MeshFilter instance IDs we've already scanned (avoids duplicates across rescans)
        private readonly HashSet<int> scannedFilterIds = new HashSet<int>();

        // When true, skip inactive renderers during scan (saves memory, prevents scanning ghost geometry)
        private readonly bool scanActiveOnly;

        // Rescan state: async-loaded objects appear after OnSceneLoaded
        private Scene activeScene;
        private float timeSinceSceneLoad;
        private float rescanTimer;
        private const float RescanInterval = 1.0f;
        private const float RescanDuration = 30.0f;

        private const int MeshesPerFrame = 32;

        public bool HasData
        {
            get { lock (instanceLock) { return currentInstances.Count > 0; } }
        }

        public bool IsStreaming => streamingActive;

        public int TotalInstanceCount
        {
            get { lock (instanceLock) { return currentInstances.Count; } }
        }

        public int StreamingQueueCount
        {
            get { lock (streamLock) { return streamingQueue.Count; } }
        }

        public DedupeEntry[] GetDedupeEntries()
        {
            lock (instanceLock)
            {
                if (currentInstances.Count == 0)
                    return Array.Empty<DedupeEntry>();

                var entries = new DedupeEntry[currentInstances.Count];
                for (int i = 0; i < currentInstances.Count; i++)
                {
                    entries[i] = new DedupeEntry
                    {
                        DedupeKey = currentInstances[i].DedupeKey,
                        RendererInstanceId = currentInstances[i].RendererInstanceId,
                        Layer = currentInstances[i].Layer
                    };
                }
                return entries;
            }
        }

        /// <summary>
        /// Collect per-instance debug entries for 3D box overlay. Called on main thread only when HUD is visible.
        /// </summary>
        public List<RemixFrameCapture.DebugMeshEntry> CollectDebugEntries()
        {
            lock (instanceLock)
            {
                var entries = new List<RemixFrameCapture.DebugMeshEntry>(currentInstances.Count);
                for (int i = 0; i < currentInstances.Count; i++)
                {
                    if (i >= instanceRenderers.Count) break;
                    var r = instanceRenderers[i];
                    if (r == null) continue;
                    if (!r.enabled || !r.gameObject.activeInHierarchy) continue;

                    var mf = r.GetComponent<MeshFilter>();
                    entries.Add(new RemixFrameCapture.DebugMeshEntry
                    {
                        Name = r.gameObject.name,
                        MeshName = mf != null && mf.sharedMesh != null ? mf.sharedMesh.name : "",
                        MeshId = mf != null && mf.sharedMesh != null ? mf.sharedMesh.GetInstanceID() : 0,
                        LayerIndex = r.gameObject.layer,
                        RendererInstanceId = r.GetInstanceID(),
                        LayerName = LayerMask.LayerToName(r.gameObject.layer),
                        Origin = "Scanner",
                        BoundsCenter = r.bounds.center,
                        BoundsExtents = r.bounds.extents,
                        MaterialName = r.sharedMaterial != null ? r.sharedMaterial.name : "(null)",
                        NoTexture = r.sharedMaterial == null,
                    });
                }
                return entries;
            }
        }

        /// <summary>
        /// Returns the set of layer indices that have scanned instances.
        /// </summary>
        public Dictionary<int, int> GetLayerCounts()
        {
            var counts = new Dictionary<int, int>();
            lock (instanceLock)
            {
                foreach (var inst in currentInstances)
                {
                    if (!counts.ContainsKey(inst.Layer))
                        counts[inst.Layer] = 0;
                    counts[inst.Layer]++;
                }
            }
            return counts;
        }

        public SceneMeshScanner(
            ManualLogSource logger,
            RemixMeshConverter meshConverter,
            RemixMaterialManager materialManager,
            object apiLock,
            bool scanActiveOnly = true)
        {
            this.logger = logger;
            this.meshConverter = meshConverter;
            this.materialManager = materialManager;
            this.apiLock = apiLock;
            this.scanActiveOnly = scanActiveOnly;
            NativeMeshReader.SetLogger(logger);
        }

        /// <summary>
        /// Must be called on the main thread. Performs the initial scan and starts
        /// periodic rescanning to catch async-loaded objects.
        /// </summary>
        public void OnSceneLoaded(Scene scene)
        {
            if (!scene.IsValid())
                return;

            scannedFilterIds.Clear();
            activeScene = scene;
            timeSinceSceneLoad = 0f;
            rescanTimer = 0f;

            int queued = ScanScene(scene, logDiagnostics: true);
            if (queued > 0)
            {
                streamingActive = true;
                logger.LogInfo($"Scene scan queued {queued} meshes for streaming (rescanning for {RescanDuration}s to catch async-loaded objects)");
            }
            else
            {
                logger.LogInfo($"Scene scan found no meshes yet in '{scene.name}' (will rescan for {RescanDuration}s)");
            }
        }

        /// <summary>
        /// Must be called on the main thread each frame (e.g. from plugin Update).
        /// Periodically rescans to pick up objects that loaded asynchronously.
        /// </summary>
        public void Update(float deltaTime)
        {
            if (!activeScene.IsValid())
                return;

            timeSinceSceneLoad += deltaTime;
            if (timeSinceSceneLoad > RescanDuration)
            {
                activeScene = default;
                return;
            }

            rescanTimer += deltaTime;
            if (rescanTimer < RescanInterval)
                return;
            rescanTimer = 0f;

            int queued = ScanScene(activeScene, logDiagnostics: false);
            if (queued > 0)
            {
                streamingActive = true;
                logger.LogInfo($"Rescan found {queued} new meshes ({timeSinceSceneLoad:F0}s after scene load)");
            }
        }

        /// <summary>
        /// Called on the render thread each frame. Drains a batch from the queue
        /// and returns the visibility-filtered snapshot built by UpdateVisibility().
        /// Falls back to currentInstances if UpdateVisibility hasn't run yet.
        /// </summary>
        public InstanceData[] GetInstances()
        {
            DrainStreamingBatch();

            var snapshot = Volatile.Read(ref visibleInstances);
            if (snapshot != null)
                return snapshot;

            // Fallback: UpdateVisibility hasn't populated visibleInstances yet (e.g. first
            // frames after drain, before main-thread LateUpdate runs). Return currentInstances
            // directly so newly streamed geometry isn't invisible for multiple frames.
            lock (instanceLock)
            {
                return currentInstances.Count > 0 ? currentInstances.ToArray() : null;
            }
        }

        /// <summary>
        /// Must be called on the main thread each frame. Filters scanned instances by
        /// active state, distance culling, and visibility culling.
        /// </summary>
        public void UpdateVisibility(Vector3 cameraPosition, bool useDistanceCulling, float maxRenderDistance, bool useVisibilityCulling)
        {
            lock (instanceLock)
            {
                if (currentInstances.Count == 0)
                {
                    Volatile.Write(ref visibleInstances, null);
                    return;
                }

                bool anyCulling = scanActiveOnly || useDistanceCulling || useVisibilityCulling;
                if (!anyCulling)
                {
                    Volatile.Write(ref visibleInstances, currentInstances.ToArray());
                    return;
                }

                var visible = new List<InstanceData>(currentInstances.Count);
                float maxDistSqr = maxRenderDistance * maxRenderDistance;

                for (int i = 0; i < currentInstances.Count; i++)
                {
                    var instance = currentInstances[i];

                    if (i < instanceRenderers.Count)
                    {
                        var renderer = instanceRenderers[i];

                        // Active-only filtering
                        if (scanActiveOnly)
                        {
                            if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy)
                                continue;
                        }

                        // Visibility culling (only meaningful for active renderers)
                        if (useVisibilityCulling && renderer != null
                            && renderer.enabled && renderer.gameObject.activeInHierarchy
                            && !renderer.isVisible)
                            continue;
                    }

                    // Distance culling using pre-computed bounds center
                    if (useDistanceCulling)
                    {
                        float sqrDist = (instance.BoundsCenter - cameraPosition).sqrMagnitude;
                        if (sqrDist > maxDistSqr)
                            continue;
                    }

                    visible.Add(instance);
                }

                Volatile.Write(ref visibleInstances, visible.Count > 0 ? visible.ToArray() : null);
            }
        }

        private static Vector3 ComputeBoundsCenter(Vector3[] vertices, Matrix4x4 localToWorld)
        {
            if (vertices == null || vertices.Length == 0)
                return Vector3.zero;
            Vector3 min = vertices[0], max = vertices[0];
            for (int i = 1; i < vertices.Length; i++)
            {
                min = Vector3.Min(min, vertices[i]);
                max = Vector3.Max(max, vertices[i]);
            }
            return localToWorld.MultiplyPoint3x4((min + max) * 0.5f);
        }

        public void ClearData()
        {
            lock (instanceLock) { currentInstances.Clear(); instanceRenderers.Clear(); }
            lock (streamLock) { streamingQueue.Clear(); }
            meshHandles.Clear();
            scannedFilterIds.Clear();
            Volatile.Write(ref visibleInstances, null);
            activeScene = default;
        }

        private int ScanScene(Scene scene, bool logDiagnostics)
        {
            var filters = Resources.FindObjectsOfTypeAll<MeshFilter>();
            var combinedDataCache = new Dictionary<int, (Vector3[] verts, Vector3[] norms, Vector2[] uvs, Color32[] cols, int[][] subIndices)>();
            int queued = 0;
            int skippedAlreadyScanned = 0, skippedWrongScene = 0, skippedNoRenderer = 0;
            int skippedInactive = 0;
            int skippedNoMesh = 0, skippedNoVerts = 0, skippedNoTris = 0, skippedReadError = 0;
            int gpuReadbackCount = 0;
            int vertexColorCount = 0;

            foreach (var filter in filters)
            {
                if (filter == null)
                    continue;

                int filterId = filter.GetInstanceID();
                if (scannedFilterIds.Contains(filterId))
                {
                    skippedAlreadyScanned++;
                    continue;
                }

                if (!filter.gameObject.scene.IsValid() || !filter.gameObject.scene.isLoaded)
                {
                    skippedWrongScene++;
                    continue;
                }

                if (filter.gameObject.scene != scene && filter.gameObject.scene.name != "DontDestroyOnLoad")
                {
                    skippedWrongScene++;
                    continue;
                }

                var renderer = filter.GetComponent<MeshRenderer>();
                if (renderer == null)
                {
                    skippedNoRenderer++;
                    scannedFilterIds.Add(filterId);
                    continue;
                }

                // Skip inactive renderers to avoid scanning ghost geometry from alternate scene variants.
                // Don't add to scannedFilterIds — rescans will retry when the object becomes active.
                if (scanActiveOnly && (!renderer.enabled || !renderer.gameObject.activeInHierarchy))
                {
                    skippedInactive++;
                    continue;
                }

                var mesh = filter.sharedMesh;
                if (mesh == null)
                {
                    skippedNoMesh++;
                    scannedFilterIds.Add(filterId);
                    continue;
                }

                // Unity's static batching merges renderers into a single "Combined Mesh" with
                // pre-transformed world-space vertices. Each renderer owns a slice of submeshes
                // at [subMeshStartIndex .. subMeshStartIndex + sharedMaterials.Length).
                bool isCombinedMesh = mesh.name != null && mesh.name.StartsWith("Combined Mesh");

                // Mark scanned before extraction — even if geometry is empty we won't retry
                scannedFilterIds.Add(filterId);

                Vector3[] vertices = null;
                Vector3[] normals = null;
                Vector2[] uvs = null;
                Color32[] colors = null;
                int[][] subMeshIndices = null;

                // For combined meshes, reuse cached vertex/index data across renderers
                if (isCombinedMesh)
                {
                    int meshId = mesh.GetInstanceID();
                    if (combinedDataCache.TryGetValue(meshId, out var cached))
                    {
                        vertices = cached.verts;
                        normals = cached.norms;
                        uvs = cached.uvs;
                        colors = cached.cols;
                        subMeshIndices = cached.subIndices;
                    }
                }

                if (vertices == null)
                {
                    if (mesh.isReadable)
                    {
                        try
                        {
                            vertices = mesh.vertices;
                        }
                        catch { }
                    }

                    if (vertices != null && vertices.Length > 0)
                    {
                        // Fast path: CPU mesh data available
                        normals = mesh.normals;
                        uvs = mesh.uv;
                        colors = mesh.colors32;
                        if (colors != null && colors.Length == 0) colors = null;
                    }
                    else if (mesh.vertexCount > 0)
                    {
                        // GPU readback path: vertex data is GPU-only
                        try
                        {
                            if (ReadMeshFromGPU(mesh, out vertices, out normals, out uvs, out subMeshIndices))
                                gpuReadbackCount++;
                        }
                        catch (Exception ex)
                        {
                            logger.LogWarning($"GPU readback failed for mesh '{mesh.name}': {ex.Message}");
                            skippedReadError++;
                            continue;
                        }
                    }

                    if (isCombinedMesh && vertices != null && vertices.Length > 0)
                        combinedDataCache[mesh.GetInstanceID()] = (vertices, normals, uvs, colors, subMeshIndices);
                }

                if (vertices == null || vertices.Length == 0)
                {
                    skippedNoVerts++;
                    continue;
                }

                // Build per-submesh surfaces with separate materials
                var materials = renderer.sharedMaterials;
                var surfaces = new List<SubMeshSurface>();

                // For combined meshes, each renderer only owns a slice of submeshes
                int subStart = 0;
                int subEnd = mesh.subMeshCount;
                if (isCombinedMesh)
                {
                    subStart = renderer.subMeshStartIndex;
                    subEnd = Math.Min(subStart + (materials != null ? materials.Length : 0), mesh.subMeshCount);
                    if (subStart >= subEnd)
                        continue;
                }

                if (subMeshIndices == null)
                {
                    // CPU path — extract per-submesh indices for this renderer's range
                    var subList = new List<int[]>();
                    for (int sub = subStart; sub < subEnd; sub++)
                    {
                        if (mesh.GetTopology(sub) != MeshTopology.Triangles)
                        {
                            subList.Add(null);
                            continue;
                        }
                        var tris = mesh.GetTriangles(sub);
                        subList.Add(tris != null && tris.Length > 0 ? tris : null);
                    }
                    subMeshIndices = subList.ToArray();
                }
                else if (isCombinedMesh)
                {
                    // GPU readback / cache returned all submeshes — extract only this renderer's range
                    int rangeCount = Math.Min(subEnd, subMeshIndices.Length) - subStart;
                    if (rangeCount > 0)
                    {
                        var subset = new int[rangeCount][];
                        Array.Copy(subMeshIndices, subStart, subset, 0, rangeCount);
                        subMeshIndices = subset;
                    }
                }

                for (int sub = 0; sub < subMeshIndices.Length; sub++)
                {
                    var tris = subMeshIndices[sub];
                    if (tris == null || tris.Length == 0)
                        continue;

                    int matId = 0;
                    if (materials != null && sub < materials.Length && materials[sub] != null)
                    {
                        matId = materials[sub].GetInstanceID();
                        materialManager.CaptureMaterialTextures(materials[sub], matId);
                    }
                    surfaces.Add(new SubMeshSurface { Indices = tris, MaterialId = matId });
                }

                // For combined meshes, compact vertex arrays so each Remix mesh only
                // includes the vertices referenced by this renderer's submeshes
                if (isCombinedMesh && surfaces.Count > 0)
                    CompactMeshData(ref vertices, ref normals, ref uvs, ref colors, surfaces);

                if (surfaces.Count == 0)
                {
                    skippedNoTris++;
                    continue;
                }

                // Validate indices across all surfaces
                bool valid = true;
                foreach (var surf in surfaces)
                {
                    if (surf.Indices.Length % 3 != 0) { valid = false; break; }
                    for (int i = 0; i < surf.Indices.Length; i++)
                    {
                        if (surf.Indices[i] < 0 || surf.Indices[i] >= vertices.Length)
                        { valid = false; break; }
                    }
                    if (!valid) break;
                }
                if (!valid)
                    continue;

                // Use transform hierarchy in hash so each object gets its own persistent Remix mesh + material binding
                ulong meshHash = GenerateInstanceMeshHash(mesh, filter.transform);
                var dedupeKey = StaticGeometryDedupe.BuildKey(renderer, mesh);

                if (colors != null && colors.Length > 0)
                    vertexColorCount++;

                lock (streamLock)
                {
                    streamingQueue.Enqueue(new ScannedMeshData
                    {
                        MeshHash = meshHash,
                        DedupeKey = dedupeKey,
                        RendererInstanceId = renderer.GetInstanceID(),
                        Vertices = vertices,
                        Normals = normals,
                        UVs = uvs,
                        Colors = colors,
                        Surfaces = surfaces.ToArray(),
                        LocalToWorld = isCombinedMesh ? Matrix4x4.identity : filter.transform.localToWorldMatrix,
                        Layer = filter.gameObject.layer,
                        SourceRenderer = renderer,
                    });
                }
                queued++;
            }

            if (logDiagnostics || queued > 0)
            {
                logger.LogInfo($"Scene scan '{scene.name}': {filters.Length} total MeshFilters, {queued} queued ({gpuReadbackCount} via GPU readback, {vertexColorCount} with vertex colors)" +
                    $" | skipped: {skippedWrongScene} wrong scene, {skippedAlreadyScanned} already scanned," +
                    $" {skippedInactive} inactive, {skippedNoRenderer} no renderer, {skippedNoMesh} no mesh," +
                    $" {skippedReadError} read error, {skippedNoVerts} no verts, {skippedNoTris} no tris");
                materialManager.LogMaterialStats();
            }

            return queued;
        }

        private void DrainStreamingBatch()
        {
            ScannedMeshData[] batch;
            lock (streamLock)
            {
                int count = Math.Min(MeshesPerFrame, streamingQueue.Count);
                if (count == 0)
                {
                    if (streamingActive)
                    {
                        streamingActive = false;
                        logger.LogInfo($"Streaming complete: {currentInstances.Count} scene mesh instances loaded");
                    }
                    return;
                }
                batch = new ScannedMeshData[count];
                for (int i = 0; i < count; i++)
                    batch[i] = streamingQueue.Dequeue();
            }

            var newInstances = new List<InstanceData>(batch.Length);
            var newRenderers = new List<Renderer>(batch.Length);

            foreach (var entry in batch)
            {
                IntPtr meshHandle = CreateRemixMesh(entry);
                if (meshHandle == IntPtr.Zero)
                    continue;

                // Convert Unity Matrix4x4 to Remix transform (Y-up to Z-up)
                var m = entry.LocalToWorld;
                var transform = RemixAPI.remixapi_Transform.FromMatrix(
                    m.m00, m.m02, m.m01, m.m03,
                    m.m20, m.m22, m.m21, m.m23,
                    m.m10, m.m12, m.m11, m.m13
                );

                newInstances.Add(new InstanceData
                {
                    MeshHandle = meshHandle,
                    DedupeKey = entry.DedupeKey,
                    RendererInstanceId = entry.RendererInstanceId,
                    Transform = transform,
                    Layer = entry.Layer,
                    BoundsCenter = ComputeBoundsCenter(entry.Vertices, entry.LocalToWorld)
                });
                newRenderers.Add(entry.SourceRenderer);
            }

            if (newInstances.Count > 0)
            {
                lock (instanceLock)
                {
                    currentInstances.AddRange(newInstances);
                    instanceRenderers.AddRange(newRenderers);
                }
            }
        }

        private IntPtr CreateRemixMesh(ScannedMeshData data)
        {
            if (data.Vertices == null || data.Vertices.Length == 0 ||
                data.Surfaces == null || data.Surfaces.Length == 0)
                return IntPtr.Zero;

            if (meshHandles.TryGetValue(data.MeshHash, out IntPtr existing))
                return existing;

            var verts = data.Vertices;
            var norms = data.Normals;
            var uvs = data.UVs;
            var cols = data.Colors;
            bool hasColors = cols != null && cols.Length == verts.Length;

            if (norms == null || norms.Length != verts.Length)
            {
                norms = ComputeFaceNormals(verts, data.Surfaces.Select(s => s.Indices).ToArray());
                logger.LogDebug($"Mesh 0x{data.MeshHash:X16}: normals missing, computed from face geometry");
            }

            if (uvs == null || uvs.Length != verts.Length)
                uvs = new Vector2[verts.Length];

            // Check if any surface needs UV tiling/offset applied
            bool anyNonIdentityST = false;
            for (int s = 0; s < data.Surfaces.Length; s++)
            {
                var st = materialManager.GetMainTexST(data.Surfaces[s].MaterialId);
                if (st.x != 1f || st.y != 1f || st.z != 0f || st.w != 0f)
                { anyNonIdentityST = true; break; }
            }

            var vertexHandles = new List<GCHandle>();
            var indexHandles = new List<GCHandle>();
            var surfaceHandles = new List<GCHandle>();

            try
            {
                RemixAPI.remixapi_HardcodedVertex[] sharedRemixVerts = null;
                GCHandle sharedVertexHandle = default;

                // If no tiling needed, build shared vertex buffer once (common fast path)
                if (!anyNonIdentityST)
                {
                    sharedRemixVerts = new RemixAPI.remixapi_HardcodedVertex[verts.Length];
                    for (int i = 0; i < verts.Length; i++)
                    {
                        uint col = hasColors ? RemixMeshConverter.Color32ToBGRA(cols[i]) : 0xFFFFFFFF;
                        sharedRemixVerts[i] = RemixAPI.MakeVertex(
                            verts[i].x, verts[i].z, verts[i].y,
                            norms[i].x, norms[i].z, norms[i].y,
                            uvs[i].x, uvs[i].y,
                            col
                        );
                    }
                    sharedVertexHandle = GCHandle.Alloc(sharedRemixVerts, GCHandleType.Pinned);
                    vertexHandles.Add(sharedVertexHandle);
                }

                // Build one surface per submesh, each with its own material (skipping non-triangle or empty surfaces)
                var surfaces = new List<RemixAPI.remixapi_MeshInfoSurfaceTriangles>();
                for (int s = 0; s < data.Surfaces.Length; s++)
                {
                    var surf = data.Surfaces[s];
                    if (surf.Indices == null || surf.Indices.Length < 3 || (surf.Indices.Length % 3) != 0)
                        continue;

                    uint[] surfIndices = new uint[surf.Indices.Length];
                    for (int i = 0; i < surf.Indices.Length; i++)
                        surfIndices[i] = (uint)surf.Indices[i];

                    GCHandle idxHandle = GCHandle.Alloc(surfIndices, GCHandleType.Pinned);
                    indexHandles.Add(idxHandle);

                    IntPtr materialHandle = IntPtr.Zero;
                    if (surf.MaterialId != 0)
                        materialHandle = materialManager.GetOrCreateMaterial(surf.MaterialId);

                    IntPtr vertsPtr;
                    ulong vertsCount;

                    if (anyNonIdentityST)
                    {
                        // Per-surface vertex buffer with _MainTex_ST applied
                        var st = materialManager.GetMainTexST(surf.MaterialId);
                        var surfVerts = new RemixAPI.remixapi_HardcodedVertex[verts.Length];
                        for (int i = 0; i < verts.Length; i++)
                        {
                            float u = uvs[i].x * st.x + st.z;
                            float v = uvs[i].y * st.y + st.w;
                            uint col = hasColors ? RemixMeshConverter.Color32ToBGRA(cols[i]) : 0xFFFFFFFF;
                            surfVerts[i] = RemixAPI.MakeVertex(
                                verts[i].x, verts[i].z, verts[i].y,
                                norms[i].x, norms[i].z, norms[i].y,
                                u, v,
                                col
                            );
                        }
                        var vHandle = GCHandle.Alloc(surfVerts, GCHandleType.Pinned);
                        vertexHandles.Add(vHandle);
                        vertsPtr = vHandle.AddrOfPinnedObject();
                        vertsCount = (ulong)surfVerts.Length;
                    }
                    else
                    {
                        vertsPtr = sharedVertexHandle.AddrOfPinnedObject();
                        vertsCount = (ulong)sharedRemixVerts.Length;
                    }

                    surfaces.Add(new RemixAPI.remixapi_MeshInfoSurfaceTriangles
                    {
                        vertices_values = vertsPtr,
                        vertices_count = vertsCount,
                        indices_values = idxHandle.AddrOfPinnedObject(),
                        indices_count = (ulong)surfIndices.Length,
                        skinning_hasvalue = 0,
                        skinning_value = new RemixAPI.remixapi_MeshInfoSkinning(),
                        material = materialHandle
                    });
                }

                if (surfaces.Count == 0)
                    return IntPtr.Zero;

                var surfacesArray = surfaces.ToArray();
                GCHandle surfaceArrayHandle = GCHandle.Alloc(surfacesArray, GCHandleType.Pinned);
                surfaceHandles.Add(surfaceArrayHandle);

                var meshInfo = new RemixAPI.remixapi_MeshInfo
                {
                    sType = RemixAPI.remixapi_StructType.REMIXAPI_STRUCT_TYPE_MESH_INFO,
                    pNext = IntPtr.Zero,
                    hash = data.MeshHash,
                    surfaces_values = surfaceArrayHandle.AddrOfPinnedObject(),
                    surfaces_count = (uint)surfacesArray.Length
                };

                IntPtr handle;
                RemixAPI.remixapi_ErrorCode result;
                lock (apiLock)
                {
                    var createFunc = meshConverter.GetCreateMeshFunc();
                    if (createFunc == null)
                        return IntPtr.Zero;
                    result = createFunc(ref meshInfo, out handle);
                }

                if (result != RemixAPI.remixapi_ErrorCode.REMIXAPI_ERROR_CODE_SUCCESS)
                {
                    logger.LogError($"Failed to create scanned mesh 0x{data.MeshHash:X16}: {result}");
                    return IntPtr.Zero;
                }

                meshHandles[data.MeshHash] = handle;
                return handle;
            }
            finally
            {
                foreach (var h in vertexHandles) h.Free();
                foreach (var h in indexHandles) h.Free();
                foreach (var h in surfaceHandles) h.Free();
            }
        }

        private static ulong GenerateInstanceMeshHash(Mesh mesh, Transform transform)
        {
            ulong hash = 14695981039346656037UL; // FNV offset basis

            // Include transform hierarchy hash so each object gets a persistent unique Remix mesh
            hash ^= HashUtils.GetHierarchyHash(transform);
            hash *= 1099511628211UL;

            if (!string.IsNullOrEmpty(mesh.name))
            {
                foreach (char c in mesh.name)
                {
                    hash ^= c;
                    hash *= 1099511628211UL;
                }
            }

            hash ^= (ulong)mesh.vertexCount;
            hash *= 1099511628211UL;

            return hash;
        }

        /// <summary>
        /// Compacts vertex/normal/UV arrays to only include vertices referenced by the
        /// given surfaces, and remaps all surface indices accordingly. Used for combined
        /// meshes where each renderer uses a small slice of a large shared vertex buffer.
        /// </summary>
        private static void CompactMeshData(
            ref Vector3[] vertices, ref Vector3[] normals, ref Vector2[] uvs,
            ref Color32[] colors, List<SubMeshSurface> surfaces)
        {
            var usedSet = new HashSet<int>();
            foreach (var surf in surfaces)
                foreach (int idx in surf.Indices)
                    usedSet.Add(idx);

            if (usedSet.Count == vertices.Length)
                return;

            var sorted = new int[usedSet.Count];
            usedSet.CopyTo(sorted);
            Array.Sort(sorted);

            var remap = new Dictionary<int, int>(sorted.Length);
            for (int i = 0; i < sorted.Length; i++)
                remap[sorted[i]] = i;

            var newVerts = new Vector3[sorted.Length];
            var newNorms = normals != null && normals.Length == vertices.Length
                ? new Vector3[sorted.Length] : null;
            var newUvs = uvs != null && uvs.Length == vertices.Length
                ? new Vector2[sorted.Length] : null;
            var newCols = colors != null && colors.Length == vertices.Length
                ? new Color32[sorted.Length] : null;

            for (int i = 0; i < sorted.Length; i++)
            {
                int old = sorted[i];
                newVerts[i] = vertices[old];
                if (newNorms != null) newNorms[i] = normals[old];
                if (newUvs != null) newUvs[i] = uvs[old];
                if (newCols != null) newCols[i] = colors[old];
            }

            foreach (var surf in surfaces)
                for (int i = 0; i < surf.Indices.Length; i++)
                    surf.Indices[i] = remap[surf.Indices[i]];

            vertices = newVerts;
            normals = newNorms;
            uvs = newUvs;
            colors = newCols;
        }

        private bool ReadMeshFromGPU(Mesh mesh, out Vector3[] positions, out Vector3[] normals, out Vector2[] uvs, out int[][] subMeshIndices)
        {
            return NativeMeshReader.ReadMeshFromGPU(mesh, out positions, out normals, out uvs, out subMeshIndices);
        }

        private static Vector3[] ComputeFaceNormals(Vector3[] verts, int[][] subMeshIndices)
        {
            return NativeMeshReader.ComputeFaceNormals(verts, subMeshIndices);
        }
    }
}
