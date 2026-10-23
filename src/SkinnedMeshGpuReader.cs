using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;
using UnityEngine.Rendering;

namespace UnityRemix
{
    /// <summary>
    /// Reads the GPU copy of a non-readable skinned mesh (Read/Write disabled at import, the
    /// default for models): bind-pose positions, normals, UVs and colors, up to four bone
    /// weights and indices per vertex from their vertex streams, the triangles, and sparse
    /// single-frame blendshape deltas. Needs Unity 2021.2+ (Mesh.GetVertexBuffer,
    /// GetBlendShapeBuffer); callers catch what older runtimes throw.
    /// </summary>
    internal static class SkinnedMeshGpuReader
    {
        internal sealed class SparseShape
        {
            public float frameWeight;     // the single frame's full weight
            public int[] indices;         // vertices it moves
            public Vector3[] positions;   // their deltas at frameWeight
            public Vector3[] normals;
        }

        internal sealed class Data
        {
            public Vector3[] positions, normals;
            public Vector2[] uvs;
            public Color32[] colors;
            public int[][] submeshTriangles;
            public float[] weights;       // bonesPerVertex per vertex
            public uint[] indices;
            public SparseShape[] blendShapes; // null without blendshapes
        }

        private struct Attribute
        {
            public bool present;
            public int stream, offset, dimension;
            public VertexAttributeFormat format;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool TryRead(Mesh mesh, int bonesPerVertex, out Data data, out string reason)
        {
            data = null;
            reason = null;
            int vertexCount = mesh.vertexCount;
            if (vertexCount == 0)
            {
                reason = "no vertices";
                return false;
            }

            var position = Find(mesh, VertexAttribute.Position);
            var normal = Find(mesh, VertexAttribute.Normal);
            var uv = Find(mesh, VertexAttribute.TexCoord0);
            var color = Find(mesh, VertexAttribute.Color);
            var weight = Find(mesh, VertexAttribute.BlendWeight);
            var index = Find(mesh, VertexAttribute.BlendIndices);
            if (!position.present || position.dimension < 3)
            {
                reason = "no positions";
                return false;
            }
            if (!index.present)
            {
                reason = "no bone indices in the vertex streams (more than four influences per vertex?)";
                return false;
            }
            int influences = Math.Min(index.dimension, bonesPerVertex);
            if (weight.present && weight.dimension != index.dimension && weight.dimension != index.dimension - 1)
            {
                reason = $"bone weights ({weight.dimension}) do not match bone indices ({index.dimension})";
                return false;
            }
            if (!weight.present && index.dimension != 1)
            {
                reason = "no bone weights";
                return false;
            }

            var streams = new Dictionary<int, byte[]>();
            var strides = new Dictionary<int, int>();
            foreach (var a in new[] { position, normal, uv, color, weight, index })
            {
                if (!a.present || streams.ContainsKey(a.stream)) continue;
                if (!NativeMeshReader.ReadVertexBuffer(mesh, a.stream, out byte[] bytes))
                {
                    reason = $"vertex stream {a.stream} not readable from the GPU";
                    return false;
                }
                int stride = mesh.GetVertexBufferStride(a.stream);
                if (stride <= 0 || bytes.Length < stride * vertexCount)
                {
                    reason = $"vertex stream {a.stream} too small ({bytes.Length} bytes for {vertexCount} x {stride})";
                    return false;
                }
                streams[a.stream] = bytes;
                strides[a.stream] = stride;
            }

            var d = new Data
            {
                positions = new Vector3[vertexCount],
                normals = normal.present ? new Vector3[vertexCount] : null,
                uvs = uv.present && uv.dimension >= 2 ? new Vector2[vertexCount] : null,
                colors = color.present && color.dimension >= 3 ? new Color32[vertexCount] : null,
                weights = new float[bonesPerVertex * vertexCount],
                indices = new uint[bonesPerVertex * vertexCount]
            };
            for (int v = 0; v < vertexCount; v++)
            {
                d.positions[v] = ReadVector3(streams, strides, position, v);
                if (d.normals != null) d.normals[v] = ReadVector3(streams, strides, normal, v);
                if (d.uvs != null)
                    d.uvs[v] = new Vector2(Component(streams, strides, uv, v, 0), Component(streams, strides, uv, v, 1));
                if (d.colors != null)
                {
                    float r = Component(streams, strides, color, v, 0), g = Component(streams, strides, color, v, 1);
                    float b = Component(streams, strides, color, v, 2);
                    float a = color.dimension >= 4 ? Component(streams, strides, color, v, 3) : 1f;
                    d.colors[v] = new Color(r, g, b, a);
                }
                float sum = 0f;
                int baseIndex = v * bonesPerVertex;
                for (int k = 0; k < influences; k++)
                {
                    d.indices[baseIndex + k] = (uint)Component(streams, strides, index, v, k);
                    float w;
                    if (!weight.present)
                        w = 1f;
                    else if (k < weight.dimension)
                        w = Component(streams, strides, weight, v, k);
                    else
                        w = Mathf.Max(0f, 1f - sum); // the last influence's weight is implied
                    d.weights[baseIndex + k] = w;
                    sum += w;
                }
            }

            if (!ReadTriangles(mesh, vertexCount, out d.submeshTriangles, out reason))
                return false;
            if (mesh.blendShapeCount > 0 && !ReadBlendShapes(mesh, vertexCount, out d.blendShapes, out reason))
                return false;
            data = d;
            return true;
        }

        private static Attribute Find(Mesh mesh, VertexAttribute attribute)
        {
            if (!mesh.HasVertexAttribute(attribute))
                return default;
            return new Attribute
            {
                present = true,
                stream = mesh.GetVertexAttributeStream(attribute),
                offset = mesh.GetVertexAttributeOffset(attribute),
                dimension = mesh.GetVertexAttributeDimension(attribute),
                format = mesh.GetVertexAttributeFormat(attribute)
            };
        }

        private static Vector3 ReadVector3(Dictionary<int, byte[]> streams, Dictionary<int, int> strides, in Attribute a, int vertex)
        {
            return new Vector3(Component(streams, strides, a, vertex, 0), Component(streams, strides, a, vertex, 1),
                               Component(streams, strides, a, vertex, 2));
        }

        // One component, normalized for the UNorm/SNorm formats, as is for floats and integers.
        private static float Component(Dictionary<int, byte[]> streams, Dictionary<int, int> strides, in Attribute a, int vertex, int component)
        {
            var bytes = streams[a.stream];
            int size = FormatSize(a.format);
            int offset = vertex * strides[a.stream] + a.offset + component * size;
            switch (a.format)
            {
                case VertexAttributeFormat.Float32: return BitConverter.ToSingle(bytes, offset);
                case VertexAttributeFormat.Float16: return Mathf.HalfToFloat(BitConverter.ToUInt16(bytes, offset));
                case VertexAttributeFormat.UNorm8: return bytes[offset] / 255f;
                case VertexAttributeFormat.SNorm8: return Mathf.Max((sbyte)bytes[offset] / 127f, -1f);
                case VertexAttributeFormat.UNorm16: return BitConverter.ToUInt16(bytes, offset) / 65535f;
                case VertexAttributeFormat.SNorm16: return Mathf.Max(BitConverter.ToInt16(bytes, offset) / 32767f, -1f);
                case VertexAttributeFormat.UInt8: return bytes[offset];
                case VertexAttributeFormat.SInt8: return (sbyte)bytes[offset];
                case VertexAttributeFormat.UInt16: return BitConverter.ToUInt16(bytes, offset);
                case VertexAttributeFormat.SInt16: return BitConverter.ToInt16(bytes, offset);
                case VertexAttributeFormat.UInt32: return BitConverter.ToUInt32(bytes, offset);
                case VertexAttributeFormat.SInt32: return BitConverter.ToInt32(bytes, offset);
                default: return 0f;
            }
        }

        private static int FormatSize(VertexAttributeFormat format)
        {
            switch (format)
            {
                case VertexAttributeFormat.Float32:
                case VertexAttributeFormat.UInt32:
                case VertexAttributeFormat.SInt32:
                    return 4;
                case VertexAttributeFormat.Float16:
                case VertexAttributeFormat.UNorm16:
                case VertexAttributeFormat.SNorm16:
                case VertexAttributeFormat.UInt16:
                case VertexAttributeFormat.SInt16:
                    return 2;
                default:
                    return 1;
            }
        }

        private static bool ReadTriangles(Mesh mesh, int vertexCount, out int[][] submeshes, out string reason)
        {
            submeshes = null;
            reason = null;
            if (!NativeMeshReader.ReadIndexBuffer(mesh, out byte[] raw))
            {
                reason = "index buffer not readable from the GPU";
                return false;
            }
            bool wide = mesh.indexFormat == IndexFormat.UInt32;
            int size = wide ? 4 : 2;
            submeshes = new int[mesh.subMeshCount][];
            for (int sub = 0; sub < mesh.subMeshCount; sub++)
            {
                if (mesh.GetTopology(sub) != MeshTopology.Triangles)
                {
                    submeshes[sub] = new int[0];
                    continue;
                }
                int start = (int)mesh.GetIndexStart(sub);
                int count = (int)mesh.GetIndexCount(sub);
                int baseVertex = (int)mesh.GetBaseVertex(sub);
                if ((start + count) * size > raw.Length)
                {
                    reason = $"submesh {sub} past the index buffer";
                    return false;
                }
                var tris = new int[count];
                for (int i = 0; i < count; i++)
                {
                    int at = (start + i) * size;
                    int value = (wide ? BitConverter.ToInt32(raw, at) : BitConverter.ToUInt16(raw, at)) + baseVertex;
                    if (value < 0 || value >= vertexCount)
                    {
                        reason = $"submesh {sub} index out of range";
                        return false;
                    }
                    tris[i] = value;
                }
                submeshes[sub] = tris;
            }
            return true;
        }

        // Mesh.GetBlendShapeBuffer(PerShape): per shape, a run of (int vertex, float3 position
        // delta, float3 normal delta, float3 tangent delta) given by GetBlendShapeBufferRange.
        // Only single-frame shapes: a multi-frame shape's run does not say where its frames split.
        private static bool ReadBlendShapes(Mesh mesh, int vertexCount, out SparseShape[] shapes, out string reason)
        {
            shapes = null;
            reason = null;
            int count = mesh.blendShapeCount;
            for (int s = 0; s < count; s++)
            {
                if (mesh.GetBlendShapeFrameCount(s) != 1)
                {
                    reason = $"blendshape '{mesh.GetBlendShapeName(s)}' has several frames";
                    return false;
                }
            }
            byte[] raw;
            int stride, elements;
            using (var buffer = mesh.GetBlendShapeBuffer(BlendShapeBufferLayout.PerShape))
            {
                if (buffer == null)
                {
                    reason = "no blendshape buffer";
                    return false;
                }
                stride = buffer.stride;
                elements = buffer.count;
                raw = new byte[stride * elements];
                buffer.GetData(raw);
            }
            if (stride < 28)
            {
                reason = $"blendshape buffer stride {stride}";
                return false;
            }
            var ranges = new BlendShapeBufferRange[count];
            for (int s = 0; s < count; s++)
                ranges[s] = mesh.GetBlendShapeBufferRange(s);
            // Whether endIndex is the last element of a shape or one past it: adjacent shapes tell.
            bool inclusive = count > 1
                ? ranges[1].startIndex == ranges[0].endIndex + 1
                : ranges[0].endIndex < elements;
            shapes = new SparseShape[count];
            for (int s = 0; s < count; s++)
            {
                int first = (int)ranges[s].startIndex;
                int end = (int)ranges[s].endIndex + (inclusive ? 1 : 0);
                if (first < 0 || end < first || end > elements)
                {
                    reason = $"blendshape {s} range {ranges[s].startIndex}..{ranges[s].endIndex} outside {elements}";
                    return false;
                }
                int n = end - first;
                var shape = new SparseShape
                {
                    frameWeight = mesh.GetBlendShapeFrameWeight(s, 0),
                    indices = new int[n],
                    positions = new Vector3[n],
                    normals = new Vector3[n]
                };
                for (int i = 0; i < n; i++)
                {
                    int at = (first + i) * stride;
                    int vertex = BitConverter.ToInt32(raw, at);
                    if (vertex < 0 || vertex >= vertexCount)
                    {
                        reason = $"blendshape {s} vertex out of range";
                        return false;
                    }
                    shape.indices[i] = vertex;
                    shape.positions[i] = new Vector3(BitConverter.ToSingle(raw, at + 4), BitConverter.ToSingle(raw, at + 8),
                                                     BitConverter.ToSingle(raw, at + 12));
                    shape.normals[i] = new Vector3(BitConverter.ToSingle(raw, at + 16), BitConverter.ToSingle(raw, at + 20),
                                                   BitConverter.ToSingle(raw, at + 24));
                }
                shapes[s] = shape;
            }
            return true;
        }
    }
}
