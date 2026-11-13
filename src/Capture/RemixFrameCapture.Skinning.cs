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
    // Skinning data: bind poses and weights (CPU or GPU), validation, pose signatures, baked topology.
    public partial class RemixFrameCapture
    {
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
            // UVs and triangles never change between frames: built once per renderer (bakedTopology).
            Vector2[] uvCoords = bakedMesh.uv;
            var allTris = new List<int>();
            for (int i = 0; i < bakedMesh.subMeshCount; i++)
            {
                if (bakedMesh.GetTopology(i) != MeshTopology.Triangles)
                    continue;
                var subTris = bakedMesh.GetTriangles(i);
                if (subTris != null && subTris.Length > 0)
                    allTris.AddRange(subTris);
            }
            int[] tris = allTris.ToArray();

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
    }
}
