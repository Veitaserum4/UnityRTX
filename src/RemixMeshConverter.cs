using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;

namespace UnityRemix
{
    /// <summary>
    /// Holds pre-extracted mesh geometry and materials gathered on the main thread
    /// so Remix meshes can be created safely on the background render thread
    /// without touching UnityEngine.Mesh APIs.
    /// </summary>
    public class PreparedMeshData
    {
        public ulong MeshKey;
        public int MeshId;
        public string MeshName;
        public ulong MeshHash;
        public Vector3[] Vertices;
        public Vector3[] Normals;
        public Vector2[] UVs;
        public Color32[] Colors;
        public List<uint[]> SubmeshIndices;
        public List<Material> SubmeshMaterials;
        public List<int> SubmeshMaterialIds;
    }

    /// <summary>
    /// Converts Unity meshes (static and skinned) to Remix format
    /// </summary>
    public class RemixMeshConverter
    {
        /// <summary>
        /// Pack a Unity Color32 (RGBA) into D3DCOLOR/BGRA uint32 for Remix's B8G8R8A8_UNORM vertex color.
        /// </summary>
        public static uint Color32ToBGRA(Color32 c)
        {
            return (uint)(c.b | (c.g << 8) | (c.r << 16) | (c.a << 24));
        }

        /// <summary>
        /// Compute per-vertex normals by averaging face normals of adjacent triangles.
        /// </summary>
        private static Vector3[] ComputeFaceNormals(Vector3[] verts, int[] triangles)
        {
            var normals = new Vector3[verts.Length];
            for (int i = 0; i + 2 < triangles.Length; i += 3)
            {
                int i0 = triangles[i], i1 = triangles[i + 1], i2 = triangles[i + 2];
                if (i0 >= verts.Length || i1 >= verts.Length || i2 >= verts.Length) continue;
                var faceNormal = Vector3.Cross(verts[i1] - verts[i0], verts[i2] - verts[i0]);
                normals[i0] += faceNormal;
                normals[i1] += faceNormal;
                normals[i2] += faceNormal;
            }
            for (int i = 0; i < normals.Length; i++)
            {
                float len = normals[i].magnitude;
                normals[i] = len > 1e-6f ? normals[i] / len : Vector3.up;
            }
            return normals;
        }

        private readonly ManualLogSource logger;
        private readonly RemixMaterialManager materialManager;
        private readonly ConfigEntry<int> configDebugLogInterval;
        private readonly object apiLock;
        
        // Cached delegates
        private RemixAPI.PFN_remixapi_CreateMesh createMeshFunc;
        private RemixAPI.PFN_remixapi_DestroyMesh destroyMeshFunc;
        private RemixAPI.PFN_remixapi_DrawInstance drawInstanceFunc;
        
        // Cache for game meshes - maps composite mesh key (meshId + materialSignature) to Remix handle
        private ConcurrentDictionary<ulong, IntPtr> meshCache = new ConcurrentDictionary<ulong, IntPtr>();
        
        // Cache for skinned mesh Remix handles - keyed by Remix mesh hash
        private Dictionary<ulong, IntPtr> skinnedMeshHandles = new Dictionary<ulong, IntPtr>();
        
        // Deferred destruction queue to prevent flickering (destroy handles after they're no longer in use)
        private Queue<IntPtr> deferredDestroyQueue = new Queue<IntPtr>();
        private const int DEFERRED_DESTROY_FRAMES = 3; // Keep handles alive for 3 frames
        
        // Track which material each mesh uses (composite mesh key -> material ID)
        private Dictionary<ulong, int> meshToMaterialMap = new Dictionary<ulong, int>();
        
        // GCHandle pooling for skinned meshes to reduce allocations
        private struct PinnedMeshData
        {
            public RemixAPI.remixapi_HardcodedVertex[] vertices;
            public uint[] indices;
            public GCHandle vertexHandle;
            public GCHandle indexHandle;
            public bool isPinned;
            public int vertexCapacity;
            public int indexCapacity;
        }
        private Dictionary<ulong, PinnedMeshData> pinnedMeshPool = new Dictionary<ulong, PinnedMeshData>();
        
        // Track logged material warnings to avoid spam
        private HashSet<string> loggedMaterialWarnings = new HashSet<string>();
        
        private int skinnedRenderCount = 0;
        
        public RemixMeshConverter(
            ManualLogSource logger,
            RemixMaterialManager materialManager,
            ConfigEntry<int> debugLogInterval,
            RemixAPI.remixapi_Interface remixInterface,
            object apiLock)
        {
            this.logger = logger;
            this.materialManager = materialManager;
            this.configDebugLogInterval = debugLogInterval;
            this.apiLock = apiLock;
            
            // Cache delegates
            if (remixInterface.CreateMesh != IntPtr.Zero)
                createMeshFunc = Marshal.GetDelegateForFunctionPointer<RemixAPI.PFN_remixapi_CreateMesh>(remixInterface.CreateMesh);
            
            if (remixInterface.DestroyMesh != IntPtr.Zero)
                destroyMeshFunc = Marshal.GetDelegateForFunctionPointer<RemixAPI.PFN_remixapi_DestroyMesh>(remixInterface.DestroyMesh);
            
            if (remixInterface.DrawInstance != IntPtr.Zero)
                drawInstanceFunc = Marshal.GetDelegateForFunctionPointer<RemixAPI.PFN_remixapi_DrawInstance>(remixInterface.DrawInstance);
        }
        
        /// <summary>
        /// Compute 64-bit composite mesh key from mesh instance ID and material signature
        /// </summary>
        public static ulong GetMeshKey(int meshId, int materialSignature)
        {
            return ((ulong)(uint)meshId << 32) | (uint)materialSignature;
        }

        /// <summary>
        /// Check if mesh is already cached
        /// </summary>
        public bool IsMeshCached(ulong meshKey)
        {
            return meshCache.ContainsKey(meshKey);
        }

        public bool IsMeshCached(int meshId)
        {
            return meshCache.ContainsKey((ulong)(uint)meshId);
        }
        
        /// <summary>
        /// Get cached mesh handle
        /// </summary>
        public bool TryGetMeshHandle(ulong meshKey, out IntPtr handle)
        {
            return meshCache.TryGetValue(meshKey, out handle);
        }

        public bool TryGetMeshHandle(int meshId, out IntPtr handle)
        {
            return meshCache.TryGetValue((ulong)(uint)meshId, out handle);
        }
        
        /// <summary>
        /// Get cached skinned mesh handle
        /// </summary>
        public bool TryGetSkinnedMeshHandle(ulong meshHash, out IntPtr handle)
        {
            return skinnedMeshHandles.TryGetValue(meshHash, out handle);
        }
        
        public IntPtr CreateRemixMeshFromUnity(Mesh mesh, Material material)
        {
            return CreateRemixMeshFromUnity(mesh, material != null ? new Material[] { material } : null);
        }

        /// <summary>
        /// Create and cache a Unity mesh in Remix format
        /// </summary>
        /// <summary>
        /// Create and cache a Unity mesh in Remix format
        /// </summary>
        public IntPtr CreateRemixMeshFromUnity(Mesh mesh, Material[] materials)
        {
            if (mesh == null || createMeshFunc == null)
                return IntPtr.Zero;

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
                    for (int i = 0; i < subMeshCount; i++)
                    {
                        if (mesh.GetTopology(i) != MeshTopology.Triangles) continue;
                        var subTris = mesh.GetTriangles(i);
                        if (subTris == null || subTris.Length == 0 || subTris.Length % 3 != 0) continue;
                        uint[] sIdx = new uint[subTris.Length];
                        for (int j = 0; j < subTris.Length; j++) sIdx[j] = (uint)subTris[j];
                        submeshIndices.Add(sIdx);
                        Material mat = (materials != null && i < materials.Length) ? materials[i] : null;
                        submeshMaterials.Add(mat);
                    }
                }
                catch { }
            }

            if (vertices == null || vertices.Length == 0 || submeshIndices.Count == 0)
            {
                try
                {
                    if (NativeMeshReader.ReadMeshFromGPU(mesh, out vertices, out normals, out uvs, out int[][] subTris))
                    {
                        submeshIndices.Clear();
                        submeshMaterials.Clear();
                        for (int i = 0; i < subTris.Length; i++)
                        {
                            var tris = subTris[i];
                            if (tris == null || tris.Length == 0 || tris.Length % 3 != 0) continue;
                            uint[] sIdx = new uint[tris.Length];
                            for (int j = 0; j < tris.Length; j++) sIdx[j] = (uint)tris[j];
                            submeshIndices.Add(sIdx);
                            Material mat = (materials != null && i < materials.Length) ? materials[i] : null;
                            submeshMaterials.Add(mat);
                        }
                    }
                }
                catch { }
            }

            if (vertices == null || vertices.Length == 0 || submeshIndices.Count == 0)
                return IntPtr.Zero;

            int totalIndices = 0;
            foreach (var sIdx in submeshIndices) totalIndices += sIdx.Length;
            int matSig = StaticGeometryDedupe.ComputeMaterialSignature(materials);
            ulong meshHash = GenerateMeshHash(mesh.name, vertices.Length, totalIndices, matSig);
            ulong meshKey = GetMeshKey(mesh.GetInstanceID(), matSig);

            var prepared = new PreparedMeshData
            {
                MeshKey = meshKey,
                MeshId = mesh.GetInstanceID(),
                MeshName = mesh.name,
                MeshHash = meshHash,
                Vertices = vertices,
                Normals = normals,
                UVs = uvs,
                Colors = colors,
                SubmeshIndices = submeshIndices,
                SubmeshMaterials = submeshMaterials
            };

            return CreateRemixMeshFromPrepared(prepared);
        }

        /// <summary>
        /// Create and cache a Remix mesh from pre-extracted geometry data (safe to call on background render thread).
        /// </summary>
        public IntPtr CreateRemixMeshFromPrepared(PreparedMeshData data)
        {
            if (data == null || createMeshFunc == null)
                return IntPtr.Zero;

            Vector3[] vertices = data.Vertices;
            Vector3[] normals = data.Normals;
            Vector2[] uvs = data.UVs;
            Color32[] colors = data.Colors;
            var submeshIndices = data.SubmeshIndices;
            var submeshMaterials = data.SubmeshMaterials;

            if (vertices == null || vertices.Length == 0 || submeshIndices == null || submeshIndices.Count == 0)
                return IntPtr.Zero;

            // Ensure normals — compute from face geometry when unavailable
            if (normals == null || normals.Length != vertices.Length)
            {
                var allTris = new List<int>();
                foreach (var idx in submeshIndices)
                {
                    if (idx == null) continue;
                    for (int j = 0; j < idx.Length; j++)
                        allTris.Add((int)idx[j]);
                }
                normals = ComputeFaceNormals(vertices, allTris.ToArray());
            }

            // Ensure UVs
            if (uvs == null || uvs.Length != vertices.Length)
            {
                uvs = new Vector2[vertices.Length];
            }

            bool hasColors = colors != null && colors.Length == vertices.Length;

            // Check if any surface needs UV tiling/offset applied
            bool anyNonIdentityST = false;
            for (int s = 0; s < submeshMaterials.Count; s++)
            {
                if (submeshMaterials[s] != null)
                {
                    var st = materialManager.GetMainTexST(submeshMaterials[s].GetInstanceID());
                    if (st.x != 1f || st.y != 1f || st.z != 0f || st.w != 0f)
                    {
                        anyNonIdentityST = true;
                        break;
                    }
                }
            }

            var vertexHandles = new List<GCHandle>();
            var indexHandles = new List<GCHandle>();
            var surfaceHandles = new List<GCHandle>();

            try
            {
                RemixAPI.remixapi_HardcodedVertex[] sharedRemixVerts = null;
                GCHandle sharedVertexHandle = default;

                if (!anyNonIdentityST)
                {
                    sharedRemixVerts = new RemixAPI.remixapi_HardcodedVertex[vertices.Length];
                    for (int i = 0; i < vertices.Length; i++)
                    {
                        uint col = hasColors ? Color32ToBGRA(colors[i]) : 0xFFFFFFFF;
                        sharedRemixVerts[i] = RemixAPI.MakeVertex(
                            vertices[i].x, vertices[i].z, vertices[i].y,
                            normals[i].x, normals[i].z, normals[i].y,
                            uvs[i].x, uvs[i].y,
                            col
                        );
                    }
                    sharedVertexHandle = GCHandle.Alloc(sharedRemixVerts, GCHandleType.Pinned);
                    vertexHandles.Add(sharedVertexHandle);
                }

                ulong meshKey = data.MeshKey != 0 ? data.MeshKey : (ulong)(uint)data.MeshId;
                int primaryMatId = (data.SubmeshMaterialIds != null && data.SubmeshMaterialIds.Count > 0 && data.SubmeshMaterialIds[0] != 0)
                    ? data.SubmeshMaterialIds[0]
                    : (submeshMaterials.Count > 0 && submeshMaterials[0] != null ? submeshMaterials[0].GetInstanceID() : 0);
                if (primaryMatId != 0)
                {
                    meshToMaterialMap[meshKey] = primaryMatId;
                }

                var surfaces = new RemixAPI.remixapi_MeshInfoSurfaceTriangles[submeshIndices.Count];
                for (int s = 0; s < submeshIndices.Count; s++)
                {
                    var surfIndices = submeshIndices[s];
                    GCHandle idxHandle = GCHandle.Alloc(surfIndices, GCHandleType.Pinned);
                    indexHandles.Add(idxHandle);

                    Material mat = (s < submeshMaterials.Count) ? submeshMaterials[s] : null;
                    int targetMatId = (data.SubmeshMaterialIds != null && s < data.SubmeshMaterialIds.Count && data.SubmeshMaterialIds[s] != 0)
                        ? data.SubmeshMaterialIds[s]
                        : (mat != null ? mat.GetInstanceID() : 0);

                    IntPtr materialHandle = IntPtr.Zero;
                    if (targetMatId != 0)
                    {
                        materialHandle = materialManager.GetOrCreateMaterial(targetMatId);
                    }

                    IntPtr vertsPtr;
                    ulong vertsCount;

                    if (anyNonIdentityST)
                    {
                        Vector4 st = new Vector4(1, 1, 0, 0);
                        if (targetMatId != 0)
                            st = materialManager.GetMainTexST(targetMatId);

                        var surfVerts = new RemixAPI.remixapi_HardcodedVertex[vertices.Length];
                        for (int i = 0; i < vertices.Length; i++)
                        {
                            float u = uvs[i].x * st.x + st.z;
                            float v = uvs[i].y * st.y + st.w;
                            uint col = hasColors ? Color32ToBGRA(colors[i]) : 0xFFFFFFFF;
                            surfVerts[i] = RemixAPI.MakeVertex(
                                vertices[i].x, vertices[i].z, vertices[i].y,
                                normals[i].x, normals[i].z, normals[i].y,
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

                    surfaces[s] = new RemixAPI.remixapi_MeshInfoSurfaceTriangles
                    {
                        vertices_values = vertsPtr,
                        vertices_count = vertsCount,
                        indices_values = idxHandle.AddrOfPinnedObject(),
                        indices_count = (ulong)surfIndices.Length,
                        skinning_hasvalue = 0,
                        skinning_value = new RemixAPI.remixapi_MeshInfoSkinning(),
                        material = materialHandle
                    };
                }

                GCHandle surfaceArrayHandle = GCHandle.Alloc(surfaces, GCHandleType.Pinned);
                surfaceHandles.Add(surfaceArrayHandle);

                ulong meshHash = data.MeshHash;
                var meshInfo = new RemixAPI.remixapi_MeshInfo
                {
                    sType = RemixAPI.remixapi_StructType.REMIXAPI_STRUCT_TYPE_MESH_INFO,
                    pNext = IntPtr.Zero,
                    hash = meshHash,
                    surfaces_values = surfaceArrayHandle.AddrOfPinnedObject(),
                    surfaces_count = (uint)surfaces.Length
                };

                IntPtr handle;
                RemixAPI.remixapi_ErrorCode result;
                lock (apiLock)
                {
                    result = createMeshFunc(ref meshInfo, out handle);
                }

                if (result != RemixAPI.remixapi_ErrorCode.REMIXAPI_ERROR_CODE_SUCCESS)
                {
                    logger.LogError($"Failed to create mesh '{data.MeshName}': {result}");
                    return IntPtr.Zero;
                }

                meshCache[meshKey] = handle;
                logger.LogInfo($"Created mesh '{data.MeshName}' with hash: 0x{meshHash:X16} and {surfaces.Length} surfaces");

                return handle;
            }
            finally
            {
                foreach (var h in vertexHandles) h.Free();
                foreach (var h in indexHandles) h.Free();
                foreach (var h in surfaceHandles) h.Free();
            }
        }
        
        /// <summary>
        /// Create Remix mesh from skinned mesh data (for animated meshes)
        /// </summary>
        public IntPtr CreateRemixMeshFromData(
            ulong meshHash, 
            Vector3[] vertices, 
            Vector3[] normals, 
            Vector2[] uvs, 
            int[] triangles, 
            int frameHash,
            int materialId = 0,
            Color32[] colors = null)
        {
            if (vertices == null || vertices.Length == 0 || triangles == null || triangles.Length == 0)
                return IntPtr.Zero;
            
            if (triangles.Length % 3 != 0)
            {
                if (skinnedRenderCount % 300 == 1)
                    logger.LogError($"Skinned mesh {meshHash} has invalid triangle count: {triangles.Length}");
                return IntPtr.Zero;
            }
            
            // Validate indices
            for (int i = 0; i < triangles.Length; i++)
            {
                if (triangles[i] < 0 || triangles[i] >= vertices.Length)
                {
                    if (skinnedRenderCount % 300 == 1)
                        logger.LogError($"Skinned mesh {meshHash} has out-of-bounds index");
                    return IntPtr.Zero;
                }
            }
            
            // Ensure normals — compute from face geometry when unavailable
            if (normals == null || normals.Length != vertices.Length)
            {
                normals = ComputeFaceNormals(vertices, triangles);
                logger.LogDebug($"Skinned mesh {meshHash}: normals missing, computed from face geometry");
            }
            
            // Ensure UVs
            if (uvs == null || uvs.Length != vertices.Length)
            {
                uvs = new Vector2[vertices.Length];
            }
            
            // Use pooled GCHandles
            if (!pinnedMeshPool.TryGetValue(meshHash, out PinnedMeshData poolData))
            {
                poolData = new PinnedMeshData
                {
                    vertices = new RemixAPI.remixapi_HardcodedVertex[vertices.Length],
                    indices = new uint[triangles.Length],
                    isPinned = false,
                    vertexCapacity = vertices.Length,
                    indexCapacity = triangles.Length
                };
            }
            
            // Resize if needed
            if (poolData.vertexCapacity < vertices.Length)
            {
                if (poolData.isPinned)
                {
                    poolData.vertexHandle.Free();
                    if (poolData.indexCapacity > 0)
                        poolData.indexHandle.Free();
                    poolData.isPinned = false;
                }
                poolData.vertices = new RemixAPI.remixapi_HardcodedVertex[vertices.Length];
                poolData.vertexCapacity = vertices.Length;
            }
            
            if (poolData.indexCapacity < triangles.Length)
            {
                if (poolData.isPinned && poolData.indexCapacity > 0)
                {
                    poolData.indexHandle.Free();
                }
                poolData.indices = new uint[triangles.Length];
                poolData.indexCapacity = triangles.Length;
            }
            
            // Fill data (Y-up to Z-up conversion), applying _MainTex_ST tiling/offset
            Vector4 st = materialManager.GetMainTexST(materialId);
            bool hasColors = colors != null && colors.Length == vertices.Length;
            for (int i = 0; i < vertices.Length; i++)
            {
                float u = uvs[i].x * st.x + st.z;
                float v = uvs[i].y * st.y + st.w;
                uint col = hasColors ? Color32ToBGRA(colors[i]) : 0xFFFFFFFF;
                poolData.vertices[i] = RemixAPI.MakeVertex(
                    vertices[i].x, vertices[i].z, vertices[i].y,
                    normals[i].x, normals[i].z, normals[i].y,
                    u, v,
                    col
                );
            }
            
            for (int i = 0; i < triangles.Length; i++)
                poolData.indices[i] = (uint)triangles[i];
            
            // Pin once if not already pinned
            if (!poolData.isPinned)
            {
                poolData.vertexHandle = GCHandle.Alloc(poolData.vertices, GCHandleType.Pinned);
                poolData.indexHandle = GCHandle.Alloc(poolData.indices, GCHandleType.Pinned);
                poolData.isPinned = true;
                pinnedMeshPool[meshHash] = poolData;
            }
            
            // Get or create material handle for skinned mesh (on render thread)
            IntPtr materialHandle = IntPtr.Zero;
            if (materialId != 0)
            {
                materialHandle = materialManager.GetOrCreateMaterial(materialId);
            }
            
            // Create mesh
            //logger.LogInfo($"Creating skinned mesh {meshHash} with material: 0x{materialHandle.ToInt64():X}");
            
            var surface = new RemixAPI.remixapi_MeshInfoSurfaceTriangles
            {
                vertices_values = poolData.vertexHandle.AddrOfPinnedObject(),
                vertices_count = (ulong)vertices.Length,
                indices_values = poolData.indexHandle.AddrOfPinnedObject(),
                indices_count = (ulong)triangles.Length,
                skinning_hasvalue = 0,
                skinning_value = new RemixAPI.remixapi_MeshInfoSkinning(),
                material = materialHandle
            };
            
            GCHandle surfaceHandle = GCHandle.Alloc(surface, GCHandleType.Pinned);
            
            try
            {
                var meshInfo = new RemixAPI.remixapi_MeshInfo
                {
                    sType = RemixAPI.remixapi_StructType.REMIXAPI_STRUCT_TYPE_MESH_INFO,
                    pNext = IntPtr.Zero,
                    hash = meshHash,
                    surfaces_values = surfaceHandle.AddrOfPinnedObject(),
                    surfaces_count = 1
                };
                
                IntPtr handle;
                RemixAPI.remixapi_ErrorCode result;
                lock (apiLock)
                {
                    result = createMeshFunc(ref meshInfo, out handle);
                }
                
                if (result != RemixAPI.remixapi_ErrorCode.REMIXAPI_ERROR_CODE_SUCCESS)
                {
                    return IntPtr.Zero;
                }
                
                return handle;
            }
            finally
            {
                surfaceHandle.Free();
            }
        }
        
        /// <summary>
        /// Draw mesh instance with transform
        /// </summary>
        public void DrawMeshInstance(IntPtr meshHandle, Matrix4x4 localToWorld, uint objectPickingValue)
        {
            if (drawInstanceFunc == null || meshHandle == IntPtr.Zero)
                return;
            
            // Convert Unity Matrix4x4 to Remix transform (Y-up to Z-up)
            var m = localToWorld;
            var transform = RemixAPI.remixapi_Transform.FromMatrix(
                m.m00, m.m02, m.m01, m.m03,
                m.m20, m.m22, m.m21, m.m23,
                m.m10, m.m12, m.m11, m.m13
            );
            
            // Create ObjectPicking extension
            var objectPickingExt = new RemixAPI.remixapi_InstanceInfoObjectPickingEXT
            {
                sType = RemixAPI.remixapi_StructType.REMIXAPI_STRUCT_TYPE_INSTANCE_INFO_OBJECT_PICKING_EXT,
                pNext = IntPtr.Zero,
                objectPickingValue = objectPickingValue
            };
            
            GCHandle pickingHandle = GCHandle.Alloc(objectPickingExt, GCHandleType.Pinned);
            
            try
            {
                var instanceInfo = new RemixAPI.remixapi_InstanceInfo
                {
                    sType = RemixAPI.remixapi_StructType.REMIXAPI_STRUCT_TYPE_INSTANCE_INFO,
                    pNext = pickingHandle.AddrOfPinnedObject(),
                    categoryFlags = 0,
                    mesh = meshHandle,
                    transform = transform,
                    doubleSided = 1
                };
                
                RemixAPI.remixapi_ErrorCode result;
                lock (apiLock)
                {
                    result = drawInstanceFunc(ref instanceInfo);
                }
                
                if (result != RemixAPI.remixapi_ErrorCode.REMIXAPI_ERROR_CODE_SUCCESS)
                {
                    logger.LogWarning($"DrawInstance failed for mesh 0x{meshHandle.ToInt64():X}: {result}");
                }
            }
            finally
            {
                pickingHandle.Free();
            }
        }
        
        /// <summary>
        /// Create a Remix mesh with GPU skinning data (bind-pose vertices + bone weights).
        /// Called once per unique sharedMesh. Returns mesh handle or IntPtr.Zero on failure.
        /// </summary>
        public IntPtr CreateSkinnedMeshWithBones(
            ulong meshHash,
            Vector3[] vertices,
            Vector3[] normals,
            Vector2[] uvs,
            int[] triangles,
            float[] blendWeights,
            uint[] blendIndices,
            int bonesPerVertex,
            int materialId,
            Color32[] colors = null)
        {
            if (vertices == null || vertices.Length == 0 || triangles == null || triangles.Length == 0)
                return IntPtr.Zero;
            
            if (normals == null || normals.Length != vertices.Length)
            {
                normals = new Vector3[vertices.Length];
                for (int i = 0; i < normals.Length; i++)
                    normals[i] = Vector3.up;
            }
            if (uvs == null || uvs.Length != vertices.Length)
                uvs = new Vector2[vertices.Length];
            
            // Build vertex array (Y-up to Z-up), applying _MainTex_ST tiling/offset
            Vector4 stSkin = materialManager.GetMainTexST(materialId);
            bool hasColors = colors != null && colors.Length == vertices.Length;
            var remixVerts = new RemixAPI.remixapi_HardcodedVertex[vertices.Length];
            for (int i = 0; i < vertices.Length; i++)
            {
                float u = uvs[i].x * stSkin.x + stSkin.z;
                float v = uvs[i].y * stSkin.y + stSkin.w;
                uint col = hasColors ? Color32ToBGRA(colors[i]) : 0xFFFFFFFF;
                remixVerts[i] = RemixAPI.MakeVertex(
                    vertices[i].x, vertices[i].z, vertices[i].y,
                    normals[i].x, normals[i].z, normals[i].y,
                    u, v,
                    col
                );
            }
            
            var indices = new uint[triangles.Length];
            for (int i = 0; i < triangles.Length; i++)
                indices[i] = (uint)triangles[i];
            
            // Pin all arrays
            GCHandle vertHandle = GCHandle.Alloc(remixVerts, GCHandleType.Pinned);
            GCHandle idxHandle = GCHandle.Alloc(indices, GCHandleType.Pinned);
            GCHandle weightsHandle = GCHandle.Alloc(blendWeights, GCHandleType.Pinned);
            GCHandle indicesHandle = GCHandle.Alloc(blendIndices, GCHandleType.Pinned);
            
            try
            {
                IntPtr materialHandle = IntPtr.Zero;
                if (materialId != 0)
                    materialHandle = materialManager.GetOrCreateMaterial(materialId);
                
                var skinning = new RemixAPI.remixapi_MeshInfoSkinning
                {
                    bonesPerVertex = (uint)bonesPerVertex,
                    blendWeights_values = weightsHandle.AddrOfPinnedObject(),
                    blendWeights_count = (uint)blendWeights.Length,
                    blendIndices_values = indicesHandle.AddrOfPinnedObject(),
                    blendIndices_count = (uint)blendIndices.Length
                };
                
                var surface = new RemixAPI.remixapi_MeshInfoSurfaceTriangles
                {
                    vertices_values = vertHandle.AddrOfPinnedObject(),
                    vertices_count = (ulong)vertices.Length,
                    indices_values = idxHandle.AddrOfPinnedObject(),
                    indices_count = (ulong)triangles.Length,
                    skinning_hasvalue = 1,
                    skinning_value = skinning,
                    material = materialHandle
                };
                
                GCHandle surfaceHandle = GCHandle.Alloc(surface, GCHandleType.Pinned);
                try
                {
                    var meshInfo = new RemixAPI.remixapi_MeshInfo
                    {
                        sType = RemixAPI.remixapi_StructType.REMIXAPI_STRUCT_TYPE_MESH_INFO,
                        pNext = IntPtr.Zero,
                        hash = meshHash,
                        surfaces_values = surfaceHandle.AddrOfPinnedObject(),
                        surfaces_count = 1
                    };
                    
                    IntPtr handle;
                    RemixAPI.remixapi_ErrorCode result;
                    lock (apiLock)
                    {
                        result = createMeshFunc(ref meshInfo, out handle);
                    }
                    
                    if (result != RemixAPI.remixapi_ErrorCode.REMIXAPI_ERROR_CODE_SUCCESS)
                    {
                        logger.LogWarning($"CreateSkinnedMeshWithBones failed for {meshHash}: {result}");
                        return IntPtr.Zero;
                    }
                    
                    return handle;
                }
                finally
                {
                    surfaceHandle.Free();
                }
            }
            finally
            {
                vertHandle.Free();
                idxHandle.Free();
                weightsHandle.Free();
                indicesHandle.Free();
            }
        }
        
        /// <summary>
        /// Draw a GPU-skinned mesh instance with bone transforms via pNext chain.
        /// </summary>
        public unsafe void DrawSkinnedInstance(IntPtr meshHandle, Matrix4x4 localToWorld, Matrix4x4[] boneTransforms, uint objectPickingValue)
        {
            if (drawInstanceFunc == null || meshHandle == IntPtr.Zero || boneTransforms == null)
                return;
            
            // Convert instance transform (Y-up to Z-up)
            var m = localToWorld;
            var transform = RemixAPI.remixapi_Transform.FromMatrix(
                m.m00, m.m02, m.m01, m.m03,
                m.m20, m.m22, m.m21, m.m23,
                m.m10, m.m12, m.m11, m.m13
            );
            
            // Convert bone transforms to Remix format (Y-up to Z-up, 3x4 matrices)
            int boneCount = boneTransforms.Length;
            var remixBones = new RemixAPI.remixapi_Transform[boneCount];
            for (int i = 0; i < boneCount; i++)
            {
                var b = boneTransforms[i];
                remixBones[i] = RemixAPI.remixapi_Transform.FromMatrix(
                    b.m00, b.m02, b.m01, b.m03,
                    b.m20, b.m22, b.m21, b.m23,
                    b.m10, b.m12, b.m11, b.m13
                );
            }
            
            GCHandle bonesHandle = GCHandle.Alloc(remixBones, GCHandleType.Pinned);
            
            try
            {
                // Build pNext chain: InstanceInfo -> BoneTransforms -> ObjectPicking
                var objectPickingExt = new RemixAPI.remixapi_InstanceInfoObjectPickingEXT
                {
                    sType = RemixAPI.remixapi_StructType.REMIXAPI_STRUCT_TYPE_INSTANCE_INFO_OBJECT_PICKING_EXT,
                    pNext = IntPtr.Zero,
                    objectPickingValue = objectPickingValue
                };
                
                GCHandle pickingHandle = GCHandle.Alloc(objectPickingExt, GCHandleType.Pinned);
                
                try
                {
                    var boneExt = new RemixAPI.remixapi_InstanceInfoBoneTransformsEXT
                    {
                        sType = RemixAPI.remixapi_StructType.REMIXAPI_STRUCT_TYPE_INSTANCE_INFO_BONE_TRANSFORMS_EXT,
                        pNext = pickingHandle.AddrOfPinnedObject(),
                        boneTransforms_values = bonesHandle.AddrOfPinnedObject(),
                        boneTransforms_count = (uint)boneCount
                    };
                    
                    GCHandle boneExtHandle = GCHandle.Alloc(boneExt, GCHandleType.Pinned);
                    
                    try
                    {
                        var instanceInfo = new RemixAPI.remixapi_InstanceInfo
                        {
                            sType = RemixAPI.remixapi_StructType.REMIXAPI_STRUCT_TYPE_INSTANCE_INFO,
                            pNext = boneExtHandle.AddrOfPinnedObject(),
                            categoryFlags = 0,
                            mesh = meshHandle,
                            transform = transform,
                            doubleSided = 1
                        };
                        
                        RemixAPI.remixapi_ErrorCode result;
                        lock (apiLock)
                        {
                            result = drawInstanceFunc(ref instanceInfo);
                        }
                        
                        if (result != RemixAPI.remixapi_ErrorCode.REMIXAPI_ERROR_CODE_SUCCESS)
                        {
                            logger.LogWarning($"DrawSkinnedInstance failed for mesh 0x{meshHandle.ToInt64():X}: {result}");
                        }
                    }
                    finally
                    {
                        boneExtHandle.Free();
                    }
                }
                finally
                {
                    pickingHandle.Free();
                }
            }
            finally
            {
                bonesHandle.Free();
            }
        }
        
        /// <summary>
        /// Manage skinned mesh handle lifecycle
        /// </summary>
        public void UpdateSkinnedMeshHandle(ulong meshHash, IntPtr newHandle)
        {
            // Queue old handle for deferred destruction (prevents flickering)
            if (skinnedMeshHandles.TryGetValue(meshHash, out IntPtr oldHandle) && oldHandle != IntPtr.Zero)
            {
                // Don't destroy immediately - queue it for later
                deferredDestroyQueue.Enqueue(oldHandle);
                
                // Process deferred destruction queue (destroy oldest handles)
                while (deferredDestroyQueue.Count > DEFERRED_DESTROY_FRAMES && destroyMeshFunc != null)
                {
                    IntPtr handleToDestroy = deferredDestroyQueue.Dequeue();
                    try
                    {
                        destroyMeshFunc(handleToDestroy);
                    }
                    catch { }
                }
            }
            
            skinnedMeshHandles[meshHash] = newHandle;
            skinnedRenderCount++;
        }
        
        /// <summary>
        /// Clean up stale skinned mesh handles
        /// </summary>
        public void CleanupStaleSkinnedMeshes(HashSet<ulong> activeMeshHashes)
        {
            if (destroyMeshFunc == null)
                return;
            
            List<ulong> toRemove = new List<ulong>();
            foreach (var kvp in skinnedMeshHandles)
            {
                if (!activeMeshHashes.Contains(kvp.Key))
                {
                    destroyMeshFunc(kvp.Value);
                    toRemove.Add(kvp.Key);
                }
            }
            
            foreach (ulong id in toRemove)
            {
                skinnedMeshHandles.Remove(id);
            }
        }
        
        /// <summary>
        /// Generate stable content-based hash for mesh
        /// </summary>
        public static ulong GenerateMeshHash(string meshName, int vertexCount, int indexCount, int materialSignature = 0)
        {
            ulong hash = 14695981039346656037UL; // FNV offset basis
            
            string cleanName = meshName;
            if (!string.IsNullOrEmpty(cleanName))
            {
                // Remove dynamic tags
                cleanName = cleanName.Replace(" (Instance)", "").Replace(" Instance", "").Replace("(Clone)", "").Trim();
                foreach (char c in cleanName)
                {
                    hash ^= c;
                    hash *= 1099511628211UL;
                }
            }
            
            // Hash vertex count and triangle/index count
            hash ^= (ulong)vertexCount;
            hash *= 1099511628211UL;
            hash ^= (ulong)indexCount;
            hash *= 1099511628211UL;
            
            if (materialSignature != 0)
            {
                hash ^= (ulong)(uint)materialSignature;
                hash *= 1099511628211UL;
            }

            if (hash == 0) hash = 1;
            return hash;
        }

        public static ulong GenerateMeshHash(Mesh mesh)
        {
            if (mesh == null) return 1;
            int indexCount = 0;
            try
            {
                if (mesh.isReadable)
                {
                    indexCount = mesh.triangles.Length;
                }
                else
                {
                    for (int s = 0; s < mesh.subMeshCount; s++)
                        indexCount += (int)mesh.GetIndexCount(s);
                }
            }
            catch
            {
                try
                {
                    for (int s = 0; s < mesh.subMeshCount; s++)
                        indexCount += (int)mesh.GetIndexCount(s);
                }
                catch { }
            }
            return GenerateMeshHash(mesh.name, mesh.vertexCount, indexCount);
        }
        
        /// <summary>
        /// Create test triangle mesh
        /// </summary>
        public IntPtr CreateTestTriangle()
        {
            if (createMeshFunc == null)
                return IntPtr.Zero;
            
            RemixAPI.remixapi_HardcodedVertex[] vertices = new RemixAPI.remixapi_HardcodedVertex[3]
            {
                RemixAPI.MakeVertex( 5, -5, 10),
                RemixAPI.MakeVertex( 0,  5, 10),
                RemixAPI.MakeVertex(-5, -5, 10),
            };
            
            GCHandle vertexHandle = GCHandle.Alloc(vertices, GCHandleType.Pinned);
            
            try
            {
                var surface = new RemixAPI.remixapi_MeshInfoSurfaceTriangles
                {
                    vertices_values = vertexHandle.AddrOfPinnedObject(),
                    vertices_count = (ulong)vertices.Length,
                    indices_values = IntPtr.Zero,
                    indices_count = 0,
                    skinning_hasvalue = 0,
                    skinning_value = new RemixAPI.remixapi_MeshInfoSkinning(),
                    material = IntPtr.Zero
                };
                
                GCHandle surfaceHandle = GCHandle.Alloc(surface, GCHandleType.Pinned);
                
                try
                {
                    var meshInfo = new RemixAPI.remixapi_MeshInfo
                    {
                        sType = RemixAPI.remixapi_StructType.REMIXAPI_STRUCT_TYPE_MESH_INFO,
                        pNext = IntPtr.Zero,
                        hash = 0x1,
                        surfaces_values = surfaceHandle.AddrOfPinnedObject(),
                        surfaces_count = 1
                    };
                    
                    IntPtr handle;
                    var result = createMeshFunc(ref meshInfo, out handle);
                    
                    if (result != RemixAPI.remixapi_ErrorCode.REMIXAPI_ERROR_CODE_SUCCESS)
                    {
                        logger.LogError($"Failed to create test mesh: {result}");
                        return IntPtr.Zero;
                    }
                    
                    logger.LogInfo($"Test triangle mesh created! Handle: {handle}");
                    return handle;
                }
                finally
                {
                    surfaceHandle.Free();
                }
            }
            finally
            {
                vertexHandle.Free();
            }
        }
        
        /// <summary>
        /// Expose CreateMesh delegate for external callers (e.g. SceneMeshScanner).
        /// </summary>
        public RemixAPI.PFN_remixapi_CreateMesh GetCreateMeshFunc() => createMeshFunc;
        
        /// <summary>
        /// Expose DrawInstance delegate for external callers (e.g. SceneMeshScanner).
        /// </summary>
        public RemixAPI.PFN_remixapi_DrawInstance GetDrawInstanceFunc() => drawInstanceFunc;

        // --- Diagnostic getters for debug HUD ---
        public int MeshCacheCount => meshCache.Count;
        public int SkinnedMeshHandleCount => skinnedMeshHandles.Count;

        public bool TryGetMaterialId(ulong meshKey, out int materialId)
        {
            return meshToMaterialMap.TryGetValue(meshKey, out materialId);
        }

        public bool TryGetMaterialId(int meshId, out int materialId)
        {
            return meshToMaterialMap.TryGetValue((ulong)(uint)meshId, out materialId);
        }
        
        /// <summary>
        /// Cleanup all resources
        /// </summary>
        public void Cleanup()
        {
            // Free pinned handles
            foreach (var poolData in pinnedMeshPool.Values)
            {
                if (poolData.isPinned)
                {
                    poolData.vertexHandle.Free();
                    if (poolData.indexCapacity > 0)
                        poolData.indexHandle.Free();
                }
            }
            pinnedMeshPool.Clear();
            loggedMaterialWarnings.Clear();
            
            // Destroy any remaining deferred handles
            if (destroyMeshFunc != null)
            {
                while (deferredDestroyQueue.Count > 0)
                {
                    IntPtr handle = deferredDestroyQueue.Dequeue();
                    try
                    {
                        destroyMeshFunc(handle);
                    }
                    catch { }
                }
            }
            
            // Note: Mesh cache handles are managed by Remix, don't destroy here
            meshCache.Clear();
            skinnedMeshHandles.Clear();
        }
    }
}
