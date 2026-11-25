using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace UnityRemix
{
    // A terrain's detail objects: the grass and small meshes its TerrainData scatters over it,
    // which Unity draws itself (no renderer of theirs is in the scene). Each prototype's
    // instances are merged, a group of detail patches at a time, into terrain chunks.
    public partial class RemixTerrainManager
    {
        // Detail patches merged into one mesh per prototype, DetailGroup x DetailGroup at a time.
        private const int DetailGroup = 4;

        private bool loggedNoDetailTransforms;

        // One prototype's geometry at unit size, standing on its origin.
        private sealed class DetailModel
        {
            public Vector3[] positions;
            public Vector3[] normals;
            public Vector2[] uvs;
            public int[] indices;
            public int materialId;
            // Grass textures: tinted between the prototype's healthy and dry colours by noise
            // over the terrain.
            public bool grass;
            public Color healthy, dry;
            public float noiseSpread;
            public float alignToGround;
        }

        private void ProcessTerrainDetails(Terrain terrain, TerrainData tData, int terrainId, Matrix4x4 localToWorld, int layer,
                                           List<TerrainChunkData> chunks)
        {
            if (!terrain.drawTreesAndFoliage)
                return;
            var prototypes = tData.detailPrototypes;
            int patches = tData.detailPatchCount;
            float density = terrain.detailObjectDensity;
            if (prototypes == null || prototypes.Length == 0 || patches <= 0 || density <= 0f)
                return;

            Vector3 size = tData.size;
            for (int p = 0; p < prototypes.Length; p++)
            {
                var model = BuildDetailModel(terrain, terrainId, prototypes[p], p, out string what);
                if (model == null)
                {
                    logger.LogInfo($"[RemixTerrain] '{terrain.name}' detail {p}: skipped ({what})");
                    continue;
                }

                int instances = 0, meshes = 0, vertexTotal = 0;
                float minW = float.MaxValue, maxW = 0f, minH = float.MaxValue, maxH = 0f;
                var verts = new List<RemixAPI.remixapi_HardcodedVertex>();
                var indices = new List<uint>();
                int groups = (patches + DetailGroup - 1) / DetailGroup;
                for (int gz = 0; gz < groups; gz++)
                {
                    for (int gx = 0; gx < groups; gx++)
                    {
                        verts.Clear();
                        indices.Clear();
                        Vector3 min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
                        Vector3 max = -min;
                        for (int pz = gz * DetailGroup; pz < Mathf.Min(patches, (gz + 1) * DetailGroup); pz++)
                        {
                            for (int px = gx * DetailGroup; px < Mathf.Min(patches, (gx + 1) * DetailGroup); px++)
                            {
                                var transforms = DetailTransforms(tData, px, pz, p, density);
                                if (transforms == null)
                                {
                                    if (!loggedNoDetailTransforms)
                                    {
                                        loggedNoDetailTransforms = true;
                                        logger.LogInfo("[RemixTerrain] TerrainData.ComputeDetailInstanceTransforms unavailable (Unity before 2022.2): terrain detail objects not drawn");
                                    }
                                    return;
                                }
                                foreach (var t in transforms)
                                {
                                    AppendDetail(model, t, tData, size, localToWorld, verts, indices, ref min, ref max);
                                    minW = Mathf.Min(minW, t.scaleXZ); maxW = Mathf.Max(maxW, t.scaleXZ);
                                    minH = Mathf.Min(minH, t.scaleY); maxH = Mathf.Max(maxH, t.scaleY);
                                }
                                instances += transforms.Length;
                            }
                        }
                        if (indices.Count == 0)
                            continue;

                        Vector3 localCenter = (min + max) * 0.5f;
                        chunks.Add(new TerrainChunkData
                        {
                            TerrainId = terrainId,
                            ChunkMeshHash = HashUtils.HashStringFNV($"Terrain_{terrainId}_Detail_{p}_{gx}_{gz}"),
                            Vertices = verts.ToArray(),
                            Indices = indices.ToArray(),
                            MaterialId = model.materialId,
                            BoundsCenter = localToWorld.MultiplyPoint3x4(localCenter),
                            BoundsRadius = Vector3.Distance(max, localCenter),
                            LocalToWorld = localToWorld,
                            Layer = layer,
                            IsDetail = true
                        });
                        meshes++;
                        vertexTotal += verts.Count;
                    }
                }
                logger.LogInfo($"[RemixTerrain] '{terrain.name}' detail {p} ({what}): {instances} instances in {meshes} meshes, {vertexTotal} vertices; " +
                               (instances > 0 ? $"width {minW:0.##}-{maxW:0.##}, height {minH:0.##}-{maxH:0.##}" : "none placed"));
            }
        }

        // One scattered detail instance (Unity 2022.2's DetailInstanceTransform), terrain-local.
        private struct DetailTransform
        {
            public float posX, posY, posZ, scaleXZ, scaleY, rotationY;
        }

#if NET472
        // The net472 build is for Unity before 2021.2, which has no ComputeDetailInstanceTransforms.
        private static DetailTransform[] DetailTransforms(TerrainData tData, int px, int pz, int layer, float density) => null;
#else
        // Unity 2022.2's: the instances of one prototype in one patch, as Unity scatters them
        // (terrain-local positions). Null where Unity has no such method.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static DetailTransform[] ComputeDetailTransforms(TerrainData tData, int px, int pz, int layer, float density)
        {
            var unity = tData.ComputeDetailInstanceTransforms(px, pz, layer, density, out _);
            if (unity == null)
                return Array.Empty<DetailTransform>();
            var transforms = new DetailTransform[unity.Length];
            for (int i = 0; i < unity.Length; i++)
            {
                var u = unity[i];
                transforms[i] = new DetailTransform
                {
                    posX = u.posX, posY = u.posY, posZ = u.posZ,
                    scaleXZ = u.scaleXZ, scaleY = u.scaleY, rotationY = u.rotationY,
                };
            }
            return transforms;
        }

        private static DetailTransform[] DetailTransforms(TerrainData tData, int px, int pz, int layer, float density)
        {
            try
            {
                return ComputeDetailTransforms(tData, px, pz, layer, density);
            }
            catch (MissingMethodException) { return null; }
            catch (TypeLoadException) { return null; }
        }
#endif

        private DetailModel BuildDetailModel(Terrain terrain, int terrainId, DetailPrototype proto, int index, out string what)
        {
            var model = new DetailModel { alignToGround = DetailAlignToGround(proto) };
            if (proto.usePrototypeMesh)
            {
                var go = proto.prototype;
                var filter = go != null ? go.GetComponentInChildren<MeshFilter>() : null;
                var renderer = filter != null ? filter.GetComponent<MeshRenderer>() : null;
                var mesh = filter != null ? filter.sharedMesh : null;
                var material = renderer != null ? renderer.sharedMaterial : null;
                what = $"mesh '{mesh?.name}', material '{material?.name}' shader '{material?.shader?.name}'";
                if (mesh == null || material == null)
                    return null;
                if (!ReadDetailMesh(mesh, model, out string error))
                {
                    what += $": {error}";
                    return null;
                }
                // The material's texture transform, as the mesh converter applies it.
                if (material.HasProperty("_MainTex"))
                {
                    Vector2 s = material.mainTextureScale, o = material.mainTextureOffset;
                    for (int i = 0; i < model.uvs.Length; i++)
                        model.uvs[i] = new Vector2(model.uvs[i].x * s.x + o.x, model.uvs[i].y * s.y + o.y);
                }
                // The prototype's own transform under its root (its mesh may be rotated or scaled).
                if (filter.transform != go.transform)
                {
                    var m = go.transform.worldToLocalMatrix * filter.transform.localToWorldMatrix;
                    for (int i = 0; i < model.positions.Length; i++)
                    {
                        model.positions[i] = m.MultiplyPoint3x4(model.positions[i]);
                        model.normals[i] = m.MultiplyVector(model.normals[i]).normalized;
                    }
                }
                model.materialId = StaticGeometryDedupe.GetCanonicalMaterialId(material);
                materialManager.CaptureMaterialTextures(material, model.materialId);
                return model;
            }

            var texture = proto.prototypeTexture;
            what = $"grass texture '{texture?.name}'";
            if (texture == null)
                return null;
            // Unity's grass shaders clip at alpha one half.
            model.materialId = materialManager.RegisterCustomMaterial($"TerrainGrass_{terrainId}_{index}_{texture.name}", texture,
                                                                      null, 0.9f, 0f, alphaCutoff: 0.5f);
            model.grass = true;
            model.healthy = proto.healthyColor;
            model.dry = proto.dryColor;
            model.noiseSpread = proto.noiseSpread;
            // Two quads crossed, a unit wide and high: Unity turns its grass billboards to the
            // camera, which a traced scene (shadows, reflections) cannot; crossed, each looks
            // full from any side. Their normals point up, as the ground's below them, so grass
            // lights as the ground it stands on rather than dark on its far side.
            model.positions = new[]
            {
                new Vector3(-0.5f, 0f, 0f), new Vector3(0.5f, 0f, 0f), new Vector3(0.5f, 1f, 0f), new Vector3(-0.5f, 1f, 0f),
                new Vector3(0f, 0f, -0.5f), new Vector3(0f, 0f, 0.5f), new Vector3(0f, 1f, 0.5f), new Vector3(0f, 1f, -0.5f),
            };
            model.normals = new Vector3[8];
            for (int i = 0; i < 8; i++)
                model.normals[i] = Vector3.up;
            model.uvs = new[]
            {
                new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(0f, 1f),
                new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(0f, 1f),
            };
            model.indices = new[] { 0, 1, 2, 0, 2, 3, 4, 5, 6, 4, 6, 7 };
            return model;
        }

        // DetailPrototype.alignToGround is Unity 2022.2's.
        [MethodImpl(MethodImplOptions.NoInlining)]
#if NET472
        private static float ReadAlignToGround(DetailPrototype proto) => 0f;
#else
        private static float ReadAlignToGround(DetailPrototype proto) => proto.alignToGround;
#endif

        private static float DetailAlignToGround(DetailPrototype proto)
        {
            try { return Mathf.Clamp01(ReadAlignToGround(proto)); }
            catch (MissingMethodException) { return 0f; }
        }

        private static bool ReadDetailMesh(Mesh mesh, DetailModel model, out string error)
        {
            error = null;
            int[][] subMeshes;
            if (mesh.isReadable)
            {
                model.positions = mesh.vertices;
                model.normals = mesh.normals;
                model.uvs = mesh.uv;
                subMeshes = new int[mesh.subMeshCount][];
                for (int s = 0; s < subMeshes.Length; s++)
                    subMeshes[s] = mesh.GetTriangles(s);
            }
            else if (!NativeMeshReader.ReadMeshFromGPU(mesh, out model.positions, out model.normals, out model.uvs, out subMeshes))
            {
                error = "GPU read failed";
                return false;
            }
            if (model.positions == null || model.positions.Length == 0 || subMeshes == null)
            {
                error = "no vertices";
                return false;
            }
            int n = model.positions.Length;
            if (model.normals == null || model.normals.Length != n)
                model.normals = NativeMeshReader.ComputeFaceNormals(model.positions, subMeshes);
            if (model.uvs == null || model.uvs.Length != n)
                model.uvs = new Vector2[n];
            var all = new List<int>();
            foreach (var s in subMeshes)
                if (s != null)
                    all.AddRange(s);
            model.indices = all.ToArray();
            if (model.indices.Length == 0)
            {
                error = "no triangles";
                return false;
            }
            return true;
        }

        private static void AppendDetail(DetailModel model, in DetailTransform t, TerrainData tData, Vector3 size, Matrix4x4 localToWorld,
                                         List<RemixAPI.remixapi_HardcodedVertex> verts, List<uint> indices, ref Vector3 min, ref Vector3 max)
        {
            var position = new Vector3(t.posX, t.posY, t.posZ);
            var rotation = Quaternion.AngleAxis(t.rotationY * Mathf.Rad2Deg, Vector3.up);
            if (model.alignToGround > 0f)
            {
                var ground = tData.GetInterpolatedNormal(t.posX / size.x, t.posZ / size.z);
                rotation = Quaternion.FromToRotation(Vector3.up, Vector3.Slerp(Vector3.up, ground, model.alignToGround)) * rotation;
            }
            var m = Matrix4x4.TRS(position, rotation, new Vector3(t.scaleXZ, t.scaleY, t.scaleXZ));

            uint color = 0xFFFFFFFF;
            if (model.grass)
            {
                // Unity's: healthy to dry by Perlin noise over the terrain's world position.
                var world = localToWorld.MultiplyPoint3x4(position);
                float noise = Mathf.Clamp01(Mathf.PerlinNoise(world.x * model.noiseSpread, world.z * model.noiseSpread));
                Color32 c = Color.Lerp(model.healthy, model.dry, noise);
                color = (uint)(c.b | (c.g << 8) | (c.r << 16) | (0xFF << 24));
            }

            var normalMatrix = model.grass ? Matrix4x4.identity : m.inverse.transpose;
            uint baseIndex = (uint)verts.Count;
            for (int i = 0; i < model.positions.Length; i++)
            {
                var p = m.MultiplyPoint3x4(model.positions[i]);
                var nrm = model.grass ? Vector3.up : normalMatrix.MultiplyVector(model.normals[i]).normalized;
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
                // Unity (X, Y up, Z) -> Remix (X, Z depth, Y up)
                verts.Add(RemixAPI.MakeVertex(p.x, p.z, p.y, nrm.x, nrm.z, nrm.y, model.uvs[i].x, model.uvs[i].y, color));
            }
            foreach (int index in model.indices)
                indices.Add(baseIndex + (uint)index);
        }

        // Main thread: a detail chunk is shown while its terrain draws details and the camera is
        // within the terrain's detail distance of it.
        private bool DetailShown(in TerrainInstanceData instance, Vector3 cameraPosition)
        {
            if (!processedTerrains.TryGetValue(instance.TerrainId, out var terrain) || terrain == null || !terrain.drawTreesAndFoliage)
                return false;
            float reach = terrain.detailObjectDistance + instance.BoundsRadius;
            return (instance.BoundsCenter - cameraPosition).sqrMagnitude <= reach * reach;
        }
    }
}
