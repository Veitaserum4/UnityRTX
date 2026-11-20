using System.Collections.Generic;
using UnityEngine;

namespace UnityRemix
{
    // Partial thin films (RemixMaterialManager.Iridescence.partial) on GPU-skinned meshes: a
    // submesh splits into the triangles the film coats and the rest, each drawn as its own mesh.
    public partial class RemixFrameCapture
    {
        private readonly Dictionary<long, float[]> filmCoverageValues = new Dictionary<long, float[]>();
        private readonly Dictionary<long, KeyValuePair<int[], int[]>> filmSplits = new Dictionary<long, KeyValuePair<int[], int[]>>();
        private readonly List<Vector4> filmUvScratch = new List<Vector4>();
        private readonly List<int> filmCoated = new List<int>(), filmBare = new List<int>();

        private void AddGpuSkinned(int key, ulong meshHash, int materialId, int[] triangles, CachedSkinningData skinData,
                                   Matrix4x4 localToWorld, Matrix4x4[] boneMatrices)
        {
            if (triangles == null || triangles.Length == 0)
                return;
            // A Remix mesh carries its material, and this mesh is created once per hash. The
            // material is part of the hash so a renderer whose property block changes its look
            // (or two renderers of one mesh that look different) gets its own mesh.
            meshHash = (meshHash ^ (ulong)(uint)materialId) * 1099511628211UL;
            // So is the triangle count: a partial film's bare part keeps the plain material, and
            // without it would reuse the whole submesh's mesh (drawn over the coated part). The
            // coated sets grow with the coverage, so equal counts are equal triangle sets.
            meshHash = (meshHash ^ (ulong)(uint)triangles.Length) * 1099511628211UL;
            // A later skin of the same mesh and skeleton (overlaySkinnedIds): lifted off the one
            // under it, its own mesh.
            bool lifted = overlaySkinnedIds.Contains(key);
            if (lifted)
                meshHash = (meshHash ^ 0x4C1F7EDUL) * 1099511628211UL;
            // Part hidden by a vertex colour (GameProfile.GhostVertexChannel): what the camera sees
            // is the rest, camera-only; the whole is the reflection-only player body (in mirrors
            // and shadows whole, as the game's own reflection and shadow casters show it).
            if (ghostSkinnedChannels.TryGetValue(key, out int ghostChannel) && skinData.colors != null)
            {
                var cut = GhostCut(skinData, triangles, ghostChannel);
                if (cut != null)
                {
                    int keptKey = (int)((uint)key ^ 0x6057C0DEu);
                    persistentSkinnedData[keptKey] = new SkinnedMeshData
                    {
                        meshId = keptKey,
                        remixMeshHash = (meshHash ^ (0x6057UL + (ulong)ghostChannel)) * 1099511628211UL,
                        materialId = materialId,
                        vertices = cut.bindVertices,
                        normals = cut.bindNormals,
                        uvs = cut.uvs,
                        colors = cut.colors,
                        triangles = cut.triangles,
                        localToWorld = localToWorld,
                        boneTransforms = boneMatrices,
                        skinningData = cut,
                        cameraOnly = true
                    };
                    reusableValidSkinnedIds.Add(keptKey);
                }
                playerBodySkinnedIds.Add(key);
            }
            persistentSkinnedData[key] = new SkinnedMeshData
            {
                meshId = key,
                remixMeshHash = meshHash,
                materialId = materialId,
                vertices = lifted ? LiftedVertices(skinData) : skinData.bindVertices,
                normals = skinData.bindNormals,
                uvs = skinData.uvs,
                colors = skinData.colors,
                triangles = triangles,
                localToWorld = localToWorld,
                boneTransforms = boneMatrices,
                skinningData = skinData
            };
            reusableValidSkinnedIds.Add(key);
        }

        // Renderers part-hidden by a vertex colour channel this frame (sub-keys included), and
        // GameProfile.GhostVertexChannel's answer per renderer.
        private readonly Dictionary<int, int> ghostSkinnedChannels = new Dictionary<int, int>();
        private readonly Dictionary<int, int> ghostChannelAnswers = new Dictionary<int, int>();

        private int GhostChannel(SkinnedMeshRenderer renderer)
        {
            int id = renderer.GetInstanceID();
            if (!ghostChannelAnswers.TryGetValue(id, out int channel))
                ghostChannelAnswers[id] = channel = Games.GameProfiles.GhostVertexChannel(renderer);
            return channel;
        }

        // The part of a mesh where the channel is below one half, cut along where it crosses one
        // half: each triangle clipped against that line (its vertices keep their indices; a
        // crossed edge gets a vertex, shared by the triangles on it, its attributes and bone
        // weights interpolated), so the edge follows the mesh's own boundary. Its own skinning
        // data (a mesh of its own); cached per triangle array.
        private readonly Dictionary<int[], CachedSkinningData> ghostCuts = new Dictionary<int[], CachedSkinningData>();

        private CachedSkinningData GhostCut(CachedSkinningData skin, int[] triangles, int channel)
        {
            if (ghostCuts.TryGetValue(triangles, out var cached))
                return cached;
            var colors = skin.colors;
            int n = skin.bindVertices.Length, bpv = Mathf.Max(1, skin.bonesPerVertex);
            float Value(int v)
            {
                if (v >= colors.Length) return 0f;
                var c = colors[v];
                return (channel == 0 ? c.r : channel == 1 ? c.g : channel == 2 ? c.b : c.a) / 255f;
            }
            var pos = new List<Vector3>(skin.bindVertices);
            var nrm = skin.bindNormals != null && skin.bindNormals.Length == n ? new List<Vector3>(skin.bindNormals) : null;
            var uv = skin.uvs != null && skin.uvs.Length == n ? new List<Vector2>(skin.uvs) : null;
            var col = new List<Color32>(colors);
            bool hasWeights = skin.blendWeights != null && skin.blendIndices != null &&
                              skin.blendWeights.Length >= n * bpv && skin.blendIndices.Length >= n * bpv;
            var weights = hasWeights ? new List<float>(skin.blendWeights) : null;
            var indices = hasWeights ? new List<uint>(skin.blendIndices) : null;
            var edgeVertex = new Dictionary<long, int>();
            var bones = new Dictionary<uint, float>();
            var strongest = new List<KeyValuePair<uint, float>>();

            int Split(int a, int b)
            {
                long edge = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
                if (edgeVertex.TryGetValue(edge, out int made))
                    return made;
                float va = Value(a), vb = Value(b);
                float t = Mathf.Abs(vb - va) > 1e-6f ? Mathf.Clamp01((0.5f - va) / (vb - va)) : 0.5f;
                int v = pos.Count;
                pos.Add(Vector3.Lerp(pos[a], pos[b], t));
                nrm?.Add(Vector3.Lerp(nrm[a], nrm[b], t).normalized);
                uv?.Add(Vector2.Lerp(uv[a], uv[b], t));
                col.Add(Color32.Lerp(col[a], col[b], t));
                if (hasWeights)
                {
                    // Both ends' bones, weighted, the strongest bpv of them renormalised.
                    bones.Clear();
                    for (int k = 0; k < bpv; k++)
                    {
                        uint ia = skin.blendIndices[a * bpv + k], ib = skin.blendIndices[b * bpv + k];
                        bones.TryGetValue(ia, out float wa);
                        bones[ia] = wa + skin.blendWeights[a * bpv + k] * (1f - t);
                        bones.TryGetValue(ib, out float wb);
                        bones[ib] = wb + skin.blendWeights[b * bpv + k] * t;
                    }
                    strongest.Clear();
                    strongest.AddRange(bones);
                    strongest.Sort((x, y) => y.Value.CompareTo(x.Value));
                    float total = 0f;
                    for (int k = 0; k < bpv && k < strongest.Count; k++) total += strongest[k].Value;
                    for (int k = 0; k < bpv; k++)
                    {
                        bool has = k < strongest.Count && total > 0f;
                        indices.Add(has ? strongest[k].Key : 0u);
                        weights.Add(has ? strongest[k].Value / total : 0f);
                    }
                }
                edgeVertex[edge] = v;
                return v;
            }

            var tris = new List<int>(triangles.Length);
            var poly = new List<int>(4);
            for (int t = 0; t + 2 < triangles.Length; t += 3)
            {
                poly.Clear();
                for (int k = 0; k < 3; k++)
                {
                    int a = triangles[t + k], b = triangles[t + (k + 1) % 3];
                    bool inA = Value(a) < 0.5f, inB = Value(b) < 0.5f;
                    if (inA)
                        poly.Add(a);
                    if (inA != inB)
                        poly.Add(Split(a, b));
                }
                for (int k = 1; k + 1 < poly.Count; k++)
                {
                    tris.Add(poly[0]);
                    tris.Add(poly[k]);
                    tris.Add(poly[k + 1]);
                }
            }
            CachedSkinningData result = null;
            if (tris.Count > 0)
            {
                result = new CachedSkinningData
                {
                    bindVertices = pos.ToArray(),
                    bindNormals = nrm?.ToArray(),
                    uvs = uv?.ToArray(),
                    colors = col.ToArray(),
                    triangles = tris.ToArray(),
                    blendWeights = weights?.ToArray() ?? skin.blendWeights,
                    blendIndices = indices?.ToArray() ?? skin.blendIndices,
                    bonesPerVertex = skin.bonesPerVertex,
                    boneCount = skin.boneCount,
                    bindPoses = skin.bindPoses
                };
                result.submeshTriangles = new[] { result.triangles };
            }
            if (ghostCuts.Count > 64)
                ghostCuts.Clear();
            ghostCuts[triangles] = result;
            return result;
        }

        // A copy of a mesh drawn over itself (armour as a second skin of the body's mesh, a
        // cutout drawn later at equal depth, which Unity's LEQUAL lets win): its bind-pose
        // vertices moved out along their normals by OverlayLift (5 mm), so it lies just above the skin
        // under it in every ray and raster test. Coincident, half its pixels showed the body.
        private const float OverlayLift = 0.005f;
        private readonly Dictionary<CachedSkinningData, Vector3[]> liftedVertices = new Dictionary<CachedSkinningData, Vector3[]>();

        private Vector3[] LiftedVertices(CachedSkinningData skinData)
        {
            if (liftedVertices.TryGetValue(skinData, out var lifted))
                return lifted;
            var source = skinData.bindVertices;
            var normals = skinData.bindNormals;
            lifted = new Vector3[source.Length];
            for (int v = 0; v < source.Length; v++)
                lifted[v] = normals != null && v < normals.Length ? source[v] + normals[v].normalized * OverlayLift : source[v];
            if (liftedVertices.Count > 256)
                liftedVertices.Clear();
            liftedVertices[skinData] = lifted;
            return lifted;
        }

        // The submesh's triangles the film coats (their vertices' coverage values, each moved by a
        // fixed per-vertex noise, average below coverageBelow) and the rest; cached per mesh,
        // submesh and coverage. False: no split (not partial, or the mesh lacks the UV set).
        private bool SplitByFilm(Mesh mesh, int sub, int[] triangles, RemixMaterialManager.Iridescence film,
                                 out int[] coated, out int[] bare)
        {
            coated = bare = null;
            if (!film.partial || mesh == null || triangles == null || film.coverageChannel < 0 || film.coverageChannel > 7 ||
                film.coverageAxis < 0 || film.coverageAxis > 3)
                return false;
            int meshId = mesh.GetInstanceID();
            long valuesKey = ((long)meshId << 32) | (uint)(film.coverageChannel * 4 + film.coverageAxis);
            if (!filmCoverageValues.TryGetValue(valuesKey, out var values))
            {
                values = null;
                if (mesh.isReadable)
                {
                    filmUvScratch.Clear();
                    mesh.GetUVs(film.coverageChannel, filmUvScratch);
                    if (filmUvScratch.Count == mesh.vertexCount && filmUvScratch.Count > 0)
                    {
                        values = new float[filmUvScratch.Count];
                        for (int v = 0; v < values.Length; v++)
                            values[v] = filmUvScratch[v][film.coverageAxis];
                    }
                    filmUvScratch.Clear();
                }
                else
                {
                    values = SkinnedMeshGpuReader.TryReadComponent(mesh,
                        UnityCompat.AttributeTexCoord0 + film.coverageChannel,
                        film.coverageAxis);
                }
                filmCoverageValues[valuesKey] = values;
                if (values != null)
                {
                    float min = float.MaxValue, max = float.MinValue;
                    foreach (var x in values) { if (x < min) min = x; if (x > max) max = x; }
                    logger.LogInfo($"[Films] '{mesh.name}': partial film coverage from UV{film.coverageChannel}.{"xyzw"[film.coverageAxis]} " +
                        $"({(mesh.isReadable ? "readable" : "GPU readback")}), {min:0.##}..{max:0.##}");
                }
                else
                {
                    logger.LogInfo($"[Films] '{mesh.name}': no UV{film.coverageChannel} to split a partial film by; coated whole");
                }
            }
            if (values == null)
                return false;

            long splitKey;
            unchecked
            {
                splitKey = valuesKey * 31 + sub;
                splitKey = splitKey * 397 ^ Mathf.RoundToInt(film.coverageBelow * 1000f);
                splitKey = splitKey * 397 ^ Mathf.RoundToInt(film.coverageNoise * 1000f);
                splitKey = splitKey * 397 ^ triangles.Length;
            }
            if (filmSplits.TryGetValue(splitKey, out var split))
            {
                coated = split.Key;
                bare = split.Value;
                return true;
            }
            if (filmSplits.Count > 512)
                filmSplits.Clear();

            filmCoated.Clear();
            filmBare.Clear();
            for (int t = 0; t + 2 < triangles.Length; t += 3)
            {
                float sum = 0f;
                for (int k = 0; k < 3; k++)
                {
                    int v = triangles[t + k];
                    sum += v < values.Length ? values[v] + film.coverageNoise * VertexNoise(v) : float.MaxValue;
                }
                var into = sum / 3f < film.coverageBelow ? filmCoated : filmBare;
                into.Add(triangles[t]);
                into.Add(triangles[t + 1]);
                into.Add(triangles[t + 2]);
            }
            coated = filmCoated.ToArray();
            bare = filmBare.ToArray();
            filmSplits[splitKey] = new KeyValuePair<int[], int[]>(coated, bare);
            return true;
        }

        // -1..1, fixed per vertex index.
        private static float VertexNoise(int v)
        {
            uint h = (uint)v * 0x9E3779B1u;
            h ^= h >> 15;
            h *= 0x85EBCA77u;
            h ^= h >> 13;
            return (h & 0xFFFF) / 32767.5f - 1f;
        }
    }
}
